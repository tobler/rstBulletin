using System.Collections.Concurrent;
using Dapper;
using VbForum.Core.Models;
using VbForum.Core.Text;

namespace VbForum.Data;

/// <summary>
/// Werbung und Hinweise (eigene Tabelle vbf_ad, Bilder in vbf_asset als „ad-{id}“).
/// Positionen: notice (Hinweisleiste unter dem Menü), thread (zwischen Beiträgen), postbody (im ersten Beitrag einer Seite),
/// forumlist (zwischen Themen in der Themenliste). Mehrere passende Anzeigen je Platz rotieren nach Gewicht.
/// </summary>
public sealed class AdService(Db db, BoardCache cache)
{
    public static readonly (string Key, string Label)[] Positions =
    {
        ("notice", "Hinweisleiste unter dem Menü (nur Text)"),
        ("thread", "Zwischen Beiträgen"),
        ("postbody", "Im ersten Beitrag der Seite (unter dem Text)"),
        ("forumlist", "Zwischen Themen in der Themenliste"),
    };
    public static string PositionLabel(string key) => Positions.FirstOrDefault(p => p.Key == key).Label ?? key;

    public sealed class Ad
    {
        public int AdId { get; set; }
        public string Title { get; set; } = "";
        public string Position { get; set; } = "thread";
        /// <summary>image, text oder html</summary>
        public string Kind { get; set; } = "image";
        public string? Text { get; set; }
        public string? Html { get; set; }
        public string LinkUrl { get; set; } = "";
        public bool NewTab { get; set; } = true;
        public long ImageVersion { get; set; }
        public string AltText { get; set; } = "";
        /// <summary>Farbe der Hinweisleiste: info, warn, accent, neutral, danger</summary>
        public string Style { get; set; } = "info";
        public bool Dismissible { get; set; }
        /// <summary>Nur in diesen Foren (inkl. Unterforen); leer = überall.</summary>
        public string ForumIds { get; set; } = "";
        /// <summary>all, guests, members</summary>
        public string Audience { get; set; } = "all";
        public int Weight { get; set; } = 1;
        public bool Active { get; set; } = true;
        public long StartDate { get; set; }
        public long EndDate { get; set; }
        public long Impressions { get; set; }
        public long Clicks { get; set; }
        public int DisplayOrder { get; set; }
        public long Created { get; set; }

        public bool IsRunning(long now) => Active && (StartDate == 0 || StartDate <= now) && (EndDate == 0 || EndDate >= now);
        public bool HasImage => ImageVersion > 0;
        public string ImageUrl => $"/ad/{AdId}/image?v={ImageVersion}";
        public string ClickUrl => $"/ad/{AdId}/click";
        public bool HasLink => !string.IsNullOrWhiteSpace(LinkUrl);
        public IReadOnlySet<int> Forums => Core.Settings.BoardSettings.ParseIds(ForumIds);
        public string CtrText => Impressions == 0 ? "–" : (Clicks * 100.0 / Impressions).ToString("0.00", System.Globalization.CultureInfo.GetCultureInfo("de-DE")) + " %";
    }

    private const string Cols = """
        CAST(adid AS SIGNED) AS AdId, title, position, kind, text, html, linkurl, newtab <> 0 AS NewTab, CAST(imageversion AS SIGNED) AS ImageVersion,
        alttext, style, dismissible <> 0 AS Dismissible, forumids, audience, weight, active <> 0 AS Active, CAST(startdate AS SIGNED) AS StartDate,
        CAST(enddate AS SIGNED) AS EndDate, CAST(impressions AS SIGNED) AS Impressions, CAST(clicks AS SIGNED) AS Clicks, displayorder, CAST(created AS SIGNED) AS Created
        """;

    private volatile List<Ad>? _active;
    private DateTime _loadedAt;
    private ConcurrentDictionary<int, long> _views = new();

    public async Task<List<Ad>> AllAsync()
    {
        await using var c = await db.OpenAsync();
        return (await c.QueryAsync<Ad>($"SELECT {Cols} FROM {Tables.OwnAd} ORDER BY position, displayorder, adid")).ToList();
    }

    public async Task<Ad?> GetAsync(int id)
    {
        await using var c = await db.OpenAsync();
        return await c.QueryFirstOrDefaultAsync<Ad>($"SELECT {Cols} FROM {Tables.OwnAd} WHERE adid = @id", new { id });
    }

    private async Task<List<Ad>> ActiveAsync()
    {
        if (_active != null && DateTime.UtcNow - _loadedAt < TimeSpan.FromMinutes(1)) return _active;
        try
        {
            await using var c = await db.OpenAsync();
            _active = (await c.QueryAsync<Ad>($"SELECT {Cols} FROM {Tables.OwnAd} WHERE active <> 0 ORDER BY displayorder, adid")).ToList();
        }
        catch (MySqlConnector.MySqlException) { _active = new(); }
        _loadedAt = DateTime.UtcNow;
        return _active;
    }

    public void Invalidate() => _active = null;

    /// <summary>Sieht dieser Benutzer überhaupt Werbung?</summary>
    public static bool ShowsAds(ForumUser u, BoardSnapshot s)
    {
        if (!s.Settings.AdsEnabled) return false;
        if (u.IsAdmin || u.IsSuperMod) return true; // Team sieht alles (zum Prüfen)
        var free = s.Settings.AdFreeGroups;
        if (u.IsGuest) return !free.Contains(s.Settings.GuestGroupId);
        return !u.GroupIds.Any(free.Contains) && !free.Contains(u.PrimaryGroupId);
    }

    /// <summary>Alle passenden Anzeigen für eine Position (für die Hinweisleiste alle, sonst wird mit Pick gewählt).</summary>
    public async Task<List<Ad>> MatchingAsync(string position, ForumUser u, Forum? forum)
    {
        var snap = await cache.GetAsync();
        if (!ShowsAds(u, snap)) return new();
        var now = ForumTime.Now;
        var parents = forum?.ParentIds.ToHashSet();
        return (await ActiveAsync()).Where(a => a.Position == position && a.IsRunning(now)
                && (a.Audience == "all" || (a.Audience == "guests") == u.IsGuest)
                && (a.Forums.Count == 0 || (parents != null && parents.Overlaps(a.Forums))))
            .ToList();
    }

    /// <summary>Eine Anzeige nach Gewicht zufällig wählen (für mehrere Plätze auf einer Seite möglichst verschiedene).</summary>
    public static Ad? Pick(IReadOnlyList<Ad> candidates, ISet<int>? alreadyShown = null)
    {
        var pool = candidates.Where(a => alreadyShown == null || !alreadyShown.Contains(a.AdId)).ToList();
        if (pool.Count == 0) pool = candidates.ToList();
        if (pool.Count == 0) return null;
        var total = pool.Sum(a => Math.Max(1, a.Weight));
        var r = Random.Shared.Next(total);
        foreach (var a in pool)
        {
            r -= Math.Max(1, a.Weight);
            if (r < 0) { alreadyShown?.Add(a.AdId); return a; }
        }
        return pool[^1];
    }

    /// <summary>Platz nach dem n-ten Element (1-basiert)?</summary>
    public static bool SlotAfter(int n, int after, int every) =>
        after > 0 && (n == after || (every > 0 && n > after && (n - after) % every == 0));

    public void CountView(int adId) => _views.AddOrUpdate(adId, 1, (_, v) => v + 1);

    /// <summary>Gesammelte Einblendungen in die Datenbank schreiben (Hintergrunddienst, jede Minute).</summary>
    public async Task FlushViewsAsync()
    {
        var views = Interlocked.Exchange(ref _views, new());
        if (views.IsEmpty) return;
        await using var c = await db.OpenAsync();
        foreach (var (id, n) in views)
            await c.ExecuteAsync($"UPDATE {Tables.OwnAd} SET impressions = impressions + @n WHERE adid = @id", new { id, n });
    }

    public async Task<string?> ClickAsync(int id)
    {
        await using var c = await db.OpenAsync();
        var url = await c.ExecuteScalarAsync<string?>($"SELECT linkurl FROM {Tables.OwnAd} WHERE adid = @id", new { id });
        if (string.IsNullOrWhiteSpace(url)) return null;
        await c.ExecuteAsync($"UPDATE {Tables.OwnAd} SET clicks = clicks + 1 WHERE adid = @id", new { id });
        return url;
    }

    public async Task<int> SaveAsync(Ad a)
    {
        await using var c = await db.OpenAsync();
        var args = new
        {
            a.AdId, title = a.Title.Trim(), position = Positions.Any(p => p.Key == a.Position) ? a.Position : "thread",
            kind = a.Kind is "image" or "text" or "html" ? a.Kind : "image", text = a.Text ?? "", html = a.Html ?? "", linkurl = a.LinkUrl.Trim(),
            newtab = a.NewTab ? 1 : 0, a.ImageVersion, alttext = a.AltText.Trim(), style = a.Style, dismissible = a.Dismissible ? 1 : 0,
            forumids = string.Join(",", a.Forums.OrderBy(x => x)), audience = a.Audience is "guests" or "members" ? a.Audience : "all",
            weight = Math.Clamp(a.Weight, 1, 100), active = a.Active ? 1 : 0, a.StartDate, a.EndDate, a.DisplayOrder, now = ForumTime.Now,
        };
        int id = a.AdId;
        if (id > 0)
            await c.ExecuteAsync($"""
                UPDATE {Tables.OwnAd} SET title = @title, position = @position, kind = @kind, text = @text, html = @html, linkurl = @linkurl,
                  newtab = @newtab, imageversion = @ImageVersion, alttext = @alttext, style = @style, dismissible = @dismissible, forumids = @forumids,
                  audience = @audience, weight = @weight, active = @active, startdate = @StartDate, enddate = @EndDate, displayorder = @DisplayOrder
                WHERE adid = @AdId
                """, args);
        else
            id = await c.ExecuteScalarAsync<int>($"""
                INSERT INTO {Tables.OwnAd} (title, position, kind, text, html, linkurl, newtab, imageversion, alttext, style, dismissible, forumids,
                  audience, weight, active, startdate, enddate, displayorder, created)
                VALUES (@title, @position, @kind, @text, @html, @linkurl, @newtab, @ImageVersion, @alttext, @style, @dismissible, @forumids,
                  @audience, @weight, @active, @StartDate, @EndDate, @DisplayOrder, @now);
                SELECT LAST_INSERT_ID();
                """, args);
        Invalidate();
        return id;
    }

    public async Task SetImageVersionAsync(int id, long version)
    {
        await using var c = await db.OpenAsync();
        await c.ExecuteAsync($"UPDATE {Tables.OwnAd} SET imageversion = @version WHERE adid = @id", new { id, version });
        Invalidate();
    }

    public async Task ResetStatsAsync(int id)
    {
        await using var c = await db.OpenAsync();
        await c.ExecuteAsync($"UPDATE {Tables.OwnAd} SET impressions = 0, clicks = 0 WHERE adid = @id", new { id });
    }

    public async Task DeleteAsync(int id)
    {
        await using var c = await db.OpenAsync();
        await c.ExecuteAsync($"DELETE FROM {Tables.OwnAd} WHERE adid = @id", new { id });
        Invalidate();
    }
}
