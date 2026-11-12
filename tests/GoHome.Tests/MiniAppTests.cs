using GoHome.Web.App;
using GoHome.Web.Data;
using GoHome.Web.Domain;
using GoHome.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GoHome.Tests;

public class MiniAppTests
{
    private const string Token = "123456:TEST-token";

    private static Dictionary<string, string> Fields(DateTimeOffset authDate) => new()
    {
        ["auth_date"] = authDate.ToUnixTimeSeconds().ToString(),
        ["query_id"] = "AAE",
        ["user"] = """{"id":42,"first_name":"Айбек","last_name":"Т"}""",
        ["start_param"] = "board_abc123",
    };

    [Fact]
    public void Valid_init_data_is_accepted()
    {
        var now = DateTimeOffset.UtcNow;
        var r = TelegramInitData.Validate(TelegramInitData.Sign(Fields(now), Token), Token, now, TimeSpan.FromDays(1));
        Assert.NotNull(r);
        Assert.Equal(42, r.UserId);
        Assert.Equal("Айбек", r.FirstName);
        Assert.Equal("board_abc123", r.StartParam);
    }

    [Fact]
    public void Tampered_wrong_token_or_stale_init_data_is_rejected()
    {
        var now = DateTimeOffset.UtcNow;
        var signed = TelegramInitData.Sign(Fields(now), Token);

        Assert.Null(TelegramInitData.Validate(signed.Replace("%22id%22%3A42", "%22id%22%3A43"), Token, now, TimeSpan.FromDays(1)));
        Assert.Null(TelegramInitData.Validate(signed, "999:other", now, TimeSpan.FromDays(1)));
        Assert.Null(TelegramInitData.Validate(signed, Token, now.AddDays(2), TimeSpan.FromDays(1)));
        Assert.Null(TelegramInitData.Validate("user=%7B%7D&auth_date=1", Token, now, TimeSpan.FromDays(1)));
    }

    [Theory]
    [InlineData("https://t.me/rideway_bot?start=board_a1b2c3d4e5f6", "a1b2c3d4e5f6")]
    [InlineData("board_A1B2C3D4E5F6", "a1b2c3d4e5f6")]
    [InlineData("/start board_a1b2c3d4e5f6", "a1b2c3d4e5f6")]
    [InlineData("a1b2c3d4e5f6", "a1b2c3d4e5f6")]
    [InlineData("https://example.com/something", null)]
    public void Board_code_is_extracted_from_qr_text(string text, string? expected) =>
        Assert.Equal(expected, MiniAppApi.ExtractBoardCode(text));

    [Fact]
    public async Task Qr_boarding_marks_ride_only_in_window_and_for_roster_passengers()
    {
        using var h = new Harness();
        var (route, stop) = await h.AddRouteAsync(seats: 3);    // посадка утром в 07:30
        var code = await h.RunAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var car = await db.Cars.SingleAsync();
            car.BoardingCode = "a1b2c3d4e5f6";
            await db.SaveChangesAsync();
            return car.BoardingCode;
        });
        var rider = await h.AddUserAsync("Rider");
        var stranger = await h.AddUserAsync("Stranger");
        await h.RunAsync(sp => sp.GetRequiredService<BookingService>().JoinRouteAsync(rider, route.Id, stop.Id, true, true, "test"));

        Task<OpResult> Board(int user, string c) => h.RunAsync(sp => sp.GetRequiredService<TripOpsService>().BoardByCodeAsync(user, c, "test"));

        h.Clock.LocalNow = h.Tuesday.ToDateTime(new TimeOnly(6, 30));   // слишком рано
        Assert.False((await Board(rider, code)).Ok);

        h.Clock.LocalNow = h.Tuesday.ToDateTime(new TimeOnly(7, 35));
        Assert.False((await Board(rider, "ffffffffffff")).Ok);           // чужой код
        Assert.False((await Board(stranger, code)).Ok);                  // нет в списке рейса
        Assert.True((await Board(rider, code)).Ok);
        Assert.True((await Board(rider, code)).Ok);                      // повторный скан — не ошибка

        var log = await h.RunAsync(sp => sp.GetRequiredService<AppDbContext>().RideLogs.SingleAsync());
        Assert.Equal(RideResult.Boarded, log.Result);
        Assert.Equal(Direction.Morning, log.Direction);
        Assert.Equal(100, log.Price);
    }
}
