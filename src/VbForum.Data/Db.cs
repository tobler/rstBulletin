using System.Text.Json;
using MySqlConnector;
using VbForum.Core.Settings;

namespace VbForum.Data;

/// <summary>Speichert die DB-Verbindung in App_Data/dbsettings.json. Änderungen wirken sofort.</summary>
public sealed class DbSettingsStore
{
    private readonly string _path;
    private DbSettings _current;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public DbSettingsStore(string dataPath)
    {
        Directory.CreateDirectory(dataPath);
        _path = Path.Combine(dataPath, "dbsettings.json");
        _current = File.Exists(_path)
            ? JsonSerializer.Deserialize<DbSettings>(File.ReadAllText(_path)) ?? new DbSettings()
            : new DbSettings();
    }

    public DbSettings Current => _current;
    public event Action? Changed;

    public void Save(DbSettings s)
    {
        File.WriteAllText(_path, JsonSerializer.Serialize(s, Json));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        _current = s;
        Changed?.Invoke();
    }

    public static string BuildConnectionString(DbSettings s) => new MySqlConnectionStringBuilder
    {
        Server = s.Server,
        Port = (uint)s.Port,
        Database = s.Database,
        UserID = s.User,
        Password = s.Password,
        CharacterSet = "utf8mb4",
        SslMode = s.UseSsl ? MySqlSslMode.Required : MySqlSslMode.Preferred,
        ConvertZeroDateTime = true,
        DefaultCommandTimeout = 120,
        ConnectionTimeout = 10,
        Pooling = true,
    }.ConnectionString;
}

/// <summary>Tabellennamen mit vB-Präfix. Eigene Tabellen heißen immer vbf_*.</summary>
public sealed class Tables(string p)
{
    private string N(string t) => $"`{p}{t}`";
    public string Prefix => p;
    public string Post => N("post");
    public string Thread => N("thread");
    public string Forum => N("forum");
    public string User => N("user");
    public string UserField => N("userfield");
    public string UserTextField => N("usertextfield");
    public string UserGroup => N("usergroup");
    public string ForumPermission => N("forumpermission");
    public string Moderator => N("moderator");
    public string Attachment => N("attachment");
    public string FileData => N("filedata");
    public string CustomAvatar => N("customavatar");
    public string Smilie => N("smilie");
    public string BbCode => N("bbcode");
    public string Pm => N("pm");
    public string PmText => N("pmtext");
    public string PmReceipt => N("pmreceipt");
    public string SubscribeThread => N("subscribethread");
    public string ThreadRead => N("threadread");
    public string ForumRead => N("forumread");
    public string DeletionLog => N("deletionlog");
    public string Moderation => N("moderation");
    public string PostParsed => N("postparsed");
    public string EditLog => N("editlog");
    public string PostEditHistory => N("postedithistory");
    public string Setting => N("setting");
    public string Datastore => N("datastore");
    public string ContentType => N("contenttype");
    public string Package => N("package");
    public string Phrase => N("phrase");
    public string UserBan => N("userban");
    public string UserTitle => N("usertitle");
    public string Administrator => N("administrator");
    public string ThreadRedirect => N("threadredirect");
    public string ThreadViews => N("threadviews");

    public const string OwnSetting = "`vbf_setting`";
    public const string OwnUserAuth = "`vbf_userauth`";
    public const string OwnToken = "`vbf_token`";
    public const string OwnAuditLog = "`vbf_auditlog`";
}

public sealed class Db(DbSettingsStore store)
{
    private Tables? _tables;
    private string? _tablesPrefix;

    public bool IsConfigured => store.Current.IsConfigured;

    public Tables T
    {
        get
        {
            var p = store.Current.TablePrefix ?? "";
            if (_tables == null || _tablesPrefix != p) { _tables = new Tables(p); _tablesPrefix = p; }
            return _tables;
        }
    }

    public async Task<MySqlConnection> OpenAsync(CancellationToken ct = default)
    {
        if (!IsConfigured) throw new InvalidOperationException("Die Datenbankverbindung ist noch nicht eingerichtet.");
        var c = new MySqlConnection(DbSettingsStore.BuildConnectionString(store.Current));
        await c.OpenAsync(ct);
        return c;
    }

    public static async Task<(bool Ok, string Message)> TestAsync(DbSettings s)
    {
        try
        {
            await using var c = new MySqlConnection(DbSettingsStore.BuildConnectionString(s));
            await c.OpenAsync();
            await using var cmd = new MySqlCommand($"SELECT COUNT(*) FROM `{s.TablePrefix}post`", c);
            var n = Convert.ToInt64(await cmd.ExecuteScalarAsync());
            await using var v = new MySqlCommand("SELECT VERSION()", c);
            return (true, $"Verbindung OK – Server {await v.ExecuteScalarAsync()}, {n:N0} Beiträge gefunden.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
