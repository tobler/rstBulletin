using System.Globalization;

namespace VbForum.Core.Text;

/// <summary>vB speichert Unix-Timestamps (UTC). Anzeige in der Board-Zeitzone, deutsch.</summary>
public static class ForumTime
{
    private static TimeZoneInfo _tz = FindTz("Europe/Berlin");
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    public static void SetTimeZone(string id) => _tz = FindTz(id);

    private static TimeZoneInfo FindTz(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch { return TimeZoneInfo.Utc; }
    }

    public static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public static DateTime Local(long unix) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime, _tz);

    public static long ToUnix(DateTime local) =>
        new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), _tz)).ToUnixTimeSeconds();

    /// <summary>„heute, 14:51“, „gestern, 22:40“, „vor 3 Tagen“, sonst „12.03.2014“.</summary>
    public static string Relative(long unix, bool withTime = true)
    {
        if (unix <= 0) return "–";
        var t = Local(unix);
        var today = Local(Now).Date;
        var diffDays = (today - t.Date).Days;
        var time = t.ToString("HH:mm", De);
        return diffDays switch
        {
            0 => withTime ? $"heute, {time}" : "heute",
            1 => withTime ? $"gestern, {time}" : "gestern",
            > 1 and < 7 => $"vor {diffDays} Tagen",
            _ => t.ToString("dd.MM.yyyy", De)
        };
    }

    public static string Full(long unix) => unix <= 0 ? "–" : Local(unix).ToString("dd.MM.yyyy, HH:mm", De);
    public static string Date(long unix) => unix <= 0 ? "–" : Local(unix).ToString("dd.MM.yyyy", De);
    public static string Number(long n) => n.ToString("N0", De);

    public static string Compact(long n) => n switch
    {
        >= 1_000_000 => (n / 1_000_000d).ToString("0.#", De) + " Mio.",
        >= 10_000 => (n / 1000d).ToString("0", De) + "k",
        _ => Number(n)
    };
}
