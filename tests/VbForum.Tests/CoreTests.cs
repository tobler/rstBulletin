using VbForum.Core.BbCode;
using VbForum.Core.Models;
using VbForum.Core.Php;
using VbForum.Core.Security;
using VbForum.Core.Text;
using VbForum.Core.Vb;

namespace VbForum.Tests;

public class BbCodeTests
{
    private static string H(string s, BbCodeContext? c = null) => BbCodeRenderer.ToHtml(s, c);

    [Fact] public void EscapesHtml() => Assert.Equal("&lt;script&gt;alert(1)&lt;/script&gt;", H("<script>alert(1)</script>"));
    [Fact] public void Bold() => Assert.Equal("<strong>fett</strong>", H("[b]fett[/b]"));
    [Fact] public void CaseInsensitiveTags() => Assert.Equal("<strong>x</strong>", H("[B]x[/b]"));
    [Fact] public void UnclosedTagStaysText() => Assert.Equal("[b]offen", H("[b]offen"));
    [Fact] public void UnknownTagStaysText() => Assert.Equal("[foo]x[/foo]", H("[foo]x[/foo]"));
    [Fact] public void Newlines() => Assert.Equal("a<br>\nb", H("a\nb"));

    [Fact] public void NumericEntitiesDecoded() => Assert.Equal("Preis 5 €", H("Preis 5 &#8364;"));
    [Fact] public void EmojiEntityDecoded() => Assert.Equal("😀", H("&#128512;"));
    [Fact] public void NamedEntityStaysLiteral() => Assert.Equal("&amp;amp;", H("&amp;"));
    [Fact] public void EntityCannotCreateTag() => Assert.Equal("[b]x", H("&#91;b]x"));

    [Fact]
    public void QuoteWithPostLink()
    {
        var h = H("[quote=Kathi_R;123]Hallo[/quote]");
        Assert.Contains("<a href=\"/post/123\">Kathi_R schrieb:</a>", h);
        Assert.Contains("Hallo", h);
    }

    [Fact]
    public void NestedQuotes()
    {
        var h = H("[quote=A]außen [quote=B]innen[/quote][/quote]");
        Assert.Equal(2, CountOf(h, "<blockquote"));
    }

    [Fact]
    public void CodeIsRaw()
    {
        var h = H("[code][b]nicht fett[/b] <x>[/code]");
        Assert.Contains("[b]nicht fett[/b] &lt;x&gt;", h);
        Assert.DoesNotContain("<strong>", h);
    }

    [Fact]
    public void UrlWithJavascriptIsRejected()
    {
        var h = H("[url=javascript:alert(1)]klick[/url]");
        Assert.DoesNotContain("href", h);
        Assert.Contains("klick", h);
    }

    [Fact]
    public void UrlGetsNofollow()
    {
        var h = H("[url=https://example.org]Beispiel[/url]");
        Assert.Contains("href=\"https://example.org\"", h);
        Assert.Contains("nofollow", h);
    }

    [Fact]
    public void QuoteInUrlOptionDoesNotBreakAttribute()
    {
        var h = H("[url=https://x.org/\"onmouseover=alert(1)]a[/url]");
        Assert.DoesNotContain("onmouseover=alert", h.Replace("&quot;", "\""));
    }

    [Fact]
    public void AutoLink() => Assert.Contains("href=\"https://example.org/a?b=1\"", H("siehe https://example.org/a?b=1."));

    [Fact]
    public void ImageThroughProxy()
    {
        var ctx = new BbCodeContext { ImageUrl = u => "/imgproxy?u=" + Uri.EscapeDataString(u) };
        var h = H("[img]http://example.org/a.jpg[/img]", ctx);
        Assert.Contains("src=\"/imgproxy?u=http%3A%2F%2Fexample.org%2Fa.jpg\"", h);
        Assert.Contains("loading=\"lazy\"", h);
    }

    [Fact] public void ImageJavascriptRejected() => Assert.DoesNotContain("<img", H("[img]javascript:alert(1)[/img]"));

    [Fact]
    public void ColorValidated()
    {
        Assert.Contains("color:red", H("[color=red]x[/color]"));
        Assert.DoesNotContain("style", H("[color=red;background:url(x)]x[/color]"));
    }

    [Fact]
    public void ListItems()
    {
        var h = H("[list]\n[*]eins\n[*]zwei\n[/list]");
        Assert.Equal("<ul class=\"bb-list\"><li>eins</li><li>zwei</li></ul>", h);
    }

    [Fact]
    public void OrderedList() => Assert.StartsWith("<ol class=\"bb-list\">", H("[list=1][*]a[/list]"));

    [Fact]
    public void InlineAttachment()
    {
        var ctx = new BbCodeContext
        {
            ResolveAttachment = id => id == 42
                ? new Attachment { AttachmentId = 42, Filename = "bild.jpg", Extension = "jpg", ThumbnailFilesize = 10 }
                : null
        };
        var h = H("[ATTACH=CONFIG]42[/ATTACH] und [ATTACH]7[/ATTACH]", ctx);
        Assert.Contains("src=\"/attachment/42\"", h);
        Assert.Contains("Anhang 7 nicht verfügbar", h);
        Assert.Contains(42L, ctx.InlinedAttachments);
    }

    [Fact]
    public void Smilies()
    {
        var ctx = new BbCodeContext { Smilies = new[] { new Smilie { SmilieText = ":)", SmiliePath = "images/smilies/smile.png", Title = "Lächeln" } } };
        Assert.Contains("<img class=\"bb-smilie\" src=\"/images/smilies/smile.png\"", H("Hallo :)", ctx));
        Assert.DoesNotContain("bb-smilie", H("a:)b", ctx));
    }

    [Fact]
    public void YouTubeVideo() => Assert.Contains("youtube-nocookie.com/embed/dQw4w9WgXcQ",
        H("[video=youtube;dQw4w9WgXcQ]https://www.youtube.com/watch?v=dQw4w9WgXcQ[/video]"));

    [Fact]
    public void OverlappingTagsDoNotThrow() => Assert.NotNull(H("[b][i]x[/b][/i]"));

    [Fact]
    public void DeepNestingIsLimited()
    {
        var s = string.Concat(Enumerable.Repeat("[b]", 5000)) + "x";
        Assert.NotNull(H(s));
    }

    private static int CountOf(string s, string sub) => (s.Length - s.Replace(sub, "").Length) / sub.Length;
}

public class PasswordTests
{
    [Fact]
    public void LegacyRoundTrip()
    {
        var (hash, salt) = VbPassword.CreateLegacy("Geheim123");
        Assert.Equal(30, salt.Length);
        Assert.True(VbPassword.VerifyLegacy("Geheim123", hash, salt));
        Assert.False(VbPassword.VerifyLegacy("geheim123", hash, salt));
    }

    [Fact]
    public void LegacyKnownValue()
    {
        // md5(md5("test") . "abc")
        var inner = VbPassword.Md5Hex("test");
        var expected = VbPassword.Md5Hex(inner + "abc");
        Assert.True(VbPassword.VerifyLegacy("test", expected, "abc"));
    }

    [Fact]
    public void LegacyLatin1EntityVariant()
    {
        // vB 4 (ISO-8859-1-Board) md5t im Browser die latin1-Bytes; Zeichen > 255 als &#NNN;
        var pw = "Bär€";
        var latin = System.Text.Encoding.Latin1.GetBytes("Bär&#8364;");
        var inner = VbPassword.Md5Hex(latin);
        var stored = VbPassword.Md5Hex(System.Text.Encoding.Latin1.GetBytes(inner + "s"));
        Assert.True(VbPassword.VerifyLegacy(pw, stored, "s"));
    }

    [Fact]
    public void Modern()
    {
        var h = VbPassword.HashModern("abc");
        Assert.True(VbPassword.VerifyModern("abc", h, out _));
        Assert.False(VbPassword.VerifyModern("abd", h, out _));
    }
}

public class TextTests
{
    [Fact] public void StorageKeepsLatin1() => Assert.Equal("Grüße €", VbText.ForStorage("Grüße €"));
    [Fact] public void StorageEncodesEmoji() => Assert.Equal("Hi &#128512;", VbText.ForStorage("Hi 😀"));
    [Fact] public void StorageEncodesCyrillic() => Assert.Equal("&#1046;", VbText.ForStorage("Ж"));
    [Fact] public void Slug() => Assert.Equal("lueftersteuerung-spinnt-nach-bios-update", HtmlUtil.Slugify("Lüftersteuerung spinnt nach BIOS-Update"));
    [Fact] public void CleanTitle() => Assert.Equal("A & B \"x\"", HtmlUtil.CleanTitle("A &amp; B &quot;x&quot;"));
}

public class PhpTests
{
    [Fact]
    public void ParsesNestedArray()
    {
        var s = "a:2:{s:3:\"ugp\";a:1:{s:16:\"forumpermissions\";a:2:{s:7:\"canview\";i:1;s:9:\"cansearch\";i:4;}}s:4:\"misc\";a:1:{s:12:\"forumoptions\";a:1:{s:6:\"active\";i:1;}}}";
        var bf = Bitfields.FromDatastore(PhpUnserializer.Parse(s));
        Assert.True(bf.LoadedFromDatastore);
        Assert.Equal(4, bf.Perm(ForumPerm.CanSearch));
        Assert.Equal(1, bf.Opt(ForumOption.Active));
    }

    [Fact]
    public void SerializeTouserarray()
    {
        var d = new Dictionary<string, object?> { ["cc"] = new Dictionary<long, object?> { [5] = "Max" } };
        Assert.Equal("a:1:{s:2:\"cc\";a:1:{i:5;s:3:\"Max\";}}", PhpUnserializer.Serialize(d));
    }
}
