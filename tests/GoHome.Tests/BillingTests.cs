using GoHome.Web.Domain;
using GoHome.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace GoHome.Tests;

public class BillingTests
{
    [Theory]
    [InlineData(false, 100)]
    [InlineData(true, 200)]
    public async Task Charges_boarded_rides_and_noshows_by_setting(bool chargeNoShow, decimal expected)
    {
        using var h = new Harness(chargeNoShow);
        var (route, stop) = await h.AddRouteAsync(seats: 3, price: 100);
        var a = await h.AddUserAsync("A");
        await h.RunAsync(sp => sp.GetRequiredService<BookingService>().JoinRouteAsync(a, route.Id, stop.Id, true, true, "test"));

        h.Clock.LocalNow = h.Tuesday.ToDateTime(new TimeOnly(19, 0));
        await h.RunAsync(sp => sp.GetRequiredService<TripOpsService>().MarkRideAsync(route.Id, h.Tuesday, Direction.Morning, a, RideResult.Boarded, null, "test"));
        await h.RunAsync(sp => sp.GetRequiredService<TripOpsService>().MarkRideAsync(route.Id, h.Tuesday, Direction.Evening, a, RideResult.NoShow, null, "test"));

        var balance = await h.RunAsync(sp => sp.GetRequiredService<BillingService>().GetBalanceAsync(a));
        Assert.Equal(expected, balance.Charged);
        Assert.Equal(1, balance.Rides);
        Assert.Equal(1, balance.NoShows);

        await h.RunAsync(sp => sp.GetRequiredService<BillingService>().RecordPaymentAsync(a, 100, PaymentMethod.Cash, "Водитель", null, "test"));
        balance = await h.RunAsync(sp => sp.GetRequiredService<BillingService>().GetBalanceAsync(a));
        Assert.Equal(expected - 100, balance.Due);
    }

    [Fact]
    public async Task Remarking_a_ride_overwrites_instead_of_double_charging()
    {
        using var h = new Harness();
        var (route, stop) = await h.AddRouteAsync(seats: 3);
        var a = await h.AddUserAsync("A");
        await h.RunAsync(sp => sp.GetRequiredService<BookingService>().JoinRouteAsync(a, route.Id, stop.Id, true, true, "test"));

        // Отметить посадку на будущий рейс нельзя.
        Assert.False((await h.RunAsync(sp => sp.GetRequiredService<TripOpsService>()
            .MarkRideAsync(route.Id, h.Tuesday, Direction.Morning, a, RideResult.Boarded, null, "test"))).Ok);

        h.Clock.LocalNow = h.Tuesday.ToDateTime(new TimeOnly(8, 0));
        for (var i = 0; i < 3; i++)
            await h.RunAsync(sp => sp.GetRequiredService<TripOpsService>().MarkRideAsync(route.Id, h.Tuesday, Direction.Morning, a, RideResult.Boarded, null, "test"));

        var balance = await h.RunAsync(sp => sp.GetRequiredService<BillingService>().GetBalanceAsync(a));
        Assert.Equal(100, balance.Charged);
    }

    [Theory]
    [InlineData("0555 12-34-56", "996555123456")]
    [InlineData("+996 555 123 456", "996555123456")]
    [InlineData("555123456", "996555123456")]
    public void Phone_is_normalized(string raw, string expected) => Assert.Equal(expected, Phone.Normalize(raw));
}
