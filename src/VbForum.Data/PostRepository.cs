using Dapper;
using VbForum.Core.Models;

namespace VbForum.Data;

public sealed class PostRepository(Db db)
{
    private Tables T => db.T;

    private const string Cols = """
        p.postid, p.threadid, p.parentid, p.username, p.userid, p.title, p.dateline, p.pagetext, p.allowsmilie,
        p.showsignature, p.ipaddress, p.visible, p.attach
        """;

    private string Joins => $"""
        LEFT JOIN {T.EditLog} el ON el.postid = p.postid
        LEFT JOIN {T.DeletionLog} dl ON dl.primaryid = p.postid AND dl.type = 'post'
        """;

    private const string JoinCols = ", el.username AS editusername, IFNULL(el.dateline,0) AS editdateline, el.reason AS editreason, dl.username AS deletedby, dl.reason AS deletereason";

    public async Task<Post?> GetAsync(long postId)
    {
        await using var c = await db.OpenAsync();
        return await c.QueryFirstOrDefaultAsync<Post>($"SELECT {Cols}{JoinCols} FROM {T.Post} p {Joins} WHERE p.postid = @postId", new { postId });
    }

    private static string Vis(bool mod, bool del) => mod && del ? "0,1,2" : mod ? "0,1" : del ? "1,2" : "1";

    /// <summary>Beiträge eines Themas. <paramref name="ownModeratedUserId"/>: eigene, noch nicht freigeschaltete Beiträge zeigen.</summary>
    public async Task<PagedResult<Post>> ListByThreadAsync(long threadId, int page, int pageSize, bool newestFirst,
        bool includeModerated, bool includeDeleted, long ownModeratedUserId = 0, string? search = null)
    {
        await using var c = await db.OpenAsync();
        var vis = Vis(includeModerated, includeDeleted);
        var visWhere = ownModeratedUserId > 0 && !includeModerated
            ? $"(p.visible IN ({vis}) OR (p.visible = 0 AND p.userid = @own))"
            : $"p.visible IN ({vis})";
        var searchWhere = string.IsNullOrWhiteSpace(search) ? "" : " AND p.pagetext LIKE @q";
        var dir = newestFirst ? "DESC" : "ASC";
        var prm = new
        {
            threadId, own = ownModeratedUserId, q = "%" + Core.Text.VbText.ForStorage(search ?? "") + "%",
            off = (page - 1) * pageSize, size = pageSize
        };
        var total = await c.ExecuteScalarAsync<long>(
            $"SELECT COUNT(*) FROM {T.Post} p WHERE p.threadid = @threadId AND {visWhere}{searchWhere}", prm);
        var items = (await c.QueryAsync<Post>(
            $"""
            SELECT {Cols}{JoinCols} FROM {T.Post} p {Joins}
            WHERE p.threadid = @threadId AND {visWhere}{searchWhere}
            ORDER BY p.dateline {dir}, p.postid {dir} LIMIT @off, @size
            """, prm)).ToList();
        for (int i = 0; i < items.Count; i++)
            items[i].Position = newestFirst ? (int)(total - (page - 1) * pageSize - i) : (page - 1) * pageSize + i + 1;
        return new PagedResult<Post>(items, total, page, pageSize);
    }

    /// <summary>Position eines Beitrags im Thema (1-basiert, chronologisch) – für /post/{id}-Links.</summary>
    public async Task<long> PositionAsync(Post p, bool includeModerated)
    {
        await using var c = await db.OpenAsync();
        var vis = includeModerated ? "0,1" : "1";
        return await c.ExecuteScalarAsync<long>(
            $"""
            SELECT COUNT(*) FROM {T.Post} WHERE threadid = @ThreadId AND visible IN ({vis})
              AND (dateline < @Dateline OR (dateline = @Dateline AND postid <= @PostId))
            """, p);
    }

    public async Task<Dictionary<long, PostAuthor>> AuthorsAsync(IEnumerable<long> userIds)
    {
        var ids = userIds.Where(i => i > 0).Distinct().ToArray();
        if (ids.Length == 0) return new();
        await using var c = await db.OpenAsync();
        return (await c.QueryAsync<PostAuthor>(
            $"""
            SELECT u.userid, u.username, u.usergroupid, u.displaygroupid, u.usertitle, u.joindate, u.posts,
                   u.avatarrevision, u.lastactivity, ut.signature,
                   (SELECT COUNT(*) FROM {T.CustomAvatar} ca WHERE ca.userid = u.userid AND ca.visible = 1) AS hascustomavatar
            FROM {T.User} u LEFT JOIN {T.UserTextField} ut ON ut.userid = u.userid
            WHERE u.userid IN @ids
            """, new { ids })).ToDictionary(a => a.UserId);
    }

    public async Task<List<Post>> ModerationQueueAsync(int limit = 200)
    {
        await using var c = await db.OpenAsync();
        return (await c.QueryAsync<Post>(
            $"""
            SELECT {Cols}{JoinCols} FROM {T.Post} p {Joins}
            WHERE p.visible = 0 ORDER BY p.dateline DESC LIMIT @limit
            """, new { limit })).ToList();
    }

    public async Task<List<Post>> DeletedAsync(int limit = 200)
    {
        await using var c = await db.OpenAsync();
        return (await c.QueryAsync<Post>(
            $"""
            SELECT {Cols}{JoinCols} FROM {T.Post} p {Joins}
            WHERE p.visible = 2 ORDER BY p.dateline DESC LIMIT @limit
            """, new { limit })).ToList();
    }

    public async Task<PagedResult<Post>> ByUserAsync(long userId, IReadOnlyCollection<int> forumIds, int page, int pageSize)
    {
        await using var c = await db.OpenAsync();
        var prm = new { userId, ids = forumIds.Count == 0 ? new[] { -1 } : forumIds.ToArray(), off = (page - 1) * pageSize, size = pageSize };
        var total = await c.ExecuteScalarAsync<long>(
            $"SELECT COUNT(*) FROM {T.Post} p JOIN {T.Thread} t ON t.threadid = p.threadid WHERE p.userid = @userId AND p.visible = 1 AND t.visible = 1 AND t.forumid IN @ids", prm);
        var items = (await c.QueryAsync<Post>(
            $"""
            SELECT {Cols} FROM {T.Post} p JOIN {T.Thread} t ON t.threadid = p.threadid
            WHERE p.userid = @userId AND p.visible = 1 AND t.visible = 1 AND t.forumid IN @ids
            ORDER BY p.dateline DESC LIMIT @off, @size
            """, prm)).ToList();
        return new PagedResult<Post>(items, total, page, pageSize);
    }
}
