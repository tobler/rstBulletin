using Dapper;
using VbForum.Core.Models;
using VbForum.Core.Text;

namespace VbForum.Data;

/// <summary>
/// Umfragen im vB-4-Format: vb_poll (Optionen und Stimmenzahlen als „a|||b|||c“), vb_pollvote (je Stimme eine Zeile;
/// bei Mehrfachauswahl votetype = Option, sonst 0), vb_thread.pollid verweist auf die Umfrage.
/// </summary>
public sealed class PollService(Db db)
{
    private Tables T => db.T;
    public const string Sep = "|||";
    public const int MaxOptions = 20;
    public const int MaxQuestion = 100;
    public const int MaxOption = 250;

    public sealed class Poll
    {
        public long PollId { get; set; }
        public string Question { get; set; } = "";
        public long Dateline { get; set; }
        public string? Options { get; set; }
        public string? Votes { get; set; }
        public int Active { get; set; }
        public int Timeout { get; set; }
        public int Multiple { get; set; }
        public int Voters { get; set; }
        public int Public { get; set; }
        public long LastVote { get; set; }

        public string DisplayQuestion => HtmlUtil.CleanTitle(Question);
        public List<string> OptionList => Split(Options).Select(HtmlUtil.CleanTitle).ToList();
        public List<int> VoteList
        {
            get
            {
                var v = Split(Votes).Select(x => int.TryParse(x.Trim(), out var n) ? n : 0).ToList();
                while (v.Count < OptionList.Count) v.Add(0);
                return v;
            }
        }
        public bool IsMultiple => Multiple != 0;
        public bool IsPublic => Public != 0;
        /// <summary>Ende (Unix) oder 0 = unbegrenzt.</summary>
        public long EndsAt => Timeout > 0 ? Dateline + Timeout * 86400L : 0;
        public bool IsClosed(long now) => Active == 0 || (EndsAt > 0 && EndsAt < now);

        private static string[] Split(string? s) => string.IsNullOrEmpty(s) ? Array.Empty<string>() : s.Split(Sep);
    }

    /// <summary>Eingabe für Anlegen/Bearbeiten.</summary>
    public sealed class Draft
    {
        public string Question { get; set; } = "";
        public List<string> Options { get; set; } = new() { "", "" };
        public bool Multiple { get; set; }
        public bool Public { get; set; }
        /// <summary>Laufzeit in Tagen, 0 = unbegrenzt.</summary>
        public int TimeoutDays { get; set; }
        public bool Active { get; set; } = true;

        public static Draft From(Poll p) => new()
        {
            Question = p.DisplayQuestion, Options = p.OptionList, Multiple = p.IsMultiple, Public = p.IsPublic, TimeoutDays = p.Timeout, Active = p.Active != 0,
        };

        public List<string> CleanOptions => Options.Select(o => o.Replace(Sep, " ").Trim()).Where(o => o.Length > 0).ToList();

        public string? Validate(int existingOptions = 0)
        {
            if (string.IsNullOrWhiteSpace(Question)) return "Bitte eine Frage für die Umfrage eingeben.";
            if (Question.Trim().Length > MaxQuestion) return $"Die Frage darf höchstens {MaxQuestion} Zeichen lang sein.";
            var o = CleanOptions;
            if (o.Count < 2) return "Eine Umfrage braucht mindestens zwei Antwortmöglichkeiten.";
            if (o.Count > MaxOptions) return $"Höchstens {MaxOptions} Antwortmöglichkeiten.";
            if (o.Count < existingOptions) return "Bestehende Antwortmöglichkeiten können nicht entfernt werden (die Stimmen würden sonst verrutschen).";
            if (o.Any(x => x.Length > MaxOption)) return $"Eine Antwort darf höchstens {MaxOption} Zeichen lang sein.";
            if (TimeoutDays is < 0 or > 3650) return "Ungültige Laufzeit.";
            return null;
        }
    }

    private const string Cols = """
        CAST(pollid AS SIGNED) AS PollId, question, CAST(dateline AS SIGNED) AS Dateline, options, votes, active,
        CAST(timeout AS SIGNED) AS Timeout, CAST(multiple AS SIGNED) AS Multiple, CAST(voters AS SIGNED) AS Voters, public, CAST(lastvote AS SIGNED) AS LastVote
        """;

    public async Task<Poll?> GetAsync(long pollId)
    {
        if (pollId <= 0) return null;
        await using var c = await db.OpenAsync();
        return await c.QueryFirstOrDefaultAsync<Poll>($"SELECT {Cols} FROM {T.Poll} WHERE pollid = @pollId", new { pollId });
    }

    /// <summary>Gewählte Optionen (1-basiert) des Benutzers.</summary>
    public async Task<HashSet<int>> MyVotesAsync(long pollId, long userId)
    {
        if (userId <= 0) return new();
        await using var c = await db.OpenAsync();
        return (await c.QueryAsync<int>($"SELECT CAST(voteoption AS SIGNED) FROM {T.PollVote} WHERE pollid = @pollId AND userid = @userId", new { pollId, userId })).ToHashSet();
    }

    /// <summary>Wer hat was gewählt (nur bei öffentlichen Umfragen): Option (1-basiert) → Namen.</summary>
    public async Task<Dictionary<int, List<(long UserId, string Name)>>> VotersAsync(long pollId)
    {
        await using var c = await db.OpenAsync();
        var rows = await c.QueryAsync<(int Opt, long UserId, string Name)>($"""
            SELECT CAST(v.voteoption AS SIGNED), CAST(v.userid AS SIGNED), u.username
            FROM {T.PollVote} v JOIN {T.User} u ON u.userid = v.userid
            WHERE v.pollid = @pollId ORDER BY v.votedate
            """, new { pollId });
        return rows.GroupBy(r => r.Opt).ToDictionary(g => g.Key, g => g.Select(r => (r.UserId, HtmlUtil.CleanTitle(r.Name))).ToList());
    }

    private static string Store(string s) => VbText.ForStorage(HtmlUtil.ToVbStoredTitle(s.Trim()));

    /// <summary>Umfrage an ein Thema hängen (Thema darf noch keine haben).</summary>
    public async Task<(long PollId, string? Error)> CreateAsync(long threadId, Draft d)
    {
        if (d.Validate() is { } err) return (0, err);
        var o = d.CleanOptions;
        await using var c = await db.OpenAsync();
        var existing = await c.ExecuteScalarAsync<long>($"SELECT CAST(pollid AS SIGNED) FROM {T.Thread} WHERE threadid = @threadId", new { threadId });
        if (existing > 0) return (0, "Dieses Thema hat bereits eine Umfrage.");
        var pollId = await c.ExecuteScalarAsync<long>($"""
            INSERT INTO {T.Poll} (question, dateline, options, votes, active, numberoptions, timeout, multiple, voters, public, lastvote)
            VALUES (@q, @now, @opts, @votes, 1, @n, @timeout, @multi, 0, @pub, 0);
            SELECT LAST_INSERT_ID();
            """, new
        {
            q = Store(d.Question), now = ForumTime.Now, opts = string.Join(Sep, o.Select(Store)), votes = string.Join(Sep, o.Select(_ => "0")),
            n = o.Count, timeout = d.TimeoutDays, multi = d.Multiple ? 1 : 0, pub = d.Public ? 1 : 0,
        });
        await c.ExecuteAsync($"UPDATE {T.Thread} SET pollid = @pollId WHERE threadid = @threadId AND open <> 10", new { pollId, threadId });
        return (pollId, null);
    }

    /// <summary>Frage/Antworten ändern. Antworten können ergänzt, aber nicht entfernt werden.</summary>
    public async Task<string?> UpdateAsync(Poll p, Draft d)
    {
        var oldCount = p.OptionList.Count;
        if (d.Validate(oldCount) is { } err) return err;
        var o = d.CleanOptions;
        var votes = p.VoteList.Take(oldCount).ToList();
        while (votes.Count < o.Count) votes.Add(0);
        await using var c = await db.OpenAsync();
        await c.ExecuteAsync($"""
            UPDATE {T.Poll} SET question = @q, options = @opts, votes = @votes, numberoptions = @n, timeout = @timeout,
                multiple = @multi, public = @pub, active = @active
            WHERE pollid = @id
            """, new
        {
            id = p.PollId, q = Store(d.Question), opts = string.Join(Sep, o.Select(Store)), votes = string.Join(Sep, votes), n = o.Count,
            timeout = d.TimeoutDays, multi = d.Multiple ? 1 : 0, pub = d.Public ? 1 : 0, active = d.Active ? 1 : 0,
        });
        return null;
    }

    public async Task SetActiveAsync(long pollId, bool active)
    {
        await using var c = await db.OpenAsync();
        await c.ExecuteAsync($"UPDATE {T.Poll} SET active = @a WHERE pollid = @pollId", new { pollId, a = active ? 1 : 0 });
    }

    public async Task DeleteAsync(long threadId, long pollId)
    {
        await using var c = await db.OpenAsync();
        await c.ExecuteAsync($"UPDATE {T.Thread} SET pollid = 0 WHERE threadid = @threadId AND pollid = @pollId AND open <> 10", new { threadId, pollId });
        await c.ExecuteAsync($"DELETE FROM {T.PollVote} WHERE pollid = @pollId", new { pollId });
        await c.ExecuteAsync($"DELETE FROM {T.Poll} WHERE pollid = @pollId", new { pollId });
    }

    /// <summary>Darf der Benutzer abstimmen? Liefert null oder den Grund.</summary>
    public string? CannotVote(ForumUser u, Forum f, ForumThread t, Poll p, Permissions perms)
    {
        if (u.IsGuest) return "Zum Abstimmen bitte anmelden.";
        if (u.IsBanned) return "Gesperrte Benutzer können nicht abstimmen.";
        if (p.IsClosed(ForumTime.Now)) return "Die Umfrage ist beendet.";
        if (!t.IsOpen && !perms.IsModerator(u, f)) return "Das Thema ist geschlossen.";
        if (!perms.IsModerator(u, f) && !perms.Has(u, f, Core.Vb.ForumPerm.CanVote)) return "Du darfst hier nicht abstimmen.";
        return null;
    }

    /// <summary>Stimme abgeben (Optionen 1-basiert).</summary>
    public async Task<string?> VoteAsync(ForumUser u, Poll p, IReadOnlyCollection<int> choices)
    {
        var n = p.OptionList.Count;
        var picked = choices.Where(x => x >= 1 && x <= n).Distinct().ToList();
        if (picked.Count == 0) return "Bitte eine Antwort auswählen.";
        if (!p.IsMultiple && picked.Count > 1) picked = picked.Take(1).ToList();

        await using var c = await db.OpenAsync();
        var already = await c.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {T.PollVote} WHERE pollid = @pid AND userid = @uid", new { pid = p.PollId, uid = u.UserId });
        if (already > 0) return "Du hast bei dieser Umfrage schon abgestimmt.";
        var now = ForumTime.Now;
        foreach (var opt in picked)
            await c.ExecuteAsync($"INSERT IGNORE INTO {T.PollVote} (pollid, userid, votedate, voteoption, votetype) VALUES (@pid, @uid, @now, @opt, @type)",
                new { pid = p.PollId, uid = u.UserId, now, opt, type = p.IsMultiple ? opt : 0 });
        await RecountAsync(c, p.PollId, n, now);
        return null;
    }

    /// <summary>Zähler in vb_poll aus vb_pollvote neu berechnen (wie vB „Umfrage zählen“).</summary>
    private async Task RecountAsync(MySqlConnector.MySqlConnection c, long pollId, int n, long? now)
    {
        var counts = (await c.QueryAsync<(int Opt, int Cnt)>($"SELECT CAST(voteoption AS SIGNED), COUNT(*) FROM {T.PollVote} WHERE pollid = @pollId GROUP BY voteoption", new { pollId }))
            .ToDictionary(x => x.Opt, x => x.Cnt);
        var voters = await c.ExecuteScalarAsync<int>($"SELECT COUNT(DISTINCT IFNULL(userid, pollvoteid)) FROM {T.PollVote} WHERE pollid = @pollId", new { pollId });
        var votes = string.Join(Sep, Enumerable.Range(1, n).Select(i => counts.GetValueOrDefault(i).ToString()));
        await c.ExecuteAsync($"UPDATE {T.Poll} SET votes = @votes, voters = @voters{(now.HasValue ? ", lastvote = @now" : "")} WHERE pollid = @pollId",
            new { votes, voters, now, pollId });
    }
}
