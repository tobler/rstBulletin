using VbForum.Core.Text;
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

    private sealed record DefaultSmilie(string Name, string Title, string[] Codes);
    private List<DefaultSmilie>? _defaults;

    /// <summary>Mitgelieferter Smilie-Satz (wwwroot/images/smilies/default, Twemoji).</summary>
    private List<DefaultSmilie> Defaults()
    {
        if (_defaults != null) return _defaults;
        try
        {
            var json = File.ReadAllText(Path.Combine(paths.WebRoot, "images", "smilies", "default", "smilies.json"));
            _defaults = System.Text.Json.JsonSerializer.Deserialize<List<DefaultSmilie>>(json,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        }
        catch { _defaults = new(); }
        return _defaults;
    }

    /// <summary>
    /// Smilies aus vb_smilie. Fehlt die Bilddatei der alten Installation, wird ein passendes Bild aus dem
    /// mitgelieferten Satz genommen (über den Dateinamen, z. B. smile.png → smile.svg, oder über den Code wie „:)“).
    /// Ohne Treffer bleibt der Smilie als Text stehen.
    /// </summary>
    private List<Smilie> AvailableSmilies(BoardSnapshot s)
    {
        if (_smilies != null && ReferenceEquals(_smilieSnap, s)) return _smilies;
        var defaults = Defaults();
        var list = new List<Smilie>();
        foreach (var x in s.Smilies)
        {
            if (x.SmiliePath.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
                File.Exists(Path.Combine(paths.WebRoot, x.SmiliePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar))))
            {
                list.Add(x);
                continue;
            }
            var file = Path.GetFileNameWithoutExtension(x.SmiliePath).ToLowerInvariant();
            var code = HtmlUtil.CleanTitle(x.SmilieText);
            var d = defaults.FirstOrDefault(d => d.Name == file) ?? defaults.FirstOrDefault(d => d.Codes.Contains(code));
            if (d != null)
                list.Add(new Smilie { SmilieId = x.SmilieId, Title = x.Title, SmilieText = x.SmilieText, SmiliePath = $"images/smilies/default/{d.Name}.svg" });
        }
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
