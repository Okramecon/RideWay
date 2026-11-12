using GoHome.Web.Data;
using GoHome.Web.Domain;
using Microsoft.EntityFrameworkCore;

namespace GoHome.Web.Services;

public record UserBalance(User User, int Rides, int NoShows, decimal Charged, decimal Paid)
{
    public decimal Due => Charged - Paid;
}

/// <summary>Сумма к оплате = стоимость отмеченных поездок − платежи. Отдельного баланса не храним.</summary>
public class BillingService(AppDbContext db, IClock clock, AuditService audit)
{
    public async Task<UserBalance> GetBalanceAsync(int userId)
    {
        var user = await db.Users.SingleAsync(u => u.Id == userId);
        return await BuildAsync(user);
    }

    public async Task<List<UserBalance>> GetAllAsync()
    {
        var users = await db.Users.Where(u => u.Role == UserRole.Passenger || db.RideLogs.Any(l => l.UserId == u.Id))
            .OrderBy(u => u.Name).ToListAsync();
        var list = new List<UserBalance>();
        foreach (var u in users) list.Add(await BuildAsync(u));
        return list;
    }

    public async Task<List<RideLog>> GetRidesAsync(int userId, DateOnly from, DateOnly to) =>
        await db.RideLogs.Where(l => l.UserId == userId && l.Date >= from && l.Date <= to)
            .OrderBy(l => l.Date).ThenBy(l => l.Direction).ToListAsync();

    public async Task<OpResult> RecordPaymentAsync(int userId, decimal amount, PaymentMethod method, string receivedBy, string? note, string actor)
    {
        if (amount <= 0) return OpResult.Fail("Сумма должна быть больше нуля.");
        var p = new Payment
        {
            UserId = userId, Amount = amount, Method = method, ReceivedBy = receivedBy, Note = note, PaidAt = clock.UtcNow,
        };
        db.Payments.Add(p);
        await db.SaveChangesAsync();
        await audit.LogAsync("Payment", p.Id, "Paid", $"user={userId} amount={amount} method={method} to={receivedBy}", actor);
        return OpResult.Success($"Оплата {amount:0} сом записана.");
    }

    private async Task<UserBalance> BuildAsync(User user)
    {
        var logs = await db.RideLogs.Where(l => l.UserId == user.Id).ToListAsync();
        var paid = (await db.Payments.Where(p => p.UserId == user.Id).Select(p => p.Amount).ToListAsync()).Sum();
        return new UserBalance(user,
            logs.Count(l => l.Result == RideResult.Boarded),
            logs.Count(l => l.Result == RideResult.NoShow),
            logs.Sum(l => l.Price),
            paid);
    }
}
