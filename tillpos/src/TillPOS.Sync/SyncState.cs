using System.Text.Json;
using TillPOS.Data;

namespace TillPOS.Sync;

/// <summary>High-water mark: newest `modified` processed, plus the names already processed at exactly that time.</summary>
public sealed record SyncMark(string Modified, IReadOnlyList<string> NamesAtMark)
{
    public static readonly SyncMark Start = new("1900-01-01 00:00:00.000000", []);
}

public interface ISyncStateStore
{
    SyncMark Get(string key);
    void Set(string key, SyncMark mark);
}

public sealed class KvSyncStateStore(CatalogStore store) : ISyncStateStore
{
    public SyncMark Get(string key) =>
        store.GetValue("sync_mark:" + key) is { } json ? JsonSerializer.Deserialize<SyncMark>(json) ?? SyncMark.Start : SyncMark.Start;

    public void Set(string key, SyncMark mark) => store.SetValue("sync_mark:" + key, JsonSerializer.Serialize(mark));
}
