using Dapper;
using VbForum.Core.Models;
using VbForum.Core.Security;
using VbForum.Core.Text;

namespace VbForum.Data;

public sealed class UserRepository(Db db)
{
    private Tables T => db.T;

    private string Select => $"""
        SELECT u.userid, u.usergroupid, u.membergroupids, u.displaygroupid, u.username, u.password, u.salt, u.email,
               u.homepage, u.usertitle, u.customtitle, u.joindate, u.lastvisit, u.lastactivity, u.lastpost, u.lastpostid,
               u.posts, u.options, u.birthday, u.ipaddress, u.autosubscribe, u.pmtotal, u.pmunread, u.avatarrevision,
               (SELECT COUNT(*) FROM {T.CustomAvatar} ca WHERE ca.userid = u.userid AND ca.visible = 1) AS hascustomavatar,
               a.hash AS modernhash
        FROM {T.User} u LEFT JOIN {Tables.OwnUserAuth} a ON a.userid = u.userid
        """;

    public async Task<UserRow?> GetAsync(long userId)
    {
        await using var c = await db.OpenAsync();
        return await c.QueryFirstOrDefaultAsync<UserRow>($"{Select} WHERE u.userid = @userId", new { userId });
    }

    /// <summary>vB legt Benutzernamen HTML-kodiert ab; gesucht wird in beiden Formen.</summary>
    public async Task<UserRow?> GetByNameAsync(string name)
    {
        await using var c = await db.OpenAsync();
        var stored = VbText.ForStorage(HtmlUtil.ToVbStoredTitle(name.Trim()));
        return await c.QueryFirstOrDefaultAsync<UserRow>($"{Select} WHERE u.username = @stored OR u.username = @plain LIMIT 1",
            new { stored, plain = name.Trim() });
    }

    public async Task<List<UserRow>> GetByEmailAsync(string email)
    {
        await using var c = await db.OpenAsync();
        return (await c.QueryAsync<UserRow>($"{Select} WHERE u.email = @email", new { email = email.Trim() })).ToList();
    }

    public async Task<bool> NameExistsAsync(string name, long exceptUserId = 0)
    {
        await using var c = await db.OpenAsync();
        var stored = VbText.ForStorage(HtmlUtil.ToVbStoredTitle(name.Trim()));
        return await c.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {T.User} WHERE (username = @stored OR username = @plain) AND userid <> @exceptUserId",
            new { stored, plain = name.Trim(), exceptUserId }) > 0;
    }

    public async Task<bool> EmailExistsAsync(string email, long exceptUserId = 0)
    {
        await using var c = await db.OpenAsync();
        return await c.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {T.User} WHERE email = @email AND userid <> @exceptUserId",
            new { email = email.Trim(), exceptUserId }) > 0;
    }

    /// <summary>Speichert das Passwort modern (vbf_userauth) und – falls gewünscht – zusätzlich im vB-Format,
    /// damit das alte Board parallel weiter funktioniert.</summary>
    public async Task SetPasswordAsync(long userId, string password, bool syncLegacy)
    {
        await using var c = await db.OpenAsync();
        var hash = VbPassword.HashModern(password);
        await c.ExecuteAsync($"REPLACE INTO {Tables.OwnUserAuth} (userid, hash, updated) VALUES (@userId, @hash, @now)",
            new { userId, hash, now = ForumTime.Now });
        if (syncLegacy)
        {
            var (lh, salt) = VbPassword.CreateLegacy(password);
            await c.ExecuteAsync($"UPDATE {T.User} SET password = @lh, salt = @salt, passworddate = CURDATE() WHERE userid = @userId",
                new { lh, salt, userId });
        }
        else
        {
            // Altes Passwort unbrauchbar machen, damit es nicht mehr gilt
            await c.ExecuteAsync($"UPDATE {T.User} SET password = '', passworddate = CURDATE() WHERE userid = @userId", new { userId });
        }
    }

    public async Task SetModernHashAsync(long userId, string modernHash)
    {
        await using var c = await db.OpenAsync();
        await c.ExecuteAsync($"REPLACE INTO {Tables.OwnUserAuth} (userid, hash, updated) VALUES (@userId, @modernHash, @now)",
            new { userId, modernHash, now = ForumTime.Now });
    }

    public async Task TouchAsync(long userId, string ip, bool newVisit)
    {
        await using var c = await db.OpenAsync();
        var now = ForumTime.Now;
        if (newVisit)
            await c.ExecuteAsync($"UPDATE {T.User} SET lastvisit = lastactivity, lastactivity = @now, ipaddress = @ip WHERE userid = @userId", new { userId, now, ip });
        else
            await c.ExecuteAsync($"UPDATE {T.User} SET lastactivity = @now WHERE userid = @userId", new { userId, now });
    }

    // ---------- Profil ----------

    public async Task<Dictionary<string, string>> ProfileFieldsAsync(long userId, IEnumerable<string> fields)
    {
        var cols = fields.Where(f => System.Text.RegularExpressions.Regex.IsMatch(f, "^field[0-9]+$")).ToList();
        if (cols.Count == 0) return new();
        await using var c = await db.OpenAsync();
        var row = await c.QueryFirstOrDefaultAsync($"SELECT {string.Join(",", cols)} FROM {T.UserField} WHERE userid = @userId", new { userId }) as IDictionary<string, object?>;
        return cols.ToDictionary(k => k, k => HtmlUtil.DecodeNumericEntities(row?[k]?.ToString() ?? ""));
    }

    public async Task SaveProfileAsync(long userId, string homepage, string signature, IDictionary<string, string> fields)
    {
        await using var c = await db.OpenAsync();
        await c.ExecuteAsync($"UPDATE {T.User} SET homepage = @hp WHERE userid = @userId", new { userId, hp = VbText.ForStorage(homepage) });
        await c.ExecuteAsync($"""
            INSERT INTO {T.UserTextField} (userid, signature) VALUES (@userId, @sig)
            ON DUPLICATE KEY UPDATE signature = VALUES(signature)
            """, new { userId, sig = VbText.ForStorage(signature) });
        foreach (var (k, v) in fields)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(k, "^field[0-9]+$")) continue;
            await c.ExecuteAsync($"UPDATE {T.UserField} SET {k} = @v WHERE userid = @userId", new { userId, v = VbText.ForStorage(v) });
        }
    }

    public async Task<string> SignatureAsync(long userId)
    {
        await using var c = await db.OpenAsync();
        return await c.ExecuteScalarAsync<string?>($"SELECT signature FROM {T.UserTextField} WHERE userid = @userId", new { userId }) ?? "";
    }

    public async Task SetEmailAsync(long userId, string email)
    {
        await using var c = await db.OpenAsync();
        await c.ExecuteAsync($"UPDATE {T.User} SET email = @email WHERE userid = @userId", new { userId, email = email.Trim() });
    }

    public async Task SetAutoSubscribeAsync(long userId, int mode)
    {
        await using var c = await db.OpenAsync();
        await c.ExecuteAsync($"UPDATE {T.User} SET autosubscribe = @mode WHERE userid = @userId", new { userId, mode });
    }

    // ---------- Avatar ----------

    public async Task<(byte[] Data, long Dateline, string Filename)?> AvatarAsync(long userId, bool thumb)
    {
        await using var c = await db.OpenAsync();
        var r = await c.QueryFirstOrDefaultAsync<(byte[]?, byte[]?, long, string)>(
            $"SELECT filedata, filedata_thumb, CAST(dateline AS SIGNED), filename FROM {T.CustomAvatar} WHERE userid = @userId AND visible = 1", new { userId });
        var data = thumb && r.Item2 is { Length: > 0 } ? r.Item2 : r.Item1;
        return data is { Length: > 0 } ? (data, r.Item3, r.Item4) : null;
    }

    public async Task SetAvatarAsync(long userId, byte[] full, int w, int h, byte[] thumb, int tw, int th, string ext)
    {
        await using var c = await db.OpenAsync();
        await c.ExecuteAsync($"""
            REPLACE INTO {T.CustomAvatar} (userid, filedata, dateline, filename, visible, filesize, width, height, filedata_thumb, width_thumb, height_thumb)
            VALUES (@userId, @full, @now, @name, 1, @size, @w, @h, @thumb, @tw, @th)
            """, new { userId, full, now = ForumTime.Now, name = "avatar" + userId + "_1." + ext, size = full.Length, w, h, thumb, tw, th });
        await c.ExecuteAsync($"UPDATE {T.User} SET avatarrevision = avatarrevision + 1, avatarid = 0 WHERE userid = @userId", new { userId });
    }

    public async Task RemoveAvatarAsync(long userId)
    {
        await using var c = await db.OpenAsync();
        await c.ExecuteAsync($"DELETE FROM {T.CustomAvatar} WHERE userid = @userId", new { userId });
        await c.ExecuteAsync($"UPDATE {T.User} SET avatarrevision = avatarrevision + 1 WHERE userid = @userId", new { userId });
    }

    // ---------- Anlegen ----------

    public async Task<long> CreateAsync(string username, string email, string password, int groupId, string ip, bool syncLegacy)
    {
        await using var c = await db.OpenAsync();
        var now = ForumTime.Now;
        var (legacyHash, salt) = syncLegacy ? VbPassword.CreateLegacy(password) : ("", VbPassword.CreateSalt());
        // Optionen wie beim zuletzt registrierten Mitglied übernehmen (vB-Standardoptionen der Installation)
        var options = await c.ExecuteScalarAsync<long?>($"SELECT options FROM {T.User} WHERE usergroupid = @groupId ORDER BY userid DESC LIMIT 1", new { groupId }) ?? 45108311;
        var title = await c.ExecuteScalarAsync<string?>($"SELECT title FROM {T.UserTitle} WHERE minposts <= 0 ORDER BY minposts DESC LIMIT 1") ?? "";
        var userId = await c.ExecuteScalarAsync<long>(
            $"""
            INSERT INTO {T.User} (usergroupid, username, password, passworddate, email, usertitle, joindate, lastvisit, lastactivity,
                options, ipaddress, salt, fbuserid, fbname, fbaccesstoken, autosubscribe, showvbcode, timezoneoffset)
            VALUES (@groupId, @name, @legacyHash, CURDATE(), @email, @title, @now, @now, @now, @options, @ip, @salt, '', '', '', 1, 2, '1');
            SELECT LAST_INSERT_ID();
            """,
            new { groupId, name = VbText.ForStorage(HtmlUtil.ToVbStoredTitle(username.Trim())), legacyHash, email = email.Trim(), title, now, options, ip, salt });
        await c.ExecuteAsync($"INSERT IGNORE INTO {T.UserTextField} (userid) VALUES (@userId)", new { userId });
        try
        {
            // vb_userfield hat je Installation andere NOT-NULL-Spalten → alle fieldN mit '' füllen
            var cols = (await c.QueryAsync<string>(
                "SELECT COLUMN_NAME FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @n AND COLUMN_NAME <> 'userid'",
                new { n = T.Prefix + "userfield" })).ToList();
            var sql = cols.Count == 0
                ? $"INSERT IGNORE INTO {T.UserField} (userid) VALUES (@userId)"
                : $"INSERT IGNORE INTO {T.UserField} (userid, {string.Join(",", cols.Select(x => $"`{x}`"))}) VALUES (@userId, {string.Join(",", cols.Select(_ => "''"))})";
            await c.ExecuteAsync(sql, new { userId });
        }
        catch { await c.ExecuteAsync($"INSERT IGNORE INTO {T.UserField} (userid) VALUES (@userId)", new { userId }); }
        await c.ExecuteAsync($"REPLACE INTO {Tables.OwnUserAuth} (userid, hash, updated) VALUES (@userId, @hash, @now)",
            new { userId, hash = VbPassword.HashModern(password), now });
        return userId;
    }

    // ---------- Admin ----------

    public sealed class AdminQuery
    {
        public string? Name { get; set; }
        public string? Email { get; set; }
        public string? Ip { get; set; }
        public int? GroupId { get; set; }
        public long? MinPosts { get; set; }
        public long? MaxPosts { get; set; }
        public long? JoinedBefore { get; set; }
        public long? JoinedAfter { get; set; }
        public long? InactiveSince { get; set; }
        public string Sort { get; set; } = "joindate";
        public bool Desc { get; set; } = true;
        public int Page { get; set; } = 1;
        public int PageSize { get; set; } = 50;
    }

    private static (string Where, object Param) AdminWhere(AdminQuery q)
    {
        var w = new List<string> { "1=1" };
        if (!string.IsNullOrWhiteSpace(q.Name)) w.Add("(u.username LIKE @name OR u.username LIKE @nameStored)");
        if (!string.IsNullOrWhiteSpace(q.Email)) w.Add("u.email LIKE @email");
        if (!string.IsNullOrWhiteSpace(q.Ip)) w.Add("u.ipaddress LIKE @ip");
        if (q.GroupId.HasValue) w.Add("(u.usergroupid = @gid OR FIND_IN_SET(@gid, u.membergroupids))");
        if (q.MinPosts.HasValue) w.Add("u.posts >= @minp");
        if (q.MaxPosts.HasValue) w.Add("u.posts <= @maxp");
        if (q.JoinedBefore.HasValue) w.Add("u.joindate < @jb");
        if (q.JoinedAfter.HasValue) w.Add("u.joindate >= @ja");
        if (q.InactiveSince.HasValue) w.Add("u.lastactivity < @inact");
        var name = (q.Name ?? "").Trim();
        return (string.Join(" AND ", w), new
        {
            name = "%" + name + "%",
            nameStored = "%" + VbText.ForStorage(HtmlUtil.ToVbStoredTitle(name)) + "%",
            email = "%" + (q.Email ?? "").Trim() + "%",
            ip = (q.Ip ?? "").Trim() + "%",
            gid = q.GroupId, minp = q.MinPosts, maxp = q.MaxPosts, jb = q.JoinedBefore, ja = q.JoinedAfter, inact = q.InactiveSince,
            off = (q.Page - 1) * q.PageSize, size = q.PageSize
        });
    }

    public async Task<PagedResult<UserRow>> AdminSearchAsync(AdminQuery q)
    {
        var (where, p) = AdminWhere(q);
        var order = q.Sort switch
        {
            "username" => "u.username", "posts" => "u.posts", "lastactivity" => "u.lastactivity", "email" => "u.email", _ => "u.joindate"
        } + (q.Desc ? " DESC" : " ASC");
        await using var c = await db.OpenAsync();
        var total = await c.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {T.User} u WHERE {where}", p);
        var items = (await c.QueryAsync<UserRow>($"{Select} WHERE {where} ORDER BY {order} LIMIT @off, @size", p)).ToList();
        return new PagedResult<UserRow>(items, total, q.Page, q.PageSize);
    }

    public async Task<List<long>> AdminSearchIdsAsync(AdminQuery q, int max)
    {
        var (where, p) = AdminWhere(q);
        await using var c = await db.OpenAsync();
        return (await c.QueryAsync<long>($"SELECT u.userid FROM {T.User} u WHERE {where} ORDER BY u.userid LIMIT {max}", p)).ToList();
    }

    public async Task AdminUpdateAsync(long userId, string username, string email, int groupId, string memberGroupIds, string userTitle,
        bool customTitle, string homepage, long posts)
    {
        await using var c = await db.OpenAsync();
        var storedName = VbText.ForStorage(HtmlUtil.ToVbStoredTitle(username.Trim()));
        var old = await c.ExecuteScalarAsync<string>($"SELECT username FROM {T.User} WHERE userid = @userId", new { userId });
        await c.ExecuteAsync($"""
            UPDATE {T.User} SET username = @storedName, email = @email, usergroupid = @groupId, membergroupids = @memberGroupIds,
              usertitle = @title, customtitle = @ct, homepage = @hp, posts = @posts WHERE userid = @userId
            """, new
        {
            userId, storedName, email = email.Trim(), groupId, memberGroupIds, title = VbText.ForStorage(userTitle), ct = customTitle ? 1 : 0,
            hp = VbText.ForStorage(homepage), posts
        });
        if (old != null && old != storedName)
        {
            // Namen in denormalisierten Spalten nachziehen (wie vB beim Umbenennen)
            await c.ExecuteAsync($"UPDATE {T.Post} SET username = @storedName WHERE userid = @userId", new { storedName, userId }, commandTimeout: 600);
            await c.ExecuteAsync($"UPDATE {T.Thread} SET postusername = @storedName WHERE postuserid = @userId", new { storedName, userId });
            await c.ExecuteAsync($"UPDATE {T.Thread} SET lastposter = @storedName WHERE lastposterid = @userId", new { storedName, userId });
            await c.ExecuteAsync($"UPDATE {T.Forum} SET lastposter = @storedName WHERE lastposterid = @userId", new { storedName, userId });
            await c.ExecuteAsync($"UPDATE {T.PmText} SET fromusername = @storedName WHERE fromuserid = @userId", new { storedName, userId });
        }
    }

    /// <summary>Löscht ein Konto. Beiträge bleiben als Gastbeiträge (userid = 0) mit Namen erhalten – wie in vB.</summary>
    public async Task DeleteAsync(long userId)
    {
        await using var c = await db.OpenAsync();
        await c.ExecuteAsync($"UPDATE {T.Post} SET userid = 0 WHERE userid = @userId", new { userId }, commandTimeout: 600);
        await c.ExecuteAsync($"UPDATE {T.Thread} SET postuserid = 0 WHERE postuserid = @userId", new { userId });
        await c.ExecuteAsync($"UPDATE {T.Thread} SET lastposterid = 0 WHERE lastposterid = @userId", new { userId });
        foreach (var t in new[] { T.UserField, T.UserTextField, T.CustomAvatar, T.SubscribeThread, T.ThreadRead, T.ForumRead, T.Moderator, T.UserBan, T.Administrator, Tables.OwnUserAuth, Tables.OwnToken })
            await c.ExecuteAsync($"DELETE FROM {t} WHERE userid = @userId", new { userId });
        var pmTexts = (await c.QueryAsync<long>($"SELECT pmtextid FROM {T.Pm} WHERE userid = @userId", new { userId })).ToList();
        await c.ExecuteAsync($"DELETE FROM {T.Pm} WHERE userid = @userId", new { userId });
        if (pmTexts.Count > 0)
            await c.ExecuteAsync($"DELETE FROM {T.PmText} WHERE pmtextid IN @pmTexts AND NOT EXISTS (SELECT 1 FROM {T.Pm} p WHERE p.pmtextid = {T.PmText}.pmtextid)", new { pmTexts });
        await c.ExecuteAsync($"DELETE FROM {T.User} WHERE userid = @userId", new { userId });
    }

    public async Task BanAsync(long userId, int bannedGroupId, long adminId, string reason, long liftDate)
    {
        await using var c = await db.OpenAsync();
        var u = await c.QueryFirstOrDefaultAsync<(long, long, string, long)>(
            $"SELECT CAST(usergroupid AS SIGNED), CAST(displaygroupid AS SIGNED), usertitle, CAST(customtitle AS SIGNED) FROM {T.User} WHERE userid = @userId", new { userId });
        await c.ExecuteAsync($"""
            REPLACE INTO {T.UserBan} (userid, usergroupid, displaygroupid, usertitle, customtitle, adminid, bandate, liftdate, reason)
            VALUES (@userId, @g, @dg, @ut, @ct, @adminId, @now, @liftDate, @reason)
            """, new { userId, g = u.Item1, dg = u.Item2, ut = u.Item3, ct = u.Item4, adminId, now = ForumTime.Now, liftDate, reason = VbText.ForStorage(reason) });
        await c.ExecuteAsync($"UPDATE {T.User} SET usergroupid = @bannedGroupId, displaygroupid = 0 WHERE userid = @userId", new { userId, bannedGroupId });
    }

    public async Task UnbanAsync(long userId, int fallbackGroupId)
    {
        await using var c = await db.OpenAsync();
        var b = await c.QueryFirstOrDefaultAsync<(long, long)?>($"SELECT CAST(usergroupid AS SIGNED), CAST(displaygroupid AS SIGNED) FROM {T.UserBan} WHERE userid = @userId", new { userId });
        await c.ExecuteAsync($"UPDATE {T.User} SET usergroupid = @g, displaygroupid = @dg WHERE userid = @userId",
            new { userId, g = b?.Item1 ?? fallbackGroupId, dg = b?.Item2 ?? 0 });
        await c.ExecuteAsync($"DELETE FROM {T.UserBan} WHERE userid = @userId", new { userId });
    }

    public async Task<(long BanDate, long LiftDate, string Reason)?> BanInfoAsync(long userId)
    {
        await using var c = await db.OpenAsync();
        var r = await c.QueryFirstOrDefaultAsync<(long, long, string)?>($"SELECT CAST(bandate AS SIGNED), CAST(liftdate AS SIGNED), reason FROM {T.UserBan} WHERE userid = @userId", new { userId });
        return r;
    }

    /// <summary>Abgelaufene Sperren aufheben (vB macht das per Cron).</summary>
    public async Task<int> LiftExpiredBansAsync(int fallbackGroupId)
    {
        await using var c = await db.OpenAsync();
        var ids = (await c.QueryAsync<long>($"SELECT userid FROM {T.UserBan} WHERE liftdate > 0 AND liftdate < @now", new { now = ForumTime.Now })).ToList();
        foreach (var id in ids) await UnbanAsync(id, fallbackGroupId);
        return ids.Count;
    }

    public async Task<PagedResult<UserRow>> MemberListAsync(string? name, string sort, int page, int pageSize)
    {
        return await AdminSearchAsync(new AdminQuery
        {
            Name = name, Sort = sort, Desc = sort != "username", Page = page, PageSize = pageSize, MinPosts = string.IsNullOrWhiteSpace(name) ? 1 : null
        });
    }

    public async Task<List<(long, string)>> SearchNamesAsync(string prefix, int limit = 10)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return new();
        await using var c = await db.OpenAsync();
        return (await c.QueryAsync<(long, string)>($"SELECT CAST(userid AS SIGNED), username FROM {T.User} WHERE username LIKE @p ORDER BY posts DESC LIMIT @limit",
            new { p = VbText.ForStorage(HtmlUtil.ToVbStoredTitle(prefix.Trim())) + "%", limit }))
            .Select(x => (x.Item1, HtmlUtil.CleanTitle(x.Item2))).ToList();
    }

    public async Task<List<UserRow>> RecentAsync(int limit)
    {
        await using var c = await db.OpenAsync();
        return (await c.QueryAsync<UserRow>($"{Select} ORDER BY u.userid DESC LIMIT @limit", new { limit })).ToList();
    }

    public async Task<long> OnlineCountAsync(int minutes = 15)
    {
        await using var c = await db.OpenAsync();
        return await c.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {T.User} WHERE lastactivity > @t", new { t = ForumTime.Now - minutes * 60 });
    }
}
