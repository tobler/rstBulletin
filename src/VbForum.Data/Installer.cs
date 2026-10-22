using System.Reflection;
using Dapper;
using MySqlConnector;
using VbForum.Core.Text;

namespace VbForum.Data;

public enum SetupStatus
{
    /// <summary>Noch keine DB-Verbindung hinterlegt.</summary>
    NeedsConnection,
    /// <summary>Verbindung steht, aber das Forum wurde noch nicht eingerichtet (vbf_setting leer).</summary>
    NeedsInstall,
    /// <summary>Eingerichtet – die Einrichtungsseite ist gesperrt.</summary>
    Ready,
    /// <summary>Verbindung hinterlegt, DB antwortet aber nicht. Die Einrichtung bleibt gesperrt.</summary>
    DbUnavailable,
}

/// <summary>Ermittelt den Einrichtungsstatus. „Ready“ wird gemerkt und danach nie wieder geprüft.</summary>
public sealed class SetupState(Db db)
{
    private volatile bool _ready;

    public async Task<SetupStatus> GetAsync()
    {
        if (_ready) return SetupStatus.Ready;
        if (!db.IsConfigured) return SetupStatus.NeedsConnection;
        try
        {
            await using var c = await db.OpenAsync();
            var exists = await c.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'vbf_setting'");
            if (exists > 0 && await c.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {Tables.OwnSetting}") > 0)
            {
                _ready = true;
                return SetupStatus.Ready;
            }
            return SetupStatus.NeedsInstall;
        }
        catch (MySqlException) { return SetupStatus.DbUnavailable; }
        catch (InvalidOperationException) { return SetupStatus.DbUnavailable; }
    }
}

/// <summary>Erstinstallation: legt bei Bedarf ein leeres, vBulletin-4-kompatibles Schema samt Grunddaten an.</summary>
public sealed class Installer(Db db, Schema schema, UserRepository users)
{
    public sealed record ExistingInfo(bool HasForumTables, long Users, long Threads, long Posts);

    public async Task<ExistingInfo> InspectAsync()
    {
        await using var c = await db.OpenAsync();
        var has = await c.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME IN @names",
            new { names = new[] { db.T.Prefix + "post", db.T.Prefix + "thread", db.T.Prefix + "user", db.T.Prefix + "forum" } });
        if (has < 4) return new ExistingInfo(false, 0, 0, 0);
        return new ExistingInfo(true,
            await c.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {db.T.User}"),
            await c.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {db.T.Thread}"),
            await c.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {db.T.Post}"));
    }

    /// <summary>Bestehende vB-Datenbank übernehmen: nur die eigenen Tabellen anlegen.</summary>
    public Task AdoptExistingAsync() => schema.EnsureOwnTablesAsync();

    public sealed class NewInstall
    {
        public string AdminName { get; set; } = "";
        public string AdminEmail { get; set; } = "";
        public string AdminPassword { get; set; } = "";
        public string Ip { get; set; } = "";
    }

    /// <summary>Neue, leere Installation: vB-Tabellen, Standardgruppen, eine Kategorie mit Forum und das Admin-Konto.</summary>
    public async Task InstallNewAsync(NewInstall n)
    {
        if ((await InspectAsync()).HasForumTables)
            throw new InvalidOperationException("In dieser Datenbank gibt es bereits Forentabellen.");

        await using (var c = await db.OpenAsync())
        {
            foreach (var stmt in LoadSchema(db.T.Prefix))
                await c.ExecuteAsync(stmt);

            var t = db.T;
            await c.ExecuteAsync($"INSERT INTO {t.Package} (packageid, productid, class) VALUES (1, 'vbulletin', 'vBForum')");
            await c.ExecuteAsync($"INSERT INTO {t.ContentType} (contenttypeid, class, packageid, canplace, cansearch, cantag, canattach, isaggregator) VALUES (1, 'Post', 1, '0', '1', '0', '1', '0')");
            await c.ExecuteAsync($"INSERT INTO {t.Setting} (varname, value, defaultvalue, optioncode, grouptitle, product) VALUES ('showprivateforums', '1', '1', '', 'forumhome', 'vbulletin')");

            // Rechte-Bits wie vB 4 (siehe VbBits.ForumPerm)
            const long guest = 1 | 2 | 4 | 4096 | 524288;
            const long member = guest | 8 | 16 | 32 | 64 | 128 | 256 | 8192 | 16384 | 32768 | 65536 | 131072;
            const long all = 1048575;
            var groups = new (int Id, string Title, string UserTitle, long Perms, long Admin)[]
            {
                (1, "Gäste", "Gast", guest, 0),
                (2, "Registrierte Benutzer", "Mitglied", member, 0),
                (3, "E-Mail noch nicht bestätigt", "", guest, 0),
                (4, "Warten auf Freischaltung", "", guest, 0),
                (5, "Super-Moderatoren", "Super-Moderator", all, 1),
                (6, "Administratoren", "Administrator", all, 3),
                (7, "Moderatoren", "Moderator", all, 1),
                (8, "Gesperrte Benutzer", "Gesperrt", 0, 0),
            };
            foreach (var g in groups)
                await c.ExecuteAsync($"""
                    INSERT INTO {t.UserGroup} (usergroupid, title, description, usertitle, opentag, closetag, forumpermissions, adminpermissions,
                        genericoptions, pmquota, attachlimit)
                    VALUES (@Id, @Title, '', @UserTitle, '', '', @Perms, @Admin, @opts, 100, 0)
                    """, new { g.Id, Title = VbText.ForStorage(g.Title), UserTitle = VbText.ForStorage(g.UserTitle), g.Perms, g.Admin, opts = g.Id == 8 ? 0 : 32 });

            await c.ExecuteAsync($"INSERT INTO {t.UserTitle} (minposts, title) VALUES (0, 'Neues Mitglied'), (50, 'Mitglied'), (500, 'Erfahrenes Mitglied')");

            // active, allowposting, bbcode, images, smilies, icons, countposts, indexposts, forumjump (+ canhavethreads)
            const long baseOpts = 1 | 2 | 64 | 128 | 512 | 1024 | 4096 | 16384 | 65536;
            await c.ExecuteAsync($"""
                INSERT INTO {t.Forum} (forumid, title, title_clean, description, description_clean, options, displayorder, parentid, parentlist, childlist,
                    link, newpostemail, newthreademail, defaultsortfield, defaultsortorder, showprivate)
                VALUES (1, 'Allgemein', 'Allgemein', '', '', @cat, 10, -1, '1,-1', '1,2,-1', '', '', '', 'lastpost', 'desc', 0),
                       (2, 'Willkommen', 'Willkommen', 'Neuigkeiten und Vorstellungen', 'Neuigkeiten und Vorstellungen', @forum, 10, 1, '2,1,-1', '2,-1', '', '', '', 'lastpost', 'desc', 0)
                """, new { cat = baseOpts, forum = baseOpts | 4 });
        }

        await schema.EnsureOwnTablesAsync();
        var adminId = await users.CreateAsync(n.AdminName, n.AdminEmail, n.AdminPassword, 6, n.Ip, syncLegacy: true);
        await using (var c = await db.OpenAsync())
            await c.ExecuteAsync($"INSERT INTO {db.T.Administrator} (userid, adminpermissions) VALUES (@adminId, 0)", new { adminId });
    }

    private static IEnumerable<string> LoadSchema(string prefix)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("VbForum.Data.vb-schema.sql")
                      ?? throw new InvalidOperationException("vb-schema.sql fehlt.");
        using var r = new StreamReader(s);
        var sql = r.ReadToEnd().Replace("{prefix}", prefix);
        return sql.Split(";\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => x.StartsWith("CREATE", StringComparison.OrdinalIgnoreCase));
    }
}
