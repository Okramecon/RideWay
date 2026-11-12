namespace GoHome.Web.Services;

/// <summary>Канал уведомлений отделён от бизнес-логики: сейчас Telegram, потом можно SMS и т.п.</summary>
public interface INotifier
{
    Task SendAsync(long telegramChatId, string text);
}

/// <summary>Используется, когда бот не настроен (нет токена) — уведомления только в лог.</summary>
public class LogNotifier(ILogger<LogNotifier> log) : INotifier
{
    public Task SendAsync(long telegramChatId, string text)
    {
        log.LogInformation("Notify {Chat}: {Text}", telegramChatId, text);
        return Task.CompletedTask;
    }
}
