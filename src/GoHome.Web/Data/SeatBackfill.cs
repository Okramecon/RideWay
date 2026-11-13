using GoHome.Web.Domain;
using Microsoft.EntityFrameworkCore;

namespace GoHome.Web.Data;

/// <summary>
/// Данные, созданные до появления выбора мест: выдаём схемы салонов и закрепляем места
/// за постоянными пассажирами и будущими разовыми бронями. Безопасно запускать при каждом старте.
/// </summary>
public static class SeatBackfill
{
    public static async Task RunAsync(AppDbContext db, DateOnly today)
    {
        foreach (var car in await db.Cars.Where(c => c.Layout == "").ToListAsync())
            car.Layout = SeatLayout.Default(car.Seats).ToString();
        await db.SaveChangesAsync();

        var cars = await db.Cars.ToDictionaryAsync(c => c.Id);
        foreach (var route in await db.Routes.ToListAsync())
        {
            var layout = cars[route.CarId].ParsedLayout();

            var regulars = await db.Memberships
                .Where(m => m.RouteId == route.Id && m.Status == MembershipStatus.Regular && (m.To == null || m.To >= today))
                .OrderBy(m => m.Id).ToListAsync();
            foreach (var m in regulars.Where(m => m.SeatNumber is null).ToList())
            {
                var taken = regulars
                    .Where(o => o != m && o.SeatNumber is not null && ((o.Morning && m.Morning) || (o.Evening && m.Evening)))
                    .Select(o => o.SeatNumber!.Value).ToHashSet();
                var free = layout.SeatNumbers.Where(n => !taken.Contains(n)).ToList();
                if (free.Count > 0) m.SeatNumber = free.Min();
            }

            var oneOffs = await db.OneOffBookings
                .Where(b => b.RouteId == route.Id && b.Status == BookingStatus.Booked && b.Date >= today)
                .OrderBy(b => b.Id).ToListAsync();
            foreach (var b in oneOffs.Where(b => b.SeatNumber is null).ToList())
            {
                var taken = regulars.Where(r => r.SeatNumber is not null && r.Covers(b.Direction)).Select(r => r.SeatNumber!.Value)
                    .Concat(oneOffs.Where(o => o != b && o.SeatNumber is not null && o.Date == b.Date && o.Direction == b.Direction)
                        .Select(o => o.SeatNumber!.Value))
                    .ToHashSet();
                var free = layout.SeatNumbers.Where(n => !taken.Contains(n)).ToList();
                if (free.Count > 0) b.SeatNumber = free.Min();
            }
        }
        await db.SaveChangesAsync();
    }
}
