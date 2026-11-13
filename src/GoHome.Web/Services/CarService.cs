using System.Text.RegularExpressions;
using GoHome.Web.Data;
using GoHome.Web.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GoHome.Web.Services;

public class StorageOptions
{
    public const string Section = "Storage";
    /// <summary>Папка для загруженных фото салона (в Docker — том /data/uploads).</summary>
    public string Path { get; set; } = "uploads";
}

/// <summary>Схема мест салона и фото салона. Схему меняют водитель и админ.</summary>
public partial class CarService(AppDbContext db, AuditService audit, IClock clock, IOptions<StorageOptions> storage)
{
    public const int MaxPhotoBytes = 6 * 1024 * 1024;

    [GeneratedRegex("^[0-9a-f]{32}\\.(jpg|png|webp)$")]
    private static partial Regex MediaNameRegex();

    public static bool IsValidMediaName(string name) => MediaNameRegex().IsMatch(name);

    /// <summary>Ссылка из QR: открывает бота и отмечает посадку. Без имени бота — только код (сработает из приложения, но не с камеры).</summary>
    public static string BoardingLink(string? botUsername, string code) =>
        botUsername is { Length: > 0 } name ? $"https://t.me/{name}?start=board_{code}" : $"board_{code}";

    public static string QrDataUri(string content) =>
        "data:image/png;base64," + Convert.ToBase64String(
            QRCoder.PngByteQRCodeHelper.GetQRCode(content, QRCoder.QRCodeGenerator.ECCLevel.M, 10));

    public static string? PhotoUrl(Car car) => car.CabinPhoto is { } f ? $"/media/{f}" : null;

    public string? MediaPath(string name) =>
        IsValidMediaName(name) && File.Exists(Path.Combine(storage.Value.Path, name)) ? Path.Combine(storage.Value.Path, name) : null;

    /// <summary>
    /// Сохранить схему. Номера мест закреплены за сиденьями, поэтому нельзя убрать место,
    /// на котором сидит постоянный пассажир или есть будущая разовая бронь.
    /// </summary>
    public async Task<OpResult> SaveLayoutAsync(int carId, string layoutText, string actor)
    {
        if (!SeatLayout.TryParse(layoutText, out var layout, out var error)) return OpResult.Fail(error);
        var car = await db.Cars.SingleOrDefaultAsync(c => c.Id == carId);
        if (car is null) return OpResult.Fail("Машина не найдена.");

        var today = clock.Today;
        var routeIds = await db.Routes.Where(r => r.CarId == carId).Select(r => r.Id).ToListAsync();
        var regulars = await db.Memberships
            .Where(m => routeIds.Contains(m.RouteId) && m.Status == MembershipStatus.Regular && (m.To == null || m.To >= today))
            .Select(m => m.SeatNumber).ToListAsync();
        var oneOffs = await db.OneOffBookings
            .Where(b => routeIds.Contains(b.RouteId) && b.Status == BookingStatus.Booked && b.Date >= today)
            .Select(b => b.SeatNumber).ToListAsync();

        var missing = regulars.Concat(oneOffs).Where(n => n is not null).Select(n => n!.Value).Distinct()
            .Where(n => !layout!.HasSeat(n)).OrderBy(n => n).ToList();
        if (missing.Count > 0)
            return OpResult.Fail($"Нельзя убрать места {string.Join(", ", missing)}: на них записаны пассажиры.");

        car.Layout = layout!.ToString();
        car.Seats = layout.SeatCount;
        await db.SaveChangesAsync();
        await audit.LogAsync("Car", car.Id, "Layout", $"{car.Layout} seats={car.Seats}", actor);
        return OpResult.Success($"Схема сохранена: мест {car.Seats}.");
    }

    public async Task<OpResult> SavePhotoAsync(int carId, Stream data, string actor)
    {
        var car = await db.Cars.SingleOrDefaultAsync(c => c.Id == carId);
        if (car is null) return OpResult.Fail("Машина не найдена.");

        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await data.ReadAsync(buffer)) > 0)
        {
            ms.Write(buffer, 0, read);
            if (ms.Length > MaxPhotoBytes) return OpResult.Fail("Фото слишком большое (до 6 МБ).");
        }
        var bytes = ms.ToArray();

        var ext = DetectExtension(bytes);
        if (ext is null) return OpResult.Fail("Нужна фотография в формате JPEG, PNG или WebP.");

        Directory.CreateDirectory(storage.Value.Path);
        var name = $"{Guid.NewGuid():N}.{ext}";
        await File.WriteAllBytesAsync(Path.Combine(storage.Value.Path, name), bytes);

        DeleteFile(car.CabinPhoto);
        car.CabinPhoto = name;
        await db.SaveChangesAsync();
        await audit.LogAsync("Car", car.Id, "Photo", name, actor);
        return OpResult.Success("Фото салона сохранено.");
    }

    public async Task<OpResult> RemovePhotoAsync(int carId, string actor)
    {
        var car = await db.Cars.SingleOrDefaultAsync(c => c.Id == carId);
        if (car is null) return OpResult.Fail("Машина не найдена.");
        if (car.CabinPhoto is null) return OpResult.Fail("Фото нет.");
        DeleteFile(car.CabinPhoto);
        car.CabinPhoto = null;
        await db.SaveChangesAsync();
        await audit.LogAsync("Car", car.Id, "PhotoRemoved", null, actor);
        return OpResult.Success("Фото удалено.");
    }

    private void DeleteFile(string? name)
    {
        if (name is null || !IsValidMediaName(name)) return;
        try { File.Delete(Path.Combine(storage.Value.Path, name)); } catch (IOException) { }
    }

    private static string? DetectExtension(byte[] b)
    {
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "jpg";
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A) return "png";
        if (b.Length >= 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P') return "webp";
        return null;
    }
}
