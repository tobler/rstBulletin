using System.Globalization;

namespace VbForum.Core.Settings;

/// <summary>Datenbankverbindung – liegt in App_Data/dbsettings.json (nicht in der DB selbst).</summary>
public sealed class DbSettings
{
    public string Server { get; set; } = "localhost";
    public int Port { get; set; } = 3306;
    public string Database { get; set; } = "";
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    public string TablePrefix { get; set; } = "vb_";
    public bool UseSsl { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Database) && !string.IsNullOrWhiteSpace(User);
}

/// <summary>Board-Einstellungen – Schlüssel/Wert in der eigenen Tabelle vbf_setting.</summary>
public sealed class BoardSettings
{
    public string BoardName { get; set; } = "Forum";
    public string BoardDescription { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    /// <summary>Version des Logos (Bild selbst liegt in vbf_asset).</summary>
    public string LogoFile { get; set; } = "";
    /// <summary>Version des Favicons (Bild selbst liegt in vbf_asset).</summary>
    public string FaviconFile { get; set; } = "";
    public string TimeZone { get; set; } = "Europe/Berlin";

    public bool BoardOffline { get; set; }
    public string OfflineMessage { get; set; } = "Das Forum wird gerade gewartet. Bitte versuche es später noch einmal.";

    public int ThreadsPerPage { get; set; } = 20;
    public int PostsPerPage { get; set; } = 15;
    public int UnreadDays { get; set; } = 30;

    public bool AllowRegistration { get; set; } = true;
    public bool RequireEmailConfirmation { get; set; } = true;
    public int RegisteredGroupId { get; set; } = 2;
    public int AwaitingEmailGroupId { get; set; } = 3;
    public string AdminGroupIds { get; set; } = "6";
    public string SuperModGroupIds { get; set; } = "5";
    public string BannedGroupIds { get; set; } = "8";
    public int GuestGroupId { get; set; } = 1;
    public bool SyncLegacyPassword { get; set; } = true;

    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    public string SmtpUser { get; set; } = "";
    public string SmtpPassword { get; set; } = "";
    public bool SmtpSsl { get; set; } = true;
    /// <summary>„auto“ (Port 465 = SSL, sonst STARTTLS), „ssl“ (implizites TLS), „starttls“ oder „none“.</summary>
    public string SmtpSecurity { get; set; } = "auto";
    public string MailFrom { get; set; } = "";
    /// <summary>Angezeigter Absendername (leer = Name des Forums).</summary>
    public string MailFromName { get; set; } = "";
    /// <summary>Optionale Antwortadresse (Reply-To).</summary>
    public string MailReplyTo { get; set; } = "";

    public int MaxAttachmentKb { get; set; } = 4096;
    public int MaxAttachmentsPerPost { get; set; } = 10;
    public string AttachmentExtensions { get; set; } = "jpg,jpeg,png,gif,webp,pdf,zip,txt";
    public int ThumbnailSize { get; set; } = 400;
    public int AvatarMaxKb { get; set; } = 200;

    public bool ImageProxy { get; set; } = true;
    public int MinSecondsBetweenPosts { get; set; } = 15;

    public bool SmtpConfigured => !string.IsNullOrWhiteSpace(SmtpHost) && !string.IsNullOrWhiteSpace(MailFrom);

    public static IReadOnlySet<int> ParseIds(string s) =>
        s.Split(',', ';', ' ').Select(x => int.TryParse(x.Trim(), out var i) ? i : 0).Where(i => i > 0).ToHashSet();

    public IReadOnlySet<int> AdminGroups => ParseIds(AdminGroupIds);
    public IReadOnlySet<int> SuperModGroups => ParseIds(SuperModGroupIds);
    public IReadOnlySet<int> BannedGroups => ParseIds(BannedGroupIds);

    public IReadOnlySet<string> AllowedExtensions =>
        AttachmentExtensions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.TrimStart('.').ToLowerInvariant()).ToHashSet();

    // --- Serialisierung als Key/Value ---
    public Dictionary<string, string> ToDictionary()
    {
        var d = new Dictionary<string, string>();
        foreach (var p in typeof(BoardSettings).GetProperties().Where(p => p.CanWrite))
            d[p.Name] = Convert.ToString(p.GetValue(this), CultureInfo.InvariantCulture) ?? "";
        return d;
    }

    public static BoardSettings FromDictionary(IDictionary<string, string> d)
    {
        var s = new BoardSettings();
        foreach (var p in typeof(BoardSettings).GetProperties().Where(p => p.CanWrite))
        {
            if (!d.TryGetValue(p.Name, out var raw)) continue;
            try
            {
                object v = p.PropertyType == typeof(bool) ? raw is "True" or "true" or "1"
                    : p.PropertyType == typeof(int) ? int.Parse(raw, CultureInfo.InvariantCulture)
                    : raw;
                p.SetValue(s, v);
            }
            catch (FormatException) { /* Standard behalten */ }
        }
        return s;
    }

    public BoardSettings Clone() => FromDictionary(ToDictionary());
}
