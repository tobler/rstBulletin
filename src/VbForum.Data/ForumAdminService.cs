using Dapper;
using VbForum.Core.Models;
using VbForum.Core.Text;
using VbForum.Core.Vb;

namespace VbForum.Data;

/// <summary>Verwaltung von Foren, Forenrechten, Benutzergruppen und Moderatoren.</summary>
public sealed class ForumAdminService(Db db, BoardCache cache)
{
    private Tables T => db.T;

    public sealed class ForumEdit
    {
        public int ForumId { get; set; }
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public int ParentId { get; set; } = -1;
        public int DisplayOrder { get; set; } = 10;
        public bool IsCategory { get; set; }
        public bool Active { get; set; } = true;
        public bool AllowPosting { get; set; } = true;
        public bool ModerateThreads { get; set; }
        public bool ModeratePosts { get; set; }
        public bool CountPosts { get; set; } = true;
        public bool AllowImages { get; set; } = true;
        public bool AllowSmilies { get; set; } = true;
        public bool AllowBbCode { get; set; } = true;
        public string Link { get; set; } = "";

        public static ForumEdit From(Forum f, Bitfields b) => new()
        {
            ForumId = f.ForumId, Title = f.DisplayTitle, Description = f.DisplayDescription, ParentId = f.ParentId,
            DisplayOrder = f.DisplayOrder, IsCategory = f.IsCategory, Active = f.IsActive,
            AllowPosting = (f.Options & b.Opt(ForumOption.AllowPosting)) != 0,
            ModerateThreads = (f.Options & b.Opt(ForumOption.ModerateNewThread)) != 0,
            ModeratePosts = (f.Options & b.Opt(ForumOption.ModerateNewPost)) != 0,
            CountPosts = (f.Options & b.Opt(ForumOption.CountPosts)) != 0,
            AllowImages = (f.Options & b.Opt(ForumOption.AllowImages)) != 0,
            AllowSmilies = (f.Options & b.Opt(ForumOption.AllowSmilies)) != 0,
            AllowBbCode = (f.Options & b.Opt(ForumOption.AllowBbCode)) != 0,
            Link = f.Link,
        };
    }

    private static long SetBit(long v, long bit, bool on) => on ? v | bit : v & ~bit;

    public async Task<int> SaveForumAsync(ForumEdit e)
    {
        var snap = await cache.GetAsync();
        var b = snap.Bits;
        if (string.IsNullOrWhiteSpace(e.Title)) throw new ArgumentException("Titel fehlt.");
        if (e.ForumId > 0 && e.ParentId > 0)
        {
            // Zyklen verhindern
            var self = snap.Forum(e.ForumId);
            if (self != null && (e.ParentId == e.ForumId || snap.Descendants(self).Any(d => d.ForumId == e.ParentId)))
                throw new ArgumentException("Ein Forum kann nicht unter sich selbst oder ein eigenes Unterforum verschoben werden.");
        }
        long options = e.ForumId > 0 ? snap.Forum(e.ForumId)?.Options ?? 0 : b.Opt(ForumOption.IndexPosts) | b.Opt(ForumOption.ShowOnForumJump) | b.Opt(ForumOption.AllowIcons);
        options = SetBit(options, b.Opt(ForumOption.Active), e.Active);
        options = SetBit(options, b.Opt(ForumOption.AllowPosting), e.AllowPosting);
        options = SetBit(options, b.Opt(ForumOption.CanContainThreads), !e.IsCategory);
        options = SetBit(options, b.Opt(ForumOption.ModerateNewThread), e.ModerateThreads);
        options = SetBit(options, b.Opt(ForumOption.ModerateNewPost), e.ModeratePosts);
        options = SetBit(options, b.Opt(ForumOption.CountPosts), e.CountPosts);
        options = SetBit(options, b.Opt(ForumOption.AllowImages), e.AllowImages);
        options = SetBit(options, b.Opt(ForumOption.AllowSmilies), e.AllowSmilies);
        options = SetBit(options, b.Opt(ForumOption.AllowBbCode), e.AllowBbCode);

        var p = new
        {
            id = e.ForumId,
            title = VbText.ForStorage(HtmlUtil.ToVbStoredTitle(e.Title.Trim())),
            clean = VbText.ForStorage(e.Title.Trim()),
            desc = VbText.ForStorage(e.Description.Trim()),
            parent = e.ParentId <= 0 ? -1 : e.ParentId,
            order = e.DisplayOrder,
            options,
            link = e.Link.Trim()
        };
        await using var c = await db.OpenAsync();
        int id = e.ForumId;
        if (id > 0)
            await c.ExecuteAsync($"""
                UPDATE {T.Forum} SET title = @title, title_clean = @clean, description = @desc, description_clean = @desc,
                  parentid = @parent, displayorder = @order, options = @options, link = @link WHERE forumid = @id
                """, p);
        else
            id = await c.ExecuteScalarAsync<int>($"""
                INSERT INTO {T.Forum} (title, title_clean, description, description_clean, options, displayorder, parentid, parentlist, childlist,
                    link, newpostemail, newthreademail, defaultsortfield, defaultsortorder)
                VALUES (@title, @clean, @desc, @desc, @options, @order, @parent, '', '', @link, '', '', 'lastpost', 'desc');
                SELECT LAST_INSERT_ID();
                """, p);
        await RebuildTreeListsAsync(c);
        cache.Invalidate();
        return id;
    }

    /// <summary>parentlist/childlist für alle Foren neu berechnen (vB-Format "5,4,-1").</summary>
    private async Task RebuildTreeListsAsync(MySqlConnector.MySqlConnection c)
    {
        var rows = (await c.QueryAsync<(long id, long parent)>($"SELECT CAST(forumid AS SIGNED), CAST(parentid AS SIGNED) FROM {T.Forum}")).Select(r => (id: (int)r.id, parent: (int)r.parent)).ToList();
        var parent = rows.ToDictionary(r => r.id, r => r.parent);
        var children = rows.GroupBy(r => r.parent).ToDictionary(g => g.Key, g => g.Select(x => x.id).ToList());
        foreach (var (id, _) in rows)
        {
            var pl = new List<int> { id };
            var guard = 0;
            while (parent.TryGetValue(pl[^1], out var pp) && pp > 0 && guard++ < 50) pl.Add(pp);
            var cl = new List<int>();
            void Walk(int x) { cl.Add(x); if (children.TryGetValue(x, out var ch)) foreach (var y in ch) Walk(y); }
            Walk(id);
            await c.ExecuteAsync($"UPDATE {T.Forum} SET parentlist = @pl, childlist = @cl WHERE forumid = @id",
                new { id, pl = string.Join(",", pl) + ",-1", cl = string.Join(",", cl) + ",-1" });
        }
    }

    public async Task<string?> DeleteForumAsync(int forumId)
    {
        await using var c = await db.OpenAsync();
        if (await c.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {T.Forum} WHERE parentid = @forumId", new { forumId }) > 0)
            return "Das Forum hat noch Unterforen. Bitte zuerst verschieben oder löschen.";
        if (await c.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {T.Thread} WHERE forumid = @forumId", new { forumId }) > 0)
            return "Das Forum enthält noch Themen. Bitte zuerst in der Themenverwaltung verschieben oder löschen.";
        foreach (var tbl in new[] { T.ForumPermission, T.Moderator, T.ForumRead })
            await c.ExecuteAsync($"DELETE FROM {tbl} WHERE forumid = @forumId", new { forumId });
        await c.ExecuteAsync($"DELETE FROM {T.Forum} WHERE forumid = @forumId", new { forumId });
        await RebuildTreeListsAsync(c);
        cache.Invalidate();
        return null;
    }

    public async Task ReorderAsync(IDictionary<int, int> orders)
    {
        await using var c = await db.OpenAsync();
        foreach (var (id, order) in orders)
            await c.ExecuteAsync($"UPDATE {T.Forum} SET displayorder = @order WHERE forumid = @id", new { id, order });
        cache.Invalidate();
    }

    /// <summary>Gruppenrecht für ein Forum setzen. null = Vorgabe entfernen (wird geerbt).</summary>
    public async Task SetForumPermissionAsync(int forumId, int groupId, long? perms)
    {
        await using var c = await db.OpenAsync();
        if (perms == null)
            await c.ExecuteAsync($"DELETE FROM {T.ForumPermission} WHERE forumid = @forumId AND usergroupid = @groupId", new { forumId, groupId });
        else
            await c.ExecuteAsync($"""
                INSERT INTO {T.ForumPermission} (forumid, usergroupid, forumpermissions) VALUES (@forumId, @groupId, @perms)
                ON DUPLICATE KEY UPDATE forumpermissions = VALUES(forumpermissions)
                """, new { forumId, groupId, perms });
        cache.Invalidate();
    }

    // ---------- Gruppen ----------

    public async Task SaveGroupAsync(UserGroup g)
    {
        await using var c = await db.OpenAsync();
        var p = new
        {
            g.UserGroupId, title = VbText.ForStorage(g.Title.Trim()), desc = VbText.ForStorage(g.Description.Trim()),
            ut = VbText.ForStorage(g.UserTitle.Trim()), g.OpenTag, g.CloseTag, g.PmQuota, g.ForumPermissions, g.AttachLimit
        };
        if (g.UserGroupId > 0)
            await c.ExecuteAsync($"""
                UPDATE {T.UserGroup} SET title = @title, description = @desc, usertitle = @ut, opentag = @OpenTag, closetag = @CloseTag,
                  pmquota = @PmQuota, forumpermissions = @ForumPermissions, attachlimit = @AttachLimit WHERE usergroupid = @UserGroupId
                """, p);
        else
        {
            // Neue Gruppe: übrige Rechte von "Registrierte Benutzer" übernehmen
            var reg = (await cache.GetAsync()).Settings.RegisteredGroupId;
            await c.ExecuteAsync($"""
                INSERT INTO {T.UserGroup} (title, description, usertitle, opentag, closetag, pmquota, forumpermissions, attachlimit,
                    pmpermissions, calendarpermissions, wolpermissions, genericpermissions, genericoptions, signaturepermissions, pmsendmax,
                    visitormessagepermissions, socialgrouppermissions, usercsspermissions, albumpermissions, genericpermissions2, sigmaxchars, sigmaxlines)
                SELECT @title, @desc, @ut, @OpenTag, @CloseTag, @PmQuota, @ForumPermissions, @AttachLimit,
                    pmpermissions, calendarpermissions, wolpermissions, genericpermissions, genericoptions, signaturepermissions, pmsendmax,
                    visitormessagepermissions, socialgrouppermissions, usercsspermissions, albumpermissions, genericpermissions2, sigmaxchars, sigmaxlines
                FROM {T.UserGroup} WHERE usergroupid = @reg
                """, new { p.title, p.desc, p.ut, g.OpenTag, g.CloseTag, g.PmQuota, g.ForumPermissions, g.AttachLimit, reg });
        }
        cache.Invalidate();
    }

    public async Task<string?> DeleteGroupAsync(int groupId)
    {
        if (groupId <= 8) return "Die vBulletin-Standardgruppen (1–8) können nicht gelöscht werden.";
        await using var c = await db.OpenAsync();
        if (await c.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {T.User} WHERE usergroupid = @groupId OR FIND_IN_SET(@groupId, membergroupids)", new { groupId }) > 0)
            return "Der Gruppe sind noch Benutzer zugeordnet.";
        await c.ExecuteAsync($"DELETE FROM {T.ForumPermission} WHERE usergroupid = @groupId", new { groupId });
        await c.ExecuteAsync($"DELETE FROM {T.UserGroup} WHERE usergroupid = @groupId", new { groupId });
        cache.Invalidate();
        return null;
    }

    public async Task<Dictionary<int, long>> GroupMemberCountsAsync()
    {
        await using var c = await db.OpenAsync();
        return (await c.QueryAsync<(long, long)>($"SELECT CAST(usergroupid AS SIGNED), COUNT(*) FROM {T.User} GROUP BY usergroupid")).ToDictionary(x => (int)x.Item1, x => x.Item2);
    }

    // ---------- Moderatoren ----------

    public async Task AddModeratorAsync(long userId, int forumId)
    {
        await using var c = await db.OpenAsync();
        await c.ExecuteAsync($"INSERT IGNORE INTO {T.Moderator} (userid, forumid, permissions, permissions2) VALUES (@userId, @forumId, 4294967295, 4294967295)",
            new { userId, forumId });
        cache.Invalidate();
    }

    public async Task RemoveModeratorAsync(long moderatorId)
    {
        await using var c = await db.OpenAsync();
        await c.ExecuteAsync($"DELETE FROM {T.Moderator} WHERE moderatorid = @moderatorId", new { moderatorId });
        cache.Invalidate();
    }
}
