using System.Text.Json;
using TillPOS.Sync;
using TillPOS.Tests.Fakes;

namespace TillPOS.Tests.Sync;

public class KeysetPagerTests
{
    private readonly FakeErp erp = new();
    private readonly InMemorySyncState state = new();
    private readonly List<string> seen = [];

    private void Row(string name, string modified) =>
        erp.AddRow("Item", new() { ["name"] = name, ["modified"] = modified });

    private static string Ts(int second, int micro = 0) => $"2026-10-05 10:00:{second:00}.{micro:000000}";

    private Task<int> Pull(int pageSize = 2, Func<IReadOnlyList<JsonElement>, Task>? handler = null) =>
        new KeysetPager(erp, state, TimeSpan.Zero).PullAsync("Item", "Item", ["item_name"], [],
            handler ?? Collect,
            pageSize);

    private Task Collect(IReadOnlyList<JsonElement> page)
    {
        seen.AddRange(page.Select(r => r.GetProperty("name").GetString()!));
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Pulls_every_row_across_pages_in_order()
    {
        for (var i = 1; i <= 5; i++) Row($"I{i}", Ts(i));
        Assert.Equal(5, await Pull());
        Assert.Equal(new[] { "I1", "I2", "I3", "I4", "I5" }, seen);
    }

    [Fact]
    public async Task Page_handled_reports_the_fresh_rows_of_each_page()
    {
        for (var i = 1; i <= 5; i++) Row($"I{i}", Ts(i));
        var pages = new List<int>();
        var pager = new KeysetPager(erp, state, TimeSpan.Zero) { PageHandled = pages.Add };

        await pager.PullAsync("Item", "Item", ["item_name"], [], Collect, 2);

        // `modified >= mark` re-reads the last row of each page, so later pages have one fresh row less.
        Assert.Equal(new[] { 2, 1, 1, 1 }, pages);
        Assert.Equal(5, pages.Sum());
    }

    [Fact]
    public void Is_at_start_until_a_page_is_saved()
    {
        var pager = new KeysetPager(erp, state, TimeSpan.Zero);
        Assert.True(pager.IsAtStart("Item"));
        state.Set("Item", new SyncMark(Ts(1), ["I1"]));
        Assert.False(pager.IsAtStart("Item"));
    }

    [Fact]
    public async Task Second_pull_only_gets_changes()
    {
        Row("I1", Ts(1));
        Row("I2", Ts(2));
        await Pull();
        seen.Clear();
        Assert.Equal(0, await Pull());

        Row("I3", Ts(3));
        Assert.Equal(1, await Pull());
        Assert.Equal(new[] { "I3" }, seen);
    }

    [Fact]
    public async Task Rows_sharing_a_timestamp_across_a_page_boundary_are_neither_skipped_nor_repeated()
    {
        foreach (var n in new[] { "A", "B", "C", "D", "E" }) Row(n, Ts(1));
        Row("F", Ts(2));
        await Pull(pageSize: 2);
        Assert.Equal(new[] { "A", "B", "C", "D", "E", "F" }, seen);
    }

    [Fact]
    public async Task Full_page_of_already_seen_rows_grows_the_page()
    {
        foreach (var n in new[] { "A", "B", "C" }) Row(n, Ts(1));
        state.Set("Item", new SyncMark(Ts(1), ["A", "B"]));
        await Pull(pageSize: 2);
        Assert.Equal(new[] { "C" }, seen);
    }

    [Fact]
    public async Task Resumes_after_handler_failure_without_skipping()
    {
        for (var i = 1; i <= 4; i++) Row($"I{i}", Ts(i));
        var calls = 0;
        await Assert.ThrowsAsync<IOException>(() => Pull(2, page =>
        {
            if (++calls == 2) throw new IOException("network dropped");
            seen.AddRange(page.Select(r => r.GetProperty("name").GetString()!));
            return Task.CompletedTask;
        }));
        Assert.Equal(new[] { "I1", "I2" }, seen);

        await Pull();
        Assert.Equal(new[] { "I1", "I2", "I3", "I4" }, seen);
    }

    [Fact]
    public async Task Overlap_window_picks_up_rows_committed_late_with_an_earlier_modified()
    {
        var pager = new KeysetPager(erp, state, TimeSpan.FromMinutes(5));
        Row("I1", Ts(1));
        await pager.PullAsync("Item", "Item", ["item_name"], [], Collect, 2);
        seen.Clear();

        Row("LATE", "2026-10-05 10:00:00.500000"); // committed after the pull, stamped before the mark
        await pager.PullAsync("Item", "Item", ["item_name"], [], Collect, 2);

        Assert.Contains("LATE", seen);
    }

    [Fact]
    public async Task Overlap_window_does_not_reread_rows_older_than_the_window()
    {
        var pager = new KeysetPager(erp, state, TimeSpan.FromMinutes(5));
        Row("OLD", "2026-10-05 09:00:00.000000");
        Row("I1", Ts(1));
        await pager.PullAsync("Item", "Item", ["item_name"], [], Collect, 2);
        seen.Clear();

        await pager.PullAsync("Item", "Item", ["item_name"], [], Collect, 2);

        Assert.DoesNotContain("OLD", seen);
    }

    [Theory]
    [InlineData("2026-10-05 10:00:01", "2026-10-05 10:00:01.000000")]
    [InlineData("2026-10-05 10:00:01.5", "2026-10-05 10:00:01.500000")]
    [InlineData("2026-10-05 10:00:01.123456", "2026-10-05 10:00:01.123456")]
    public void Normalizes_frappe_timestamps(string raw, string expected) =>
        Assert.Equal(expected, KeysetPager.NormalizeTimestamp(raw));
}
