using GoHome.Web.Data;
using GoHome.Web.Domain;
using GoHome.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GoHome.Web.Pages;

public record DriverRow(User User, Car? Car, string? RouteName);

public class DriversModel(AppDbContext db, AuditService audit, IClock clock) : AdminPageModel
{
    public List<DriverRow> Rows { get; private set; } = [];

    public async Task OnGetAsync()
    {
        var users = await db.Users.Where(u => u.Role != UserRole.Passenger).OrderBy(u => u.Name).ToListAsync();
        var cars = await db.Cars.ToListAsync();
        var routes = await db.Routes.ToListAsync();
        Rows = users.Select(u =>
        {
            var car = cars.FirstOrDefault(c => c.DriverUserId == u.Id);
            return new DriverRow(u, car, car is null ? null : routes.FirstOrDefault(r => r.CarId == car.Id)?.Name);
        }).ToList();
    }

    public async Task<IActionResult> OnPostCreateAsync(string? name, string? phone, UserRole role)
    {
        if (string.IsNullOrWhiteSpace(name)) return Done(OpResult.Fail("Укажите имя."));
        if (role == UserRole.Passenger) role = UserRole.Driver;
        var normalized = Phone.Normalize(phone);
        if (normalized.Length < 9) return Done(OpResult.Fail("Некорректный номер телефона."));
        if (await db.Users.AnyAsync(u => u.Phone == normalized)) return Done(OpResult.Fail("Пользователь с таким номером уже есть."));
        var user = new User { Name = name.Trim(), Phone = normalized, Role = role, CreatedAt = clock.UtcNow };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        await audit.LogAsync("User", user.Id, "Create", $"{name} {normalized} {role}", Actor);
        return Done($"Добавлен {user.Name}. При входе в бот по этому номеру ему откроется вкладка «Водитель».");
    }

    public async Task<IActionResult> OnPostRoleAsync(int userId, UserRole role)
    {
        var user = await db.Users.SingleAsync(u => u.Id == userId);
        if (role == UserRole.Passenger && await db.Cars.AnyAsync(c => c.DriverUserId == userId))
            return Done(OpResult.Fail($"{user.Name} закреплён за машиной — сначала снимите его на странице «Машины»."));
        var old = user.Role;
        user.Role = role;
        await db.SaveChangesAsync();
        await audit.LogAsync("User", user.Id, "Role", $"{old} → {role}", Actor);
        return Done(role == UserRole.Passenger ? $"{user.Name} теперь пассажир — он в разделе «Пассажиры»." : $"{user.Name}: роль изменена.");
    }
}
