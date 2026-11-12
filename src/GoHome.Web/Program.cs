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

app.Run();

public partial class Program;
