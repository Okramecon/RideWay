using GoHome.Web.Bot;
using GoHome.Web.Data;
using GoHome.Web.Domain;
using Microsoft.EntityFrameworkCore;

namespace GoHome.Web.Services;

/// <summary>Сообщения водителю об изменениях в его рейсах.</summary>
public class DriverAlerts(AppDbContext db, INotifier notifier, IClock clock, ILogger<DriverAlerts> log)
{
    /// <summary>На рейс(ы) маршрута записался новый пассажир.</summary>
    public async Task NewPassengerAsync(int routeId, int userId, int stopId, DateOnly date, IReadOnlyList<Direction> dirs, int? seat)
    {
        if (dirs.Count == 0) return;
        var route = await db.Routes.Include(r => r.Car).ThenInclude(c => c.Driver).Include(r => r.Stops)
            .SingleOrDefaultAsync(r => r.Id == routeId);
        var chat = route?.Car.Driver?.TelegramId;
        var user = await db.Users.FindAsync(userId);
        var stop = route?.Stops.FirstOrDefault(s => s.Id == stopId);
        if (chat is null || user is null || stop is null) return;

        var day = date == clock.Today ? "сегодня" : WorkCalendar.Format(date);
        var when = dirs.Count > 1 ? "утро и вечер" : WorkCalendar.Format(dirs[0]);
        var where = dirs.Contains(Direction.Morning)
            ? $"Посадка: {BotTexts.H(stop.Name)} в {stop.MorningTime:HH\\:mm}"
            : $"Высадка: {BotTexts.H(stop.Name)}";
        var text = $"➕ <b>Новый пассажир</b> — {day}, {when}\n"
                   + $"{BotTexts.H(user.Name)}, +{BotTexts.H(user.Phone)}\n"
                   + where
                   + (seat is { } n ? $"\nМесто № {n}" : "");
        try
        {
            await notifier.SendAsync(chat.Value, text);
        }
        catch (Exception ex)
        {
            // Запись уже сделана — из-за недоставленного сообщения её не отменяем.
            log.LogWarning(ex, "Driver alert failed for route {RouteId}", routeId);
        }
    }
}
