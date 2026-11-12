using GoHome.Web.App;
using GoHome.Web.Data;
using GoHome.Web.Domain;
using GoHome.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QRCoder;

namespace GoHome.Web.Pages;

public class CarsModel(AppDbContext db, AuditService audit, BotInfo bot) : AdminPageModel
{
    public List<Car> Cars { get; private set; } = [];
    public List<User> Drivers { get; private set; } = [];
    public string? BotUsername => bot.Username;

    public async Task OnGetAsync()
    {
        Cars = await db.Cars.Include(c => c.Driver).OrderBy(c => c.Model).ToListAsync();
        Drivers = await db.Users.Where(u => u.Role != UserRole.Passenger).OrderBy(u => u.Name).ToListAsync();
    }

    public async Task<IActionResult> OnPostSaveAsync(int? id, string model, string plate, int seats, int? driverUserId)
    {
        if (seats is < 1 or > 50) return Done(OpResult.Fail("Количество мест: от 1 до 50."));
        var car = id is null ? new Car { BoardingCode = Car.NewBoardingCode() } : await db.Cars.SingleAsync(c => c.Id == id);
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

    /// <summary>Новый код — старая наклейка перестаёт работать (если QR сфотографировали и отмечаются не из машины).</summary>
    public async Task<IActionResult> OnPostRegenerateQrAsync(int id)
    {
        var car = await db.Cars.SingleAsync(c => c.Id == id);
        car.BoardingCode = Car.NewBoardingCode();
        await db.SaveChangesAsync();
        await audit.LogAsync("Car", car.Id, "RegenerateQr", null, Actor);
        return Done($"QR для {car.Plate} перевыпущен — распечатайте и замените наклейку.");
    }

    /// <summary>PNG с QR: ссылка t.me/&lt;бот&gt;?start=board_&lt;код&gt; — открывается обычной камерой телефона.</summary>
    public async Task<IActionResult> OnGetQrAsync(int id)
    {
        var car = await db.Cars.SingleAsync(c => c.Id == id);
        var png = PngByteQRCodeHelper.GetQRCode(BoardingLink(car.BoardingCode), QRCodeGenerator.ECCLevel.M, 12);
        return File(png, "image/png");
    }

    public string BoardingLink(string code) =>
        bot.Username is { } name ? $"https://t.me/{name}?start=board_{code}" : $"board_{code}";
}
