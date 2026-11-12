using GoHome.Web.Data;
using GoHome.Web.Domain;
using Microsoft.EntityFrameworkCore;

namespace GoHome.Web.Pages;

public class AuditModel(AppDbContext db) : AdminPageModel
{
    public List<AuditLog> Items { get; private set; } = [];

    public async Task OnGetAsync(string? entity) =>
        Items = await db.AuditLogs.Where(a => entity == null || a.Entity == entity)
            .OrderByDescending(a => a.Id).Take(300).ToListAsync();
}
