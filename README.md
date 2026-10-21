# VbForum

Neues Forum-Frontend in C# / Blazor (.NET 10) für eine **bestehende vBulletin-4.2-Datenbank**. Die vB-Tabellen werden
weiterverwendet und vB-kompatibel beschrieben – das alte Board kann (bis zur Umstellung) parallel weiterlaufen.

## Aufbau

| Projekt | Inhalt |
|---|---|
| `src/VbForum.Core` | BBCode-Renderer, vB-Passwortlogik, Bitfelder, Modelle, Text-/Zeichensatz-Helfer – ohne Abhängigkeit zur DB |
| `src/VbForum.Data` | Datenzugriff mit Dapper + MySqlConnector, Rechteprüfung, Schreiblogik inkl. aller vB-Zähler |
| `src/VbForum.Web` | Blazor Web App: Lese-Seiten statisch (SSR), Editor/Admin interaktiv (InteractiveServer) |
| `tests/VbForum.Tests` | xUnit-Tests (BBCode, Passwörter, Zeichensatz, PHP-Serialisierung) |
| `tools/seed_testdb.py` | Füllt eine leere vB-Datenbank mit Testdaten (nur für die lokale Entwicklung) |

## Schnellstart (Entwicklung)

```bash
dotnet build
dotnet test
cd src/VbForum.Web
dotnet run
```

Beim ersten Start ohne Datenbankverbindung leitet das Forum auf `/setup` um. Der dort verlangte **Einrichtungs-Code**
steht im Log („Einrichtungs-Code: …“). Nach dem Speichern meldest du dich mit deinem bestehenden vB-Admin-Konto an
(Gruppe 6 = Administratoren, einstellbar).

## Was in der Datenbank passiert

**Eigene Tabellen** (werden automatisch angelegt, vB-Tabellen bleiben strukturell unverändert):

| Tabelle | Zweck |
|---|---|
| `vbf_setting` | Board-Einstellungen aus dem Admin-Bereich |
| `vbf_userauth` | Moderne Passwort-Hashes (PBKDF2). Wird beim ersten Login aus dem vB-Hash erzeugt |
| `vbf_token` | Einmal-Links (Passwort zurücksetzen, E-Mail bestätigen) – nur Hashes gespeichert |
| `vbf_auditlog` | Protokoll der Admin-/Moderationsaktionen |

Einzige Strukturänderung an vB-Tabellen ist optional: Unter *Admin › Datenbank & Wartung* lassen sich
FULLTEXT-Indizes auf `vb_post.pagetext` und `vb_thread.title` anlegen (für schnelle Suche; bei 655k Beiträgen
einige Minuten, Tabelle währenddessen gesperrt – am besten nachts).

**Geschrieben wird vB-kompatibel:**
- Beiträge, Themen, Zähler (`replycount`, `threadcount`, `lastpost*`, `user.posts` …), Moderationswarteschlange
  (`vb_moderation`), Lösch-Log (`vb_deletionlog`, „sanftes“ Löschen mit `visible = 2`), Edit-Log und Bearbeitungsverlauf,
  Lesemarkierungen (`vb_threadread`/`vb_forumread`), Abos, PNs (`vb_pm`/`vb_pmtext` inkl. serialisiertem `touserarray`),
  Anhänge als Blob in `vb_filedata`/`vb_attachment` (inkl. Thumbnails), Avatare in `vb_customavatar`, Sperren in `vb_userban`.
- **Zeichensätze:** Das Board lief mit ISO-8859-1, mehrere Tabellen sind `latin1`. Zeichen außerhalb von Windows-1252
  (Emojis, Kyrillisch …) werden wie früher als `&#NNN;` abgelegt. Gelesen wird über utf8mb4.
- **Passwörter:** Login prüft erst den modernen Hash, sonst `md5(md5(pw) . salt)` (UTF-8- und latin1-Variante).
  Bei Passwortänderungen wird – solange *„Passwörter zusätzlich im vB-Format speichern“* aktiv ist – auch der
  vB-Hash aktualisiert, damit das alte Board weiter funktioniert. Nach der endgültigen Umstellung abschalten.
- Nach Bearbeitungen wird `vb_postparsed` für den Beitrag geleert, damit vB keinen veralteten Cache zeigt.

**Parallelbetrieb mit vB:** Nach Änderungen an Foren, Gruppen oder Rechten im neuen Admin-Bereich im alten vB einmal
*Wartung › Caches aktualisieren* ausführen (vB hält Foren/Rechte in `vb_datastore` vorgehalten).

## Einrichten auf dem Server

### Linux (systemd + nginx)

```bash
dotnet publish src/VbForum.Web -c Release -o /opt/vbforum
```

`/etc/systemd/system/vbforum.service`:

```ini
[Unit]
Description=VbForum
After=network.target mariadb.service

[Service]
WorkingDirectory=/opt/vbforum
ExecStart=/usr/bin/dotnet /opt/vbforum/VbForum.Web.dll --urls http://127.0.0.1:5080
Restart=always
User=vbforum
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=VbForum__DataPath=/var/lib/vbforum

[Install]
WantedBy=multi-user.target
```

nginx (wichtig: WebSocket-Header für die interaktiven Seiten):

```nginx
server {
    server_name forum.example.de;
    client_max_body_size 64m;
    location / {
        proxy_pass         http://127.0.0.1:5080;
        proxy_http_version 1.1;
        proxy_set_header   Upgrade $http_upgrade;
        proxy_set_header   Connection "upgrade";
        proxy_set_header   Host $host;
        proxy_set_header   X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header   X-Forwarded-Proto $scheme;
        proxy_read_timeout 300s;
    }
}
```

### Windows (IIS)

ASP.NET Core Hosting Bundle (.NET 10) installieren, `dotnet publish -c Release`, Site auf den Publish-Ordner zeigen
lassen, **WebSocket-Protokoll** als Windows-Feature aktivieren. Der App-Pool-Benutzer braucht Schreibrechte auf
`App_Data` (oder `VbForum:DataPath` setzen).

### `App_Data` (bzw. `VbForum:DataPath`)

Enthält DB-Zugang (`dbsettings.json`), Schlüssel für Anmelde-Cookies (`keys/`), Signatur-Schlüssel für den Bild-Proxy,
das hochgeladene Logo und den Bild-Cache. **Mitsichern, nie ins Git** – ohne `keys/` werden nach einem Neustart alle
Benutzer abgemeldet.

## Nach der Inbetriebnahme

1. **Smilies:** Die Bilddateien aus der alten Installation (`images/smilies/…`) nach `wwwroot/images/smilies/` kopieren.
   Fehlende Dateien werden als Text angezeigt.
2. **SMTP** unter *Admin › Einstellungen* eintragen und Testmail senden (für Registrierung, Passwort-Reset, Abos).
3. **Basis-URL** prüfen (für Links in Mails).
4. **Volltextindex** anlegen (*Admin › Datenbank & Wartung*).
5. Alte URLs (`showthread.php`, `forumdisplay.php`, `member.php`, `attachment.php` …) werden automatisch per 301
   weitergeleitet – die Domain kann also einfach umgestellt werden.

## Bewusst (noch) nicht enthalten

Umfragen, Reputation/Verwarnungen, Kalender, Blogs, Social Groups, Präfixe, Thread-Bewertungen, Benachrichtigungen
für Forum-Abos, Ignorierliste. Die vB-Daten dazu bleiben unangetastet und können bei Bedarf ergänzt werden.

## Rechte – wie sie berechnet werden

Wie in vB: Für jede Gruppe des Benutzers (Haupt- + Zusatzgruppen) wird vom Forum zur Wurzel der erste Eintrag in
`vb_forumpermission` gesucht, sonst gilt der Gruppenstandard; mehrere Gruppen werden ODER-verknüpft. Die Bitwerte
werden aus `vb_datastore` (`bitfields`) gelesen, mit den vB-4-Standardwerten als Rückfall. Administratoren
(einstellbar, Standard Gruppe 6) dürfen alles; Moderatoren aus `vb_moderator` gelten für ihr Forum und alle Unterforen.
