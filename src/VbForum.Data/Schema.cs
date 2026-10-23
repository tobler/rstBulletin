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

            CREATE TABLE IF NOT EXISTS {Tables.OwnPlan} (
              `planid` INT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
              `title` VARCHAR(100) NOT NULL,
              `description` VARCHAR(500) NOT NULL DEFAULT '',
              `price_cents` INT UNSIGNED NOT NULL DEFAULT 0,
              `currency` CHAR(3) NOT NULL DEFAULT 'EUR',
              `months` SMALLINT UNSIGNED NOT NULL DEFAULT 1,
              `groupid` SMALLINT UNSIGNED NOT NULL,
              `primarygroup` TINYINT NOT NULL DEFAULT 0,
              `payurl` VARCHAR(1000) NOT NULL DEFAULT '',
              `active` TINYINT NOT NULL DEFAULT 1,
              `displayorder` SMALLINT NOT NULL DEFAULT 0,
              `highlight` TINYINT NOT NULL DEFAULT 0,
              `paymode` VARCHAR(10) NOT NULL DEFAULT 'rstpay',
              `rst_days` INT NOT NULL DEFAULT 30,
              `rst_paymenttype` INT NOT NULL DEFAULT 1,
              `rst_rebilling` TINYINT NOT NULL DEFAULT 0,
              `rst_rebillingdays` INT NOT NULL DEFAULT 0
            ) DEFAULT CHARSET=utf8mb4;

            CREATE TABLE IF NOT EXISTS {Tables.OwnPayment} (
              `paymentid` INT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
              `dateline` INT UNSIGNED NOT NULL,
              `action` VARCHAR(30) NOT NULL,
              `transaction_id` VARCHAR(100) NOT NULL DEFAULT '',
              `userid` INT UNSIGNED NOT NULL DEFAULT 0,
              `param1` VARCHAR(255) NOT NULL DEFAULT '',
              `amount` VARCHAR(20) NOT NULL DEFAULT '',
              `payer` VARCHAR(200) NOT NULL DEFAULT '',
              `country` VARCHAR(10) NOT NULL DEFAULT '',
              `ip` VARCHAR(45) NOT NULL DEFAULT '',
              `result` VARCHAR(255) NOT NULL DEFAULT '',
              KEY `dateline` (`dateline`),
              KEY `userid` (`userid`),
              KEY `transaction_id` (`transaction_id`)
            ) DEFAULT CHARSET=utf8mb4;

            CREATE TABLE IF NOT EXISTS {Tables.OwnSubscription} (
              `subscriptionid` INT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
              `userid` INT UNSIGNED NOT NULL,
              `planid` INT UNSIGNED NOT NULL DEFAULT 0,
              `groupid` SMALLINT UNSIGNED NOT NULL,
              `startdate` INT UNSIGNED NOT NULL,
              `enddate` INT UNSIGNED NOT NULL DEFAULT 0,
              `status` VARCHAR(10) NOT NULL DEFAULT 'active',
              `reference` VARCHAR(100) NOT NULL DEFAULT '',
              `source` VARCHAR(20) NOT NULL DEFAULT 'api',
              `created` INT UNSIGNED NOT NULL,
              `updated` INT UNSIGNED NOT NULL,
              KEY `userid` (`userid`),
              KEY `status_end` (`status`, `enddate`),
              KEY `reference` (`reference`)
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
        await MigrateAsync();
    }

    /// <summary>Spalten nachziehen, die erst in späteren Versionen dazukamen.</summary>
    public async Task MigrateAsync()
    {
        await using var c = await db.OpenAsync();
        async Task Col(string table, string column, string ddl)
        {
            var exists = await c.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @t AND COLUMN_NAME = @column",
                new { t = table.Trim('`'), column });
            if (exists == 0) await c.ExecuteAsync($"ALTER TABLE {table} ADD COLUMN {ddl}");
        }
        await Col(Tables.OwnPlan, "paymode", "`paymode` VARCHAR(10) NOT NULL DEFAULT 'rstpay'");
        await Col(Tables.OwnPlan, "rst_days", "`rst_days` INT NOT NULL DEFAULT 30");
        await Col(Tables.OwnPlan, "rst_paymenttype", "`rst_paymenttype` INT NOT NULL DEFAULT 1");
        await Col(Tables.OwnPlan, "rst_rebilling", "`rst_rebilling` TINYINT NOT NULL DEFAULT 0");
        await Col(Tables.OwnPlan, "rst_rebillingdays", "`rst_rebillingdays` INT NOT NULL DEFAULT 0");

        // vB-Tabellen für Umfragen und Reputation (in übernommenen vB-Datenbanken vorhanden, bei Neuinstallationen anlegen)
        var t = db.T;
        await c.ExecuteAsync($"""
            CREATE TABLE IF NOT EXISTS {t.Poll} (
              `pollid` int(10) unsigned NOT NULL AUTO_INCREMENT,
              `question` varchar(100) NOT NULL DEFAULT '',
              `dateline` int(10) unsigned NOT NULL DEFAULT 0,
              `options` text DEFAULT NULL,
              `votes` text DEFAULT NULL,
              `active` smallint(6) NOT NULL DEFAULT 1,
              `numberoptions` smallint(5) unsigned NOT NULL DEFAULT 0,
              `timeout` smallint(5) unsigned NOT NULL DEFAULT 0,
              `multiple` smallint(5) unsigned NOT NULL DEFAULT 0,
              `voters` smallint(5) unsigned NOT NULL DEFAULT 0,
              `public` smallint(6) NOT NULL DEFAULT 0,
              `lastvote` int(10) unsigned NOT NULL DEFAULT 0,
              PRIMARY KEY (`pollid`)
            ) DEFAULT CHARSET=utf8mb4;

            CREATE TABLE IF NOT EXISTS {t.PollVote} (
              `pollvoteid` int(10) unsigned NOT NULL AUTO_INCREMENT,
              `pollid` int(10) unsigned NOT NULL DEFAULT 0,
              `userid` int(10) unsigned DEFAULT NULL,
              `votedate` int(10) unsigned NOT NULL DEFAULT 0,
              `voteoption` int(10) unsigned NOT NULL DEFAULT 0,
              `votetype` int(10) unsigned NOT NULL DEFAULT 0,
              PRIMARY KEY (`pollvoteid`),
              UNIQUE KEY `pollid` (`pollid`,`userid`,`votetype`),
              KEY `userid` (`userid`)
            ) DEFAULT CHARSET=utf8mb4;

            CREATE TABLE IF NOT EXISTS {t.Reputation} (
              `reputationid` int(10) unsigned NOT NULL AUTO_INCREMENT,
              `postid` int(10) unsigned NOT NULL DEFAULT 1,
              `userid` int(10) unsigned NOT NULL DEFAULT 1,
              `reputation` int(11) NOT NULL DEFAULT 0,
              `whoadded` int(10) unsigned NOT NULL DEFAULT 0,
              `reason` varchar(250) DEFAULT '',
              `dateline` int(10) unsigned NOT NULL DEFAULT 0,
              PRIMARY KEY (`reputationid`),
              UNIQUE KEY `whoadded_postid` (`whoadded`,`postid`),
              KEY `userid` (`userid`),
              KEY `multi` (`postid`,`userid`),
              KEY `dateline` (`dateline`)
            ) DEFAULT CHARSET=utf8mb4;

            CREATE TABLE IF NOT EXISTS {t.ReputationLevel} (
              `reputationlevelid` int(10) unsigned NOT NULL AUTO_INCREMENT,
              `minimumreputation` int(11) NOT NULL DEFAULT 0,
              PRIMARY KEY (`reputationlevelid`),
              KEY `reputationlevel` (`minimumreputation`)
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
