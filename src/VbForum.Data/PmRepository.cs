using Dapper;
using VbForum.Core.Models;
using VbForum.Core.Php;
using VbForum.Core.Text;

namespace VbForum.Data;

/// <summary>Private Nachrichten im vB-Format: vb_pmtext (Inhalt, einmal) + vb_pm (je Empfänger/Absender eine Zeile).
/// folderid 0 = Posteingang, -1 = Gesendet.</summary>
public sealed class PmRepository(Db db)
{
    private Tables T => db.T;
    public const int Inbox = 0;
    public const int Sent = -1;

    private const string Cols = """
        pm.pmid, pm.pmtextid, pm.userid, pm.folderid, pm.messageread, pm.parentpmid,
        t.fromuserid, t.fromusername, t.title, t.message, t.touserarray, t.dateline, t.allowsmilie
        """;

    public async Task<PagedResult<PrivateMessage>> ListAsync(long userId, int folder, int page, int pageSize)
    {
        await using var c = await db.OpenAsync();
        var p = new { userId, folder, off = (page - 1) * pageSize, size = pageSize };
        var total = await c.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {T.Pm} pm WHERE pm.userid = @userId AND pm.folderid = @folder", p);
        var items = (await c.QueryAsync<PrivateMessage>(
            $"""
            SELECT {Cols} FROM {T.Pm} pm JOIN {T.PmText} t ON t.pmtextid = pm.pmtextid
            WHERE pm.userid = @userId AND pm.folderid = @folder ORDER BY t.dateline DESC LIMIT @off, @size
            """, p)).ToList();
        return new PagedResult<PrivateMessage>(items, total, page, pageSize);
    }

    public async Task<PrivateMessage?> GetAsync(long userId, long pmId)
    {
        await using var c = await db.OpenAsync();
        return await c.QueryFirstOrDefaultAsync<PrivateMessage>(
            $"SELECT {Cols} FROM {T.Pm} pm JOIN {T.PmText} t ON t.pmtextid = pm.pmtextid WHERE pm.pmid = @pmId AND pm.userid = @userId",
            new { userId, pmId });
    }

    public async Task MarkReadAsync(long userId, long pmId)
    {
        await using var c = await db.OpenAsync();
        var n = await c.ExecuteAsync($"UPDATE {T.Pm} SET messageread = 1 WHERE pmid = @pmId AND userid = @userId AND messageread = 0", new { pmId, userId });
        if (n > 0)
        {
            await c.ExecuteAsync($"UPDATE {T.User} SET pmunread = GREATEST(pmunread, 1) - 1 WHERE userid = @userId", new { userId });
            await c.ExecuteAsync($"UPDATE {T.PmReceipt} SET readtime = @now WHERE pmid = @pmId AND readtime = 0", new { pmId, now = ForumTime.Now });
        }
    }

    public async Task DeleteAsync(long userId, IEnumerable<long> pmIds)
    {
        var ids = pmIds.ToArray();
        if (ids.Length == 0) return;
        await using var c = await db.OpenAsync();
        var texts = (await c.QueryAsync<long>($"SELECT pmtextid FROM {T.Pm} WHERE userid = @userId AND pmid IN @ids", new { userId, ids })).ToList();
        await c.ExecuteAsync($"DELETE FROM {T.Pm} WHERE userid = @userId AND pmid IN @ids", new { userId, ids });
        if (texts.Count > 0)
            await c.ExecuteAsync($"DELETE FROM {T.PmText} WHERE pmtextid IN @texts AND NOT EXISTS (SELECT 1 FROM {T.Pm} x WHERE x.pmtextid = {T.PmText}.pmtextid)", new { texts });
        await RecountAsync(c, userId);
    }

    private async Task RecountAsync(MySqlConnector.MySqlConnection c, long userId) =>
        await c.ExecuteAsync($"""
            UPDATE {T.User} SET pmtotal = (SELECT COUNT(*) FROM {T.Pm} WHERE userid = @userId),
              pmunread = (SELECT COUNT(*) FROM {T.Pm} WHERE userid = @userId AND messageread = 0 AND folderid <> -1)
            WHERE userid = @userId
            """, new { userId });

    public sealed record Recipient(long UserId, string Username, int PmTotal, int Quota);

    public async Task<long> SendAsync(long fromUserId, string fromUsername, IReadOnlyList<Recipient> to, string title, string message, long parentPmId)
    {
        await using var c = await db.OpenAsync();
        var now = ForumTime.Now;
        var toArray = PhpUnserializer.Serialize(new Dictionary<string, object?>
        {
            ["cc"] = to.ToDictionary(r => (object)r.UserId, r => (object?)VbText.ForStorage(HtmlUtil.ToVbStoredTitle(r.Username)))
        });
        var textId = await c.ExecuteScalarAsync<long>($"""
            INSERT INTO {T.PmText} (fromuserid, fromusername, title, message, touserarray, iconid, dateline, showsignature, allowsmilie)
            VALUES (@fromUserId, @fromName, @title, @message, @toArray, 0, @now, 1, 1);
            SELECT LAST_INSERT_ID();
            """, new
        {
            fromUserId, fromName = VbText.ForStorage(HtmlUtil.ToVbStoredTitle(fromUsername)),
            title = VbText.ForStorage(HtmlUtil.ToVbStoredTitle(title.Trim())), message = VbText.ForStorage(message.TrimEnd()), toArray, now
        });
        foreach (var r in to)
        {
            await c.ExecuteAsync($"INSERT INTO {T.Pm} (pmtextid, userid, folderid, messageread, parentpmid) VALUES (@textId, @uid, 0, 0, @parentPmId)",
                new { textId, uid = r.UserId, parentPmId });
            await c.ExecuteAsync($"UPDATE {T.User} SET pmtotal = pmtotal + 1, pmunread = pmunread + 1 WHERE userid = @uid", new { uid = r.UserId });
        }
        var sentId = await c.ExecuteScalarAsync<long>($"""
            INSERT INTO {T.Pm} (pmtextid, userid, folderid, messageread, parentpmid) VALUES (@textId, @fromUserId, -1, 1, @parentPmId);
            SELECT LAST_INSERT_ID();
            """, new { textId, fromUserId, parentPmId });
        await c.ExecuteAsync($"UPDATE {T.User} SET pmtotal = pmtotal + 1 WHERE userid = @fromUserId", new { fromUserId });
        return sentId;
    }

    /// <summary>Empfänger aus touserarray lesen (für „Gesendet“-Ansicht).</summary>
    public static List<string> Recipients(string? touserarray)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(touserarray)) return result;
        try
        {
            if (PhpUnserializer.Parse(touserarray) is Dictionary<string, object?> d)
                foreach (var v in d.Values)
                    if (v is Dictionary<string, object?> inner) result.AddRange(inner.Values.Select(x => HtmlUtil.CleanTitle(x?.ToString())));
                    else if (v != null) result.Add(HtmlUtil.CleanTitle(v.ToString()));
        }
        catch { /* ältere Formate ignorieren */ }
        return result;
    }

    public async Task<int> SentLastHourAsync(long userId)
    {
        await using var c = await db.OpenAsync();
        return await c.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {T.PmText} WHERE fromuserid = @userId AND dateline > @t",
            new { userId, t = ForumTime.Now - 3600 });
    }
}

public sealed class SubscriptionRepository(Db db)
{
    private Tables T => db.T;

    public async Task<List<Subscription>> ListAsync(long userId)
    {
        await using var c = await db.OpenAsync();
        return (await c.QueryAsync<Subscription>($"""
            SELECT s.subscribethreadid, s.threadid, s.emailupdate, t.title, t.lastpost, t.lastposter, t.forumid, t.replycount,
                   GREATEST(IFNULL(tr.readtime,0), IFNULL(fr.readtime,0)) AS readtime
            FROM {T.SubscribeThread} s JOIN {T.Thread} t ON t.threadid = s.threadid
            LEFT JOIN {T.ThreadRead} tr ON tr.threadid = s.threadid AND tr.userid = s.userid
            LEFT JOIN {T.ForumRead} fr ON fr.forumid = t.forumid AND fr.userid = s.userid
            WHERE s.userid = @userId AND t.visible = 1 ORDER BY t.lastpost DESC LIMIT 500
            """, new { userId })).ToList();
    }

    public async Task<bool> IsSubscribedAsync(long userId, long threadId)
    {
        if (userId == 0) return false;
        await using var c = await db.OpenAsync();
        return await c.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {T.SubscribeThread} WHERE userid = @userId AND threadid = @threadId", new { userId, threadId }) > 0;
    }

    public async Task SubscribeAsync(long userId, long threadId, bool email)
    {
        await using var c = await db.OpenAsync();
        await c.ExecuteAsync($"""
            INSERT INTO {T.SubscribeThread} (userid, threadid, emailupdate, folderid, canview) VALUES (@userId, @threadId, @e, 0, 1)
            ON DUPLICATE KEY UPDATE emailupdate = VALUES(emailupdate)
            """, new { userId, threadId, e = email ? 1 : 0 });
    }

    public async Task UnsubscribeAsync(long userId, long threadId)
    {
        await using var c = await db.OpenAsync();
        await c.ExecuteAsync($"DELETE FROM {T.SubscribeThread} WHERE userid = @userId AND threadid = @threadId", new { userId, threadId });
    }
}

/// <summary>Einmal-Token (Passwort zurücksetzen, E-Mail bestätigen) und Admin-Protokoll.</summary>
public sealed class TokenRepository(Db db)
{
    public async Task<string> CreateAsync(long userId, string type, string data, TimeSpan validFor)
    {
        var token = Core.Security.VbPassword.RandomToken(32);
        await using var c = await db.OpenAsync();
        await c.ExecuteAsync($"DELETE FROM {Tables.OwnToken} WHERE (userid = @userId AND type = @type) OR expires < @now", new { userId, type, now = ForumTime.Now });
        await c.ExecuteAsync($"INSERT INTO {Tables.OwnToken} (token, userid, type, data, expires) VALUES (@token, @userId, @type, @data, @exp)",
            new { token = Hash(token), userId, type, data, exp = ForumTime.Now + (long)validFor.TotalSeconds });
        return token;
    }

    /// <summary>Löst ein Token ein (einmalig). Gibt (userId, data) zurück oder null.</summary>
    public async Task<(long UserId, string Data)?> ConsumeAsync(string token, string type)
    {
        await using var c = await db.OpenAsync();
        var h = Hash(token);
        var r = await c.QueryFirstOrDefaultAsync<(long, string)?>(
            $"SELECT CAST(userid AS SIGNED), data FROM {Tables.OwnToken} WHERE token = @h AND type = @type AND expires >= @now", new { h, type, now = ForumTime.Now });
        if (r != null) await c.ExecuteAsync($"DELETE FROM {Tables.OwnToken} WHERE token = @h", new { h });
        return r;
    }

    private static string Hash(string token) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

    public async Task LogAsync(ForumUser by, string action, string target, string ip)
    {
        try
        {
            await using var c = await db.OpenAsync();
            await c.ExecuteAsync($"INSERT INTO {Tables.OwnAuditLog} (dateline, userid, username, action, target, ip) VALUES (@now, @uid, @name, @action, @target, @ip)",
                new { now = ForumTime.Now, uid = by.UserId, name = by.Username, action, target = target.Length > 250 ? target[..250] : target, ip });
        }
        catch { /* Protokoll darf nie die Aktion verhindern */ }
    }

    public sealed class AuditEntry
    {
        public long Id { get; set; }
        public long Dateline { get; set; }
        public long UserId { get; set; }
        public string Username { get; set; } = "";
        public string Action { get; set; } = "";
        public string Target { get; set; } = "";
        public string Ip { get; set; } = "";
    }

    public async Task<List<AuditEntry>> AuditAsync(int limit)
    {
        await using var c = await db.OpenAsync();
        return (await c.QueryAsync<AuditEntry>($"SELECT id, dateline, userid, username, action, target, ip FROM {Tables.OwnAuditLog} ORDER BY id DESC LIMIT @limit", new { limit })).ToList();
    }
}
