using System.Globalization;
using Dapper;
using Microsoft.Extensions.Logging;
using VbForum.Core.Models;
using VbForum.Core.Text;

namespace VbForum.Data;

/// <summary>
/// Anbindung an rstpay: Bezahl-Session anfordern und Rückmeldungen verarbeiten.
/// Die Rückmeldung verhält sich wie das bisherige PHP-Skript (Aktionen, Gruppen, Antwort „OK &lt;transaction_id&gt;“ / „NOK“).
/// </summary>
public sealed class RstPayService(Db db, BoardCache cache, PremiumService premium, ForumAdminService admin, UserRepository users,
    ILogger<RstPayService> log)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    // ---------- Session anfordern ----------

    /// <param name="Detail">Technische Ursache (nur für Admins angezeigt).</param>
    public sealed record SessionResult(string? Url, string? Error, string? Detail = null);

    public async Task<SessionResult> CreatePaymentUrlAsync(PremiumService.Plan plan, ForumUser u)
    {
        var s = (await cache.GetAsync()).Settings;
        if (!s.RstPayConfigured) return new(null, "rstpay ist noch nicht eingerichtet.");
        var baseUrl = s.RstPayBaseUrl.TrimEnd('/');
        static string E(string v) => Uri.EscapeDataString(v);
        // rstpay erwartet den Betrag in Cent; Lifetime = duration 999999
        var amount = plan.PriceCents.ToString(CultureInfo.InvariantCulture);
        var duration = plan.IsLifetime && plan.RstDays <= 0 ? 999999 : plan.RstDays;
        var url = $"{baseUrl}/api/requestsession" +
                  $"?projectid={E(s.RstPayProjectId)}&merchantid={E(s.RstPayMerchantId)}&merchantkey={E(s.RstPayMerchantKey)}" +
                  $"&amount={amount}&duration={duration}&paymenttype={plan.RstPaymentType}" +
                  $"&description={E($"{plan.Title} - Username: {u.Username}")}" +
                  $"&rebilling={(plan.RstRebilling ? 1 : 0)}&currency={E(plan.Currency)}" +
                  $"&param1={u.UserId}&param2={E(u.Username)}";
        var safeUrl = url.Replace(E(s.RstPayMerchantKey), "***");
        try
        {
            using var resp = await Http.GetAsync(url);
            var body = (await resp.Content.ReadAsStringAsync()).Trim();
            var shortBody = body.Length > 300 ? body[..300] : body;
            if (!resp.IsSuccessStatusCode)
            {
                log.LogWarning("rstpay: requestsession HTTP {Status}: {Body} ({Url})", (int)resp.StatusCode, shortBody, safeUrl);
                return new(null, "Der Zahlungsdienst hat die Anfrage abgelehnt. Bitte später erneut versuchen.",
                    $"rstpay antwortete mit HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {shortBody}");
            }
            if (body.Length is 0 or > 200 || body.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '-' or '_')))
            {
                log.LogWarning("rstpay: unerwartete Antwort auf requestsession: {Answer} ({Url})", shortBody, safeUrl);
                return new(null, "Der Zahlungsdienst hat keine gültige Sitzung geliefert. Bitte später erneut versuchen.",
                    $"Antwort von rstpay statt Session-ID: „{shortBody}“");
            }
            return new($"{baseUrl}/payment/{body}", null);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "rstpay: requestsession fehlgeschlagen ({Url})", safeUrl);
            var root = ex;
            while (root.InnerException != null) root = root.InnerException;
            return new(null, "Der Zahlungsdienst ist gerade nicht erreichbar. Bitte später erneut versuchen.",
                $"{(ex is TaskCanceledException ? "Zeitüberschreitung nach 20 Sekunden" : root.Message)} – Adresse: {baseUrl}");
        }
    }

    // ---------- Rückmeldung ----------

    /// <summary>Verarbeitet eine Rückmeldung. Liefert den Antworttext („OK …“ oder „NOK“).</summary>
    public async Task<string> HandleCallbackAsync(IReadOnlyDictionary<string, string> p, string ip)
    {
        string V(string k) => p.TryGetValue(k, out var v) ? v.Trim() : "";
        var action = V("action").ToLowerInvariant();
        var transactionId = V("transaction_id");
        var param1 = V("param1");
        var s = (await cache.GetAsync()).Settings;
        var foreign = action is "foreign_booking" or "foreign_cancel" or "foreign_chargeback" or "free_booking";

        // param1 = Benutzer-ID, bei foreign_*/free_booking der Benutzername
        UserRow? user = null;
        if (param1.Length > 0)
            user = foreign ? await users.GetByNameAsync(param1) : long.TryParse(param1, out var id) ? await users.GetAsync(id) : null;

        string result;
        var ok = true;
        switch (action)
        {
            case "booking" or "transaction" or "rebilling" or "rebooking" or "foreign_booking":
                if (user == null) { result = "Benutzer nicht gefunden"; break; }
                if (IsProtected(user, s)) { result = "Admin/Moderator – nicht geändert"; break; }
                var plan = await MatchPlanAsync(V("amount"));
                var r = await premium.GrantAsync(new PremiumService.GrantRequest
                {
                    UserId = user.UserId, PlanId = plan?.PlanId ?? 0, GroupId = plan?.GroupId ?? s.RstPayPremiumGroupId,
                    Reference = transactionId, Source = "rstpay", OpenEnded = true, SetPrimary = true,
                });
                result = r.Ok ? $"Premium (Gruppe {r.Subscription!.GroupId}){(plan != null ? $", Modell {plan.Title}" : "")}" : r.Error!;
                break;

            case "free_booking":
                if (user == null) { result = "Benutzer nicht gefunden"; break; }
                if (user.UserGroupId == s.RstPayFreeFromGroupId)
                {
                    await admin.SetPrimaryGroupAsync(user.UserId, s.RstPayFreeGroupId);
                    result = $"Gruppe {s.RstPayFreeFromGroupId} → {s.RstPayFreeGroupId}";
                }
                else result = $"unverändert (Gruppe {user.UserGroupId})";
                break;

            case "cancel" or "foreign_cancel":
                if (user == null) { result = "Benutzer nicht gefunden"; break; }
                if (IsProtected(user, s)) { result = "Admin/Moderator – nicht geändert"; break; }
                await EndPremiumAsync(user, s, s.RstPayCancelGroupId, "revoked");
                result = $"gekündigt → Gruppe {s.RstPayCancelGroupId}";
                break;

            case "chargeback" or "foreign_chargeback":
                if (user == null) { result = "Benutzer nicht gefunden"; break; }
                if (IsProtected(user, s)) { result = "Admin/Moderator – nicht geändert"; break; }
                await EndPremiumAsync(user, s, s.RstPayChargebackGroupId, "chargeback");
                result = $"Rückbuchung → Gruppe {s.RstPayChargebackGroupId}";
                break;

            default:
                ok = false;
                result = "unbekannte Aktion";
                break;
        }
        cache.Invalidate();

        await LogAsync(action, transactionId, user?.UserId ?? 0, param1, V("amount"),
            $"{V("firstname")} {V("lastname")}".Trim(), V("country"), ip, (ok ? "" : "NOK: ") + result);
        log.LogInformation("rstpay {Action} {Param1} {Tx}: {Result}", action, param1, transactionId, result);
        // Wie das alte Skript: bekannte Aktion → „OK <transaction_id>“, sonst „NOK“
        return ok ? $"OK {transactionId}".TrimEnd() : "NOK";
    }

    private static bool IsProtected(UserRow u, Core.Settings.BoardSettings s) =>
        u.AllGroupIds().Any(g => s.AdminGroups.Contains(g) || s.SuperModGroups.Contains(g));

    /// <summary>Premium beenden: Abos schließen, Premium-Gruppe(n) entfernen, neue Hauptgruppe setzen.</summary>
    private async Task EndPremiumAsync(UserRow user, Core.Settings.BoardSettings s, int newPrimary, string status)
    {
        var premiumGroups = (await premium.PlansAsync(false)).Select(p => p.GroupId).Append(s.RstPayPremiumGroupId).Distinct().ToList();
        foreach (var g in premiumGroups) await premium.EndSubscriptionsAsync(user.UserId, g, status);
        await admin.SetPrimaryGroupAsync(user.UserId, newPrimary);
        foreach (var g in premiumGroups.Where(g => g != newPrimary))
            await admin.RemoveGroupMemberAsync(user.UserId, g, newPrimary);
    }

    /// <summary>Modell anhand des gezahlten Betrags finden („59.90“, „59,90“ oder Cent „5990“).</summary>
    private async Task<PremiumService.Plan?> MatchPlanAsync(string amount)
    {
        if (string.IsNullOrWhiteSpace(amount)) return null;
        var plans = await premium.PlansAsync(false);
        var a = amount.Replace(",", ".");
        if (decimal.TryParse(a, NumberStyles.Number, CultureInfo.InvariantCulture, out var euro))
        {
            var cents = (int)Math.Round(euro * 100);
            var hit = plans.FirstOrDefault(p => p.PriceCents == cents)
                      ?? (!a.Contains('.') ? plans.FirstOrDefault(p => p.PriceCents == (int)euro) : null);
            if (hit != null) return hit;
        }
        return null;
    }

    private async Task LogAsync(string action, string tx, long userId, string param1, string amount, string payer, string country, string ip, string result)
    {
        try
        {
            await using var c = await db.OpenAsync();
            await c.ExecuteAsync($"""
                INSERT INTO {Tables.OwnPayment} (dateline, action, transaction_id, userid, param1, amount, payer, country, ip, result)
                VALUES (@now, @action, @tx, @userId, @param1, @amount, @payer, @country, @ip, @result)
                """, new
            {
                now = ForumTime.Now, action = Cut(action, 30), tx = Cut(tx, 100), userId, param1 = Cut(param1, 255), amount = Cut(amount, 20),
                payer = Cut(payer, 200), country = Cut(country, 10), ip = Cut(ip, 45), result = Cut(result, 255),
            });
        }
        catch (Exception ex) { log.LogWarning(ex, "rstpay: Protokoll konnte nicht geschrieben werden"); }
    }

    private static string Cut(string v, int n) => v.Length > n ? v[..n] : v;

    public sealed record PaymentRow(long PaymentId, long Dateline, string Action, string TransactionId, long UserId, string Username,
        string Param1, string Amount, string Payer, string Country, string Ip, string Result);

    public async Task<List<PaymentRow>> RecentAsync(int limit = 200)
    {
        await using var c = await db.OpenAsync();
        return (await c.QueryAsync<PaymentRow>($"""
            SELECT CAST(p.paymentid AS SIGNED) AS PaymentId, CAST(p.dateline AS SIGNED) AS Dateline, p.action AS Action, p.transaction_id AS TransactionId,
                   CAST(p.userid AS SIGNED) AS UserId, IFNULL(u.username, '') AS Username, p.param1 AS Param1, p.amount AS Amount, p.payer AS Payer,
                   p.country AS Country, p.ip AS Ip, p.result AS Result
            FROM {Tables.OwnPayment} p LEFT JOIN {db.T.User} u ON u.userid = p.userid ORDER BY p.paymentid DESC LIMIT @limit
            """, new { limit })).ToList();
    }
}
