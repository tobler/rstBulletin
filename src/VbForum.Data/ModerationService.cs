using Dapper;
using VbForum.Core.Models;
using VbForum.Core.Text;
using VbForum.Core.Vb;

namespace VbForum.Data;

/// <summary>Themen-Moderation: öffnen/schließen, anpinnen, verschieben, umbenennen, löschen, freischalten.</summary>
public sealed class ModerationService(Db db, BoardCache cache, Counters counters, AttachmentRepository attachments)
{
    private Tables T => db.T;

    public async Task SetOpenAsync(long threadId, bool open)
    {
        await using var c = await db.OpenAsync();
        await c.ExecuteAsync($"UPDATE {T.Thread} SET open = @o WHERE threadid = @threadId AND open <> 10", new { threadId, o = open ? 1 : 0 });
    }

    public async Task SetStickyAsync(long threadId, bool sticky)
    {
        await using var c = await db.OpenAsync();
        await c.ExecuteAsync($"UPDATE {T.Thread} SET sticky = @s WHERE threadid = @threadId", new { threadId, s = sticky ? 1 : 0 });
    }

    public async Task RenameAsync(long threadId, string title)
    {
        await using var c = await db.OpenAsync();
        var stored = VbText.ForStorage(HtmlUtil.ToVbStoredTitle(title.Trim()));
        await c.ExecuteAsync($"UPDATE {T.Thread} SET title = @stored WHERE threadid = @threadId", new { threadId, stored });
        await c.ExecuteAsync($"UPDATE {T.Forum} SET lastthread = @stored WHERE lastthreadid = @threadId", new { threadId, stored });
        cache.Invalidate();
    }

    public async Task<bool> MoveAsync(long threadId, int targetForumId)
    {
        var snap = await cache.GetAsync();
        var target = snap.Forum(targetForumId);
        if (target == null || target.IsCategory) return false;
        await using var c = await db.OpenAsync();
        var from = await c.ExecuteScalarAsync<int?>($"SELECT forumid FROM {T.Thread} WHERE threadid = @threadId", new { threadId });
        if (from == null || from == targetForumId) return from != null;
        await c.ExecuteAsync($"UPDATE {T.Thread} SET forumid = @targetForumId WHERE threadid = @threadId", new { threadId, targetForumId });
        await c.ExecuteAsync($"DELETE FROM {T.ThreadRedirect} WHERE threadid = @threadId", new { threadId });
        await counters.RebuildForumAsync(c, from.Value);
        await counters.RebuildForumAsync(c, targetForumId);
        cache.Invalidate();
        return true;
    }

    public async Task<bool> SoftDeleteThreadAsync(ForumUser by, ForumThread t, string reason)
    {
        await using var c = await db.OpenAsync();
        await c.ExecuteAsync($"UPDATE {T.Thread} SET visible = 2 WHERE threadid = @id", new { id = t.ThreadId });
        await c.ExecuteAsync($"DELETE FROM {T.Moderation} WHERE primaryid = @id AND type = 'thread'", new { id = t.ThreadId });
        await c.ExecuteAsync($"""
            REPLACE INTO {T.DeletionLog} (primaryid, type, userid, username, reason, dateline)
            VALUES (@id, 'thread', @uid, @name, @reason, @now)
            """, new { id = t.ThreadId, uid = by.UserId, name = VbText.ForStorage(HtmlUtil.ToVbStoredTitle(by.Username)), reason = VbText.ForStorage(reason), now = ForumTime.Now });
        await AfterThreadChangeAsync(c, t.ForumId, t.ThreadId);
        return true;
    }

    public async Task RestoreThreadAsync(long threadId)
    {
        await using var c = await db.OpenAsync();
        var forumId = await c.ExecuteScalarAsync<int>($"SELECT forumid FROM {T.Thread} WHERE threadid = @threadId", new { threadId });
        await c.ExecuteAsync($"UPDATE {T.Thread} SET visible = 1 WHERE threadid = @threadId", new { threadId });
        await c.ExecuteAsync($"DELETE FROM {T.DeletionLog} WHERE primaryid = @threadId AND type = 'thread'", new { threadId });
        await AfterThreadChangeAsync(c, forumId, threadId);
    }

    public async Task ApproveThreadAsync(long threadId)
    {
        await using var c = await db.OpenAsync();
        var forumId = await c.ExecuteScalarAsync<int>($"SELECT forumid FROM {T.Thread} WHERE threadid = @threadId", new { threadId });
        await c.ExecuteAsync($"UPDATE {T.Thread} SET visible = 1 WHERE threadid = @threadId", new { threadId });
        var first = await c.ExecuteScalarAsync<long>($"SELECT firstpostid FROM {T.Thread} WHERE threadid = @threadId", new { threadId });
        await c.ExecuteAsync($"UPDATE {T.Post} SET visible = 1 WHERE postid = @first AND visible = 0", new { first });
        await c.ExecuteAsync($"DELETE FROM {T.Moderation} WHERE primaryid = @threadId AND type = 'thread'", new { threadId });
        await AfterThreadChangeAsync(c, forumId, threadId);
    }

    /// <summary>Endgültiges Löschen eines Themas inkl. aller Beiträge, Anhänge, Abos und Lesemarkierungen.</summary>
    public async Task HardDeleteThreadAsync(long threadId)
    {
        await using var c = await db.OpenAsync();
        var forumId = await c.ExecuteScalarAsync<int?>($"SELECT forumid FROM {T.Thread} WHERE threadid = @threadId", new { threadId });
        if (forumId == null) return;
        var postIds = (await c.QueryAsync<long>($"SELECT postid FROM {T.Post} WHERE threadid = @threadId", new { threadId })).ToList();
        var userIds = (await c.QueryAsync<long>($"SELECT DISTINCT userid FROM {T.Post} WHERE threadid = @threadId AND userid > 0", new { threadId })).ToList();
        await attachments.DeleteForPostsAsync(postIds);
        if (postIds.Count > 0)
        {
            foreach (var tbl in new[] { T.EditLog, T.PostParsed, T.PostEditHistory })
                await c.ExecuteAsync($"DELETE FROM {tbl} WHERE postid IN @postIds", new { postIds });
            await c.ExecuteAsync($"DELETE FROM {T.DeletionLog} WHERE type = 'post' AND primaryid IN @postIds", new { postIds });
            await c.ExecuteAsync($"DELETE FROM {T.Moderation} WHERE type = 'reply' AND primaryid IN @postIds", new { postIds });
            await c.ExecuteAsync($"DELETE FROM {T.Post} WHERE threadid = @threadId", new { threadId });
        }
        foreach (var tbl in new[] { T.SubscribeThread, T.ThreadRead, T.ThreadRedirect })
            await c.ExecuteAsync($"DELETE FROM {tbl} WHERE threadid = @threadId", new { threadId });
        await c.ExecuteAsync($"DELETE FROM {T.DeletionLog} WHERE type = 'thread' AND primaryid = @threadId", new { threadId });
        await c.ExecuteAsync($"DELETE FROM {T.Moderation} WHERE type = 'thread' AND primaryid = @threadId", new { threadId });
        // Weiterleitungen (open = 10) auf dieses Thema entfernen
        await c.ExecuteAsync($"DELETE FROM {T.Thread} WHERE open = 10 AND pollid = @threadId", new { threadId });
        await c.ExecuteAsync($"DELETE FROM {T.Thread} WHERE threadid = @threadId", new { threadId });

        await counters.RebuildForumAsync(c, forumId.Value);
        var snap = await cache.GetAsync();
        var counting = snap.Forums.Values.Where(f => (f.Options & snap.Bits.Opt(ForumOption.CountPosts)) != 0).Select(f => f.ForumId).ToList();
        foreach (var u in userIds) await counters.RebuildUserPostsAsync(c, u, counting);
        cache.Invalidate();
    }

    private async Task AfterThreadChangeAsync(MySqlConnector.MySqlConnection c, int forumId, long threadId)
    {
        await counters.RebuildThreadAsync(c, threadId);
        await counters.RebuildForumAsync(c, forumId);
        var snap = await cache.GetAsync();
        var counting = snap.Forums.Values.Where(f => (f.Options & snap.Bits.Opt(ForumOption.CountPosts)) != 0).Select(f => f.ForumId).ToList();
        var userIds = (await c.QueryAsync<long>($"SELECT DISTINCT userid FROM {T.Post} WHERE threadid = @threadId AND userid > 0", new { threadId })).ToList();
        foreach (var u in userIds) await counters.RebuildUserPostsAsync(c, u, counting);
        cache.Invalidate();
    }

    public sealed record QueueItem(string Type, long Id, long ThreadId, string Title, string Username, long Dateline, string Excerpt, int ForumId);

    /// <summary>Moderationswarteschlange: neue Themen und Antworten, die freigeschaltet werden müssen.</summary>
    public async Task<List<QueueItem>> QueueAsync(int limit = 200)
    {
        await using var c = await db.OpenAsync();
        var threads = await c.QueryAsync<(long, string, string, long, string, long)>($"""
            SELECT CAST(t.threadid AS SIGNED), t.title, t.postusername, CAST(t.dateline AS SIGNED), IFNULL(p.pagetext,''), CAST(t.forumid AS SIGNED) FROM {T.Thread} t
            LEFT JOIN {T.Post} p ON p.postid = t.firstpostid WHERE t.visible = 0 ORDER BY t.dateline DESC LIMIT @limit
            """, new { limit });
        var posts = await c.QueryAsync<(long, long, string, string, long, string, long)>($"""
            SELECT CAST(p.postid AS SIGNED), CAST(p.threadid AS SIGNED), t.title, p.username, CAST(p.dateline AS SIGNED), IFNULL(p.pagetext,''), CAST(t.forumid AS SIGNED) FROM {T.Post} p
            JOIN {T.Thread} t ON t.threadid = p.threadid
            WHERE p.visible = 0 AND p.postid <> t.firstpostid ORDER BY p.dateline DESC LIMIT @limit
            """, new { limit });
        return threads.Select(x => new QueueItem("thread", x.Item1, x.Item1, HtmlUtil.CleanTitle(x.Item2), HtmlUtil.CleanTitle(x.Item3), x.Item4, HtmlUtil.PlainText(x.Item5, 200), (int)x.Item6))
            .Concat(posts.Select(x => new QueueItem("post", x.Item1, x.Item2, HtmlUtil.CleanTitle(x.Item3), HtmlUtil.CleanTitle(x.Item4), x.Item5, HtmlUtil.PlainText(x.Item6, 200), (int)x.Item7)))
            .OrderByDescending(x => x.Dateline).ToList();
    }

    public async Task<long> QueueCountAsync()
    {
        await using var c = await db.OpenAsync();
        return await c.ExecuteScalarAsync<long>($"""
            SELECT (SELECT COUNT(*) FROM {T.Thread} WHERE visible = 0) +
                   (SELECT COUNT(*) FROM {T.Post} p JOIN {T.Thread} t ON t.threadid = p.threadid WHERE p.visible = 0 AND p.postid <> t.firstpostid)
            """);
    }

    public async Task<List<QueueItem>> DeletedAsync(int limit = 200)
    {
        await using var c = await db.OpenAsync();
        var threads = await c.QueryAsync<(long, string, string, long, string, long)>($"""
            SELECT CAST(t.threadid AS SIGNED), t.title, IFNULL(d.username, t.postusername), CAST(IFNULL(d.dateline, t.dateline) AS SIGNED), IFNULL(d.reason,''), CAST(t.forumid AS SIGNED) FROM {T.Thread} t
            LEFT JOIN {T.DeletionLog} d ON d.primaryid = t.threadid AND d.type = 'thread'
            WHERE t.visible = 2 ORDER BY IFNULL(d.dateline, t.dateline) DESC LIMIT @limit
            """, new { limit });
        var posts = await c.QueryAsync<(long, long, string, string, long, string, long)>($"""
            SELECT CAST(p.postid AS SIGNED), CAST(p.threadid AS SIGNED), t.title, IFNULL(d.username, p.username), CAST(IFNULL(d.dateline, p.dateline) AS SIGNED), IFNULL(d.reason,''), CAST(t.forumid AS SIGNED) FROM {T.Post} p
            JOIN {T.Thread} t ON t.threadid = p.threadid
            LEFT JOIN {T.DeletionLog} d ON d.primaryid = p.postid AND d.type = 'post'
            WHERE p.visible = 2 AND t.visible <> 2 ORDER BY IFNULL(d.dateline, p.dateline) DESC LIMIT @limit
            """, new { limit });
        return threads.Select(x => new QueueItem("thread", x.Item1, x.Item1, HtmlUtil.CleanTitle(x.Item2), HtmlUtil.CleanTitle(x.Item3), x.Item4, HtmlUtil.CleanTitle(x.Item5), (int)x.Item6))
            .Concat(posts.Select(x => new QueueItem("post", x.Item1, x.Item2, HtmlUtil.CleanTitle(x.Item3), HtmlUtil.CleanTitle(x.Item4), x.Item5, HtmlUtil.CleanTitle(x.Item6), (int)x.Item7)))
            .OrderByDescending(x => x.Dateline).ToList();
    }
}
