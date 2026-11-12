using GoHome.Web.Domain;
using Microsoft.EntityFrameworkCore;

namespace GoHome.Web.Data;

/// <summary>Демо-данные для локальной разработки (SeedDemoData=true): 3 машины, 3 маршрута.</summary>
public static class DemoSeed
{
    public static async Task RunAsync(AppDbContext db)
    {
        if (await db.Routes.AnyAsync()) return;

        var now = DateTime.UtcNow;
        (string Driver, string Phone, string Car, string Plate, string Route, (string Stop, string Morning, string Evening)[] Stops)[] data =
        [
            ("Водитель 1", "996700000001", "Toyota Camry", "01KG 001AAA", "Джал → Офис",
                [("Джал, ТЦ", "07:30", "18:55"), ("Ахунбаева / Мира", "07:40", "18:45"), ("Офис", "08:00", "18:30")]),
            ("Водитель 2", "996700000002", "Honda Fit", "01KG 002BBB", "Восток-5 → Офис",
                [("Восток-5", "07:30", "18:55"), ("Жибек Жолу / Ден Сяопина", "07:42", "18:43"), ("Офис", "08:00", "18:30")]),
            ("Водитель 3", "996700000003", "Hyundai Solaris", "01KG 003CCC", "Асанбай → Офис",
                [("Асанбай", "07:30", "18:55"), ("Южная магистраль", "07:40", "18:45"), ("Офис", "08:00", "18:30")]),
        ];

        foreach (var d in data)
        {
            var driver = new User { Name = d.Driver, Phone = d.Phone, Role = UserRole.Driver, CreatedAt = now };
            var car = new Car { Model = d.Car, Plate = d.Plate, Seats = 4, Driver = driver };
            var route = new Route
            {
                Name = d.Route, Car = car, PricePerRide = 100, IsActive = true,
                Stops = d.Stops.Select((s, i) => new Stop
                {
                    Order = i + 1, Name = s.Stop, MorningTime = TimeOnly.Parse(s.Morning), EveningTime = TimeOnly.Parse(s.Evening),
                }).ToList(),
            };
            db.Routes.Add(route);
        }
        await db.SaveChangesAsync();
    }
}
