using Dapper;
using VbForum.Core.Text;

namespace VbForum.Data;

public sealed class AdminStats(Db db)
{
    public sealed record Overview(long Threads, long Posts, long Users, long NewUsers7d, long Posts24h, long Threads24h, long Online15m,
        long Moderated, long Deleted, string ServerVersion, long Pms);

    public async Task<Overview> OverviewAsync()
    {
        var t = db.T;
        await using var c = await db.OpenAsync();
        var now = ForumTime.Now;
        return new Overview(
            await c.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {t.Thread} WHERE visible = 1"),
            await c.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {t.Post}"),
            await c.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {t.User}"),
            await c.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {t.User} WHERE joindate > @x", new { x = now - 7 * 86400 }),
            await c.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {t.Post} WHERE dateline > @x", new { x = now - 86400 }),
            await c.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {t.Thread} WHERE dateline > @x", new { x = now - 86400 }),
            await c.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {t.User} WHERE lastactivity > @x", new { x = now - 900 }),
            await c.ExecuteScalarAsync<long>($"SELECT (SELECT COUNT(*) FROM {t.Thread} WHERE visible = 0) + (SELECT COUNT(*) FROM {t.Post} p JOIN {t.Thread} th ON th.threadid = p.threadid WHERE p.visible = 0 AND p.postid <> th.firstpostid)"),
            await c.ExecuteScalarAsync<long>($"SELECT (SELECT COUNT(*) FROM {t.Thread} WHERE visible = 2) + (SELECT COUNT(*) FROM {t.Post} WHERE visible = 2)"),
            await c.ExecuteScalarAsync<string>("SELECT VERSION()") ?? "",
            await c.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {t.PmText}"));
    }

    /// <summary>Beiträge pro Tag der letzten <paramref name="days"/> Tage (für das Diagramm).</summary>
    public async Task<List<(DateTime Day, long Posts)>> PostsPerDayAsync(int days)
    {
        await using var c = await db.OpenAsync();
        var since = ForumTime.Now - days * 86400L;
        var rows = (await c.QueryAsync<(long, long)>($"SELECT CAST(FLOOR(dateline / 86400) AS SIGNED), COUNT(*) FROM {db.T.Post} WHERE dateline > @since GROUP BY 1", new { since }))
            .ToDictionary(x => x.Item1, x => x.Item2);
        var start = since / 86400 + 1;
        return Enumerable.Range(0, days).Select(i => (DateTimeOffset.FromUnixTimeSeconds((start + i) * 86400).Date, rows.GetValueOrDefault(start + i))).ToList();
    }

    /// <summary>Kandidaten für die Spam-/Karteileichen-Bereinigung.</summary>
    public async Task<long> CountCleanupCandidatesAsync(int inactiveDays, int registeredGroupId, int awaitingGroupId)
    {
        await using var c = await db.OpenAsync();
        return await c.ExecuteScalarAsync<long>($"""
            SELECT COUNT(*) FROM {db.T.User} WHERE posts = 0 AND usergroupid IN (@registeredGroupId, @awaitingGroupId)
              AND lastactivity < @t AND joindate < @t
              AND NOT EXISTS (SELECT 1 FROM {db.T.Post} p WHERE p.userid = {db.T.User}.userid LIMIT 1)
            """, new { registeredGroupId, awaitingGroupId, t = ForumTime.Now - inactiveDays * 86400L }, commandTimeout: 600);
    }

    public async Task<List<long>> CleanupCandidatesAsync(int inactiveDays, int registeredGroupId, int awaitingGroupId, int limit)
    {
        await using var c = await db.OpenAsync();
        return (await c.QueryAsync<long>($"""
            SELECT userid FROM {db.T.User} WHERE posts = 0 AND usergroupid IN (@registeredGroupId, @awaitingGroupId)
              AND lastactivity < @t AND joindate < @t
              AND NOT EXISTS (SELECT 1 FROM {db.T.Post} p WHERE p.userid = {db.T.User}.userid LIMIT 1)
            ORDER BY userid LIMIT @limit
            """, new { registeredGroupId, awaitingGroupId, t = ForumTime.Now - inactiveDays * 86400L, limit }, commandTimeout: 600)).ToList();
    }
}
