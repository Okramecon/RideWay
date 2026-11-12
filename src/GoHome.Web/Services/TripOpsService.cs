using GoHome.Web.Data;
using GoHome.Web.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GoHome.Web.Services;

/// <summary>Действия водителя/админа по рейсу: посадка, неявка, прибытие, отмена.</summary>
public class TripOpsService(
    AppDbContext db, TripService trips, IClock clock, AuditService audit, INotifier notifier, IOptions<PilotOptions> options)
{
    public async Task<OpResult> MarkRideAsync(int routeId, DateOnly date, Direction dir, int userId, RideResult result, int? actorUserId, string actor)
    {
        if (date > clock.Today) return OpResult.Fail("Этот рейс ещё не состоялся.");
        var roster = await trips.GetRosterAsync(routeId, date, dir);
        if (roster.IsCancelled) return OpResult.Fail("Рейс отменён.");
        var entry = roster.Entries.FirstOrDefault(e => e.UserId == userId);
        if (entry is null) return OpResult.Fail("Пассажира нет в списке рейса.");

        var price = result == RideResult.Boarded || options.Value.ChargeNoShow ? roster.Route.PricePerRide : 0m;

        // Ключ уникальности — (пассажир, дата, направление): повторная отметка перезаписывает.
        var log = await db.RideLogs.SingleOrDefaultAsync(l => l.UserId == userId && l.Date == date && l.Direction == dir);
        if (log is null)
        {
            log = new RideLog { UserId = userId, Date = date, Direction = dir };
            db.RideLogs.Add(log);
        }
        log.RouteId = routeId;
        log.Result = result;
        log.Price = price;
        log.MarkedByUserId = actorUserId;
        log.MarkedAt = clock.UtcNow;
        await db.SaveChangesAsync();
        await audit.LogAsync("RideLog", log.Id, result.ToString(), $"route={routeId} {date} {dir} price={price}", actor);

        return OpResult.Success($"{entry.Name}: {(result == RideResult.Boarded ? "сел" : "не пришёл")}.");
    }

    public async Task<OpResult> MarkArrivedAsync(int routeId, DateOnly date, Direction dir, string actor)
    {
        // Время прибытия = момент нажатия, поэтому только для сегодняшнего рейса.
        if (date != clock.Today) return OpResult.Fail("Прибытие отмечается только в день рейса.");
        var arrival = await db.TripArrivals.SingleOrDefaultAsync(a => a.RouteId == routeId && a.Date == date && a.Direction == dir);
        if (arrival is null)
        {
            arrival = new TripArrival { RouteId = routeId, Date = date, Direction = dir };
            db.TripArrivals.Add(arrival);
        }
        arrival.ArrivedAt = clock.LocalNow;
        await db.SaveChangesAsync();
        await audit.LogAsync("TripArrival", arrival.Id, "Arrived", $"{arrival.ArrivedAt:HH:mm}", actor);
        return OpResult.Success($"Рейс завершён в {arrival.ArrivedAt:HH:mm}.");
    }

    /// <summary>Отменяет рейс и уведомляет всех, кто должен был ехать. Возвращает затронутых пассажиров.</summary>
    public async Task<(OpResult Result, List<RosterEntry> Affected)> CancelTripAsync(
        int routeId, DateOnly date, Direction dir, string reason, int? actorUserId, string actor)
    {
        var roster = await trips.GetRosterAsync(routeId, date, dir);
        if (roster.IsCancelled) return (OpResult.Fail("Рейс уже отменён."), []);

        var c = new TripCancellation
        {
            RouteId = routeId, Date = date, Direction = dir, Reason = reason,
            CreatedByUserId = actorUserId, CreatedAt = clock.UtcNow,
        };
        db.TripCancellations.Add(c);
        await db.SaveChangesAsync();
        await audit.LogAsync("TripCancellation", c.Id, "Cancel", $"route={routeId} {date} {dir}: {reason}", actor);

        var affected = roster.Riding.ToList();
        var text = $"⛔ Рейс «{roster.Route.Name}» {WorkCalendar.Format(date)}, {WorkCalendar.Format(dir)} отменён."
                   + (string.IsNullOrWhiteSpace(reason) ? "" : $"\nПричина: {reason}") + "\nПоездка не оплачивается.";
        foreach (var e in affected.Where(e => e.TelegramId is not null))
            await notifier.SendAsync(e.TelegramId!.Value, text);

        return (OpResult.Success($"Рейс отменён, уведомлено пассажиров: {affected.Count(e => e.TelegramId is not null)} из {affected.Count}."), affected);
    }

    public async Task<OpResult> RestoreTripAsync(int routeId, DateOnly date, Direction dir, string actor)
    {
        var c = await db.TripCancellations.SingleOrDefaultAsync(x => x.RouteId == routeId && x.Date == date && x.Direction == dir);
        if (c is null) return OpResult.Fail("Рейс не отменён.");
        db.TripCancellations.Remove(c);
        await db.SaveChangesAsync();
        await audit.LogAsync("TripCancellation", c.Id, "Restore", null, actor);
        return OpResult.Success("Отмена рейса снята.");
    }
}
