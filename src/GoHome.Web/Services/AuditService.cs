using GoHome.Web.Data;
using GoHome.Web.Domain;

namespace GoHome.Web.Services;

public class AuditService(AppDbContext db, IClock clock)
{
    public async Task LogAsync(string entity, int entityId, string action, string? details, string actor)
    {
        db.AuditLogs.Add(new AuditLog
        {
            Entity = entity, EntityId = entityId, Action = action, Details = details, Actor = actor, At = clock.UtcNow,
        });
        await db.SaveChangesAsync();
    }
}
