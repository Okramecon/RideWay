namespace GoHome.Web.Services;

public class PilotOptions
{
    public const string Section = "Pilot";

    /// <summary>До какого времени накануне можно отметить «не еду» / записаться / отменить разовую поездку.</summary>
    public TimeOnly ChangeDeadline { get; set; } = new(21, 0);

    /// <summary>Брать ли плату за неявку без предупреждения. На старте — нет, но неявки считаются.</summary>
    public bool ChargeNoShow { get; set; }

    /// <summary>На сколько рабочих дней вперёд можно записаться на разовую поездку.</summary>
    public int BookingDaysAhead { get; set; } = 5;

    /// <summary>Нерабочие дни помимо выходных (праздники).</summary>
    public List<DateOnly> Holidays { get; set; } = [];

    /// <summary>Часовой пояс пилота.</summary>
    public string TimeZone { get; set; } = "Asia/Bishkek";
}

public interface IClock
{
    DateTime UtcNow { get; }
    DateTime LocalNow { get; }
    DateOnly Today => DateOnly.FromDateTime(LocalNow);
}

public class SystemClock : IClock
{
    private readonly TimeZoneInfo _tz;

    public SystemClock(Microsoft.Extensions.Options.IOptions<PilotOptions> options)
    {
        try { _tz = TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZone); }
        catch (TimeZoneNotFoundException) { _tz = TimeZoneInfo.CreateCustomTimeZone("UTC+6", TimeSpan.FromHours(6), "UTC+6", "UTC+6"); }
    }

    public DateTime UtcNow => DateTime.UtcNow;
    public DateTime LocalNow => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _tz);
}
