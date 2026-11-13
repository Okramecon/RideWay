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

    public record JoinRequest(int RouteId, int StopId, bool Morning, bool Evening, int? Seat);
    public record SeatChangeRequest(int? Seat, int? StopId);
    public record TripRef(DateOnly Date, Direction Dir);
    public record OneOffRequest(int RouteId, int StopId, DateOnly Date, Direction Dir, int? Seat);
    public record CarRef(int RouteId);
    public record LayoutRequest(int RouteId, string Layout);
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
                    m.RouteId, route = m.Route.Name, m.StopId, stop = m.Stop.Name, stopLat = m.Stop.Lat, stopLng = m.Stop.Lng, stopNote = m.Stop.Note,
                    m.Morning, m.Evening, m.Status, m.From, seat = m.SeatNumber,
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
                r.Id, r.Name, price = r.PricePerRide, seats = r.Car.Seats, car = $"{r.Car.Model} {r.Car.Plate}", photo = CarService.PhotoUrl(r.Car),
                freeMorning = Math.Max(0, r.Car.Seats - regulars.Count(m => m.RouteId == r.Id && m.Morning)),
                freeEvening = Math.Max(0, r.Car.Seats - regulars.Count(m => m.RouteId == r.Id && m.Evening)),
                path = RouteMapService.LineFor(r),
                stops = r.Stops.OrderBy(s => s.Order).Select(s => new { s.Id, s.Order, s.Name, s.Note, s.Lat, s.Lng, morning = s.MorningTime, evening = s.EveningTime }),
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

        // Схема салона для выбора постоянного места: занято ли место на выбранных направлениях.
        api.MapGet("/routes/{id:int}/seats", async (HttpContext ctx, int id, bool? morning, bool? evening, AppDbContext db, BookingService booking) =>
        {
            var route = await db.Routes.Include(r => r.Car).FirstOrDefaultAsync(r => r.Id == id && r.IsActive);
            if (route is null) return Results.NotFound();
            var dirs = new List<Direction>();
            if (morning ?? true) dirs.Add(Direction.Morning);
            if (evening ?? true) dirs.Add(Direction.Evening);
            if (dirs.Count == 0) dirs = [Direction.Morning, Direction.Evening];

            var u = Current(ctx);
            var taken = await booking.RegularSeatsTakenAsync(id, [.. dirs], excludeUserId: u.Id);
            var mine = await db.Memberships.Where(m => m.UserId == u.Id && m.RouteId == id && m.Status == MembershipStatus.Regular && m.To == null)
                .Select(m => m.SeatNumber).FirstOrDefaultAsync();
            var layout = route.Car.ParsedLayout();
            return Results.Ok(new
            {
                photo = CarService.PhotoUrl(route.Car),
                mySeat = mine,
                free = layout.SeatNumbers.Count(n => !taken.Contains(n)),
                layout = Grid(layout, n => (n == mine ? "mine" : taken.Contains(n) ? "taken" : "free", null)),
            });
        });

        // Схема салона конкретного рейса (для разовой поездки). Имена пассажирам не показываем.
        api.MapGet("/routes/{id:int}/seatmap", async (HttpContext ctx, int id, DateOnly date, Direction dir, TripService trips, WorkCalendar calendar) =>
        {
            var t = await trips.GetRosterAsync(id, date, dir);
            var u = Current(ctx);
            var owners = t.SeatOwners;
            return Results.Ok(new
            {
                photo = CarService.PhotoUrl(t.Route.Car),
                label = $"{WorkCalendar.Format(date)}, {WorkCalendar.Format(dir)}",
                canBook = t.Runs && calendar.CanChange(date),
                free = t.FreeSeatNumbers.Count,
                layout = Grid(t.Layout, n => (owners.TryGetValue(n, out var o) ? (o.UserId == u.Id ? "mine" : "taken") : "free", null)),
            });
        });

        api.MapPost("/membership", async (HttpContext ctx, JoinRequest r, BookingService booking) =>
            Result(await booking.JoinRouteAsync(Current(ctx).Id, r.RouteId, r.StopId, r.Morning, r.Evening, Actor(ctx), r.Seat)));

        api.MapPost("/membership/seat", async (HttpContext ctx, SeatChangeRequest r, BookingService booking) =>
            Result(await booking.ChangeSeatAsync(Current(ctx).Id, r.Seat, r.StopId, Actor(ctx))));

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
                    time, stop = e.Stop.Name, seat = e.SeatNumber, e.IsRegular, e.IsAbsent, t.IsCancelled, e.Result,
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
            Result(await booking.BookOneOffAsync(Current(ctx).Id, r.RouteId, r.StopId, r.Date, r.Dir, Actor(ctx), r.Seat)));

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
                photo = CarService.PhotoUrl(t.Route.Car),
                // Карта рейса: линия маршрута и остановки; passengers — сколько человек садится/выходит на остановке в этот рейс.
                map = new
                {
                    path = RouteMapService.LineFor(t.Route),
                    stops = t.Route.Stops.OrderBy(s => s.Order).Select(s => new
                    {
                        s.Id, s.Order, s.Name, s.Note, s.Lat, s.Lng, passengers = t.Riding.Count(e => e.Stop.Id == s.Id),
                    }),
                },
                layout = Grid(t.Layout, n => t.SeatOwners.TryGetValue(n, out var o) ? ("taken", ShortName(o.Name)) : ("free", null)),
                passengers = t.Entries.Select(e => new
                {
                    e.UserId, e.Name, e.Phone, stop = e.Stop.Name, stopLat = e.Stop.Lat, stopLng = e.Stop.Lng, seat = e.SeatNumber,
                    time = direction == Direction.Morning ? e.Stop.MorningTime : e.Stop.EveningTime,
                    e.IsRegular, e.IsAbsent, e.Result,
                }),
            });
        });

        // ───── машина водителя: схема мест и фото салона ─────
        driver.MapGet("/car", async (HttpContext ctx, int routeId, AppDbContext db, BotInfo bot) =>
        {
            if (!await CanDriveAsync(ctx, db, routeId)) return Results.Forbid();
            var car = await db.Cars.FirstOrDefaultAsync(c => db.Routes.Any(r => r.Id == routeId && r.CarId == c.Id));
            if (car is null) return Results.NotFound();
            // QR отдаём картинкой в самом ответе: <img> не умеет слать заголовок авторизации Telegram.
            var link = CarService.BoardingLink(bot.Username, car.BoardingCode);
            return Results.Ok(new
            {
                carId = car.Id, car.Model, car.Plate, car.Seats, layout = car.ParsedLayout().ToString(), photo = CarService.PhotoUrl(car),
                qr = CarService.QrDataUri(link), qrLink = link,
            });
        });

        driver.MapPost("/car/layout", async (HttpContext ctx, LayoutRequest r, AppDbContext db, CarService cars) =>
        {
            if (!await CanDriveAsync(ctx, db, r.RouteId)) return Results.Forbid();
            var carId = await db.Routes.Where(x => x.Id == r.RouteId).Select(x => x.CarId).FirstOrDefaultAsync();
            return Result(await cars.SaveLayoutAsync(carId, r.Layout, Actor(ctx)));
        });

        // Фото салона: multipart, поля routeId и file. Авторизация — заголовком Authorization, не cookie, поэтому antiforgery не нужен.
        driver.MapPost("/car/photo", async (HttpContext ctx, AppDbContext db, CarService cars) =>
        {
            if (!ctx.Request.HasFormContentType) return Results.BadRequest();
            var form = await ctx.Request.ReadFormAsync();
            if (!int.TryParse(form["routeId"], out var routeId) || form.Files["file"] is not { } file)
                return Result(OpResult.Fail("Выберите фото."));
            if (!await CanDriveAsync(ctx, db, routeId)) return Results.Forbid();
            if (file.Length > CarService.MaxPhotoBytes) return Result(OpResult.Fail("Фото слишком большое (до 6 МБ)."));
            var carId = await db.Routes.Where(x => x.Id == routeId).Select(x => x.CarId).FirstOrDefaultAsync();
            await using var stream = file.OpenReadStream();
            return Result(await cars.SavePhotoAsync(carId, stream, Actor(ctx)));
        });

        driver.MapPost("/car/photo/remove", async (HttpContext ctx, CarRef r, AppDbContext db, CarService cars) =>
        {
            if (!await CanDriveAsync(ctx, db, r.RouteId)) return Results.Forbid();
            var carId = await db.Routes.Where(x => x.Id == r.RouteId).Select(x => x.CarId).FirstOrDefaultAsync();
            return Result(await cars.RemovePhotoAsync(carId, Actor(ctx)));
        });

        driver.MapPost("/mark",async (HttpContext ctx, MarkRequest r, TripOpsService ops, AppDbContext db) =>
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

    /// <summary>
    /// Отладочный вход без подписи Telegram — только для запросов с этого же компьютера. Туннель (cloudflared) тоже приходит с localhost,
    /// но добавляет заголовки прокси; по ним отличаем внешние запросы и не пускаем их в обход подписи.
    /// </summary>
    internal static bool IsDirectLocalRequest(HttpContext http) =>
        http.Connection.RemoteIpAddress is { } ip && System.Net.IPAddress.IsLoopback(ip)
        && !http.Request.Headers.ContainsKey("X-Forwarded-For")
        && !http.Request.Headers.ContainsKey("CF-Connecting-IP")
        && !http.Request.Headers.ContainsKey("CF-Ray");

    private static IResult Result(OpResult r) => Results.Ok(new { ok = r.Ok, message = r.Message });

    /// <summary>Сетка салона для рисования: у каждой ячейки тип, номер места, состояние (free/taken/mine) и подпись.</summary>
    public static object[][] Grid(SeatLayout layout, Func<int, (string State, string? Who)> info) =>
        layout.Rows.Select(row => row.Select(c =>
        {
            if (c.Kind == CellKind.Seat)
            {
                var (state, who) = info(c.Number);
                return (object)new { type = "seat", number = c.Number, state, who };
            }
            return new { type = c.Kind == CellKind.Driver ? "driver" : "empty", number = 0, state = "", who = (string?)null };
        }).ToArray()).ToArray();

    private static string ShortName(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 1 ? $"{parts[0]} {parts[1][0]}." : name;
    }

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
        else if (env.IsDevelopment() && IsDirectLocalRequest(http) && long.TryParse(http.Request.Headers["X-Dev-User"], out var devId))
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
