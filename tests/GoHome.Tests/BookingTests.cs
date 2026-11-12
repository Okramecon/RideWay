using GoHome.Web.Domain;
using GoHome.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace GoHome.Tests;

public class BookingTests
{
    private static Task<OpResult> Join(Harness h, int userId, int routeId, int stopId) =>
        h.RunAsync(sp => sp.GetRequiredService<BookingService>().JoinRouteAsync(userId, routeId, stopId, true, true, "test"));

    private static Task<OpResult> BookOneOff(Harness h, int userId, int routeId, int stopId, DateOnly date, Direction dir = Direction.Morning) =>
        h.RunAsync(sp => sp.GetRequiredService<BookingService>().BookOneOffAsync(userId, routeId, stopId, date, dir, "test"));

    private static Task<TripRoster> Roster(Harness h, int routeId, DateOnly date, Direction dir = Direction.Morning) =>
        h.RunAsync(sp => sp.GetRequiredService<TripService>().GetRosterAsync(routeId, date, dir));

    [Fact]
    public async Task Regulars_beyond_capacity_go_to_waitlist()
    {
        using var h = new Harness();
        var (route, stop) = await h.AddRouteAsync(seats: 2);
        var a = await h.AddUserAsync("A");
        var b = await h.AddUserAsync("B");
        var c = await h.AddUserAsync("C");

        Assert.Contains("записаны", (await Join(h, a, route.Id, stop.Id)).Message);
        Assert.Contains("записаны", (await Join(h, b, route.Id, stop.Id)).Message);
        Assert.Contains("очереди", (await Join(h, c, route.Id, stop.Id)).Message);

        var roster = await Roster(h, route.Id, h.Tuesday);
        Assert.Equal(2, roster.Occupied);
        Assert.Equal(0, roster.Free);
    }

    [Fact]
    public async Task Waitlisted_can_be_promoted_only_after_seat_frees()
    {
        using var h = new Harness();
        var (route, stop) = await h.AddRouteAsync(seats: 1);
        var a = await h.AddUserAsync("A");
        var b = await h.AddUserAsync("B");
        await Join(h, a, route.Id, stop.Id);
        await Join(h, b, route.Id, stop.Id);

        var waitId = await h.RunAsync(sp => Task.FromResult(sp.GetRequiredService<GoHome.Web.Data.AppDbContext>()
            .Memberships.Single(m => m.UserId == b).Id));

        Assert.False((await h.RunAsync(sp => sp.GetRequiredService<BookingService>().PromoteAsync(waitId, "test"))).Ok);
        await h.RunAsync(sp => sp.GetRequiredService<BookingService>().LeaveRouteAsync(a, "test"));
        Assert.True((await h.RunAsync(sp => sp.GetRequiredService<BookingService>().PromoteAsync(waitId, "test"))).Ok);
    }

    [Fact]
    public async Task OneOff_uses_seat_freed_by_absence_and_blocks_return()
    {
        using var h = new Harness();
        var (route, stop) = await h.AddRouteAsync(seats: 1);
        var regular = await h.AddUserAsync("Regular");
        var guest = await h.AddUserAsync("Guest");
        await Join(h, regular, route.Id, stop.Id);

        Assert.False((await BookOneOff(h, guest, route.Id, stop.Id, h.Tuesday)).Ok);

        var absent = await h.RunAsync(sp => sp.GetRequiredService<BookingService>().MarkAbsentAsync(regular, h.Tuesday, Direction.Morning, "test"));
        Assert.True(absent.Ok);
        Assert.True((await BookOneOff(h, guest, route.Id, stop.Id, h.Tuesday)).Ok);

        // Место уже занято разовой поездкой — вернуться нельзя, иначе перебор.
        var back = await h.RunAsync(sp => sp.GetRequiredService<BookingService>().UnmarkAbsentAsync(regular, h.Tuesday, Direction.Morning, "test"));
        Assert.False(back.Ok);
        Assert.Equal(1, (await Roster(h, route.Id, h.Tuesday)).Occupied);
    }

    [Fact]
    public async Task Changes_are_closed_after_deadline_the_evening_before()
    {
        using var h = new Harness();
        var (route, stop) = await h.AddRouteAsync(seats: 3);
        var a = await h.AddUserAsync("A");
        var b = await h.AddUserAsync("B");
        await Join(h, a, route.Id, stop.Id);

        h.Clock.LocalNow = Harness.Monday.ToDateTime(new TimeOnly(21, 1));

        Assert.False((await h.RunAsync(sp => sp.GetRequiredService<BookingService>().MarkAbsentAsync(a, h.Tuesday, Direction.Morning, "test"))).Ok);
        Assert.False((await BookOneOff(h, b, route.Id, stop.Id, h.Tuesday)).Ok);
    }

    [Fact]
    public async Task Same_user_cannot_hold_two_seats_on_one_trip()
    {
        using var h = new Harness();
        var (route, stop) = await h.AddRouteAsync(seats: 3);
        var a = await h.AddUserAsync("A");
        Assert.True((await BookOneOff(h, a, route.Id, stop.Id, h.Tuesday)).Ok);
        Assert.False((await BookOneOff(h, a, route.Id, stop.Id, h.Tuesday)).Ok);
    }

    [Fact]
    public async Task Parallel_bookings_never_exceed_capacity()
    {
        using var h = new Harness();
        var (route, stop) = await h.AddRouteAsync(seats: 3);
        var users = new List<int>();
        for (var i = 0; i < 10; i++) users.Add(await h.AddUserAsync($"U{i}"));

        var results = await Task.WhenAll(users.Select(u => Task.Run(() => BookOneOff(h, u, route.Id, stop.Id, h.Tuesday))));

        Assert.Equal(3, results.Count(r => r.Ok));
        Assert.Equal(3, (await Roster(h, route.Id, h.Tuesday)).Occupied);
    }

    [Fact]
    public async Task Weekend_and_cancelled_trips_are_not_bookable()
    {
        using var h = new Harness();
        var (route, stop) = await h.AddRouteAsync(seats: 3);
        var a = await h.AddUserAsync("A", telegramId: 111);
        var b = await h.AddUserAsync("B");

        Assert.False((await BookOneOff(h, b, route.Id, stop.Id, Harness.Monday.AddDays(5))).Ok); // суббота

        await Join(h, a, route.Id, stop.Id);
        var (res, affected) = await h.RunAsync(sp => sp.GetRequiredService<TripOpsService>()
            .CancelTripAsync(route.Id, h.Tuesday, Direction.Morning, "Ремонт", null, "test"));
        Assert.True(res.Ok);
        Assert.Single(affected);
        Assert.Single(h.Notifier.Sent);
        Assert.False((await BookOneOff(h, b, route.Id, stop.Id, h.Tuesday)).Ok);
    }
}

