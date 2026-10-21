using System.Text.RegularExpressions;
using Dapper;
using VbForum.Core.Models;
using VbForum.Core.Text;

namespace VbForum.Data;

public enum SearchSort { Relevance, Newest, Oldest }

public sealed class SearchQuery
{
    public string Text { get; init; } = "";
    public bool TitlesOnly { get; init; }
    public int? ForumId { get; init; }
    public string? Username { get; init; }
    public long? Since { get; init; }
    public SearchSort Sort { get; init; } = SearchSort.Relevance;
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
}

public sealed record SearchResult(List<SearchHit> Hits, bool HasMore, bool UsedFulltext, string? Notice);

/// <summary>Suche über MySQL-Volltextindex (falls angelegt), sonst LIKE als Rückfall.</summary>
public sealed partial class SearchService(Db db, Schema schema, BoardCache cache)
{
    private Tables T => db.T;
    private (bool Post, bool Title, DateTime At)? _ft;

    [GeneratedRegex("\"([^\"]+)\"|(\\S+)")]
    private static partial Regex TermRegex();

    private async Task<(bool Post, bool Title)> FulltextAsync()
    {
        if (_ft is { } f && DateTime.UtcNow - f.At < TimeSpan.FromMinutes(10)) return (f.Post, f.Title);
        var p = await schema.HasPostFulltextAsync();
        var t = await schema.HasThreadFulltextAsync();
        _ft = (p, t, DateTime.UtcNow);
        return (p, t);
    }

    public void ResetFulltextCache() => _ft = null;

    private static List<string> Terms(string q) =>
        TermRegex().Matches(q).Select(m => (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim())
            .Where(s => s.Length > 0).Take(10).ToList();

    public async Task<SearchResult> SearchAsync(SearchQuery q, ForumUser u)
    {
        var snap = await cache.GetAsync();
        var perms = new Permissions(snap);
        if (!perms.CanSearch(u)) return new SearchResult(new(), false, false, "Du darfst die Suche nicht verwenden.");
        var forumIds = perms.ReadableOthersForumIds(u);
        if (q.ForumId is int fid && snap.Forum(fid) is { } root)
        {
            var sub = new HashSet<int> { fid };
            foreach (var d in snap.Descendants(root)) sub.Add(d.ForumId);
            forumIds.IntersectWith(sub);
        }
        if (forumIds.Count == 0) return new SearchResult(new(), false, false, null);

        var terms = Terms(q.Text);
        long? userId = null;
        if (!string.IsNullOrWhiteSpace(q.Username))
        {
            await using var cu = await db.OpenAsync();
            var stored = VbText.ForStorage(HtmlUtil.ToVbStoredTitle(q.Username.Trim()));
            userId = await cu.ExecuteScalarAsync<long?>($"SELECT userid FROM {T.User} WHERE username = @stored OR username = @plain LIMIT 1",
                new { stored, plain = q.Username.Trim() });
            if (userId == null) return new SearchResult(new(), false, false, $"Benutzer „{q.Username}“ nicht gefunden.");
        }
        if (terms.Count == 0 && userId == null) return new SearchResult(new(), false, false, null);

        var (ftPost, ftTitle) = await FulltextAsync();
        bool useFt = terms.Count > 0 && (q.TitlesOnly ? ftTitle : ftPost) && terms.All(t => t.Length >= 4 || t.Contains(' '));
        string? notice = null;
        if (!useFt && terms.Count > 0 && (q.TitlesOnly ? ftTitle : ftPost))
            notice = "Hinweis: Begriffe mit weniger als 4 Zeichen werden ohne Volltextindex gesucht – das kann dauern.";

        var col = q.TitlesOnly ? "t.title" : "p.pagetext";
        var where = new List<string> { "p.visible = 1", "t.visible = 1", "t.open <> 10", "t.forumid IN @forumIds" };
        if (q.TitlesOnly) where.Add("p.postid = t.firstpostid");
        if (userId != null) where.Add(q.TitlesOnly ? "t.postuserid = @userId" : "p.userid = @userId");
        if (q.Since != null) where.Add(q.TitlesOnly ? "t.dateline >= @since" : "p.dateline >= @since");

        var prm = new DynamicParameters(new { forumIds, userId, since = q.Since, off = (q.Page - 1) * q.PageSize, take = q.PageSize + 1 });
        string score = "0";
        if (terms.Count > 0)
        {
            if (useFt)
            {
                var boolean = string.Join(' ', terms.Select(t => t.Contains(' ')
                    ? "+\"" + Sanitize(VbText.ForStorage(t)) + "\""
                    : "+" + Sanitize(VbText.ForStorage(t)) + "*"));
                prm.Add("ft", boolean);
                where.Add($"MATCH({col}) AGAINST(@ft IN BOOLEAN MODE)");
                score = $"MATCH({col}) AGAINST(@ft IN BOOLEAN MODE)";
            }
            else
            {
                for (int i = 0; i < terms.Count; i++)
                {
                    var stored = VbText.ForStorage(q.TitlesOnly ? HtmlUtil.ToVbStoredTitle(terms[i]) : terms[i]);
                    prm.Add("t" + i, "%" + stored.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
                    where.Add($"{col} LIKE @t{i}");
                }
            }
        }

        var order = q.Sort switch
        {
            SearchSort.Oldest => "p.dateline ASC",
            SearchSort.Newest => "p.dateline DESC",
            _ => useFt ? "score DESC, p.dateline DESC" : "p.dateline DESC"
        };

        await using var c = await db.OpenAsync();
        var sql = $"""
            SELECT p.postid, p.threadid, t.forumid, t.title AS threadtitle, p.username, p.userid, p.dateline, p.pagetext, {score} AS score
            FROM {T.Post} p JOIN {T.Thread} t ON t.threadid = p.threadid
            WHERE {string.Join(" AND ", where)}
            ORDER BY {order} LIMIT @off, @take
            """;
        var hits = (await c.QueryAsync<SearchHit>(sql, prm, commandTimeout: 60)).ToList();
        bool more = hits.Count > q.PageSize;
        if (more) hits.RemoveAt(hits.Count - 1);
        return new SearchResult(hits, more, useFt, notice);
    }

    private static string Sanitize(string s) => Regex.Replace(s, "[+\\-<>()~*\"@]", " ").Trim();

    /// <summary>Zerlegt einen Auszug um den ersten Treffer herum in (vorher, Treffer, nachher) für die Hervorhebung.</summary>
    public static (string Pre, string Hit, string Post) Highlight(string? pagetext, string query, int radius = 120)
    {
        var plain = HtmlUtil.PlainText(pagetext, 5000);
        var terms = Terms(query).OrderByDescending(t => t.Length).ToList();
        foreach (var t in terms)
        {
            var i = plain.IndexOf(t, StringComparison.OrdinalIgnoreCase);
            if (i < 0) continue;
            var start = Math.Max(0, i - radius);
            var end = Math.Min(plain.Length, i + t.Length + radius);
            return ((start > 0 ? "… " : "") + plain[start..i], plain.Substring(i, t.Length), plain[(i + t.Length)..end] + (end < plain.Length ? " …" : ""));
        }
        return (plain.Length > radius * 2 ? plain[..(radius * 2)] + " …" : plain, "", "");
    }
}
