using System.Globalization;
using System.Text;

namespace VbForum.Core.Php;

/// <summary>
/// Minimaler Parser für PHP-serialize()-Strings (vB legt Caches wie die Bitfelder so in vb_datastore ab).
/// Liefert Dictionary&lt;string, object?&gt; für Arrays, sonst string/long/double/bool/null.
/// Längenangaben sind Byte-Längen (UTF-8/latin1), daher wird auf Bytes gearbeitet.
/// </summary>
public sealed class PhpUnserializer
{
    private readonly byte[] _b;
    private readonly Encoding _enc;
    private int _p;

    private PhpUnserializer(byte[] bytes, Encoding enc) { _b = bytes; _enc = enc; }

    public static object? Parse(string serialized) => Parse(Encoding.UTF8.GetBytes(serialized), Encoding.UTF8);

    public static object? Parse(byte[] bytes, Encoding? enc = null)
    {
        var p = new PhpUnserializer(bytes, enc ?? Encoding.UTF8);
        return p.ReadValue();
    }

    private object? ReadValue()
    {
        char type = (char)_b[_p];
        switch (type)
        {
            case 'N': _p += 2; return null;
            case 'b': { _p += 2; var v = ReadUntil(';'); return v == "1"; }
            case 'i': { _p += 2; return long.Parse(ReadUntil(';'), CultureInfo.InvariantCulture); }
            case 'd': { _p += 2; return double.Parse(ReadUntil(';'), CultureInfo.InvariantCulture); }
            case 's':
            {
                _p += 2;
                int len = int.Parse(ReadUntil(':'), CultureInfo.InvariantCulture);
                _p++; // "
                var s = _enc.GetString(_b, _p, len);
                _p += len + 2; // ";
                return s;
            }
            case 'a':
            {
                _p += 2;
                int count = int.Parse(ReadUntil(':'), CultureInfo.InvariantCulture);
                _p++; // {
                var dict = new Dictionary<string, object?>(count, StringComparer.Ordinal);
                for (int i = 0; i < count; i++)
                {
                    var key = Convert.ToString(ReadValue(), CultureInfo.InvariantCulture) ?? "";
                    dict[key] = ReadValue();
                }
                _p++; // }
                return dict;
            }
            case 'O':
            {
                // O:len:"Class":n:{...} → Eigenschaften als Dictionary
                _p += 2;
                int len = int.Parse(ReadUntil(':'), CultureInfo.InvariantCulture);
                _p += len + 3;
                int count = int.Parse(ReadUntil(':'), CultureInfo.InvariantCulture);
                _p++;
                var dict = new Dictionary<string, object?>(count, StringComparer.Ordinal);
                for (int i = 0; i < count; i++)
                {
                    var key = Convert.ToString(ReadValue(), CultureInfo.InvariantCulture) ?? "";
                    dict[key.TrimStart('\0', '*')] = ReadValue();
                }
                _p++;
                return dict;
            }
            default:
                throw new FormatException($"Unbekannter PHP-Serialisierungstyp '{type}' an Position {_p}.");
        }
    }

    private string ReadUntil(char end)
    {
        int start = _p;
        while (_b[_p] != (byte)end) _p++;
        var s = Encoding.ASCII.GetString(_b, start, _p - start);
        _p++;
        return s;
    }

    /// <summary>Gegenrichtung für einfache Strukturen (z. B. pmtext.touserarray).</summary>
    public static string Serialize(object? value)
    {
        var sb = new StringBuilder();
        Write(sb, value);
        return sb.ToString();

        static void Write(StringBuilder sb, object? v)
        {
            switch (v)
            {
                case null: sb.Append("N;"); break;
                case bool b: sb.Append("b:").Append(b ? 1 : 0).Append(';'); break;
                case int or long or uint: sb.Append("i:").Append(Convert.ToString(v, CultureInfo.InvariantCulture)).Append(';'); break;
                case string s:
                    sb.Append("s:").Append(Encoding.UTF8.GetByteCount(s)).Append(":\"").Append(s).Append("\";"); break;
                case System.Collections.IDictionary d:
                    sb.Append("a:").Append(d.Count).Append(":{");
                    foreach (System.Collections.DictionaryEntry e in d)
                    {
                        if (e.Key is int or long) Write(sb, Convert.ToInt64(e.Key)); else Write(sb, e.Key?.ToString());
                        Write(sb, e.Value);
                    }
                    sb.Append('}'); break;
                default: Write(sb, v.ToString()); break;
            }
        }
    }
}
