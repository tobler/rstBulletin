using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Logging;

namespace VbForum.Data;

public sealed class MailService(BoardCache cache, ILogger<MailService> log)
{
    public async Task<bool> IsConfiguredAsync() => (await cache.GetAsync()).Settings.SmtpConfigured;

    /// <summary>Sendet eine Textmail. Gibt false zurück, wenn SMTP nicht eingerichtet ist oder der Versand scheitert.</summary>
    public async Task<bool> SendAsync(string to, string subject, string body)
    {
        var s = (await cache.GetAsync()).Settings;
        if (!s.SmtpConfigured || string.IsNullOrWhiteSpace(to)) return false;
        try
        {
            using var client = new SmtpClient(s.SmtpHost, s.SmtpPort)
            {
                EnableSsl = s.SmtpSsl,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                Timeout = 20000,
            };
            if (!string.IsNullOrWhiteSpace(s.SmtpUser))
                client.Credentials = new NetworkCredential(s.SmtpUser, s.SmtpPassword);
            using var msg = new MailMessage(new MailAddress(s.MailFrom, s.BoardName), new MailAddress(to))
            {
                Subject = subject,
                Body = body + "\n\n-- \n" + s.BoardName + (string.IsNullOrEmpty(s.BaseUrl) ? "" : "\n" + s.BaseUrl),
                BodyEncoding = System.Text.Encoding.UTF8,
                SubjectEncoding = System.Text.Encoding.UTF8,
            };
            await client.SendMailAsync(msg);
            return true;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Mailversand an {To} fehlgeschlagen", to);
            return false;
        }
    }

    public async Task<string> AbsoluteUrlAsync(string path)
    {
        var s = (await cache.GetAsync()).Settings;
        return string.IsNullOrEmpty(s.BaseUrl) ? path : s.BaseUrl.TrimEnd('/') + path;
    }
}
