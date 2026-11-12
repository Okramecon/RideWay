using GoHome.Web.Data;
using GoHome.Web.Domain;
using GoHome.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GoHome.Web.Pages;

public class UsersModel(AppDbContext db, BookingService booking, AuditService audit, IClock clock) : AdminPageModel
{
    public List<(User User, Membership? Membership)> Rows { get; private set; } = [];
    public List<Stop> Stops { get; private set; } = [];

    public async Task OnGetAsync()
    {
        var users = await db.Users.OrderBy(u => u.Role == UserRole.Passenger).ThenBy(u => u.Name).ToListAsync();
        var memberships = await db.Memberships.Include(m => m.Route).Include(m => m.Stop)
            .Where(m => m.Status != MembershipStatus.Left).ToListAsync();
        Rows = users.Select(u => (u, memberships.FirstOrDefault(m => m.UserId == u.Id))).ToList();
        Stops = await db.Stops.Include(s => s.Route).Where(s => s.Route.IsActive)
            .OrderBy(s => s.Route.Name).ThenBy(s => s.Order).ToListAsync();
    }

    public async Task<IActionResult> OnPostCreateAsync(string? name, string? phone, UserRole role)
    {
        if (string.IsNullOrWhiteSpace(name)) return Done(OpResult.Fail("Укажите имя."));
        var normalized = Phone.Normalize(phone);
        if (normalized.Length < 9) return Done(OpResult.Fail("Некорректный номер телефона."));
        if (await db.Users.AnyAsync(u => u.Phone == normalized)) return Done(OpResult.Fail("Пользователь с таким номером уже есть."));
        var user = new User { Name = name.Trim(), Phone = normalized, Role = role, CreatedAt = clock.UtcNow };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        await audit.LogAsync("User", user.Id, "Create", $"{name} {normalized} {role}", Actor);
        return Done($"Добавлен {user.Name}. Он(а) войдёт в бота, поделившись этим номером.");
    }

    public async Task<IActionResult> OnPostRoleAsync(int userId, UserRole role)
    {
        var user = await db.Users.SingleAsync(u => u.Id == userId);
        var old = user.Role;
        user.Role = role;
        await db.SaveChangesAsync();
        await audit.LogAsync("User", user.Id, "Role", $"{old} → {role}", Actor);
        return Done($"{user.Name}: роль изменена.");
    }

    /// <summary>Записать на маршрут вручную (например, кто не пользуется ботом).</summary>
    public async Task<IActionResult> OnPostJoinAsync(int userId, string routeStop, string dirs)
    {
        var parts = routeStop.Split(':');
        return Done(await booking.JoinRouteAsync(userId, int.Parse(parts[0]), int.Parse(parts[1]),
            dirs is "both" or "morning", dirs is "both" or "evening", Actor));
    }

    public async Task<IActionResult> OnPostPromoteAsync(int membershipId) =>
        Done(await booking.PromoteAsync(membershipId, Actor));

    public async Task<IActionResult> OnPostLeaveAsync(int userId) =>
        Done(await booking.LeaveRouteAsync(userId, Actor));
}
