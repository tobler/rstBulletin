using VbForum.Data;

namespace VbForum.Web.Infrastructure;

/// <summary>
/// Leitet Aufrufe, die nicht über die Basis-URL kommen (anderer Host wie www./ohne www. oder http statt https),
/// per 301 dorthin um. Nur wenn in den Einstellungen aktiviert; Schnittstellen, Health-Check und interne Aufrufe
/// (localhost/IP-Adressen, z. B. vom Reverse-Proxy oder Docker) bleiben unberührt.
/// </summary>
public static class CanonicalHost
{
    public static async Task<bool> RedirectAsync(HttpContext ctx)
    {
        var req = ctx.Request;
        if (!HttpMethods.IsGet(req.Method) && !HttpMethods.IsHead(req.Method)) return false;
        var path = req.Path.Value ?? "/";
        if (path.StartsWith("/healthz") || path.StartsWith("/api/") || path.StartsWith("/rstpay/") || path.StartsWith("/_blazor") || path.StartsWith("/setup"))
            return false;
        var db = ctx.RequestServices.GetRequiredService<Db>();
        if (!db.IsConfigured) return false;
        BoardSnapshot snap;
        try { snap = await ctx.RequestServices.GetRequiredService<BoardCache>().GetAsync(); }
        catch { return false; }
        var s = snap.Settings;
        if (!s.RedirectToBaseUrl || !Uri.TryCreate(s.BaseUrl, UriKind.Absolute, out var target)) return false;
        if (target.Scheme is not ("http" or "https")) return false;

        var host = req.Host.Host;
        if (IsInternal(host) || IsInternal(target.Host)) return false;
        bool sameHost = string.Equals(host, target.Host, StringComparison.OrdinalIgnoreCase);
        bool sameScheme = string.Equals(req.Scheme, target.Scheme, StringComparison.OrdinalIgnoreCase);
        if (sameHost && sameScheme) return false;
        // Hinter einem Proxy ohne X-Forwarded-Proto sieht die App immer „http“ – dann nur den Host angleichen
        if (sameHost && !sameScheme && target.Scheme == "https" && !req.Headers.ContainsKey("X-Forwarded-Proto") && !req.Headers.ContainsKey("X-Original-Proto")) return false;

        var url = $"{target.Scheme}://{target.Authority}{req.PathBase}{req.Path}{req.QueryString}";
        ctx.Response.Redirect(url, permanent: true);
        return true;
    }

    public static bool IsInternal(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || System.Net.IPAddress.TryParse(host, out _) || !host.Contains('.');
}
