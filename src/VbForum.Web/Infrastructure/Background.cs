using System.Collections.Concurrent;
using VbForum.Data;

namespace VbForum.Web.Infrastructure;

/// <summary>Sammelt Themenaufrufe im Speicher und schreibt sie minütlich gebündelt (MyISAM sperrt sonst bei jedem Aufruf die Tabelle).</summary>
public sealed class ViewCounter
{
    private ConcurrentDictionary<long, int> _views = new();
    public void Add(long threadId) => _views.AddOrUpdate(threadId, 1, (_, n) => n + 1);
    public Dictionary<long, int> Drain() => new(Interlocked.Exchange(ref _views, new()));
}

/// <summary>Einfache Bremse gegen Passwort-Raten: max. 8 Fehlversuche pro IP bzw. Name in 15 Minuten.</summary>
public sealed class LoginThrottle
{
    private readonly ConcurrentDictionary<string, List<DateTime>> _fails = new();
    private const int Max = 8;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    public bool IsBlocked(string ip, string name) => Count("ip:" + ip) >= Max * 3 || Count("n:" + name.ToLowerInvariant()) >= Max;

    public void Fail(string ip, string name) { Add("ip:" + ip); Add("n:" + name.ToLowerInvariant()); }

    public void Success(string ip, string name) { _fails.TryRemove("n:" + name.ToLowerInvariant(), out _); }

    private int Count(string key)
    {
        if (!_fails.TryGetValue(key, out var l)) return 0;
        lock (l) { l.RemoveAll(t => DateTime.UtcNow - t > Window); return l.Count; }
    }

    private void Add(string key)
    {
        var l = _fails.GetOrAdd(key, _ => new List<DateTime>());
        lock (l) l.Add(DateTime.UtcNow);
    }
}

/// <summary>Hintergrundaufgaben: Aufrufzähler schreiben, abgelaufene Sperren aufheben, Bild-Cache aufräumen.</summary>
public sealed class ForumBackgroundService(ViewCounter views, IServiceProvider sp, ForumPaths paths, ILogger<ForumBackgroundService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        int tick = 0;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromMinutes(1), ct); } catch (OperationCanceledException) { break; }
            var db = sp.GetRequiredService<Db>();
            if (!db.IsConfigured) continue;
            try
            {
                await sp.GetRequiredService<ThreadRepository>().AddViewsAsync(views.Drain());
                if (++tick % 60 == 0)
                {
                    var cache = sp.GetRequiredService<BoardCache>();
                    var lifted = await sp.GetRequiredService<UserRepository>().LiftExpiredBansAsync((await cache.GetAsync()).Settings.RegisteredGroupId);
                    if (lifted > 0) log.LogInformation("{n} abgelaufene Sperren aufgehoben", lifted);
                    var expired = await sp.GetRequiredService<PremiumService>().ExpireAsync();
                    if (expired > 0) log.LogInformation("{n} abgelaufene Premium-Abos beendet", expired);
                    CleanImageCache();
                }
            }
            catch (Exception ex) { log.LogWarning(ex, "Hintergrundaufgabe fehlgeschlagen"); }
        }
        try { await sp.GetRequiredService<ThreadRepository>().AddViewsAsync(views.Drain()); } catch { /* Shutdown */ }
    }

    private void CleanImageCache()
    {
        if (!Directory.Exists(paths.ImageCache)) return;
        foreach (var f in Directory.EnumerateFiles(paths.ImageCache))
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > TimeSpan.FromDays(7)) try { File.Delete(f); } catch { }
    }
}
