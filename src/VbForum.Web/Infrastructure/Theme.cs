using VbForum.Data;

namespace VbForum.Web.Infrastructure;

/// <summary>Hell/Dunkel: Cookie „vbf-theme“ (auch für Gäste) + beim Benutzer gespeichert (vbf_userpref).</summary>
public static class Theme
{
    public const string Cookie = "vbf-theme";
    public const string Pref = "theme";

    /// <summary>„light“, „dark“ oder null (= automatisch nach Systemeinstellung).</summary>
    public static string? Normalize(string? v) => v is "light" or "dark" ? v : null;

    public static string? FromRequest(HttpContext? http) => Normalize(http?.Request.Cookies[Cookie]);

    public static void SetCookie(HttpContext http, string? theme)
    {
        if (Normalize(theme) is { } t)
            http.Response.Cookies.Append(Cookie, t, new CookieOptions
            {
                MaxAge = TimeSpan.FromDays(365), HttpOnly = false, SameSite = SameSiteMode.Lax, IsEssential = true, Secure = http.Request.IsHttps, Path = "/",
            });
        else http.Response.Cookies.Delete(Cookie, new CookieOptions { Path = "/" });
    }

    /// <summary>Beim Anmelden die gespeicherte Einstellung des Benutzers übernehmen.</summary>
    public static async Task ApplyUserPrefAsync(HttpContext http, UserRepository users, long userId)
    {
        try
        {
            var pref = Normalize(await users.GetPrefAsync(userId, Pref));
            if (pref != null) SetCookie(http, pref);
            // Hat der Benutzer als Gast schon gewählt, aber noch nichts gespeichert → übernehmen
            else if (FromRequest(http) is { } guestChoice) await users.SetPrefAsync(userId, Pref, guestChoice);
        }
        catch { /* nicht kritisch */ }
    }
}
