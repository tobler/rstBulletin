namespace VbForum.Core.Vb;

/// <summary>
/// Bitfelder aus vBulletin 4. Die Standardwerte stimmen mit vB 4.x überein; zur Laufzeit werden sie,
/// falls vorhanden, aus vb_datastore ('bitfields') überschrieben (siehe Bitfields.Load).
/// </summary>
[Flags]
public enum ForumPerm : long
{
    None = 0,
    CanView = 1,
    CanViewOthers = 2,
    CanSearch = 4,
    CanEmail = 8,
    CanPostNew = 16,
    CanReplyOwn = 32,
    CanReplyOthers = 64,
    CanEditPost = 128,
    CanDeletePost = 256,
    CanDeleteThread = 512,
    CanOpenClose = 1024,
    CanMove = 2048,
    CanGetAttachment = 4096,
    CanPostAttachment = 8192,
    CanPostPoll = 16384,
    CanVote = 32768,
    CanThreadRate = 65536,
    FollowForumModeration = 131072,
    CanSeeDelNotice = 262144,
    CanViewThreads = 524288,
}

[Flags]
public enum ForumOption : long
{
    None = 0,
    Active = 1,
    AllowPosting = 2,
    CanContainThreads = 4,
    ModerateNewPost = 8,
    ModerateNewThread = 16,
    ModerateAttach = 32,
    AllowBbCode = 64,
    AllowImages = 128,
    AllowHtml = 256,
    AllowSmilies = 512,
    AllowIcons = 1024,
    AllowRatings = 2048,
    CountPosts = 4096,
    CanHavePassword = 8192,
    IndexPosts = 16384,
    StyleOverride = 32768,
    ShowOnForumJump = 65536,
}

[Flags]
public enum AdminPerm : long
{
    None = 0,
    IsModerator = 1,
    CanControlPanel = 2,
}

/// <summary>Zur Laufzeit gültige Bitwerte (aus dem Datastore geladen oder Standard).</summary>
public sealed class Bitfields
{
    public Dictionary<string, long> ForumPermissions { get; init; } = Defaults<ForumPerm>();
    public Dictionary<string, long> ForumOptions { get; init; } = Defaults<ForumOption>();
    public Dictionary<string, long> AdminPermissions { get; init; } = Defaults<AdminPerm>();
    public bool LoadedFromDatastore { get; init; }

    private static Dictionary<string, long> Defaults<T>() where T : struct, Enum =>
        Enum.GetValues<T>().Where(v => Convert.ToInt64(v) != 0)
            .ToDictionary(v => v.ToString().ToLowerInvariant(), v => Convert.ToInt64(v), StringComparer.OrdinalIgnoreCase);

    public long Perm(ForumPerm p) => ForumPermissions.TryGetValue(p.ToString(), out var v) ? v : (long)p;
    public long Opt(ForumOption o) => ForumOptions.TryGetValue(o.ToString(), out var v) ? v : (long)o;
    public long Admin(AdminPerm a) => AdminPermissions.TryGetValue(a.ToString(), out var v) ? v : (long)a;

    /// <summary>Liest die vB-Bitfeld-Struktur: ['ugp']['forumpermissions'], ['misc']['forumoptions'], ['ugp']['adminpermissions'].</summary>
    public static Bitfields FromDatastore(object? parsed)
    {
        if (parsed is not Dictionary<string, object?> root) return new Bitfields();
        Dictionary<string, long>? Get(string group, string name)
        {
            if (!root.TryGetValue(group, out var g) || g is not Dictionary<string, object?> gd) return null;
            if (!gd.TryGetValue(name, out var f) || f is not Dictionary<string, object?> fd) return null;
            var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in fd)
            {
                // vB 4: Wert ist entweder die Zahl oder ein Array mit 'value'
                long? n = v switch
                {
                    long l => l,
                    string s when long.TryParse(s, out var x) => x,
                    Dictionary<string, object?> d when d.TryGetValue("value", out var vv) => Convert.ToInt64(vv),
                    _ => null
                };
                if (n.HasValue) result[k] = n.Value;
            }
            return result.Count > 0 ? result : null;
        }

        var fp = Get("ugp", "forumpermissions");
        var fo = Get("misc", "forumoptions");
        var ap = Get("ugp", "adminpermissions");
        var def = new Bitfields();
        return new Bitfields
        {
            ForumPermissions = fp ?? def.ForumPermissions,
            ForumOptions = fo ?? def.ForumOptions,
            AdminPermissions = ap ?? def.AdminPermissions,
            LoadedFromDatastore = fp != null || fo != null
        };
    }
}
