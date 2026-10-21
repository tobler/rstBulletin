using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using VbForum.Data;

namespace VbForum.Web.Infrastructure;

public static class ForumClaims
{
    public const string UserId = "vbf:uid";
    public const string Stamp = "vbf:stamp";
    public const string Checked = "vbf:chk";
}

/// <summary>Liefert den angemeldeten Benutzer – im statischen Rendering wie in interaktiven Komponenten.
/// Gruppen/Rechte werden je Anfrage bzw. Circuit frisch aus der DB geladen (Änderungen im Admin wirken sofort).</summary>
public sealed class CurrentUser(AuthenticationStateProvider auth, UserRepository users, BoardCache cache)
{
    private ForumUser? _user;
    private Task<ForumUser>? _loading;

    public Task<ForumUser> GetAsync() => _user != null ? Task.FromResult(_user) : (_loading ??= LoadAsync());

    public void Reset() { _user = null; _loading = null; }

    private async Task<ForumUser> LoadAsync()
    {
        var state = await auth.GetAuthenticationStateAsync();
        var id = state.User.FindFirstValue(ForumClaims.UserId);
        if (!long.TryParse(id, out var uid)) return _user = ForumUser.Guest;
        var row = await users.GetAsync(uid);
        if (row == null) return _user = ForumUser.Guest;
        return _user = ForumUser.From(row, await cache.GetAsync());
    }
}

public static class AuthSetup
{
    public static ClaimsPrincipal CreatePrincipal(long userId, string username, string stamp)
    {
        var id = new ClaimsIdentity(new[]
        {
            new Claim(ForumClaims.UserId, userId.ToString()),
            new Claim(ClaimTypes.Name, username),
            new Claim(ForumClaims.Stamp, stamp),
            new Claim(ForumClaims.Checked, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()),
        }, CookieAuthenticationDefaults.AuthenticationScheme);
        return new ClaimsPrincipal(id);
    }

    /// <summary>Prüft alle 5 Minuten, ob Konto noch existiert, nicht gesperrt ist und das Passwort unverändert ist.</summary>
    public static async Task ValidatePrincipal(CookieValidatePrincipalContext ctx)
    {
        var p = ctx.Principal;
        if (p == null) return;
        long.TryParse(p.FindFirstValue(ForumClaims.Checked), out var chk);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (now - chk < 300) return;

        var sp = ctx.HttpContext.RequestServices;
        var db = sp.GetRequiredService<Db>();
        if (!db.IsConfigured) return;
        long.TryParse(p.FindFirstValue(ForumClaims.UserId), out var uid);
        var row = await sp.GetRequiredService<UserRepository>().GetAsync(uid);
        var snap = await sp.GetRequiredService<BoardCache>().GetAsync();
        if (row == null || AuthService.Stamp(row) != p.FindFirstValue(ForumClaims.Stamp) || snap.Settings.BannedGroups.Contains(row.UserGroupId))
        {
            ctx.RejectPrincipal();
            await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }
        ctx.ReplacePrincipal(CreatePrincipal(row.UserId, row.DisplayName, AuthService.Stamp(row)));
        ctx.ShouldRenew = true;
    }
}
