using Dapper;

namespace VbForum.Data;

/// <summary>Benutzer löschen – wahlweise mit allen Beiträgen.</summary>
public sealed class UserDeletion(Db db, UserRepository users, PostRepository posts, PostingService posting, ModerationService moderation)
{
    /// <param name="deleteContent">
    /// true: Vom Benutzer eröffnete Themen werden komplett gelöscht (inkl. Antworten anderer), seine übrigen Beiträge einzeln.
    /// false: Beiträge bleiben unter dem Namen „Gelöschter User“ stehen.
    /// </param>
    public async Task DeleteAsync(long userId, bool deleteContent, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (deleteContent)
        {
            List<long> threadIds, postIds;
            await using (var c = await db.OpenAsync(ct))
                threadIds = (await c.QueryAsync<long>($"SELECT CAST(threadid AS SIGNED) FROM {db.T.Thread} WHERE postuserid = @userId", new { userId })).ToList();
            int n = 0;
            foreach (var id in threadIds)
            {
                ct.ThrowIfCancellationRequested();
                await moderation.HardDeleteThreadAsync(id);
                progress?.Report($"Themen gelöscht: {++n} von {threadIds.Count}");
            }
            await using (var c = await db.OpenAsync(ct))
                postIds = (await c.QueryAsync<long>($"SELECT CAST(postid AS SIGNED) FROM {db.T.Post} WHERE userid = @userId", new { userId })).ToList();
            n = 0;
            foreach (var id in postIds)
            {
                ct.ThrowIfCancellationRequested();
                if (await posts.GetAsync(id) is { } p) await posting.HardDeletePostAsync(p);
                progress?.Report($"Beiträge gelöscht: {++n} von {postIds.Count}");
            }
        }
        await users.DeleteAsync(userId);
    }
}
