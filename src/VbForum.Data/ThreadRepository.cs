using Dapper;
using VbForum.Core.Models;
using VbForum.Core.Text;

namespace VbForum.Data;

public enum ThreadSort { LastPost, Dateline, Replies, Views, Title }

public sealed class ThreadListQuery
{
    public int ForumId { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
    public ThreadSort Sort { get; init; } = ThreadSort.LastPost;
    public bool Desc { get; init; } = true;
    public bool OnlyUnread { get; init; }
    public bool IncludeModerated { get; init; }
    public bool IncludeDeleted { get; init; }
    /// <summary>Wenn gesetzt: nur Themen dieses Benutzers (fehlendes canviewothers).</summary>
    public long? OnlyUserId { get; init; }
}

public sealed class ThreadRepository(Db db)
{
    private Tables T => db.T;

    private const string Cols = """
        t.threadid, t.title, t.firstpostid, t.lastpostid, t.lastpost, t.forumid, t.pollid, t.open, t.replycount,
        t.hiddencount, t.deletedcount, t.postusername, t.postuserid, t.lastposter, t.lastposterid, t.dateline,
        t.views, t.visible, t.sticky, t.attach, t.prefixid
        """;

    public async Task<ForumThread?> GetAsync(long threadId)
    {
        await using var c = await db.OpenAsync();
        return await c.QueryFirstOrDefaultAsync<ForumThread>($"SELECT {Cols} FROM {T.Thread} t WHERE t.threadid = @threadId", new { threadId });
    }

    private static string OrderBy(ThreadSort s, bool desc)
    {
        var dir = desc ? "DESC" : "ASC";
        return s switch
        {
            ThreadSort.Dateline => $"t.dateline {dir}",
            ThreadSort.Replies => $"t.replycount {dir}, t.lastpost DESC",
            ThreadSort.Views => $"t.views {dir}, t.lastpost DESC",
            ThreadSort.Title => $"t.title {dir}",
            _ => $"t.lastpost {dir}"
        };
    }

    private static string VisibleList(bool mod, bool del) =>
        mod && del ? "0,1,2" : mod ? "0,1" : del ? "1,2" : "1";

    private string ReadJoin => $"""
        LEFT JOIN {T.ThreadRead} tr ON tr.threadid = t.threadid AND tr.userid = @uid
        LEFT JOIN {T.ForumRead} fr ON fr.forumid = t.forumid AND fr.userid = @uid
        """;

    private const string UnreadExpr = "(@uid > 0 AND t.lastpost > GREATEST(IFNULL(tr.readtime,0), IFNULL(fr.readtime,0), @cutoff))";

    /// <summary>Angepinnte Themen (werden auf Seite 1 oben angezeigt) + normale Themen seitenweise.</summary>
    public async Task<(List<ForumThread> Sticky, PagedResult<ForumThread> Threads)> ListAsync(ThreadListQuery q, long userId, long unreadCutoff)
    {
        await using var c = await db.OpenAsync();
        var vis = VisibleList(q.IncludeModerated, q.IncludeDeleted);
        var own = q.OnlyUserId.HasValue ? " AND t.postuserid = @ownId" : "";
        var unread = q.OnlyUnread ? $" AND {UnreadExpr}" : "";
        var p = new { f = q.ForumId, uid = userId, cutoff = unreadCutoff, ownId = q.OnlyUserId ?? 0, off = (q.Page - 1) * q.PageSize, size = q.PageSize };

        var sticky = q.Page == 1
            ? (await c.QueryAsync<ForumThread>(
                $"""
                SELECT {Cols}, {UnreadExpr} AS isunread FROM {T.Thread} t {ReadJoin}
                WHERE t.forumid = @f AND t.sticky = 1 AND t.visible IN ({vis}){own}{unread}
                ORDER BY t.lastpost DESC LIMIT 50
                """, p)).ToList()
            : new List<ForumThread>();

        var total = await c.ExecuteScalarAsync<long>(
            q.OnlyUnread
                ? $"SELECT COUNT(*) FROM {T.Thread} t {ReadJoin} WHERE t.forumid = @f AND t.sticky = 0 AND t.visible IN ({vis}){own}{unread}"
                : $"SELECT COUNT(*) FROM {T.Thread} t WHERE t.forumid = @f AND t.sticky = 0 AND t.visible IN ({vis}){own}", p);

        var items = (await c.QueryAsync<ForumThread>(
            $"""
            SELECT {Cols}, {UnreadExpr} AS isunread FROM {T.Thread} t {ReadJoin}
            WHERE t.forumid = @f AND t.sticky = 0 AND t.visible IN ({vis}){own}{unread}
            ORDER BY {OrderBy(q.Sort, q.Desc)} LIMIT @off, @size
            """, p)).ToList();

        return (sticky, new PagedResult<ForumThread>(items, total, q.Page, q.PageSize));
    }

    /// <summary>„Neue Beiträge“: Themen mit Aktivität seit <paramref name="since"/> in lesbaren Foren.</summary>
    public async Task<PagedResult<ForumThread>> ListRecentAsync(IReadOnlyCollection<int> forumIds, long since, long userId, long unreadCutoff,
        bool onlyUnread, int page, int pageSize)
    {
        if (forumIds.Count == 0) return new PagedResult<ForumThread>(Array.Empty<ForumThread>(), 0, page, pageSize);
        await using var c = await db.OpenAsync();
        var unread = onlyUnread ? $" AND {UnreadExpr}" : "";
        var p = new { ids = forumIds, since, uid = userId, cutoff = unreadCutoff, off = (page - 1) * pageSize, size = pageSize };
        var total = await c.ExecuteScalarAsync<long>(
            $"SELECT COUNT(*) FROM {T.Thread} t {ReadJoin} WHERE t.forumid IN @ids AND t.visible = 1 AND t.open <> 10 AND t.lastpost > @since{unread}", p);
        var items = (await c.QueryAsync<ForumThread>(
            $"""
            SELECT {Cols}, {UnreadExpr} AS isunread, f.title AS forumtitle FROM {T.Thread} t {ReadJoin}
            JOIN {T.Forum} f ON f.forumid = t.forumid
            WHERE t.forumid IN @ids AND t.visible = 1 AND t.open <> 10 AND t.lastpost > @since{unread}
            ORDER BY t.lastpost DESC LIMIT @off, @size
            """, p)).ToList();
        return new PagedResult<ForumThread>(items, total, page, pageSize);
    }

    public async Task<Dictionary<int, long>> ForumReadTimesAsync(long userId)
    {
        if (userId == 0) return new();
        await using var c = await db.OpenAsync();
        return (await c.QueryAsync<(long, long)>($"SELECT CAST(forumid AS SIGNED), CAST(readtime AS SIGNED) FROM {T.ForumRead} WHERE userid = @userId", new { userId }))
            .ToDictionary(x => (int)x.Item1, x => x.Item2);
    }

    public async Task<long> ThreadReadTimeAsync(long userId, long threadId)
    {
        if (userId == 0) return 0;
        await using var c = await db.OpenAsync();
        return await c.ExecuteScalarAsync<long?>($"SELECT readtime FROM {T.ThreadRead} WHERE userid = @userId AND threadid = @threadId",
            new { userId, threadId }) ?? 0;
    }

    public async Task MarkThreadReadAsync(long userId, long threadId, long time)
    {
        if (userId == 0) return;
        await using var c = await db.OpenAsync();
        await c.ExecuteAsync($"""
            INSERT INTO {T.ThreadRead} (userid, threadid, readtime) VALUES (@userId, @threadId, @time)
            ON DUPLICATE KEY UPDATE readtime = GREATEST(readtime, VALUES(readtime))
            """, new { userId, threadId, time });
    }

    public async Task MarkForumsReadAsync(long userId, IEnumerable<int> forumIds, long time)
    {
        if (userId == 0) return;
        await using var c = await db.OpenAsync();
        foreach (var forumId in forumIds)
            await c.ExecuteAsync($"REPLACE INTO {T.ForumRead} (userid, forumid, readtime) VALUES (@userId, @forumId, @time)",
                new { userId, forumId, time });
    }

    public async Task AddViewsAsync(IReadOnlyDictionary<long, int> views)
    {
        if (views.Count == 0) return;
        await using var c = await db.OpenAsync();
        foreach (var (id, n) in views)
            await c.ExecuteAsync($"UPDATE {T.Thread} SET views = views + @n WHERE threadid = @id", new { id, n });
    }

    // ---------------- Admin-Suche ----------------

    public sealed class AdminQuery
    {
        public string? Title { get; set; }
        public int? ForumId { get; set; }
        public string? Username { get; set; }
        public int? Visible { get; set; }
        public bool? Open { get; set; }
        public bool? Sticky { get; set; }
        public long? FromUnix { get; set; }
        public long? ToUnix { get; set; }
        public string Sort { get; set; } = "lastpost";
        public bool Desc { get; set; } = true;
        public int Page { get; set; } = 1;
        public int PageSize { get; set; } = 50;
    }

    public async Task<PagedResult<ForumThread>> AdminSearchAsync(AdminQuery q, IReadOnlyCollection<int>? forumIds)
    {
        await using var c = await db.OpenAsync();
        var where = new List<string> { "1=1" };
        if (!string.IsNullOrWhiteSpace(q.Title)) where.Add("t.title LIKE @title");
        if (forumIds is { Count: > 0 }) where.Add("t.forumid IN @forumIds");
        if (!string.IsNullOrWhiteSpace(q.Username)) where.Add("t.postusername LIKE @user");
        if (q.Visible.HasValue) where.Add("t.visible = @vis");
        if (q.Open.HasValue) where.Add(q.Open.Value ? "t.open = 1" : "t.open = 0");
        if (q.Sticky.HasValue) where.Add(q.Sticky.Value ? "t.sticky = 1" : "t.sticky = 0");
        if (q.FromUnix.HasValue) where.Add("t.dateline >= @from");
        if (q.ToUnix.HasValue) where.Add("t.dateline < @to");
        var order = q.Sort switch
        {
            "dateline" => "t.dateline", "replycount" => "t.replycount", "views" => "t.views", "title" => "t.title", _ => "t.lastpost"
        } + (q.Desc ? " DESC" : " ASC");
        var p = new
        {
            title = "%" + VbText.ForStorage(HtmlUtil.ToVbStoredTitle(q.Title ?? "")) + "%",
            forumIds, user = (q.Username ?? "") + "%", vis = q.Visible, from = q.FromUnix, to = q.ToUnix,
            off = (q.Page - 1) * q.PageSize, size = q.PageSize
        };
        var w = string.Join(" AND ", where);
        var total = await c.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {T.Thread} t WHERE {w}", p);
        var items = (await c.QueryAsync<ForumThread>(
            $"SELECT {Cols}, f.title AS forumtitle FROM {T.Thread} t LEFT JOIN {T.Forum} f ON f.forumid = t.forumid WHERE {w} ORDER BY {order} LIMIT @off, @size", p)).ToList();
        return new PagedResult<ForumThread>(items, total, q.Page, q.PageSize);
    }
}
