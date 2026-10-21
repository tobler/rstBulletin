using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using VbForum.Core.Models;
using VbForum.Core.Text;
using VbForum.Data;

namespace VbForum.Web.Infrastructure;

public static class Endpoints
{
    public static async Task<ForumUser> ForumUserAsync(this HttpContext http)
    {
        var id = http.User.FindFirstValue(ForumClaims.UserId);
        if (!long.TryParse(id, out var uid)) return ForumUser.Guest;
        var row = await http.RequestServices.GetRequiredService<UserRepository>().GetAsync(uid);
        return row == null ? ForumUser.Guest : ForumUser.From(row, await http.RequestServices.GetRequiredService<BoardCache>().GetAsync());
    }

    public static string ClientIp(this HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "";

    private static IResult Back(HttpContext http, string fallback)
    {
        var r = http.Request.Form.TryGetValue("returnUrl", out var v) ? v.ToString() : null;
        return Results.LocalRedirect(!string.IsNullOrEmpty(r) && r.StartsWith('/') && !r.StartsWith("//") ? r : fallback);
    }

    public static void MapForumEndpoints(this WebApplication app)
    {
        // ---------- Dateien ----------

        app.MapGet("/attachment/{id:long}/{kind?}", async (long id, string? kind, HttpContext http, AttachmentRepository att,
            PostRepository posts, ThreadRepository threads, BoardCache cache) =>
        {
            var a = await att.GetAsync(id);
            if (a == null) return Results.NotFound();
            var p = await posts.GetAsync(a.ContentId);
            var t = p == null ? null : await threads.GetAsync(p.ThreadId);
            var snap = await cache.GetAsync();
            var f = t == null ? null : snap.Forum(t.ForumId);
            var u = await http.ForumUserAsync();
            var perms = new Permissions(snap);
            if (p == null || t == null || f == null || !perms.CanReadThread(u, f, t) || !perms.CanGetAttachment(u, f) ||
                (p.Visible != 1 && !perms.IsModerator(u, f) && p.UserId != u.UserId))
                return Results.StatusCode(u.IsGuest ? 401 : 403);

            bool thumb = kind == "thumb" && a.HasThumbnail;
            var etag = new EntityTagHeaderValue($"\"a{id}{(thumb ? "t" : "")}-{a.Dateline}\"");
            if (http.Request.Headers.IfNoneMatch.ToString() == etag.Tag.ToString()) return Results.StatusCode(304);
            var data = await att.DataAsync(a.FileDataId, thumb);
            if (data == null) return Results.NotFound();
            http.Response.Headers.CacheControl = "private, max-age=604800";
            var ct = ImageService.ContentType(thumb ? "jpg" : a.Extension);
            if (thumb && ImageService.Inspect(data) is { } info) ct = ImageService.ContentType(info.Extension);
            // Nur Bilder inline anzeigen; alles andere als Download (verhindert HTML/SVG-Ausführung)
            return a.IsImage && !a.Extension.Equals("svg", StringComparison.OrdinalIgnoreCase)
                ? Results.File(data, ct, entityTag: etag)
                : Results.File(data, ct, a.DisplayName, entityTag: etag);
        });

        app.MapGet("/avatar/{userId:long}", async (long userId, string? size, HttpContext http, UserRepository users) =>
        {
            var av = await users.AvatarAsync(userId, size == "s");
            if (av == null) return Results.NotFound();
            var etag = new EntityTagHeaderValue($"\"av{userId}-{av.Value.Dateline}{size}\"");
            if (http.Request.Headers.IfNoneMatch.ToString() == etag.Tag.ToString()) return Results.StatusCode(304);
            http.Response.Headers.CacheControl = "public, max-age=86400";
            var info = ImageService.Inspect(av.Value.Data);
            return Results.File(av.Value.Data, ImageService.ContentType(info?.Extension ?? "gif"), entityTag: etag);
        });

        app.MapGet("/branding/logo", async (BoardCache cache, ForumPaths paths, HttpContext http) =>
        {
            var s = (await cache.GetAsync()).Settings;
            if (string.IsNullOrEmpty(s.LogoFile)) return Results.NotFound();
            var file = Path.Combine(paths.Branding, Path.GetFileName(s.LogoFile));
            if (!File.Exists(file)) return Results.NotFound();
            http.Response.Headers.CacheControl = "public, max-age=3600";
            http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'";
            return Results.File(file, ImageService.ContentType(Path.GetExtension(file).TrimStart('.')));
        });

        app.MapGet("/imgproxy", ImageProxyAsync);

        // ---------- Status für das PN-Popup (wird vom Browser regelmäßig abgefragt) ----------
        app.MapGet("/api/pm/status", async (HttpContext http, PmRepository pms, UserRepository users) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!long.TryParse(http.User.FindFirstValue(ForumClaims.UserId), out var uid)) return Results.Unauthorized();
            var u = await users.GetAsync(uid);
            if (u == null) return Results.Unauthorized();
            var s = await pms.StatusAsync(uid);
            return Results.Json(new
            {
                unread = s.Unread,
                popup = u.PmPopup != 0,
                latest = s.LatestId == 0 ? null : new { id = s.LatestId, from = s.From, title = s.Title, time = ForumTime.Relative(s.Dateline) }
            });
        });

        // ---------- Alte vBulletin-URLs ----------

        app.MapGet("/showthread.php", (HttpContext h) =>
        {
            var q = h.Request.Query;
            if (long.TryParse(q["p"], out var pid)) return Results.Redirect($"/post/{pid}", permanent: true);
            if (long.TryParse(q["t"], out var tid))
            {
                var page = int.TryParse(q["page"], out var pg) && pg > 1 ? $"?page={pg}" : "";
                return Results.Redirect($"/thread/{tid}{page}", permanent: true);
            }
            // vB-SEO-Format: showthread.php?12345-Titel
            var raw = h.Request.QueryString.Value?.TrimStart('?') ?? "";
            var lead = new string(raw.TakeWhile(char.IsDigit).ToArray());
            return long.TryParse(lead, out var t2) ? Results.Redirect($"/thread/{t2}", permanent: true) : Results.Redirect("/");
        });
        app.MapGet("/forumdisplay.php", (HttpContext h) =>
        {
            var q = h.Request.Query;
            if (int.TryParse(q["f"], out var fid)) return Results.Redirect($"/forum/{fid}", permanent: true);
            var raw = h.Request.QueryString.Value?.TrimStart('?') ?? "";
            var lead = new string(raw.TakeWhile(char.IsDigit).ToArray());
            return int.TryParse(lead, out var f2) ? Results.Redirect($"/forum/{f2}", permanent: true) : Results.Redirect("/");
        });
        app.MapGet("/member.php", (HttpContext h) =>
        {
            var q = h.Request.Query;
            if (long.TryParse(q["u"], out var uid)) return Results.Redirect($"/user/{uid}", permanent: true);
            var raw = h.Request.QueryString.Value?.TrimStart('?') ?? "";
            var lead = new string(raw.TakeWhile(char.IsDigit).ToArray());
            return long.TryParse(lead, out var u2) ? Results.Redirect($"/user/{u2}", permanent: true) : Results.Redirect("/members");
        });
        app.MapGet("/attachment.php", (long? attachmentid) =>
            attachmentid is long a ? Results.Redirect($"/attachment/{a}", permanent: true) : Results.NotFound());
        app.MapGet("/index.php", () => Results.Redirect("/", permanent: true));
        app.MapGet("/forum.php", () => Results.Redirect("/", permanent: true));
        app.MapGet("/search.php", () => Results.Redirect("/search", permanent: true));
        app.MapGet("/private.php", () => Results.Redirect("/account/pm", permanent: true));

        // Kurzlinks auf Beiträge: /post/123 → richtige Seite des Themas
        app.MapGet("/post/{id:long}", async (long id, HttpContext http, PostRepository posts, ThreadRepository threads, BoardCache cache) =>
        {
            var p = await posts.GetAsync(id);
            if (p == null) return Results.NotFound();
            var t = await threads.GetAsync(p.ThreadId);
            if (t == null) return Results.NotFound();
            var snap = await cache.GetAsync();
            var perms = new Permissions(snap);
            var u = await http.ForumUserAsync();
            var f = snap.Forum(t.ForumId);
            var mod = f != null && perms.IsModerator(u, f);
            var pos = await posts.PositionAsync(p, mod);
            var size = snap.Settings.PostsPerPage;
            var page = (int)Math.Max(1, (pos + size - 1) / size);
            return Results.Redirect($"/thread/{t.ThreadId}/{t.Slug}{(page > 1 ? $"?page={page}" : "")}#post{id}");
        });

        app.MapGet("/thread/{id:long}/lastpost", async (long id, ThreadRepository threads) =>
        {
            var t = await threads.GetAsync(id);
            return t == null ? Results.NotFound() : Results.Redirect($"/post/{t.LastPostId}");
        });

        /// Erster ungelesener Beitrag
        app.MapGet("/thread/{id:long}/unread", async (long id, HttpContext http, ThreadRepository threads, BoardCache cache) =>
        {
            var t = await threads.GetAsync(id);
            if (t == null) return Results.NotFound();
            var u = await http.ForumUserAsync();
            if (u.IsGuest) return Results.Redirect($"/thread/{id}/{t.Slug}");
            var read = await threads.ThreadReadTimeAsync(u.UserId, id);
            var fr = (await threads.ForumReadTimesAsync(u.UserId)).GetValueOrDefault(t.ForumId);
            var since = Math.Max(read, fr);
            var db = http.RequestServices.GetRequiredService<Db>();
            await using var c = await db.OpenAsync();
            var pid = await Dapper.SqlMapper.ExecuteScalarAsync<long?>(c,
                $"SELECT postid FROM {db.T.Post} WHERE threadid = @id AND visible = 1 AND dateline > @since ORDER BY dateline, postid LIMIT 1", new { id, since });
            return Results.Redirect($"/post/{pid ?? t.LastPostId}");
        });

        // ---------- Aktionen (Formulare, mit Antiforgery) ----------

        app.MapPost("/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.LocalRedirect("/");
        });

        app.MapPost("/markread", async (HttpContext http, [FromForm] int? forumId, ThreadRepository threads, BoardCache cache) =>
        {
            var u = await http.ForumUserAsync();
            if (u.IsGuest) return Results.LocalRedirect("/login");
            var snap = await cache.GetAsync();
            var ids = forumId is int f && snap.Forum(f) is { } root
                ? new[] { root }.Concat(snap.Descendants(root)).Select(x => x.ForumId)
                : snap.Forums.Keys;
            await threads.MarkForumsReadAsync(u.UserId, ids, ForumTime.Now);
            return Back(http, forumId is int ff ? $"/forum/{ff}" : "/");
        });

        app.MapPost("/thread/{id:long}/subscribe", async (long id, HttpContext http, [FromForm] string? mode, SubscriptionRepository subs) =>
        {
            var u = await http.ForumUserAsync();
            if (u.IsGuest) return Results.LocalRedirect("/login");
            if (mode == "off") await subs.UnsubscribeAsync(u.UserId, id);
            else await subs.SubscribeAsync(u.UserId, id, mode != "silent");
            return Back(http, $"/thread/{id}");
        });

        app.MapPost("/mod/thread/{id:long}", async (long id, HttpContext http, [FromForm] string action, [FromForm] int? target,
            [FromForm] string? title, [FromForm] string? reason, ThreadRepository threads, ModerationService mod, BoardCache cache, TokenRepository audit) =>
        {
            var u = await http.ForumUserAsync();
            var t = await threads.GetAsync(id);
            if (t == null) return Results.NotFound();
            var snap = await cache.GetAsync();
            var f = snap.Forum(t.ForumId);
            if (f == null || !new Permissions(snap).IsModerator(u, f)) return Results.StatusCode(403);
            switch (action)
            {
                case "open": await mod.SetOpenAsync(id, true); break;
                case "close": await mod.SetOpenAsync(id, false); break;
                case "stick": await mod.SetStickyAsync(id, true); break;
                case "unstick": await mod.SetStickyAsync(id, false); break;
                case "approve": await mod.ApproveThreadAsync(id); break;
                case "delete": await mod.SoftDeleteThreadAsync(u, t, reason ?? ""); break;
                case "restore": await mod.RestoreThreadAsync(id); break;
                case "move" when target is int tf: await mod.MoveAsync(id, tf); break;
                case "rename" when !string.IsNullOrWhiteSpace(title): await mod.RenameAsync(id, title); break;
                case "harddelete" when u.IsAdmin || u.IsSuperMod:
                    await mod.HardDeleteThreadAsync(id);
                    await audit.LogAsync(u, "thread.harddelete", $"{id} {t.DisplayTitle}", http.ClientIp());
                    return Results.LocalRedirect($"/forum/{t.ForumId}");
                default: return Results.BadRequest();
            }
            await audit.LogAsync(u, "thread." + action, $"{id} {t.DisplayTitle}", http.ClientIp());
            return Back(http, $"/thread/{id}");
        });

        app.MapPost("/mod/post/{id:long}", async (long id, HttpContext http, [FromForm] string action, [FromForm] string? reason,
            PostRepository posts, ThreadRepository threads, PostingService posting, BoardCache cache, TokenRepository audit) =>
        {
            var u = await http.ForumUserAsync();
            var p = await posts.GetAsync(id);
            if (p == null) return Results.NotFound();
            var t = await threads.GetAsync(p.ThreadId);
            if (t == null) return Results.NotFound();
            var snap = await cache.GetAsync();
            var perms = new Permissions(snap);
            var f = snap.Forum(t.ForumId);
            if (f == null) return Results.NotFound();
            bool isMod = perms.IsModerator(u, f);
            switch (action)
            {
                case "delete":
                    if (!await posting.SoftDeletePostAsync(u, p, t, reason ?? "")) return Results.StatusCode(403);
                    break;
                case "restore" when isMod: await posting.RestorePostAsync(p); break;
                case "approve" when isMod: await posting.ApprovePostAsync(p); break;
                case "harddelete" when u.IsAdmin || u.IsSuperMod:
                    await posting.HardDeletePostAsync(p);
                    await audit.LogAsync(u, "post.harddelete", $"{id} in {t.ThreadId}", http.ClientIp());
                    return Back(http, $"/thread/{t.ThreadId}");
                default: return Results.StatusCode(403);
            }
            if (isMod) await audit.LogAsync(u, "post." + action, $"{id} in {t.ThreadId}", http.ClientIp());
            return Back(http, p.PostId == t.FirstPostId && action == "delete" ? $"/forum/{t.ForumId}" : $"/thread/{t.ThreadId}");
        });

        app.MapPost("/account/avatar", async (HttpContext http, IFormFile? file, [FromForm] string? remove, UserRepository users, BoardCache cache) =>
        {
            var u = await http.ForumUserAsync();
            if (u.IsGuest) return Results.LocalRedirect("/login");
            if (remove == "1") { await users.RemoveAvatarAsync(u.UserId); return Results.LocalRedirect("/account?msg=avatar-removed"); }
            var s = (await cache.GetAsync()).Settings;
            if (file == null || file.Length == 0) return Results.LocalRedirect("/account?err=avatar-empty");
            if (file.Length > Math.Max(s.AvatarMaxKb, 200) * 1024L * 10) return Results.LocalRedirect("/account?err=avatar-size");
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            var data = ms.ToArray();
            if (ImageService.Inspect(data) == null) return Results.LocalRedirect("/account?err=avatar-type");
            var full = ImageService.Resize(data, 200, square: true, quality: 88);
            var thumb = ImageService.Resize(data, 64, square: true, quality: 85);
            if (full == null || thumb == null) return Results.LocalRedirect("/account?err=avatar-type");
            await users.SetAvatarAsync(u.UserId, full.Value.Data, full.Value.Width, full.Value.Height, thumb.Value.Data, thumb.Value.Width, thumb.Value.Height, full.Value.Extension);
            return Results.LocalRedirect("/account?msg=avatar-saved");
        }).DisableAntiforgery().AddEndpointFilter(async (ctx, next) =>
        {
            // Formular-Upload: Antiforgery manuell prüfen (DisableAntiforgery nur wegen IFormFile-Bindung)
            var af = ctx.HttpContext.RequestServices.GetRequiredService<Microsoft.AspNetCore.Antiforgery.IAntiforgery>();
            if (!await af.IsRequestValidAsync(ctx.HttpContext)) return Results.BadRequest("Ungültiges Formular-Token.");
            return await next(ctx);
        });
    }

    // ---------- Bild-Proxy für http-Bilder in alten Beiträgen ----------

    private static readonly HttpClient ProxyClient = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(5),
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 3,
        ConnectCallback = async (ctx, ct) =>
        {
            // Nur öffentliche Adressen (kein Zugriff aufs interne Netz über den Proxy)
            var addrs = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct);
            var ip = addrs.FirstOrDefault(a => !IsPrivate(a)) ?? throw new HttpRequestException("Ziel nicht erlaubt");
            var s = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try { await s.ConnectAsync(new IPEndPoint(ip, ctx.DnsEndPoint.Port), ct); return new NetworkStream(s, true); }
            catch { s.Dispose(); throw; }
        }
    }) { Timeout = TimeSpan.FromSeconds(10) };

    private static bool IsPrivate(IPAddress a)
    {
        if (IPAddress.IsLoopback(a)) return true;
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (a.AddressFamily == AddressFamily.InterNetworkV6)
            return a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || a.IsIPv6UniqueLocal || a.Equals(IPAddress.IPv6Any);
        var b = a.GetAddressBytes();
        return b[0] == 10 || b[0] == 127 || b[0] == 0 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
               (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127) || b[0] >= 224;
    }

    private static async Task<IResult> ImageProxyAsync(string? u, string? s, UrlSigner signer, ForumPaths paths, HttpContext http)
    {
        if (string.IsNullOrEmpty(u) || !signer.Verify(u, s) || !Uri.TryCreate(u, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return Results.BadRequest();
        Directory.CreateDirectory(paths.ImageCache);
        var key = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(u)));
        var file = Path.Combine(paths.ImageCache, key);
        http.Response.Headers.CacheControl = "public, max-age=604800";
        if (File.Exists(file + ".type"))
        {
            var t = await File.ReadAllTextAsync(file + ".type");
            return t == "missing" ? Results.NotFound() : Results.File(file, t);
        }
        try
        {
            using var resp = await ProxyClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            var type = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (!resp.IsSuccessStatusCode || !type.StartsWith("image/") || type.Contains("svg") || resp.Content.Headers.ContentLength > 8_000_000)
            {
                await File.WriteAllTextAsync(file + ".type", "missing");
                return Results.NotFound();
            }
            await using var src = await resp.Content.ReadAsStreamAsync();
            using var ms = new MemoryStream();
            var buf = new byte[81920]; int n; long total = 0;
            while ((n = await src.ReadAsync(buf)) > 0)
            {
                total += n;
                if (total > 8_000_000) return Results.NotFound();
                ms.Write(buf, 0, n);
            }
            await File.WriteAllBytesAsync(file, ms.ToArray());
            await File.WriteAllTextAsync(file + ".type", type);
            return Results.File(ms.ToArray(), type);
        }
        catch
        {
            await File.WriteAllTextAsync(file + ".type", "missing");
            return Results.NotFound();
        }
    }
}
