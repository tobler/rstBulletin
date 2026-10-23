using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VbForum.Core.Text;
using VbForum.Data;

namespace VbForum.Web.Infrastructure;

/// <summary>
/// Schnittstelle für das Zahlungssystem (rstpay). Authentifizierung: Header „Authorization: Bearer &lt;Key&gt;“
/// oder „X-Api-Key: &lt;Key&gt;“. Ohne gesetzten Schlüssel ist die API abgeschaltet.
/// </summary>
public static class PremiumApi
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string Iso(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    public sealed class Body
    {
        public long UserId { get; set; }
        public string? Username { get; set; }
        public string? Email { get; set; }
        public int PlanId { get; set; }
        public int GroupId { get; set; }
        /// <summary>Ende als Unix-Sekunden oder ISO-Datum (2026-12-31 / 2026-12-31T23:59:59Z).</summary>
        public JsonElement? Until { get; set; }
        public string? Reference { get; set; }
    }

    public static void MapPremiumApi(this WebApplication app)
    {
        var g = app.MapGroup("/api/premium");

        g.MapGet("/plans", async (HttpContext http, PremiumService premium, BoardCache cache) =>
        {
            if (await AuthAsync(http, cache) is { } fail) return fail;
            var plans = await premium.PlansAsync(onlyActive: false);
            return Results.Json(plans.Select(p => new
            {
                planId = p.PlanId, p.Title, priceCents = p.PriceCents, p.Currency, months = p.Months, lifetime = p.IsLifetime, groupId = p.GroupId, active = p.Active,
            }), Json);
        });

        g.MapGet("/user", async (HttpContext http, long? userId, string? username, string? email, PremiumService premium, UserRepository users, BoardCache cache) =>
        {
            if (await AuthAsync(http, cache) is { } fail) return fail;
            var u = await premium.ResolveUserAsync(userId ?? 0, username, email);
            if (u == null) return Results.Json(new { ok = false, error = "Benutzer nicht gefunden." }, Json, statusCode: 404);
            var subs = await premium.UserSubscriptionsAsync(u.UserId);
            var now = ForumTime.Now;
            return Results.Json(new
            {
                ok = true, userId = u.UserId, username = u.DisplayName, email = u.Email, primaryGroupId = u.UserGroupId,
                groupIds = u.AllGroupIds().ToArray(),
                subscriptions = subs.Select(s => Out(s, now)),
            }, Json);
        });

        g.MapPost("/grant", async (HttpContext http, PremiumService premium, BoardCache cache) =>
        {
            if (await AuthAsync(http, cache) is { } fail) return fail;
            var b = await ReadAsync(http);
            if (b == null) return Results.Json(new { ok = false, error = "Ungültiges JSON." }, Json, statusCode: 400);
            if (!TryUntil(b.Until, out var until)) return Results.Json(new { ok = false, error = "„until“ ist kein gültiges Datum." }, Json, statusCode: 400);
            var r = await premium.GrantAsync(new PremiumService.GrantRequest
            {
                UserId = b.UserId, Username = b.Username, Email = b.Email, PlanId = b.PlanId, GroupId = b.GroupId, Until = until, Reference = b.Reference, Source = "api",
            });
            await LogAsync(http, "premium.grant", r.Ok ? $"{r.Subscription!.UserId} {r.Subscription.Username} → Gruppe {r.Subscription.GroupId}" : r.Error!);
            return r.Ok ? Results.Json(new { ok = true, subscription = Out(r.Subscription!, ForumTime.Now) }, Json)
                        : Results.Json(new { ok = false, error = r.Error }, Json, statusCode: r.Status);
        });

        g.MapPost("/revoke", async (HttpContext http, PremiumService premium, BoardCache cache) =>
        {
            if (await AuthAsync(http, cache) is { } fail) return fail;
            var b = await ReadAsync(http);
            if (b == null) return Results.Json(new { ok = false, error = "Ungültiges JSON." }, Json, statusCode: 400);
            var r = await premium.RevokeAsync(new PremiumService.RevokeRequest
            {
                UserId = b.UserId, Username = b.Username, Email = b.Email, PlanId = b.PlanId, GroupId = b.GroupId, Reference = b.Reference,
            });
            await LogAsync(http, "premium.revoke", r.Ok ? $"{b.UserId}{b.Username}{b.Email} ← Plan {b.PlanId} Gruppe {b.GroupId} Ref {b.Reference}" : r.Error!);
            return r.Ok ? Results.Json(new { ok = true }, Json) : Results.Json(new { ok = false, error = r.Error }, Json, statusCode: r.Status);
        });
    }

    private static object Out(PremiumService.Subscription s, long now) => new
    {
        subscriptionId = s.SubscriptionId, userId = s.UserId, planId = s.PlanId, groupId = s.GroupId,
        start = Iso(s.StartDate),
        end = s.EndDate == 0 ? null : Iso(s.EndDate),
        lifetime = s.EndDate == 0, status = s.IsActive(now) ? "active" : s.Status == "active" ? "expired" : s.Status, reference = s.Reference,
    };

    private static async Task<Body?> ReadAsync(HttpContext http)
    {
        try { return await JsonSerializer.DeserializeAsync<Body>(http.Request.Body, Json); }
        catch { return null; }
    }

    private static bool TryUntil(JsonElement? e, out long? until)
    {
        until = null;
        if (e is not { } v || v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return true;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) { until = n; return true; }
        if (v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString() ?? "";
            if (long.TryParse(s, out var n2)) { until = n2; return true; }
            if (DateTime.TryParseExact(s, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d))
            { until = ForumTime.ToUnix(d.Date.AddDays(1)) - 1; return true; } // ganzer Tag inklusive
            if (DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var dto))
            { until = dto.ToUnixTimeSeconds(); return true; }
        }
        return false;
    }

    /// <summary>Schlüssel prüfen (zeitkonstant). Liefert eine Fehlerantwort oder null.</summary>
    private static async Task<IResult?> AuthAsync(HttpContext http, BoardCache cache)
    {
        var key = (await cache.GetAsync()).Settings.PremiumApiKey;
        if (string.IsNullOrEmpty(key)) return Results.Json(new { ok = false, error = "Die Premium-API ist nicht aktiviert." }, Json, statusCode: 503);
        var given = http.Request.Headers["X-Api-Key"].ToString();
        var auth = http.Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(given) && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) given = auth[7..].Trim();
        var ok = given.Length > 0 && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(key));
        return ok ? null : Results.Json(new { ok = false, error = "Ungültiger API-Schlüssel." }, Json, statusCode: 401);
    }

    private static async Task LogAsync(HttpContext http, string action, string target)
    {
        try
        {
            var audit = http.RequestServices.GetRequiredService<TokenRepository>();
            await audit.LogAsync(new ForumUser { Username = "rstpay-API" }, action, target.Length > 250 ? target[..250] : target, http.ClientIp());
        }
        catch { /* Protokoll ist nicht kritisch */ }
    }
}
