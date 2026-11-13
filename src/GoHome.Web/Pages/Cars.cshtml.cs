using GoHome.Web.App;
using GoHome.Web.Data;
using GoHome.Web.Domain;
using GoHome.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QRCoder;

namespace GoHome.Web.Pages;

public class CarsModel(AppDbContext db, AuditService audit, CarService cars, BotInfo bot) : AdminPageModel
{
    public List<Car> Cars { get; private set; } = [];
    public List<User> Drivers { get; private set; } = [];
    public string? BotUsername => bot.Username;

    public async Task OnGetAsync()
    {
        Cars = await db.Cars.Include(c => c.Driver).OrderBy(c => c.Model).ToListAsync();
        Drivers = await db.Users.Where(u => u.Role != UserRole.Passenger).OrderBy(u => u.Name).ToListAsync();
    }

    /// <summary>Создать машину (число мест задаёт типовую схему) или изменить модель/номер/водителя. Схема мест правится отдельно.</summary>
    public async Task<IActionResult> OnPostSaveAsync(int? id, string model, string plate, int seats, int? driverUserId)
    {
        Car car;
        if (id is null)
        {
            if (seats is < 1 or > SeatLayout.MaxSeats) return Done(OpResult.Fail($"Количество мест: от 1 до {SeatLayout.MaxSeats}."));
            car = new Car { BoardingCode = Car.NewBoardingCode(), Seats = seats, Layout = SeatLayout.Default(seats).ToString() };
            db.Cars.Add(car);
        }
        else
        {
            car = await db.Cars.SingleAsync(c => c.Id == id);
        }
        car.Model = model.Trim();
        car.Plate = plate.Trim();
        car.DriverUserId = driverUserId;
        await db.SaveChangesAsync();
        await audit.LogAsync("Car", car.Id, id is null ? "Create" : "Update", $"{model} {plate} seats={car.Seats} driver={driverUserId}", Actor);
        return Done("Машина сохранена.");
    }

    public async Task<IActionResult> OnPostLayoutAsync(int id, string layout) =>
        Done(await cars.SaveLayoutAsync(id, layout, Actor));

    public async Task<IActionResult> OnPostPhotoAsync(int id, IFormFile? photo)
    {
        if (photo is null || photo.Length == 0) return Done(OpResult.Fail("Выберите файл с фото."));
        if (photo.Length > CarService.MaxPhotoBytes) return Done(OpResult.Fail("Фото слишком большое (до 6 МБ)."));
        await using var stream = photo.OpenReadStream();
        return Done(await cars.SavePhotoAsync(id, stream, Actor));
    }

    public async Task<IActionResult> OnPostRemovePhotoAsync(int id) =>
        Done(await cars.RemovePhotoAsync(id, Actor));

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

    public string BoardingLink(string code) => CarService.BoardingLink(bot.Username, code);
}
