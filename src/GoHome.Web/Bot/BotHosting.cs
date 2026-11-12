using GoHome.Web.Services;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace GoHome.Web.Bot;

public class BotOptions
{
    public const string Section = "Bot";
    /// <summary>Токен от @BotFather. Пусто — бот выключен, работает только админка.</summary>
    public string Token { get; set; } = "";
    /// <summary>Имя бота без @ (для QR-ссылок). Если пусто — берётся из getMe.</summary>
    public string Username { get; set; } = "";
}

/// <summary>
/// Long polling: публичный адрес для самого бота не нужен.
/// (Mini App при этом открывается по HTTPS-адресу App:PublicUrl.)
/// </summary>
public class BotPollingService(
    ITelegramBotClient bot, IServiceScopeFactory scopes, App.BotInfo info, IConfiguration config, ILogger<BotPollingService> log)
    : BackgroundService, IUpdateHandler
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var me = await bot.GetMe(ct);
                info.Username = me.Username;
                log.LogInformation("Telegram bot @{Username} started", me.Username);
                await SetupMenuButtonAsync(ct);
                await bot.ReceiveAsync(this, new ReceiverOptions
                {
                    AllowedUpdates = [UpdateType.Message, UpdateType.CallbackQuery],
                    DropPendingUpdates = false,
                }, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Bot polling failed, retrying in 10s");
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }
        }
    }

    /// <summary>Кнопка «Меню» в чате открывает Mini App. Telegram принимает только HTTPS-адрес.</summary>
    private async Task SetupMenuButtonAsync(CancellationToken ct)
    {
        var url = BotUpdateHandler.MiniAppUrl(config);
        if (url is null)
        {
            log.LogWarning("App:PublicUrl не HTTPS — кнопка Mini App не будет установлена. Для локальной проверки используйте туннель (см. README).");
            return;
        }
        await bot.SetChatMenuButton(menuButton: new Telegram.Bot.Types.MenuButtonWebApp { Text = "RideWay", WebApp = new Telegram.Bot.Types.WebAppInfo(url) }, cancellationToken: ct);
        log.LogInformation("Mini App menu button set to {Url}", url);
    }

    public async Task HandleUpdateAsync(ITelegramBotClient client, Update update, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<BotUpdateHandler>();
        try
        {
            await handler.HandleAsync(update, ct);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to handle update {UpdateId}", update.Id);
            var chatId = update.Message?.Chat.Id ?? update.CallbackQuery?.Message?.Chat.Id;
            if (chatId is not null)
                await client.SendMessage(chatId, "Что-то пошло не так. Попробуйте ещё раз или напишите администратору.", cancellationToken: ct);
        }
    }

    public Task HandleErrorAsync(ITelegramBotClient client, Exception exception, HandleErrorSource source, CancellationToken ct)
    {
        log.LogWarning(exception, "Telegram error from {Source}", source);
        return exception is ApiRequestException ? Task.CompletedTask : Task.Delay(TimeSpan.FromSeconds(2), ct);
    }
}

public class TelegramNotifier(ITelegramBotClient bot, ILogger<TelegramNotifier> log) : INotifier
{
    public async Task SendAsync(long telegramChatId, string text)
    {
        try
        {
            await bot.SendMessage(telegramChatId, text, parseMode: ParseMode.Html);
        }
        catch (Exception ex)
        {
            // Пользователь мог заблокировать бота — это не должно ломать бизнес-операцию.
            log.LogWarning(ex, "Failed to notify {Chat}", telegramChatId);
        }
    }
}
