namespace TillPOS.Sync.Feeds;

public interface ISyncFeed
{
    string Name { get; }
    /// <summary>Pulls this feed's changes into the local store; returns rows processed.</summary>
    Task<int> RunAsync(CancellationToken ct);
}

/// <summary>A feed that can tell, before it runs, how many rows a full first download will page through
/// (shown as "4,350 of 12,014" on the first-start screen).</summary>
public interface ICountedFeed
{
    /// <summary>The row count when the next pull is a first download, otherwise null.</summary>
    Task<int?> ExpectedRowsAsync(CancellationToken ct);
}

/// <summary>A feed that can succeed with something worth knowing (e.g. a counter skipped): <see cref="LastNote"/> after a run,
/// reported as <see cref="FeedResult.Note"/> without failing the pull.</summary>
public interface INotingFeed
{
    /// <summary>The note of the last run, or null.</summary>
    string? LastNote { get; }
}
