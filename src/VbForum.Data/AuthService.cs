using System.Net.Mail;
using System.Text.RegularExpressions;
using VbForum.Core.Models;
using VbForum.Core.Security;
using VbForum.Core.Text;

namespace VbForum.Data;

public sealed partial class AuthService(UserRepository users, BoardCache cache, TokenRepository tokens, MailService mail)
{
    public sealed record LoginResult(UserRow? User, string? Error);

    public async Task<LoginResult> ValidateAsync(string nameOrEmail, string password)
    {
        if (string.IsNullOrWhiteSpace(nameOrEmail) || string.IsNullOrEmpty(password))
            return new LoginResult(null, "Bitte Benutzername und Passwort eingeben.");
        var u = await users.GetByNameAsync(nameOrEmail);
        if (u == null && nameOrEmail.Contains('@'))
        {
            var byMail = await users.GetByEmailAsync(nameOrEmail);
            if (byMail.Count == 1) u = byMail[0];
        }
        if (u == null) return new LoginResult(null, "Benutzername oder Passwort ist falsch.");

        bool ok = false;
        if (!string.IsNullOrEmpty(u.ModernHash))
        {
            ok = VbPassword.VerifyModern(password, u.ModernHash, out var rehash);
            if (ok && rehash) await users.SetModernHashAsync(u.UserId, VbPassword.HashModern(password));
        }
        if (!ok && VbPassword.VerifyLegacy(password, u.Password, u.Salt))
        {
            // Erster Login im neuen Forum (oder Passwort im alten vB geändert) → modernen Hash anlegen
            ok = true;
            await users.SetModernHashAsync(u.UserId, VbPassword.HashModern(password));
            u = await users.GetAsync(u.UserId) ?? u;
        }
        if (!ok) return new LoginResult(null, "Benutzername oder Passwort ist falsch.");

        var s = await cache.GetAsync();
        if (s.Settings.BannedGroups.Contains(u.UserGroupId))
        {
            var ban = await users.BanInfoAsync(u.UserId);
            var until = ban is { LiftDate: > 0 } b ? $" bis {ForumTime.Full(b.LiftDate)}" : "";
            var reason = ban is { Reason.Length: > 0 } r ? $" Grund: {HtmlUtil.CleanTitle(r.Reason)}" : "";
            return new LoginResult(null, $"Dein Konto ist gesperrt{until}.{reason}");
        }
        return new LoginResult(u, null);
    }

    public static string Stamp(UserRow u) => VbPassword.SecurityStamp(u.ModernHash ?? u.Password);

    // ---------- Registrierung ----------

    [GeneratedRegex(@"^[\p{L}\p{N} ._\-]{3,25}$")]
    private static partial Regex UsernameRegex();

    public static bool ValidEmail(string email)
    {
        try { var a = new MailAddress(email.Trim()); return a.Address == email.Trim() && email.Contains('.'); }
        catch { return false; }
    }

    public static string? PasswordProblem(string pw) =>
        pw.Length < 8 ? "Das Passwort muss mindestens 8 Zeichen lang sein." :
        pw.Length > 200 ? "Das Passwort ist zu lang." : null;

    public sealed record RegisterResult(bool Ok, string? Error, long UserId, bool NeedsConfirmation);

    public async Task<RegisterResult> RegisterAsync(string username, string email, string password, string ip)
    {
        var s = (await cache.GetAsync()).Settings;
        if (!s.AllowRegistration) return new(false, "Registrierungen sind derzeit deaktiviert.", 0, false);
        username = username.Trim();
        if (!UsernameRegex().IsMatch(username) || username.Trim('.', '-', '_', ' ').Length < 3)
            return new(false, "Der Benutzername muss 3–25 Zeichen lang sein (Buchstaben, Ziffern, Leerzeichen, . _ -).", 0, false);
        if (!ValidEmail(email)) return new(false, "Bitte gib eine gültige E-Mail-Adresse ein.", 0, false);
        if (PasswordProblem(password) is { } pwErr) return new(false, pwErr, 0, false);
        if (await users.NameExistsAsync(username)) return new(false, "Dieser Benutzername ist bereits vergeben.", 0, false);
        if (await users.EmailExistsAsync(email)) return new(false, "Diese E-Mail-Adresse wird bereits verwendet.", 0, false);

        bool confirm = s.RequireEmailConfirmation && s.SmtpConfigured;
        var id = await users.CreateAsync(username, email, password, confirm ? s.AwaitingEmailGroupId : s.RegisteredGroupId, ip, s.SyncLegacyPassword);
        cache.Invalidate();
        if (confirm) await SendConfirmationAsync(id, username, email);
        return new(true, null, id, confirm);
    }

    public async Task SendConfirmationAsync(long userId, string username, string email)
    {
        var token = await tokens.CreateAsync(userId, "confirm", email, TimeSpan.FromDays(7));
        var url = await mail.AbsoluteUrlAsync($"/account/confirm?token={Uri.EscapeDataString(token)}");
        var board = (await cache.GetAsync()).Settings.BoardName;
        await mail.SendAsync(email, $"Bitte bestätige deine E-Mail-Adresse – {board}", new MailContent(
            $"Hallo {username},",
            new[] { $"schön, dass du dich bei {board} registriert hast! Bitte bestätige noch deine E-Mail-Adresse, damit dein Konto freigeschaltet wird." },
            "E-Mail-Adresse bestätigen", url,
            Footnote: "Der Link ist 7 Tage gültig. Falls du dich nicht registriert hast, kannst du diese Mail einfach ignorieren."));
    }

    public async Task<bool> ConfirmAsync(string token)
    {
        var r = await tokens.ConsumeAsync(token, "confirm");
        if (r == null) return false;
        var u = await users.GetAsync(r.Value.UserId);
        var s = (await cache.GetAsync()).Settings;
        if (u == null) return false;
        if (u.UserGroupId == s.AwaitingEmailGroupId)
            await users.AdminUpdateAsync(u.UserId, u.DisplayName, u.Email, s.RegisteredGroupId, u.MemberGroupIds, u.UserTitle, u.CustomTitle == 1, u.Homepage, u.Posts);
        return true;
    }

    // ---------- Passwort ----------

    public async Task RequestResetAsync(string email)
    {
        if (!ValidEmail(email)) return;
        foreach (var u in (await users.GetByEmailAsync(email)).Take(3))
        {
            var token = await tokens.CreateAsync(u.UserId, "reset", "", TimeSpan.FromHours(2));
            var url = await mail.AbsoluteUrlAsync($"/account/reset?token={Uri.EscapeDataString(token)}");
            await mail.SendAsync(u.Email, "Passwort zurücksetzen", new MailContent(
                $"Hallo {u.DisplayName},",
                new[] { "für dein Konto wurde ein neues Passwort angefordert. Über den folgenden Knopf kannst du es festlegen." },
                "Neues Passwort festlegen", url,
                Footnote: "Der Link ist 2 Stunden gültig. Wenn du das nicht warst, ignoriere diese Mail – dein Passwort bleibt unverändert."));
        }
    }

    public async Task<string?> ResetAsync(string token, string password)
    {
        if (PasswordProblem(password) is { } e) return e;
        var r = await tokens.ConsumeAsync(token, "reset");
        if (r == null) return "Der Link ist ungültig oder abgelaufen. Bitte fordere einen neuen an.";
        var s = (await cache.GetAsync()).Settings;
        await users.SetPasswordAsync(r.Value.UserId, password, s.SyncLegacyPassword);
        return null;
    }

    public async Task<string?> ChangePasswordAsync(long userId, string current, string next)
    {
        var u = await users.GetAsync(userId);
        if (u == null) return "Benutzer nicht gefunden.";
        var check = await ValidateAsync(u.DisplayName, current);
        if (check.User == null) return "Das aktuelle Passwort ist falsch.";
        if (PasswordProblem(next) is { } e) return e;
        var s = (await cache.GetAsync()).Settings;
        await users.SetPasswordAsync(userId, next, s.SyncLegacyPassword);
        return null;
    }

    public async Task<string?> ChangeEmailAsync(long userId, string password, string email)
    {
        var u = await users.GetAsync(userId);
        if (u == null) return "Benutzer nicht gefunden.";
        if ((await ValidateAsync(u.DisplayName, password)).User == null) return "Das Passwort ist falsch.";
        if (!ValidEmail(email)) return "Bitte gib eine gültige E-Mail-Adresse ein.";
        if (await users.EmailExistsAsync(email, userId)) return "Diese E-Mail-Adresse wird bereits verwendet.";
        await users.SetEmailAsync(userId, email);
        return null;
    }
}
