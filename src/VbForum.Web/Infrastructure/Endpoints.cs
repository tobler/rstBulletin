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

        // Für Docker/Load-Balancer: 200 = läuft und DB erreichbar (oder noch nicht eingerichtet), 503 = DB weg
        app.MapGet("/healthz", async (SetupState setup) =>
            await setup.GetAsync() == SetupStatus.DbUnavailable ? Results.StatusCode(503) : Results.Text("ok"));

        app.MapGet("/branding/logo", async (BoardCache cache, AssetStore assets, ForumPaths paths, HttpContext http) =>
        {
            var s = (await cache.GetAsync()).Settings;
            if (string.IsNullOrEmpty(s.LogoFile)) return Results.NotFound();
            var a = await assets.GetAsync("logo");
            if (a == null)
            {
                // Altbestand: Logo lag früher als Datei im Datenverzeichnis → einmalig in die DB übernehmen
                var file = Path.Combine(paths.Branding, Path.GetFileName(s.LogoFile));
                if (!File.Exists(file)) return Results.NotFound();
                await assets.SetAsync("logo", ImageService.ContentType(Path.GetExtension(file).TrimStart('.')), await File.ReadAllBytesAsync(file));
                a = await assets.GetAsync("logo");
                if (a == null) return Results.NotFound();
            }
            return AssetResult(http, a);
        });

        // Favicon aus den Einstellungen, sonst das mitgelieferte
        async Task<IResult> Favicon(BoardCache cache, AssetStore assets, HttpContext http)
        {
            try
            {
                var s = (await cache.GetAsync()).Settings;
                if (!string.IsNullOrEmpty(s.FaviconFile) && await assets.GetAsync("favicon") is { } a) return AssetResult(http, a);
            }
            catch { /* DB noch nicht eingerichtet */ }
            http.Response.Headers.CacheControl = "public, max-age=3600";
            return Results.File(Path.Combine(app.Environment.WebRootPath, "favicon.svg"), "image/svg+xml");
        }
        app.MapGet("/branding/favicon", Favicon);
        app.MapGet("/favicon.ico", Favicon);

        app.MapGet("/imgproxy", ImageProxyAsync);

        // Vorschau der Mailvorlagen (nur Admins)
        app.MapGet("/admin/mail-preview", async (string? type, MailService mail, BoardCache cache) =>
        {
            var board = (await cache.GetAsync()).Settings.BoardName;
            var (subject, content) = type switch
            {
                "reply" => ("Neue Antwort: Wochenende", new MailContent("Hallo MaxM,", new[] { "Kathi_R hat im Thema „Wochenende“ geantwortet:" },
                    "Zum Beitrag", await mail.AbsoluteUrlAsync("/post/1"), Quote: "Klingt gut, ich bin dabei! Treffpunkt wie immer um 10 Uhr?",
                    Footnote: "Weitere Benachrichtigungen zu diesem Thema erhältst du erst, wenn du das Forum wieder besucht hast.")),
                "reset" => ("Passwort zurücksetzen", new MailContent("Hallo MaxM,", new[] { "für dein Konto wurde ein neues Passwort angefordert. Über den folgenden Knopf kannst du es festlegen." },
                    "Neues Passwort festlegen", await mail.AbsoluteUrlAsync("/account/reset?token=beispiel"),
                    Footnote: "Der Link ist 2 Stunden gültig. Wenn du das nicht warst, ignoriere diese Mail – dein Passwort bleibt unverändert.")),
                _ => ($"Bitte bestätige deine E-Mail-Adresse – {board}", new MailContent("Hallo MaxM,",
                    new[] { $"schön, dass du dich bei {board} registriert hast! Bitte bestätige noch deine E-Mail-Adresse, damit dein Konto freigeschaltet wird." },
                    "E-Mail-Adresse bestätigen", await mail.AbsoluteUrlAsync("/account/confirm?token=beispiel"),
                    Footnote: "Der Link ist 7 Tage gültig. Falls du dich nicht registriert hast, kannst du diese Mail einfach ignorieren.")),
            };
            var nav = "<div style=\"font:13px system-ui;text-align:center;padding:10px;background:#fff;border-bottom:1px solid #ddd\">Vorschau: " +
                      "<a href=\"?type=confirm\">Registrierung</a> · <a href=\"?type=reset\">Passwort</a> · <a href=\"?type=reply\">Abo-Benachrichtigung</a></div>";
            var html = await mail.PreviewAsync(subject, content);
            return Results.Content(html.Replace("<body style=\"margin:0;padding:0;background:#EEF1F6\">", "<body style=\"margin:0;padding:0;background:#EEF1F6\">" + nav), "text/html; charset=utf-8");
        }).RequireAuthorization("Admin");

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

        // showthread.php?t=123&page=2, ?p=456, ?123-Titel, /showthread.php/123-Titel/page2 und /showthread.php/Titel/page2
        app.MapGet("/showthread.php/{**rest}", (HttpContext h, LegacyLookup lookup, BoardCache cache) => LegacyThreadAsync(h, lookup, cache));
        app.MapGet("/showthread.php", (HttpContext h, LegacyLookup lookup, BoardCache cache) => LegacyThreadAsync(h, lookup, cache));
        app.MapGet("/forumdisplay.php/{**rest}", (HttpContext h, LegacyLookup lookup) => LegacyForumAsync(h, lookup));
        app.MapGet("/forumdisplay.php", (HttpContext h, LegacyLookup lookup) => LegacyForumAsync(h, lookup));
        app.MapGet("/member.php/{**rest}", (HttpContext h, LegacyLookup lookup) => LegacyMemberAsync(h, lookup));
        app.MapGet("/member.php", (HttpContext h, LegacyLookup lookup) => LegacyMemberAsync(h, lookup));
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

        // Als gelesen markieren: ein Forum/Bereich samt Unterforen, mehrere Foren (forumIds) oder ohne Angabe alle
        app.MapPost("/markread", async (HttpContext http, [FromForm] int? forumId, ThreadRepository threads, BoardCache cache) =>
        {
            var u = await http.ForumUserAsync();
            if (u.IsGuest) return Results.LocalRedirect("/login");
            var snap = await cache.GetAsync();
            var several = http.Request.Form["forumIds"].Select(x => int.TryParse(x, out var i) ? snap.Forum(i) : null).OfType<Forum>().ToList();
            var roots = forumId is int f && snap.Forum(f) is { } root ? new List<Forum> { root } : several;
            var ids = roots.Count > 0
                ? roots.SelectMany(r => new[] { r }.Concat(snap.Descendants(r))).Select(x => x.ForumId).Distinct()
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

        // Schnelllöschung aus dem Benutzermenü (nur Admins): ohne Rückfrage, Beiträge/Nachrichten bleiben als „Gelöschter User“
        app.MapPost("/admin/users/{id:long}/quickdelete", async (long id, HttpContext http, UserRepository users, UserDeletion deletion,
            BoardCache cache, TokenRepository audit) =>
        {
            var me = await http.ForumUserAsync();
            if (!me.IsAdmin) return Results.StatusCode(403);
            var row = await users.GetAsync(id);
            if (row == null) return Back(http, "/");
            // Eigenes Konto und andere Administratoren nur bewusst über die Benutzerverwaltung
            if (id == me.UserId || ForumUser.From(row, await cache.GetAsync()).IsAdmin)
                return Results.LocalRedirect($"/admin/users/{id}#delete");
            await deletion.DeleteAsync(id, deleteContent: false);
            await audit.LogAsync(me, "user.quickdelete", $"{id} {row.DisplayName}", http.ClientIp());
            cache.Invalidate();
            return Back(http, "/");
        }).RequireAuthorization("Admin");

        // Premium buchen: Bezahl-Link mit Benutzerdaten füllen und zu rstpay weiterleiten
        app.MapGet("/premium/buy/{planId:int}", async (int planId, string? returnUrl, HttpContext http, PremiumService premium, MailService mail, TokenRepository audit) =>
        {
            var u = await http.ForumUserAsync();
            if (u.IsGuest) return Results.LocalRedirect($"/login?returnUrl={Uri.EscapeDataString("/premium")}");
            var plan = await premium.PlanAsync(planId);
            if (plan == null || !plan.Active || string.IsNullOrWhiteSpace(plan.PayUrl)) return Results.LocalRedirect("/premium");
            var back = !string.IsNullOrEmpty(returnUrl) && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//") ? returnUrl : "/premium";
            back += (back.Contains('?') ? "&" : "?") + "done=1";
            var url = PremiumService.BuildPayUrl(plan, u, u.Email, await mail.AbsoluteUrlAsync(back));
            await audit.LogAsync(u, "premium.checkout", $"Plan {plan.PlanId} {plan.Title}", http.ClientIp());
            return Results.Redirect(url);
        });

        // Darstellung: hell / dunkel / automatisch – Cookie + beim Benutzer speichern
        app.MapPost("/theme", async (HttpContext http, [FromForm] string? theme, UserRepository users) =>
        {
            var t = Theme.Normalize(theme);
            Theme.SetCookie(http, t);
            var u = await http.ForumUserAsync();
            if (!u.IsGuest) await users.SetPrefAsync(u.UserId, Theme.Pref, t);
            return Back(http, "/");
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

    private static IResult AssetResult(HttpContext http, AssetStore.Asset a)
    {
        // URL enthält die Version (?v=…) → lange cachen
        http.Response.Headers.CacheControl = http.Request.Query.ContainsKey("v") ? "public, max-age=31536000, immutable" : "public, max-age=3600";
        http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'";
        return Results.File(a.Data, a.ContentType);
    }

    // ---------- Alte vB-URLs ----------

    /// <summary>
    /// Teile einer alten URL: Pfad nach „xyz.php/“ bzw. die „nackte“ Query (?123-Titel/page2), jeweils an „/“ getrennt.
    /// Prozent-Kodierung wird aus der Roh-URL gelesen: erst als UTF-8, sonst als ISO-8859-1 (so hat das alte Board kodiert).
    /// </summary>
    private static List<string> LegacySegments(HttpContext h, string script)
    {
        var raw = h.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestFeature>()?.RawTarget ?? (h.Request.Path + h.Request.QueryString);
        var q = raw.IndexOf('?');
        var path = q >= 0 ? raw[..q] : raw;
        var query = q >= 0 ? raw[(q + 1)..] : "";
        var i = path.IndexOf(script, StringComparison.OrdinalIgnoreCase);
        var rest = i >= 0 ? path[(i + script.Length)..].Trim('/') : "";
        if (rest.Length == 0)
        {
            // ?123-Titel/page2 (ohne key=value am Anfang)
            var first = query.Split('&')[0];
            if (first.Length > 0 && !first.Contains('=')) rest = first;
        }
        return rest.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(DecodeLegacy).Where(x => x.Length > 0).ToList();
    }

    private static string DecodeLegacy(string s)
    {
        var bytes = new List<byte>(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '%' && i + 2 < s.Length && Uri.IsHexDigit(s[i + 1]) && Uri.IsHexDigit(s[i + 2]))
            {
                bytes.Add(Convert.ToByte(s.Substring(i + 1, 2), 16)); i += 2;
            }
            else if (s[i] == '+') bytes.Add((byte)' ');
            else bytes.AddRange(System.Text.Encoding.UTF8.GetBytes(s[i].ToString()));
        }
        var arr = bytes.ToArray();
        try { return new System.Text.UTF8Encoding(false, true).GetString(arr); }
        catch (System.Text.DecoderFallbackException) { return System.Text.Encoding.Latin1.GetString(arr); }
    }

    /// <summary>„123-Titel“ → 123, „Titel“ → null.</summary>
    private static long? LeadingId(string seg)
    {
        var digits = new string(seg.TakeWhile(char.IsDigit).ToArray());
        return digits.Length > 0 && (digits.Length == seg.Length || seg[digits.Length] == '-') && long.TryParse(digits, out var id) ? id : null;
    }

    private static int? PageOf(IEnumerable<string> segs, HttpContext h)
    {
        foreach (var s in segs)
            if (s.StartsWith("page", StringComparison.OrdinalIgnoreCase) && int.TryParse(s[4..], out var p)) return p;
        return int.TryParse(h.Request.Query["page"], out var qp) ? qp : null;
    }

    private static async Task<IResult> LegacyThreadAsync(HttpContext h, LegacyLookup lookup, BoardCache cache)
    {
        var q = h.Request.Query;
        if (long.TryParse(q["p"], out var pid) || long.TryParse(q["postid"], out pid)) return Results.Redirect($"/post/{pid}", permanent: true);

        var segs = LegacySegments(h, "showthread.php");
        long? tid = long.TryParse(q["t"], out var t) || long.TryParse(q["threadid"], out t) ? t : segs.Count > 0 ? LeadingId(segs[0]) : null;

        // Seite umrechnen: vB hatte evtl. eine andere Anzahl Beiträge pro Seite
        var vbPage = PageOf(segs.Skip(1), h) ?? 1;
        var pageQuery = "";
        if (vbPage > 1)
        {
            var ours = Math.Max(1, (await cache.GetAsync()).Settings.PostsPerPage);
            var page = (vbPage - 1) * await lookup.VbPostsPerPageAsync() / ours + 1;
            if (page > 1) pageQuery = $"?page={page}";
        }

        if (tid is long id) return Results.Redirect($"/thread/{id}{pageQuery}", permanent: true);
        if (segs.Count == 0) return Results.Redirect("/");

        // Nur der Titel in der URL → Thema über den Titel suchen
        var hits = await lookup.FindThreadsAsync(segs[0]);
        if (hits.Count == 1) return Results.Redirect($"/thread/{hits[0].ThreadId}{pageQuery}", permanent: true);
        // Mehrdeutig oder nicht gefunden → Titelsuche zeigen
        var words = segs[0].Replace('-', ' ').Replace('_', ' ').Trim();
        return Results.Redirect($"/search?q={Uri.EscapeDataString(words)}&in=titles");
    }

    private static async Task<IResult> LegacyForumAsync(HttpContext h, LegacyLookup lookup)
    {
        var q = h.Request.Query;
        if (int.TryParse(q["f"], out var fid) || int.TryParse(q["forumid"], out fid)) return Results.Redirect($"/forum/{fid}", permanent: true);
        var segs = LegacySegments(h, "forumdisplay.php");
        if (segs.Count == 0) return Results.Redirect("/");
        if (LeadingId(segs[0]) is long id) return Results.Redirect($"/forum/{id}", permanent: true);
        var hits = await lookup.FindForumsAsync(segs[0]);
        return hits.Count == 1 ? Results.Redirect($"/forum/{hits[0]}", permanent: true) : Results.Redirect("/");
    }

    private static async Task<IResult> LegacyMemberAsync(HttpContext h, LegacyLookup lookup)
    {
        var q = h.Request.Query;
        if (long.TryParse(q["u"], out var uid) || long.TryParse(q["userid"], out uid)) return Results.Redirect($"/user/{uid}", permanent: true);
        var segs = LegacySegments(h, "member.php");
        if (segs.Count == 0) return Results.Redirect("/members");
        if (LeadingId(segs[0]) is long id) return Results.Redirect($"/user/{id}", permanent: true);
        return await lookup.FindUserAsync(segs[0]) is long u ? Results.Redirect($"/user/{u}", permanent: true) : Results.Redirect("/members");
    }
}
