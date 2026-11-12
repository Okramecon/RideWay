using GoHome.Web.Data;
using GoHome.Web.Domain;
using GoHome.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GoHome.Tests;

public class FakeClock : IClock
{
    public DateTime LocalNow { get; set; }
    public DateTime UtcNow => LocalNow.AddHours(-6);
}

public class FakeNotifier : INotifier
{
    public List<(long Chat, string Text)> Sent { get; } = [];
    public Task SendAsync(long telegramChatId, string text)
    {
        lock (Sent) Sent.Add((telegramChatId, text));
        return Task.CompletedTask;
    }
}

/// <summary>Файловая SQLite на тест: так можно открывать несколько контекстов параллельно (проверка гонок).</summary>
public sealed class Harness : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"gohome-test-{Guid.NewGuid():N}.db");
    public ServiceProvider Services { get; }
    public FakeClock Clock { get; } = new();
    public FakeNotifier Notifier { get; } = new();

    // Понедельник 16.11.2026, 10:00 — изменения на вторник ещё открыты.
    public static readonly DateOnly Monday = new(2026, 11, 16);
    public DateOnly Tuesday => Monday.AddDays(1);

    public Harness(bool chargeNoShow = false)
    {
        Clock.LocalNow = Monday.ToDateTime(new TimeOnly(10, 0));
        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        sc.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={_dbPath};Pooling=False"));
        sc.Configure<PilotOptions>(o => o.ChargeNoShow = chargeNoShow);
        sc.AddSingleton<IClock>(Clock);
        sc.AddSingleton<INotifier>(Notifier);
        sc.AddScoped<WorkCalendar>();
        sc.AddScoped<AuditService>();
        sc.AddScoped<TripService>();
        sc.AddScoped<BookingService>();
        sc.AddScoped<TripOpsService>();
        sc.AddScoped<BillingService>();
        sc.AddScoped<MetricsService>();
        Services = sc.BuildServiceProvider();

        using var scope = Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.Migrate();
    }

    public T Get<T>(IServiceScope scope) where T : notnull => scope.ServiceProvider.GetRequiredService<T>();

    public async Task<(Route Route, Stop Stop)> AddRouteAsync(int seats, decimal price = 100)
    {
        using var scope = Services.CreateScope();
        var db = Get<AppDbContext>(scope);
        var route = new Route
        {
            Name = "Тест", PricePerRide = price, IsActive = true,
            Car = new Car { Model = "Car", Plate = "X", Seats = seats },
            Stops =
            [
                new Stop { Order = 1, Name = "Дом", MorningTime = new(7, 30), EveningTime = new(18, 50) },
                new Stop { Order = 2, Name = "Офис", MorningTime = new(8, 0), EveningTime = new(18, 30) },
            ],
        };
        db.Routes.Add(route);
        await db.SaveChangesAsync();
        return (route, route.Stops[0]);
    }

    public async Task<int> AddUserAsync(string name, long? telegramId = null)
    {
        using var scope = Services.CreateScope();
        var db = Get<AppDbContext>(scope);
        var u = new User { Name = name, Phone = $"996{Random.Shared.Next(100000000, 999999999)}", TelegramId = telegramId, CreatedAt = DateTime.UtcNow };
        db.Users.Add(u);
        await db.SaveChangesAsync();
        return u.Id;
    }

    public async Task<T> RunAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        using var scope = Services.CreateScope();
        return await action(scope.ServiceProvider);
    }

    public void Dispose()
    {
        Services.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch (IOException) { }
    }
}
