namespace TillPOS.Sync.Feeds;

public interface ISyncFeed
{
    string Name { get; }
    /// <summary>Pulls this feed's changes into the local store; returns rows processed.</summary>
    Task<int> RunAsync(CancellationToken ct);
}
