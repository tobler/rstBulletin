using Dapper;
using Microsoft.Extensions.Logging;
using VbForum.Core.Models;
using VbForum.Core.Php;
using VbForum.Core.Settings;
using VbForum.Core.Text;
using VbForum.Core.Vb;

namespace VbForum.Data;

/// <summary>Unveränderlicher Schnappschuss der selten geänderten Board-Daten.</summary>
public sealed class BoardSnapshot
{
    public required BoardSettings Settings { get; init; }
    public required Dictionary<int, Forum> Forums { get; init; }
    public required List<Forum> RootForums { get; init; }
    public required Dictionary<int, UserGroup> Groups { get; init; }
    public required Dictionary<(int ForumId, int GroupId), long> ForumPermissions { get; init; }
    public required List<ModeratorRow> Moderators { get; init; }
    public required List<Smilie> Smilies { get; init; }
    public required List<CustomBbCode> CustomCodes { get; init; }
    public required Bitfields Bits { get; init; }
    public required long PostContentTypeId { get; init; }
    public required Dictionary<string, string> ProfileFields { get; init; }
    public required long TotalMembers { get; init; }
    public required long NewestUserId { get; init; }
    public required string NewestUsername { get; init; }
    public DateTime LoadedAt { get; init; } = DateTime.UtcNow;

    public long TotalThreads => Forums.Values.Sum(f => f.ThreadCount);
    public long TotalPosts => Forums.Values.Sum(f => f.ReplyCount);

    public Forum? Forum(int id) => Forums.GetValueOrDefault(id);

    /// <summary>Alle Foren in Baumreihenfolge (für Auswahllisten).</summary>
    public IEnumerable<Forum> Flatten()
    {
        IEnumerable<Forum> Walk(Forum f) => new[] { f }.Concat(f.Children.SelectMany(Walk));
        return RootForums.SelectMany(Walk);
    }

    public IEnumerable<Forum> Breadcrumb(Forum f)
    {
        var chain = new List<Forum>();
        for (var x = f; x != null; x = x.Parent) chain.Add(x);
        chain.Reverse();
        return chain;
    }

    public IEnumerable<Forum> Descendants(Forum f) => f.Children.SelectMany(c => new[] { c }.Concat(Descendants(c)));
}

public sealed class BoardCache(Db db, ILogger<BoardCache> log)
{
    private volatile BoardSnapshot? _snap;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);

    public void Invalidate() => _snap = null;

    public BoardSnapshot? Peek => _snap;

    public async Task<BoardSnapshot> GetAsync()
    {
        var s = _snap;
        if (s != null && DateTime.UtcNow - s.LoadedAt < MaxAge) return s;
        await _lock.WaitAsync();
        try
        {
            s = _snap;
            if (s != null && DateTime.UtcNow - s.LoadedAt < MaxAge) return s;
            s = await LoadAsync();
            _snap = s;
            ForumTime.SetTimeZone(s.Settings.TimeZone);
            return s;
        }
        finally { _lock.Release(); }
    }

    private async Task<BoardSnapshot> LoadAsync()
    {
        var t = db.T;
        await using var c = await db.OpenAsync();

        var settingsDict = (await c.QueryAsync<(string, string?)>($"SELECT `name`, `value` FROM {Tables.OwnSetting}"))
            .ToDictionary(x => x.Item1, x => x.Item2 ?? "");
        var settings = BoardSettings.FromDictionary(settingsDict);

        Bitfields bits = new();
        try
        {
            var raw = await c.ExecuteScalarAsync<string?>($"SELECT `data` FROM {t.Datastore} WHERE title = 'bitfields'");
            if (!string.IsNullOrEmpty(raw)) bits = Bitfields.FromDatastore(PhpUnserializer.Parse(raw));
        }
        catch (Exception ex) { log.LogWarning(ex, "Bitfelder aus vb_datastore nicht lesbar – verwende Standardwerte."); }

        var forums = (await c.QueryAsync<Forum>(
            $"""
            SELECT forumid, title, description, options, displayorder, replycount, threadcount, lastpost, lastposter,
                   lastposterid, lastpostid, lastthread, lastthreadid, parentid, parentlist, childlist, password, link,
                   defaultsortfield, defaultsortorder
            FROM {t.Forum} ORDER BY displayorder, forumid
            """)).ToDictionary(f => f.ForumId);
        var roots = new List<Forum>();
        foreach (var f in forums.Values)
        {
            f.IsCategory = (f.Options & bits.Opt(ForumOption.CanContainThreads)) == 0;
            f.IsActive = (f.Options & bits.Opt(ForumOption.Active)) != 0;
            if (f.ParentId > 0 && forums.TryGetValue(f.ParentId, out var p)) { f.Parent = p; p.Children.Add(f); }
            else roots.Add(f);
        }
        void SetDepth(Forum f, int d) { f.Depth = d; foreach (var ch in f.Children) SetDepth(ch, d + 1); }
        foreach (var r in roots) SetDepth(r, 0);

        var groups = (await c.QueryAsync<UserGroup>(
            $"""
            SELECT g.usergroupid, g.title, g.description, g.usertitle, g.opentag, g.closetag, g.forumpermissions,
                   g.pmpermissions, g.adminpermissions, g.genericpermissions, g.genericoptions, g.pmquota, g.attachlimit
            FROM {t.UserGroup} g ORDER BY g.usergroupid
            """)).ToDictionary(g => g.UserGroupId);

        var fp = (await c.QueryAsync<ForumPermissionRow>($"SELECT forumpermissionid, forumid, usergroupid, forumpermissions FROM {t.ForumPermission}"))
            .GroupBy(r => (r.ForumId, r.UserGroupId)).ToDictionary(g => g.Key, g => g.First().ForumPermissions);

        var mods = (await c.QueryAsync<ModeratorRow>(
            $"SELECT m.moderatorid, m.userid, m.forumid, m.permissions, u.username FROM {t.Moderator} m LEFT JOIN {t.User} u ON u.userid = m.userid")).ToList();

        var smilies = (await c.QueryAsync<Smilie>($"SELECT smilieid, title, smilietext, smiliepath FROM {t.Smilie} ORDER BY displayorder")).ToList();
        var codes = (await c.QueryAsync<CustomBbCode>($"SELECT bbcodetag, bbcodereplacement, twoparams FROM {t.BbCode}")).ToList();

        long postType = 1;
        try
        {
            postType = await c.ExecuteScalarAsync<long?>(
                $"""
                SELECT c.contenttypeid FROM {t.ContentType} c JOIN {t.Package} p ON p.packageid = c.packageid
                WHERE c.class = 'Post' AND p.productid = 'vbulletin' LIMIT 1
                """) ?? 1;
        }
        catch (Exception ex) { log.LogWarning(ex, "Content-Type für Beiträge nicht ermittelbar – verwende 1."); }

        var profileFields = new Dictionary<string, string>();
        try
        {
            var cols = await c.QueryAsync<string>(
                "SELECT COLUMN_NAME FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @n AND COLUMN_NAME LIKE 'field%' ORDER BY ORDINAL_POSITION",
                new { n = t.Prefix + "userfield" });
            var titles = (await c.QueryAsync<(string, string)>(
                $"SELECT varname, text FROM {t.Phrase} WHERE varname LIKE 'field%\\_title' AND languageid IN (-1, 0) ORDER BY languageid DESC"))
                .GroupBy(x => x.Item1).ToDictionary(g => g.Key, g => g.First().Item2);
            foreach (var col in cols)
                profileFields[col] = titles.TryGetValue(col + "_title", out var tt) ? HtmlUtil.CleanTitle(tt) : col;
        }
        catch (Exception ex) { log.LogWarning(ex, "Profilfelder nicht lesbar."); }

        var members = await c.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {t.User}");
        var newest = await c.QueryFirstOrDefaultAsync<(long, string)>($"SELECT CAST(userid AS SIGNED), username FROM {t.User} ORDER BY userid DESC LIMIT 1");

        return new BoardSnapshot
        {
            Settings = settings, Forums = forums, RootForums = roots, Groups = groups, ForumPermissions = fp,
            Moderators = mods, Smilies = smilies, CustomCodes = codes, Bits = bits, PostContentTypeId = postType,
            ProfileFields = profileFields, TotalMembers = members, NewestUserId = newest.Item1,
            NewestUsername = HtmlUtil.CleanTitle(newest.Item2)
        };
    }

    public async Task SaveSettingsAsync(BoardSettings s)
    {
        await using var c = await db.OpenAsync();
        foreach (var (k, v) in s.ToDictionary())
            await c.ExecuteAsync($"REPLACE INTO {Tables.OwnSetting} (`name`, `value`) VALUES (@k, @v)", new { k, v });
        Invalidate();
    }
}
