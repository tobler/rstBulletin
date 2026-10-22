using System.Globalization;
using System.Text;
using Dapper;
using VbForum.Core.Text;

namespace VbForum.Data;

/// <summary>
/// Findet Themen/Foren/Benutzer zu alten vB-URLs, die nur den Titel enthalten
/// (z. B. /showthread.php/Wochenende/page2 – „Friendly URLs“ ohne ID).
/// </summary>
public sealed class LegacyLookup(Db db, BoardCache cache)
{
    private int? _vbPerPage;

    public sealed record ThreadHit(long ThreadId, string Title, long LastPost);

    /// <summary>Themen, deren Titel zum URL-Fragment passt (Groß-/Kleinschreibung, Sonderzeichen und Umlaut-Schreibweise egal).</summary>
    public async Task<List<ThreadHit>> FindThreadsAsync(string fragment)
    {
        var key = UrlKey(fragment);
        if (key.Length == 0) return new();
        // Vorfilter in SQL über die ASCII-Teile des Fragments, genauer Vergleich danach in C#
        var parts = AsciiParts(fragment);
        var like = parts.Count == 0 ? "%" : "%" + string.Join("%", parts.Select(EscapeLike)) + "%";
        await using var c = await db.OpenAsync();
        var rows = await c.QueryAsync<(long, string, long)>(
            $"""
            SELECT CAST(threadid AS SIGNED), title, CAST(lastpost AS SIGNED) FROM {db.T.Thread}
            WHERE visible = 1 AND open <> 10 AND title LIKE @like
            ORDER BY lastpost DESC LIMIT 2000
            """, new { like });
        return rows.Where(r => TitleKeys(r.Item2).Contains(key))
            .Select(r => new ThreadHit(r.Item1, HtmlUtil.CleanTitle(r.Item2), r.Item3)).ToList();
    }

    public async Task<List<int>> FindForumsAsync(string fragment)
    {
        var key = UrlKey(fragment);
        if (key.Length == 0) return new();
        var snap = await cache.GetAsync();
        return snap.Forums.Values.Where(f => TitleKeys(f.Title).Contains(key)).Select(f => f.ForumId).ToList();
    }

    public async Task<long?> FindUserAsync(string fragment)
    {
        var key = UrlKey(fragment);
        if (key.Length == 0) return null;
        var parts = AsciiParts(fragment);
        var like = parts.Count == 0 ? "%" : "%" + string.Join("%", parts.Select(EscapeLike)) + "%";
        await using var c = await db.OpenAsync();
        var rows = await c.QueryAsync<(long, string)>(
            $"SELECT CAST(userid AS SIGNED), username FROM {db.T.User} WHERE username LIKE @like LIMIT 500", new { like });
        var hits = rows.Where(r => TitleKeys(r.Item2).Contains(key)).ToList();
        return hits.Count == 1 ? hits[0].Item1 : null;
    }

    /// <summary>Beiträge pro Seite im alten vB (vb_setting.maxposts), um „page2“ richtig umzurechnen.</summary>
    public async Task<int> VbPostsPerPageAsync()
    {
        if (_vbPerPage is int v) return v;
        try
        {
            await using var c = await db.OpenAsync();
            var s = await c.ExecuteScalarAsync<string?>($"SELECT value FROM {db.T.Setting} WHERE varname = 'maxposts'");
            _vbPerPage = int.TryParse(s, out var n) && n > 0 ? n : 10;
        }
        catch { _vbPerPage = 10; }
        return _vbPerPage.Value;
    }

    // ---------- Normalisierung ----------

    /// <summary>Nur Buchstaben/Ziffern, klein, ohne Akzente (ä → a).</summary>
    public static string Key(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString().Replace("ß", "ss");
    }

    private static string UrlKey(string fragment) => Key(fragment);

    /// <summary>Ein Titel passt in zwei Schreibweisen: „Lüfter“ als „lufter“ und als „luefter“ (vB wandelt Umlaute je nach Einstellung um).</summary>
    private static HashSet<string> TitleKeys(string storedTitle)
    {
        static string Ae(string x) => x.Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue").Replace("Ä", "Ae").Replace("Ö", "Oe").Replace("Ü", "Ue");
        var t = HtmlUtil.CleanTitle(storedTitle);
        // vB hat den gespeicherten (HTML-kodierten) Titel in die URL gesteckt: „&amp;“ wurde dort zu „amp“
        var raw = System.Text.RegularExpressions.Regex.Replace(storedTitle, "&#\\d+;", m => HtmlUtil.CleanTitle(m.Value));
        return new HashSet<string> { Key(t), Key(Ae(t)), Key(raw), Key(Ae(raw)) };
    }

    private static List<string> AsciiParts(string fragment)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        foreach (var ch in fragment)
        {
            if (ch < 128 && char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (sb.Length > 0) { parts.Add(sb.ToString()); sb.Clear(); }
        }
        if (sb.Length > 0) parts.Add(sb.ToString());
        // „ae“ in der URL kann im Titel ein „ä“ sein → solche Teile nicht als Vorfilter nehmen
        return parts.Where(p => p.Length >= 2 && !p.Contains("ae", StringComparison.OrdinalIgnoreCase) &&
                                !p.Contains("oe", StringComparison.OrdinalIgnoreCase) && !p.Contains("ue", StringComparison.OrdinalIgnoreCase) &&
                                !p.Contains("ss", StringComparison.OrdinalIgnoreCase)).Take(6).ToList();
    }

    private static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
