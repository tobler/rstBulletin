using System.Data;
using Dapper;

namespace VbForum.Data;

/// <summary>Pflegt die denormalisierten vB-Zähler (thread.replycount/lastpost…, forum.threadcount/replycount/lastpost…).</summary>
public sealed class Counters(Db db)
{
    private Tables T => db.T;

    /// <summary>Thema neu berechnen: Antworten, versteckte/gelöschte, erster/letzter Beitrag.</summary>
    public async Task RebuildThreadAsync(IDbConnection c, long threadId)
    {
        var s = await c.QueryFirstOrDefaultAsync<(long vis, long hid, long del)>(
            $"SELECT CAST(IFNULL(SUM(visible=1),0) AS SIGNED), CAST(IFNULL(SUM(visible=0),0) AS SIGNED), CAST(IFNULL(SUM(visible=2),0) AS SIGNED) FROM {T.Post} WHERE threadid = @threadId", new { threadId });
        var first = await c.QueryFirstOrDefaultAsync<(long id, string name, long uid, long date)?>(
            $"SELECT CAST(postid AS SIGNED), username, CAST(userid AS SIGNED), CAST(dateline AS SIGNED) FROM {T.Post} WHERE threadid = @threadId ORDER BY dateline, postid LIMIT 1", new { threadId });
        var last = await c.QueryFirstOrDefaultAsync<(long id, string name, long uid, long date)?>(
            $"SELECT CAST(postid AS SIGNED), username, CAST(userid AS SIGNED), CAST(dateline AS SIGNED) FROM {T.Post} WHERE threadid = @threadId AND visible = 1 ORDER BY dateline DESC, postid DESC LIMIT 1", new { threadId });
        if (first == null) return;
        var l = last ?? first.Value;
        await c.ExecuteAsync($"""
            UPDATE {T.Thread} SET replycount = @rc, hiddencount = @hid, deletedcount = @del,
              firstpostid = @fid, lastpostid = @lid, lastpost = @ldate, lastposter = @lname, lastposterid = @luid
            WHERE threadid = @threadId
            """, new
        {
            threadId, rc = Math.Max(0, s.vis - 1), hid = s.hid, del = s.del, fid = first.Value.id,
            lid = l.id, ldate = l.date, lname = l.name, luid = l.uid
        });
    }

    /// <summary>Forum neu berechnen: Themen, Beiträge (inkl. Startbeitrag, wie vB), letzter Beitrag.</summary>
    public async Task RebuildForumAsync(IDbConnection c, int forumId)
    {
        var s = await c.QueryFirstOrDefaultAsync<(long tc, long pc)>(
            $"SELECT COUNT(*), CAST(IFNULL(SUM(replycount + 1), 0) AS SIGNED) FROM {T.Thread} WHERE forumid = @forumId AND visible = 1 AND open <> 10", new { forumId });
        var last = await c.QueryFirstOrDefaultAsync<(long tid, string title, long lp, string lposter, long lpid, long luid)?>(
            $"""
            SELECT CAST(threadid AS SIGNED), title, CAST(lastpost AS SIGNED), lastposter, CAST(lastpostid AS SIGNED), CAST(lastposterid AS SIGNED) FROM {T.Thread}
            WHERE forumid = @forumId AND visible = 1 AND open <> 10 ORDER BY lastpost DESC LIMIT 1
            """, new { forumId });
        await c.ExecuteAsync($"""
            UPDATE {T.Forum} SET threadcount = @tc, replycount = @pc, lastpost = @lp, lastposter = @lposter,
              lastpostid = @lpid, lastposterid = @luid, lastthread = @title, lastthreadid = @tid
            WHERE forumid = @forumId
            """, new
        {
            forumId, s.tc, s.pc, lp = last?.lp ?? 0, lposter = last?.lposter ?? "", lpid = last?.lpid ?? 0,
            luid = last?.luid ?? 0, title = last?.title ?? "", tid = last?.tid ?? 0
        });
    }

    public async Task RebuildUserPostsAsync(IDbConnection c, long userId, IReadOnlyCollection<int> countingForumIds)
    {
        if (userId == 0) return;
        await c.ExecuteAsync($"""
            UPDATE {T.User} SET posts = (
              SELECT COUNT(*) FROM {T.Post} p JOIN {T.Thread} t ON t.threadid = p.threadid
              WHERE p.userid = @userId AND p.visible = 1 AND t.visible = 1 AND t.forumid IN @ids)
            WHERE userid = @userId
            """, new { userId, ids = countingForumIds.Count == 0 ? new[] { -1 } : countingForumIds.ToArray() });
    }

    // ---------- Wartung: alles neu berechnen ----------

    public async Task<int> RebuildAllThreadsAsync(IProgress<string>? progress, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        var ids = (await c.QueryAsync<long>($"SELECT threadid FROM {T.Thread} WHERE open <> 10 ORDER BY threadid")).ToList();
        int n = 0;
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            await RebuildThreadAsync(c, id);
            if (++n % 1000 == 0) progress?.Report($"Themen: {n:N0} / {ids.Count:N0}");
        }
        progress?.Report($"Themen: {n:N0} / {ids.Count:N0} fertig");
        return n;
    }

    public async Task<int> RebuildAllForumsAsync(CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        var ids = (await c.QueryAsync<int>($"SELECT forumid FROM {T.Forum}")).ToList();
        foreach (var id in ids) await RebuildForumAsync(c, id);
        return ids.Count;
    }

    /// <summary>Beitragszähler aller Benutzer in einem Rutsch (nur Foren mit "Beiträge zählen").</summary>
    public async Task RebuildAllUserPostsAsync(IReadOnlyCollection<int> countingForumIds, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        var ids = countingForumIds.Count == 0 ? new[] { -1 } : countingForumIds.ToArray();
        await c.ExecuteAsync($"""
            UPDATE {T.User} u LEFT JOIN (
              SELECT p.userid, COUNT(*) n FROM {T.Post} p JOIN {T.Thread} t ON t.threadid = p.threadid
              WHERE p.visible = 1 AND t.visible = 1 AND t.forumid IN @ids GROUP BY p.userid
            ) x ON x.userid = u.userid
            SET u.posts = IFNULL(x.n, 0)
            """, new { ids }, commandTimeout: 3600);
    }
}
