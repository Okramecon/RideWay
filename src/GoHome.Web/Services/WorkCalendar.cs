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
