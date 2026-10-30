using Dapper;
using MySqlConnector;
using VbForum.Core.Text;

namespace VbForum.Data;

/// <summary>
/// Ermahnungen und Verwarnungen wie vBulletin 4:
/// vb_infraction (eine Zeile je Verwarnung; action 0 = aktiv, 1 = abgelaufen, 2 = aufgehoben),
/// vb_infractionlevel (Stufen mit Punkten und Laufzeit; Titel als Phrase „infractionlevel{id}_title“),
/// vb_infractionban (automatische Sperre ab X Punkten/Verwarnungen),
/// vb_user.ipoints (aktive Punkte), .infractions / .warnings (Anzahl), vb_post.infraction (1 = Ermahnung, 2 = Verwarnung).
/// </summary>
public sealed class InfractionService(Db db, BoardCache cache, UserRepository users, PmRepository pms)
{
    private Tables T => db.T;

    public sealed class Level
    {
        public int InfractionLevelId { get; set; }
        public int Points { get; set; }
        public int Expires { get; set; }
        public string Period { get; set; } = "D";
        public int Warning { get; set; }
        public int Extend { get; set; }
        public string Title { get; set; } = "";

        public bool IsWarning => Warning != 0 || Points == 0;
        public long ExpireSeconds => Period == "N" || Expires <= 0 ? 0 : Expires * Period switch { "H" => 3600L, "M" => 30 * 86400L, _ => 86400L };
        public string DurationText => ExpireSeconds == 0 ? "läuft nie ab" : $"{Expires} {Period switch { "H" => Expires == 1 ? "Stunde" : "Stunden", "M" => Expires == 1 ? "Monat" : "Monate", _ => Expires == 1 ? "Tag" : "Tage" }}";
    }

    public sealed class Infraction
    {
        public long InfractionId { get; set; }
        public int InfractionLevelId { get; set; }
        public long PostId { get; set; }
        public long UserId { get; set; }
        public string? Username { get; set; }
        public long WhoAdded { get; set; }
        public string? WhoAddedName { get; set; }
        public int Points { get; set; }
        public long Dateline { get; set; }
        public string Note { get; set; } = "";
        public int Action { get; set; }
        public long ActionDateline { get; set; }
        public long ActionUserId { get; set; }
        public string? ActionUserName { get; set; }
        public string? ActionReason { get; set; }
        public long Expires { get; set; }
        public long ThreadId { get; set; }
        public string? CustomReason { get; set; }
        public string? PostThreadTitle { get; set; }
        public long PostThreadId { get; set; }
        /// <summary>Wird vom Service gesetzt (Titel der Stufe oder eigener Grund).</summary>
        public string Reason { get; set; } = "";

        public bool IsWarning => Points == 0;
        public bool IsActive => Action == 0;
        public string StatusText => Action switch { 0 => "aktiv", 1 => "abgelaufen", 2 => "aufgehoben", _ => "?" };
    }

    // ---------- Stufen ----------

    public async Task<List<Level>> LevelsAsync()
    {
        try
        {
            await using var c = await db.OpenAsync();
            var levels = (await c.QueryAsync<Level>($"""
                SELECT CAST(infractionlevelid AS SIGNED) AS InfractionLevelId, CAST(points AS SIGNED) AS Points, CAST(expires AS SIGNED) AS Expires,
                       period, CAST(warning AS SIGNED) AS Warning, CAST(extend AS SIGNED) AS Extend
                FROM {T.InfractionLevel} ORDER BY points, infractionlevelid
                """)).ToList();
            var titles = await PhraseTitlesAsync(c);
            foreach (var l in levels) l.Title = titles.GetValueOrDefault(l.InfractionLevelId) ?? $"Stufe {l.InfractionLevelId}";
            return levels;
        }
        catch (MySqlException) { return new(); }
    }

    private async Task<Dictionary<int, string>> PhraseTitlesAsync(MySqlConnection c)
    {
        var lang = await c.ExecuteScalarAsync<string?>($"SELECT value FROM {T.Setting} WHERE varname = 'languageid'");
        int.TryParse(lang, out var langId);
        var rows = await c.QueryAsync<(string Var, int Lang, string Text)>(
            $"SELECT varname, languageid, text FROM {T.Phrase} WHERE fieldname = 'infractionlevel' AND varname LIKE 'infractionlevel%\\_title'");
        var result = new Dictionary<int, string>();
        foreach (var g in rows.GroupBy(r => r.Var))
        {
            var id = g.Key["infractionlevel".Length..^"_title".Length];
            if (int.TryParse(id, out var i))
                result[i] = HtmlUtil.CleanTitle(g.OrderByDescending(p => p.Lang == langId).ThenByDescending(p => p.Lang).First().Text);
        }
        return result;
    }

    public async Task<int> SaveLevelAsync(Level l)
    {
        await using var c = await db.OpenAsync();
        var args = new { id = l.InfractionLevelId, points = Math.Max(0, l.Points), expires = Math.Max(0, l.Expires), period = l.Period is "H" or "D" or "M" or "N" ? l.Period : "D", warning = l.Warning != 0 ? 1 : 0, extend = l.Extend != 0 ? 1 : 0 };
        int id = l.InfractionLevelId;
        if (id > 0)
            await c.ExecuteAsync($"UPDATE {T.InfractionLevel} SET points = @points, expires = @expires, period = @period, warning = @warning, extend = @extend WHERE infractionlevelid = @id", args);
        else
            id = await c.ExecuteScalarAsync<int>($"INSERT INTO {T.InfractionLevel} (points, expires, period, warning, extend) VALUES (@points, @expires, @period, @warning, @extend); SELECT LAST_INSERT_ID();", args);
        // Titel als vB-Phrase (vorhandene Zeilen aktualisieren, sonst Standardsprache 0)
        var varname = $"infractionlevel{id}_title";
        var text = VbText.ForStorage(HtmlUtil.ToVbStoredTitle(l.Title.Trim()));
        if (await c.ExecuteAsync($"UPDATE {T.Phrase} SET text = @text, dateline = @now WHERE varname = @varname AND fieldname = 'infractionlevel'", new { text, varname, now = ForumTime.Now }) == 0)
            await c.ExecuteAsync($"""
                INSERT INTO {T.Phrase} (languageid, varname, fieldname, text, product, username, dateline, version)
                VALUES (0, @varname, 'infractionlevel', @text, 'vbulletin', 'rstBulletin', @now, '')
                """, new { text, varname, now = ForumTime.Now });
        return id;
    }

    public async Task DeleteLevelAsync(int id)
    {
        await using var c = await db.OpenAsync();
        await c.ExecuteAsync($"DELETE FROM {T.InfractionLevel} WHERE infractionlevelid = @id", new { id });
        await c.ExecuteAsync($"DELETE FROM {T.Phrase} WHERE varname = @v AND fieldname = 'infractionlevel'", new { v = $"infractionlevel{id}_title" });
    }

    // ---------- Automatische Sperren ----------

    public sealed class BanRule
    {
        public int InfractionBanId { get; set; }
        public int UserGroupId { get; set; } = -1;
        public int BanUserGroupId { get; set; }
        public int Amount { get; set; }
        public string Period { get; set; } = "PERMA";
        public string Method { get; set; } = "points";

        public long? LiftAfterSeconds => ParsePeriod(Period);
        public static long? ParsePeriod(string p)
        {
            if (p == "PERMA") return 0;
            var parts = p.Split('_');
            if (parts.Length != 2 || !int.TryParse(parts[1], out var n)) return null;
            return parts[0] switch { "H" => n * 3600L, "D" => n * 86400L, "M" => n * 30 * 86400L, "Y" => n * 365 * 86400L, _ => null };
        }
        public string PeriodText => Period == "PERMA" ? "dauerhaft" : Period.Split('_') is [var u, var n] ? $"{n} {u switch { "H" => "Std.", "D" => "Tag(e)", "M" => "Monat(e)", "Y" => "Jahr(e)", _ => u }}" : Period;
    }

    public async Task<List<BanRule>> BanRulesAsync()
    {
        try
        {
            await using var c = await db.OpenAsync();
            return (await c.QueryAsync<BanRule>($"""
                SELECT CAST(infractionbanid AS SIGNED) AS InfractionBanId, usergroupid, CAST(banusergroupid AS SIGNED) AS BanUserGroupId,
                       CAST(amount AS SIGNED) AS Amount, period, method FROM {T.InfractionBan} ORDER BY amount
                """)).ToList();
        }
        catch (MySqlException) { return new(); }
    }

    public async Task SaveBanRuleAsync(BanRule r)
    {
        await using var c = await db.OpenAsync();
        var args = new { id = r.InfractionBanId, g = r.UserGroupId, bg = r.BanUserGroupId, amount = Math.Max(1, r.Amount), period = r.Period, method = r.Method == "infractions" ? "infractions" : "points" };
        if (r.InfractionBanId > 0)
            await c.ExecuteAsync($"UPDATE {T.InfractionBan} SET usergroupid = @g, banusergroupid = @bg, amount = @amount, period = @period, method = @method WHERE infractionbanid = @id", args);
        else
            await c.ExecuteAsync($"INSERT INTO {T.InfractionBan} (usergroupid, banusergroupid, amount, period, method) VALUES (@g, @bg, @amount, @period, @method)", args);
    }

    public async Task DeleteBanRuleAsync(int id)
    {
        await using var c = await db.OpenAsync();
        await c.ExecuteAsync($"DELETE FROM {T.InfractionBan} WHERE infractionbanid = @id", new { id });
    }

    // ---------- Verwarnungen lesen ----------

    private string Select => $"""
        SELECT CAST(i.infractionid AS SIGNED) AS InfractionId, CAST(i.infractionlevelid AS SIGNED) AS InfractionLevelId, CAST(i.postid AS SIGNED) AS PostId,
               CAST(i.userid AS SIGNED) AS UserId, u.username AS Username, CAST(i.whoadded AS SIGNED) AS WhoAdded, w.username AS WhoAddedName,
               CAST(i.points AS SIGNED) AS Points, CAST(i.dateline AS SIGNED) AS Dateline, i.note, CAST(i.action AS SIGNED) AS Action,
               CAST(i.actiondateline AS SIGNED) AS ActionDateline, CAST(i.actionuserid AS SIGNED) AS ActionUserId, a.username AS ActionUserName,
               i.actionreason AS ActionReason, CAST(i.expires AS SIGNED) AS Expires, CAST(i.threadid AS SIGNED) AS ThreadId, i.customreason AS CustomReason,
               t.title AS PostThreadTitle, CAST(IFNULL(t.threadid, 0) AS SIGNED) AS PostThreadId
        FROM {T.Infraction} i
        LEFT JOIN {T.User} u ON u.userid = i.userid
        LEFT JOIN {T.User} w ON w.userid = i.whoadded
        LEFT JOIN {T.User} a ON a.userid = i.actionuserid
        LEFT JOIN {T.Post} p ON p.postid = i.postid AND i.postid > 0
        LEFT JOIN {T.Thread} t ON t.threadid = p.threadid
        """;

    private async Task<List<Infraction>> FillAsync(IEnumerable<Infraction> rows)
    {
        var list = rows.ToList();
        var levels = (await LevelsAsync()).ToDictionary(l => l.InfractionLevelId);
        foreach (var i in list)
            i.Reason = !string.IsNullOrWhiteSpace(i.CustomReason) ? HtmlUtil.CleanTitle(i.CustomReason)
                : levels.TryGetValue(i.InfractionLevelId, out var l) ? l.Title
                : i.InfractionLevelId > 0 ? $"Stufe {i.InfractionLevelId}" : "Ohne Angabe";
        return list;
    }

    public async Task<List<Infraction>> ForUserAsync(long userId)
    {
        await using var c = await db.OpenAsync();
        return await FillAsync(await c.QueryAsync<Infraction>($"{Select} WHERE i.userid = @userId ORDER BY i.dateline DESC", new { userId }));
    }

    public async Task<List<Infraction>> RecentAsync(int limit = 100, bool onlyActive = false)
    {
        await using var c = await db.OpenAsync();
        return await FillAsync(await c.QueryAsync<Infraction>($"{Select} {(onlyActive ? "WHERE i.action = 0" : "")} ORDER BY i.dateline DESC LIMIT @limit", new { limit }));
    }

    public async Task<Infraction?> GetAsync(long id)
    {
        await using var c = await db.OpenAsync();
        return (await FillAsync(await c.QueryAsync<Infraction>($"{Select} WHERE i.infractionid = @id", new { id }))).FirstOrDefault();
    }

    public sealed record Summary(long Points, long Infractions, long Warnings, int Active);

    public async Task<Summary> SummaryAsync(long userId)
    {
        await using var c = await db.OpenAsync();
        var u = await c.QueryFirstOrDefaultAsync<(long, long, long)>($"SELECT CAST(ipoints AS SIGNED), CAST(infractions AS SIGNED), CAST(warnings AS SIGNED) FROM {T.User} WHERE userid = @userId", new { userId });
        var active = await c.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {T.Infraction} WHERE userid = @userId AND action = 0", new { userId });
        return new Summary(u.Item1, u.Item2, u.Item3, active);
    }

    // ---------- Rechte ----------

    /// <summary>Darf der Benutzer Ermahnungen/Verwarnungen vergeben?</summary>
    public static bool CanWarn(ForumUser u, BoardSnapshot s) =>
        !u.IsGuest && (u.IsAdmin || ((u.IsSuperMod || new Permissions(s).IsAnyModerator(u)) && s.Settings.ModeratorsCanWarn));

    /// <summary>Darf der Benutzer sperren/entsperren?</summary>
    public static bool CanBan(ForumUser u, BoardSnapshot s) =>
        !u.IsGuest && (u.IsAdmin || ((u.IsSuperMod || new Permissions(s).IsAnyModerator(u)) && s.Settings.ModeratorsCanBan));

    /// <summary>Darf <paramref name="actor"/> diesen Benutzer überhaupt maßregeln? Admins und Super-Moderatoren nie,
    /// Moderatoren nur durch Admins/Super-Moderatoren, sich selbst nie.</summary>
    public static string? CannotTarget(ForumUser actor, ForumUser target, BoardSnapshot s)
    {
        if (target.IsGuest) return "Benutzer nicht gefunden.";
        if (target.UserId == actor.UserId) return "Dich selbst kannst du nicht verwarnen oder sperren.";
        if (target.IsAdmin || target.IsSuperMod) return "Administratoren und Super-Moderatoren können nicht verwarnt oder gesperrt werden.";
        if (new Permissions(s).IsAnyModerator(target) && !actor.IsAdmin && !actor.IsSuperMod) return "Moderatoren können nur von Administratoren verwarnt oder gesperrt werden.";
        return null;
    }

    // ---------- Vergeben / aufheben ----------

    public sealed class GiveRequest
    {
        public long UserId { get; set; }
        public long PostId { get; set; }
        /// <summary>0 = eigener Grund mit Punkten/Laufzeit aus CustomPoints/CustomDays.</summary>
        public int LevelId { get; set; }
        public string CustomReason { get; set; } = "";
        public int CustomPoints { get; set; }
        /// <summary>Laufzeit in Tagen, 0 = läuft nie ab.</summary>
        public int CustomDays { get; set; } = 30;
        /// <summary>Interne Notiz – nur für das Team sichtbar.</summary>
        public string Note { get; set; } = "";
        /// <summary>Zusätzlicher Text in der privaten Nachricht an den Benutzer.</summary>
        public string Message { get; set; } = "";
        public bool SendPm { get; set; } = true;
    }

    public sealed record GiveResult(bool Ok, string? Error = null, bool AutoBanned = false, string? BanText = null);

    public async Task<GiveResult> GiveAsync(ForumUser by, GiveRequest r)
    {
        var snap = await cache.GetAsync();
        var row = await users.GetAsync(r.UserId);
        if (row == null) return new(false, "Benutzer nicht gefunden.");
        var target = ForumUser.From(row, snap);
        if (!CanWarn(by, snap)) return new(false, "Du darfst keine Verwarnungen vergeben.");
        if (CannotTarget(by, target, snap) is { } err) return new(false, err);

        int points; long expireSecs; string reasonTitle; string custom = "";
        if (r.LevelId > 0)
        {
            var level = (await LevelsAsync()).FirstOrDefault(l => l.InfractionLevelId == r.LevelId);
            if (level == null) return new(false, "Diese Verwarnungsstufe gibt es nicht.");
            points = level.IsWarning ? 0 : level.Points;
            expireSecs = level.ExpireSeconds;
            reasonTitle = level.Title;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(r.CustomReason)) return new(false, "Bitte einen Grund angeben.");
            points = Math.Clamp(r.CustomPoints, 0, 1000);
            expireSecs = r.CustomDays > 0 ? r.CustomDays * 86400L : 0;
            reasonTitle = r.CustomReason.Trim();
            custom = VbText.ForStorage(HtmlUtil.ToVbStoredTitle(reasonTitle.Length > 250 ? reasonTitle[..250] : reasonTitle));
        }

        long threadId = 0;
        Core.Models.Post? post = null;
        await using var c = await db.OpenAsync();
        if (r.PostId > 0)
        {
            post = await c.QueryFirstOrDefaultAsync<Core.Models.Post>($"SELECT postid, threadid, userid, title FROM {T.Post} WHERE postid = @id", new { id = r.PostId });
            if (post == null || post.UserId != r.UserId) return new(false, "Der Beitrag gehört nicht zu diesem Benutzer.");
            threadId = post.ThreadId;
        }

        var now = ForumTime.Now;
        var note = VbText.ForStorage(HtmlUtil.ToVbStoredTitle(r.Note.Trim()));
        if (note.Length > 255) note = note[..255];
        await c.ExecuteAsync($"""
            INSERT INTO {T.Infraction} (infractionlevelid, postid, userid, whoadded, points, dateline, note, action, actiondateline, actionuserid, actionreason, expires, threadid, customreason)
            VALUES (@lvl, @pid, @uid, @by, @points, @now, @note, 0, 0, 0, '', @expires, 0, @custom)
            """, new { lvl = r.LevelId > 0 ? r.LevelId : 0, pid = r.PostId, uid = r.UserId, by = by.UserId, points, now, note, expires = expireSecs > 0 ? now + expireSecs : 0, custom });
        if (points > 0)
            await c.ExecuteAsync($"UPDATE {T.User} SET infractions = infractions + 1, ipoints = ipoints + @points WHERE userid = @uid", new { points, uid = r.UserId });
        else
            await c.ExecuteAsync($"UPDATE {T.User} SET warnings = warnings + 1 WHERE userid = @uid", new { uid = r.UserId });
        if (r.PostId > 0)
            await c.ExecuteAsync($"UPDATE {T.Post} SET infraction = GREATEST(infraction, @v) WHERE postid = @pid", new { v = points > 0 ? 2 : 1, pid = r.PostId });

        // Nachricht an den Benutzer (wie vB)
        if (r.SendPm)
        {
            var kind = points > 0 ? "Verwarnung" : "Ermahnung";
            var baseUrl = snap.Settings.BaseUrl.TrimEnd('/');
            var lines = new List<string>
            {
                $"Hallo {target.Username},", "",
                $"du hast eine {kind} erhalten.", "",
                $"[b]Grund:[/b] {reasonTitle}",
            };
            if (post != null)
            {
                var title = await c.ExecuteScalarAsync<string?>($"SELECT title FROM {T.Thread} WHERE threadid = @t", new { t = post.ThreadId });
                lines.Add($"[b]Beitrag:[/b] [url={baseUrl}/post/{post.PostId}]{HtmlUtil.CleanTitle(title)}[/url]");
            }
            if (points > 0)
                lines.Add($"[b]Punkte:[/b] {points}{(expireSecs > 0 ? $" (verfallen am {ForumTime.Full(now + expireSecs)})" : " (verfallen nicht)")}");
            if (!string.IsNullOrWhiteSpace(r.Message)) { lines.Add(""); lines.Add(r.Message.Trim()); }
            lines.Add("");
            lines.Add($"Deine Verwarnungen und Ermahnungen findest du in deinem Benutzerbereich: [url={baseUrl}/account/infractions]Verwarnungen[/url]");
            await pms.SendAsync(by.UserId, by.Username, new[] { new PmRepository.Recipient(target.UserId, target.Username, 0, 0) },
                $"Du hast eine {kind} erhalten", string.Join("\n", lines), 0);
        }

        var ban = await CheckAutoBanAsync(c, target, by.UserId, snap);
        return new(true, null, ban != null, ban);
    }

    /// <summary>Automatische Sperre nach vb_infractionban prüfen. Liefert eine Beschreibung, falls gesperrt wurde.</summary>
    private async Task<string?> CheckAutoBanAsync(MySqlConnection c, ForumUser target, long byId, BoardSnapshot snap)
    {
        if (snap.Settings.BannedGroups.Contains(target.PrimaryGroupId)) return null;
        var rules = await BanRulesAsync();
        if (rules.Count == 0) return null;
        var (pts, cnt) = await c.QueryFirstAsync<(long, long)>($"""
            SELECT CAST(ipoints AS SIGNED), (SELECT COUNT(*) FROM {T.Infraction} WHERE userid = @uid AND action = 0 AND points > 0) FROM {T.User} WHERE userid = @uid
            """, new { uid = target.UserId });
        var hit = rules.Where(r => (r.UserGroupId == -1 || r.UserGroupId == target.PrimaryGroupId) && r.BanUserGroupId > 0 && r.LiftAfterSeconds != null &&
                                   (r.Method == "points" ? pts >= r.Amount : cnt >= r.Amount))
                       .OrderByDescending(r => r.LiftAfterSeconds == 0 ? long.MaxValue : r.LiftAfterSeconds).FirstOrDefault();
        if (hit == null) return null;
        var lift = hit.LiftAfterSeconds!.Value > 0 ? ForumTime.Now + hit.LiftAfterSeconds.Value : 0;
        var reason = $"Automatische Sperre: {(hit.Method == "points" ? $"{pts} Verwarnungspunkte" : $"{cnt} aktive Verwarnungen")}";
        await users.BanAsync(target.UserId, hit.BanUserGroupId, byId, reason, lift);
        return $"{reason} – gesperrt {hit.PeriodText}.";
    }

    public async Task<bool> ReverseAsync(ForumUser by, long infractionId, string reason)
    {
        await using var c = await db.OpenAsync();
        var i = await c.QueryFirstOrDefaultAsync<(long UserId, int Points, int Action, long PostId)?>(
            $"SELECT CAST(userid AS SIGNED), CAST(points AS SIGNED), CAST(action AS SIGNED), CAST(postid AS SIGNED) FROM {T.Infraction} WHERE infractionid = @infractionId", new { infractionId });
        if (i == null || i.Value.Action == 2) return false;
        var (uid, points, action, postId) = i.Value;
        await c.ExecuteAsync($"""
            UPDATE {T.Infraction} SET action = 2, actiondateline = @now, actionuserid = @by, actionreason = @reason WHERE infractionid = @infractionId
            """, new { now = ForumTime.Now, by = by.UserId, reason = VbText.ForStorage(HtmlUtil.ToVbStoredTitle((reason ?? "").Trim())), infractionId });
        if (points > 0)
            await c.ExecuteAsync($"""
                UPDATE {T.User} SET infractions = GREATEST(infractions, 1) - 1{(action == 0 ? ", ipoints = GREATEST(ipoints, @points) - @points" : "")} WHERE userid = @uid
                """, new { points, uid });
        else
            await c.ExecuteAsync($"UPDATE {T.User} SET warnings = GREATEST(warnings, 1) - 1 WHERE userid = @uid", new { uid });
        if (postId > 0)
        {
            var max = await c.ExecuteScalarAsync<int>($"""
                SELECT IFNULL(MAX(CASE WHEN points > 0 THEN 2 ELSE 1 END), 0) FROM {T.Infraction} WHERE postid = @postId AND action <> 2
                """, new { postId });
            await c.ExecuteAsync($"UPDATE {T.Post} SET infraction = @max WHERE postid = @postId", new { max, postId });
        }
        return true;
    }

    /// <summary>Abgelaufene Verwarnungen: Punkte wieder abziehen (vB-Cron „Infractions“). Liefert die Anzahl.</summary>
    public async Task<int> ExpireAsync()
    {
        await using var c = await db.OpenAsync();
        var rows = (await c.QueryAsync<(long Id, long UserId, int Points)>(
            $"SELECT CAST(infractionid AS SIGNED), CAST(userid AS SIGNED), CAST(points AS SIGNED) FROM {T.Infraction} WHERE action = 0 AND expires > 0 AND expires < @now",
            new { now = ForumTime.Now })).ToList();
        foreach (var r in rows)
        {
            await c.ExecuteAsync($"UPDATE {T.Infraction} SET action = 1, actiondateline = @now WHERE infractionid = @id AND action = 0", new { now = ForumTime.Now, id = r.Id });
            if (r.Points > 0)
                await c.ExecuteAsync($"UPDATE {T.User} SET ipoints = GREATEST(ipoints, @p) - @p WHERE userid = @u", new { p = r.Points, u = r.UserId });
        }
        return rows.Count;
    }
}
