using System.Text;
using GoHome.Web.Data;
using GoHome.Web.Domain;
using GoHome.Web.Services;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using static GoHome.Web.Bot.BotTexts;
using User = GoHome.Web.Domain.User;

namespace GoHome.Web.Bot;

/// <summary>
/// Обработка сообщений и нажатий кнопок. Callback data: короткие коды через «:» (лимит Telegram — 64 байта).
/// </summary>
public class BotUpdateHandler(
    ITelegramBotClient bot,
    AppDbContext db,
    TripService trips,
    BookingService booking,
    TripOpsService ops,
    BillingService billing,
    WorkCalendar calendar,
    IClock clock,
    IConfiguration config)
{
    private CancellationToken _ct;

    public async Task HandleAsync(Update update, CancellationToken ct)
    {
        _ct = ct;
        if (update.Message is { } msg) await OnMessageAsync(msg);
        else if (update.CallbackQuery is { } cb) await OnCallbackAsync(cb);
    }

    // ───────────────────────────── сообщения ─────────────────────────────

    private async Task OnMessageAsync(Message msg)
    {
        if (msg.From is null || msg.Chat.Type != ChatType.Private) return;
        var chatId = msg.Chat.Id;

        if (msg.Contact is { } contact)
        {
            await OnContactAsync(msg, contact);
            return;
        }

        var user = await db.Users.SingleOrDefaultAsync(u => u.TelegramId == msg.From.Id, _ct);
        if (user is null)
        {
            await AskContactAsync(chatId);
            return;
        }

        switch (msg.Text?.Trim())
        {
            case BtnRoutes: await ShowRoutesAsync(chatId, null); break;
            case BtnMyTrips: await ShowMyTripsAsync(chatId, null, user); break;
            case BtnBalance: await ShowBalanceAsync(chatId, user); break;
            case BtnDriverTrip when user.Role != UserRole.Passenger: await ShowDriverTripAsync(chatId, null, user, null); break;
            case BtnDriverTomorrow when user.Role != UserRole.Passenger: await ShowDriverTomorrowAsync(chatId, user); break;
            case BtnAllToday when user.Role == UserRole.Admin: await ShowAllTodayAsync(chatId); break;
            default:
                await Send(chatId, Help(user.Role) + AdminLink(user), MainMenu(user));
                break;
        }
    }

    private async Task AskContactAsync(long chatId)
    {
        var kb = new ReplyKeyboardMarkup(KeyboardButton.WithRequestContact("📱 Поделиться номером")) { ResizeKeyboard = true, OneTimeKeyboard = true };
        await Send(chatId, "Здравствуйте! Это бот поездок в офис.\nЧтобы начать, поделитесь номером телефона — по нему мы вас узнаем.", kb);
    }

    private async Task OnContactAsync(Message msg, Contact contact)
    {
        if (contact.UserId != msg.From!.Id)
        {
            await Send(msg.Chat.Id, "Пожалуйста, отправьте свой номер кнопкой ниже.");
            return;
        }

        var phone = Phone.Normalize(contact.PhoneNumber);
        // Водителей и админов заводят в админке заранее по номеру — здесь их привязываем к Telegram.
        var user = await db.Users.SingleOrDefaultAsync(u => u.Phone == phone, _ct)
                   ?? await db.Users.SingleOrDefaultAsync(u => u.TelegramId == msg.From.Id, _ct);
        if (user is null)
        {
            user = new User
            {
                Phone = phone,
                Name = $"{contact.FirstName} {contact.LastName}".Trim(),
                Role = UserRole.Passenger,
                CreatedAt = clock.UtcNow,
            };
            db.Users.Add(user);
        }
        user.TelegramId = msg.From.Id;
        if (string.IsNullOrWhiteSpace(user.Name)) user.Name = $"{contact.FirstName} {contact.LastName}".Trim();
        await db.SaveChangesAsync(_ct);

        await Send(msg.Chat.Id, $"Готово, {H(user.Name)}!\n\n" + Help(user.Role) + AdminLink(user), MainMenu(user));
    }

    private static ReplyKeyboardMarkup MainMenu(User user)
    {
        var rows = new List<KeyboardButton[]>();
        if (user.Role != UserRole.Passenger)
            rows.Add([BtnDriverTrip, BtnDriverTomorrow]);
        if (user.Role == UserRole.Admin)
            rows.Add([BtnAllToday]);
        rows.Add([BtnRoutes, BtnMyTrips]);
        rows.Add([BtnBalance, BtnHelp]);
        return new ReplyKeyboardMarkup(rows) { ResizeKeyboard = true };
    }

    private string AdminLink(User user) =>
        user.Role == UserRole.Admin && config["App:PublicUrl"] is { Length: > 0 } url ? $"\n\nАдминка: {url}" : "";

    // ───────────────────────────── кнопки ─────────────────────────────

    private async Task OnCallbackAsync(CallbackQuery cb)
    {
        var chatId = cb.Message?.Chat.Id ?? cb.From.Id;
        var messageId = cb.Message?.MessageId;
        var user = await db.Users.SingleOrDefaultAsync(u => u.TelegramId == cb.From.Id, _ct);
        if (user is null)
        {
            await bot.AnswerCallbackQuery(cb.Id, cancellationToken: _ct);
            await AskContactAsync(chatId);
            return;
        }

        var p = (cb.Data ?? "").Split(':');
        var actor = $"tg:{user.Id}:{user.Name}";
        string? toast = null;

        switch (p[0])
        {
            // Пассажир: маршруты
            case "rts":
                await ShowRoutesAsync(chatId, messageId);
                break;
            case "rt":
                await ShowRouteAsync(chatId, messageId, int.Parse(p[1]));
                break;
            case "jn":
                await ChooseStopAsync(chatId, messageId, int.Parse(p[1]), "Выберите вашу остановку:", s => $"js:{p[1]}:{s.Id}");
                break;
            case "js":
                await Edit(chatId, messageId, "Какие поездки нужны?", new InlineKeyboardMarkup([
                    [InlineKeyboardButton.WithCallbackData("Утро и вечер", $"jd:{p[1]}:{p[2]}:3")],
                    [InlineKeyboardButton.WithCallbackData("Только утро", $"jd:{p[1]}:{p[2]}:1"),
                     InlineKeyboardButton.WithCallbackData("Только вечер", $"jd:{p[1]}:{p[2]}:2")],
                    [InlineKeyboardButton.WithCallbackData("« Назад", $"rt:{p[1]}")],
                ]));
                break;
            case "jd":
            {
                var flags = int.Parse(p[3]);
                var r = await booking.JoinRouteAsync(user.Id, int.Parse(p[1]), int.Parse(p[2]), (flags & 1) != 0, (flags & 2) != 0, actor);
                await Edit(chatId, messageId, (r.Ok ? "✅ " : "⚠️ ") + H(r.Message));
                break;
            }
            case "oo":
                await ChooseOneOffTripAsync(chatId, messageId, int.Parse(p[1]));
                break;
            case "od":
                await ChooseStopAsync(chatId, messageId, int.Parse(p[1]),
                    $"Разовая поездка {WorkCalendar.Format(ParseD(p[2]))}, {WorkCalendar.Format((Direction)int.Parse(p[3]))}. Ваша остановка:",
                    s => $"os:{p[1]}:{p[2]}:{p[3]}:{s.Id}");
                break;
            case "os":
            {
                var r = await booking.BookOneOffAsync(user.Id, int.Parse(p[1]), int.Parse(p[4]), ParseD(p[2]), (Direction)int.Parse(p[3]), actor);
                await Edit(chatId, messageId, (r.Ok ? "✅ " : "⚠️ ") + H(r.Message));
                break;
            }

            // Пассажир: мои поездки
            case "ab":
                toast = (await booking.MarkAbsentAsync(user.Id, ParseD(p[1]), (Direction)int.Parse(p[2]), actor)).Message;
                await ShowMyTripsAsync(chatId, messageId, user);
                break;
            case "un":
                toast = (await booking.UnmarkAbsentAsync(user.Id, ParseD(p[1]), (Direction)int.Parse(p[2]), actor)).Message;
                await ShowMyTripsAsync(chatId, messageId, user);
                break;
            case "lv":
                await Edit(chatId, messageId, "Точно перестать ездить постоянно? Место освободится для других.", new InlineKeyboardMarkup([
                    [InlineKeyboardButton.WithCallbackData("Да, отписаться", "lv!"), InlineKeyboardButton.WithCallbackData("Нет", "my")],
                ]));
                break;
            case "lv!":
                await Edit(chatId, messageId, H((await booking.LeaveRouteAsync(user.Id, actor)).Message));
                break;
            case "my":
                await ShowMyTripsAsync(chatId, messageId, user);
                break;

            // Водитель
            case "tr" when user.Role != UserRole.Passenger:
                await ShowDriverTripAsync(chatId, messageId, user, (int.Parse(p[1]), ParseD(p[2]), (Direction)int.Parse(p[3])));
                break;
            case "bd" or "ns" when user.Role != UserRole.Passenger:
            {
                var key = (int.Parse(p[1]), ParseD(p[2]), (Direction)int.Parse(p[3]));
                if (!await CanDriveAsync(user, key.Item1)) break;
                var result = p[0] == "bd" ? RideResult.Boarded : RideResult.NoShow;
                toast = (await ops.MarkRideAsync(key.Item1, key.Item2, key.Item3, int.Parse(p[4]), result, user.Id, actor)).Message;
                await ShowDriverTripAsync(chatId, messageId, user, key);
                break;
            }
            case "ar" when user.Role != UserRole.Passenger:
            {
                var key = (int.Parse(p[1]), ParseD(p[2]), (Direction)int.Parse(p[3]));
                if (!await CanDriveAsync(user, key.Item1)) break;
                toast = (await ops.MarkArrivedAsync(key.Item1, key.Item2, key.Item3, actor)).Message;
                await ShowDriverTripAsync(chatId, messageId, user, key);
                break;
            }
            case "cx" when user.Role != UserRole.Passenger:
                await Edit(chatId, messageId, "Отменить рейс? Все пассажиры получат уведомление.", new InlineKeyboardMarkup([
                    [InlineKeyboardButton.WithCallbackData("Да, отменить", $"cx!:{p[1]}:{p[2]}:{p[3]}"),
                     InlineKeyboardButton.WithCallbackData("Нет", $"tr:{p[1]}:{p[2]}:{p[3]}")],
                ]));
                break;
            case "cx!" when user.Role != UserRole.Passenger:
            {
                var key = (int.Parse(p[1]), ParseD(p[2]), (Direction)int.Parse(p[3]));
                if (!await CanDriveAsync(user, key.Item1)) break;
                var (r, _) = await ops.CancelTripAsync(key.Item1, key.Item2, key.Item3, "Отменено водителем", user.Id, actor);
                toast = r.Message;
                await ShowDriverTripAsync(chatId, messageId, user, key);
                break;
            }
            default:
                toast = "Действие недоступно.";
                break;
        }

        await bot.AnswerCallbackQuery(cb.Id, toast is null ? null : Truncate(toast, 190), cancellationToken: _ct);
    }

    // ───────────────────────────── экраны пассажира ─────────────────────────────

    private async Task ShowRoutesAsync(long chatId, int? messageId)
    {
        var routes = await db.Routes.Include(r => r.Car).Include(r => r.Stops)
            .Where(r => r.IsActive).OrderBy(r => r.Name).ToListAsync(_ct);
        var regulars = await db.Memberships.Where(m => m.Status == MembershipStatus.Regular && m.To == null).ToListAsync(_ct);

        var sb = new StringBuilder("<b>Маршруты</b>\n\n");
        var buttons = new List<InlineKeyboardButton[]>();
        foreach (var r in routes)
        {
            var mine = regulars.Where(m => m.RouteId == r.Id).ToList();
            var freeM = r.Car.Seats - mine.Count(m => m.Morning);
            var freeE = r.Car.Seats - mine.Count(m => m.Evening);
            var first = r.Stops.MinBy(s => s.Order);
            sb.AppendLine($"<b>{H(r.Name)}</b> — {Money(r.PricePerRide)} за поездку");
            if (first is not null) sb.AppendLine($"Утром от «{H(first.Name)}» в {first.MorningTime:HH:mm}");
            sb.AppendLine($"Постоянных мест свободно: утро {Math.Max(0, freeM)}, вечер {Math.Max(0, freeE)}");
            sb.AppendLine();
            buttons.Add([InlineKeyboardButton.WithCallbackData(r.Name, $"rt:{r.Id}")]);
        }
        if (routes.Count == 0) sb.AppendLine("Пока нет активных маршрутов.");
        await SendOrEdit(chatId, messageId, sb.ToString(), new InlineKeyboardMarkup(buttons));
    }

    private async Task ShowRouteAsync(long chatId, int? messageId, int routeId)
    {
        var r = await db.Routes.Include(x => x.Car).Include(x => x.Stops).SingleAsync(x => x.Id == routeId, _ct);
        var sb = new StringBuilder($"<b>{H(r.Name)}</b>\n{H(r.Car.Model)} {H(r.Car.Plate)}, мест: {r.Car.Seats}\nЦена: {Money(r.PricePerRide)} за поездку\n\n");
        sb.AppendLine("Остановка — утро / вечер:");
        foreach (var s in r.Stops.OrderBy(s => s.Order))
            sb.AppendLine($"• {H(s.Name)} — {s.MorningTime:HH:mm} / {s.EveningTime:HH:mm}");
        await SendOrEdit(chatId, messageId, sb.ToString(), new InlineKeyboardMarkup([
            [InlineKeyboardButton.WithCallbackData("📌 Ездить постоянно", $"jn:{r.Id}")],
            [InlineKeyboardButton.WithCallbackData("🎫 Разовая поездка", $"oo:{r.Id}")],
            [InlineKeyboardButton.WithCallbackData("« Все маршруты", "rts")],
        ]));
    }

    private async Task ChooseStopAsync(long chatId, int? messageId, int routeId, string title, Func<Stop, string> data)
    {
        var stops = await db.Stops.Where(s => s.RouteId == routeId).OrderBy(s => s.Order).ToListAsync(_ct);
        var rows = stops.Select(s => new[] { InlineKeyboardButton.WithCallbackData($"{s.Name} ({s.MorningTime:HH:mm} / {s.EveningTime:HH:mm})", data(s)) })
            .Append([InlineKeyboardButton.WithCallbackData("« Назад", $"rt:{routeId}")]);
        await Edit(chatId, messageId, title, new InlineKeyboardMarkup(rows));
    }

    private async Task ChooseOneOffTripAsync(long chatId, int? messageId, int routeId)
    {
        var rows = new List<InlineKeyboardButton[]>();
        foreach (var date in calendar.UpcomingChangeableDays())
            foreach (var dir in Enum.GetValues<Direction>())
            {
                var roster = await trips.GetRosterAsync(routeId, date, dir);
                if (roster.Runs && roster.Free > 0)
                    rows.Add([InlineKeyboardButton.WithCallbackData(
                        $"{WorkCalendar.Format(date)}, {WorkCalendar.Format(dir)} — мест: {roster.Free}", $"od:{routeId}:{D(date)}:{(int)dir}")]);
            }
        rows.Add([InlineKeyboardButton.WithCallbackData("« Назад", $"rt:{routeId}")]);
        await Edit(chatId, messageId,
            rows.Count == 1 ? "На ближайшие дни свободных мест нет." : "Выберите рейс (запись закрывается накануне в 21:00):",
            new InlineKeyboardMarkup(rows));
    }

    private async Task ShowMyTripsAsync(long chatId, int? messageId, User user)
    {
        var membership = await db.Memberships.Include(m => m.Route).Include(m => m.Stop)
            .Where(m => m.UserId == user.Id && m.Status != MembershipStatus.Left).FirstOrDefaultAsync(_ct);
        var sb = new StringBuilder();
        if (membership is null)
            sb.AppendLine($"Вы не записаны на постоянный маршрут. Нажмите «{BtnRoutes}».");
        else if (membership.Status == MembershipStatus.Waitlist)
            sb.AppendLine($"Вы в очереди ожидания на «{H(membership.Route.Name)}».");
        else
            sb.AppendLine($"Постоянно: <b>{H(membership.Route.Name)}</b>, «{H(membership.Stop.Name)}» ({(membership.Morning ? "утро" : "")}{(membership.Morning && membership.Evening ? " и " : "")}{(membership.Evening ? "вечер" : "")}), с {WorkCalendar.Format(membership.From)}");
        sb.AppendLine();

        var upcoming = await trips.GetUserUpcomingAsync(user.Id);
        var rows = new List<InlineKeyboardButton[]>();
        if (upcoming.Count == 0) sb.AppendLine("Ближайших поездок нет.");
        foreach (var (t, e) in upcoming)
        {
            var time = t.Direction == Direction.Morning ? e.Stop.MorningTime : e.Stop.EveningTime;
            var label = $"{WorkCalendar.Format(t.Date)}, {WorkCalendar.Format(t.Direction)}";
            var status = t.IsCancelled ? "⛔ рейс отменён" : e.IsAbsent ? "🙅 не еду" : $"✅ {time:HH:mm}, {e.Stop.Name}";
            sb.AppendLine($"{label}: {H(status)}{(e.IsRegular ? "" : " (разово)")}");

            if (t.IsCancelled || !calendar.CanChange(t.Date)) continue;
            rows.Add(e.IsAbsent
                ? [InlineKeyboardButton.WithCallbackData($"↩️ Всё-таки еду: {label}", $"un:{D(t.Date)}:{(int)t.Direction}")]
                : [InlineKeyboardButton.WithCallbackData($"🙅 Не еду: {label}", $"ab:{D(t.Date)}:{(int)t.Direction}")]);
        }
        sb.AppendLine("\nОтметить «не еду» можно до 21:00 накануне.");
        if (membership is not null) rows.Add([InlineKeyboardButton.WithCallbackData("Отписаться от маршрута", "lv")]);
        await SendOrEdit(chatId, messageId, sb.ToString(), new InlineKeyboardMarkup(rows));
    }

    private async Task ShowBalanceAsync(long chatId, User user)
    {
        var b = await billing.GetBalanceAsync(user.Id);
        var rides = await billing.GetRidesAsync(user.Id, clock.Today.AddDays(-13), clock.Today);
        var sb = new StringBuilder();
        sb.AppendLine($"<b>К оплате: {Money(Math.Max(0, b.Due))}</b>");
        if (b.Due < 0) sb.AppendLine($"Переплата: {Money(-b.Due)} — учтём в следующих поездках.");
        sb.AppendLine($"Всего поездок: {b.Rides}, начислено {Money(b.Charged)}, оплачено {Money(b.Paid)}.");
        if (rides.Count > 0)
        {
            sb.AppendLine("\nЗа 2 недели:");
            foreach (var l in rides)
                sb.AppendLine($"{WorkCalendar.Format(l.Date)}, {WorkCalendar.Format(l.Direction)} — {(l.Result == RideResult.Boarded ? "поездка" : "неявка")}, {Money(l.Price)}");
        }
        await Send(chatId, sb.ToString());
    }

    // ───────────────────────────── экраны водителя ─────────────────────────────

    private async Task<Domain.Route?> DriverRouteAsync(User user) =>
        await db.Routes.Include(r => r.Car).FirstOrDefaultAsync(r => r.IsActive && r.Car.DriverUserId == user.Id, _ct);

    private async Task<bool> CanDriveAsync(User user, int routeId) =>
        user.Role == UserRole.Admin || await db.Routes.AnyAsync(r => r.Id == routeId && r.Car.DriverUserId == user.Id, _ct);

    private async Task ShowDriverTripAsync(long chatId, int? messageId, User user, (int RouteId, DateOnly Date, Direction Dir)? key)
    {
        if (key is null)
        {
            var route = await DriverRouteAsync(user);
            if (route is null)
            {
                await Send(chatId, user.Role == UserRole.Admin ? $"За вами не закреплена машина. Используйте «{BtnAllToday}»." : "За вами не закреплена машина. Обратитесь к администратору.");
                return;
            }
            // До 13:00 показываем утренний рейс, после — вечерний.
            key = (route.Id, clock.Today, clock.LocalNow.Hour < 13 ? Direction.Morning : Direction.Evening);
        }

        var (routeId, date, dir) = key.Value;
        var t = await trips.GetRosterAsync(routeId, date, dir);
        var k = $"{routeId}:{D(date)}:{(int)dir}";
        var rows = new List<InlineKeyboardButton[]>();
        if (t.Runs)
        {
            foreach (var e in t.Riding)
                rows.Add([
                    InlineKeyboardButton.WithCallbackData($"{(e.Result == RideResult.Boarded ? "☑️" : "✅")} {e.Name}", $"bd:{k}:{e.UserId}"),
                    InlineKeyboardButton.WithCallbackData($"{(e.Result == RideResult.NoShow ? "☑️" : "❌")} не пришёл", $"ns:{k}:{e.UserId}"),
                ]);
            rows.Add([InlineKeyboardButton.WithCallbackData("🏁 Прибыли", $"ar:{k}"), InlineKeyboardButton.WithCallbackData("⛔ Отменить рейс", $"cx:{k}")]);
        }
        var other = dir == Direction.Morning ? Direction.Evening : Direction.Morning;
        rows.Add([InlineKeyboardButton.WithCallbackData($"↔️ {WorkCalendar.Format(other)}", $"tr:{routeId}:{D(date)}:{(int)other}"),
                  InlineKeyboardButton.WithCallbackData("🔄 Обновить", $"tr:{k}")]);
        await SendOrEdit(chatId, messageId, Roster(t, withPhones: true), new InlineKeyboardMarkup(rows));
    }

    private async Task ShowDriverTomorrowAsync(long chatId, User user)
    {
        var route = await DriverRouteAsync(user);
        if (route is null)
        {
            await Send(chatId, "За вами не закреплена машина.");
            return;
        }
        var date = calendar.Workdays(clock.Today.AddDays(1), clock.Today.AddDays(10)).First();
        var sb = new StringBuilder();
        foreach (var dir in Enum.GetValues<Direction>())
            sb.AppendLine(Roster(await trips.GetRosterAsync(route.Id, date, dir), withPhones: true));
        await Send(chatId, sb.ToString());
    }

    private async Task ShowAllTodayAsync(long chatId)
    {
        var date = calendar.IsWorkday(clock.Today) ? clock.Today : calendar.Workdays(clock.Today, clock.Today.AddDays(10)).First();
        var rows = new List<InlineKeyboardButton[]>();
        var sb = new StringBuilder();
        foreach (var t in await trips.GetDayAsync(date))
        {
            sb.AppendLine($"{H(t.Route.Name)}, {WorkCalendar.Format(t.Direction)}: {(t.IsCancelled ? "отменён" : $"{t.Occupied}/{t.Capacity}")}");
            rows.Add([InlineKeyboardButton.WithCallbackData($"{t.Route.Name}, {WorkCalendar.Format(t.Direction)}", $"tr:{t.Route.Id}:{D(date)}:{(int)t.Direction}")]);
        }
        await Send(chatId, $"<b>Рейсы {WorkCalendar.Format(date)}</b>\n" + sb, new InlineKeyboardMarkup(rows));
    }

    // ───────────────────────────── отправка ─────────────────────────────

    private Task Send(long chatId, string text, ReplyMarkup? markup = null) =>
        bot.SendMessage(chatId, text, parseMode: ParseMode.Html, replyMarkup: markup, cancellationToken: _ct);

    private async Task Edit(long chatId, int? messageId, string text, InlineKeyboardMarkup? markup = null)
    {
        if (messageId is null)
        {
            await Send(chatId, text, markup);
            return;
        }
        try
        {
            await bot.EditMessageText(chatId, messageId.Value, text, parseMode: ParseMode.Html, replyMarkup: markup, cancellationToken: _ct);
        }
        catch (ApiRequestException ex) when (ex.Message.Contains("message is not modified"))
        {
        }
    }

    private Task SendOrEdit(long chatId, int? messageId, string text, InlineKeyboardMarkup markup) =>
        messageId is null ? Send(chatId, text, markup) : Edit(chatId, messageId, text, markup);

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
