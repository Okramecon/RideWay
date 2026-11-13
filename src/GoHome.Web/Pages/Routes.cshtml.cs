using System.Globalization;
using GoHome.Web.Data;
using GoHome.Web.Domain;
using GoHome.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GoHome.Web.Pages;

public class RoutesModel(AppDbContext db, AuditService audit, RouteMapService maps) : AdminPageModel
{
    public List<Route> Routes { get; private set; } = [];
    public List<Car> Cars { get; private set; } = [];
    public Dictionary<int, (int Regular, int Waitlist)> Counts { get; private set; } = [];

    /// <summary>Открытый на редактирование маршрут.</summary>
    [BindProperty(SupportsGet = true)] public int? Id { get; set; }
    public Route? Current => Routes.FirstOrDefault(r => r.Id == Id);

    public async Task OnGetAsync()
    {
        Routes = await db.Routes.Include(r => r.Car).Include(r => r.Stops).OrderBy(r => r.Name).ToListAsync();
        Cars = await db.Cars.OrderBy(c => c.Model).ToListAsync();
        var ms = await db.Memberships.Where(m => m.Status != MembershipStatus.Left).ToListAsync();
        Counts = Routes.ToDictionary(r => r.Id, r => (
            ms.Count(m => m.RouteId == r.Id && m.Status == MembershipStatus.Regular),
            ms.Count(m => m.RouteId == r.Id && m.Status == MembershipStatus.Waitlist)));
    }

    public async Task<IActionResult> OnPostSaveRouteAsync(int? routeId, string name, int carId, decimal price, bool isActive)
    {
        var route = routeId is null ? new Route() : await db.Routes.SingleAsync(r => r.Id == routeId);
        route.Name = name.Trim();
        route.CarId = carId;
        route.PricePerRide = price;
        route.IsActive = routeId is null || isActive;
        if (routeId is null) db.Routes.Add(route);
        await db.SaveChangesAsync();
        await audit.LogAsync("Route", route.Id, routeId is null ? "Create" : "Update", $"{name} car={carId} price={price} active={route.IsActive}", Actor);
        return Done("Маршрут сохранён.", new { id = route.Id });
    }

    public async Task<IActionResult> OnPostSaveStopAsync(int routeId, int? stopId, int order, string name, TimeOnly morningTime, TimeOnly eveningTime, string? note)
    {
        var stop = stopId is null ? new Stop { RouteId = routeId } : await db.Stops.SingleAsync(s => s.Id == stopId && s.RouteId == routeId);
        var orderChanged = stopId is null || stop.Order != order;
        stop.Order = order;
        stop.Name = name.Trim();
        stop.MorningTime = morningTime;
        stop.EveningTime = eveningTime;
        stop.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (stopId is null) db.Stops.Add(stop);
        await db.SaveChangesAsync();
        // Порядок остановок поменялся — построенная по дорогам линия больше не подходит.
        if (orderChanged) await maps.InvalidatePathAsync(routeId);
        await audit.LogAsync("Stop", stop.Id, stopId is null ? "Create" : "Update", $"route={routeId} {order}. {name} {morningTime}/{eveningTime}", Actor);
        return Done("Остановка сохранена.", new { id = routeId });
    }

    public async Task<IActionResult> OnPostDeleteStopAsync(int routeId, int stopId)
    {
        var used = await db.Memberships.AnyAsync(m => m.StopId == stopId && m.Status != MembershipStatus.Left)
                   || await db.OneOffBookings.AnyAsync(b => b.StopId == stopId);
        if (used) return Done(OpResult.Fail("На остановку уже записаны пассажиры — сначала переведите их на другую."), new { id = routeId });
        db.Stops.Remove(await db.Stops.SingleAsync(s => s.Id == stopId && s.RouteId == routeId));
        await db.SaveChangesAsync();
        await maps.InvalidatePathAsync(routeId);
        await audit.LogAsync("Stop", stopId, "Delete", null, Actor);
        return Done("Остановка удалена.", new { id = routeId });
    }

    // ───── карта (вызываются из JS на странице, отвечают JSON) ─────

    /// <summary>Координаты приходят строками с точкой — так не зависим от региональных настроек сервера.</summary>
    public async Task<IActionResult> OnPostStopLocationAsync(int routeId, int stopId, string? lat, string? lng)
    {
        var r = await maps.SetStopLocationAsync(routeId, stopId, ParseCoord(lat), ParseCoord(lng), Actor);
        return new JsonResult(new { ok = r.Ok, message = r.Message });
    }

    public async Task<IActionResult> OnPostBuildPathAsync(int routeId)
    {
        var r = await maps.BuildPathAsync(routeId, Actor, HttpContext.RequestAborted);
        return new JsonResult(new { ok = r.Ok, message = r.Message, path = await PathAsync(routeId) });
    }

    public async Task<IActionResult> OnPostClearPathAsync(int routeId)
    {
        var r = await maps.ClearPathAsync(routeId, Actor);
        return new JsonResult(new { ok = r.Ok, message = r.Message, path = await PathAsync(routeId) });
    }

    private async Task<List<double[]>> PathAsync(int routeId) =>
        RouteMapService.ParsePath(await db.Routes.AsNoTracking().Where(r => r.Id == routeId).Select(r => r.PathJson).FirstOrDefaultAsync());

    private static double? ParseCoord(string? s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
}
