using GoHome.Web.Data;
using GoHome.Web.Domain;
using GoHome.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GoHome.Tests;

/// <summary>Запись «на сегодня», напоминания перед выездом и сообщение водителю о новом пассажире.</summary>
public class TodayBookingTests
{
    // Маршрут из Harness: Дом — утром 07:30 (он же выезд), Офис — вечером выезд 18:30.
    private static Task<(OpResult Result, List<Direction> Booked)> Book(Harness h, int user, int routeId, int stopId, int? seat = null) =>
        h.RunAsync(sp => sp.GetRequiredService<BookingService>().BookTodayAsync(user, routeId, stopId, seat, "test"));

    private static void At(Harness h, int hour, int minute) => h.Clock.LocalNow = Harness.Monday.ToDateTime(new TimeOnly(hour, minute));

    [Fact]
    public async Task Morning_booking_takes_morning_and_evening_on_one_seat()
    {
        using var h = new Harness();
        var (route, stop) = await h.AddRouteAsync(seats: 3);
        var user = await h.AddUserAsync("Аня");
        At(h, 7, 0);

        var (res, booked) = await Book(h, user, route.Id, stop.Id, seat: 2);
        Assert.True(res.Ok, res.Message);
        Assert.Equal([Direction.Morning, Direction.Evening], booked);

        var seats = await h.RunAsync(sp => sp.GetRequiredService<AppDbContext>().OneOffBookings
            .Where(b => b.UserId == user && b.Date == Harness.Monday).Select(b => b.SeatNumber).ToListAsync());
        Assert.Equal([2, 2], seats);

        var again = await Book(h, user, route.Id, stop.Id);
        Assert.False(again.Result.Ok);
        Assert.Contains("уже записаны", again.Result.Message);
    }

    [Fact]
    public async Task Booking_closes_10_minutes_before_boarding()
    {
        using var h = new Harness();
        var (route, stop) = await h.AddRouteAsync(seats: 3);
        var late = await h.AddUserAsync("Поздний");
        var tooLate = await h.AddUserAsync("Совсем поздний");

        At(h, 7, 21);                                         // утро закрылось в 07:20 — остаётся вечер
        var (res, booked) = await Book(h, late, route.Id, stop.Id);
        Assert.True(res.Ok, res.Message);
        Assert.Equal([Direction.Evening], booked);

        At(h, 18, 21);                                        // вечер закрылся в 18:20
        var closed = await Book(h, tooLate, route.Id, stop.Id);
        Assert.False(closed.Result.Ok);
        Assert.Contains("закрыта", closed.Result.Message);
    }

    [Fact]
    public async Task Seat_must_be_free_on_every_booked_trip()
    {
        using var h = new Harness();
        var (route, stop) = await h.AddRouteAsync(seats: 2);
        var a = await h.AddUserAsync("A");
        var b = await h.AddUserAsync("B");
        At(h, 7, 0);
        Assert.True((await Book(h, a, route.Id, stop.Id, seat: 1)).Result.Ok);

        var taken = await Book(h, b, route.Id, stop.Id, seat: 1);
        Assert.False(taken.Result.Ok);
        var auto = await Book(h, b, route.Id, stop.Id);
        Assert.True(auto.Result.Ok);
        Assert.Contains("№ 2", auto.Result.Message);
    }

    [Fact]
    public async Task Same_day_booking_can_be_cancelled_until_cutoff()
    {
        using var h = new Harness();
        var (route, stop) = await h.AddRouteAsync(seats: 3);
        var user = await h.AddUserAsync("Аня");
        At(h, 7, 0);
        await Book(h, user, route.Id, stop.Id);
        var ids = await h.RunAsync(sp => sp.GetRequiredService<AppDbContext>().OneOffBookings
            .Where(b => b.UserId == user).OrderBy(b => b.Direction).Select(b => b.Id).ToListAsync());
        Task<OpResult> Cancel(int id) => h.RunAsync(sp => sp.GetRequiredService<BookingService>().CancelOneOffAsync(id, user, "test"));

        At(h, 7, 25);
        Assert.False((await Cancel(ids[0])).Ok);              // утро: посадка 07:30, отменить уже нельзя
        Assert.True((await Cancel(ids[1])).Ok);               // вечер: до 18:20 можно
    }

    [Fact]
    public async Task Driver_gets_message_about_new_passenger()
    {
        using var h = new Harness();
        var (route, stop) = await h.AddRouteAsync(seats: 3);
        var driver = await h.AddUserAsync("Водитель", telegramId: 900);
        await h.RunAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            (await db.Cars.SingleAsync()).DriverUserId = driver;
            return await db.SaveChangesAsync();
        });
        var user = await h.AddUserAsync("Аня");
        At(h, 7, 0);
        var (_, booked) = await Book(h, user, route.Id, stop.Id, seat: 3);
        await h.RunAsync(async sp =>
        {
            await sp.GetRequiredService<DriverAlerts>().NewPassengerAsync(route.Id, user, stop.Id, Harness.Monday, booked, 3);
            return 0;
        });

        var sent = Assert.Single(h.Notifier.Sent);
        Assert.Equal(900, sent.Chat);
        Assert.Contains("Аня", sent.Text);
        Assert.Contains("утро и вечер", sent.Text);
        Assert.Contains("07:30", sent.Text);
        Assert.Contains("№ 3", sent.Text);
    }

    [Fact]
    public async Task Reminders_are_sent_once_at_30_and_10_minutes_before_departure()
    {
        using var h = new Harness();
        var (route, stop) = await h.AddRouteAsync(seats: 3);
        var rider = await h.AddUserAsync("Аня", telegramId: 101);
        var skipper = await h.AddUserAsync("Не едет", telegramId: 102);
        h.Clock.LocalNow = Harness.Monday.AddDays(-3).ToDateTime(new TimeOnly(12, 0));   // пятница: записаться и отметить «не еду» на понедельник ещё можно
        await h.RunAsync(sp => sp.GetRequiredService<BookingService>().JoinRouteAsync(skipper, route.Id, stop.Id, true, true, "test"));
        await h.RunAsync(sp => sp.GetRequiredService<BookingService>().MarkAbsentAsync(skipper, Harness.Monday, Direction.Morning, "test"));

        At(h, 6, 50);
        await Book(h, rider, route.Id, stop.Id);
        Task<int> Tick() => h.RunAsync(sp => sp.GetRequiredService<DepartureReminders>().SendDueAsync());

        Assert.Equal(0, await Tick());                        // 40 минут до выезда — рано
        At(h, 7, 1);
        Assert.Equal(1, await Tick());
        Assert.Equal(0, await Tick());                        // повторно не шлём
        At(h, 7, 21);
        Assert.Equal(1, await Tick());
        At(h, 7, 31);
        Assert.Equal(0, await Tick());                        // уже выехали

        Assert.All(h.Notifier.Sent, s => Assert.Equal(101, s.Chat));
        Assert.Contains("Через 29 мин", h.Notifier.Sent[0].Text);
        Assert.Contains("Через 9 мин", h.Notifier.Sent[1].Text);
    }

    [Fact]
    public async Task No_reminders_after_driver_departed()
    {
        using var h = new Harness();
        var (route, stop) = await h.AddRouteAsync(seats: 3);
        var rider = await h.AddUserAsync("Аня", telegramId: 101);
        At(h, 6, 50);
        await Book(h, rider, route.Id, stop.Id);
        At(h, 7, 21);
        await h.RunAsync(sp => sp.GetRequiredService<TripOpsService>().DepartAsync(route.Id, Harness.Monday, Direction.Morning, "test"));
        h.Notifier.Sent.Clear();

        Assert.Equal(0, await h.RunAsync(sp => sp.GetRequiredService<DepartureReminders>().SendDueAsync()));
    }
}
