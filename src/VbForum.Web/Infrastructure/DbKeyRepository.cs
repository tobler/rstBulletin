using System.Xml.Linq;
using Dapper;
using Microsoft.AspNetCore.DataProtection.Repositories;
using VbForum.Data;

namespace VbForum.Web.Infrastructure;

/// <summary>
/// Schlüssel für Anmelde-Cookies und Formulare in der Datenbank (vbf_dpkey) statt im Dateisystem –
/// so bleiben alle Benutzer auch nach einem Redeploy ohne Volume angemeldet.
/// Solange die Datenbank noch nicht eingerichtet ist, wird ins Datenverzeichnis geschrieben; diese Schlüssel
/// wandern später automatisch mit in die Datenbank.
/// </summary>
public sealed class DbKeyRepository(Db db, string fallbackDir, ILogger log) : IXmlRepository
{
    private readonly FileSystemXmlRepository _files = new(new DirectoryInfo(fallbackDir), Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        var fileKeys = _files.GetAllElements();
        if (!TryDb(c =>
            {
                // Schlüssel aus der Datei-Phase (vor der Einrichtung) übernehmen
                foreach (var k in fileKeys)
                    c.Execute("INSERT IGNORE INTO `vbf_dpkey` (name, xml) VALUES (@name, @xml)", new { name = Name(k), xml = k.ToString(SaveOptions.DisableFormatting) });
            })) return fileKeys;
        var rows = new List<XElement>();
        TryDb(c => rows.AddRange(c.Query<string>("SELECT xml FROM `vbf_dpkey`").Select(XElement.Parse)));
        return rows;
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        if (!TryDb(c => c.Execute("INSERT IGNORE INTO `vbf_dpkey` (name, xml) VALUES (@name, @xml)",
                new { name = Name(element, friendlyName), xml = element.ToString(SaveOptions.DisableFormatting) })))
            _files.StoreElement(element, friendlyName);
    }

    private static string Name(XElement e, string? fallback = null) =>
        (string?)e.Attribute("id") ?? fallback ?? Guid.NewGuid().ToString();

    private bool TryDb(Action<MySqlConnector.MySqlConnection> action)
    {
        if (!db.IsConfigured) return false;
        try
        {
            using var c = db.OpenAsync().GetAwaiter().GetResult();
            c.Execute("CREATE TABLE IF NOT EXISTS `vbf_dpkey` (`name` VARCHAR(100) NOT NULL PRIMARY KEY, `xml` TEXT NOT NULL) DEFAULT CHARSET=utf8mb4");
            action(c);
            return true;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Schlüssel konnten nicht in der Datenbank gelesen/gespeichert werden – verwende das Datenverzeichnis.");
            return false;
        }
    }
}
