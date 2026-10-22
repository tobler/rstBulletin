using System.Net;
using System.Text;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;
using VbForum.Core.Settings;

namespace VbForum.Data;

/// <summary>Inhalt einer Mail – wird als HTML (mit Logo und Button) und als Textversion verschickt.</summary>
public sealed record MailContent(
    string Greeting,
    IReadOnlyList<string> Paragraphs,
    string? ButtonText = null,
    string? ButtonUrl = null,
    string? Quote = null,
    string? Footnote = null);

public sealed class MailService(BoardCache cache, AssetStore assets, ILogger<MailService> log)
{
    private const string Accent = "#1F56C3";

    public async Task<bool> IsConfiguredAsync() => (await cache.GetAsync()).Settings.SmtpConfigured;

    /// <summary>Sendet eine Mail. Gibt false zurück, wenn SMTP nicht eingerichtet ist oder der Versand scheitert.</summary>
    public async Task<bool> SendAsync(string to, string subject, MailContent content) => await TrySendAsync(to, subject, content) == null;

    /// <summary>Wie SendAsync, liefert aber die Fehlermeldung (null = verschickt) – für die Testmail im Admin-Bereich.</summary>
    public async Task<string?> TrySendAsync(string to, string subject, MailContent content)
    {
        var s = (await cache.GetAsync()).Settings;
        if (!s.SmtpConfigured) return "SMTP ist nicht eingerichtet (Server und Absenderadresse fehlen).";
        if (string.IsNullOrWhiteSpace(to)) return "Kein Empfänger angegeben.";
        try
        {
            var fromName = string.IsNullOrWhiteSpace(s.MailFromName) ? s.BoardName : s.MailFromName.Trim();
            var msg = new MimeMessage();
            msg.From.Add(new MailboxAddress(fromName, s.MailFrom.Trim()));
            msg.To.Add(MailboxAddress.Parse(to.Trim()));
            if (!string.IsNullOrWhiteSpace(s.MailReplyTo)) msg.ReplyTo.Add(MailboxAddress.Parse(s.MailReplyTo.Trim()));
            msg.Subject = subject;

            // HTML mit eingebettetem Logo + Textversion
            var logo = await LogoAsync(s);
            var body = new BodyBuilder { TextBody = RenderText(s, content) };
            if (logo != null)
            {
                var res = body.LinkedResources.Add("logo" + Extension(logo.ContentType), logo.Data, ContentType.Parse(logo.ContentType));
                res.ContentId = "logo";
            }
            body.HtmlBody = RenderHtml(s, subject, content, logo != null ? "cid:logo" : null);
            msg.Body = body.ToMessageBody();

            using var client = new MailKit.Net.Smtp.SmtpClient { Timeout = 20000 };
            await client.ConnectAsync(s.SmtpHost.Trim(), s.SmtpPort, Security(s));
            if (!string.IsNullOrWhiteSpace(s.SmtpUser))
                await client.AuthenticateAsync(s.SmtpUser.Trim(), s.SmtpPassword);
            await client.SendAsync(msg);
            await client.DisconnectAsync(true);
            return null;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Mailversand an {To} fehlgeschlagen", to);
            return ex.Message;
        }
    }

    /// <summary>Port 465 braucht implizites TLS (SslOnConnect), 587/25 meist STARTTLS.</summary>
    private static SecureSocketOptions Security(BoardSettings s) => (s.SmtpSecurity ?? "auto").ToLowerInvariant() switch
    {
        "ssl" => SecureSocketOptions.SslOnConnect,
        "starttls" => SecureSocketOptions.StartTls,
        "none" => SecureSocketOptions.None,
        _ => s.SmtpPort == 465 ? SecureSocketOptions.SslOnConnect
             : s.SmtpSsl ? SecureSocketOptions.StartTls : SecureSocketOptions.StartTlsWhenAvailable,
    };

    private static string Extension(string contentType) => contentType switch
    {
        "image/png" => ".png", "image/jpeg" => ".jpg", "image/gif" => ".gif", "image/webp" => ".webp", _ => ""
    };

    /// <summary>HTML für die Vorschau im Admin-Bereich (Logo als data:-URL).</summary>
    public async Task<string> PreviewAsync(string subject, MailContent content)
    {
        var s = (await cache.GetAsync()).Settings;
        var logo = await LogoAsync(s);
        return RenderHtml(s, subject, content, logo == null ? null : $"data:{logo.ContentType};base64,{Convert.ToBase64String(logo.Data)}");
    }

    public async Task<string> AbsoluteUrlAsync(string path)
    {
        var s = (await cache.GetAsync()).Settings;
        return string.IsNullOrEmpty(s.BaseUrl) ? path : s.BaseUrl.TrimEnd('/') + path;
    }

    /// <summary>Logo nur als Rasterbild – SVG zeigen viele Mailprogramme nicht an.</summary>
    private async Task<AssetStore.Asset?> LogoAsync(BoardSettings s)
    {
        if (string.IsNullOrEmpty(s.LogoFile)) return null;
        try
        {
            var a = await assets.GetAsync("logo");
            return a != null && a.ContentType != "image/svg+xml" && a.Data.Length < 500_000 ? a : null;
        }
        catch { return null; }
    }

    // ---------- Vorlagen ----------

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    public static string RenderText(BoardSettings s, MailContent c)
    {
        var sb = new StringBuilder();
        sb.Append(c.Greeting).Append("\n\n");
        foreach (var p in c.Paragraphs) sb.Append(p).Append("\n\n");
        if (!string.IsNullOrEmpty(c.Quote)) sb.Append(string.Join("\n", c.Quote.Split('\n').Select(l => "> " + l))).Append("\n\n");
        if (!string.IsNullOrEmpty(c.ButtonUrl)) sb.Append(c.ButtonText).Append(":\n").Append(c.ButtonUrl).Append("\n\n");
        if (!string.IsNullOrEmpty(c.Footnote)) sb.Append(c.Footnote).Append("\n\n");
        sb.Append("-- \n").Append(s.BoardName);
        if (!string.IsNullOrEmpty(s.BaseUrl)) sb.Append('\n').Append(s.BaseUrl);
        return sb.ToString();
    }

    public static string RenderHtml(BoardSettings s, string subject, MailContent c, string? logoSrc)
    {
        var board = E(s.BoardName);
        var baseUrl = s.BaseUrl?.TrimEnd('/') ?? "";
        var head = logoSrc != null
            ? $"<img src=\"{E(logoSrc)}\" alt=\"{board}\" style=\"max-height:48px;max-width:240px;height:auto;display:block;border:0\">"
            : $"<span style=\"font-size:20px;font-weight:700;color:#14171F\">{board}</span>";
        if (!string.IsNullOrEmpty(baseUrl)) head = $"<a href=\"{E(baseUrl)}\" style=\"text-decoration:none\">{head}</a>";

        var body = new StringBuilder();
        body.Append($"<p style=\"margin:0 0 16px\">{E(c.Greeting)}</p>");
        foreach (var p in c.Paragraphs)
            body.Append($"<p style=\"margin:0 0 16px\">{E(p).Replace("\n", "<br>")}</p>");
        if (!string.IsNullOrEmpty(c.Quote))
            body.Append($"<div style=\"margin:0 0 20px;padding:12px 16px;background:#F4F6FA;border-left:3px solid {Accent};border-radius:6px;color:#3B4252\">{E(c.Quote).Replace("\n", "<br>")}</div>");
        if (!string.IsNullOrEmpty(c.ButtonUrl))
        {
            body.Append($"""
                <table role="presentation" cellspacing="0" cellpadding="0" border="0" style="margin:8px 0 20px"><tr>
                <td style="border-radius:8px;background:{Accent}"><a href="{E(c.ButtonUrl)}" style="display:inline-block;padding:12px 22px;font-weight:600;font-size:15px;color:#ffffff;text-decoration:none;border-radius:8px">{E(c.ButtonText)}</a></td>
                </tr></table>
                <p style="margin:0 0 16px;font-size:12px;color:#6B7280">Falls der Knopf nicht funktioniert, diesen Link in den Browser kopieren:<br><a href="{E(c.ButtonUrl)}" style="color:{Accent};word-break:break-all">{E(c.ButtonUrl)}</a></p>
                """);
        }
        if (!string.IsNullOrEmpty(c.Footnote))
            body.Append($"<p style=\"margin:0;font-size:13px;color:#6B7280\">{E(c.Footnote)}</p>");

        var footerLink = string.IsNullOrEmpty(baseUrl) ? board : $"<a href=\"{E(baseUrl)}\" style=\"color:#6B7280\">{board}</a>";
        return $"""
            <!doctype html>
            <html lang="de"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>{E(subject)}</title></head>
            <body style="margin:0;padding:0;background:#EEF1F6">
            <table role="presentation" width="100%" cellspacing="0" cellpadding="0" border="0" style="background:#EEF1F6"><tr><td align="center" style="padding:28px 12px">
              <table role="presentation" width="100%" cellspacing="0" cellpadding="0" border="0" style="max-width:580px;background:#ffffff;border-radius:12px;border:1px solid #DDE2EA">
                <tr><td style="padding:22px 28px;border-bottom:1px solid #EEF1F6">{head}</td></tr>
                <tr><td style="padding:26px 28px 24px;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;font-size:15px;line-height:1.55;color:#14171F">
                  {body}
                </td></tr>
              </table>
              <p style="max-width:580px;margin:14px auto 0;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;font-size:12px;color:#6B7280;text-align:center">
                Diese Nachricht wurde automatisch von {footerLink} verschickt.
              </p>
            </td></tr></table>
            </body></html>
            """;
    }
}
