using TillPOS.Sync;

namespace TillPOS.Tests.Fakes;

public sealed class InMemorySyncState : ISyncStateStore
{
    public Dictionary<string, SyncMark> Marks { get; } = [];
    public SyncMark Get(string key) => Marks.GetValueOrDefault(key, SyncMark.Start);
    public void Set(string key, SyncMark mark) => Marks[key] = mark;
}
