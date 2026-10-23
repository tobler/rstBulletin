using VbForum.Core.Models;
using VbForum.Core.Vb;

namespace VbForum.Data;

/// <summary>Der aktuell angemeldete Benutzer (oder Gast).</summary>
public sealed class ForumUser
{
    public static readonly ForumUser Guest = new() { Username = "Gast" };

    public long UserId { get; init; }
    public string Username { get; init; } = "";
    public string Email { get; init; } = "";
    public int PrimaryGroupId { get; init; }
    public IReadOnlyList<int> GroupIds { get; init; } = Array.Empty<int>();
    public bool IsAdmin { get; init; }
    public bool IsSuperMod { get; init; }
    public bool IsBanned { get; init; }
    public long LastVisit { get; init; }
    public long LastActivity { get; init; }
    public int PmUnread { get; init; }
    public long AvatarRevision { get; init; }
    public bool HasAvatar { get; init; }

    public bool IsGuest => UserId == 0;

    public static ForumUser From(UserRow u, BoardSnapshot s)
    {
        var groups = u.AllGroupIds().Distinct().ToList();
        var ctrl = s.Bits.Admin(AdminPerm.CanControlPanel);
        bool admin = groups.Any(g => s.Settings.AdminGroups.Contains(g)) ||
                     groups.Any(g => s.Groups.TryGetValue(g, out var gr) && (gr.AdminPermissions & ctrl) != 0);
        bool smod = admin || groups.Any(g => s.Settings.SuperModGroups.Contains(g));
        return new ForumUser
        {
            UserId = u.UserId,
            Username = u.DisplayName,
            Email = u.Email,
            PrimaryGroupId = u.UserGroupId,
            GroupIds = groups,
            IsAdmin = admin,
            IsSuperMod = smod,
            IsBanned = s.Settings.BannedGroups.Contains(u.UserGroupId),
            LastVisit = u.LastVisit,
            LastActivity = u.LastActivity,
            PmUnread = u.PmUnread,
            AvatarRevision = u.AvatarRevision,
            HasAvatar = u.HasCustomAvatar > 0,
        };
    }
}

/// <summary>Rechteprüfung nach vB-Logik: Forenrechte werden je Gruppe vom Forum zur Wurzel gesucht (erster Treffer gewinnt),
/// sonst gelten die Gruppen-Standardrechte. Mehrere Gruppen werden ODER-verknüpft.</summary>
public sealed class Permissions(BoardSnapshot s)
{
    public BoardSnapshot Snapshot => s;

    public long GroupForumPerms(int groupId, Forum f)
    {
        foreach (var id in f.ParentIds)
            if (s.ForumPermissions.TryGetValue((id, groupId), out var v)) return v;
        return s.Groups.TryGetValue(groupId, out var g) ? g.ForumPermissions : 0;
    }

    /// <summary>Woher kommt das Recht? (für die Admin-Rechteansicht) – forumId oder 0 = Gruppenstandard.</summary>
    public int InheritedFrom(int groupId, Forum f)
    {
        foreach (var id in f.ParentIds)
            if (s.ForumPermissions.ContainsKey((id, groupId))) return id;
        return 0;
    }

    public long ForumPerms(ForumUser u, Forum f)
    {
        if (u.IsAdmin) return long.MaxValue;
        if (u.IsGuest) return GroupForumPerms(s.Settings.GuestGroupId, f);
        long p = 0;
        foreach (var g in u.GroupIds) p |= GroupForumPerms(g, f);
        return p;
    }

    public bool Has(ForumUser u, Forum f, ForumPerm perm) => (ForumPerms(u, f) & s.Bits.Perm(perm)) != 0;

    public bool CanSeeForum(ForumUser u, Forum f) =>
        (f.IsActive || u.IsAdmin) && (string.IsNullOrEmpty(f.Password) || IsModerator(u, f)) &&
        Has(u, f, ForumPerm.CanView) && (f.Parent == null || CanSeeForum(u, f.Parent));

    /// <summary>
    /// Forum ohne „Forum sehen“-Recht trotzdem (mit Schloss, ohne Zugriff) in der Übersicht zeigen?
    /// vB: forum.showprivate 2 = ja, 1 = nein, 0 = Board-Einstellung showprivateforums.
    /// </summary>
    public bool ShowsLocked(ForumUser u, Forum f) =>
        !CanSeeForum(u, f) && f.IsActive && string.IsNullOrEmpty(f.Password) &&
        (f.Parent == null || CanSeeForum(u, f.Parent)) &&
        (f.ShowPrivate >= 2 || (f.ShowPrivate == 0 && s.ShowPrivateForums > 0));

    /// <summary>Gesperrtes Forum, dessen Thementitel trotzdem angezeigt werden (showprivate = 3; vB behandelt 3 wie 2).</summary>
    public bool ShowsLockedTitles(ForumUser u, Forum f) => !f.IsCategory && f.ShowPrivate == 3 && ShowsLocked(u, f);

    /// <summary>In der Übersicht aufführen – entweder normal oder mit Schloss.</summary>
    public bool IsListed(ForumUser u, Forum f) => CanSeeForum(u, f) || ShowsLocked(u, f);

    /// <summary>Themenliste (Titel) ansehen: „Forum sehen“ genügt (wie vB) – oder gesperrtes Forum mit Titelanzeige.</summary>
    public bool CanListThreads(ForumUser u, Forum f) => !f.IsCategory && (CanSeeForum(u, f) || ShowsLockedTitles(u, f));

    /// <summary>Inhalte von Themen lesen: „Forum sehen“ und zusätzlich „Beiträge lesen“ (canviewthreads).</summary>
    public bool CanReadThreads(ForumUser u, Forum f) =>
        !f.IsCategory && CanSeeForum(u, f) && Has(u, f, ForumPerm.CanViewThreads);

    /// <summary>Ohne canviewothers sieht man nur eigene Themen.</summary>
    public bool CanViewOthers(ForumUser u, Forum f) => Has(u, f, ForumPerm.CanViewOthers);

    /// <summary>Titel eines Themas sehen (Themenliste, Kopf der Themenseite).</summary>
    public bool CanSeeThread(ForumUser u, Forum f, ForumThread t) =>
        CanListThreads(u, f) && (CanViewOthers(u, f) || ShowsLockedTitles(u, f) || (!u.IsGuest && t.PostUserId == u.UserId)) &&
        (t.Visible == 1 || IsModerator(u, f) || (t.Visible == 0 && !u.IsGuest && t.PostUserId == u.UserId));

    /// <summary>Beiträge eines Themas lesen.</summary>
    public bool CanReadThread(ForumUser u, Forum f, ForumThread t) =>
        CanSeeThread(u, f, t) && CanReadThreads(u, f);

    public bool IsModerator(ForumUser u, Forum? f)
    {
        if (u.IsGuest) return false;
        if (u.IsAdmin || u.IsSuperMod) return true;
        if (f == null) return false;
        var ids = f.ParentIds.ToHashSet();
        return s.Moderators.Any(m => m.UserId == u.UserId && ids.Contains(m.ForumId));
    }

    public bool IsAnyModerator(ForumUser u) => !u.IsGuest && (u.IsAdmin || u.IsSuperMod || s.Moderators.Any(m => m.UserId == u.UserId));

    private bool PostingAllowed(Forum f) => (f.Options & s.Bits.Opt(ForumOption.AllowPosting)) != 0;

    public bool CanPostThread(ForumUser u, Forum f) =>
        !u.IsGuest && !u.IsBanned && !f.IsCategory && CanSeeForum(u, f) &&
        (IsModerator(u, f) || (PostingAllowed(f) && Has(u, f, ForumPerm.CanPostNew)));

    /// <summary>Umfrage anlegen (beim neuen Thema oder nachträglich durch den Themenstarter).</summary>
    public bool CanPostPoll(ForumUser u, Forum f) =>
        s.Settings.PollsEnabled && !u.IsGuest && !u.IsBanned && !f.IsCategory && CanSeeForum(u, f) &&
        (IsModerator(u, f) || Has(u, f, ForumPerm.CanPostPoll));

    public bool CanReply(ForumUser u, Forum f, ForumThread t)
    {
        if (u.IsGuest || u.IsBanned || t.IsRedirect) return false;
        if (IsModerator(u, f)) return true;
        if (!t.IsOpen || t.Visible != 1 || !PostingAllowed(f)) return false;
        return Has(u, f, t.PostUserId == u.UserId ? ForumPerm.CanReplyOwn : ForumPerm.CanReplyOthers);
    }

    public bool CanEditPost(ForumUser u, Forum f, ForumThread t, Post p) =>
        !u.IsGuest && (IsModerator(u, f) ||
            (p.UserId == u.UserId && !u.IsBanned && t.IsOpen && Has(u, f, ForumPerm.CanEditPost)));

    public bool CanDeletePost(ForumUser u, Forum f, ForumThread t, Post p) =>
        !u.IsGuest && (IsModerator(u, f) ||
            (p.UserId == u.UserId && !u.IsBanned && t.IsOpen &&
             Has(u, f, p.PostId == t.FirstPostId ? ForumPerm.CanDeleteThread : ForumPerm.CanDeletePost)));

    public bool CanSearch(ForumUser u) =>
        u.IsAdmin || s.Forums.Values.Any(f => !f.IsCategory && Has(u, f, ForumPerm.CanSearch));

    public bool CanGetAttachment(ForumUser u, Forum f) => Has(u, f, ForumPerm.CanGetAttachment);
    public bool CanPostAttachment(ForumUser u, Forum f) => !u.IsGuest && Has(u, f, ForumPerm.CanPostAttachment);

    /// <summary>Foren, deren Themen der Benutzer lesen darf (für Suche, Neue Beiträge).</summary>
    public HashSet<int> ReadableForumIds(ForumUser u) =>
        s.Forums.Values.Where(f => CanReadThreads(u, f) && string.IsNullOrEmpty(f.Password)).Select(f => f.ForumId).ToHashSet();

    public HashSet<int> ReadableOthersForumIds(ForumUser u) =>
        s.Forums.Values.Where(f => CanReadThreads(u, f) && CanViewOthers(u, f) && string.IsNullOrEmpty(f.Password))
            .Select(f => f.ForumId).ToHashSet();
}
