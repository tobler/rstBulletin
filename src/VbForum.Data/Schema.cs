using Dapper;

namespace VbForum.Data;

/// <summary>Legt die eigenen Tabellen (vbf_*) an. vB-Tabellen werden nicht verändert.</summary>
public sealed class Schema(Db db)
{
    public async Task EnsureOwnTablesAsync()
    {
        await using var c = await db.OpenAsync();
        await c.ExecuteAsync($"""
            CREATE TABLE IF NOT EXISTS {Tables.OwnSetting} (
              `name` VARCHAR(100) NOT NULL PRIMARY KEY,
              `value` MEDIUMTEXT NULL
            ) DEFAULT CHARSET=utf8mb4;

            CREATE TABLE IF NOT EXISTS {Tables.OwnUserAuth} (
              `userid` INT UNSIGNED NOT NULL PRIMARY KEY,
              `hash` VARCHAR(255) NOT NULL,
              `updated` INT UNSIGNED NOT NULL DEFAULT 0
            ) DEFAULT CHARSET=ascii;

            CREATE TABLE IF NOT EXISTS {Tables.OwnToken} (
              `token` CHAR(64) NOT NULL PRIMARY KEY,
              `userid` INT UNSIGNED NOT NULL,
              `type` VARCHAR(20) NOT NULL,
              `data` VARCHAR(255) NOT NULL DEFAULT '',
              `expires` INT UNSIGNED NOT NULL,
              KEY `userid` (`userid`, `type`)
            ) DEFAULT CHARSET=utf8mb4;

            CREATE TABLE IF NOT EXISTS {Tables.OwnAsset} (
              `name` VARCHAR(40) NOT NULL PRIMARY KEY,
              `contenttype` VARCHAR(60) NOT NULL,
              `data` MEDIUMBLOB NOT NULL,
              `updated` INT UNSIGNED NOT NULL
            ) DEFAULT CHARSET=utf8mb4;

            CREATE TABLE IF NOT EXISTS {Tables.OwnUserPref} (
              `userid` INT UNSIGNED NOT NULL,
              `name` VARCHAR(40) NOT NULL,
              `value` VARCHAR(255) NOT NULL DEFAULT '',
              PRIMARY KEY (`userid`, `name`)
            ) DEFAULT CHARSET=utf8mb4;

            CREATE TABLE IF NOT EXISTS {Tables.OwnAuditLog} (
              `id` INT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
              `dateline` INT UNSIGNED NOT NULL,
              `userid` INT UNSIGNED NOT NULL,
              `username` VARCHAR(100) NOT NULL,
              `action` VARCHAR(60) NOT NULL,
              `target` VARCHAR(255) NOT NULL DEFAULT '',
              `ip` VARCHAR(45) NOT NULL DEFAULT '',
              KEY `dateline` (`dateline`)
            ) DEFAULT CHARSET=utf8mb4;
            """);
    }

    public sealed record TableInfo(string Name, string Engine, string Collation, long Rows, long DataBytes, long IndexBytes);

    public async Task<List<TableInfo>> TablesAsync()
    {
        await using var c = await db.OpenAsync();
        var t = db.T;
        var names = new[] { "post", "thread", "forum", "user", "usergroup", "attachment", "filedata", "pm", "pmtext", "subscribethread", "threadread", "forumread", "customavatar" }
            .Select(n => t.Prefix + n).ToArray();
        var rows = await c.QueryAsync<(string, string, string, long, long, long)>(
            """
            SELECT TABLE_NAME, IFNULL(ENGINE,''), IFNULL(TABLE_COLLATION,''), CAST(IFNULL(TABLE_ROWS,0) AS SIGNED), CAST(IFNULL(DATA_LENGTH,0) AS SIGNED), CAST(IFNULL(INDEX_LENGTH,0) AS SIGNED)
            FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME IN @names ORDER BY TABLE_NAME
            """, new { names });
        return rows.Select(r => new TableInfo(r.Item1, r.Item2, r.Item3, r.Item4, r.Item5, r.Item6)).ToList();
    }

    public async Task<bool> HasPostFulltextAsync()
    {
        await using var c = await db.OpenAsync();
        var n = await c.ExecuteScalarAsync<long>(
            """
            SELECT COUNT(*) FROM information_schema.STATISTICS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @t AND INDEX_TYPE = 'FULLTEXT' AND COLUMN_NAME = 'pagetext'
            """, new { t = db.T.Prefix + "post" });
        return n > 0;
    }

    public async Task<bool> HasThreadFulltextAsync()
    {
        await using var c = await db.OpenAsync();
        var n = await c.ExecuteScalarAsync<long>(
            """
            SELECT COUNT(*) FROM information_schema.STATISTICS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @t AND INDEX_TYPE = 'FULLTEXT' AND COLUMN_NAME = 'title'
            """, new { t = db.T.Prefix + "thread" });
        return n > 0;
    }

    /// <summary>Legt Volltextindizes an (vB lässt sie parallel weiterlaufen). Bei 655k Beiträgen dauert das einige Minuten.</summary>
    public async Task CreateFulltextAsync()
    {
        await using var c = await db.OpenAsync();
        if (!await HasPostFulltextAsync())
            await c.ExecuteAsync($"ALTER TABLE {db.T.Post} ADD FULLTEXT INDEX `vbf_ft_pagetext` (`pagetext`)", commandTimeout: 3600);
        if (!await HasThreadFulltextAsync())
            await c.ExecuteAsync($"ALTER TABLE {db.T.Thread} ADD FULLTEXT INDEX `vbf_ft_title` (`title`)", commandTimeout: 3600);
    }
}
