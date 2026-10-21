using Dapper;
using VbForum.Core.Models;
using VbForum.Core.Security;
using VbForum.Core.Text;

namespace VbForum.Data;

/// <summary>Anhänge liegen bei diesem Board als Blob in vb_filedata (attachfile = 0).</summary>
public sealed class AttachmentRepository(Db db, BoardCache cache)
{
    private Tables T => db.T;

    private const string Cols = """
        a.attachmentid, a.contentid, a.userid, a.dateline, a.filedataid, a.state, a.counter, a.filename,
        f.filesize, f.extension, f.width, f.height, f.thumbnail_filesize AS thumbnailfilesize
        """;

    public async Task<Dictionary<long, List<Attachment>>> ByPostsAsync(IEnumerable<long> postIds)
    {
        var ids = postIds.Distinct().ToArray();
        if (ids.Length == 0) return new();
        var type = (await cache.GetAsync()).PostContentTypeId;
        await using var c = await db.OpenAsync();
        return (await c.QueryAsync<Attachment>(
            $"""
            SELECT {Cols} FROM {T.Attachment} a JOIN {T.FileData} f ON f.filedataid = a.filedataid
            WHERE a.contenttypeid = @type AND a.contentid IN @ids ORDER BY a.displayorder, a.attachmentid
            """, new { type, ids })).GroupBy(a => a.ContentId).ToDictionary(g => g.Key, g => g.ToList());
    }

    public async Task<Attachment?> GetAsync(long attachmentId)
    {
        var type = (await cache.GetAsync()).PostContentTypeId;
        await using var c = await db.OpenAsync();
        return await c.QueryFirstOrDefaultAsync<Attachment>(
            $"SELECT {Cols} FROM {T.Attachment} a JOIN {T.FileData} f ON f.filedataid = a.filedataid WHERE a.attachmentid = @attachmentId AND a.contenttypeid = @type",
            new { attachmentId, type });
    }

    public async Task<byte[]?> DataAsync(long fileDataId, bool thumbnail)
    {
        await using var c = await db.OpenAsync();
        var col = thumbnail ? "thumbnail" : "filedata";
        return await c.ExecuteScalarAsync<byte[]?>($"SELECT {col} FROM {T.FileData} WHERE filedataid = @fileDataId", new { fileDataId });
    }

    public sealed record Upload(string FileName, byte[] Data);

    /// <summary>Speichert Anhänge zu einem Beitrag. Bilder bekommen ein Thumbnail. Gleiche Dateien desselben Benutzers werden wiederverwendet.</summary>
    public async Task<int> AddToPostAsync(long postId, long userId, IEnumerable<Upload> uploads, int thumbSize)
    {
        var type = (await cache.GetAsync()).PostContentTypeId;
        await using var c = await db.OpenAsync();
        int n = 0;
        var now = ForumTime.Now;
        foreach (var up in uploads)
        {
            var hash = VbPassword.Md5Hex(up.Data);
            var ext = Path.GetExtension(up.FileName).TrimStart('.').ToLowerInvariant();
            var existing = await c.ExecuteScalarAsync<long?>(
                $"SELECT filedataid FROM {T.FileData} WHERE filehash = @hash AND filesize = @size AND userid = @userId LIMIT 1",
                new { hash, size = up.Data.Length, userId });
            long fdId;
            if (existing.HasValue)
            {
                fdId = existing.Value;
                await c.ExecuteAsync($"UPDATE {T.FileData} SET refcount = refcount + 1 WHERE filedataid = @fdId", new { fdId });
            }
            else
            {
                var info = Core.Models.Attachment.ImageExtensions.Contains(ext) ? ImageService.Inspect(up.Data) : null;
                byte[]? thumb = null; int tw = 0, th = 0;
                if (info != null && (info.Width > thumbSize || info.Height > thumbSize))
                {
                    var r = ImageService.Resize(up.Data, thumbSize);
                    if (r != null) { thumb = r.Value.Data; tw = r.Value.Width; th = r.Value.Height; }
                }
                fdId = await c.ExecuteScalarAsync<long>(
                    $"""
                    INSERT INTO {T.FileData} (userid, dateline, thumbnail_dateline, filedata, filesize, filehash, thumbnail,
                        thumbnail_filesize, extension, width, height, thumbnail_width, thumbnail_height, refcount)
                    VALUES (@userId, @now, @tdate, @data, @size, @hash, @thumb, @tsize, @ext, @w, @h, @tw, @th, 1);
                    SELECT LAST_INSERT_ID();
                    """,
                    new
                    {
                        userId, now, tdate = thumb != null ? now : 0, data = up.Data, size = up.Data.Length, hash, thumb,
                        tsize = thumb?.Length ?? 0, ext = info?.Extension == "jpg" && ext == "jpeg" ? "jpeg" : ext,
                        w = info?.Width ?? 0, h = info?.Height ?? 0, tw, th
                    });
            }
            await c.ExecuteAsync(
                $"""
                INSERT INTO {T.Attachment} (contenttypeid, contentid, userid, dateline, filedataid, state, filename, posthash, displayorder)
                VALUES (@type, @postId, @userId, @now, @fdId, 'visible', @name, '', @order)
                """,
                new { type, postId, userId, now, fdId, name = VbText.ForStorage(Path.GetFileName(up.FileName)), order = n });
            n++;
        }
        if (n > 0)
        {
            await c.ExecuteAsync($"UPDATE {T.Post} SET attach = attach + @n WHERE postid = @postId", new { n, postId });
            await c.ExecuteAsync($"UPDATE {T.Thread} t JOIN {T.Post} p ON p.threadid = t.threadid SET t.attach = t.attach + @n WHERE p.postid = @postId", new { n, postId });
        }
        return n;
    }

    public async Task DeleteAsync(long attachmentId)
    {
        await using var c = await db.OpenAsync();
        var a = await c.QueryFirstOrDefaultAsync<(long fd, long post)>(
            $"SELECT CAST(filedataid AS SIGNED), CAST(contentid AS SIGNED) FROM {T.Attachment} WHERE attachmentid = @attachmentId", new { attachmentId });
        if (a.fd == 0) return;
        await c.ExecuteAsync($"DELETE FROM {T.Attachment} WHERE attachmentid = @attachmentId", new { attachmentId });
        await c.ExecuteAsync($"UPDATE {T.FileData} SET refcount = GREATEST(refcount, 1) - 1 WHERE filedataid = @fd", new { a.fd });
        await c.ExecuteAsync($"DELETE FROM {T.FileData} WHERE filedataid = @fd AND refcount = 0", new { a.fd });
        await c.ExecuteAsync($"UPDATE {T.Post} SET attach = GREATEST(attach, 1) - 1 WHERE postid = @post", new { a.post });
        await c.ExecuteAsync($"UPDATE {T.Thread} t JOIN {T.Post} p ON p.threadid = t.threadid SET t.attach = GREATEST(t.attach, 1) - 1 WHERE p.postid = @post", new { a.post });
    }

    public async Task DeleteForPostsAsync(IEnumerable<long> postIds)
    {
        var ids = postIds.ToArray();
        if (ids.Length == 0) return;
        var type = (await cache.GetAsync()).PostContentTypeId;
        await using var c = await db.OpenAsync();
        var list = (await c.QueryAsync<long>($"SELECT attachmentid FROM {T.Attachment} WHERE contenttypeid = @type AND contentid IN @ids", new { type, ids })).ToList();
        foreach (var id in list) await DeleteAsync(id);
    }

    public sealed record Stats(long Count, long Bytes);

    public async Task<Stats> StatsAsync()
    {
        await using var c = await db.OpenAsync();
        var r = await c.QueryFirstAsync<(long, long)>($"SELECT COUNT(*), CAST(IFNULL(SUM(filesize),0) AS SIGNED) FROM {T.FileData}");
        return new Stats(r.Item1, r.Item2);
    }

    public async Task<List<(Attachment A, long ThreadId, string ThreadTitle, string Username)>> LargestAsync(int limit)
    {
        var type = (await cache.GetAsync()).PostContentTypeId;
        await using var c = await db.OpenAsync();
        var rows = await c.QueryAsync<Attachment, long, string, string, (Attachment, long, string, string)>(
            $"""
            SELECT {Cols}, CAST(IFNULL(p.threadid,0) AS SIGNED) AS threadid, t.title, u.username FROM {T.Attachment} a
            JOIN {T.FileData} f ON f.filedataid = a.filedataid
            LEFT JOIN {T.Post} p ON p.postid = a.contentid
            LEFT JOIN {T.Thread} t ON t.threadid = p.threadid
            LEFT JOIN {T.User} u ON u.userid = a.userid
            WHERE a.contenttypeid = @type ORDER BY f.filesize DESC LIMIT @limit
            """, (a, tid, title, user) => (a, tid, title ?? "", user ?? ""), new { type, limit }, splitOn: "threadid,title,username");
        return rows.ToList();
    }
}
