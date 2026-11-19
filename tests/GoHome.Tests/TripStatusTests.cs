using GoHome.Web.Data;
using GoHome.Web.Domain;
using GoHome.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GoHome.Tests;

public class TripStatusTests
{
    [Fact]
    public async Task Depart_marks_trip_once_and_notifies_only_riding_passengers()
    {
        using var h = new Harness();
        var (route, stop) = await h.AddRouteAsync(seats: 3);
        var rider = await h.AddUserAsync("Rider", telegramId: 101);
        var skipper = await h.AddUserAsync("Skipper", telegramId: 102);
        foreach (var u in new[] { rider, skipper })
            await h.RunAsync(sp => sp.GetRequiredService<BookingService>().JoinRouteAsync(u, route.Id, stop.Id, true, true, "test"));
        await h.RunAsync(sp => sp.GetRequiredService<BookingService>().MarkAbsentAsync(skipper, h.Tuesday, Direction.Morning, "test"));

        Task<OpResult> Depart(DateOnly date) => h.RunAsync(sp => sp.GetRequiredService<TripOpsService>().DepartAsync(route.Id, date, Direction.Morning, "test"));
        Task<bool> Departed() => h.RunAsync(sp => sp.GetRequiredService<TripOpsService>().IsDepartedAsync(route.Id, h.Tuesday, Direction.Morning));

        Assert.False((await Depart(h.Tuesday)).Ok);                       // завтрашний рейс — рано
        h.Clock.LocalNow = h.Tuesday.ToDateTime(new TimeOnly(7, 20));
        Assert.False(await Departed());
        Assert.True((await Depart(h.Tuesday)).Ok);
        Assert.True(await Departed());
        Assert.True((await Depart(h.Tuesday)).Ok);                        // повторное нажатие не шлёт второе сообщение

        var sent = Assert.Single(h.Notifier.Sent);
        Assert.Equal(101, sent.Chat);
        Assert.Contains("07:30", sent.Text);
        // Вечерний рейс того же дня остаётся «не выехал».
        Assert.False(await h.RunAsync(sp => sp.GetRequiredService<TripOpsService>().IsDepartedAsync(route.Id, h.Tuesday, Direction.Evening)));
    }

    [Fact]
    public async Task Late_passenger_message_goes_to_the_driver_of_their_trip()
    {
        using var h = new Harness();
        var (route, stop) = await h.AddRouteAsync(seats: 3);
        var driver = await h.AddUserAsync("Driver", telegramId: 900);
        await h.RunAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            (await db.Cars.SingleAsync()).DriverUserId = driver;
            return await db.SaveChangesAsync();
        });
        var rider = await h.AddUserAsync("Rider", telegramId: 101);
        var stranger = await h.AddUserAsync("Stranger", telegramId: 103);
        await h.RunAsync(sp => sp.GetRequiredService<BookingService>().JoinRouteAsync(rider, route.Id, stop.Id, true, true, "test"));

        h.Clock.LocalNow = h.Tuesday.ToDateTime(new TimeOnly(7, 25));
        Task<OpResult> Late(int user) => h.RunAsync(sp => sp.GetRequiredService<TripOpsService>().NotifyLateAsync(user, h.Tuesday, Direction.Morning, 3));

        Assert.False((await Late(stranger)).Ok);
        Assert.True((await Late(rider)).Ok);
        var sent = Assert.Single(h.Notifier.Sent);
        Assert.Equal(900, sent.Chat);
        Assert.Contains("Rider", sent.Text);
    }
}
