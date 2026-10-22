# rstBulletin

A modern, mobile-friendly forum written in C# / Blazor (.NET 10). It can either take over an **existing
vBulletin 4.2 database** or start as a **fresh installation**.

When it takes over a vBulletin database, it keeps using and writing the vB tables in a vB-compatible way. The old
board can keep running in parallel until you switch over.

> vBulletin is a trademark of its respective owner. This project is not affiliated with or endorsed by it and contains
> no vBulletin code. It only reads and writes a compatible database layout.

## Features

- Forums with sub-forums, threads and posts. Posts are written in BBCode, with a toolbar, preview, smilies and drafts.
- Image attachments are stored as blobs in the database, with thumbnails and a lightbox.
- vB permission model:
  - Group permissions are inherited along the forum tree, and secondary groups (`membergroupids`) count.
  - Forums can be hidden, shown locked, or shown locked with thread titles only.
  - Moderators can be assigned per forum.
- Login with existing vB passwords. On first login a modern PBKDF2 hash is created.
- Search (FULLTEXT when available), sorting, "new posts", subscriptions with e-mail notification.
- User area: profile, avatar, password, e-mail address, private messages with a pop-up for new messages.
- Admin area:
  - Board settings, logo and SMTP.
  - Database maintenance.
  - Forums and permissions, plus a "check permissions" tool.
  - Users and groups.
  - Threads and the moderation queue.
  - Attachments and the audit log.
- Old vB URLs (`showthread.php`, `forumdisplay.php`, `member.php`, `attachment.php` …) redirect with 301.

The user interface is currently in German.

## Projects

| Project | Contents |
|---|---|
| `src/VbForum.Core` | BBCode renderer, vB password logic, bitfields, models, text and charset helpers. No database dependency |
| `src/VbForum.Data` | Data access (Dapper + MySqlConnector), permissions, write logic including all vB counters, installer |
| `src/VbForum.Web` | Blazor Web App. Read pages are static SSR; editor, setup and admin pages use InteractiveServer |
| `tests/VbForum.Tests` | xUnit tests: BBCode, passwords, charset, PHP serialization |
| `tools/seed_testdb.py` | Fills an empty schema with test data, for local development only |

## Quick start (development)

```bash
dotnet build
dotnet test
cd src/VbForum.Web
dotnet run
```

Requirements: .NET 10 SDK and MySQL 5.7+ or MariaDB 10.3+.

## Database connection

The connection can be provided via **environment variables**:

| Variable | Meaning | Default |
|---|---|---|
| `DB_HOST` | Server | `localhost` |
| `DB_PORT` | Port | `3306` |
| `DB_USER` | User | – |
| `DB_PASSWORD` | Password | – |
| `DB_DATABASE` | Database name | – |
| `DB_TABLE_PREFIX` | Table prefix (optional) | `vb_` |
| `DB_SSL` | `true` to require TLS (optional) | `false` |

If any of `DB_HOST`, `DB_USER` or `DB_DATABASE` is set, the connection comes from the environment. It is then shown
read-only in the admin area.

Otherwise, you enter the connection in the browser during setup. It is then stored in `App_Data/dbsettings.json`,
which only the service user can read.

## First-time setup

On first start, every request is redirected to `/setup`:

1. **Database:** enter the connection details. This step is skipped when the environment variables are set.
2. **Install or adopt.** What happens here depends on what the setup finds in the database:
   - **The database already contains a vBulletin forum** (with the given prefix). Only the own tables `vbf_*` are
     created; the vB tables are not touched. Afterwards you log in with your existing vB administrator account.
   - **The database is empty.** All required tables are created with a vB-4-compatible layout (InnoDB, utf8mb4).
     The setup also creates the default user groups, a first category with one forum, and an administrator account
     with the credentials you enter.

Once setup has finished, `/setup` returns 404 and cannot be used again. If the database is unreachable later, the board
shows a maintenance page. The setup does not reopen in that case.

> Until setup is completed, anyone who can reach the site can run it. Complete it right after deploying, or keep the
> site behind a firewall until then.

## What happens in the database

**Own tables.** These are always created. vB tables are not structurally changed.

| Table | Purpose |
|---|---|
| `vbf_setting` | Board settings from the admin area |
| `vbf_userauth` | Modern password hashes (PBKDF2), created from the vB hash on first login |
| `vbf_token` | One-time links (password reset, e-mail confirmation). Only hashes are stored |
| `vbf_auditlog` | Log of admin and moderation actions |
| `vbf_asset` | Logo and favicon, so they survive a redeploy without any volume |

The only optional structural change to vB tables: under *Admin › Datenbank & Wartung* you can add FULLTEXT indexes on
`vb_post.pagetext` and `vb_thread.title` for fast search. With several hundred thousand posts this takes a few minutes
and locks the table, so it is best done at night.

**Writes are vB-compatible:**

- Posts, threads and counters (`replycount`, `threadcount`, `lastpost*`, `user.posts` …).
- Moderation queue (`vb_moderation`), deletion log (`vb_deletionlog`; soft delete uses `visible = 2`), edit log and edit
  history.
- Read markers (`vb_threadread`/`vb_forumread`) and subscriptions.
- Private messages (`vb_pm`/`vb_pmtext`, including the serialized `touserarray`).
- Attachments as blobs in `vb_filedata`/`vb_attachment`, avatars in `vb_customavatar`, bans in `vb_userban`.
- **Charsets.** Old vB boards often have `latin1` tables. Characters outside Windows-1252 (emoji, Cyrillic …) are
  stored as `&#NNN;`, as vB does. Reading uses utf8mb4.
- **Passwords.** Login first checks the modern hash, then falls back to `md5(md5(pw) . salt)` (UTF-8 and latin1
  variants). While *"also store passwords in vB format"* is enabled, password changes also update the vB hash, so the
  old board keeps working. Disable it after the final switch-over.
- After an edit, `vb_postparsed` is cleared for that post so vB does not show a stale cache.

**Running in parallel with vB.** After changing forums, groups or permissions in the new admin area, run
*Maintenance › Update Counters / Rebuild Caches* once in the old vB, because vB caches forums and permissions in
`vb_datastore`.

## Permissions

Permissions are computed as in vB:

- For each of the user's groups (primary and secondary), the first entry in `vb_forumpermission` is looked up, going
  from the forum up to the root. If there is none, the group default applies.
- Multiple groups are OR-ed.
- Bit values are read from `vb_datastore` (`bitfields`), with the vB 4 defaults as fallback.
- Administrators (configurable, default group 6) may do everything.
- Moderators from `vb_moderator` apply to their forum and all sub-forums.

Forums a user cannot see are hidden, shown with a lock, or shown with a lock and thread titles. This is set per forum
under "Für Gruppen ohne Zugriff". *Admin › Rechte prüfen* shows exactly what a guest or a given user may do in each
forum, and where that permission comes from.

## Deployment

### Linux (systemd + nginx)

```bash
dotnet publish src/VbForum.Web -c Release -o /opt/rstbulletin
```

`/etc/systemd/system/rstbulletin.service`:

```ini
[Unit]
Description=rstBulletin
After=network.target mariadb.service

[Service]
WorkingDirectory=/opt/rstbulletin
ExecStart=/usr/bin/dotnet /opt/rstbulletin/VbForum.Web.dll --urls http://127.0.0.1:5080
Restart=always
User=rstbulletin
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=VbForum__DataPath=/var/lib/rstbulletin
Environment=DB_HOST=localhost
Environment=DB_USER=forum
Environment=DB_DATABASE=forum
# Put the password in a root-only file instead of the unit:
EnvironmentFile=/etc/rstbulletin/db.env

[Install]
WantedBy=multi-user.target
```

nginx. The WebSocket headers are required for the interactive pages:

```nginx
server {
    server_name forum.example.com;
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

### Docker

```bash
docker build -t rstbulletin .
# from an Apple Silicon Mac for an x64 server:
docker build --platform linux/amd64 -t rstbulletin .

docker run -d --name rstbulletin -p 8080:8080 \
  -e DB_HOST=db.example.local -e DB_USER=forum -e DB_PASSWORD=secret -e DB_DATABASE=forum \
  -v rstbulletin-data:/data \
  rstbulletin
```

- The container listens on port **8080** and runs as a non-root user.
- `/data` holds the cookie keys and the image cache (logo and favicon are stored in the database). **Keep it on a volume**, otherwise users are logged out on every
  redeploy.
- Smilie images from an old vB installation can be mounted read-only to `/app/wwwroot/images/smilies`.
- Health check: `GET /healthz` returns 200, or 503 when the database is unreachable. The image has a built-in
  `HEALTHCHECK`.
- Put it behind a reverse proxy that passes WebSocket upgrades and `X-Forwarded-For`/`X-Forwarded-Proto` (see the nginx
  example above).
- `docker-compose.example.yml` shows the forum together with a MariaDB container.

### Windows (IIS)

1. Install the ASP.NET Core Hosting Bundle (.NET 10).
2. Run `dotnet publish -c Release` and point the IIS site at the publish folder.
3. Enable the **WebSocket Protocol** Windows feature.
4. Give the app pool user write access to `App_Data`, or set `VbForum:DataPath` to another folder.
5. Set the environment variables in `web.config` or on the app pool.

### Data directory (`App_Data` or `VbForum:DataPath`)

This directory contains:

- the DB connection (`dbsettings.json`, unless it comes from environment variables),
- the cookie encryption keys (`keys/`),
- the signing key for the image proxy,
- the image cache.

**Back it up and never commit it.** Without `keys/`, all users are logged out after a restart.

## After going live

1. **Smilies.** When adopting a vB board, copy the image files from the old installation (`images/smilies/…`) to
   `wwwroot/images/smilies/`. Missing files are shown as text.
2. **SMTP.** Configure it under *Admin › Einstellungen* and send a test mail. It is needed for registration, password
   reset and subscriptions.
3. **Base URL.** Check it, because it is used for links in e-mails.
4. **Full-text index.** Create it under *Admin › Datenbank & Wartung*.

## Not (yet) included

Polls, reputation and infractions, calendar, blogs, social groups, thread prefixes, thread ratings, forum
subscriptions, ignore list. The vB data for these is left untouched and can be supported later.

## License

GNU General Public License v3.0. See [LICENSE](LICENSE).
