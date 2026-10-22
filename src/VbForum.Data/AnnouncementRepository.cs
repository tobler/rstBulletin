using Dapper;
using VbForum.Core.Text;

namespace VbForum.Data;

/// <summary>Ankündigungen (vb_announcement). forumid = -1 gilt in allen Foren, sonst im Forum und allen Unterforen.</summary>
public sealed class AnnouncementRepository(Db db)
{
    // vB announcementoptions: allowbbcode 1, allowhtml 2, allowsmilies 4, parseurl 8, signature 16
    public const long DefaultOptions = 1 | 4 | 8 | 16;

    public sealed class Announcement
    {
        public long AnnouncementId { get; set; }
        public string Title { get; set; } = "";
        public long UserId { get; set; }
        public string Username { get; set; } = "";
        public long StartDate { get; set; }
        public long EndDate { get; set; }
        public string PageText { get; set; } = "";
        public int ForumId { get; set; }
        public long Views { get; set; }
        public long AnnouncementOptions { get; set; }
        public bool IsRead { get; set; }

        public string DisplayTitle => HtmlUtil.CleanTitle(Title);
        public bool IsActive(long now) => StartDate <= now && EndDate >= now;
    }

    private string Select => $"""
        SELECT CAST(a.announcementid AS SIGNED) AS AnnouncementId, a.title AS Title, CAST(a.userid AS SIGNED) AS UserId,
               IFNULL(u.username, '') AS Username, CAST(a.startdate AS SIGNED) AS StartDate, CAST(a.enddate AS SIGNED) AS EndDate,
               IFNULL(a.pagetext, '') AS PageText, CAST(a.forumid AS SIGNED) AS ForumId, CAST(a.views AS SIGNED) AS Views,
               CAST(a.announcementoptions AS SIGNED) AS AnnouncementOptions
        FROM {db.T.Announcement} a LEFT JOIN {db.T.User} u ON u.userid = a.userid
        """;

    /// <summary>Aktuell gültige Ankündigungen für ein Forum (inkl. der globalen und der aus übergeordneten Foren).</summary>
    public async Task<List<Announcement>> ActiveForForumAsync(IEnumerable<int> forumChain, long userId)
    {
        var ids = forumChain.Append(-1).ToList();
        var now = ForumTime.Now;
        await using var c = await db.OpenAsync();
        var list = (await c.QueryAsync<Announcement>($"{Select} WHERE a.startdate <= @now AND a.enddate >= @now AND a.forumid IN @ids ORDER BY a.startdate DESC",
            new { now, ids })).ToList();
        await MarkReadStateAsync(c, list, userId);
        return list;
    }

    /// <summary>Globale Ankündigungen (für die Startseite).</summary>
    public async Task<List<Announcement>> ActiveGlobalAsync(long userId)
    {
        var now = ForumTime.Now;
        await using var c = await db.OpenAsync();
        var list = (await c.QueryAsync<Announcement>($"{Select} WHERE a.startdate <= @now AND a.enddate >= @now AND a.forumid = -1 ORDER BY a.startdate DESC",
            new { now })).ToList();
        await MarkReadStateAsync(c, list, userId);
        return list;
    }

    private async Task MarkReadStateAsync(MySqlConnector.MySqlConnection c, List<Announcement> list, long userId)
    {
        if (userId == 0 || list.Count == 0) { list.ForEach(a => a.IsRead = true); return; }
        var read = (await c.QueryAsync<long>($"SELECT CAST(announcementid AS SIGNED) FROM {db.T.AnnouncementRead} WHERE userid = @userId AND announcementid IN @ids",
            new { userId, ids = list.Select(a => a.AnnouncementId) })).ToHashSet();
        foreach (var a in list) a.IsRead = read.Contains(a.AnnouncementId);
    }

    public async Task<Announcement?> GetAsync(long id)
    {
        await using var c = await db.OpenAsync();
        return await c.QueryFirstOrDefaultAsync<Announcement>($"{Select} WHERE a.announcementid = @id", new { id });
    }

    public async Task<List<Announcement>> ListAllAsync()
    {
        await using var c = await db.OpenAsync();
        return (await c.QueryAsync<Announcement>($"{Select} ORDER BY a.enddate DESC, a.startdate DESC")).ToList();
    }

    /// <summary>Aufruf zählen und als gelesen markieren.</summary>
    public async Task ViewedAsync(long id, long userId)
    {
        await using var c = await db.OpenAsync();
        await c.ExecuteAsync($"UPDATE {db.T.Announcement} SET views = views + 1 WHERE announcementid = @id", new { id });
        if (userId > 0)
            await c.ExecuteAsync($"INSERT IGNORE INTO {db.T.AnnouncementRead} (announcementid, userid) VALUES (@id, @userId)", new { id, userId });
    }

    public async Task<long> SaveAsync(long id, string title, string text, int forumId, long start, long end, long userId)
    {
        await using var c = await db.OpenAsync();
        var p = new
        {
            id, userId, start, end, forumId,
            title = VbText.ForStorage(HtmlUtil.ToVbStoredTitle(title.Trim())),
            text = VbText.ForStorage(text),
            opts = DefaultOptions,
        };
        if (id > 0)
        {
            await c.ExecuteAsync($"""
                UPDATE {db.T.Announcement} SET title = @title, pagetext = @text, forumid = @forumId, startdate = @start, enddate = @end
                WHERE announcementid = @id
                """, p);
            // Geänderte Ankündigung gilt wieder als ungelesen
            await c.ExecuteAsync($"DELETE FROM {db.T.AnnouncementRead} WHERE announcementid = @id", new { id });
            return id;
        }
        return await c.ExecuteScalarAsync<long>($"""
            INSERT INTO {db.T.Announcement} (title, userid, startdate, enddate, pagetext, forumid, views, announcementoptions)
            VALUES (@title, @userId, @start, @end, @text, @forumId, 0, @opts);
            SELECT LAST_INSERT_ID();
            """, p);
    }

    public async Task DeleteAsync(long id)
    {
        await using var c = await db.OpenAsync();
        await c.ExecuteAsync($"DELETE FROM {db.T.Announcement} WHERE announcementid = @id", new { id });
        await c.ExecuteAsync($"DELETE FROM {db.T.AnnouncementRead} WHERE announcementid = @id", new { id });
    }
}
