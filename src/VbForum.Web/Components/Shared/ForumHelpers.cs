using VbForum.Core.Models;
using VbForum.Core.Settings;
using VbForum.Core.Text;
using VbForum.Data;

namespace VbForum.Web.Components.Shared;

/// <summary>Aggregierte Werte eines Forums inkl. aller sichtbaren Unterforen (wie die vB-Startseite).</summary>
public sealed record ForumSummary(Forum Forum, long Threads, long Posts, long LastPost, long LastThreadId, string LastThread,
    string LastPoster, long LastPosterId, long LastPostId, bool Unread, List<Forum> VisibleChildren, bool Locked = false, bool ShowCounts = true)
{
    public string Url => Links.Forum(Forum);

    public static ForumSummary Build(Forum f, Permissions perms, ForumUser u, IReadOnlyDictionary<int, long> readTimes, long cutoff)
    {
        if (perms.ShowsLocked(u, f))
        {
            // Sichtbar, aber ohne Zugriff: keine letzten Themen, Zähler nur wenn das Board es so eingestellt hat
            bool counts = perms.Snapshot.ShowPrivateForums == 2;
            return new ForumSummary(f, f.ThreadCount, f.ReplyCount, 0, 0, "", "", 0, 0, false, new(), Locked: true, ShowCounts: counts);
        }
        var visible = new List<Forum> { f };
        void Walk(Forum x) { foreach (var c in x.Children.Where(c => perms.CanSeeForum(u, c))) { visible.Add(c); Walk(c); } }
        Walk(f);
        var readable = visible.Where(x => perms.CanListThreads(u, x)).ToList();
        var last = readable.OrderByDescending(x => x.LastPost).FirstOrDefault();
        bool unread = !u.IsGuest && readable.Any(x => x.LastPost > Math.Max(readTimes.GetValueOrDefault(x.ForumId), cutoff));
        return new ForumSummary(f, readable.Sum(x => x.ThreadCount), readable.Sum(x => x.ReplyCount), last?.LastPost ?? 0,
            last?.LastThreadId ?? 0, HtmlUtil.CleanTitle(last?.LastThread), HtmlUtil.CleanTitle(last?.LastPoster), last?.LastPosterId ?? 0,
            last?.LastPostId ?? 0, unread, f.Children.Where(c => perms.IsListed(u, c)).ToList());
    }
}

public static class Links
{
    public static string Forum(Forum f) => string.IsNullOrWhiteSpace(f.Link) ? $"/forum/{f.ForumId}/{f.Slug}" : f.Link;
    public static string Thread(ForumThread t) => $"/thread/{t.ThreadId}/{t.Slug}";
    public static string Thread(long id, string title) => $"/thread/{id}/{HtmlUtil.Slugify(title)}";
    public static string User(long id, string name) => id > 0 ? $"/user/{id}/{HtmlUtil.Slugify(name)}" : "#";
    public static string Post(long id) => $"/post/{id}";

    public static long UnreadCutoff(BoardSettings s) => ForumTime.Now - Math.Max(1, s.UnreadDays) * 86400L;

    public static string WithQuery(string path, IDictionary<string, object?> q)
    {
        var parts = q.Where(kv => kv.Value != null && kv.Value.ToString() != "" && kv.Value.ToString() != "False")
            .Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value is bool b ? (b ? "1" : "0") : kv.Value!.ToString()!)}");
        var qs = string.Join("&", parts);
        return qs.Length == 0 ? path : path + "?" + qs;
    }
}
