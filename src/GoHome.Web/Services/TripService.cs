using GoHome.Web.Data;
using GoHome.Web.Domain;
using Microsoft.EntityFrameworkCore;

namespace GoHome.Web.Services;

public record RosterEntry(
    int UserId,
    string Name,
    string Phone,
    long? TelegramId,
    Stop Stop,
    bool IsRegular,
    bool IsAbsent,
    int? OneOffBookingId,
    RideResult? Result)
{
    /// <summary>Занимает место в машине.</summary>
    public bool TakesSeat => !IsAbsent;
}

public record TripRoster(
    Route Route,
    DateOnly Date,
    Direction Direction,
    bool IsWorkday,
    TripCancellation? Cancellation,
    DateTime? ArrivedAt,
    List<RosterEntry> Entries)
{
    public int Capacity => Route.Car.Seats;
    public int Occupied => Entries.Count(e => e.TakesSeat);
    public int Free => Math.Max(0, Capacity - Occupied);
    public bool IsCancelled => Cancellation is not null;
    public bool Runs => IsWorkday && !IsCancelled && Route.IsActive;
    public IEnumerable<RosterEntry> Riding => Entries.Where(e => e.TakesSeat);

    public TimeOnly? DepartureTime => Direction == Direction.Morning
        ? Route.Stops.MinBy(s => s.Order)?.MorningTime
        : Route.Stops.Min(s => (TimeOnly?)s.EveningTime);
}

/// <summary>
/// Рейс = (маршрут, дата, направление). Отдельной таблицы рейсов нет:
/// список строится из постоянных записей − «не еду» + разовые поездки.
/// </summary>
public class TripService(AppDbContext db, WorkCalendar calendar, IClock clock)
{
    public async Task<TripRoster> GetRosterAsync(int routeId, DateOnly date, Direction dir)
    {
        var route = await db.Routes.Include(r => r.Car).ThenInclude(c => c.Driver).Include(r => r.Stops)
            .SingleAsync(r => r.Id == routeId);

        var regulars = await db.Memberships.Include(m => m.User).Include(m => m.Stop)
            .Where(m => m.RouteId == routeId && m.Status == MembershipStatus.Regular
                        && m.From <= date && (m.To == null || m.To >= date))
            .ToListAsync();
        regulars = regulars.Where(m => m.Covers(dir)).ToList();

        var regularIds = regulars.Select(m => m.UserId).ToList();
        var absent = (await db.Absences
                .Where(a => a.Date == date && a.Direction == dir && regularIds.Contains(a.UserId))
                .Select(a => a.UserId).ToListAsync())
            .ToHashSet();

        var oneOffs = await db.OneOffBookings.Include(b => b.User).Include(b => b.Stop)
            .Where(b => b.RouteId == routeId && b.Date == date && b.Direction == dir && b.Status == BookingStatus.Booked)
            .ToListAsync();

        var results = await db.RideLogs
            .Where(l => l.RouteId == routeId && l.Date == date && l.Direction == dir)
            .ToDictionaryAsync(l => l.UserId, l => l.Result);

        var cancellation = await db.TripCancellations
            .SingleOrDefaultAsync(c => c.RouteId == routeId && c.Date == date && c.Direction == dir);
        var arrival = await db.TripArrivals
            .SingleOrDefaultAsync(a => a.RouteId == routeId && a.Date == date && a.Direction == dir);

        var entries = regulars
            .Select(m => new RosterEntry(m.UserId, m.User.Name, m.User.Phone, m.User.TelegramId, m.Stop,
                true, absent.Contains(m.UserId), null, ResultOf(results, m.UserId)))
            .Concat(oneOffs.Select(b => new RosterEntry(b.UserId, b.User.Name, b.User.Phone, b.User.TelegramId, b.Stop,
                false, false, b.Id, ResultOf(results, b.UserId))));

        // Утром забираем по порядку остановок, вечером развозим в обратном.
        entries = dir == Direction.Morning
            ? entries.OrderBy(e => e.Stop.Order).ThenBy(e => e.Name)
            : entries.OrderByDescending(e => e.Stop.Order).ThenBy(e => e.Name);

        return new TripRoster(route, date, dir, calendar.IsWorkday(date), cancellation, arrival?.ArrivedAt, entries.ToList());
    }

    public async Task<List<TripRoster>> GetDayAsync(DateOnly date)
    {
        var routeIds = await db.Routes.Where(r => r.IsActive).OrderBy(r => r.Name).Select(r => r.Id).ToListAsync();
        var list = new List<TripRoster>();
        foreach (var id in routeIds)
            foreach (var dir in Enum.GetValues<Direction>())
                list.Add(await GetRosterAsync(id, date, dir));
        return list;
    }

    /// <summary>Рейсы пассажира (где он занимает место) на ближайшие рабочие дни.</summary>
    public async Task<List<(TripRoster Trip, RosterEntry Entry)>> GetUserUpcomingAsync(int userId, int days = 5)
    {
        var routeIds = await db.Memberships
            .Where(m => m.UserId == userId && m.Status == MembershipStatus.Regular).Select(m => m.RouteId)
            .Union(db.OneOffBookings.Where(b => b.UserId == userId && b.Status == BookingStatus.Booked).Select(b => b.RouteId))
            .Distinct().ToListAsync();

        var result = new List<(TripRoster, RosterEntry)>();
        var dates = calendar.Workdays(clock.Today, clock.Today.AddDays(30)).Take(days);
        foreach (var date in dates)
            foreach (var routeId in routeIds)
                foreach (var dir in Enum.GetValues<Direction>())
                {
                    var roster = await GetRosterAsync(routeId, date, dir);
                    var entry = roster.Entries.FirstOrDefault(e => e.UserId == userId);
                    if (entry is not null) result.Add((roster, entry));
                }
        return result;
    }

    private static RideResult? ResultOf(Dictionary<int, RideResult> results, int userId) =>
        results.TryGetValue(userId, out var r) ? r : null;
}
