using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;

namespace VbForum.Core.Security;

/// <summary>
/// Passwort-Logik kompatibel zu vBulletin 3.x/4.x: password = md5(md5(klartext) . salt).
/// Moderne Hashes (PBKDF2 via ASP.NET Identity) liegen in der eigenen Tabelle vbf_userauth.
/// </summary>
public static class VbPassword
{
    private static readonly PasswordHasher<object> Modern = new();
    private static readonly object Dummy = new();

    public static string Md5Hex(byte[] bytes) => Convert.ToHexStringLower(MD5.HashData(bytes));
    public static string Md5Hex(string s) => Md5Hex(Encoding.UTF8.GetBytes(s));

    /// <summary>
    /// vB 4 md5t das Passwort im Browser in zwei Varianten: UTF-8 und "latin1 + Zeichen &gt; 255 als &amp;#NNN;".
    /// Beim Prüfen probieren wir beide, damit auch alte Passwörter mit Umlauten/Sonderzeichen funktionieren.
    /// </summary>
    public static IEnumerable<string> LegacyInnerHashes(string password)
    {
        yield return Md5Hex(Encoding.UTF8.GetBytes(password));

        var sb = new StringBuilder();
        foreach (var ch in password)
            sb.Append(ch > 255 ? $"&#{(int)ch};" : ch.ToString());
        var latin1 = Encoding.Latin1.GetBytes(sb.ToString());
        var alt = Md5Hex(latin1);
        if (alt != Md5Hex(Encoding.UTF8.GetBytes(password))) yield return alt;
    }

    public static bool VerifyLegacy(string password, string? storedHash, string? salt)
    {
        if (string.IsNullOrEmpty(storedHash)) return false;
        salt ??= "";
        var stored = Encoding.ASCII.GetBytes(storedHash.Trim().ToLowerInvariant());
        foreach (var inner in LegacyInnerHashes(password))
        {
            var candidate = Encoding.ASCII.GetBytes(Md5Hex(Encoding.Latin1.GetBytes(inner + salt)));
            if (CryptographicOperations.FixedTimeEquals(candidate, stored)) return true;
        }
        return false;
    }

    public static (string Hash, string Salt) CreateLegacy(string password)
    {
        var salt = CreateSalt();
        var inner = Md5Hex(Encoding.UTF8.GetBytes(password));
        return (Md5Hex(Encoding.Latin1.GetBytes(inner + salt)), salt);
    }

    /// <summary>vB-Salt: 30 druckbare ASCII-Zeichen (33–126).</summary>
    public static string CreateSalt(int length = 30)
    {
        var chars = new char[length];
        for (int i = 0; i < length; i++)
        {
            char c;
            do { c = (char)RandomNumberGenerator.GetInt32(33, 127); } while (c is '\\' or '\'' or '"');
            chars[i] = c;
        }
        return new string(chars);
    }

    public static string HashModern(string password) => Modern.HashPassword(Dummy, password);

    public static bool VerifyModern(string password, string hash, out bool needsRehash)
    {
        var r = Modern.VerifyHashedPassword(Dummy, hash, password);
        needsRehash = r == PasswordVerificationResult.SuccessRehashNeeded;
        return r != PasswordVerificationResult.Failed;
    }

    /// <summary>Stempel für Cookies: ändert sich, sobald sich der Passwort-Hash ändert → alte Sitzungen ungültig.</summary>
    public static string SecurityStamp(string? hash) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("vbf-stamp:" + (hash ?? ""))))[..16];

    public static string RandomToken(int bytes = 32) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
