using GoHome.Web.Data;
using GoHome.Web.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GoHome.Web.Services;

/// <summary>Действия водителя/админа по рейсу: посадка, неявка, прибытие, отмена.</summary>
public class TripOpsService(
    AppDbContext db, TripService trips, IClock clock, AuditService audit, INotifier notifier, IOptions<PilotOptions> options)
{
    /// <summary>Окно, в котором принимается посадка по QR: от −30 до +60 минут к времени посадки пассажира.</summary>
    public static readonly TimeSpan BoardEarly = TimeSpan.FromMinutes(30), BoardLate = TimeSpan.FromMinutes(60);

    /// <summary>
    /// Посадка по QR-наклейке в машине. Рейс определяется по машине и текущему времени;
    /// принимается только для пассажира из списка рейса и только в окне вокруг его времени посадки.
    /// </summary>
    public async Task<OpResult> BoardByCodeAsync(int userId, string code, string actor)
    {
        var route = await db.Routes.Include(r => r.Car)
            .FirstOrDefaultAsync(r => r.IsActive && r.Car.BoardingCode == code && code != "");
        if (route is null) return OpResult.Fail("QR-код не распознан. Попросите водителя отметить вас вручную.");

        var now = clock.LocalNow;
        var today = clock.Today;
        foreach (var dir in Enum.GetValues<Direction>())
        {
            var roster = await trips.GetRosterAsync(route.Id, today, dir);
            if (!roster.Runs) continue;
            var entry = roster.Riding.FirstOrDefault(e => e.UserId == userId);
            if (entry is null) continue;

            var boardAt = dir == Direction.Morning ? entry.Stop.MorningTime : roster.DepartureTime ?? entry.Stop.EveningTime;
            var at = today.ToDateTime(boardAt);
            if (now < at - BoardEarly || now > at + BoardLate) continue;

            if (entry.Result == RideResult.Boarded)
                return OpResult.Success($"Посадка уже отмечена: {route.Name}, {WorkCalendar.Format(dir)}.");
            var r = await MarkRideAsync(route.Id, today, dir, userId, RideResult.Boarded, userId, actor);
            return r.Ok ? OpResult.Success($"Посадка отмечена: {route.Name}, {WorkCalendar.Format(dir)}. Хорошей дороги!") : r;
        }
        return OpResult.Fail($"Сейчас у вас нет поездки на «{route.Name}». Отметиться можно за 30 минут до посадки и в течение часа после.");
    }

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
