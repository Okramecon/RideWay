using GoHome.Web.Bot;
using GoHome.Web.Data;
using GoHome.Web.Domain;
using Microsoft.EntityFrameworkCore;

namespace GoHome.Web.Services;

/// <summary>
/// Напоминания пассажирам за 30 и за 10 минут до выезда машины (вызывается раз в минуту из ScheduledNotifications).
/// Окна (10; 30] и (0; 10] минут: кто записался позже 30-минутной отметки, получит то напоминание, в чьё окно попал.
/// Отправленное помечаем в журнале (Entity = Reminder), чтобы перезапуск приложения (например, автообновление)
/// не прислал его второй раз. Выехавшим (водитель нажал «Выехал») и уже севшим не напоминаем.
/// </summary>
public class DepartureReminders(
    AppDbContext db, TripService trips, TripOpsService ops, WorkCalendar calendar, AuditService audit,
    INotifier notifier, IClock clock, ILogger<DepartureReminders> log)
{
    public const string Entity = "Reminder";

    public async Task<int> SendDueAsync()
    {
        var now = clock.LocalNow;
        var today = DateOnly.FromDateTime(now);
        if (!calendar.IsWorkday(today)) return 0;
        var count = 0;

        foreach (var routeId in await db.Routes.Where(r => r.IsActive).Select(r => r.Id).ToListAsync())
            foreach (var dir in Enum.GetValues<Direction>())
            {
                var t = await trips.GetRosterAsync(routeId, today, dir);
                if (!t.Runs || t.ArrivedAt is not null || t.DepartureTime is not { } dep) continue;
                var left = (today.ToDateTime(dep) - now).TotalMinutes;
                int? kind = left is > 10 and <= 30 ? 30 : left is > 0 and <= 10 ? 10 : null;
                if (kind is null) continue;

                var riders = t.Riding.Where(e => e.TelegramId is not null && e.Result is null).ToList();
                if (riders.Count == 0 || await ops.IsDepartedAsync(routeId, today, dir)) continue;

                var key = $"{today:yyyy-MM-dd}|{dir}|{kind}";
                var sent = (await db.AuditLogs.Where(a => a.Entity == Entity && a.Details == key)
                    .Select(a => a.EntityId).ToListAsync()).ToHashSet();
                var mins = (int)Math.Ceiling(left);
                var car = $"{t.Route.Car.Model} {t.Route.Car.Plate}".Trim();

                foreach (var e in riders.Where(e => !sent.Contains(e.UserId)))
                {
                    var text = dir == Direction.Morning
                        ? $"⏰ Через {mins} мин машина выезжает.\nТочка сбора: <b>{BotTexts.H(e.Stop.Name)}</b>, машина ≈ {e.Stop.MorningTime:HH\\:mm}"
                          + (string.IsNullOrWhiteSpace(e.Stop.Note) ? "" : $" ({BotTexts.H(e.Stop.Note)})")
                        : $"⏰ Через {mins} мин выезд от офиса — в {dep:HH\\:mm}.";
                    text += $"\nМесто № {e.SeatNumber?.ToString() ?? "—"} · {BotTexts.H(car)}";
                    try
                    {
                        await notifier.SendAsync(e.TelegramId!.Value, text);
                        count++;
                    }
                    catch (Exception ex)
                    {
                        log.LogWarning(ex, "Reminder to user {UserId} failed", e.UserId);
                    }
                    await audit.LogAsync(Entity, e.UserId, "Sent", key, "system");
                }
            }
        return count;
    }
}
