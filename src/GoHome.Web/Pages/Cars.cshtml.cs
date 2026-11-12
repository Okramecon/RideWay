using GoHome.Web.Data;
using GoHome.Web.Domain;
using GoHome.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GoHome.Web.Pages;

public class CarsModel(AppDbContext db, AuditService audit) : AdminPageModel
{
    public List<Car> Cars { get; private set; } = [];
    public List<User> Drivers { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Cars = await db.Cars.Include(c => c.Driver).OrderBy(c => c.Model).ToListAsync();
        Drivers = await db.Users.Where(u => u.Role != UserRole.Passenger).OrderBy(u => u.Name).ToListAsync();
    }

    public async Task<IActionResult> OnPostSaveAsync(int? id, string model, string plate, int seats, int? driverUserId)
    {
        if (seats is < 1 or > 50) return Done(OpResult.Fail("Количество мест: от 1 до 50."));
        var car = id is null ? new Car() : await db.Cars.SingleAsync(c => c.Id == id);
        if (id is not null && seats < car.Seats)
        {
            // Нельзя уменьшить вместимость ниже числа постоянных пассажиров.
            var regulars = await db.Memberships.Where(m => m.Route.CarId == car.Id && m.Status == MembershipStatus.Regular).ToListAsync();
            if (regulars.Count(m => m.Morning) > seats || regulars.Count(m => m.Evening) > seats)
                return Done(OpResult.Fail("Постоянных пассажиров больше, чем новое число мест."));
        }
        car.Model = model.Trim();
        car.Plate = plate.Trim();
        car.Seats = seats;
        car.DriverUserId = driverUserId;
        if (id is null) db.Cars.Add(car);
        await db.SaveChangesAsync();
        await audit.LogAsync("Car", car.Id, id is null ? "Create" : "Update", $"{model} {plate} seats={seats} driver={driverUserId}", Actor);
        return Done("Машина сохранена.");
    }
}
