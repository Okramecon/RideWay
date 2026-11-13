using GoHome.Web.App;
using GoHome.Web.Data;
using GoHome.Web.Domain;
using GoHome.Web.Services;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using static GoHome.Web.Bot.BotTexts;
using User = GoHome.Web.Domain.User;

namespace GoHome.Web.Bot;

/// <summary>
/// Чат с ботом — только вход и ярлыки. Весь интерфейс — в Mini App (/app):
/// • поделиться номером (регистрация / привязка водителя по номеру);
/// • /start board_&lt;код&gt; — QR в машине, отсканированный обычной камерой;
/// • на любое другое сообщение — кнопка «Открыть RideWay».
/// </summary>
public class BotUpdateHandler(ITelegramBotClient bot, AppDbContext db, TripOpsService ops, IClock clock, IConfiguration config)
{
    private CancellationToken _ct;

    /// <summary>HTTPS-адрес Mini App или null, если App:PublicUrl не настроен (Telegram требует HTTPS).</summary>
    public static string? MiniAppUrl(IConfiguration config) =>
        config["App:PublicUrl"] is { } u && u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? u.TrimEnd('/') + "/app/?v=" + BuildId
            : null;

    /// <summary>Меняется при каждом запуске приложения: новый адрес заставляет Telegram заново загрузить страницу, а не взять её из кэша.</summary>
    private static readonly string BuildId = DateTime.UtcNow.Ticks.ToString("x");

    public async Task HandleAsync(Update update, CancellationToken ct)
    {
        _ct = ct;
        if (update.Message is not { From: not null } msg || msg.Chat.Type != ChatType.Private) return;

        if (msg.Contact is { } contact)
        {
            await OnContactAsync(msg, contact);
            return;
        }

        var user = await db.Users.SingleOrDefaultAsync(u => u.TelegramId == msg.From.Id, _ct);
        if (user is null)
        {
            await AskContactAsync(msg.Chat.Id);
            return;
        }

        if (MiniAppApi.ExtractBoardCode(msg.Text) is { } code && msg.Text!.StartsWith("/start", StringComparison.Ordinal))
        {
            var r = await ops.BoardByCodeAsync(user.Id, code, $"qr:{user.Id}:{user.Name}");
            await Send(msg.Chat.Id, (r.Ok ? "✅ " : "⚠️ ") + H(r.Message), AppButton());
            return;
        }

        await Send(msg.Chat.Id, Welcome(user), AppButton());
    }

    private async Task AskContactAsync(long chatId)
    {
        var kb = new ReplyKeyboardMarkup(KeyboardButton.WithRequestContact("📱 Поделиться номером")) { ResizeKeyboard = true, OneTimeKeyboard = true };
        await Send(chatId, "Здравствуйте! Это RideWay — поездки в офис и обратно.\nЧтобы начать, поделитесь номером телефона — по нему мы вас узнаем.", kb);
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
            user = new User { Phone = phone, Role = UserRole.Passenger, CreatedAt = clock.UtcNow };
            db.Users.Add(user);
        }
        user.TelegramId = msg.From.Id;
        if (string.IsNullOrWhiteSpace(user.Name)) user.Name = $"{contact.FirstName} {contact.LastName}".Trim();
        await db.SaveChangesAsync(_ct);

        // Убираем клавиатуру «поделиться номером» и даём кнопку приложения.
        await Send(msg.Chat.Id, $"Готово, {H(user.Name)}!", new ReplyKeyboardRemove());
        await Send(msg.Chat.Id, Welcome(user), AppButton());
    }

    private string Welcome(User user)
    {
        var text = "Всё управление — в приложении: маршруты, запись, «не еду», оплата"
                   + (user.Role == UserRole.Passenger ? "." : ", а для водителя — список пассажиров и отметки.")
                   + "\nПри посадке отсканируйте QR-код в машине — поездка отметится сама.";
        if (MiniAppUrl(config) is null) text += "\n\n⚠️ Приложение ещё не настроено (нужен HTTPS-адрес App:PublicUrl).";
        if (user.Role == UserRole.Admin && config["App:PublicUrl"] is { Length: > 0 } url) text += $"\n\nАдминка: {url}";
        return text;
    }

    private InlineKeyboardMarkup? AppButton() =>
        MiniAppUrl(config) is { } url
            ? new InlineKeyboardMarkup(InlineKeyboardButton.WithWebApp("🚐 Открыть RideWay", new WebAppInfo(url)))
            : null;

    private Task Send(long chatId, string text, ReplyMarkup? markup = null) =>
        bot.SendMessage(chatId, text, parseMode: ParseMode.Html, replyMarkup: markup, cancellationToken: _ct);
}
