using TillPOS.Presentation;
using TillPOS.Sync;

namespace TillPOS.Tests.Presentation;

public class DownloadViewModelTests
{
    private static readonly string[] Feeds = ["POS Profile", "Item", "Item Price", "Reconcile"];

    private static DownloadViewModel Vm() => new(Feeds);

    private static FeedResult Ok(string feed, int rows = 0, double seconds = 1.2) => new(feed, rows, TimeSpan.FromSeconds(seconds), null);

    private static PullProgress P(int step, int rows, int? expected, params FeedResult[] done) =>
        new(step, Feeds.Length, Feeds[step - 1], rows, expected, done);

    /// <summary>A whole first download as the puller reports it.</summary>
    private static List<PullProgress> Sequence()
    {
        var profile = Ok("POS Profile", 1, 0.4);
        var items = Ok("Item", 12014, 20.5);
        var prices = Ok("Item Price", 9000, 8);
        var check = Ok("Reconcile", 0, 2);
        return
        [
            P(1, 0, null), P(1, 1, null, profile),
            P(2, 0, 12014), P(2, 500, 12014), P(2, 4350, 12014), P(2, 12014, 12014), P(2, 12014, 12014, profile, items),
            P(3, 0, null), P(3, 500, null), P(3, 9000, null, profile, items, prices),
            P(4, 0, null), P(4, 0, null, profile, items, prices, check),
        ];
    }

    [Fact]
    public void Starts_with_every_step_waiting_under_its_friendly_name()
    {
        var vm = Vm();

        Assert.Equal("Setting up this till", vm.Title);
        Assert.Equal("Downloading items, prices and settings from ERPNext", vm.Subtitle);
        Assert.Equal(new[] { "Shop settings", "Items and barcodes", "Prices", "Daily check" }, vm.Steps.Select(s => s.Name));
        Assert.All(vm.Steps, s => Assert.Equal(DownloadState.Waiting, s.State));
        Assert.Equal(0, vm.Progress);
        Assert.Equal("0:00", vm.Elapsed);
    }

    [Theory]
    [InlineData("POS Profile", "Shop settings")]
    [InlineData("Item Group", "Item groups")]
    [InlineData("Item Tax Template", "Item tax templates")]
    [InlineData("Sales Taxes and Charges Template", "Sales tax templates")]
    [InlineData("Pricing Rule", "Offers and discounts")]
    [InlineData("Item", "Items and barcodes")]
    [InlineData("Item Price", "Prices")]
    [InlineData("Deleted Document", "Removed items")]
    [InlineData("Reconcile", "Daily check")]
    [InlineData("POS Cashier", "Cashiers")]
    [InlineData("Something New", "Something New")]
    public void Friendly_names(string feed, string expected) => Assert.Equal(expected, DownloadViewModel.FriendlyName(feed));

    [Fact]
    public void Running_step_shows_rows_of_expected()
    {
        var vm = Vm();
        vm.Apply(P(2, 4350, 12014, Ok("POS Profile", 1)));

        Assert.Equal(new[] { DownloadState.Done, DownloadState.Running, DownloadState.Waiting, DownloadState.Waiting }, vm.Steps.Select(s => s.State));
        Assert.Equal("Items and barcodes: 4,350 of 12,014", vm.CurrentText);
        Assert.Equal("Step 2 of 4", vm.StepText);
        Assert.Equal("4,350", vm.Steps[1].Detail);
        Assert.False(vm.IsIndeterminate);
        Assert.Equal((1 + 4350 / 12014.0) / 4 * 100, vm.Progress, 6);
    }

    [Fact]
    public void Running_step_without_expected_count_is_indeterminate_at_half_a_step()
    {
        var vm = Vm();
        vm.Apply(P(3, 4350, null, Ok("POS Profile", 1), Ok("Item", 12014)));

        Assert.Equal("Prices: 4,350", vm.CurrentText);
        Assert.True(vm.IsIndeterminate);
        Assert.Equal(2.5 / 4 * 100, vm.Progress, 6);
    }

    [Fact]
    public void Step_without_rows_yet_shows_just_its_name()
    {
        var vm = Vm();
        vm.Apply(P(1, 0, null));

        Assert.Equal("Shop settings…", vm.CurrentText);
        Assert.Equal("", vm.Steps[0].Detail);
        Assert.Equal(DownloadState.Running, vm.Steps[0].State);
    }

    [Fact]
    public void Finished_steps_show_rows_and_seconds()
    {
        var vm = Vm();
        vm.Apply(P(2, 12014, 12014, Ok("POS Profile", 1, 0.4), Ok("Item", 12014, 20.54)));

        Assert.Equal("0.4 s", vm.Steps[0].Detail);
        Assert.Equal("12,014 items · 20.5 s", vm.Steps[1].Detail);
        Assert.Equal("Items and barcodes: 12,014", vm.CurrentText);
        Assert.Equal(DownloadState.Done, vm.Steps[1].State);
        Assert.False(vm.IsIndeterminate);
        Assert.Equal(50, vm.Progress, 6);
    }

    [Fact]
    public void Failed_feed_shows_its_error()
    {
        var vm = Vm();
        vm.Apply(P(1, 0, null, new FeedResult("POS Profile", 0, TimeSpan.FromSeconds(1), "POS Profile Till 9 not found")));

        Assert.Equal(DownloadState.Failed, vm.Steps[0].State);
        Assert.Equal("POS Profile Till 9 not found", vm.Steps[0].Detail);
    }

    [Fact]
    public void Progress_only_grows_and_ends_at_100()
    {
        var vm = Vm();
        var seen = new List<double>();
        foreach (var p in Sequence())
        {
            vm.Apply(p);
            seen.Add(vm.Progress);
        }

        Assert.Equal(seen.Order(), seen);
        Assert.Equal(100, seen[^1], 6);
        Assert.All(vm.Steps, s => Assert.Equal(DownloadState.Done, s.State));
        Assert.Equal("Step 4 of 4", vm.StepText);
        Assert.False(vm.IsIndeterminate);
    }

    [Fact]
    public void Unknown_feeds_are_added_by_name()
    {
        var vm = new DownloadViewModel();
        vm.Apply(new PullProgress(2, 3, "Item", 10, null, [Ok("POS Profile", 1)]));

        Assert.Equal(3, vm.Steps.Count);
        Assert.Equal(new[] { "Shop settings", "Items and barcodes" }, vm.Steps.Take(2).Select(s => s.Name));
        Assert.Equal(DownloadState.Waiting, vm.Steps[2].State);
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(42, "0:42")]
    [InlineData(65.9, "1:05")]
    [InlineData(3725, "62:05")]
    public void Tick_shows_minutes_and_seconds(double seconds, string expected)
    {
        var vm = Vm();
        vm.Tick(TimeSpan.FromSeconds(seconds));
        Assert.Equal(expected, vm.Elapsed);
    }

    [Fact]
    public void Failure_shows_problem_and_countdown_until_the_next_attempt()
    {
        var vm = Vm();
        vm.Apply(P(1, 0, null));

        vm.Failed("No such host is known", TimeSpan.FromSeconds(30));

        Assert.True(vm.HasError);
        Assert.Equal("Cannot reach ERPNext (No such host is known). The first start needs the internet.", vm.ErrorText);
        Assert.Equal("Retrying in 30 s", vm.RetryText);

        vm.CountdownTick(TimeSpan.FromSeconds(26.4));
        Assert.Equal("Retrying in 27 s", vm.RetryText);
        vm.CountdownTick(TimeSpan.Zero);
        Assert.Equal("Retrying now…", vm.RetryText);

        vm.Apply(P(1, 0, null));
        Assert.False(vm.HasError);
        Assert.Equal("", vm.ErrorText);
        Assert.Equal("", vm.RetryText);
    }

    [Fact]
    public void New_attempt_restarts_the_steps()
    {
        var vm = Vm();
        foreach (var p in Sequence().Take(5)) vm.Apply(p);

        vm.Apply(P(1, 0, null));

        Assert.Equal(new[] { DownloadState.Running, DownloadState.Waiting, DownloadState.Waiting, DownloadState.Waiting }, vm.Steps.Select(s => s.State));
        Assert.All(vm.Steps.Skip(1), s => Assert.Equal("", s.Detail));
        Assert.Equal(12.5, vm.Progress, 6);
    }
}
