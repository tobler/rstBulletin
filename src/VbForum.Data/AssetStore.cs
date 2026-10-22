using System.Collections.Concurrent;
using Dapper;

namespace VbForum.Data;

/// <summary>
/// Logo, Favicon & Co. liegen in der Datenbank (vbf_asset) – so überstehen sie ein Redeploy des Containers
/// auch ohne Volume. Im Speicher gecacht.
/// </summary>
public sealed class AssetStore(Db db)
{
    public sealed record Asset(string ContentType, byte[] Data, long Updated);

    private readonly ConcurrentDictionary<string, Asset?> _cache = new();

    public async Task<Asset?> GetAsync(string name)
    {
        if (_cache.TryGetValue(name, out var a)) return a;
        await using var c = await db.OpenAsync();
        var row = await c.QueryFirstOrDefaultAsync<(string, byte[], long)?>(
            $"SELECT contenttype, data, CAST(updated AS SIGNED) FROM {Tables.OwnAsset} WHERE name = @name", new { name });
        a = row is { } r ? new Asset(r.Item1, r.Item2, r.Item3) : null;
        _cache[name] = a;
        return a;
    }

    public async Task SetAsync(string name, string contentType, byte[] data)
    {
        await using var c = await db.OpenAsync();
        await c.ExecuteAsync($"REPLACE INTO {Tables.OwnAsset} (name, contenttype, data, updated) VALUES (@name, @contentType, @data, UNIX_TIMESTAMP())",
            new { name, contentType, data });
        _cache.TryRemove(name, out _);
    }

    public async Task RemoveAsync(string name)
    {
        await using var c = await db.OpenAsync();
        await c.ExecuteAsync($"DELETE FROM {Tables.OwnAsset} WHERE name = @name", new { name });
        _cache.TryRemove(name, out _);
    }
}
