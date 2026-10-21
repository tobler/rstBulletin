using VbForum.Core.Text;

namespace VbForum.Core.Models;

// Property-Namen entsprechen den vB-Spalten (Dapper mappt case-insensitive).

public sealed class Forum
{
    public int ForumId { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public long Options { get; set; }
    public int DisplayOrder { get; set; }
    public long ReplyCount { get; set; }
    public long ThreadCount { get; set; }
    public long LastPost { get; set; }
    public string LastPoster { get; set; } = "";
    public long LastPosterId { get; set; }
    public long LastPostId { get; set; }
    public string LastThread { get; set; } = "";
    public long LastThreadId { get; set; }
    public int ParentId { get; set; }
    public string ParentList { get; set; } = "";
    public string? ChildList { get; set; }
    public string Password { get; set; } = "";
    public string Link { get; set; } = "";
    public string DefaultSortField { get; set; } = "lastpost";
    public string DefaultSortOrder { get; set; } = "desc";
    /// <summary>vB: 0 = Board-Standard, 1 = für Gruppen ohne Zugriff ausblenden, 2 = mit Schloss anzeigen.</summary>
    public int ShowPrivate { get; set; }

    // vom ForumCache befüllt
    public List<Forum> Children { get; } = new();
    public Forum? Parent { get; set; }
    public int Depth { get; set; }
    public bool IsCategory { get; set; }
    public bool IsActive { get; set; }

    public string DisplayTitle => HtmlUtil.CleanTitle(Title);
    public string DisplayDescription => HtmlUtil.CleanTitle(Description);
    public string Slug => HtmlUtil.Slugify(Title);

    /// <summary>parentlist z. B. "12,3,-1" → [12, 3] (inkl. sich selbst, vom Kind zur Wurzel).</summary>
    public IEnumerable<int> ParentIds => ParentList.Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(s => int.TryParse(s, out var i) ? i : -1).Where(i => i > 0);
}

public sealed class ForumThread
{
    public long ThreadId { get; set; }
    public string Title { get; set; } = "";
    public long FirstPostId { get; set; }
    public long LastPostId { get; set; }
    public long LastPost { get; set; }
    public int ForumId { get; set; }
    public long PollId { get; set; }
    public int Open { get; set; }
    public long ReplyCount { get; set; }
    public long HiddenCount { get; set; }
    public long DeletedCount { get; set; }
    public string PostUsername { get; set; } = "";
    public long PostUserId { get; set; }
    public string LastPoster { get; set; } = "";
    public long LastPosterId { get; set; }
    public long Dateline { get; set; }
    public long Views { get; set; }
    public int Visible { get; set; }
    public int Sticky { get; set; }
    public int Attach { get; set; }
    public string PrefixId { get; set; } = "";

    // aus Joins
    public long ReadTime { get; set; }
    public bool IsUnread { get; set; }
    public string? ForumTitle { get; set; }

    public bool IsOpen => Open == 1;
    /// <summary>open = 10 bedeutet in vB: Weiterleitung (verschobenes Thema).</summary>
    public bool IsRedirect => Open == 10;
    public bool IsSticky => Sticky == 1;
    public bool IsDeleted => Visible == 2;
    public bool IsModerated => Visible == 0;
    public string DisplayTitle => HtmlUtil.CleanTitle(Title);
    public string Slug => HtmlUtil.Slugify(Title);
}

public sealed class Post
{
    public long PostId { get; set; }
    public long ThreadId { get; set; }
    public long ParentId { get; set; }
    public string Username { get; set; } = "";
    public long UserId { get; set; }
    public string Title { get; set; } = "";
    public long Dateline { get; set; }
    public string? PageText { get; set; }
    public int AllowSmilie { get; set; }
    public int ShowSignature { get; set; }
    public string IpAddress { get; set; } = "";
    public int Visible { get; set; }
    public int Attach { get; set; }

    // aus editlog
    public string? EditUsername { get; set; }
    public long EditDateline { get; set; }
    public string? EditReason { get; set; }

    // aus deletionlog
    public string? DeletedBy { get; set; }
    public string? DeleteReason { get; set; }

    public int Position { get; set; }
}

/// <summary>Autoren-Infos für die Beitragsansicht.</summary>
public sealed class PostAuthor
{
    public long UserId { get; set; }
    public string Username { get; set; } = "";
    public int UserGroupId { get; set; }
    public int DisplayGroupId { get; set; }
    public string UserTitle { get; set; } = "";
    public long JoinDate { get; set; }
    public long Posts { get; set; }
    public long AvatarRevision { get; set; }
    public int HasCustomAvatar { get; set; }
    public long LastActivity { get; set; }
    public string? Signature { get; set; }

    public string DisplayName => HtmlUtil.CleanTitle(Username);
}

public sealed class UserRow
{
    public long UserId { get; set; }
    public int UserGroupId { get; set; }
    public string MemberGroupIds { get; set; } = "";
    public int DisplayGroupId { get; set; }
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string Salt { get; set; } = "";
    public string Email { get; set; } = "";
    public string Homepage { get; set; } = "";
    public string UserTitle { get; set; } = "";
    public int CustomTitle { get; set; }
    public long JoinDate { get; set; }
    public long LastVisit { get; set; }
    public long LastActivity { get; set; }
    public long LastPost { get; set; }
    public long LastPostId { get; set; }
    public long Posts { get; set; }
    public long Options { get; set; }
    public string Birthday { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public int AutoSubscribe { get; set; }
    public int PmTotal { get; set; }
    public int PmUnread { get; set; }
    public int PmPopup { get; set; }
    public long AvatarRevision { get; set; }
    public int HasCustomAvatar { get; set; }

    // aus vbf_userauth
    public string? ModernHash { get; set; }

    public string DisplayName => HtmlUtil.CleanTitle(Username);

    public IEnumerable<int> AllGroupIds()
    {
        yield return UserGroupId;
        foreach (var s in MemberGroupIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (int.TryParse(s, out var g) && g != UserGroupId) yield return g;
    }
}

public sealed class UserGroup
{
    public int UserGroupId { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string UserTitle { get; set; } = "";
    public string OpenTag { get; set; } = "";
    public string CloseTag { get; set; } = "";
    public long ForumPermissions { get; set; }
    public long PmPermissions { get; set; }
    public long AdminPermissions { get; set; }
    public long GenericPermissions { get; set; }
    public long GenericOptions { get; set; }
    public int PmQuota { get; set; }
    public long AttachLimit { get; set; }
    public long MemberCount { get; set; }

    public string DisplayTitle => HtmlUtil.CleanTitle(Title);
}

public sealed class ForumPermissionRow
{
    public long ForumPermissionId { get; set; }
    public int ForumId { get; set; }
    public int UserGroupId { get; set; }
    public long ForumPermissions { get; set; }
}

public sealed class ModeratorRow
{
    public long ModeratorId { get; set; }
    public long UserId { get; set; }
    public int ForumId { get; set; }
    public long Permissions { get; set; }
    public string? Username { get; set; }
}

public sealed class Attachment
{
    public long AttachmentId { get; set; }
    public long ContentId { get; set; }
    public long UserId { get; set; }
    public long Dateline { get; set; }
    public long FileDataId { get; set; }
    public string State { get; set; } = "visible";
    public long Counter { get; set; }
    public string Filename { get; set; } = "";
    public long FileSize { get; set; }
    public string Extension { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public long ThumbnailFilesize { get; set; }

    public static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { "jpg", "jpeg", "png", "gif", "webp", "bmp" };

    public bool IsImage => ImageExtensions.Contains(Extension);
    public bool HasThumbnail => ThumbnailFilesize > 0;
    public string DisplayName => HtmlUtil.CleanTitle(Filename);
}

public sealed class Smilie
{
    public int SmilieId { get; set; }
    public string Title { get; set; } = "";
    public string SmilieText { get; set; } = "";
    public string SmiliePath { get; set; } = "";
}

public sealed class CustomBbCode
{
    public string BbCodeTag { get; set; } = "";
    public string? BbCodeReplacement { get; set; }
    public int TwoParams { get; set; }
}

public sealed class PrivateMessage
{
    public long PmId { get; set; }
    public long PmTextId { get; set; }
    public long UserId { get; set; }
    public int FolderId { get; set; }
    public int MessageRead { get; set; }
    public long ParentPmId { get; set; }
    public long FromUserId { get; set; }
    public string FromUsername { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Message { get; set; }
    public string? ToUserArray { get; set; }
    public long Dateline { get; set; }
    public int AllowSmilie { get; set; }

    public string DisplayTitle => HtmlUtil.CleanTitle(Title);
    public bool IsUnread => MessageRead == 0;
}

public sealed class Subscription
{
    public long SubscribeThreadId { get; set; }
    public long ThreadId { get; set; }
    public int EmailUpdate { get; set; }
    public string Title { get; set; } = "";
    public long LastPost { get; set; }
    public string LastPoster { get; set; } = "";
    public int ForumId { get; set; }
    public long ReplyCount { get; set; }
    public long ReadTime { get; set; }

    public string DisplayTitle => HtmlUtil.CleanTitle(Title);
    public bool HasNew => LastPost > ReadTime;
}

public sealed class SearchHit
{
    public long PostId { get; set; }
    public long ThreadId { get; set; }
    public int ForumId { get; set; }
    public string ThreadTitle { get; set; } = "";
    public string Username { get; set; } = "";
    public long UserId { get; set; }
    public long Dateline { get; set; }
    public string? PageText { get; set; }
    public double Score { get; set; }
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, long Total, int Page, int PageSize)
{
    public int PageCount => PageSize <= 0 ? 1 : (int)Math.Max(1, (Total + PageSize - 1) / PageSize);
}
