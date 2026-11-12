using GoHome.Web.Data;
using GoHome.Web.Domain;
using GoHome.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GoHome.Web.Pages;

public class BillingModel(BillingService billing, AppDbContext db) : AdminPageModel
{
    public List<UserBalance> Balances { get; private set; } = [];
    public List<Payment> RecentPayments { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Balances = (await billing.GetAllAsync()).Where(b => b.Charged > 0 || b.Paid > 0 || b.User.Role == UserRole.Passenger)
            .OrderByDescending(b => b.Due).ToList();
        RecentPayments = await db.Payments.Include(p => p.User).OrderByDescending(p => p.PaidAt).Take(30).ToListAsync();
    }

    public async Task<IActionResult> OnPostPayAsync(int userId, decimal amount, PaymentMethod method, string? receivedBy, string? note) =>
        Done(await billing.RecordPaymentAsync(userId, amount, method, receivedBy?.Trim() ?? "", note, Actor));
}
