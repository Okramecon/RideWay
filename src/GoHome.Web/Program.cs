using GoHome.Web.Bot;
using GoHome.Web.Data;
using GoHome.Web.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot;

var builder = WebApplication.CreateBuilder(args);
var services = builder.Services;
var config = builder.Configuration;

services.Configure<PilotOptions>(config.GetSection(PilotOptions.Section));
// Кириллица в HTML без &#x...; сущностей.
services.Configure<Microsoft.Extensions.WebEncoders.WebEncoderOptions>(o =>
    o.TextEncoderSettings = new System.Text.Encodings.Web.TextEncoderSettings(System.Text.Unicode.UnicodeRanges.All));
services.Configure<BotOptions>(config.GetSection(BotOptions.Section));

services.AddDbContext<AppDbContext>(o => o.UseSqlite(config.GetConnectionString("Default") ?? "Data Source=gohome.db"));

services.AddSingleton<IClock, SystemClock>();
services.AddScoped<WorkCalendar>();
services.AddScoped<AuditService>();
services.AddScoped<TripService>();
services.AddScoped<BookingService>();
services.AddScoped<TripOpsService>();
services.AddScoped<BillingService>();
services.AddScoped<MetricsService>();
services.AddScoped<CarService>();
services.Configure<RoutingOptions>(config.GetSection(RoutingOptions.Section));
services.AddHttpClient<IRoutingProvider, OsrmRoutingProvider>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(20);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("RideWayPilot/1.0");
});
services.AddScoped<RouteMapService>();
services.Configure<StorageOptions>(config.GetSection(StorageOptions.Section));
// Относительный путь хранилища считаем от папки приложения.
services.PostConfigure<StorageOptions>(o => o.Path = Path.GetFullPath(o.Path, builder.Environment.ContentRootPath));

var botToken = config[$"{BotOptions.Section}:Token"];
if (!string.IsNullOrWhiteSpace(botToken))
{
    services.AddSingleton<ITelegramBotClient>(new TelegramBotClient(botToken));
    services.AddSingleton<INotifier, TelegramNotifier>();
    services.AddScoped<BotUpdateHandler>();
    services.AddHostedService<BotPollingService>();
}
else
{
    services.AddSingleton<INotifier, LogNotifier>();
}
services.AddHostedService<ScheduledNotifications>();
services.AddSingleton<GoHome.Web.App.BotInfo>();
services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

// Ключи cookie — в постоянный том, чтобы вход в админку переживал перезапуск контейнера.
if (config["DataProtection:KeysPath"] is { Length: > 0 } keysPath)
    services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keysPath));

services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/Login";
        o.ExpireTimeSpan = TimeSpan.FromDays(30);
        o.SlidingExpiration = true;
    });
services.AddAuthorization();
services.AddRazorPages(o =>
{
    o.Conventions.AuthorizeFolder("/");
    o.Conventions.AllowAnonymousToPage("/Login");
    o.Conventions.AllowAnonymousToPage("/Error");
});

var app = builder.Build();

if (string.IsNullOrWhiteSpace(botToken))
    app.Logger.LogWarning("Bot:Token не задан — Telegram-бот выключен, работает только админка.");

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
    if (app.Environment.IsDevelopment() && config.GetValue<bool>("SeedDemoData"))
        await DemoSeed.RunAsync(db);

    // У машин, заведённых до появления QR-посадки, кода нет — выдаём.
    foreach (var car in db.Cars.Where(c => c.BoardingCode == "").ToList())
        car.BoardingCode = GoHome.Web.Domain.Car.NewBoardingCode();
    await db.SaveChangesAsync();

    // Схемы салонов и места у записей, созданных до появления выбора мест.
    var clock = scope.ServiceProvider.GetRequiredService<IClock>();
    await SeatBackfill.RunAsync(db, clock.Today);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapStaticAssets();
app.MapRazorPages().WithStaticAssets();
app.MapGet("/health", () => Results.Ok("ok")).AllowAnonymous();
// Mini App. Страницу не кэшируем: WebView Telegram иначе держит старую версию часами и новые функции «не появляются».
app.MapGet("/app/", async (IWebHostEnvironment env, HttpContext ctx) =>
{
    var html = await File.ReadAllTextAsync(Path.Combine(env.WebRootPath, "app", "index.html"));
    var scriptVersion = File.GetLastWriteTimeUtc(Path.Combine(env.WebRootPath, "js", "seat-editor.js")).Ticks;
    ctx.Response.Headers.CacheControl = "no-store";
    return Results.Content(html.Replace("/js/seat-editor.js", $"/js/seat-editor.js?v={scriptVersion}"), "text/html; charset=utf-8");
}).AllowAnonymous();
// Фото салона. Имена случайные (guid), поэтому отдаём без авторизации — <img> не умеет слать заголовки Telegram.
app.MapGet("/media/{name}", (string name, CarService cars) =>
    cars.MediaPath(name) is { } path
        ? Results.File(path, name.EndsWith(".png") ? "image/png" : name.EndsWith(".webp") ? "image/webp" : "image/jpeg",
            lastModified: null, entityTag: null, enableRangeProcessing: false)
        : Results.NotFound()).AllowAnonymous();
GoHome.Web.App.MiniAppApi.MapMiniAppApi(app);

app.Run();

public partial class Program;
