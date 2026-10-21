using System.Text;

namespace VbForum.Core.Text;

/// <summary>
/// Das alte Board lief mit ISO-8859-1. Viele Spalten (vb_post, vb_thread, vb_forum, vb_pmtext …) sind latin1
/// (in MySQL = Windows-1252). Alles, was dort nicht darstellbar ist, legen wir – wie vB/der Browser früher –
/// als numerische Entity ab (&amp;#128512;). Gelesen wird über HtmlUtil.DecodeNumericEntities.
/// </summary>
public static class VbText
{
    private static readonly Encoding Cp1252;

    static VbText()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Cp1252 = Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    public static string ForStorage(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace("\r\n", "\n");
        StringBuilder? sb = null;
        var e = s.EnumerateRunes();
        int i = 0;
        foreach (var rune in e)
        {
            bool ok = rune.Value < 0x80 || IsCp1252(rune);
            if (!ok)
            {
                sb ??= new StringBuilder(s, 0, i, s.Length + 16);
                sb.Append("&#").Append(rune.Value).Append(';');
            }
            else
            {
                sb?.Append(rune.ToString());
            }
            i += rune.Utf16SequenceLength;
        }
        return sb?.ToString() ?? s;
    }

    private static bool IsCp1252(Rune r)
    {
        if (r.IsBmp == false) return false;
        Span<char> c = stackalloc char[1];
        c[0] = (char)r.Value;
        try { Span<byte> b = stackalloc byte[4]; Cp1252.GetBytes(c, b); return true; }
        catch (EncoderFallbackException) { return false; }
    }
}
