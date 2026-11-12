using GoHome.Web.Services;

namespace GoHome.Web.Pages;

public class IndexModel(MetricsService metrics, TripService trips, WorkCalendar calendar, IClock clock) : AdminPageModel
{
    public PilotMetrics Metrics { get; private set; } = null!;
    public List<TripRoster> Today { get; private set; } = [];
    public DateOnly TodayDate { get; private set; }

    public async Task OnGetAsync(DateOnly? from, DateOnly? to)
    {
        Metrics = await metrics.GetAsync(from, to);
        TodayDate = calendar.IsWorkday(clock.Today) ? clock.Today : calendar.Workdays(clock.Today, clock.Today.AddDays(10)).First();
        Today = await trips.GetDayAsync(TodayDate);
    }
}
