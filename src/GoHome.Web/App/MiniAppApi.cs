using GoHome.Web.Bot;
using GoHome.Web.Data;
using GoHome.Web.Domain;
using GoHome.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GoHome.Web.App;

/// <summary>
/// API Telegram Mini App (/app). Каждый запрос несёт initData в заголовке «Authorization: tma &lt;initData&gt;»,
/// подпись проверяется токеном бота. В Development можно подставить «X-Dev-User: &lt;telegramId&gt;» для отладки в браузере.
/// </summary>
public static class MiniAppApi
{
    private const string UserKey = "miniapp-user";

    public record JoinRequest(int RouteId, int StopId, bool Morning, bool Evening);
    public record TripRef(DateOnly Date, Direction Dir);
    public record OneOffRequest(int RouteId, int StopId, DateOnly Date, Direction Dir);
    public record BoardRequest(string Code);
    public record DriverTripRef(int RouteId, DateOnly Date, Direction Dir);
    public record MarkRequest(int RouteId, DateOnly Date, Direction Dir, int UserId, RideResult Result);
    public record CancelRequest(int RouteId, DateOnly Date, Direction Dir, string? Reason);

    public static void MapMiniAppApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/app").AllowAnonymous().AddEndpointFilter(AuthFilter);

        api.MapGet("/me", async (HttpContext ctx, AppDbContext db, BillingService billing, BotInfo bot) =>
        {
            var u = Current(ctx);
            var m = await db.Memberships.Include(x => x.Route).Include(x => x.Stop)
                .Where(x => x.UserId == u.Id && x.Status != MembershipStatus.Left).FirstOrDefaultAsync();
            var balance = await billing.GetBalanceAsync(u.Id);
            var drives = u.Role != UserRole.Passenger;
            return Results.Ok(new
            {
                user = new { u.Id, u.Name, role = u.Role },
                membership = m is null ? null : new
                {
                    m.RouteId, route = m.Route.Name, m.StopId, stop = m.Stop.Name, m.Morning, m.Evening, m.Status, m.From,
                },
                due = Math.Max(0, balance.Due),
                isDriver = drives,
                botUsername = bot.Username,
            });
        });

        api.MapGet("/routes", async (AppDbContext db) =>
        {
            var routes = await db.Routes.Include(r => r.Car).Include(r => r.Stops)
                .Where(r => r.IsActive).OrderBy(r => r.Name).ToListAsync();
            var regulars = await db.Memberships.Where(m => m.Status == MembershipStatus.Regular && m.To == null).ToListAsync();
            return routes.Select(r => new
            {
                r.Id, r.Name, price = r.PricePerRide, seats = r.Car.Seats, car = $"{r.Car.Model} {r.Car.Plate}",
                freeMorning = Math.Max(0, r.Car.Seats - regulars.Count(m => m.RouteId == r.Id && m.Morning)),
                freeEvening = Math.Max(0, r.Car.Seats - regulars.Count(m => m.RouteId == r.Id && m.Evening)),
                stops = r.Stops.OrderBy(s => s.Order).Select(s => new { s.Id, s.Order, s.Name, morning = s.MorningTime, evening = s.EveningTime }),
            });
        });

        api.MapGet("/routes/{id:int}/trips", async (int id, TripService trips, WorkCalendar calendar) =>
        {
            var list = new List<object>();
            foreach (var date in calendar.UpcomingChangeableDays())
                foreach (var dir in Enum.GetValues<Direction>())
                {
                    var t = await trips.GetRosterAsync(id, date, dir);
                    if (t.Runs && t.Free > 0)
                        list.Add(new { date, dir, free = t.Free, label = $"{WorkCalendar.Format(date)}, {WorkCalendar.Format(dir)}", departure = t.DepartureTime });
                }
            return list;
        });

        api.MapPost("/membership", async (HttpContext ctx, JoinRequest r, BookingService booking) =>
            Result(await booking.JoinRouteAsync(Current(ctx).Id, r.RouteId, r.StopId, r.Morning, r.Evening, Actor(ctx))));

        api.MapPost("/membership/leave", async (HttpContext ctx, BookingService booking) =>
            Result(await booking.LeaveRouteAsync(Current(ctx).Id, Actor(ctx))));

        api.MapGet("/trips", async (HttpContext ctx, TripService trips, WorkCalendar calendar, IClock clock) =>
        {
            var u = Current(ctx);
            var upcoming = await trips.GetUserUpcomingAsync(u.Id, days: 5);
            return upcoming.Select(x =>
            {
                var (t, e) = x;
                var time = t.Direction == Direction.Morning ? e.Stop.MorningTime : e.Stop.EveningTime;
                return new
                {
                    routeId = t.Route.Id, route = t.Route.Name, t.Date, dir = t.Direction,
                    label = $"{WorkCalendar.Format(t.Date)}, {WorkCalendar.Format(t.Direction)}",
                    isToday = t.Date == clock.Today,
                    time, stop = e.Stop.Name, e.IsRegular, e.IsAbsent, t.IsCancelled, e.Result,
                    canChange = !t.IsCancelled && calendar.CanChange(t.Date),
                    driver = t.Route.Car.Driver?.Name, driverPhone = t.Route.Car.Driver?.Phone, car = $"{t.Route.Car.Model} {t.Route.Car.Plate}",
                };
            });
        });

        api.MapPost("/absence", async (HttpContext ctx, TripRef r, BookingService booking) =>
            Result(await booking.MarkAbsentAsync(Current(ctx).Id, r.Date, r.Dir, Actor(ctx))));

        api.MapPost("/absence/remove", async (HttpContext ctx, TripRef r, BookingService booking) =>
            Result(await booking.UnmarkAbsentAsync(Current(ctx).Id, r.Date, r.Dir, Actor(ctx))));

        api.MapPost("/oneoff", async (HttpContext ctx, OneOffRequest r, BookingService booking) =>
            Result(await booking.BookOneOffAsync(Current(ctx).Id, r.RouteId, r.StopId, r.Date, r.Dir, Actor(ctx))));

        api.MapGet("/balance", async (HttpContext ctx, BillingService billing, IClock clock) =>
        {
            var u = Current(ctx);
            var b = await billing.GetBalanceAsync(u.Id);
            var rides = await billing.GetRidesAsync(u.Id, clock.Today.AddDays(-27), clock.Today);
            return Results.Ok(new
            {
                due = b.Due, b.Charged, b.Paid, b.Rides, b.NoShows,
                items = rides.OrderByDescending(l => l.Date).ThenByDescending(l => l.Direction).Select(l => new
                {
                    l.Date, dir = l.Direction, label = $"{WorkCalendar.Format(l.Date)}, {WorkCalendar.Format(l.Direction)}", l.Result, l.Price,
                }),
            });
        });

        api.MapPost("/board", async (HttpContext ctx, BoardRequest r, TripOpsService ops) =>
            Result(await ops.BoardByCodeAsync(Current(ctx).Id, ExtractBoardCode(r.Code) ?? "", Actor(ctx))));

        // ───── водитель ─────
        var driver = api.MapGroup("/driver").AddEndpointFilter(async (ctx, next) =>
            Current(ctx.HttpContext).Role == UserRole.Passenger ? Results.Forbid() : await next(ctx));

        driver.MapGet("/routes", async (HttpContext ctx, AppDbContext db) =>
        {
            var u = Current(ctx);
            return await db.Routes.Where(r => r.IsActive && (u.Role == UserRole.Admin || r.Car.DriverUserId == u.Id))
                .OrderBy(r => r.Name).Select(r => new { r.Id, r.Name }).ToListAsync();
        });

        driver.MapGet("/trip", async (HttpContext ctx, int routeId, DateOnly? date, Direction? dir, TripService trips, IClock clock, AppDbContext db) =>
        {
            if (!await CanDriveAsync(ctx, db, routeId)) return Results.Forbid();
            var d = date ?? clock.Today;
            var direction = dir ?? (clock.LocalNow.Hour < 13 ? Direction.Morning : Direction.Evening);
            var t = await trips.GetRosterAsync(routeId, d, direction);
            return Results.Ok(new
            {
                routeId, route = t.Route.Name, date = d, dir = direction,
                label = $"{WorkCalendar.Format(d)}, {WorkCalendar.Format(direction)}",
                t.IsWorkday, t.IsCancelled, reason = t.Cancellation?.Reason, t.Capacity, t.Occupied,
                departure = t.DepartureTime, arrivedAt = t.ArrivedAt, isToday = d == clock.Today, canMark = d <= clock.Today,
                passengers = t.Entries.Select(e => new
                {
                    e.UserId, e.Name, e.Phone, stop = e.Stop.Name,
                    time = direction == Direction.Morning ? e.Stop.MorningTime : e.Stop.EveningTime,
                    e.IsRegular, e.IsAbsent, e.Result,
                }),
            });
        });

        driver.MapPost("/mark", async (HttpContext ctx, MarkRequest r, TripOpsService ops, AppDbContext db) =>
            await CanDriveAsync(ctx, db, r.RouteId)
                ? Result(await ops.MarkRideAsync(r.RouteId, r.Date, r.Dir, r.UserId, r.Result, Current(ctx).Id, Actor(ctx)))
                : Results.Forbid());

        driver.MapPost("/arrived", async (HttpContext ctx, DriverTripRef r, TripOpsService ops, AppDbContext db) =>
            await CanDriveAsync(ctx, db, r.RouteId) ? Result(await ops.MarkArrivedAsync(r.RouteId, r.Date, r.Dir, Actor(ctx))) : Results.Forbid());

        driver.MapPost("/cancel", async (HttpContext ctx, CancelRequest r, TripOpsService ops, AppDbContext db) =>
            await CanDriveAsync(ctx, db, r.RouteId)
                ? Result((await ops.CancelTripAsync(r.RouteId, r.Date, r.Dir, string.IsNullOrWhiteSpace(r.Reason) ? "Отменено водителем" : r.Reason, Current(ctx).Id, Actor(ctx))).Result)
                : Results.Forbid());
    }

    /// <summary>Код из QR: принимает и голый код, и ссылку вида t.me/bot?start=board_XXXX.</summary>
    public static string? ExtractBoardCode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(text, "board_([0-9a-f]{6,32})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value.ToLowerInvariant();
        var t = text.Trim().ToLowerInvariant();
        return System.Text.RegularExpressions.Regex.IsMatch(t, "^[0-9a-f]{6,32}$") ? t : null;
    }

    private static IResult Result(OpResult r) => Results.Ok(new { ok = r.Ok, message = r.Message });

    private static User Current(HttpContext ctx) => (User)ctx.Items[UserKey]!;

    private static string Actor(HttpContext ctx) => $"app:{Current(ctx).Id}:{Current(ctx).Name}";

    private static async Task<bool> CanDriveAsync(HttpContext ctx, AppDbContext db, int routeId)
    {
        var u = Current(ctx);
        return u.Role == UserRole.Admin || await db.Routes.AnyAsync(r => r.Id == routeId && r.Car.DriverUserId == u.Id);
    }

    private static async ValueTask<object?> AuthFilter(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (http.Items.ContainsKey(UserKey)) return await next(context);

        var sp = http.RequestServices;
        var env = sp.GetRequiredService<IWebHostEnvironment>();
        var token = sp.GetRequiredService<IOptions<BotOptions>>().Value.Token;
        var db = sp.GetRequiredService<AppDbContext>();

        long? telegramId = null;
        var auth = http.Request.Headers.Authorization.ToString();
        if (auth.StartsWith("tma ", StringComparison.Ordinal))
            telegramId = TelegramInitData.Validate(auth[4..], token, DateTimeOffset.UtcNow, TimeSpan.FromDays(1))?.UserId;
        else if (env.IsDevelopment() && long.TryParse(http.Request.Headers["X-Dev-User"], out var devId))
            telegramId = devId;

        if (telegramId is null)
            return Results.Json(new { error = "unauthorized", message = "Откройте приложение из Telegram." }, statusCode: 401);

        var user = await db.Users.SingleOrDefaultAsync(u => u.TelegramId == telegramId);
        if (user is null)
            return Results.Json(new { error = "need_contact", message = "Поделитесь номером телефона, чтобы начать." }, statusCode: 403);

        http.Items[UserKey] = user;
        return await next(context);
    }
}

/// <summary>Имя бота для QR-ссылок: из конфигурации Bot:Username или из getMe при старте.</summary>
public class BotInfo(IOptions<BotOptions> options)
{
    public string? Username { get; set; } = string.IsNullOrWhiteSpace(options.Value.Username) ? null : options.Value.Username.TrimStart('@');
}
