using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Components;
using VbForum.Core.BbCode;
using VbForum.Core.Models;
using VbForum.Data;

namespace VbForum.Web.Infrastructure;

/// <summary>Signiert Bild-Proxy-URLs, damit der Proxy nicht als offener Proxy missbraucht werden kann.</summary>
public sealed class UrlSigner
{
    private readonly byte[] _key;

    public UrlSigner(ForumPaths paths)
    {
        var file = Path.Combine(paths.Data, "secret.key");
        if (!File.Exists(file)) File.WriteAllBytes(file, RandomNumberGenerator.GetBytes(32));
        _key = File.ReadAllBytes(file);
    }

    public string Sign(string value) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(value)))[..24];

    public bool Verify(string value, string? sig) =>
        sig != null && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sign(value)), Encoding.ASCII.GetBytes(sig));

    public string ProxyUrl(string url) => $"/imgproxy?u={Uri.EscapeDataString(url)}&s={Sign(url)}";
}

public sealed record ForumPaths(string Data, string WebRoot)
{
    public string ImageCache => Path.Combine(Data, "imgcache");
    public string Branding => Path.Combine(Data, "branding");
}

/// <summary>Rendert Beiträge/Signaturen/PNs mit Smilies, eigenen BBCodes, Anhängen und Bild-Proxy.</summary>
public sealed class PostRenderer(BoardCache cache, UrlSigner signer, ForumPaths paths)
{
    private List<Smilie>? _smilies;
    private BoardSnapshot? _smilieSnap;

    /// <summary>Nur Smilies, deren Bilddatei unter wwwroot liegt – fehlende bleiben als Text stehen.</summary>
    private List<Smilie> AvailableSmilies(BoardSnapshot s)
    {
        if (_smilies != null && ReferenceEquals(_smilieSnap, s)) return _smilies;
        var list = s.Smilies.Where(x => x.SmiliePath.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
            File.Exists(Path.Combine(paths.WebRoot, x.SmiliePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)))).ToList();
        _smilieSnap = s;
        return _smilies = list;
    }

    public async Task<List<Smilie>> SmiliesAsync() => AvailableSmilies(await cache.GetAsync());

    public async Task<BbCodeContext> ContextAsync(Forum? forum, IReadOnlyList<Attachment>? attachments = null, bool canSeeAttachments = true)
    {
        var s = await cache.GetAsync();
        var b = s.Bits;
        bool images = forum == null || (forum.Options & b.Opt(Core.Vb.ForumOption.AllowImages)) != 0;
        bool smilies = forum == null || (forum.Options & b.Opt(Core.Vb.ForumOption.AllowSmilies)) != 0;
        bool bbcode = forum == null || (forum.Options & b.Opt(Core.Vb.ForumOption.AllowBbCode)) != 0;
        var map = attachments?.ToDictionary(a => a.AttachmentId) ?? new();
        return new BbCodeContext
        {
            AllowBbCode = bbcode,
            AllowImages = images,
            AllowSmilies = smilies,
            Smilies = AvailableSmilies(s),
            CustomCodes = s.CustomCodes,
            SmilieBaseUrl = "/",
            ResolveAttachment = id => canSeeAttachments && map.TryGetValue(id, out var a) ? a : null,
            ImageUrl = s.Settings.ImageProxy
                ? url => url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? signer.ProxyUrl(url) : url
                : null,
        };
    }

    public MarkupString Render(string? bbcode, BbCodeContext ctx) => new(BbCodeRenderer.ToHtml(bbcode, ctx));

    public async Task<MarkupString> RenderSimpleAsync(string? bbcode) => Render(bbcode, await ContextAsync(null));

    /// <summary>Signaturen: ohne Anhänge, Bilder erlaubt (wie in vB üblich), auf 1000 Zeichen begrenzt.</summary>
    public async Task<MarkupString> RenderSignatureAsync(string? sig)
    {
        if (string.IsNullOrWhiteSpace(sig)) return new MarkupString("");
        if (sig.Length > 1000) sig = sig[..1000];
        return Render(sig, await ContextAsync(null));
    }
}
