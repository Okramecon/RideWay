using GoHome.Web.Domain;
using GoHome.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace GoHome.Web.Pages;

public class TripsModel(TripService trips, TripOpsService ops, IClock clock) : AdminPageModel
{
    [BindProperty(SupportsGet = true)] public DateOnly? Date { get; set; }
    public DateOnly Day => Date ?? clock.Today;
    public List<TripRoster> Rosters { get; private set; } = [];

    public async Task OnGetAsync() => Rosters = await trips.GetDayAsync(Day);

    private object Back => new { date = Day.ToString("yyyy-MM-dd") };

    public async Task<IActionResult> OnPostMarkAsync(int routeId, Direction dir, int userId, RideResult result) =>
        Done(await ops.MarkRideAsync(routeId, Day, dir, userId, result, null, Actor), Back);

    public async Task<IActionResult> OnPostArrivedAsync(int routeId, Direction dir) =>
        Done(await ops.MarkArrivedAsync(routeId, Day, dir, Actor), Back);

    public async Task<IActionResult> OnPostCancelAsync(int routeId, Direction dir, string? reason) =>
        Done((await ops.CancelTripAsync(routeId, Day, dir, reason ?? "", null, Actor)).Result, Back);

    public async Task<IActionResult> OnPostRestoreAsync(int routeId, Direction dir) =>
        Done(await ops.RestoreTripAsync(routeId, Day, dir, Actor), Back);
}
