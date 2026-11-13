using GoHome.Web.Data;
using GoHome.Web.Domain;
using GoHome.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GoHome.Tests;

public class SeatLayoutTests
{
    [Fact]
    public void Default_layout_has_driver_front_passenger_and_rear_rows()
    {
        var layout = SeatLayout.Default(4);
        Assert.Equal("D,.,1/2,3,4", layout.ToString());
        Assert.Equal([1, 2, 3, 4], layout.SeatNumbers);
        Assert.Equal(4, layout.SeatCount);
        Assert.Equal("D,.,1/2,3,.", SeatLayout.Default(3).ToString());
        Assert.Equal(7, SeatLayout.Default(7).SeatCount);
    }

    [Fact]
    public void Layout_round_trips_and_keeps_seat_numbers_stable()
    {
        Assert.True(SeatLayout.TryParse("D,.,5/2,.,9", out var layout, out _));
        Assert.Equal([2, 5, 9], layout!.SeatNumbers);
        Assert.Equal("D,.,5/2,.,9", layout.ToString());
        Assert.True(layout.HasSeat(9));
        Assert.False(layout.HasSeat(1));
    }

    [Theory]
    [InlineData("")]
    [InlineData("D,.,.")]            // нет мест
    [InlineData("1,1,2")]            // повтор номера
    [InlineData("D,D,1")]            // два водителя
    [InlineData("D,.,1/2,3")]        // разная ширина
    [InlineData("1,x,2")]            // мусор
    [InlineData("1,2,3,4,5,6")]      // слишком широко
    [InlineData("0,1,2")]            // номер 0
    public void Invalid_layouts_are_rejected(string text) => Assert.False(SeatLayout.TryParse(text, out _, out _));
}

public class SeatBookingTests
{
    private static BookingService Booking(IServiceProvider sp) => sp.GetRequiredService<BookingService>();

    private static Task<OpResult> Join(Harness h, int user, Route route, Stop stop, bool m = true, bool e = true, int? seat = null) =>
        h.RunAsync(sp => Booking(sp).JoinRouteAsync(user, route.Id, stop.Id, m, e, "test", seat));

    private static Task<OpResult> OneOff(Harness h, int user, Route route, Stop stop, DateOnly date, int? seat = null, Direction dir = Direction.Morning) =>
        h.RunAsync(sp => Booking(sp).BookOneOffAsync(user, route.Id, stop.Id, date, dir, "test", seat));

    private static Task<TripRoster> Roster(Harness h, int routeId, DateOnly date, Direction dir = Direction.Morning) =>
        h.RunAsync(sp => sp.GetRequiredService<TripService>().GetRosterAsync(routeId, date, dir));

    private static async Task<Membership> MembershipOf(Harness h, int user) =>
        await h.RunAsync(sp => sp.GetRequiredService<AppDbContext>().Memberships.OrderBy(m => m.Id).LastAsync(m => m.UserId == user));

    [Fact]
    public async Task Join_assigns_lowest_free_seat_and_respects_choice()
    {
        using var h = new Harness();
        var (route, stop) = await h.AddRouteAsync(seats: 4);
        var a = await h.AddUserAsync("A");
        var b = await h.AddUserAsync("B");
        var c = await h.AddUserAsync("C");

        Assert.True((await Join(h, a, route, stop)).Ok);
        Assert.Equal(1, (await MembershipOf(h, a)).SeatNumber);

        Assert.True((await Join(h, b, route, stop, seat: 4)).Ok);
        Assert.Equal(4, (await MembershipOf(h, b)).SeatNumber);

        Assert.True((await Join(h, c, route, stop)).Ok);
        Assert.Equal(2, (await MembershipOf(h, c)).SeatNumber);
    }

    [Fact]
    public async Task Taken_or_missing_seat_is_refused_and_nothing_changes()
    {
        using var h = new Harness();
        var (route, stop) = await h.AddRouteAsync(seats: 3);
        var a = await h.AddUserAsync("A");
        var b = await h.AddUserAsync("B");
        await Join(h, a, route, stop, seat: 2);

        Assert.False((await Join(h, b, route, stop, seat: 2)).Ok);
        Assert.False((await Join(h, b, route, stop, seat: 9)).Ok);
        Assert.Equal(1, await h.RunAsync(sp => sp.GetRequiredService<AppDbContext>().Memberships.CountAsync()));
    }

    [Fact]
    public async Task Full_route_puts_new_passenger_on_waitlist_without_seat()
    {
        using var h = new Harness();
        var (route, stop) = await h.AddRouteAsync(seats: 2);
        var users = new List<int>();
        for (var i = 0; i < 3; i++) users.Add(await h.AddUserAsync($"U{i}"));
        foreach (var u in users) await Join(h, u, route, stop);

        var last = await MembershipOf(h, users[2]);
        Assert.Equal(MembershipStatus.Waitlist, last.Status);
        Assert.Null(last.SeatNumber);
    }

    [Fact]
    public async Task Morning_only_and_evening_only_passengers_can_share_a_seat()
    {
        using var h = new Harness();
        var (route, stop) = await h.AddRouteAsync(seats: 1);
        var a = await h.AddUserAsync("Morning");
        var b = await h.AddUserAsync("Evening");
        Assert.True((await Join(h, a, route, stop, m: true, e: false)).Ok);
        Assert.True((await Join(h, b, route, stop, m: false, e: true)).Ok);
        Assert.Equal(1, (await MembershipOf(h, b)).SeatNumber);
        Assert.Equal(MembershipStatus.Regular, (await MembershipOf(h, b)).Status);
    }

    [Fact]
    public async Task OneOff_takes_chosen_seat_and_roster_shows_owners()
    {
        using var h = new Harness();
        var (route, stop) = await h.AddRouteAsync(seats: 4);
        var regular = await h.AddUserAsync("Regular");
        var guest = await h.AddUserAsync("Guest");
        var other = await h.AddUserAsync("Other");
        await Join(h, regular, route, stop);                        // место 1

        Assert.True((await OneOff(h, guest, route, stop, h.Tuesday, seat: 3)).Ok);
        Assert.False((await OneOff(h, other, route, stop, h.Tuesday, seat: 3)).Ok);   // занято
        Assert.False((await OneOff(h, other, route, stop, h.Tuesday, seat: 1)).Ok);   // место постоянного
        Assert.True((await OneOff(h, other, route, stop, h.Tuesday)).Ok);             // авто → место 2

        var roster = await Roster(h, route.Id, h.Tuesday);
        Assert.Equal([1, 2, 3], roster.TakenSeats.OrderBy(n => n));
        Assert.Equal([4], roster.FreeSeatNumbers);
        Assert.Equal("Guest", roster.SeatOwners[3].Name);
    }

    [Fact]
    public async Task Seat_of_absent_regular_can_be_booked_but_not_reclaimed()
    {
        using var h = new Harness();
        var (route, stop) = await h.AddRouteAsync(seats: 2);
        var regular = await h.AddUserAsync("Regular");
        var guest = await h.AddUserAsync("Guest");
        await Join(h, regular, route, stop, seat: 2);

        await h.RunAsync(sp => Booking(sp).MarkAbsentAsync(regular, h.Tuesday, Direction.Morning, "test"));
        Assert.Contains(2, (await Roster(h, route.Id, h.Tuesday)).FreeSeatNumbers);

        Assert.True((await OneOff(h, guest, route, stop, h.Tuesday, seat: 2)).Ok);
        var back = await h.RunAsync(sp => Booking(sp).UnmarkAbsentAsync(regular, h.Tuesday, Direction.Morning, "test"));
        Assert.False(back.Ok);

        // Гость отменил — место снова можно вернуть.
        var guestBooking = await h.RunAsync(sp => sp.GetRequiredService<AppDbContext>().OneOffBookings.SingleAsync());
        await h.RunAsync(sp => Booking(sp).CancelOneOffAsync(guestBooking.Id, guest, "test"));
        Assert.True((await h.RunAsync(sp => Booking(sp).UnmarkAbsentAsync(regular, h.Tuesday, Direction.Morning, "test"))).Ok);
    }

    [Fact]
    public async Task Parallel_bookings_get_distinct_seats()
    {
        using var h = new Harness();
        var (route, stop) = await h.AddRouteAsync(seats: 3);
        var users = new List<int>();
        for (var i = 0; i < 8; i++) users.Add(await h.AddUserAsync($"U{i}"));

        var results = await Task.WhenAll(users.Select(u => Task.Run(() => OneOff(h, u, route, stop, h.Tuesday))));
        Assert.Equal(3, results.Count(r => r.Ok));

        var seats = (await Roster(h, route.Id, h.Tuesday)).Entries.Select(e => e.SeatNumber).ToList();
        Assert.Equal(3, seats.Distinct().Count());
        Assert.DoesNotContain(null, seats);
    }

    [Fact]
    public async Task Parallel_bookings_of_the_same_seat_allow_only_one()
    {
        using var h = new Harness();
        var (route, stop) = await h.AddRouteAsync(seats: 4);
        var users = new List<int>();
        for (var i = 0; i < 6; i++) users.Add(await h.AddUserAsync($"U{i}"));

        var results = await Task.WhenAll(users.Select(u => Task.Run(() => OneOff(h, u, route, stop, h.Tuesday, seat: 3))));
        Assert.Equal(1, results.Count(r => r.Ok));
    }

    [Fact]
    public async Task ChangeSeat_moves_regular_to_free_seat_only()
    {
        using var h = new Harness();
        var (route, stop) = await h.AddRouteAsync(seats: 3);
        var a = await h.AddUserAsync("A");
        var b = await h.AddUserAsync("B");
        await Join(h, a, route, stop);                 // 1
        await Join(h, b, route, stop);                 // 2

        Assert.False((await h.RunAsync(sp => Booking(sp).ChangeSeatAsync(a, 2, null, "test"))).Ok);
        Assert.True((await h.RunAsync(sp => Booking(sp).ChangeSeatAsync(a, 3, null, "test"))).Ok);
        Assert.Equal(3, (await MembershipOf(h, a)).SeatNumber);
        // Старое место теперь свободно.
        Assert.True((await Join(h, await h.AddUserAsync("C"), route, stop)).Ok);
    }

    [Fact]
    public async Task Joining_a_seat_with_someone_elses_future_oneoff_starts_after_it()
    {
        using var h = new Harness();
        var (route, stop) = await h.AddRouteAsync(seats: 1);
        var former = await h.AddUserAsync("Former");
        var guest = await h.AddUserAsync("Guest");
        var newcomer = await h.AddUserAsync("Newcomer");

        await Join(h, former, route, stop);
        await h.RunAsync(sp => Booking(sp).MarkAbsentAsync(former, h.Tuesday, Direction.Morning, "test"));
        Assert.True((await OneOff(h, guest, route, stop, h.Tuesday, seat: 1)).Ok);
        await h.RunAsync(sp => Booking(sp).LeaveRouteAsync(former, "test"));

        Assert.True((await Join(h, newcomer, route, stop, m: true, e: false)).Ok);
        var m = await MembershipOf(h, newcomer);
        Assert.Equal(MembershipStatus.Regular, m.Status);
        Assert.Equal(h.Tuesday.AddDays(1), m.From);          // со среды, вторник занят гостем
    }
}

public class CarServiceTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    [Fact]
    public async Task Layout_cannot_drop_occupied_seats_but_can_drop_free_ones()
    {
        using var h = new Harness();
        var (route, stop) = await h.AddRouteAsync(seats: 4);
        var a = await h.AddUserAsync("A");
        await h.RunAsync(sp => sp.GetRequiredService<BookingService>().JoinRouteAsync(a, route.Id, stop.Id, true, true, "test", 3));
        var carId = route.CarId;

        var refused = await h.RunAsync(sp => sp.GetRequiredService<CarService>().SaveLayoutAsync(carId, "D,.,1/2,.,4", "test"));
        Assert.False(refused.Ok);
        Assert.Contains("3", refused.Message);

        var ok = await h.RunAsync(sp => sp.GetRequiredService<CarService>().SaveLayoutAsync(carId, "D,.,1/.,3,.", "test"));
        Assert.True(ok.Ok);
        var car = await h.RunAsync(sp => sp.GetRequiredService<AppDbContext>().Cars.SingleAsync());
        Assert.Equal(2, car.Seats);
        Assert.Equal("D,.,1/.,3,.", car.Layout);

        Assert.False((await h.RunAsync(sp => sp.GetRequiredService<CarService>().SaveLayoutAsync(carId, "garbage", "test"))).Ok);
    }

    [Fact]
    public async Task Removed_seat_numbers_are_not_reused_for_other_seats()
    {
        using var h = new Harness();
        var (route, stop) = await h.AddRouteAsync(seats: 3);
        await h.RunAsync(sp => sp.GetRequiredService<CarService>().SaveLayoutAsync(route.CarId, "D,.,1/.,3,.", "test"));
        var a = await h.AddUserAsync("A");
        await h.RunAsync(sp => sp.GetRequiredService<BookingService>().JoinRouteAsync(a, route.Id, stop.Id, true, true, "test"));
        var m = await h.RunAsync(sp => sp.GetRequiredService<AppDbContext>().Memberships.SingleAsync());
        Assert.Equal(1, m.SeatNumber);
    }

    [Fact]
    public async Task Photo_is_stored_validated_and_replaced()
    {
        using var h = new Harness();
        var (route, _) = await h.AddRouteAsync(seats: 3);

        var bad = await h.RunAsync(sp => sp.GetRequiredService<CarService>().SavePhotoAsync(route.CarId, new MemoryStream("<html>"u8.ToArray()), "test"));
        Assert.False(bad.Ok);

        Assert.True((await h.RunAsync(sp => sp.GetRequiredService<CarService>().SavePhotoAsync(route.CarId, new MemoryStream(Png), "test"))).Ok);
        var first = (await h.RunAsync(sp => sp.GetRequiredService<AppDbContext>().Cars.SingleAsync())).CabinPhoto!;
        Assert.EndsWith(".png", first);
        Assert.NotNull(h.RunAsync(sp => Task.FromResult(sp.GetRequiredService<CarService>().MediaPath(first))).Result);

        Assert.True((await h.RunAsync(sp => sp.GetRequiredService<CarService>().SavePhotoAsync(route.CarId, new MemoryStream(Png), "test"))).Ok);
        Assert.Null(h.RunAsync(sp => Task.FromResult(sp.GetRequiredService<CarService>().MediaPath(first))).Result);   // старый файл удалён

        Assert.True((await h.RunAsync(sp => sp.GetRequiredService<CarService>().RemovePhotoAsync(route.CarId, "test"))).Ok);
        Assert.Null((await h.RunAsync(sp => sp.GetRequiredService<AppDbContext>().Cars.SingleAsync())).CabinPhoto);
    }

    [Fact]
    public async Task Oversized_photo_is_refused()
    {
        using var h = new Harness();
        var (route, _) = await h.AddRouteAsync(seats: 3);
        var big = new byte[CarService.MaxPhotoBytes + 10];
        Png.CopyTo(big, 0);
        Assert.False((await h.RunAsync(sp => sp.GetRequiredService<CarService>().SavePhotoAsync(route.CarId, new MemoryStream(big), "test"))).Ok);
    }

    [Fact]
    public async Task Backfill_assigns_layouts_and_distinct_seats_to_legacy_records()
    {
        using var h = new Harness();
        var (route, stop) = await h.AddRouteAsync(seats: 3);
        var users = new[] { await h.AddUserAsync("A"), await h.AddUserAsync("B") };
        await h.RunAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            foreach (var u in users)
                db.Memberships.Add(new Membership { UserId = u, RouteId = route.Id, StopId = stop.Id, Morning = true, Evening = true,
                    Status = MembershipStatus.Regular, From = Harness.Monday, CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
            return 0;
        });

        await h.RunAsync(async sp => { await SeatBackfill.RunAsync(sp.GetRequiredService<AppDbContext>(), Harness.Monday); return 0; });

        var (ms, car) = await h.RunAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            return (await db.Memberships.ToListAsync(), await db.Cars.SingleAsync());
        });
        Assert.Equal("D,.,1/2,3,.", car.Layout);
        Assert.Equal([1, 2], ms.Select(m => m.SeatNumber!.Value).OrderBy(n => n));
    }
}
