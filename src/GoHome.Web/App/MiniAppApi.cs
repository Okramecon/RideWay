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
    public record TodayRequest(int RouteId, int StopId, int? Seat);
    public record CarRef(int RouteId);
    public record LayoutRequest(int RouteId, string Layout);
    public record BoardRequest(string Code);
    public record DriverTripRef(int RouteId, DateOnly Date, Direction Dir);
    public record MarkRequest(int RouteId, DateOnly Date, Direction Dir, int UserId, RideResult Result);
    public record CancelRequest(int RouteId, DateOnly Date, Direction Dir, string? Reason);
    public record LateRequest(DateOnly Date, Direction Dir, int Minutes);
    public record OneOffCancelRequest(int BookingId);
    public record SurveyRequest(string Kind, Dictionary<string, string>? Answers);
    public record DelayRequest(int RouteId, DateOnly Date, Direction Dir, int Minutes);
    public record DriverPaymentRequest(int RouteId, int UserId, decimal Amount);

    public static void MapMiniAppApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/app").AllowAnonymous().AddEndpointFilter(AuthFilter);

        api.MapGet("/me", async (HttpContext ctx, AppDbContext db, BillingService billing, BotInfo bot, IClock clock) =>
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
                // Местное время сервера: по нему приложение считает «через N минут», даже если часы телефона сбиты.
                serverNow = clock.LocalNow.ToString("yyyy-MM-ddTHH:mm:ss"),
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

        // Запись «на сегодня»: какие сегодняшние рейсы ещё открыты для этой остановки и общая схема мест на них
        // (место занято, если занято хотя бы на одном из открытых рейсов — пассажир едет на одном месте весь день).
        api.MapGet("/routes/{id:int}/today", async (HttpContext ctx, int id, int? stopId, AppDbContext db, BookingService booking) =>
        {
            var route = await db.Routes.Include(r => r.Car).FirstOrDefaultAsync(r => r.Id == id && r.IsActive);
            if (route is null) return Results.NotFound();
            var list = await booking.TodayTripsAsync(Current(ctx).Id, id, stopId ?? 0);
            var open = list.Where(t => t.Open).ToList();
            var taken = open.SelectMany(t => t.Roster.TakenSeats).ToHashSet();
            var layout = route.Car.ParsedLayout();
            return Results.Ok(new
            {
                photo = CarService.PhotoUrl(route.Car),
                trips = list.Select(t => new
                {
                    dir = t.Dir, board = t.BoardTime, departure = t.Roster.DepartureTime, mine = t.Mine, open = t.Open,
                    cancelled = t.Roster.IsCancelled, free = t.Roster.FreeSeatNumbers.Count,
                }),
                free = open.Count == 0 ? 0 : layout.SeatNumbers.Count(n => !taken.Contains(n)),
                layout = Grid(layout, n => (taken.Contains(n) ? "taken" : "free", null)),
            });
        });

        api.MapPost("/today/book", async (HttpContext ctx, TodayRequest r, BookingService booking, DriverAlerts alerts, IClock clock) =>
        {
            var u = Current(ctx);
            var (res, booked) = await booking.BookTodayAsync(u.Id, r.RouteId, r.StopId, r.Seat, Actor(ctx));
            if (res.Ok) await alerts.NewPassengerAsync(r.RouteId, u.Id, r.StopId, clock.Today, booked, r.Seat);
            return Result(res);
        });

        api.MapPost("/membership", async (HttpContext ctx, JoinRequest r, BookingService booking) =>
            Result(await booking.JoinRouteAsync(Current(ctx).Id, r.RouteId, r.StopId, r.Morning, r.Evening, Actor(ctx), r.Seat)));

        api.MapPost("/membership/seat", async (HttpContext ctx, SeatChangeRequest r, BookingService booking) =>
            Result(await booking.ChangeSeatAsync(Current(ctx).Id, r.Seat, r.StopId, Actor(ctx))));

        api.MapPost("/membership/leave", async (HttpContext ctx, BookingService booking) =>
            Result(await booking.LeaveRouteAsync(Current(ctx).Id, Actor(ctx))));

        api.MapGet("/trips", async (HttpContext ctx, TripService trips, TripOpsService ops, WorkCalendar calendar, IClock clock) =>
        {
            var u = Current(ctx);
            var upcoming = await trips.GetUserUpcomingAsync(u.Id, days: 5);
            var list = new List<object>();
            foreach (var (t, e) in upcoming)
            {
                var time = t.Direction == Direction.Morning ? e.Stop.MorningTime : e.Stop.EveningTime;
                var isToday = t.Date == clock.Today;
                list.Add(new
                {
                    routeId = t.Route.Id, route = t.Route.Name, t.Date, dir = t.Direction,
                    label = $"{WorkCalendar.Format(t.Date)}, {WorkCalendar.Format(t.Direction)}",
                    isToday,
                    time, departure = t.DepartureTime, price = t.Route.PricePerRide,
                    stop = e.Stop.Name, stopNote = e.Stop.Note, stopLat = e.Stop.Lat, stopLng = e.Stop.Lng,
                    seat = e.SeatNumber, e.IsRegular, e.IsAbsent, t.IsCancelled, e.Result, bookingId = e.OneOffBookingId,
                    // Разовую запись на сегодня можно отменить до 10 минут до посадки; «не еду» постоянного — только накануне до 21:00.
                    canChange = !t.IsCancelled && (calendar.CanChange(t.Date)
                        || (!e.IsRegular && calendar.CanBookSameDay(t.Date, WorkCalendar.BoardTime(t.Route, e.Stop, t.Direction)))),
                    departed = isToday && await ops.IsDepartedAsync(t.Route.Id, t.Date, t.Direction),
                    arrived = t.ArrivedAt is not null,
                    driver = t.Route.Car.Driver?.Name, driverPhone = t.Route.Car.Driver?.Phone, car = $"{t.Route.Car.Model} {t.Route.Car.Plate}",
                });
            }
            return list;
        });

        // Неделя пассажира: 5 ближайших рабочих дней × утро/вечер на его маршруте — своя поездка или свободные места.
        api.MapGet("/week", async (HttpContext ctx, int? routeId, AppDbContext db, TripService trips, WorkCalendar calendar, IClock clock) =>
        {
            var u = Current(ctx);
            var today = clock.Today;
            var id = routeId
                ?? await db.Memberships.Where(m => m.UserId == u.Id && m.Status == MembershipStatus.Regular && m.To == null)
                    .Select(m => (int?)m.RouteId).FirstOrDefaultAsync()
                ?? await db.OneOffBookings.Where(b => b.UserId == u.Id && b.Status == BookingStatus.Booked && b.Date >= today)
                    .OrderBy(b => b.Date).Select(b => (int?)b.RouteId).FirstOrDefaultAsync()
                ?? await db.Routes.Where(r => r.IsActive).OrderBy(r => r.Name).Select(r => (int?)r.Id).FirstOrDefaultAsync();
            var route = id is null ? null : await db.Routes.FirstOrDefaultAsync(r => r.Id == id && r.IsActive);
            if (route is null) return Results.Ok(new { routeId = (int?)null, route = "", price = 0m, days = Array.Empty<object>() });

            var days = new List<object>();
            foreach (var date in calendar.Workdays(today, today.AddDays(30)).Take(5))
            {
                var cells = new List<object>();
                foreach (var dir in Enum.GetValues<Direction>())
                {
                    var t = await trips.GetRosterAsync(route.Id, date, dir);
                    var e = t.Entries.FirstOrDefault(x => x.UserId == u.Id);
                    cells.Add(new
                    {
                        dir, runs = t.Runs, t.IsCancelled, canChange = !t.IsCancelled && calendar.CanChange(date), free = t.Free,
                        departure = t.DepartureTime,
                        time = e is null ? (TimeOnly?)null : dir == Direction.Morning ? e.Stop.MorningTime : e.Stop.EveningTime,
                        mine = e is not null, isRegular = e?.IsRegular ?? false, isAbsent = e?.IsAbsent ?? false,
                        result = e?.Result, seat = e?.SeatNumber, bookingId = e?.OneOffBookingId,
                    });
                }
                days.Add(new { date, label = WorkCalendar.Format(date), isToday = date == today, cells });
            }
            return Results.Ok(new { routeId = (int?)route.Id, route = route.Name, price = route.PricePerRide, days });
        });

        api.MapPost("/oneoff/cancel", async (HttpContext ctx, OneOffCancelRequest r, BookingService booking) =>
            Result(await booking.CancelOneOffAsync(r.BookingId, Current(ctx).Id, Actor(ctx))));

        api.MapPost("/late", async (HttpContext ctx, LateRequest r, TripOpsService ops) =>
            Result(await ops.NotifyLateAsync(Current(ctx).Id, r.Date, r.Dir, r.Minutes)));

        // Короткие опросы (лист ожидания, интерес к абонементу). Отдельной таблицы пока нет — ответы пишем в журнал, раздел «Survey».
        api.MapPost("/survey", async (HttpContext ctx, SurveyRequest r, AuditService audit) =>
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(r.Kind ?? "", "^[a-z_]{1,32}$")) return Results.BadRequest();
            var answers = (r.Answers ?? []).Take(10)
                .ToDictionary(kv => kv.Key.Length > 32 ? kv.Key[..32] : kv.Key, kv => kv.Value is { Length: > 100 } v ? v[..100] : kv.Value ?? "");
            var json = System.Text.Json.JsonSerializer.Serialize(answers, new System.Text.Json.JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
            await audit.LogAsync("Survey", Current(ctx).Id, r.Kind!, json, Actor(ctx));
            return Result(OpResult.Success("Спасибо, записали."));
        });

        api.MapPost("/absence", async (HttpContext ctx, TripRef r, BookingService booking) =>
            Result(await booking.MarkAbsentAsync(Current(ctx).Id, r.Date, r.Dir, Actor(ctx))));

        api.MapPost("/absence/remove", async (HttpContext ctx, TripRef r, BookingService booking) =>
            Result(await booking.UnmarkAbsentAsync(Current(ctx).Id, r.Date, r.Dir, Actor(ctx))));

        api.MapPost("/oneoff", async (HttpContext ctx, OneOffRequest r, BookingService booking, DriverAlerts alerts) =>
        {
            var res = await booking.BookOneOffAsync(Current(ctx).Id, r.RouteId, r.StopId, r.Date, r.Dir, Actor(ctx), r.Seat);
            if (res.Ok) await alerts.NewPassengerAsync(r.RouteId, Current(ctx).Id, r.StopId, r.Date, [r.Dir], r.Seat);
            return Result(res);
        });

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

        driver.MapGet("/trip", async (HttpContext ctx, int routeId, DateOnly? date, Direction? dir, TripService trips, TripOpsService ops, BillingService billing, IClock clock, AppDbContext db) =>
        {
            if (!await CanDriveAsync(ctx, db, routeId)) return Results.Forbid();
            var d = date ?? clock.Today;
            var direction = dir ?? (clock.LocalNow.Hour < 13 ? Direction.Morning : Direction.Evening);
            var t = await trips.GetRosterAsync(routeId, d, direction);
            var dues = new Dictionary<int, decimal>();
            foreach (var e in t.Entries) dues[e.UserId] = Math.Max(0, (await billing.GetBalanceAsync(e.UserId)).Due);
            return Results.Ok(new
            {
                routeId, route = t.Route.Name, date = d, dir = direction,
                label = $"{WorkCalendar.Format(d)}, {WorkCalendar.Format(direction)}",
                t.IsWorkday, t.IsCancelled, reason = t.Cancellation?.Reason, t.Capacity, t.Occupied,
                departure = t.DepartureTime, arrivedAt = t.ArrivedAt, isToday = d == clock.Today, canMark = d <= clock.Today,
                departed = d == clock.Today && await ops.IsDepartedAsync(routeId, d, direction),
                price = t.Route.PricePerRide,
                photo = CarService.PhotoUrl(t.Route.Car),
                // Карта рейса: линия маршрута и остановки; passengers — сколько человек садится/выходит на остановке в этот рейс.
                map = new
                {
                    path = RouteMapService.LineFor(t.Route),
                    stops = t.Route.Stops.OrderBy(s => s.Order).Select(s => new
                    {
                        s.Id, s.Order, s.Name, s.Note, s.Lat, s.Lng, passengers = t.Riding.Count(e => e.Stop.Id == s.Id),
                        time = direction == Direction.Morning ? s.MorningTime : s.EveningTime,
                    }),
                },
                layout = Grid(t.Layout, n => t.SeatOwners.TryGetValue(n, out var o) ? ("taken", ShortName(o.Name)) : ("free", null)),
                passengers = t.Entries.Select(e => new
                {
                    e.UserId, e.Name, e.Phone, stop = e.Stop.Name, stopId = e.Stop.Id, stopLat = e.Stop.Lat, stopLng = e.Stop.Lng, seat = e.SeatNumber,
                    time = direction == Direction.Morning ? e.Stop.MorningTime : e.Stop.EveningTime,
                    e.IsRegular, e.IsAbsent, e.Result, due = dues[e.UserId],
                }),
            });
        });

        driver.MapPost("/depart", async (HttpContext ctx, DriverTripRef r, TripOpsService ops, AppDbContext db) =>
            await CanDriveAsync(ctx, db, r.RouteId) ? Result(await ops.DepartAsync(r.RouteId, r.Date, r.Dir, Actor(ctx))) : Results.Forbid());

        driver.MapPost("/delay", async (HttpContext ctx, DelayRequest r, TripOpsService ops, AppDbContext db) =>
            await CanDriveAsync(ctx, db, r.RouteId) ? Result(await ops.NotifyDelayAsync(r.RouteId, r.Date, r.Dir, r.Minutes, Actor(ctx))) : Results.Forbid());

        // Водитель принял наличные: не больше долга и только от того, кто ездил на его маршруте.
        driver.MapPost("/payment", async (HttpContext ctx, DriverPaymentRequest r, BillingService billing, AppDbContext db) =>
        {
            if (!await CanDriveAsync(ctx, db, r.RouteId)) return Results.Forbid();
            if (!await db.RideLogs.AnyAsync(l => l.RouteId == r.RouteId && l.UserId == r.UserId))
                return Result(OpResult.Fail("Этот пассажир не ездил на вашем маршруте."));
            var due = (await billing.GetBalanceAsync(r.UserId)).Due;
            if (r.Amount <= 0 || r.Amount > due) return Result(OpResult.Fail($"Сумма должна быть от 1 до {due:0} сом."));
            return Result(await billing.RecordPaymentAsync(r.UserId, r.Amount, PaymentMethod.Cash, Current(ctx).Name, "принял водитель в приложении", Actor(ctx)));
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

    /// <summary>
    /// То же для локальной проверки в Docker или с телефона в той же Wi‑Fi: запрос из частной сети (10/8, 172.16/12, 192.168/16) без заголовков прокси.
    /// Включается только явно: Development + Dev:AllowPrivateNetwork=true.
    /// </summary>
    public static bool IsDirectPrivateRequest(HttpContext http)
    {
        if (http.Request.Headers.ContainsKey("X-Forwarded-For") || http.Request.Headers.ContainsKey("CF-Connecting-IP") || http.Request.Headers.ContainsKey("CF-Ray"))
            return false;
        var ip = http.Connection.RemoteIpAddress;
        if (ip is null) return false;
        if (System.Net.IPAddress.IsLoopback(ip)) return true;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
    }

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
        else if (env.IsDevelopment() && long.TryParse(http.Request.Headers["X-Dev-User"], out var devId)
                 && (IsDirectLocalRequest(http)
                     || (sp.GetRequiredService<IConfiguration>().GetValue<bool>("Dev:AllowPrivateNetwork") && IsDirectPrivateRequest(http))))
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
