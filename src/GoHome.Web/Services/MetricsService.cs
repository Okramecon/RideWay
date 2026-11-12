using GoHome.Web.Data;
using GoHome.Web.Domain;
using Microsoft.EntityFrameworkCore;

namespace GoHome.Web.Services;

public record RouteMetrics(
    string Route,
    int Seats,
    int TripsRun,
    int TripsCancelled,
    int Boarded,
    int NoShows,
    double Occupancy,
    int ArrivalsMarked,
    double? AvgMorningDelayMin,
    decimal Revenue);

public record PilotMetrics(
    DateOnly From,
    DateOnly To,
    List<RouteMetrics> Routes,
    int Regulars,
    int Waitlist,
    int EverRode,
    int ActiveLast7Days,
    int FrequentLast7Days,
    int Absences,
    decimal Charged,
    decimal Paid)
{
    public int TripsRun => Routes.Sum(r => r.TripsRun);
    public int Boarded => Routes.Sum(r => r.Boarded);
    public int NoShows => Routes.Sum(r => r.NoShows);
    public double Occupancy => Routes.Sum(r => r.TripsRun * r.Seats) is var cap and > 0 ? (double)Boarded / cap : 0;
    public decimal Due => Charged - Paid;
}

/// <summary>Метрики пилота (раздел 2 PILOT_SPEC.md).</summary>
public class MetricsService(AppDbContext db, WorkCalendar calendar, IClock clock)
{
    public async Task<PilotMetrics> GetAsync(DateOnly? from = null, DateOnly? to = null)
    {
        var today = clock.Today;
        var end = to ?? today;
        var start = from ?? await db.RideLogs.MinAsync(l => (DateOnly?)l.Date) ?? today;

        var routes = await db.Routes.Include(r => r.Car).Include(r => r.Stops).OrderBy(r => r.Name).ToListAsync();
        var logs = await db.RideLogs.Where(l => l.Date >= start && l.Date <= end).ToListAsync();
        var cancels = await db.TripCancellations.Where(c => c.Date >= start && c.Date <= end).ToListAsync();
        var arrivals = await db.TripArrivals.Where(a => a.Date >= start && a.Date <= end).ToListAsync();
        var workdays = calendar.Workdays(start, end).ToList();

        var routeMetrics = routes.Select(r =>
        {
            var cancelled = cancels.Count(c => c.RouteId == r.Id);
            var tripsRun = r.IsActive ? workdays.Count * 2 - cancelled : 0;
            var rlogs = logs.Where(l => l.RouteId == r.Id).ToList();
            var boarded = rlogs.Count(l => l.Result == RideResult.Boarded);

            // Утреннее опоздание: прибытие к последней остановке (офис) против её планового времени.
            var target = r.Stops.MaxBy(s => s.Order)?.MorningTime;
            var delays = arrivals.Where(a => a.RouteId == r.Id && a.Direction == Direction.Morning && target is not null)
                .Select(a => (TimeOnly.FromDateTime(a.ArrivedAt) - target!.Value).TotalMinutes)
                .Select(m => m > 720 ? m - 1440 : m) // TimeOnly вычитание циклично
                .ToList();

            return new RouteMetrics(r.Name, r.Car.Seats, tripsRun, cancelled, boarded,
                rlogs.Count(l => l.Result == RideResult.NoShow),
                tripsRun > 0 ? (double)boarded / (tripsRun * r.Car.Seats) : 0,
                arrivals.Count(a => a.RouteId == r.Id),
                delays.Count > 0 ? delays.Average() : null,
                rlogs.Sum(l => l.Price));
        }).ToList();

        var weekAgo = today.AddDays(-7);
        var lastWeek = await db.RideLogs.Where(l => l.Date > weekAgo && l.Result == RideResult.Boarded)
            .Select(l => new { l.UserId, l.Date }).ToListAsync();
        var daysByUser = lastWeek.GroupBy(x => x.UserId).Select(g => g.Select(x => x.Date).Distinct().Count()).ToList();

        var memberships = await db.Memberships.Where(m => m.Status != MembershipStatus.Left).ToListAsync();
        var paid = (await db.Payments.Select(p => p.Amount).ToListAsync()).Sum();
        var charged = (await db.RideLogs.Select(l => l.Price).ToListAsync()).Sum();

        return new PilotMetrics(start, end, routeMetrics,
            memberships.Count(m => m.Status == MembershipStatus.Regular),
            memberships.Count(m => m.Status == MembershipStatus.Waitlist),
            await db.RideLogs.Where(l => l.Result == RideResult.Boarded).Select(l => l.UserId).Distinct().CountAsync(),
            daysByUser.Count,
            daysByUser.Count(d => d >= 4),
            await db.Absences.CountAsync(a => a.Date >= start && a.Date <= end),
            charged, paid);
    }
}
