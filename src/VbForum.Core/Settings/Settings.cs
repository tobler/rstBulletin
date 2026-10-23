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

    // ---------- Premium / kostenpflichtige Foren ----------
    /// <summary>Foren (inkl. Unterforen), in denen statt „kein Zugriff“ die Abo-Angebote erscheinen.</summary>
    public string PremiumForumIds { get; set; } = "";
    /// <summary>Text über den Abo-Angeboten (BBCode).</summary>
    public string PremiumText { get; set; } = "Dieser Bereich ist Premium-Mitgliedern vorbehalten. Mit einem Premium-Zugang kannst du alle Beiträge lesen und mitschreiben.";
    /// <summary>Schlüssel für die Zahlungs-API (rstpay). Leer = API abgeschaltet.</summary>
    public string PremiumApiKey { get; set; } = "";

    // rstpay (Zahlungsdienst): Session anfordern + Rückmeldung (Callback)
    public string RstPayBaseUrl { get; set; } = "https://rstpay.de";
    public string RstPayProjectId { get; set; } = "";
    public string RstPayMerchantId { get; set; } = "";
    public string RstPayMerchantKey { get; set; } = "";
    /// <summary>Geheimer Teil der Rückruf-Adresse /rstpay/callback/{token}.</summary>
    public string RstPayCallbackToken { get; set; } = "";
    /// <summary>Optional: nur diese IP-Adressen dürfen die Rückruf-Adresse aufrufen (Komma-getrennt).</summary>
    public string RstPayAllowedIps { get; set; } = "";
    public int RstPayPremiumGroupId { get; set; } = 10;
    /// <summary>Gruppe nach Kündigung/Ablauf (bisher 11 = Free-User).</summary>
    public int RstPayCancelGroupId { get; set; } = 11;
    /// <summary>Gruppe bei Rückbuchung (bisher 8 = gesperrt).</summary>
    public int RstPayChargebackGroupId { get; set; } = 8;
    /// <summary>free_booking: Benutzer aus dieser Gruppe (3 = E-Mail unbestätigt) …</summary>
    public int RstPayFreeFromGroupId { get; set; } = 3;
    /// <summary>… kommen in diese Gruppe (11 = Free-User).</summary>
    public int RstPayFreeGroupId { get; set; } = 11;
    public bool RstPayConfigured => !string.IsNullOrWhiteSpace(RstPayProjectId) && !string.IsNullOrWhiteSpace(RstPayMerchantId) && !string.IsNullOrWhiteSpace(RstPayMerchantKey);
    public IReadOnlySet<int> PremiumForums => ParseIds(PremiumForumIds);
    public int MinSecondsBetweenPosts { get; set; } = 15;

    // ---------- Umfragen & Reputation (Standardwerte wie vBulletin 4) ----------
    public bool PollsEnabled { get; set; } = true;
    public bool ReputationEnabled { get; set; } = true;
    public bool ReputationAllowNegative { get; set; } = true;
    /// <summary>Bewertende Benutzer für den Empfänger sichtbar (vB: anonym).</summary>
    public bool ReputationShowNames { get; set; }
    /// <summary>Feste Stärke für Administratoren (vB adminpower), 0 = normale Berechnung.</summary>
    public int ReputationAdminPower { get; set; } = 5;
    /// <summary>Mindestens so viele Beiträge, um bewerten zu dürfen (vB minreputationpost).</summary>
    public int ReputationMinPosts { get; set; } = 50;
    /// <summary>Mindest-Reputation, um bewerten zu dürfen (vB minreputationcount).</summary>
    public int ReputationMinReputation { get; set; } = 10;
    /// <summary>+1 Stärke je X Beiträge (vB pcpower, 0 = aus).</summary>
    public int ReputationPostsPerPoint { get; set; } = 500;
    /// <summary>+1 Stärke je X Reputationspunkte (vB kppower, 0 = aus).</summary>
    public int ReputationRepPerPoint { get; set; } = 100;
    /// <summary>+1 Stärke je X Tage Mitgliedschaft (vB rdpower, 0 = aus).</summary>
    public int ReputationDaysPerPoint { get; set; } = 365;
    /// <summary>Höchstens so viele Bewertungen in 24 Stunden (vB maxreputationperday).</summary>
    public int ReputationDailyLimit { get; set; } = 10;
    /// <summary>So viele andere Benutzer bewerten, bevor derselbe wieder bewertet werden kann (vB reputationrepeat).</summary>
    public int ReputationSpread { get; set; } = 10;

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
