using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authorization;
using VbForum.Core.Text;
using VbForum.Core.Vb;
using VbForum.Data;

namespace VbForum.Web.Infrastructure;

public sealed class SetupToken { public string? Value { get; set; } }

public enum ForumRole { Moderator, Admin }

public sealed record ForumRoleRequirement(ForumRole Role) : IAuthorizationRequirement;

/// <summary>Admin-/Moderatorrechte werden live aus der DB ermittelt (Gruppen können sich ändern).</summary>
public sealed class ForumRoleHandler(UserRepository users, BoardCache cache) : AuthorizationHandler<ForumRoleRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, ForumRoleRequirement req)
    {
        // Läuft auch in der Routing-Middleware (außerhalb von Komponenten) → direkt über die Claims laden
        if (context.User.Identity?.IsAuthenticated != true) return;
        if (!long.TryParse(context.User.FindFirst(ForumClaims.UserId)?.Value, out var uid)) return;
        var row = await users.GetAsync(uid);
        if (row == null) return;
        var u = ForumUser.From(row, await cache.GetAsync());
        if (req.Role == ForumRole.Admin ? u.IsAdmin : new Permissions(await cache.GetAsync()).IsAnyModerator(u))
            context.Succeed(req);
    }
}

/// <summary>Einrichtungsmodus, Wartungsmodus und Aktivitätszeitstempel.</summary>
public sealed class ForumGateMiddleware(RequestDelegate next)
{
    private static readonly ConcurrentDictionary<long, DateTime> LastTouch = new();

    private static bool IsInfrastructure(PathString p) =>
        p.StartsWithSegments("/_framework") || p.StartsWithSegments("/_blazor") || p.StartsWithSegments("/_content") ||
        p.StartsWithSegments("/css") || p.StartsWithSegments("/js") || p.StartsWithSegments("/images") ||
        Path.HasExtension(p.Value ?? "") && !p.Value!.EndsWith(".php", StringComparison.OrdinalIgnoreCase);

    public async Task InvokeAsync(HttpContext ctx, Db db, BoardCache cache, UserRepository users)
    {
        var path = ctx.Request.Path;
        if (!db.IsConfigured)
        {
            if (path.StartsWithSegments("/setup") || IsInfrastructure(path) || path.StartsWithSegments("/Error")) { await next(ctx); return; }
            ctx.Response.Redirect("/setup");
            return;
        }
        if (IsInfrastructure(path) || path.StartsWithSegments("/attachment") || path.StartsWithSegments("/avatar") || path.StartsWithSegments("/imgproxy"))
        {
            await next(ctx);
            return;
        }

        BoardSnapshot snap;
        try { snap = await cache.GetAsync(); }
        catch (Exception)
        {
            // DB nicht erreichbar → Admin kann unter /setup die Verbindung korrigieren
            if (path.StartsWithSegments("/setup") || path.StartsWithSegments("/Error")) { await next(ctx); return; }
            ctx.Response.StatusCode = 503;
            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.WriteAsync("<!doctype html><meta charset=utf-8><title>Wartung</title><body style=\"font-family:system-ui;padding:40px\"><h1>Forum vorübergehend nicht erreichbar</h1><p>Die Datenbank antwortet nicht. Bitte versuche es in ein paar Minuten erneut.</p><p><a href=\"/setup\">Verbindung prüfen (Administratoren)</a></p>");
            return;
        }

        var u = ctx.User.Identity?.IsAuthenticated == true ? await ctx.ForumUserAsync() : ForumUser.Guest;
        if (snap.Settings.BoardOffline && !u.IsAdmin &&
            !(path.StartsWithSegments("/login") || path.StartsWithSegments("/logout") || path.StartsWithSegments("/offline")))
        {
            ctx.Response.Redirect("/offline");
            return;
        }

        if (!u.IsGuest)
        {
            var now = DateTime.UtcNow;
            if (!LastTouch.TryGetValue(u.UserId, out var last) || now - last > TimeSpan.FromMinutes(2))
            {
                LastTouch[u.UserId] = now;
                // Neuer Besuch, wenn länger als 30 Minuten inaktiv → lastvisit fortschreiben (Basis für "neue Beiträge")
                var newVisit = ForumTime.Now - u.LastActivity > 1800;
                try { await users.TouchAsync(u.UserId, ctx.ClientIp(), newVisit); } catch { /* nicht kritisch */ }
            }
        }
        await next(ctx);
    }
}

/// <summary>Lange Wartungsaufgaben (Zähler, Volltextindex) laufen im Hintergrund; das Admin-UI zeigt den Fortschritt.</summary>
public sealed class MaintenanceJobs(IServiceProvider sp, ILogger<MaintenanceJobs> log)
{
    public string? Running { get; private set; }
    public string Status { get; private set; } = "";
    public DateTime? Finished { get; private set; }
    public event Action? Changed;
    private CancellationTokenSource? _cts;

    public bool Start(string name, Func<IServiceProvider, IProgress<string>, CancellationToken, Task> job)
    {
        lock (this)
        {
            if (Running != null) return false;
            Running = name; Status = "Gestartet …"; Finished = null;
            _cts = new CancellationTokenSource();
        }
        Changed?.Invoke();
        var progress = new Progress<string>(s => { Status = s; Changed?.Invoke(); });
        _ = Task.Run(async () =>
        {
            try { await job(sp, progress, _cts.Token); Status = "Fertig."; }
            catch (OperationCanceledException) { Status = "Abgebrochen."; }
            catch (Exception ex) { log.LogError(ex, "Wartung {Name} fehlgeschlagen", name); Status = "Fehler: " + ex.Message; }
            finally { Running = null; Finished = DateTime.Now; Changed?.Invoke(); }
        });
        return true;
    }

    public void Cancel() => _cts?.Cancel();

    public static IReadOnlyCollection<int> CountingForums(BoardSnapshot s) =>
        s.Forums.Values.Where(f => (f.Options & s.Bits.Opt(ForumOption.CountPosts)) != 0).Select(f => f.ForumId).ToList();
}
