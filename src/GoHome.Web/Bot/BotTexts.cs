using System.Net;
using System.Text;
using GoHome.Web.Domain;
using GoHome.Web.Services;

namespace GoHome.Web.Bot;

public static class BotTexts
{

    public static string H(string s) => WebUtility.HtmlEncode(s);

    public static string Money(decimal m) => $"{m:0} сом";

    public static string Roster(TripRoster t, bool withPhones)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"<b>{H(t.Route.Name)}</b> — {WorkCalendar.Format(t.Date)}, {WorkCalendar.Format(t.Direction)}"
                      + (t.DepartureTime is { } dep ? $", отправление {dep:HH:mm}" : ""));
        if (!t.IsWorkday)
        {
            sb.AppendLine("Нерабочий день — рейса нет.");
            return sb.ToString();
        }
        if (t.IsCancelled)
        {
            sb.AppendLine($"⛔ Рейс отменён. {H(t.Cancellation!.Reason)}");
            return sb.ToString();
        }
        sb.AppendLine($"Едут: {t.Occupied} из {t.Capacity}");
        sb.AppendLine();
        foreach (var e in t.Entries)
        {
            var time = t.Direction == Direction.Morning ? e.Stop.MorningTime : e.Stop.EveningTime;
            var icon = e.IsAbsent ? "🙅" : e.Result switch
            {
                RideResult.Boarded => "✅",
                RideResult.NoShow => "❌",
                _ => "▫️",
            };
            sb.Append($"{icon} {time:HH:mm} {H(e.Stop.Name)} — {H(e.Name)}");
            if (!e.IsRegular) sb.Append(" (разово)");
            if (e.IsAbsent) sb.Append(" — не едет");
            if (withPhones && !e.IsAbsent) sb.Append($" +{e.Phone}");
            sb.AppendLine();
        }
        if (t.Entries.Count == 0) sb.AppendLine("Пассажиров нет.");
        if (t.ArrivedAt is { } a) sb.AppendLine($"\n🏁 Прибыли в {a:HH:mm}");
        return sb.ToString();
    }
}
