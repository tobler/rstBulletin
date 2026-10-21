using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using VbForum.Core.Models;
using VbForum.Core.Text;

namespace VbForum.Core.BbCode;

public sealed class BbCodeContext
{
    public bool AllowBbCode { get; init; } = true;
    public bool AllowImages { get; init; } = true;
    public bool AllowSmilies { get; init; } = true;

    /// <summary>Auflösen von [ATTACH]id[/ATTACH] – null = unbekannt/kein Zugriff.</summary>
    public Func<long, Attachment?>? ResolveAttachment { get; init; }
    /// <summary>IDs von Anhängen, die inline angezeigt wurden (werden unter dem Beitrag nicht nochmal gelistet).</summary>
    public HashSet<long> InlinedAttachments { get; } = new();

    /// <summary>Umschreiben externer Bild-URLs (z. B. http → Bild-Proxy).</summary>
    public Func<string, string>? ImageUrl { get; init; }

    public IReadOnlyList<Smilie> Smilies { get; init; } = Array.Empty<Smilie>();
    public string SmilieBaseUrl { get; init; } = "/";
    public IReadOnlyList<CustomBbCode> CustomCodes { get; init; } = Array.Empty<CustomBbCode>();

    public string AttachmentUrl(long id) => $"/attachment/{id}";
    public string AttachmentThumbUrl(long id) => $"/attachment/{id}/thumb";
}

/// <summary>
/// BBCode → HTML, kompatibel zu vBulletin 4. Alles wird escaped; nur die erzeugten Tags sind HTML.
/// Nicht geschlossene oder unbekannte Tags bleiben als Text stehen (wie bei vB).
/// </summary>
public static partial class BbCodeRenderer
{
    private const int MaxDepth = 40;

    private static readonly HashSet<string> RawTags = new(StringComparer.OrdinalIgnoreCase)
        { "code", "php", "html", "noparse", "img", "attach", "video", "sigpic", "email" };

    private static readonly HashSet<string> BuiltIn = new(StringComparer.OrdinalIgnoreCase)
    {
        "b", "i", "u", "s", "strike", "color", "size", "font", "highlight", "left", "center", "right", "indent",
        "url", "email", "thread", "post", "list", "img", "quote", "code", "php", "html", "noparse", "attach",
        "video", "sigpic", "hr", "sub", "sup", "table", "tr", "td"
    };

    private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
        { "quote", "code", "php", "html", "list", "left", "center", "right", "indent", "video", "hr", "table" };

    [GeneratedRegex(@"\[(/?)([a-zA-Z*][a-zA-Z0-9_]*|\*)(?:=([^\]\r\n]*))?\]")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"(?<![=""'\]/\w])((?:https?://|www\.)[^\s<>\[\]""']+[^\s<>\[\]""'.,;:!?)])", RegexOptions.IgnoreCase)]
    private static partial Regex AutoLinkRegex();

    [GeneratedRegex(@"^(#[0-9a-fA-F]{3}|#[0-9a-fA-F]{6}|[a-zA-Z]{3,20}|rgb\(\s*\d{1,3}\s*,\s*\d{1,3}\s*,\s*\d{1,3}\s*\))$")]
    private static partial Regex ColorRegex();

    [GeneratedRegex(@"^[\w ,'\-]{1,60}$")]
    private static partial Regex FontRegex();

    [GeneratedRegex(@"^(?:https?://)?(?:www\.|m\.)?(?:youtube\.com/(?:watch\?v=|embed/|shorts/)|youtu\.be/)([A-Za-z0-9_-]{6,20})", RegexOptions.IgnoreCase)]
    private static partial Regex YouTubeRegex();

    // ---------------- Baum ----------------

    private sealed class Node
    {
        public string Name = "";
        public string? Option;
        public string OpenRaw = "";
        public string? Text;      // Textknoten
        public string? Raw;       // Inhalt roher Tags (code, img …)
        public bool Closed = true;
        public readonly List<Node> Children = new();
        public bool IsText => Text != null;
    }

    public static string ToHtml(string? bbcode, BbCodeContext? ctx = null)
    {
        ctx ??= new BbCodeContext();
        if (string.IsNullOrEmpty(bbcode)) return "";
        var text = bbcode.Replace("\r\n", "\n").Replace('\r', '\n');
        if (!ctx.AllowBbCode)
            return RenderText(text, ctx, autoLink: true);

        var root = Parse(text, ctx);
        var sb = new StringBuilder(text.Length + text.Length / 3);
        RenderChildren(root, sb, ctx, depth: 0);
        return sb.ToString();
    }

    private static bool IsKnown(string name, BbCodeContext ctx, bool hasOption) =>
        BuiltIn.Contains(name) ||
        ctx.CustomCodes.Any(c => c.BbCodeTag.Equals(name, StringComparison.OrdinalIgnoreCase) && (c.TwoParams == 1) == hasOption);

    private static Node Parse(string input, BbCodeContext ctx)
    {
        var root = new Node { Name = "#root" };
        var stack = new List<Node> { root };
        int pos = 0;

        void AddText(string s)
        {
            if (s.Length == 0) return;
            var top = stack[^1];
            if (top.Children.Count > 0 && top.Children[^1].IsText) top.Children[^1].Text += s;
            else top.Children.Add(new Node { Text = s });
        }

        var m = TagRegex().Match(input, 0);
        while (m.Success)
        {
            AddText(input[pos..m.Index]);
            pos = m.Index + m.Length;
            bool closing = m.Groups[1].Value == "/";
            string name = m.Groups[2].Value.ToLowerInvariant();
            string? option = m.Groups[3].Success ? m.Groups[3].Value : null;

            if (!closing && RawTags.Contains(name))
            {
                var closeTag = "[/" + name + "]";
                int end = input.IndexOf(closeTag, pos, StringComparison.OrdinalIgnoreCase);
                if (end >= 0)
                {
                    stack[^1].Children.Add(new Node { Name = name, Option = option, OpenRaw = m.Value, Raw = input[pos..end] });
                    pos = end + closeTag.Length;
                }
                else AddText(m.Value);
            }
            else if (!closing && name == "*")
            {
                int listIdx = stack.FindLastIndex(n => n.Name == "list");
                if (listIdx > 0)
                {
                    while (stack.Count - 1 > listIdx) stack.RemoveAt(stack.Count - 1);
                    var li = new Node { Name = "*" };
                    stack[^1].Children.Add(li);
                    stack.Add(li);
                }
                else AddText(m.Value);
            }
            else if (!closing && name == "hr")
            {
                stack[^1].Children.Add(new Node { Name = "hr" });
            }
            else if (!closing && stack.Count < MaxDepth && IsKnown(name, ctx, option != null))
            {
                var n = new Node { Name = name, Option = option, OpenRaw = m.Value, Closed = false };
                stack[^1].Children.Add(n);
                stack.Add(n);
            }
            else if (closing && name != "*")
            {
                int k = stack.FindLastIndex(n => n.Name == name);
                if (k > 0)
                {
                    stack[k].Closed = true;
                    // dazwischen offene Tags gelten als implizit geschlossen
                    for (int j = k + 1; j < stack.Count; j++) stack[j].Closed = true;
                    stack.RemoveRange(k, stack.Count - k);
                }
                else AddText(m.Value);
            }
            else AddText(m.Value);

            m = TagRegex().Match(input, pos);
        }
        AddText(input[pos..]);
        foreach (var n in stack.Skip(1)) if (n.Name == "*") n.Closed = true;
        return root;
    }

    // ---------------- Rendering ----------------

    private static void RenderChildren(Node parent, StringBuilder sb, BbCodeContext ctx, int depth)
    {
        bool skipNewline = false;
        for (int i = 0; i < parent.Children.Count; i++)
        {
            var n = parent.Children[i];
            if (n.IsText)
            {
                var t = n.Text!;
                if (skipNewline && t.StartsWith('\n')) t = t[1..];
                // Zeilenumbruch direkt vor einem Block-Element entfällt
                if (i + 1 < parent.Children.Count && IsBlock(parent.Children[i + 1]) && t.EndsWith('\n')) t = t[..^1];
                sb.Append(RenderText(t, ctx, autoLink: true));
                skipNewline = false;
            }
            else
            {
                RenderNode(n, sb, ctx, depth + 1);
                skipNewline = IsBlock(n);
            }
        }
    }

    private static bool IsBlock(Node n) => !n.IsText && n.Closed && BlockTags.Contains(n.Name);

    private static string Inner(Node n, BbCodeContext ctx, int depth, bool trimNewlines = false)
    {
        var sb = new StringBuilder();
        if (trimNewlines)
        {
            if (n.Children.Count > 0 && n.Children[0].IsText && n.Children[0].Text!.StartsWith('\n'))
                n.Children[0].Text = n.Children[0].Text![1..];
            if (n.Children.Count > 0 && n.Children[^1].IsText && n.Children[^1].Text!.EndsWith('\n'))
                n.Children[^1].Text = n.Children[^1].Text![..^1];
        }
        RenderChildren(n, sb, ctx, depth);
        return sb.ToString();
    }

    private static void RenderNode(Node n, StringBuilder sb, BbCodeContext ctx, int depth)
    {
        if (!n.Closed && n.Raw == null && n.Name != "*")
        {
            // nicht geschlossen → als Text ausgeben
            sb.Append(RenderText(n.OpenRaw, ctx, autoLink: false));
            RenderChildren(n, sb, ctx, depth);
            return;
        }

        string opt = n.Option == null ? "" : HtmlUtil.DecodeNumericEntities(n.Option).Trim().Trim('"', '\'');

        switch (n.Name)
        {
            case "b": Wrap(sb, "strong", Inner(n, ctx, depth)); break;
            case "i": Wrap(sb, "em", Inner(n, ctx, depth)); break;
            case "u": Wrap(sb, "u", Inner(n, ctx, depth)); break;
            case "s": case "strike": Wrap(sb, "s", Inner(n, ctx, depth)); break;
            case "sub": Wrap(sb, "sub", Inner(n, ctx, depth)); break;
            case "sup": Wrap(sb, "sup", Inner(n, ctx, depth)); break;
            case "highlight": Wrap(sb, "mark", Inner(n, ctx, depth)); break;
            case "hr": sb.Append("<hr class=\"bb-hr\">"); break;

            case "color":
                if (ColorRegex().IsMatch(opt)) sb.Append("<span style=\"color:").Append(Attr(opt)).Append("\">").Append(Inner(n, ctx, depth)).Append("</span>");
                else sb.Append(Inner(n, ctx, depth));
                break;

            case "size":
                sb.Append("<span style=\"font-size:").Append(SizeToCss(opt)).Append("\">").Append(Inner(n, ctx, depth)).Append("</span>");
                break;

            case "font":
                if (FontRegex().IsMatch(opt)) sb.Append("<span style=\"font-family:").Append(Attr(opt)).Append("\">").Append(Inner(n, ctx, depth)).Append("</span>");
                else sb.Append(Inner(n, ctx, depth));
                break;

            case "left": case "center": case "right":
                sb.Append("<div style=\"text-align:").Append(n.Name).Append("\">").Append(Inner(n, ctx, depth, true)).Append("</div>");
                break;

            case "indent":
            {
                int level = int.TryParse(opt, out var l) ? Math.Clamp(l, 1, 5) : 1;
                sb.Append("<div style=\"margin-left:").Append(level * 2).Append("em\">").Append(Inner(n, ctx, depth, true)).Append("</div>");
                break;
            }

            case "url":
            {
                string label;
                string target;
                if (n.Option != null) { target = opt; label = Inner(n, ctx, depth); }
                else { target = PlainOf(n); label = RenderText(target, ctx, autoLink: false); }
                var href = SafeUrl(target);
                if (href == null) sb.Append(label);
                else sb.Append(LinkTag(href)).Append(string.IsNullOrWhiteSpace(label) ? Attr(href) : label).Append("</a>");
                break;
            }

            case "email":
            {
                var addr = n.Option != null ? opt : HtmlUtil.DecodeNumericEntities(n.Raw ?? "").Trim();
                var label = n.Option != null ? RenderText(n.Raw ?? addr, ctx, false) : Attr(addr);
                if (addr.Contains('@') && !addr.Any(char.IsWhiteSpace))
                    sb.Append("<a href=\"mailto:").Append(Attr(addr)).Append("\">").Append(label).Append("</a>");
                else sb.Append(label);
                break;
            }

            case "thread": case "post":
            {
                var id = n.Option ?? PlainOf(n);
                var label = n.Option != null ? Inner(n, ctx, depth) : Attr(id);
                if (long.TryParse(id.Trim(), out var tid))
                    sb.Append("<a href=\"/").Append(n.Name).Append('/').Append(tid).Append("\">").Append(label).Append("</a>");
                else sb.Append(label);
                break;
            }

            case "list":
            {
                var type = opt.ToLowerInvariant();
                string open = type switch
                {
                    "1" => "<ol class=\"bb-list\">",
                    "a" => "<ol class=\"bb-list\" type=\"a\">",
                    "i" => "<ol class=\"bb-list\" type=\"i\">",
                    _ => "<ul class=\"bb-list\">"
                };
                sb.Append(open);
                foreach (var c in n.Children)
                {
                    if (c.Name == "*")
                        sb.Append("<li>").Append(Inner(c, ctx, depth, true)).Append("</li>");
                    else if (!(c.IsText && string.IsNullOrWhiteSpace(c.Text)))
                    {
                        var tmp = new Node(); tmp.Children.Add(c);
                        sb.Append("<li>").Append(Inner(tmp, ctx, depth, true)).Append("</li>");
                    }
                }
                sb.Append(type is "1" or "a" or "i" ? "</ol>" : "</ul>");
                break;
            }

            case "quote":
            {
                string? who = null; long? postId = null;
                if (n.Option != null)
                {
                    var parts = opt.Split(';', 2);
                    who = HtmlUtil.CleanTitle(parts[0].Trim());
                    if (parts.Length == 2 && long.TryParse(parts[1].Trim(), out var pid)) postId = pid;
                }
                sb.Append("<blockquote class=\"bb-quote\">");
                if (!string.IsNullOrWhiteSpace(who))
                {
                    sb.Append("<div class=\"bb-quote-head\">");
                    if (postId.HasValue) sb.Append("<a href=\"/post/").Append(postId.Value).Append("\">");
                    sb.Append(Attr(who)).Append(" schrieb:");
                    if (postId.HasValue) sb.Append("</a>");
                    sb.Append("</div>");
                }
                sb.Append("<div class=\"bb-quote-body\">").Append(Inner(n, ctx, depth, true)).Append("</div></blockquote>");
                break;
            }

            case "code": case "php": case "html":
            {
                var code = HtmlUtil.DecodeNumericEntities(n.Raw ?? "");
                code = code.Trim('\n');
                sb.Append("<pre class=\"bb-code\" data-lang=\"").Append(n.Name).Append("\"><code>")
                  .Append(HtmlUtil.Escape(code)).Append("</code></pre>");
                break;
            }

            case "noparse":
                sb.Append(RenderText(n.Raw ?? "", ctx, autoLink: false));
                break;

            case "img":
            {
                var url = HtmlUtil.DecodeNumericEntities(n.Raw ?? "").Trim();
                var safe = SafeUrl(url, imagesOnly: true);
                if (safe == null) { sb.Append(RenderText(n.Raw ?? "", ctx, false)); break; }
                if (!ctx.AllowImages) { sb.Append(LinkTag(safe)).Append(Attr(safe)).Append("</a>"); break; }
                var src = ctx.ImageUrl?.Invoke(safe) ?? safe;
                sb.Append("<img class=\"bb-img\" src=\"").Append(Attr(src)).Append("\" alt=\"Bild\" loading=\"lazy\" decoding=\"async\">");
                break;
            }

            case "attach":
            {
                var raw = (n.Raw ?? "").Trim();
                if (!long.TryParse(raw, out var aid)) { sb.Append(RenderText(n.OpenRaw + raw + "[/attach]", ctx, false)); break; }
                var a = ctx.ResolveAttachment?.Invoke(aid);
                if (a == null)
                {
                    sb.Append("<span class=\"bb-attach-missing\">[Anhang ").Append(aid).Append(" nicht verfügbar]</span>");
                    break;
                }
                ctx.InlinedAttachments.Add(aid);
                if (a.IsImage && ctx.AllowImages)
                {
                    // Inline immer das Originalbild (per CSS begrenzt); Thumbnails nur in der Galerie unter dem Beitrag
                    var src = ctx.AttachmentUrl(aid);
                    sb.Append("<a class=\"bb-attach-img\" href=\"").Append(ctx.AttachmentUrl(aid)).Append("\" data-lightbox>")
                      .Append("<img src=\"").Append(src).Append("\" alt=\"").Append(Attr(a.DisplayName)).Append("\" loading=\"lazy\" decoding=\"async\"");
                    if (a.Width > 0 && a.Height > 0) sb.Append(" width=\"").Append(a.Width).Append("\" height=\"").Append(a.Height).Append('"');
                    sb.Append("></a>");
                }
                else
                {
                    sb.Append("<a class=\"bb-attach-file\" href=\"").Append(ctx.AttachmentUrl(aid)).Append("\">")
                      .Append(Attr(a.DisplayName)).Append(" (").Append(HtmlUtil.FormatBytes(a.FileSize)).Append(")</a>");
                }
                break;
            }

            case "video":
            {
                var url = HtmlUtil.DecodeNumericEntities(n.Raw ?? "").Trim();
                var yt = YouTubeRegex().Match(url);
                string? ytId = yt.Success ? yt.Groups[1].Value : null;
                // vB 4: [video=youtube;ID]url[/video]
                if (ytId == null && opt.StartsWith("youtube", StringComparison.OrdinalIgnoreCase))
                {
                    var p = opt.Split(';');
                    if (p.Length > 1 && Regex.IsMatch(p[1], "^[A-Za-z0-9_-]{6,20}$")) ytId = p[1];
                }
                if (ytId != null)
                {
                    sb.Append("<div class=\"bb-video\"><iframe src=\"https://www.youtube-nocookie.com/embed/").Append(ytId)
                      .Append("\" title=\"YouTube-Video\" loading=\"lazy\" allowfullscreen referrerpolicy=\"strict-origin-when-cross-origin\"></iframe></div>");
                }
                else
                {
                    var safe = SafeUrl(url);
                    if (safe != null) sb.Append(LinkTag(safe)).Append("Video ansehen").Append("</a>");
                }
                break;
            }

            case "sigpic":
                break;

            case "table":
                sb.Append("<div class=\"bb-table-wrap\"><table class=\"bb-table\">").Append(Inner(n, ctx, depth, true)).Append("</table></div>");
                break;
            case "tr": sb.Append("<tr>").Append(Inner(n, ctx, depth, true)).Append("</tr>"); break;
            case "td": sb.Append("<td>").Append(Inner(n, ctx, depth, true)).Append("</td>"); break;

            default:
                RenderCustom(n, opt, sb, ctx, depth);
                break;
        }
    }

    private static void RenderCustom(Node n, string opt, StringBuilder sb, BbCodeContext ctx, int depth)
    {
        var def = ctx.CustomCodes.FirstOrDefault(c =>
            c.BbCodeTag.Equals(n.Name, StringComparison.OrdinalIgnoreCase) && (c.TwoParams == 1) == (n.Option != null));
        var inner = Inner(n, ctx, depth);
        if (def?.BbCodeReplacement == null) { sb.Append(inner); return; }
        // Ersetzungen stammen vom Admin (vertrauenswürdig); Parameter werden escaped eingesetzt.
        var html = def.BbCodeReplacement
            .Replace("{param}", inner, StringComparison.OrdinalIgnoreCase)
            .Replace("{option}", Attr(opt), StringComparison.OrdinalIgnoreCase);
        sb.Append(html);
    }

    private static void Wrap(StringBuilder sb, string tag, string inner) =>
        sb.Append('<').Append(tag).Append('>').Append(inner).Append("</").Append(tag).Append('>');

    private static string PlainOf(Node n)
    {
        var sb = new StringBuilder();
        void Walk(Node x) { if (x.IsText) sb.Append(x.Text); else if (x.Raw != null) sb.Append(x.Raw); else x.Children.ForEach(Walk); }
        n.Children.ForEach(Walk);
        return HtmlUtil.DecodeNumericEntities(sb.ToString()).Trim();
    }

    private static string SizeToCss(string opt)
    {
        if (int.TryParse(opt.TrimEnd('p', 'x', 't'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
        {
            if (opt.EndsWith("px") || opt.EndsWith("pt") || v > 7) return Math.Clamp(v, 9, 36) + "px";
            return v switch { <= 1 => "0.75em", 2 => "0.875em", 3 => "1em", 4 => "1.15em", 5 => "1.4em", 6 => "1.75em", _ => "2.2em" };
        }
        return "1em";
    }

    private static string LinkTag(string href)
    {
        bool external = href.StartsWith("http", StringComparison.OrdinalIgnoreCase) || href.StartsWith("ftp", StringComparison.OrdinalIgnoreCase);
        return external
            ? $"<a href=\"{Attr(href)}\" rel=\"nofollow ugc noopener\" target=\"_blank\">"
            : $"<a href=\"{Attr(href)}\">";
    }

    /// <summary>Erlaubt nur http(s)/ftp/mailto bzw. relative Pfade. Ergänzt "http://" bei "www.".</summary>
    public static string? SafeUrl(string? url, bool imagesOnly = false)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        url = url.Trim();
        if (url.Any(c => char.IsControl(c) || c is '"' or '<' or '>' or ' ')) url = url.Replace(" ", "%20");
        if (url.Any(c => char.IsControl(c) || c is '"' or '<' or '>')) return null;
        if (url.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) url = "http://" + url;
        if (url.StartsWith('/') && !url.StartsWith("//")) return url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return null;
        var ok = imagesOnly
            ? u.Scheme is "http" or "https"
            : u.Scheme is "http" or "https" or "ftp" or "mailto";
        return ok ? u.OriginalString : null;
    }

    private static string Attr(string s) => HtmlUtil.Escape(s);

    // ---------------- Text ----------------

    private static string RenderText(string raw, BbCodeContext ctx, bool autoLink)
    {
        var text = HtmlUtil.DecodeNumericEntities(raw);
        var sb = new StringBuilder(text.Length + 16);
        if (autoLink)
        {
            int last = 0;
            foreach (Match m in AutoLinkRegex().Matches(text))
            {
                AppendPlain(sb, text[last..m.Index], ctx);
                var href = SafeUrl(m.Value);
                if (href != null) sb.Append(LinkTag(href)).Append(Attr(m.Value)).Append("</a>");
                else AppendPlain(sb, m.Value, ctx);
                last = m.Index + m.Length;
            }
            AppendPlain(sb, text[last..], ctx);
        }
        else AppendPlain(sb, text, ctx);
        return sb.ToString();
    }

    private static void AppendPlain(StringBuilder sb, string text, BbCodeContext ctx)
    {
        if (text.Length == 0) return;
        var esc = HtmlUtil.Escape(text);
        if (ctx.AllowSmilies && ctx.Smilies.Count > 0) esc = ReplaceSmilies(esc, ctx);
        sb.Append(esc.Replace("\n", "<br>\n"));
    }

    private sealed class SmilieSet
    {
        public required Regex Rx;
        public required Dictionary<string, Smilie> Map;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IReadOnlyList<Smilie>, SmilieSet> SmilieCache = new();

    private static string ReplaceSmilies(string escaped, BbCodeContext ctx)
    {
        var set = SmilieCache.GetValue(ctx.Smilies, list =>
        {
            var map = list.Where(s => !string.IsNullOrWhiteSpace(s.SmilieText))
                .GroupBy(s => HtmlUtil.Escape(HtmlUtil.CleanTitle(s.SmilieText)))
                .ToDictionary(g => g.Key, g => g.First());
            var alts = map.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape);
            return new SmilieSet
            {
                Map = map,
                Rx = new Regex("(?<=^|[\\s>(])(" + string.Join("|", alts) + ")(?=$|[\\s<.,!?)])", RegexOptions.Compiled)
            };
        });
        if (set.Map.Count == 0) return escaped;
        return set.Rx.Replace(escaped, m =>
        {
            if (!set.Map.TryGetValue(m.Value, out var s)) return m.Value;
            var path = s.SmiliePath.StartsWith("http", StringComparison.OrdinalIgnoreCase) || s.SmiliePath.StartsWith('/')
                ? s.SmiliePath : ctx.SmilieBaseUrl.TrimEnd('/') + "/" + s.SmiliePath;
            return $"<img class=\"bb-smilie\" src=\"{Attr(path)}\" alt=\"{m.Value}\" title=\"{Attr(HtmlUtil.CleanTitle(s.Title))}\">";
        });
    }
}
