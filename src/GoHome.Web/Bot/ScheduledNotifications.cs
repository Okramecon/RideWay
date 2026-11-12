using System.Text;
using GoHome.Web.Data;
using GoHome.Web.Domain;
using GoHome.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GoHome.Web.Bot;

/// <summary>
/// Плановые уведомления (проверка раз в минуту, местное время):
/// • после дедлайна (21:05) — водителям список на следующий рабочий день;
/// • пятница 19:30 — пассажирам сумма к оплате за неделю.
/// </summary>
public class ScheduledNotifications(IServiceScopeFactory scopes, IClock clock, IOptions<PilotOptions> options, ILogger<ScheduledNotifications> log)
    : BackgroundService
{
    private readonly Dictionary<string, DateOnly> _sent = [];

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                await TickAsync();
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Scheduled notifications failed");
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    private async Task TickAsync()
    {
        var now = clock.LocalNow;
        var today = DateOnly.FromDateTime(now);
        var time = TimeOnly.FromDateTime(now);

        var rosterAt = options.Value.ChangeDeadline.AddMinutes(5);
        if (InWindow(time, rosterAt) && Once("driver-roster", today))
            await SendDriverRostersAsync(today);

        if (today.DayOfWeek == DayOfWeek.Friday && InWindow(time, new TimeOnly(19, 30)) && Once("weekly-bill", today))
            await SendWeeklyBillsAsync();
    }

    private static bool InWindow(TimeOnly now, TimeOnly at) => now >= at && now < at.AddMinutes(30);

    private bool Once(string key, DateOnly day)
    {
        if (_sent.TryGetValue(key, out var last) && last == day) return false;
        _sent[key] = day;
        return true;
    }

    private async Task SendDriverRostersAsync(DateOnly today)
    {
        using var scope = scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var calendar = sp.GetRequiredService<WorkCalendar>();
        var tomorrow = today.AddDays(1);
        if (!calendar.IsWorkday(tomorrow)) return;

        var db = sp.GetRequiredService<AppDbContext>();
        var trips = sp.GetRequiredService<TripService>();
        var notifier = sp.GetRequiredService<INotifier>();
        var routes = await db.Routes.Include(r => r.Car).ThenInclude(c => c.Driver)
            .Where(r => r.IsActive && r.Car.Driver != null && r.Car.Driver.TelegramId != null).ToListAsync();

        foreach (var route in routes)
        {
            var sb = new StringBuilder("📋 <b>Список на завтра</b>\n\n");
            foreach (var dir in Enum.GetValues<Direction>())
                sb.AppendLine(BotTexts.Roster(await trips.GetRosterAsync(route.Id, tomorrow, dir), withPhones: true));
            await notifier.SendAsync(route.Car.Driver!.TelegramId!.Value, sb.ToString());
        }
    }

    private async Task SendWeeklyBillsAsync()
    {
        using var scope = scopes.CreateScope();
        var billing = scope.ServiceProvider.GetRequiredService<BillingService>();
        var notifier = scope.ServiceProvider.GetRequiredService<INotifier>();
        foreach (var b in await billing.GetAllAsync())
        {
            if (b.Due <= 0 || b.User.TelegramId is null) continue;
            await notifier.SendAsync(b.User.TelegramId.Value,
                $"💰 Итоги недели: к оплате <b>{BotTexts.Money(b.Due)}</b>.\nОплатить можно наличными или переводом водителю своего маршрута.");
        }
    }
}
