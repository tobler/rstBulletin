using Dapper;
using VbForum.Core.Text;

namespace VbForum.Data;

/// <summary>
/// Reputation wie vBulletin 4: vb_reputation (eine Zeile je Bewertung, eindeutig je Bewerter und Beitrag),
/// vb_user.reputation (Summe) und vb_user.reputationlevelid (Stufe aus vb_reputationlevel).
/// </summary>
public sealed class ReputationService(Db db, BoardCache cache)
{
    private Tables T => db.T;
    private volatile List<Level>? _levels;
    private DateTime _levelsAt;

    public sealed record Level(int Id, int Minimum, string Title);

    /// <summary>Stufen aus vB 4 (deutsche Texte), falls die Datenbank keine eigenen hat.</summary>
    private static readonly (int Min, string Title)[] DefaultLevels =
    {
        (-999999, "ist berüchtigt"), (-50, "kann nur auf Besserung hoffen"), (-10, "hat eine etwas zweifelhafte Vergangenheit"),
        (0, "ist ein unbeschriebenes Blatt"), (10, "ist auf einem guten Weg"), (50, "wird bald berühmt werden"),
        (150, "hat eine beeindruckende Ausstrahlung"), (250, "ist ein Rohdiamant"), (350, "ist einfach richtig nett"),
        (450, "ist ein leuchtendes Vorbild"), (550, "ist bekannt wie ein bunter Hund"), (650, "ist eine wahre Augenweide"),
        (1000, "kann auf vieles stolz sein"), (1500, "hat eine glänzende Zukunft"), (2000, "hat einen Ruf, der ihm vorauseilt"),
    };

    public async Task<List<Level>> LevelsAsync()
    {
        if (_levels != null && DateTime.UtcNow - _levelsAt < TimeSpan.FromMinutes(10)) return _levels;
        var list = new List<Level>();
        try
        {
            await using var c = await db.OpenAsync();
            var rows = (await c.QueryAsync<(int Id, int Min)>($"SELECT CAST(reputationlevelid AS SIGNED), minimumreputation FROM {T.ReputationLevel} ORDER BY minimumreputation")).ToList();
            if (rows.Count > 0)
            {
                // Texte stehen in vB als Phrase „reputation{id}“ (fieldname reputationlevel); bevorzugt die Standardsprache des Boards
                var lang = await c.ExecuteScalarAsync<string?>($"SELECT value FROM {T.Setting} WHERE varname = 'languageid'");
                int.TryParse(lang, out var langId);
                var phrases = (await c.QueryAsync<(string Var, int Lang, string Text)>(
                        $"SELECT varname, languageid, text FROM {T.Phrase} WHERE fieldname = 'reputationlevel'"))
                    .GroupBy(p => p.Var)
                    .ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.Lang == langId).ThenByDescending(p => p.Lang).First().Text);
                foreach (var r in rows)
                    list.Add(new Level(r.Id, r.Min, phrases.TryGetValue($"reputation{r.Id}", out var t) ? HtmlUtil.CleanTitle(t) : Fallback(r.Min)));
            }
        }
        catch (MySqlConnector.MySqlException) { /* Tabellen fehlen → Standardstufen */ }
        if (list.Count == 0) list = DefaultLevels.Select((l, i) => new Level(i + 1, l.Min, l.Title)).ToList();
        _levelsAt = DateTime.UtcNow;
        return _levels = list;
    }

    private static string Fallback(int min) => DefaultLevels.LastOrDefault(l => l.Min <= min).Title ?? DefaultLevels[0].Title;

    public static Level LevelFor(IReadOnlyList<Level> levels, long rep) =>
        levels.LastOrDefault(l => l.Minimum <= rep) ?? levels.First();

    /// <summary>Anzeige wie im vB-Postbit: bis zu 10 Kästchen (je 100 Punkte eines), ab dem sechsten in kräftigerer Farbe.</summary>
    public static (int Count, int Sign) Pips(long rep)
    {
        if (rep == 0) return (1, 0);
        var count = (int)Math.Min(10, Math.Abs(rep) / 100 + 1);
        return (count, rep > 0 ? 1 : -1);
    }

    public sealed class Received
    {
        public long ReputationId { get; set; }
        public long PostId { get; set; }
        public long UserId { get; set; }
        public int Reputation { get; set; }
        public long WhoAdded { get; set; }
        public string? WhoAddedName { get; set; }
        public string? Reason { get; set; }
        public long Dateline { get; set; }
        public long ThreadId { get; set; }
        public string? ThreadTitle { get; set; }
        public int ForumId { get; set; }
        public string DisplayReason => HtmlUtil.CleanTitle(Reason);
    }

    /// <summary>Zuletzt erhaltene Bewertungen.</summary>
    public async Task<List<Received>> ReceivedAsync(long userId, int limit = 50)
    {
        await using var c = await db.OpenAsync();
        return (await c.QueryAsync<Received>($"""
            SELECT CAST(r.reputationid AS SIGNED) AS ReputationId, CAST(r.postid AS SIGNED) AS PostId, CAST(r.userid AS SIGNED) AS UserId, r.reputation,
                   CAST(r.whoadded AS SIGNED) AS WhoAdded, u.username AS WhoAddedName, r.reason, CAST(r.dateline AS SIGNED) AS Dateline,
                   CAST(IFNULL(t.threadid, 0) AS SIGNED) AS ThreadId, t.title AS ThreadTitle, CAST(IFNULL(t.forumid, 0) AS SIGNED) AS ForumId
            FROM {T.Reputation} r
            LEFT JOIN {T.User} u ON u.userid = r.whoadded
            LEFT JOIN {T.Post} p ON p.postid = r.postid
            LEFT JOIN {T.Thread} t ON t.threadid = p.threadid
            WHERE r.userid = @userId
            ORDER BY r.dateline DESC LIMIT @limit
            """, new { userId, limit })).ToList();
    }

    public async Task<(long Reputation, int Positive, int Negative)> SummaryAsync(long userId)
    {
        await using var c = await db.OpenAsync();
        var rep = await c.ExecuteScalarAsync<long>($"SELECT reputation FROM {T.User} WHERE userid = @userId", new { userId });
        var counts = await c.QueryFirstAsync<(long Pos, long Neg)>(
            $"SELECT CAST(IFNULL(SUM(reputation > 0), 0) AS SIGNED), CAST(IFNULL(SUM(reputation < 0), 0) AS SIGNED) FROM {T.Reputation} WHERE userid = @userId", new { userId });
        return (rep, (int)counts.Pos, (int)counts.Neg);
    }

    private sealed class GiverRow
    {
        public long Posts { get; set; }
        public long Reputation { get; set; }
        public long JoinDate { get; set; }
    }

    /// <summary>Stärke einer Bewertung nach vB-Formel (fetch_reppower).</summary>
    public async Task<int> PowerAsync(ForumUser u, bool positive = true)
    {
        var s = (await cache.GetAsync()).Settings;
        if (u.IsGuest) return 0;
        if (u.IsAdmin && s.ReputationAdminPower > 0) return positive ? s.ReputationAdminPower : -s.ReputationAdminPower;
        await using var c = await db.OpenAsync();
        var g = await c.QueryFirstOrDefaultAsync<GiverRow>($"SELECT posts, reputation, joindate FROM {T.User} WHERE userid = @id", new { id = u.UserId });
        if (g == null || g.Posts < s.ReputationMinPosts || g.Reputation < s.ReputationMinReputation) return 0;
        var p = 1;
        if (s.ReputationPostsPerPoint > 0) p += (int)(g.Posts / s.ReputationPostsPerPoint);
        if (s.ReputationRepPerPoint > 0 && g.Reputation > 0) p += (int)(g.Reputation / s.ReputationRepPerPoint);
        if (s.ReputationDaysPerPoint > 0) p += (int)((ForumTime.Now - g.JoinDate) / 86400 / s.ReputationDaysPerPoint);
        return positive ? p : -(p / 2);
    }

    /// <summary>Darf der Benutzer überhaupt bewerten (unabhängig vom Beitrag)?</summary>
    public static bool MayUse(ForumUser u, BoardSnapshot snap) =>
        snap.Settings.ReputationEnabled && !u.IsGuest && !u.IsBanned && u.PrimaryGroupId != snap.Settings.AwaitingEmailGroupId;

    /// <summary>Text zu einem Rückgabecode von GiveAsync (wird als ?rep=code an die Themenseite übergeben).</summary>
    public static string? Message(string? code, Core.Settings.BoardSettings s) => code switch
    {
        "ok" => "Danke – deine Bewertung wurde gespeichert.",
        "perm" => "Du darfst keine Bewertungen abgeben.",
        "noauthor" => "Dieser Beitrag hat keinen Autor, der bewertet werden kann.",
        "self" => "Eigene Beiträge kannst du nicht bewerten.",
        "hidden" => "Dieser Beitrag kann nicht bewertet werden.",
        "noneg" => "Negative Bewertungen sind abgeschaltet.",
        "already" => "Diesen Beitrag hast du bereits bewertet.",
        "daily" => $"Du hast in den letzten 24 Stunden schon {s.ReputationDailyLimit} Bewertungen abgegeben. Bitte versuche es später wieder.",
        "spread" => "Du musst erst andere Benutzer bewerten, bevor du diesen Benutzer erneut bewerten kannst.",
        "power" => $"Deine Bewertung hätte noch kein Gewicht. Dafür brauchst du mindestens {s.ReputationMinPosts} Beiträge und {s.ReputationMinReputation} Reputationspunkte.",
        _ => null,
    };

    /// <summary>Beiträge (aus der Liste), die der Benutzer schon bewertet hat.</summary>
    public async Task<HashSet<long>> RatedAsync(long userId, IReadOnlyCollection<long> postIds)
    {
        if (userId <= 0 || postIds.Count == 0) return new();
        await using var c = await db.OpenAsync();
        return (await c.QueryAsync<long>($"SELECT CAST(postid AS SIGNED) FROM {T.Reputation} WHERE whoadded = @userId AND postid IN @postIds", new { userId, postIds })).ToHashSet();
    }

    /// <summary>Bewertung abgeben. Liefert null bei Erfolg, sonst einen Fehlercode (siehe Message).</summary>
    public async Task<string?> GiveAsync(ForumUser u, Core.Models.Post post, bool positive, string? reason)
    {
        var snap = await cache.GetAsync();
        var s = snap.Settings;
        if (!MayUse(u, snap)) return "perm";
        if (post.UserId <= 0) return "noauthor";
        if (post.UserId == u.UserId) return "self";
        if (post.Visible != 1) return "hidden";
        if (!positive && !s.ReputationAllowNegative && !u.IsAdmin) return "noneg";
        reason = (reason ?? "").Trim();
        if (reason.Length > 250) reason = reason[..250];

        await using var c = await db.OpenAsync();
        if (await c.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {T.Reputation} WHERE whoadded = @uid AND postid = @pid", new { uid = u.UserId, pid = post.PostId }) > 0)
            return "already";
        if (!u.IsAdmin && s.ReputationDailyLimit > 0 &&
            await c.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {T.Reputation} WHERE whoadded = @uid AND dateline > @since", new { uid = u.UserId, since = ForumTime.Now - 86400 }) >= s.ReputationDailyLimit)
            return "daily";
        if (!u.IsAdmin && s.ReputationSpread > 0)
        {
            var recent = await c.QueryAsync<long>($"SELECT CAST(userid AS SIGNED) FROM {T.Reputation} WHERE whoadded = @uid ORDER BY dateline DESC LIMIT @n", new { uid = u.UserId, n = s.ReputationSpread });
            if (recent.Contains(post.UserId)) return "spread";
        }
        var power = await PowerAsync(u, positive);
        if (power == 0)
            return "power";

        await c.ExecuteAsync($"""
            INSERT INTO {T.Reputation} (postid, userid, reputation, whoadded, reason, dateline)
            VALUES (@pid, @to, @power, @uid, @reason, @now)
            """, new { pid = post.PostId, to = post.UserId, power, uid = u.UserId, reason = VbText.ForStorage(HtmlUtil.ToVbStoredTitle(reason)), now = ForumTime.Now });
        await UpdateUserAsync(c, post.UserId, power);
        return null;
    }

    public async Task<Received?> GetAsync(long reputationId)
    {
        await using var c = await db.OpenAsync();
        return await c.QueryFirstOrDefaultAsync<Received>($"""
            SELECT CAST(reputationid AS SIGNED) AS ReputationId, CAST(postid AS SIGNED) AS PostId, CAST(userid AS SIGNED) AS UserId, reputation,
                   CAST(whoadded AS SIGNED) AS WhoAdded, reason, CAST(dateline AS SIGNED) AS Dateline
            FROM {T.Reputation} WHERE reputationid = @reputationId
            """, new { reputationId });
    }

    /// <summary>Bewertung löschen (Admin) – die Punkte werden beim Empfänger wieder abgezogen.</summary>
    public async Task DeleteAsync(Received r)
    {
        await using var c = await db.OpenAsync();
        if (await c.ExecuteAsync($"DELETE FROM {T.Reputation} WHERE reputationid = @id", new { id = r.ReputationId }) > 0)
            await UpdateUserAsync(c, r.UserId, -r.Reputation);
    }

    /// <summary>Reputations-Einstellungen aus der alten vB-Konfiguration (vb_setting) übernehmen. Liefert die Anzahl gefundener Werte.</summary>
    public async Task<int> ImportLegacySettingsAsync(Core.Settings.BoardSettings target)
    {
        Dictionary<string, string> v;
        try
        {
            await using var c = await db.OpenAsync();
            v = (await c.QueryAsync<(string K, string V)>($"""
                SELECT varname, value FROM {T.Setting}
                WHERE varname IN ('reputationenable','adminpower','minreputationpost','minreputationcount','pcpower','kppower','rdpower','maxreputationperday','reputationrepeat')
                """)).ToDictionary(x => x.K, x => x.V ?? "");
        }
        catch (MySqlConnector.MySqlException) { return 0; }
        int I(string k, int def) => v.TryGetValue(k, out var x) && int.TryParse(x, out var n) ? n : def;
        if (v.ContainsKey("reputationenable")) target.ReputationEnabled = I("reputationenable", 1) != 0;
        target.ReputationAdminPower = I("adminpower", target.ReputationAdminPower);
        target.ReputationMinPosts = I("minreputationpost", target.ReputationMinPosts);
        target.ReputationMinReputation = I("minreputationcount", target.ReputationMinReputation);
        target.ReputationPostsPerPoint = I("pcpower", target.ReputationPostsPerPoint);
        target.ReputationRepPerPoint = I("kppower", target.ReputationRepPerPoint);
        target.ReputationDaysPerPoint = I("rdpower", target.ReputationDaysPerPoint);
        target.ReputationDailyLimit = I("maxreputationperday", target.ReputationDailyLimit);
        target.ReputationSpread = I("reputationrepeat", target.ReputationSpread);
        return v.Count;
    }

    private async Task UpdateUserAsync(MySqlConnector.MySqlConnection c, long userId, int delta)
    {
        var rep = await c.ExecuteScalarAsync<long>($"SELECT reputation FROM {T.User} WHERE userid = @userId", new { userId }) + delta;
        var level = LevelFor(await LevelsAsync(), rep);
        await c.ExecuteAsync($"UPDATE {T.User} SET reputation = @rep, reputationlevelid = @lvl WHERE userid = @userId", new { rep, lvl = level.Id, userId });
    }
}
