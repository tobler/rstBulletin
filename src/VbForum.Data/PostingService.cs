using Dapper;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using VbForum.Core.Models;
using VbForum.Core.Text;
using VbForum.Core.Vb;

namespace VbForum.Data;

public sealed record PostResult(bool Ok, string? Error = null, long ThreadId = 0, long PostId = 0, bool Moderated = false)
{
    public static PostResult Fail(string e) => new(false, e);
}

/// <summary>Schreibt Themen/Beiträge vB-kompatibel inkl. aller Zähler, Moderationswarteschlange, Abos und Lesemarkierungen.
/// Die vB-Tabellen sind MyISAM (keine Transaktionen) – Reihenfolge ist so gewählt, dass ein Abbruch höchstens Zähler verfälscht,
/// die die Wartung wieder geradeziehen kann.</summary>
public sealed class PostingService(Db db, BoardCache cache, Counters counters, AttachmentRepository attachments,
    MailService mail, ILogger<PostingService> log)
{
    public const int MaxTitle = 85;
    public const int MaxText = 50000;
    private Tables T => db.T;

    private static string? Validate(string? title, string text, bool needTitle)
    {
        if (needTitle && string.IsNullOrWhiteSpace(title)) return "Bitte gib einen Titel ein.";
        if (needTitle && title!.Trim().Length > MaxTitle) return $"Der Titel darf höchstens {MaxTitle} Zeichen lang sein.";
        if (string.IsNullOrWhiteSpace(text)) return "Der Beitrag ist leer.";
        if (text.Length > MaxText) return $"Der Beitrag ist zu lang (max. {MaxText:N0} Zeichen).";
        return null;
    }

    private async Task<string?> FloodCheckAsync(MySqlConnection c, ForumUser u, Permissions perms, Forum f)
    {
        var s = perms.Snapshot.Settings;
        if (perms.IsModerator(u, f) || s.MinSecondsBetweenPosts <= 0) return null;
        var last = await c.ExecuteScalarAsync<long>($"SELECT lastpost FROM {T.User} WHERE userid = @id", new { id = u.UserId });
        var wait = last + s.MinSecondsBetweenPosts - ForumTime.Now;
        return wait > 0 ? $"Bitte warte noch {wait} Sekunden, bevor du erneut schreibst." : null;
    }

    private static bool CountsPosts(BoardSnapshot s, Forum f) => (f.Options & s.Bits.Opt(ForumOption.CountPosts)) != 0;

    public async Task<PostResult> CreateThreadAsync(ForumUser u, Forum f, string title, string text,
        IReadOnlyList<AttachmentRepository.Upload> files, string ip)
    {
        var snap = await cache.GetAsync();
        var perms = new Permissions(snap);
        if (!perms.CanPostThread(u, f)) return PostResult.Fail("Du darfst in diesem Forum keine Themen erstellen.");
        if (Validate(title, text, true) is { } err) return PostResult.Fail(err);
        if (files.Count > 0 && !perms.CanPostAttachment(u, f)) return PostResult.Fail("Du darfst hier keine Anhänge hochladen.");

        await using var c = await db.OpenAsync();
        if (await FloodCheckAsync(c, u, perms, f) is { } flood) return PostResult.Fail(flood);

        bool moderated = !perms.IsModerator(u, f) && (f.Options & snap.Bits.Opt(ForumOption.ModerateNewThread)) != 0;
        int vis = moderated ? 0 : 1;
        var now = ForumTime.Now;
        var storedTitle = VbText.ForStorage(HtmlUtil.ToVbStoredTitle(title.Trim()));
        var storedName = VbText.ForStorage(HtmlUtil.ToVbStoredTitle(u.Username));
        var body = VbText.ForStorage(text.TrimEnd());

        var threadId = await c.ExecuteScalarAsync<long>($"""
            INSERT INTO {T.Thread} (title, lastpost, forumid, open, replycount, postusername, postuserid, lastposter, lastposterid,
                dateline, views, visible, sticky, taglist, keywords)
            VALUES (@storedTitle, @now, @fid, 1, 0, @storedName, @uid, @storedName, @uid, @now, 0, @vis, 0, NULL, NULL);
            SELECT LAST_INSERT_ID();
            """, new { storedTitle, now, fid = f.ForumId, storedName, uid = u.UserId, vis });

        var postId = await InsertPostAsync(c, threadId, u, storedName, storedTitle, body, now, ip, vis);
        await c.ExecuteAsync($"UPDATE {T.Thread} SET firstpostid = @postId, lastpostid = @postId WHERE threadid = @threadId", new { postId, threadId });

        if (files.Count > 0) await attachments.AddToPostAsync(postId, u.UserId, files, snap.Settings.ThumbnailSize);

        if (moderated)
            await c.ExecuteAsync($"REPLACE INTO {T.Moderation} (primaryid, type, dateline) VALUES (@threadId, 'thread', @now)", new { threadId, now });
        else
        {
            await counters.RebuildForumAsync(c, f.ForumId);
            cache.Invalidate();
        }
        await UpdateUserAfterPostAsync(c, u.UserId, postId, now, !moderated && CountsPosts(snap, f));
        await AutoSubscribeAsync(c, u.UserId, threadId);
        await c.ExecuteAsync($"REPLACE INTO {T.ThreadRead} (userid, threadid, readtime) VALUES (@uid, @threadId, @now)", new { uid = u.UserId, threadId, now });

        return new PostResult(true, null, threadId, postId, moderated);
    }

    public async Task<PostResult> ReplyAsync(ForumUser u, ForumThread t, string text,
        IReadOnlyList<AttachmentRepository.Upload> files, string ip)
    {
        var snap = await cache.GetAsync();
        var perms = new Permissions(snap);
        var f = snap.Forum(t.ForumId);
        if (f == null || !perms.CanReadThread(u, f, t) || !perms.CanReply(u, f, t))
            return PostResult.Fail("Du darfst in diesem Thema nicht antworten.");
        if (Validate(null, text, false) is { } err) return PostResult.Fail(err);
        if (files.Count > 0 && !perms.CanPostAttachment(u, f)) return PostResult.Fail("Du darfst hier keine Anhänge hochladen.");

        await using var c = await db.OpenAsync();
        if (await FloodCheckAsync(c, u, perms, f) is { } flood) return PostResult.Fail(flood);

        bool moderated = !perms.IsModerator(u, f) && (f.Options & snap.Bits.Opt(ForumOption.ModerateNewPost)) != 0;
        int vis = moderated ? 0 : 1;
        var now = ForumTime.Now;
        var storedName = VbText.ForStorage(HtmlUtil.ToVbStoredTitle(u.Username));
        var previousLastPost = t.LastPost;

        var postId = await InsertPostAsync(c, t.ThreadId, u, storedName, "", VbText.ForStorage(text.TrimEnd()), now, ip, vis);
        if (files.Count > 0) await attachments.AddToPostAsync(postId, u.UserId, files, snap.Settings.ThumbnailSize);

        if (moderated)
        {
            await c.ExecuteAsync($"REPLACE INTO {T.Moderation} (primaryid, type, dateline) VALUES (@postId, 'reply', @now)", new { postId, now });
            await c.ExecuteAsync($"UPDATE {T.Thread} SET hiddencount = hiddencount + 1 WHERE threadid = @id", new { id = t.ThreadId });
        }
        else
        {
            await c.ExecuteAsync($"""
                UPDATE {T.Thread} SET replycount = replycount + 1, lastpost = @now, lastpostid = @postId,
                  lastposter = @storedName, lastposterid = @uid WHERE threadid = @id
                """, new { now, postId, storedName, uid = u.UserId, id = t.ThreadId });
            if (t.Visible == 1)
            {
                await c.ExecuteAsync($"""
                    UPDATE {T.Forum} SET replycount = replycount + 1, lastpost = @now, lastpostid = @postId, lastposter = @storedName,
                      lastposterid = @uid, lastthread = @title, lastthreadid = @tid WHERE forumid = @fid
                    """, new { now, postId, storedName, uid = u.UserId, title = t.Title, tid = t.ThreadId, fid = f.ForumId });
                cache.Invalidate();
            }
        }
        await UpdateUserAfterPostAsync(c, u.UserId, postId, now, !moderated && t.Visible == 1 && CountsPosts(snap, f));
        await AutoSubscribeAsync(c, u.UserId, t.ThreadId);
        await c.ExecuteAsync($"REPLACE INTO {T.ThreadRead} (userid, threadid, readtime) VALUES (@uid, @tid, @now)", new { uid = u.UserId, tid = t.ThreadId, now });

        if (!moderated)
            _ = Task.Run(() => NotifySubscribersAsync(t, f, u, postId, previousLastPost, text));

        return new PostResult(true, null, t.ThreadId, postId, moderated);
    }

    private async Task<long> InsertPostAsync(MySqlConnection c, long threadId, ForumUser u, string storedName, string title, string body,
        long now, string ip, int vis) =>
        await c.ExecuteScalarAsync<long>($"""
            INSERT INTO {T.Post} (threadid, parentid, username, userid, title, dateline, pagetext, allowsmilie, showsignature, ipaddress, visible, attach, htmlstate)
            VALUES (@threadId, 0, @storedName, @uid, @title, @now, @body, 1, 1, @ip, @vis, 0, 'on_nl2br');
            SELECT LAST_INSERT_ID();
            """, new { threadId, storedName, uid = u.UserId, title, now, body, ip = ip.Length > 45 ? ip[..45] : ip, vis });

    private async Task UpdateUserAfterPostAsync(MySqlConnection c, long userId, long postId, long now, bool count) =>
        await c.ExecuteAsync($"UPDATE {T.User} SET lastpost = @now, lastpostid = @postId{(count ? ", posts = posts + 1" : "")} WHERE userid = @userId",
            new { userId, postId, now });

    private async Task AutoSubscribeAsync(MySqlConnection c, long userId, long threadId)
    {
        var mode = await c.ExecuteScalarAsync<int>($"SELECT autosubscribe FROM {T.User} WHERE userid = @userId", new { userId });
        if (mode < 0) return;
        await c.ExecuteAsync($"INSERT IGNORE INTO {T.SubscribeThread} (userid, threadid, emailupdate, folderid, canview) VALUES (@userId, @threadId, @mode, 0, 1)",
            new { userId, threadId, mode = Math.Min(mode, 1) });
    }

    private async Task NotifySubscribersAsync(ForumThread t, Forum f, ForumUser author, long postId, long previousLastPost, string text)
    {
        try
        {
            if (!await mail.IsConfiguredAsync()) return;
            var snap = await cache.GetAsync();
            var perms = new Permissions(snap);
            await using var c = await db.OpenAsync();
            var subs = await c.QueryAsync<UserRow>($"""
                SELECT u.userid, u.usergroupid, u.membergroupids, u.username, u.email, u.lastactivity
                FROM {T.SubscribeThread} s JOIN {T.User} u ON u.userid = s.userid
                WHERE s.threadid = @tid AND s.emailupdate = 1 AND u.userid <> @author AND u.lastactivity >= @prev
                """, new { tid = t.ThreadId, author = author.UserId, prev = previousLastPost });
            var url = await mail.AbsoluteUrlAsync($"/post/{postId}");
            var excerpt = HtmlUtil.PlainText(text, 400);
            foreach (var s in subs)
            {
                var fu = ForumUser.From(s, snap);
                if (fu.IsBanned || !perms.CanReadThread(fu, f, t)) continue;
                await mail.SendAsync(s.Email, $"Neue Antwort: {t.DisplayTitle}", new MailContent(
                    $"Hallo {s.DisplayName},",
                    new[] { $"{author.Username} hat im Thema „{t.DisplayTitle}“ geantwortet:" },
                    "Zum Beitrag", url, Quote: excerpt,
                    Footnote: "Weitere Benachrichtigungen zu diesem Thema erhältst du erst, wenn du das Forum wieder besucht hast. Abos verwaltest du in deinem Benutzerbereich."));
            }
        }
        catch (Exception ex) { log.LogWarning(ex, "Abo-Benachrichtigung fehlgeschlagen"); }
    }

    // ---------------- Bearbeiten ----------------

    public async Task<PostResult> EditAsync(ForumUser u, Post p, ForumThread t, string? newTitle, string text, string reason,
        IReadOnlyList<AttachmentRepository.Upload> newFiles, IReadOnlyCollection<long> removeAttachmentIds)
    {
        var snap = await cache.GetAsync();
        var perms = new Permissions(snap);
        var f = snap.Forum(t.ForumId);
        if (f == null || !perms.CanEditPost(u, f, t, p)) return PostResult.Fail("Du darfst diesen Beitrag nicht bearbeiten.");
        bool isFirst = p.PostId == t.FirstPostId;
        if (Validate(newTitle, text, isFirst && newTitle != null) is { } err) return PostResult.Fail(err);
        if (newFiles.Count > 0 && !perms.CanPostAttachment(u, f)) return PostResult.Fail("Du darfst hier keine Anhänge hochladen.");

        await using var c = await db.OpenAsync();
        var now = ForumTime.Now;
        var body = VbText.ForStorage(text.TrimEnd());
        // Original einmalig sichern (vB: postedithistory mit original = 1)
        var hasHistory = await c.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {T.PostEditHistory} WHERE postid = @id", new { id = p.PostId }) > 0;
        if (!hasHistory)
            await c.ExecuteAsync($"""
                INSERT INTO {T.PostEditHistory} (postid, userid, username, title, iconid, dateline, reason, original, pagetext)
                VALUES (@PostId, @UserId, @Username, @Title, 0, @Dateline, '', 1, @PageText)
                """, p);
        await c.ExecuteAsync($"""
            INSERT INTO {T.PostEditHistory} (postid, userid, username, title, iconid, dateline, reason, original, pagetext)
            VALUES (@pid, @uid, @name, @title, 0, @now, @reason, 0, @body)
            """, new { pid = p.PostId, uid = u.UserId, name = VbText.ForStorage(HtmlUtil.ToVbStoredTitle(u.Username)), title = p.Title, now, reason = VbText.ForStorage(reason), body });

        await c.ExecuteAsync($"UPDATE {T.Post} SET pagetext = @body WHERE postid = @id", new { body, id = p.PostId });
        await c.ExecuteAsync($"""
            REPLACE INTO {T.EditLog} (postid, userid, username, dateline, reason, hashistory)
            VALUES (@id, @uid, @name, @now, @reason, 1)
            """, new { id = p.PostId, uid = u.UserId, name = VbText.ForStorage(HtmlUtil.ToVbStoredTitle(u.Username)), now, reason = VbText.ForStorage(reason) });
        await c.ExecuteAsync($"DELETE FROM {T.PostParsed} WHERE postid = @id", new { id = p.PostId });

        if (isFirst && !string.IsNullOrWhiteSpace(newTitle))
        {
            var storedTitle = VbText.ForStorage(HtmlUtil.ToVbStoredTitle(newTitle.Trim()));
            await c.ExecuteAsync($"UPDATE {T.Thread} SET title = @storedTitle WHERE threadid = @tid", new { storedTitle, tid = t.ThreadId });
            await c.ExecuteAsync($"UPDATE {T.Forum} SET lastthread = @storedTitle WHERE lastthreadid = @tid", new { storedTitle, tid = t.ThreadId });
            cache.Invalidate();
        }

        foreach (var aid in removeAttachmentIds)
        {
            var a = await attachments.GetAsync(aid);
            if (a != null && a.ContentId == p.PostId) await attachments.DeleteAsync(aid);
        }
        if (newFiles.Count > 0) await attachments.AddToPostAsync(p.PostId, p.UserId, newFiles, snap.Settings.ThumbnailSize);

        return new PostResult(true, null, t.ThreadId, p.PostId);
    }

    // ---------------- Löschen / Wiederherstellen / Freischalten ----------------

    /// <summary>Sanftes Löschen wie vB (visible = 2 + deletionlog). Startbeitrag → ganzes Thema.</summary>
    public async Task<bool> SoftDeletePostAsync(ForumUser by, Post p, ForumThread t, string reason)
    {
        var snap = await cache.GetAsync();
        var perms = new Permissions(snap);
        var f = snap.Forum(t.ForumId);
        if (f == null || !perms.CanDeletePost(by, f, t, p)) return false;
        if (p.PostId == t.FirstPostId) return await new ModerationService(db, cache, counters, attachments).SoftDeleteThreadAsync(by, t, reason);

        await using var c = await db.OpenAsync();
        await c.ExecuteAsync($"UPDATE {T.Post} SET visible = 2 WHERE postid = @id", new { id = p.PostId });
        await c.ExecuteAsync($"DELETE FROM {T.Moderation} WHERE primaryid = @id AND type = 'reply'", new { id = p.PostId });
        await c.ExecuteAsync($"""
            REPLACE INTO {T.DeletionLog} (primaryid, type, userid, username, reason, dateline)
            VALUES (@id, 'post', @uid, @name, @reason, @now)
            """, new { id = p.PostId, uid = by.UserId, name = VbText.ForStorage(HtmlUtil.ToVbStoredTitle(by.Username)), reason = VbText.ForStorage(reason), now = ForumTime.Now });
        await AfterVisibilityChangeAsync(c, snap, t, f, p.UserId, p.Visible == 1);
        return true;
    }

    public async Task RestorePostAsync(Post p)
    {
        var snap = await cache.GetAsync();
        await using var c = await db.OpenAsync();
        var t = await c.QueryFirstOrDefaultAsync<ForumThread>($"SELECT threadid, forumid, visible, firstpostid FROM {T.Thread} WHERE threadid = @id", new { id = p.ThreadId });
        if (t == null) return;
        await c.ExecuteAsync($"UPDATE {T.Post} SET visible = 1 WHERE postid = @id", new { id = p.PostId });
        await c.ExecuteAsync($"DELETE FROM {T.DeletionLog} WHERE primaryid = @id AND type = 'post'", new { id = p.PostId });
        await AfterVisibilityChangeAsync(c, snap, t, snap.Forum(t.ForumId), p.UserId, true);
    }

    public async Task ApprovePostAsync(Post p)
    {
        var snap = await cache.GetAsync();
        await using var c = await db.OpenAsync();
        var t = await c.QueryFirstOrDefaultAsync<ForumThread>($"SELECT threadid, forumid, visible, firstpostid FROM {T.Thread} WHERE threadid = @id", new { id = p.ThreadId });
        if (t == null) return;
        if (p.PostId == t.FirstPostId && t.Visible == 0)
        {
            await new ModerationService(db, cache, counters, attachments).ApproveThreadAsync(t.ThreadId);
            return;
        }
        await c.ExecuteAsync($"UPDATE {T.Post} SET visible = 1 WHERE postid = @id", new { id = p.PostId });
        await c.ExecuteAsync($"DELETE FROM {T.Moderation} WHERE primaryid = @id AND type = 'reply'", new { id = p.PostId });
        await AfterVisibilityChangeAsync(c, snap, t, snap.Forum(t.ForumId), p.UserId, true);
    }

    /// <summary>Endgültig löschen (inkl. Anhänge). Startbeitrag → ganzes Thema.</summary>
    public async Task HardDeletePostAsync(Post p)
    {
        var snap = await cache.GetAsync();
        await using var c = await db.OpenAsync();
        var t = await c.QueryFirstOrDefaultAsync<ForumThread>($"SELECT threadid, forumid, visible, firstpostid FROM {T.Thread} WHERE threadid = @id", new { id = p.ThreadId });
        if (t != null && t.FirstPostId == p.PostId)
        {
            await new ModerationService(db, cache, counters, attachments).HardDeleteThreadAsync(t.ThreadId);
            return;
        }
        await attachments.DeleteForPostsAsync(new[] { p.PostId });
        foreach (var tbl in new[] { T.EditLog, T.PostParsed, T.PostEditHistory })
            await c.ExecuteAsync($"DELETE FROM {tbl} WHERE postid = @id", new { id = p.PostId });
        await c.ExecuteAsync($"DELETE FROM {T.DeletionLog} WHERE primaryid = @id AND type = 'post'", new { id = p.PostId });
        await c.ExecuteAsync($"DELETE FROM {T.Moderation} WHERE primaryid = @id AND type = 'reply'", new { id = p.PostId });
        await c.ExecuteAsync($"DELETE FROM {T.Post} WHERE postid = @id", new { id = p.PostId });
        if (t != null) await AfterVisibilityChangeAsync(c, snap, t, snap.Forum(t.ForumId), p.UserId, p.Visible == 1);
    }

    private async Task AfterVisibilityChangeAsync(MySqlConnection c, BoardSnapshot snap, ForumThread t, Forum? f, long userId, bool countChanged)
    {
        await counters.RebuildThreadAsync(c, t.ThreadId);
        if (f != null) await counters.RebuildForumAsync(c, f.ForumId);
        if (countChanged)
            await counters.RebuildUserPostsAsync(c, userId, snap.Forums.Values.Where(x => CountsPosts(snap, x)).Select(x => x.ForumId).ToList());
        cache.Invalidate();
    }
}
