using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace VbForum.Core.Text;

public static partial class HtmlUtil
{
    [GeneratedRegex(@"&#(x[0-9a-fA-F]+|[0-9]+);")]
    private static partial Regex NumericEntityRegex();

    /// <summary>
    /// vBulletin speichert Zeichen, die das Formular-Charset nicht darstellen konnte, als numerische Entities
    /// (&amp;#8364;). Diese werden vor dem Escaping zu echten Zeichen dekodiert. Ungültige bleiben stehen.
    /// </summary>
    public static string DecodeNumericEntities(string? s)
    {
        if (string.IsNullOrEmpty(s) || !s.Contains("&#", StringComparison.Ordinal)) return s ?? "";
        return NumericEntityRegex().Replace(s, m =>
        {
            var v = m.Groups[1].Value;
            int cp;
            try
            {
                cp = v[0] is 'x' or 'X'
                    ? int.Parse(v.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                    : int.Parse(v, CultureInfo.InvariantCulture);
            }
            catch (OverflowException) { return m.Value; }
            return cp is > 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF)
                ? char.ConvertFromUtf32(cp)
                : m.Value;
        });
    }

    /// <summary>Minimales HTML-Escaping (&amp; &lt; &gt; " ') – Unicode bleibt unverändert.</summary>
    public static string Escape(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.AsSpan().IndexOfAny("&<>\"'") < 0) return s;
        var sb = new StringBuilder(s.Length + 16);
        foreach (var c in s)
            sb.Append(c switch { '&' => "&amp;", '<' => "&lt;", '>' => "&gt;", '"' => "&quot;", '\'' => "&#39;", _ => c.ToString() });
        return sb.ToString();
    }

    /// <summary>Für Titel/Namen aus der vB-DB: Entities dekodieren (vB speichert Titel teils HTML-escaped).</summary>
    public static string CleanTitle(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        // vB legt Thread-Titel, Forennamen und Benutzernamen HTML-kodiert ab (&amp;, &quot;, &#8364;)
        return WebUtility.HtmlDecode(s);
    }

    /// <summary>Gegenstück zu CleanTitle: so legt vB Titel/Benutzernamen ab (htmlspecialchars_uni).</summary>
    public static string ToVbStoredTitle(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '&': sb.Append("&amp;"); break;
                case '"': sb.Append("&quot;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                default: sb.Append(ch); break;
            }
        }
        return sb.ToString();
    }

    public static string Slugify(string? title, int max = 60)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        var s = CleanTitle(title).ToLowerInvariant()
            .Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue").Replace("ß", "ss");
        var norm = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        bool dash = false;
        foreach (var c in norm)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9') { sb.Append(c); dash = false; }
            else if (!dash && sb.Length > 0) { sb.Append('-'); dash = true; }
            if (sb.Length >= max) break;
        }
        return sb.ToString().Trim('-');
    }

    [GeneratedRegex(@"\[(/?)([a-z*]+)(=[^\]]*)?\]", RegexOptions.IgnoreCase)]
    private static partial Regex BbTagRegex();

    [GeneratedRegex(@"\[quote[^\]]*\].*?\[/quote\]", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex QuoteBlockRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    /// <summary>Klartext-Auszug aus BBCode (für Suchtreffer, Vorschauen, Mails).</summary>
    public static string PlainText(string? bbcode, int max = 300)
    {
        if (string.IsNullOrEmpty(bbcode)) return "";
        var s = QuoteBlockRegex().Replace(bbcode, " ");
        s = BbTagRegex().Replace(s, " ");
        s = DecodeNumericEntities(s);
        s = WhitespaceRegex().Replace(s, " ").Trim();
        return s.Length <= max ? s : s[..max].TrimEnd() + " …";
    }

    public static string Initials(string? name)
    {
        var n = CleanTitle(name).Trim();
        if (n.Length == 0) return "?";
        var letters = n.Where(char.IsLetterOrDigit).Take(2).ToArray();
        return letters.Length == 0 ? n[..1].ToUpperInvariant() : new string(letters).ToUpperInvariant();
    }

    public static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:0.0} MB",
        _ => $"{bytes / 1024.0 / 1024.0 / 1024.0:0.0} GB"
    };
}
