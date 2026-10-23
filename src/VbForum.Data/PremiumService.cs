using Dapper;
using Microsoft.Extensions.Logging;
using VbForum.Core.Models;
using VbForum.Core.Text;

namespace VbForum.Data;

/// <summary>
/// Kostenpflichtige Zugänge: Abo-Modelle (vbf_plan) und gebuchte Abos (vbf_subscription).
/// Der Zugang selbst läuft über eine Benutzergruppe (z. B. „Premium“), die das Zahlungssystem per API
/// vergibt und wieder entzieht. Abgelaufene Abos entzieht das Forum zusätzlich selbst.
/// </summary>
public sealed class PremiumService(Db db, BoardCache cache, ForumAdminService admin, UserRepository users, ILogger<PremiumService> log)
{
    // ---------- Modelle ----------

    public sealed class Plan
    {
        public int PlanId { get; set; }
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public int PriceCents { get; set; }
        public string Currency { get; set; } = "EUR";
        /// <summary>Laufzeit in Monaten, 0 = lebenslang.</summary>
        public int Months { get; set; } = 1;
        public int GroupId { get; set; }
        public bool PrimaryGroup { get; set; }
        /// <summary>Bezahl-Link bei rstpay. Platzhalter: {userid} {username} {email} {planid} {price} {returnurl}</summary>
        public string PayUrl { get; set; } = "";
        public bool Active { get; set; } = true;
        public int DisplayOrder { get; set; }
        public bool Highlight { get; set; }
        /// <summary>„rstpay“ (Session über die rstpay-API) oder „link“ (fester Bezahl-Link mit Platzhaltern).</summary>
        public string PayMode { get; set; } = "rstpay";
        /// <summary>rstpay: Laufzeit in Tagen (Parameter duration).</summary>
        public int RstDays { get; set; } = 30;
        /// <summary>rstpay: Zahlart (paymenttype), 1 = Lastschrift.</summary>
        public int RstPaymentType { get; set; } = 1;
        /// <summary>rstpay: wiederkehrende Abbuchung (rebilling).</summary>
        public bool RstRebilling { get; set; }
        /// <summary>rstpay: Abstand der Folgeabbuchungen in Tagen (rebillingduration); 0 = wie Laufzeit.</summary>
        public int RstRebillingDays { get; set; }
        public int EffectiveRebillingDays => RstRebillingDays > 0 ? RstRebillingDays : RstDays;

        public bool IsLifetime => Months == 0;
        public string PriceText => (PriceCents / 100m).ToString("N2", System.Globalization.CultureInfo.GetCultureInfo("de-DE")) + (Currency == "EUR" ? " €" : " " + Currency);
        public string DurationText => Months switch { 0 => "Lebenslang", 1 => "1 Monat", 12 => "1 Jahr", _ => $"{Months} Monate" };
        public string? PerMonthText => Months > 1 ? ((PriceCents / 100m) / Months).ToString("N2", System.Globalization.CultureInfo.GetCultureInfo("de-DE")) + " € / Monat" : null;
    }

    private const string PlanCols = """
        CAST(planid AS SIGNED) AS PlanId, title AS Title, description AS Description, CAST(price_cents AS SIGNED) AS PriceCents, currency AS Currency,
        CAST(months AS SIGNED) AS Months, CAST(groupid AS SIGNED) AS GroupId, primarygroup <> 0 AS PrimaryGroup, payurl AS PayUrl,
        active <> 0 AS Active, CAST(displayorder AS SIGNED) AS DisplayOrder, highlight <> 0 AS Highlight,
        paymode AS PayMode, CAST(rst_days AS SIGNED) AS RstDays, CAST(rst_paymenttype AS SIGNED) AS RstPaymentType, rst_rebilling <> 0 AS RstRebilling, CAST(rst_rebillingdays AS SIGNED) AS RstRebillingDays
        """;

    public async Task<List<Plan>> PlansAsync(bool onlyActive)
    {
        await using var c = await db.OpenAsync();
        return (await c.QueryAsync<Plan>($"SELECT {PlanCols} FROM {Tables.OwnPlan} {(onlyActive ? "WHERE active <> 0" : "")} ORDER BY displayorder, price_cents")).ToList();
    }

    public async Task<Plan?> PlanAsync(int id)
    {
        await using var c = await db.OpenAsync();
        return await c.QueryFirstOrDefaultAsync<Plan>($"SELECT {PlanCols} FROM {Tables.OwnPlan} WHERE planid = @id", new { id });
    }

    public async Task<int> SavePlanAsync(Plan p)
    {
        await using var c = await db.OpenAsync();
        var args = new
        {
            p.PlanId, title = p.Title.Trim(), description = p.Description.Trim(), price = Math.Max(0, p.PriceCents), currency = (p.Currency ?? "EUR").Trim().ToUpperInvariant(),
            months = Math.Max(0, p.Months), p.GroupId, primary = p.PrimaryGroup ? 1 : 0, payurl = p.PayUrl.Trim(), active = p.Active ? 1 : 0, p.DisplayOrder, hl = p.Highlight ? 1 : 0,
            mode = p.PayMode == "link" ? "link" : "rstpay", days = Math.Max(0, p.RstDays), ptype = p.RstPaymentType, rebill = p.RstRebilling ? 1 : 0, rdays = Math.Max(0, p.RstRebillingDays),
        };
        if (p.PlanId > 0)
        {
            await c.ExecuteAsync($"""
                UPDATE {Tables.OwnPlan} SET title = @title, description = @description, price_cents = @price, currency = @currency, months = @months,
                  groupid = @GroupId, primarygroup = @primary, payurl = @payurl, active = @active, displayorder = @DisplayOrder, highlight = @hl,
                  paymode = @mode, rst_days = @days, rst_paymenttype = @ptype, rst_rebilling = @rebill, rst_rebillingdays = @rdays
                WHERE planid = @PlanId
                """, args);
            return p.PlanId;
        }
        return await c.ExecuteScalarAsync<int>($"""
            INSERT INTO {Tables.OwnPlan} (title, description, price_cents, currency, months, groupid, primarygroup, payurl, active, displayorder, highlight,
                paymode, rst_days, rst_paymenttype, rst_rebilling, rst_rebillingdays)
            VALUES (@title, @description, @price, @currency, @months, @GroupId, @primary, @payurl, @active, @DisplayOrder, @hl, @mode, @days, @ptype, @rebill, @rdays);
            SELECT LAST_INSERT_ID();
            """, args);
    }

    public async Task DeletePlanAsync(int id)
    {
        await using var c = await db.OpenAsync();
        await c.ExecuteAsync($"DELETE FROM {Tables.OwnPlan} WHERE planid = @id", new { id });
    }

    /// <summary>Bezahl-Link mit eingesetzten Benutzerdaten.</summary>
    public static string BuildPayUrl(Plan p, ForumUser u, string email, string returnUrl)
    {
        static string E(string? v) => Uri.EscapeDataString(v ?? "");
        return p.PayUrl
            .Replace("{userid}", u.UserId.ToString())
            .Replace("{username}", E(u.Username))
            .Replace("{email}", E(email))
            .Replace("{planid}", p.PlanId.ToString())
            .Replace("{price}", (p.PriceCents / 100m).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))
            .Replace("{returnurl}", E(returnUrl));
    }

    /// <summary>Gehört das Forum (oder ein übergeordnetes) zu den kostenpflichtigen Foren?</summary>
    public static bool IsPremiumForum(BoardSnapshot s, Forum f) => f.ParentIds.Any(s.Settings.PremiumForums.Contains);

    // ---------- Abos ----------

    public sealed class Subscription
    {
        public long SubscriptionId { get; set; }
        public long UserId { get; set; }
        public string Username { get; set; } = "";
        public int PlanId { get; set; }
        public string PlanTitle { get; set; } = "";
        public int GroupId { get; set; }
        public long StartDate { get; set; }
        /// <summary>0 = lebenslang.</summary>
        public long EndDate { get; set; }
        public string Status { get; set; } = "active";
        public string Reference { get; set; } = "";
        public string Source { get; set; } = "";
        public long Created { get; set; }
        public long Updated { get; set; }

        public bool IsActive(long now) => Status == "active" && (EndDate == 0 || EndDate > now);
    }

    private const string SubCols = """
        CAST(s.subscriptionid AS SIGNED) AS SubscriptionId, CAST(s.userid AS SIGNED) AS UserId, IFNULL(u.username, '') AS Username,
        CAST(s.planid AS SIGNED) AS PlanId, IFNULL(p.title, '') AS PlanTitle, CAST(s.groupid AS SIGNED) AS GroupId,
        CAST(s.startdate AS SIGNED) AS StartDate, CAST(s.enddate AS SIGNED) AS EndDate, s.status AS Status, s.reference AS Reference,
        s.source AS Source, CAST(s.created AS SIGNED) AS Created, CAST(s.updated AS SIGNED) AS Updated
        """;

    private string SubFrom => $"FROM {Tables.OwnSubscription} s LEFT JOIN {db.T.User} u ON u.userid = s.userid LEFT JOIN {Tables.OwnPlan} p ON p.planid = s.planid";

    public async Task<List<Subscription>> UserSubscriptionsAsync(long userId)
    {
        await using var c = await db.OpenAsync();
        return (await c.QueryAsync<Subscription>($"SELECT {SubCols} {SubFrom} WHERE s.userid = @userId ORDER BY s.subscriptionid DESC", new { userId })).ToList();
    }

    public async Task<PagedResult<Subscription>> SearchAsync(string? search, string? status, int page, int pageSize)
    {
        var where = new List<string> { "1 = 1" };
        if (!string.IsNullOrWhiteSpace(search)) where.Add("(u.username LIKE @s OR u.email LIKE @s OR s.reference LIKE @s)");
        var now = ForumTime.Now;
        if (status == "active") where.Add("s.status = 'active' AND (s.enddate = 0 OR s.enddate > @now)");
        else if (status == "ended") where.Add("NOT (s.status = 'active' AND (s.enddate = 0 OR s.enddate > @now))");
        var w = string.Join(" AND ", where);
        var p = new { s = $"%{search?.Trim()}%", now, off = (Math.Max(1, page) - 1) * pageSize, size = pageSize };
        await using var c = await db.OpenAsync();
        var total = await c.ExecuteScalarAsync<long>($"SELECT COUNT(*) {SubFrom} WHERE {w}", p);
        var items = (await c.QueryAsync<Subscription>($"SELECT {SubCols} {SubFrom} WHERE {w} ORDER BY s.subscriptionid DESC LIMIT @off, @size", p)).ToList();
        return new PagedResult<Subscription>(items, total, page, pageSize);
    }

    public sealed record Counts(long Active, long Lifetime, long EndingIn7Days);

    public async Task<Counts> CountsAsync()
    {
        var now = ForumTime.Now;
        await using var c = await db.OpenAsync();
        var r = await c.QueryFirstAsync<(long, long, long)>($"""
            SELECT
              CAST(IFNULL(SUM(status = 'active' AND (enddate = 0 OR enddate > @now)), 0) AS SIGNED),
              CAST(IFNULL(SUM(status = 'active' AND enddate = 0), 0) AS SIGNED),
              CAST(IFNULL(SUM(status = 'active' AND enddate > @now AND enddate < @soon), 0) AS SIGNED)
            FROM {Tables.OwnSubscription}
            """, new { now, soon = now + 7 * 86400 });
        return new Counts(r.Item1, r.Item2, r.Item3);
    }

    public sealed class GrantRequest
    {
        public long UserId { get; set; }
        public string? Username { get; set; }
        public string? Email { get; set; }
        public int PlanId { get; set; }
        /// <summary>Statt eines Modells direkt eine Gruppe vergeben.</summary>
        public int GroupId { get; set; }
        /// <summary>Ende (Unix-Sekunden). Ohne Angabe: Laufzeit des Modells; bei Gruppe ohne Modell lebenslang.</summary>
        public long? Until { get; set; }
        /// <summary>Transaktions-/Abo-Nummer von rstpay – gleiche Nummer erneut = Verlängerung/Aktualisierung statt neues Abo.</summary>
        public string? Reference { get; set; }
        public string Source { get; set; } = "api";
        /// <summary>Kein Enddatum setzen – das Zahlungssystem meldet das Ende selbst (rstpay „cancel“).</summary>
        public bool OpenEnded { get; set; }
        /// <summary>Hauptgruppe hart setzen wie das alte rstpay-Skript (usergroupid = X), statt Zusatzgruppe.</summary>
        public bool SetPrimary { get; set; }
    }

    public sealed record Result(bool Ok, string? Error, Subscription? Subscription = null, int Status = 200);

    public async Task<UserRow?> ResolveUserAsync(long userId, string? username, string? email)
    {
        if (userId > 0) return await users.GetAsync(userId);
        if (!string.IsNullOrWhiteSpace(username)) return await users.GetByNameAsync(username);
        if (!string.IsNullOrWhiteSpace(email))
        {
            var list = await users.GetByEmailAsync(email);
            return list.Count == 1 ? list[0] : null;
        }
        return null;
    }

    /// <summary>Abo freischalten oder verlängern: Benutzer kommt in die Gruppe des Modells.</summary>
    public async Task<Result> GrantAsync(GrantRequest r)
    {
        var user = await ResolveUserAsync(r.UserId, r.Username, r.Email);
        if (user == null) return new(false, "Benutzer nicht gefunden.", Status: 404);
        Plan? plan = null;
        if (r.PlanId > 0 && (plan = await PlanAsync(r.PlanId)) == null) return new(false, $"Abo-Modell {r.PlanId} nicht gefunden.", Status: 404);
        var groupId = plan?.GroupId ?? r.GroupId;
        var snap = await cache.GetAsync();
        if (groupId <= 0 || !snap.Groups.ContainsKey(groupId)) return new(false, "Keine gültige Benutzergruppe angegeben (planId oder groupId).", Status: 400);
        if (snap.Settings.AdminGroups.Contains(groupId) || snap.Settings.SuperModGroups.Contains(groupId))
            return new(false, "Administrator- und Moderatorgruppen können nicht über die API vergeben werden.", Status: 403);

        var now = ForumTime.Now;
        var reference = (r.Reference ?? "").Trim();
        await using var c = await db.OpenAsync();

        // Ende berechnen: ausdrücklich angegeben, sonst Laufzeit – bei Verlängerung ab dem bisherigen Ende
        long end;
        if (r.Until is long u && u > 0) end = u;
        else if (r.OpenEnded || plan == null || plan.IsLifetime) end = 0;
        else
        {
            var currentEnd = await c.ExecuteScalarAsync<long?>($"""
                SELECT CAST(MAX(enddate) AS SIGNED) FROM {Tables.OwnSubscription}
                WHERE userid = @uid AND groupid = @groupId AND status = 'active' AND enddate > @now
                  AND (@reference = '' OR reference <> @reference)
                """, new { uid = user.UserId, groupId, now, reference }) ?? 0;
            var from = Math.Max(now, currentEnd);
            end = ForumTime.ToUnix(ForumTime.Local(from).AddMonths(plan.Months));
        }

        long id;
        var existing = reference.Length == 0 ? null : await c.QueryFirstOrDefaultAsync<long?>(
            $"SELECT CAST(subscriptionid AS SIGNED) FROM {Tables.OwnSubscription} WHERE reference = @reference AND userid = @uid ORDER BY subscriptionid DESC LIMIT 1",
            new { reference, uid = user.UserId });
        if (existing is long ex)
        {
            await c.ExecuteAsync($"""
                UPDATE {Tables.OwnSubscription} SET planid = @planId, groupid = @groupId, enddate = @end, status = 'active', updated = @now
                WHERE subscriptionid = @ex
                """, new { planId = plan?.PlanId ?? 0, groupId, end, now, ex });
            id = ex;
        }
        else
        {
            id = await c.ExecuteScalarAsync<long>($"""
                INSERT INTO {Tables.OwnSubscription} (userid, planid, groupid, startdate, enddate, status, reference, source, created, updated)
                VALUES (@uid, @planId, @groupId, @now, @end, 'active', @reference, @source, @now, @now);
                SELECT LAST_INSERT_ID();
                """, new { uid = user.UserId, planId = plan?.PlanId ?? 0, groupId, now, end, reference, source = r.Source });
        }

        // Gruppe vergeben (ist sie schon gesetzt, passiert nichts)
        if (r.SetPrimary) await admin.SetPrimaryGroupAsync(user.UserId, groupId);
        else await admin.AddGroupMemberAsync(user.UserId, groupId, plan?.PrimaryGroup ?? false);
        cache.Invalidate();
        log.LogInformation("Premium: {User} → Gruppe {Group} bis {End} (Ref {Ref})", user.DisplayName, groupId, end == 0 ? "lebenslang" : ForumTime.Full(end), reference);
        return new(true, null, await GetAsync(c, id));
    }

    public sealed class RevokeRequest
    {
        public long UserId { get; set; }
        public string? Username { get; set; }
        public string? Email { get; set; }
        public int PlanId { get; set; }
        public int GroupId { get; set; }
        public string? Reference { get; set; }
        public string Source { get; set; } = "api";
    }

    /// <summary>Abo beenden: Abo(s) als „beendet“ markieren und Gruppe entziehen, sofern kein anderes Abo sie noch vergibt.</summary>
    public async Task<Result> RevokeAsync(RevokeRequest r)
    {
        var user = await ResolveUserAsync(r.UserId, r.Username, r.Email);
        if (user == null) return new(false, "Benutzer nicht gefunden.", Status: 404);
        var groupId = r.GroupId;
        if (groupId <= 0 && r.PlanId > 0) groupId = (await PlanAsync(r.PlanId))?.GroupId ?? 0;
        var reference = (r.Reference ?? "").Trim();
        await using var c = await db.OpenAsync();
        if (groupId <= 0 && reference.Length > 0)
            groupId = await c.ExecuteScalarAsync<int?>($"SELECT CAST(groupid AS SIGNED) FROM {Tables.OwnSubscription} WHERE userid = @uid AND reference = @reference LIMIT 1",
                new { uid = user.UserId, reference }) ?? 0;
        if (groupId <= 0) return new(false, "Keine Gruppe angegeben (planId, groupId oder reference).", Status: 400);
        var snap = await cache.GetAsync();
        if (snap.Settings.AdminGroups.Contains(groupId) || snap.Settings.SuperModGroups.Contains(groupId))
            return new(false, "Administrator- und Moderatorgruppen können nicht über die API entzogen werden.", Status: 403);

        var now = ForumTime.Now;
        await c.ExecuteAsync($"""
            UPDATE {Tables.OwnSubscription} SET status = 'revoked', updated = @now
            WHERE userid = @uid AND groupid = @groupId AND status = 'active' AND (@reference = '' OR reference = @reference)
            """, new { uid = user.UserId, groupId, now, reference });
        await RemoveGroupIfUnusedAsync(c, user.UserId, groupId, now);
        log.LogInformation("Premium beendet: {User} ← Gruppe {Group} (Ref {Ref})", user.DisplayName, groupId, reference);
        return new(true, null);
    }

    /// <summary>Aktive Abos eines Benutzers für eine Gruppe beenden, ohne die Gruppe anzufassen (rstpay setzt sie selbst).</summary>
    public async Task EndSubscriptionsAsync(long userId, int groupId, string status)
    {
        await using var c = await db.OpenAsync();
        await c.ExecuteAsync($"UPDATE {Tables.OwnSubscription} SET status = @status, updated = @now WHERE userid = @userId AND groupid = @groupId AND status = 'active'",
            new { userId, groupId, status, now = ForumTime.Now });
    }

    private async Task RemoveGroupIfUnusedAsync(MySqlConnector.MySqlConnection c, long userId, int groupId, long now)
    {
        var stillActive = await c.ExecuteScalarAsync<long>($"""
            SELECT COUNT(*) FROM {Tables.OwnSubscription}
            WHERE userid = @userId AND groupid = @groupId AND status = 'active' AND (enddate = 0 OR enddate > @now)
            """, new { userId, groupId, now });
        if (stillActive > 0) return;
        await admin.RemoveGroupMemberAsync(userId, groupId, (await cache.GetAsync()).Settings.RegisteredGroupId);
        cache.Invalidate();
    }

    /// <summary>Abgelaufene Abos beenden (läuft stündlich im Hintergrund).</summary>
    public async Task<int> ExpireAsync()
    {
        var now = ForumTime.Now;
        await using var c = await db.OpenAsync();
        var due = (await c.QueryAsync<(long, long, long)>($"""
            SELECT CAST(subscriptionid AS SIGNED), CAST(userid AS SIGNED), CAST(groupid AS SIGNED) FROM {Tables.OwnSubscription}
            WHERE status = 'active' AND enddate > 0 AND enddate <= @now
            """, new { now })).ToList();
        foreach (var (id, userId, groupId) in due)
        {
            await c.ExecuteAsync($"UPDATE {Tables.OwnSubscription} SET status = 'expired', updated = @now WHERE subscriptionid = @id", new { id, now });
            await RemoveGroupIfUnusedAsync(c, userId, (int)groupId, now);
        }
        return due.Count;
    }

    private async Task<Subscription?> GetAsync(MySqlConnector.MySqlConnection c, long id) =>
        await c.QueryFirstOrDefaultAsync<Subscription>($"SELECT {SubCols} {SubFrom} WHERE s.subscriptionid = @id", new { id });
}
