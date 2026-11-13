using GoHome.Web.Data;
using GoHome.Web.Domain;
using GoHome.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GoHome.Web.Pages;

public class PassengersModel(AppDbContext db, BookingService booking, AuditService audit, IClock clock) : AdminPageModel
{
    public List<(User User, Membership? Membership)> Rows { get; private set; } = [];
    public List<Stop> Stops { get; private set; } = [];

    public async Task OnGetAsync()
    {
        var users = await db.Users.Where(u => u.Role == UserRole.Passenger).OrderBy(u => u.Name).ToListAsync();
        var memberships = await db.Memberships.Include(m => m.Route).Include(m => m.Stop)
            .Where(m => m.Status != MembershipStatus.Left).ToListAsync();
        Rows = users.Select(u => (u, memberships.FirstOrDefault(m => m.UserId == u.Id))).ToList();
        Stops = await db.Stops.Include(s => s.Route).Where(s => s.Route.IsActive)
            .OrderBy(s => s.Route.Name).ThenBy(s => s.Order).ToListAsync();
    }

    public async Task<IActionResult> OnPostCreateAsync(string? name, string? phone)
    {
        if (string.IsNullOrWhiteSpace(name)) return Done(OpResult.Fail("Укажите имя."));
        var normalized = Phone.Normalize(phone);
        if (normalized.Length < 9) return Done(OpResult.Fail("Некорректный номер телефона."));
        if (await db.Users.AnyAsync(u => u.Phone == normalized)) return Done(OpResult.Fail("Пользователь с таким номером уже есть."));
        var user = new User { Name = name.Trim(), Phone = normalized, Role = UserRole.Passenger, CreatedAt = clock.UtcNow };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        await audit.LogAsync("User", user.Id, "Create", $"{name} {normalized} {user.Role}", Actor);
        return Done($"Добавлен {user.Name}. Он(а) войдёт в бота, поделившись этим номером.");
    }

    public async Task<IActionResult> OnPostMakeDriverAsync(int userId)
    {
        var user = await db.Users.SingleAsync(u => u.Id == userId);
        await booking.LeaveRouteAsync(userId, Actor);       // водитель не ездит пассажиром — освобождаем его место
        user.Role = UserRole.Driver;
        await db.SaveChangesAsync();
        await audit.LogAsync("User", user.Id, "Role", $"Passenger → {user.Role}", Actor);
        return Done($"{user.Name} теперь водитель — он в разделе «Водители».");
    }

    /// <summary>Записать на маршрут вручную (например, кто не пользуется ботом). Место — необязательно, иначе первое свободное.</summary>
    public async Task<IActionResult> OnPostJoinAsync(int userId, string routeStop, string dirs, int? seat)
    {
        var parts = routeStop.Split(':');
        return Done(await booking.JoinRouteAsync(userId, int.Parse(parts[0]), int.Parse(parts[1]),
            dirs is "both" or "morning", dirs is "both" or "evening", Actor, seat));
    }

    public async Task<IActionResult> OnPostPromoteAsync(int membershipId, int? seat) =>
        Done(await booking.PromoteAsync(membershipId, Actor, seat));

    public async Task<IActionResult> OnPostLeaveAsync(int userId) =>
        Done(await booking.LeaveRouteAsync(userId, Actor));
}
