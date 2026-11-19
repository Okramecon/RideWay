using GoHome.Web.Domain;
using Microsoft.EntityFrameworkCore;

namespace GoHome.Web.Data;

/// <summary>
/// Демо-данные для локальной разработки (SeedDemoData=true): пилотный маршрут «Тунгуч → Офис» с пассажирами и ещё два маршрута для списка.
/// У демо-людей есть Telegram ID, чтобы открывать Mini App в браузере: /app/?dev=1001 (пассажир без записи), 1002–1004 (записаны), 2001 (водитель).
/// Координаты остановок приблизительные — только чтобы карта не была пустой; настоящие ставятся в админке («Маршруты»).
/// </summary>
public static class DemoSeed
{
    public static async Task RunAsync(AppDbContext db)
    {
        if (await db.Routes.AnyAsync()) return;

        var now = DateTime.UtcNow;
        (string Driver, string Phone, string Car, string Plate, string Route, (string Stop, string Morning, string Evening, double? Lat, double? Lng)[] Stops)[] data =
        [
            ("Водитель Тунгуч", "996700000001", "Toyota Camry", "01KG 001AAA", "Тунгуч → Офис",
            [
                ("Тунгуч, кольцо", "07:30", "19:10", 42.8530, 74.6720),
                ("Тунгуч, школа", "07:36", "19:04", 42.8560, 74.6630),
                ("Тунгуч, выезд", "07:42", "18:58", 42.8600, 74.6520),
                ("Офис", "08:10", "18:30", 42.8750, 74.6040),
            ]),
            ("Водитель 2", "996700000002", "Honda Fit", "01KG 002BBB", "Восток-5 → Офис",
                [("Восток-5", "07:30", "18:55", null, null), ("Жибек Жолу / Ден Сяопина", "07:42", "18:43", null, null), ("Офис", "08:00", "18:30", null, null)]),
            ("Водитель 3", "996700000003", "Hyundai Solaris", "01KG 003CCC", "Асанбай → Офис",
                [("Асанбай", "07:30", "18:55", null, null), ("Южная магистраль", "07:40", "18:45", null, null), ("Офис", "08:00", "18:30", null, null)]),
        ];

        var routes = new List<Route>();
        var n = 0;
        foreach (var d in data)
        {
            var driver = new User { Name = d.Driver, Phone = d.Phone, Role = UserRole.Driver, TelegramId = 2001 + n++, CreatedAt = now };
            var car = new Car { Model = d.Car, Plate = d.Plate, Seats = 4, Driver = driver };
            var route = new Route
            {
                Name = d.Route, Car = car, PricePerRide = 100, IsActive = true,
                Stops = d.Stops.Select((s, i) => new Stop
                {
                    Order = i + 1, Name = s.Stop, MorningTime = TimeOnly.Parse(s.Morning), EveningTime = TimeOnly.Parse(s.Evening), Lat = s.Lat, Lng = s.Lng,
                }).ToList(),
            };
            db.Routes.Add(route);
            routes.Add(route);
        }

        // Пассажиры: Айбек и Тимур без записи (проверить запись и очередь), трое уже ездят на пилотном маршруте.
        var names = new[] { "Айбек Токтосунов", "Асель Жумабекова", "Нурлан Асанов", "Диана Ким", "Тимур Садыков" };
        var people = names.Select((name, i) => new User { Name = name, Phone = $"99655500000{i + 1}", TelegramId = 1001 + i, CreatedAt = now }).ToList();
        db.Users.AddRange(people);
        await db.SaveChangesAsync();

        var pilot = routes[0];
        var from = DateOnly.FromDateTime(DateTime.Today.AddDays(-7));
        (int Person, int Stop, int Seat)[] regulars = [(1, 0, 1), (2, 1, 3), (3, 1, 4)];
        foreach (var r in regulars)
            db.Memberships.Add(new Membership
            {
                UserId = people[r.Person].Id, RouteId = pilot.Id, StopId = pilot.Stops[r.Stop].Id, Morning = true, Evening = true,
                SeatNumber = r.Seat, Status = MembershipStatus.Regular, From = from, CreatedAt = now,
            });
        await db.SaveChangesAsync();
    }
}
