using GoHome.Web.Domain;
using Microsoft.Extensions.Options;

namespace GoHome.Web.Services;

/// <summary>Рабочие дни и дедлайны изменений.</summary>
public class WorkCalendar(IOptions<PilotOptions> options, IClock clock)
{
    private readonly PilotOptions _o = options.Value;

    public bool IsWorkday(DateOnly d) =>
        d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !_o.Holidays.Contains(d);

    /// <summary>Крайний момент (местное время) для изменений по рейсу этой даты: накануне в ChangeDeadline.</summary>
    public DateTime DeadlineFor(DateOnly date) => date.AddDays(-1).ToDateTime(_o.ChangeDeadline);

    public bool CanChange(DateOnly date) => clock.LocalNow < DeadlineFor(date);

    /// <summary>За сколько минут до посадки закрывается запись «на сегодня».</summary>
    public const int SameDayCutoffMinutes = 10;

    /// <summary>Запись (и отмена разовой записи) в день поездки: сегодня и не позже чем за 10 минут до посадки.</summary>
    public bool CanBookSameDay(DateOnly date, TimeOnly boardTime) =>
        date == clock.Today && clock.LocalNow < date.ToDateTime(boardTime).AddMinutes(-SameDayCutoffMinutes);

    /// <summary>Когда пассажир садится: утром — у своей остановки, вечером — у офиса при выезде.</summary>
    public static TimeOnly BoardTime(Route route, Stop stop, Direction dir) =>
        dir == Direction.Morning ? stop.MorningTime : route.Stops.Min(s => s.EveningTime);

    /// <summary>Ближайшие рабочие дни, на которые ещё можно что-то менять.</summary>
    public List<DateOnly> UpcomingChangeableDays(int? count = null)
    {
        var result = new List<DateOnly>();
        var d = clock.Today;
        for (var i = 0; result.Count < (count ?? _o.BookingDaysAhead) && i < 30; i++, d = d.AddDays(1))
            if (IsWorkday(d) && CanChange(d)) result.Add(d);
        return result;
    }

    public IEnumerable<DateOnly> Workdays(DateOnly from, DateOnly to)
    {
        for (var d = from; d <= to; d = d.AddDays(1))
            if (IsWorkday(d)) yield return d;
    }

    public static string Format(DateOnly d) =>
        d.ToString("ddd dd.MM", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));

    public static string Format(Direction d) => d == Direction.Morning ? "утро" : "вечер";
}
