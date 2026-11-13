using System.Globalization;
using System.Text.Json;
using GoHome.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace GoHome.Web.Services;

public record RoadPath(List<double[]> Points, double DistanceKm, double DurationMin);

/// <summary>Построение линии маршрута по дорогам. Сейчас — OSRM; провайдер заменяемый (2ГИС, Google, свой OSRM).</summary>
public interface IRoutingProvider
{
    Task<(RoadPath? Path, string? Error)> BuildAsync(IReadOnlyList<(double Lat, double Lng)> waypoints, CancellationToken ct = default);
}

/// <summary>
/// Публичный демо-сервер OSRM: бесплатно и без ключа, но «не для нагрузки» — для пилота (3 маршрута, линия строится один раз)
/// достаточно. Для рабочей эксплуатации поднимите свой OSRM или возьмите платный сервис и поменяйте BaseUrl.
/// </summary>
public class OsrmRoutingProvider(HttpClient http, Microsoft.Extensions.Options.IOptions<RoutingOptions> options) : IRoutingProvider
{
    public async Task<(RoadPath? Path, string? Error)> BuildAsync(IReadOnlyList<(double Lat, double Lng)> waypoints, CancellationToken ct = default)
    {
        var coords = string.Join(';', waypoints.Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.Lng:0.######},{p.Lat:0.######}")));
        try
        {
            using var res = await http.GetAsync($"{options.Value.BaseUrl.TrimEnd('/')}/route/v1/driving/{coords}?overview=full&geometries=geojson", ct);
            if (!res.IsSuccessStatusCode) return (null, $"Сервис маршрутов ответил ошибкой {(int)res.StatusCode}.");
            return Parse(await res.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (null, "Сервис маршрутов недоступен. Попробуйте позже.");
        }
    }

    /// <summary>Разбор ответа OSRM: geometry.coordinates — пары [lng, lat]; отдаём [lat, lng] с точностью ~1 м.</summary>
    public static (RoadPath? Path, string? Error) Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.GetProperty("code").GetString() != "Ok" || root.GetProperty("routes").GetArrayLength() == 0)
                return (null, "По этим точкам дорога не найдена — проверьте, что остановки стоят на улицах.");

            var route = root.GetProperty("routes")[0];
            var points = new List<double[]>();
            foreach (var c in route.GetProperty("geometry").GetProperty("coordinates").EnumerateArray())
            {
                var p = new[] { Math.Round(c[1].GetDouble(), 5), Math.Round(c[0].GetDouble(), 5) };
                if (points.Count == 0 || points[^1][0] != p[0] || points[^1][1] != p[1]) points.Add(p);
            }
            return points.Count < 2
                ? (null, "Сервис вернул пустую линию.")
                : (new RoadPath(points, route.GetProperty("distance").GetDouble() / 1000, route.GetProperty("duration").GetDouble() / 60), null);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return (null, "Не удалось разобрать ответ сервиса маршрутов.");
        }
    }
}

public class RoutingOptions
{
    public const string Section = "Routing";
    public string BaseUrl { get; set; } = "https://router.project-osrm.org";
}

/// <summary>Места остановок на карте и линия маршрута.</summary>
public class RouteMapService(AppDbContext db, IRoutingProvider routing, AuditService audit)
{
    /// <summary>Поставить остановку на карту. lat и lng оба null — убрать с карты.</summary>
    public async Task<OpResult> SetStopLocationAsync(int routeId, int stopId, double? lat, double? lng, string actor)
    {
        if ((lat is null) != (lng is null)) return OpResult.Fail("Нужны обе координаты.");
        if (lat is not null && (lat is < -90 or > 90 || double.IsNaN(lat.Value) || lng is < -180 or > 180 || double.IsNaN(lng!.Value)))
            return OpResult.Fail("Координаты вне допустимых пределов.");

        var stop = await db.Stops.SingleOrDefaultAsync(s => s.Id == stopId && s.RouteId == routeId);
        if (stop is null) return OpResult.Fail("Остановка не найдена.");

        stop.Lat = lat is null ? null : Math.Round(lat.Value, 6);
        stop.Lng = lng is null ? null : Math.Round(lng.Value, 6);
        await InvalidatePathAsync(routeId, save: false);
        await db.SaveChangesAsync();
        await audit.LogAsync("Stop", stop.Id, lat is null ? "LocationCleared" : "Location", lat is null ? null : $"{stop.Lat},{stop.Lng}", actor);
        return OpResult.Success(lat is null ? $"«{stop.Name}» убрана с карты." : $"«{stop.Name}» поставлена на карту.");
    }

    /// <summary>Остановки поменялись — построенная по дорогам линия устарела, возвращаемся к прямым между точками.</summary>
    public async Task InvalidatePathAsync(int routeId, bool save = true)
    {
        var route = await db.Routes.SingleOrDefaultAsync(r => r.Id == routeId);
        if (route?.PathJson is null) return;
        route.PathJson = null;
        if (save) await db.SaveChangesAsync();
    }

    public async Task<OpResult> BuildPathAsync(int routeId, string actor, CancellationToken ct = default)
    {
        var route = await db.Routes.Include(r => r.Stops).SingleOrDefaultAsync(r => r.Id == routeId, ct);
        if (route is null) return OpResult.Fail("Маршрут не найден.");

        var located = route.Stops.Where(s => s.HasLocation).OrderBy(s => s.Order).ToList();
        if (located.Count < 2) return OpResult.Fail("Поставьте на карте хотя бы две остановки.");

        var (path, error) = await routing.BuildAsync(located.Select(s => (s.Lat!.Value, s.Lng!.Value)).ToList(), ct);
        if (path is null) return OpResult.Fail(error ?? "Не удалось построить маршрут.");

        route.PathJson = JsonSerializer.Serialize(path.Points);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("Route", route.Id, "PathBuilt", $"{path.DistanceKm:0.0} km, {path.Points.Count} points", actor);

        var skipped = route.Stops.Count - located.Count;
        return OpResult.Success($"Линия по дорогам построена: {path.DistanceKm:0.0} км, около {path.DurationMin:0} мин без пробок."
                                + (skipped > 0 ? $" Остановок без места на карте пропущено: {skipped}." : ""));
    }

    public async Task<OpResult> ClearPathAsync(int routeId, string actor)
    {
        var route = await db.Routes.SingleOrDefaultAsync(r => r.Id == routeId);
        if (route is null) return OpResult.Fail("Маршрут не найден.");
        route.PathJson = null;
        await db.SaveChangesAsync();
        await audit.LogAsync("Route", route.Id, "PathCleared", null, actor);
        return OpResult.Success("Линия по дорогам убрана — рисуем прямые между остановками.");
    }

    /// <summary>Линия для карты: сохранённая по дорогам либо прямые через остановки с координатами.</summary>
    public static List<double[]> LineFor(Domain.Route route)
    {
        var saved = ParsePath(route.PathJson);
        if (saved.Count >= 2) return saved;
        return route.Stops.Where(s => s.HasLocation).OrderBy(s => s.Order).Select(s => new[] { s.Lat!.Value, s.Lng!.Value }).ToList();
    }

    public static List<double[]> ParsePath(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<double[]>>(json) ?? []; }
        catch (JsonException) { return []; }
    }
}
