using GoHome.Web.Data;
using GoHome.Web.Domain;
using GoHome.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GoHome.Tests;

public class OsrmParseTests
{
    private const string Ok = """
        {"code":"Ok","routes":[{"distance":12345.6,"duration":1620.0,
         "geometry":{"type":"LineString","coordinates":[[74.500001,42.800001],[74.500004,42.800003],[74.55,42.85],[74.6,42.9]]}}]}
        """;

    [Fact]
    public void Parses_geometry_as_lat_lng_and_merges_near_duplicates()
    {
        var (path, error) = OsrmRoutingProvider.Parse(Ok);
        Assert.Null(error);
        Assert.Equal(12.3456, path!.DistanceKm, 3);
        Assert.Equal(27, path.DurationMin, 3);
        Assert.Equal([[42.8, 74.5], [42.85, 74.55], [42.9, 74.6]], path.Points);   // lat первым, точки ~1 м слиты
    }

    [Theory]
    [InlineData("""{"code":"NoRoute","routes":[]}""")]
    [InlineData("""{"code":"Ok","routes":[]}""")]
    [InlineData("not json")]
    [InlineData("""{"foo":1}""")]
    public void Failures_give_a_readable_error_instead_of_throwing(string json)
    {
        var (path, error) = OsrmRoutingProvider.Parse(json);
        Assert.Null(path);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}

public class RouteMapTests
{
    private static RouteMapService Maps(IServiceProvider sp) => sp.GetRequiredService<RouteMapService>();

    private static async Task<Route> LoadAsync(Harness h, int routeId) =>
        await h.RunAsync(sp => sp.GetRequiredService<AppDbContext>().Routes.Include(r => r.Stops).SingleAsync(r => r.Id == routeId));

    [Fact]
    public async Task Stop_location_is_saved_rounded_and_can_be_cleared()
    {
        using var h = new Harness();
        var (route, stop) = await h.AddRouteAsync(seats: 3);

        Assert.True((await h.RunAsync(sp => Maps(sp).SetStopLocationAsync(route.Id, stop.Id, 42.874612345, 74.569812345, "test"))).Ok);
        var saved = (await LoadAsync(h, route.Id)).Stops.Single(s => s.Id == stop.Id);
        Assert.Equal(42.874612, saved.Lat);
        Assert.Equal(74.569812, saved.Lng);
        Assert.True(saved.HasLocation);

        Assert.True((await h.RunAsync(sp => Maps(sp).SetStopLocationAsync(route.Id, stop.Id, null, null, "test"))).Ok);
        Assert.False((await LoadAsync(h, route.Id)).Stops.Single(s => s.Id == stop.Id).HasLocation);
    }

    [Theory]
    [InlineData(91.0, 74.5)]
    [InlineData(-91.0, 74.5)]
    [InlineData(42.8, 181.0)]
    [InlineData(42.8, null)]
    [InlineData(null, 74.5)]
    [InlineData(double.NaN, 74.5)]
    public async Task Invalid_coordinates_are_refused(double? lat, double? lng)
    {
        using var h = new Harness();
        var (route, stop) = await h.AddRouteAsync(seats: 3);
        Assert.False((await h.RunAsync(sp => Maps(sp).SetStopLocationAsync(route.Id, stop.Id, lat, lng, "test"))).Ok);
        Assert.False((await LoadAsync(h, route.Id)).Stops.Single(s => s.Id == stop.Id).HasLocation);
    }

    [Fact]
    public async Task Stop_of_another_route_cannot_be_moved()
    {
        using var h = new Harness();
        var (routeA, _) = await h.AddRouteAsync(seats: 3);
        var (_, stopB) = await h.AddRouteAsync(seats: 3);
        Assert.False((await h.RunAsync(sp => Maps(sp).SetStopLocationAsync(routeA.Id, stopB.Id, 42.8, 74.5, "test"))).Ok);
    }

    [Fact]
    public async Task Building_a_road_path_needs_two_located_stops_and_passes_them_in_order()
    {
        using var h = new Harness();
        var (route, first) = await h.AddRouteAsync(seats: 3);
        var second = (await LoadAsync(h, route.Id)).Stops.Single(s => s.Id != first.Id);

        Assert.False((await h.RunAsync(sp => Maps(sp).BuildPathAsync(route.Id, "test"))).Ok);
        Assert.Equal(0, h.Routing.Calls);

        // Ставим «в обратном порядке», чтобы проверить сортировку по номеру остановки.
        await h.RunAsync(sp => Maps(sp).SetStopLocationAsync(route.Id, second.Id, 42.90, 74.60, "test"));
        await h.RunAsync(sp => Maps(sp).SetStopLocationAsync(route.Id, first.Id, 42.80, 74.50, "test"));

        var result = await h.RunAsync(sp => Maps(sp).BuildPathAsync(route.Id, "test"));
        Assert.True(result.Ok);
        Assert.Contains("12.3", result.Message);
        Assert.Equal([(42.80, 74.50), (42.90, 74.60)], h.Routing.LastWaypoints);
        Assert.Equal(3, RouteMapService.ParsePath((await LoadAsync(h, route.Id)).PathJson).Count);
    }

    [Fact]
    public async Task Failed_build_keeps_the_previous_path_and_moving_a_stop_invalidates_it()
    {
        using var h = new Harness();
        var (route, first) = await h.AddRouteAsync(seats: 3);
        var second = (await LoadAsync(h, route.Id)).Stops.Single(s => s.Id != first.Id);
        await h.RunAsync(sp => Maps(sp).SetStopLocationAsync(route.Id, first.Id, 42.80, 74.50, "test"));
        await h.RunAsync(sp => Maps(sp).SetStopLocationAsync(route.Id, second.Id, 42.90, 74.60, "test"));
        await h.RunAsync(sp => Maps(sp).BuildPathAsync(route.Id, "test"));
        var before = (await LoadAsync(h, route.Id)).PathJson;
        Assert.NotNull(before);

        h.Routing.Result = (null, "Сервис недоступен");
        var failed = await h.RunAsync(sp => Maps(sp).BuildPathAsync(route.Id, "test"));
        Assert.False(failed.Ok);
        Assert.Equal(before, (await LoadAsync(h, route.Id)).PathJson);

        await h.RunAsync(sp => Maps(sp).SetStopLocationAsync(route.Id, first.Id, 42.81, 74.51, "test"));
        Assert.Null((await LoadAsync(h, route.Id)).PathJson);          // линия по дорогам устарела
    }

    [Fact]
    public async Task Line_falls_back_to_straight_segments_in_stop_order_and_prefers_saved_path()
    {
        using var h = new Harness();
        var (route, first) = await h.AddRouteAsync(seats: 3);
        var second = (await LoadAsync(h, route.Id)).Stops.Single(s => s.Id != first.Id);
        await h.RunAsync(sp => Maps(sp).SetStopLocationAsync(route.Id, second.Id, 42.90, 74.60, "test"));
        await h.RunAsync(sp => Maps(sp).SetStopLocationAsync(route.Id, first.Id, 42.80, 74.50, "test"));

        Assert.Equal([[42.80, 74.50], [42.90, 74.60]], RouteMapService.LineFor(await LoadAsync(h, route.Id)));

        await h.RunAsync(sp => Maps(sp).BuildPathAsync(route.Id, "test"));
        Assert.Equal(3, RouteMapService.LineFor(await LoadAsync(h, route.Id)).Count);

        await h.RunAsync(sp => Maps(sp).ClearPathAsync(route.Id, "test"));
        Assert.Equal(2, RouteMapService.LineFor(await LoadAsync(h, route.Id)).Count);
    }
}
