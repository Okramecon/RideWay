using GoHome.Web.Domain;
using Microsoft.EntityFrameworkCore;

namespace GoHome.Web.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Car> Cars => Set<Car>();
    public DbSet<Route> Routes => Set<Route>();
    public DbSet<Stop> Stops => Set<Stop>();
    public DbSet<Membership> Memberships => Set<Membership>();
    public DbSet<OneOffBooking> OneOffBookings => Set<OneOffBooking>();
    public DbSet<Absence> Absences => Set<Absence>();
    public DbSet<TripCancellation> TripCancellations => Set<TripCancellation>();
    public DbSet<RideLog> RideLogs => Set<RideLog>();
    public DbSet<TripArrival> TripArrivals => Set<TripArrival>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>().HasIndex(x => x.Phone).IsUnique();
        b.Entity<User>().HasIndex(x => x.TelegramId).IsUnique();

        b.Entity<Car>().HasOne(x => x.Driver).WithMany().HasForeignKey(x => x.DriverUserId)
            .OnDelete(DeleteBehavior.SetNull);

        b.Entity<Route>().Property(x => x.PricePerRide).HasConversion<double>();
        b.Entity<Route>().HasMany(x => x.Stops).WithOne(x => x.Route).HasForeignKey(x => x.RouteId);

        b.Entity<Membership>().HasIndex(x => new { x.RouteId, x.Status });
        b.Entity<Membership>().HasOne(x => x.Stop).WithMany().HasForeignKey(x => x.StopId)
            .OnDelete(DeleteBehavior.Restrict);

        b.Entity<OneOffBooking>().HasIndex(x => new { x.RouteId, x.Date, x.Direction });
        b.Entity<OneOffBooking>().HasOne(x => x.Stop).WithMany().HasForeignKey(x => x.StopId)
            .OnDelete(DeleteBehavior.Restrict);

        b.Entity<Absence>().HasIndex(x => new { x.UserId, x.Date, x.Direction }).IsUnique();
        b.Entity<TripCancellation>().HasIndex(x => new { x.RouteId, x.Date, x.Direction }).IsUnique();
        b.Entity<TripArrival>().HasIndex(x => new { x.RouteId, x.Date, x.Direction }).IsUnique();

        // Одна отметка на пассажира и рейс — повторная отметка перезаписывает.
        b.Entity<RideLog>().HasIndex(x => new { x.UserId, x.Date, x.Direction }).IsUnique();
        b.Entity<RideLog>().Property(x => x.Price).HasConversion<double>();

        b.Entity<Payment>().Property(x => x.Amount).HasConversion<double>();
    }
}
