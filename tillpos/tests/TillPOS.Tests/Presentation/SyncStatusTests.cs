using TillPOS.Core.Security;
using TillPOS.Core.Sales;
using TillPOS.Core.Shifts;
using TillPOS.Data;
using TillPOS.Presentation;
using TillPOS.Sync;
using TillPOS.Sync.Upload;
using TillPOS.Tests.Sync.Upload;

namespace TillPOS.Tests.Presentation;

/// <summary>The read-only Sync status window (the header's sync pill) and the header's "N uploaded today".</summary>
public sealed class SyncStatusTests : IDisposable
{
    private const string Shift = "TILL2-SHIFT-20261007080000";
    private readonly PresentationFixture f = new();
    private readonly ShellViewModel shell = new() { Upload = UploadMode.Live, Online = true, SyncStatus = "Online · synced 09:25" };
    private readonly DateTimeOffset midnight;

    public SyncStatusTests()
    {
        midnight = SyncStatusViewModel.StartOfDay(f.Clock.Now);
        f.LogInWithOpenShift();
        // In the app the shell's session is the one every screen uses.
        shell.Session.Cashier = f.Session.Cashier;
        shell.Session.Shift = f.Session.Shift;
    }

    public void Dispose() => f.Dispose();

    private SyncStatusViewModel Vm() => new(f.Ctx, shell);

    private void Bill(string id, decimal total = 6.79m, ReceiptKind kind = ReceiptKind.Sale)
    {
        var sale = UploadTestData.Sale(id, Shift, f.Clock.Now.AddMinutes(-30));
        f.Ctx.Receipts.Save(sale with { Kind = kind, GrandTotal = total, ReturnAgainst = kind == ReceiptKind.Return ? "TILL2-A" : null });
    }

    [Fact]
    public void Start_of_day_is_local_midnight_in_the_clocks_offset() =>
        Assert.Equal(new DateTimeOffset(2026, 10, 7, 0, 0, 0, TimeSpan.FromHours(4)), midnight);

    [Fact]
    public void Uploaded_today_lists_every_kind_newest_first_with_shop_time_name_and_amount()
    {
        Bill("TILL2-A", 12.5m);
        Bill("TILL2-R", -6.79m, ReceiptKind.Return);
        f.Ctx.Approvals.Add(new ApprovalRecord("APP1", ApprovalAction.LineVoid, "simran", "sup", Shift, null, "MILK", 6.79m, null, f.Clock.Now));
        f.Ctx.Shifts.MarkSynced(Shift, ShiftDocument.Opening, "POSA-OS-26-0001", midnight.AddHours(8).AddMinutes(1));
        f.Ctx.Receipts.MarkSynced("TILL2-A", "ACC-PSINV-26-0001", midnight.AddHours(9).AddMinutes(25));
        f.Ctx.Receipts.MarkSynced("TILL2-R", "ACC-PSINV-26-0002", midnight.AddHours(9).AddMinutes(26));
        f.Ctx.Approvals.MarkUploaded("APP1", "TPA-0001", midnight.AddHours(9).AddMinutes(27));
        // Yesterday's upload is not today's.
        Bill("TILL2-OLD");
        f.Ctx.Receipts.MarkSynced("TILL2-OLD", "ACC-PSINV-26-0000", midnight.AddSeconds(-1));

        var vm = Vm();

        Assert.Equal(4, vm.UploadedTodayCount);
        Assert.Equal(
        [
            ("09:27", "Approval", "APP1", "TPA-0001", "6.79"),
            ("09:26", "Return", "TILL2-R", "ACC-PSINV-26-0002", "-6.79"),
            ("09:25", "Bill", "TILL2-A", "ACC-PSINV-26-0001", "12.50"),
            ("08:01", "Shift opening", Shift, "POSA-OS-26-0001", "200.00"),
        ], vm.UploadedToday.Select(r => (r.Time, r.Kind, r.Id, r.ErpName, r.Amount)));
    }

    [Fact]
    public void Waiting_documents_say_why_from_the_last_upload_run_or_their_own_note()
    {
        Bill("TILL2-A");
        Bill("TILL2-B");
        f.Ctx.Receipts.MarkInFlight("TILL2-B", f.Clock.Now.AddMinutes(5));
        shell.UploadProblemDetails = [new UploadProblem("TILL2-A", null, "Bill TILL2-A waits for its shift's opening to upload.")];

        var vm = Vm();

        Assert.Equal(
        [
            ("Bill", "TILL2-A", "Bill TILL2-A waits for its shift's opening to upload."),
            ("Bill", "TILL2-B", "upload in progress"),
            ("Shift opening", Shift, "Waiting for the next upload"),
        ], vm.Waiting.Select(r => (r.Kind, r.Id, r.Reason)));                 // newest first
        Assert.Equal(3, vm.WaitingCount);
        Assert.Equal("", vm.WaitingMore);
    }

    [Fact]
    public void A_shifts_opening_and_closing_each_show_their_own_reason()
    {
        f.Ctx.Shifts.Close(new ShiftClosing(Shift, f.Clock.Now, [], 0, 0, 0m, 0m, 0m));
        shell.UploadProblemDetails =
        [
            new UploadProblem(Shift, null, $"Closing of shift {Shift} waits: 1 bill(s) of the shift are not uploaded yet.") { Kind = OutboxKind.Closing },
            new UploadProblem(Shift, null, $"Opening of shift {Shift} waits for TILL2-SHIFT-1 to close in ERPNext.") { Kind = OutboxKind.Opening },
        ];

        var vm = Vm();

        Assert.Equal(
        [
            ("Shift closing", $"Closing of shift {Shift} waits: 1 bill(s) of the shift are not uploaded yet."),
            ("Shift opening", $"Opening of shift {Shift} waits for TILL2-SHIFT-1 to close in ERPNext."),
        ], vm.Waiting.Select(r => (r.Kind, r.Reason)));
    }

    [Fact]
    public void Each_list_shows_the_newest_200_and_counts_the_rest()
    {
        f.Ctx.Shifts.MarkSynced(Shift, ShiftDocument.Opening, "POSA-OS-1", midnight.AddHours(8));
        for (var i = 0; i < 205; i++)
        {
            var id = $"TILL2-{i:D3}";
            f.Ctx.Receipts.Save(UploadTestData.Sale(id, Shift, f.Clock.Now.AddMinutes(-300 + i)));
            if (i < 203) f.Ctx.Receipts.MarkSynced(id, $"ACC-{i:D3}", midnight.AddHours(8).AddSeconds(i + 1));
            else f.Ctx.Receipts.MarkFailed(id, "refused", f.Clock.Now.AddMinutes(5));
        }
        for (var i = 0; i < 210; i++)
            f.Ctx.Approvals.Add(new ApprovalRecord($"APP{i:D3}", ApprovalAction.LineVoid, "simran", "sup", Shift, null, "MILK", 1m, null,
                f.Clock.Now.AddMinutes(-300 + i)));

        var vm = Vm();

        Assert.Equal((200, 204, "and 4 more"), (vm.UploadedToday.Count, vm.UploadedTodayCount, vm.UploadedTodayMore));
        Assert.Equal("TILL2-202", vm.UploadedToday[0].Id);                                 // newest first
        Assert.Equal((200, 210, "and 10 more"), (vm.Waiting.Count, vm.WaitingCount, vm.WaitingMore));
        Assert.Equal("APP209", vm.Waiting[0].Id);
        Assert.Equal((2, 2, ""), (vm.Failed.Count, vm.FailedCount, vm.FailedMore));
    }

    [Fact]
    public void A_waiting_return_is_named_from_the_bills_kind_without_reading_the_bill()
    {
        Bill("TILL2-R", -6.79m, ReceiptKind.Return);
        using (var c = f.Temp.Db.Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "UPDATE receipt SET json = 'not json'";                    // reading the bill would fail
            cmd.ExecuteNonQuery();
        }

        var vm = Vm();

        Assert.Equal("", vm.Message);
        Assert.Contains(vm.Waiting, r => (r.Kind, r.Id) == ("Return", "TILL2-R"));
    }

    [Theory]
    [InlineData(UploadMode.Off, "Upload is off on this till")]
    [InlineData(UploadMode.DryRun, "Dry run: checked and previewed only, never sent")]
    public void Without_live_upload_nothing_is_sent_and_the_window_says_so(UploadMode mode, string reason)
    {
        shell.Upload = mode;
        var vm = Vm();
        Assert.Equal(reason, Assert.Single(vm.Waiting).Reason);
        Assert.Contains(mode == UploadMode.Off ? "Off" : "Dry run", vm.UploadModeText);
    }

    [Fact]
    public void Failed_documents_show_their_error()
    {
        Bill("TILL2-A");
        f.Ctx.Receipts.MarkFailed("TILL2-A", "Item RICE5 is disabled", f.Clock.Now.AddMinutes(5));
        f.Ctx.Shifts.MarkSynced(Shift, ShiftDocument.Opening, "POSA-OS-1");

        var vm = Vm();

        Assert.Equal([("Bill", "TILL2-A", "Item RICE5 is disabled")], vm.Failed.Select(r => (r.Kind, r.Id, r.Error)));
        Assert.Equal(1, vm.FailedCount);
        Assert.Empty(vm.Waiting);
    }

    [Fact]
    public void The_last_pull_lists_each_feed_and_explains_its_problem_in_plain_words()
    {
        shell.LastSyncAt = midnight.AddHours(9).AddMinutes(25);
        shell.LastPull = new PullReport(
        [
            new FeedResult("Items", 1520, TimeSpan.FromSeconds(2), null),
            new FeedResult("POS Profile", 2, TimeSpan.Zero, null) { Note = "Counter \"Al Ain Counter 9\" could not be read" },
            new FeedResult("Cashiers", 0, TimeSpan.Zero, "ERPNext returned HTTP 403: {\"exc_type\":\"PermissionError\"}"),
            new FeedResult("Recent bills", 0, TimeSpan.Zero, "No such host is known. (dev.quickgroc.com:443)"),
        ]);

        var vm = Vm();

        Assert.Equal("Online", vm.Connection);
        Assert.Equal("09:25", vm.LastSync);
        Assert.Equal("2 sync problem(s) in the last download", vm.FeedsSummary);
        Assert.Equal(
        [
            ("Items", "1520", true, "OK"),
            ("POS Profile", "2", true, "OK - Counter \"Al Ain Counter 9\" could not be read"),
            ("Cashiers", "0", false, "ERPNext refused: the till's login may not read this. (ERPNext returned HTTP 403: {\"exc_type\":\"PermissionError\"})"),
            ("Recent bills", "0", false, "Could not reach ERPNext (network). (No such host is known. (dev.quickgroc.com:443))"),
        ], vm.Feeds.Select(r => (r.Feed, r.Rows, r.Ok, r.Text)));
    }

    [Theory]
    [InlineData("ERPNext returned HTTP 401: Unauthorized", "ERPNext did not accept the till's API key")]
    [InlineData("Item Price X not found", "Not found in ERPNext")]
    [InlineData("The request was canceled due to the configured HttpClient.Timeout of 60 seconds elapsing.", "ERPNext did not answer in time")]
    [InlineData("ERPNext returned HTTP 502: Bad Gateway", "ERPNext had a server error; it is tried again at the next sync")]
    [InlineData("Something odd", "The download failed")]
    public void Feed_errors_in_plain_words(string error, string plain) =>
        Assert.Equal(plain, SyncStatusViewModel.PlainFeedError(error));

    [Fact]
    public void Before_the_first_download_the_window_says_so()
    {
        shell.Online = false;
        var vm = Vm();
        Assert.Equal("Offline", vm.Connection);
        Assert.Equal("not yet", vm.LastSync);
        Assert.Equal("No download since the till started", vm.FeedsSummary);
        Assert.Empty(vm.Feeds);
    }

    [Fact]
    public void A_clean_download_is_summed_up()
    {
        shell.LastPull = new PullReport([new FeedResult("Items", 3, TimeSpan.Zero, null), new FeedResult("Prices", 4, TimeSpan.Zero, null)]);
        Assert.Equal("All 2 feeds OK in the last download", Vm().FeedsSummary);
    }

    // ---- Opening it, and Upload problems from any screen ----

    [Fact]
    public async Task The_window_is_read_only_and_needs_no_pin()
    {
        SyncStatusViewModel? shown = null;
        f.Dialogs.OnSyncStatus = vm => { shown = vm; return false; };
        shell.Show(new object());

        await SyncStatusViewModel.OpenAsync(f.Ctx, shell);

        Assert.NotNull(shown);
        Assert.Equal(0, f.Dialogs.PinRequests);
        Assert.Null(f.Navigator.Current);
    }

    [Fact]
    public async Task Upload_problems_open_from_any_screen_behind_the_supervisor_pin_and_back_returns_there()
    {
        var sale = new object();
        shell.Show(sale);
        f.Dialogs.OnSyncStatus = _ => true;
        f.Dialogs.Pins.Enqueue("9999");

        await SyncStatusViewModel.OpenAsync(f.Ctx, shell);

        var problems = Assert.IsType<UploadProblemsViewModel>(f.Navigator.Current);
        var log = Assert.Single(f.Ctx.Approvals.Unsynced(), a => a.Reason == "Open Upload problems");
        Assert.Equal(("simran", "sup", Shift), (log.CashierId, log.SupervisorId, log.ShiftClientId));
        problems.BackCommand.Execute(null);
        Assert.Same(sale, f.Navigator.Current);
    }

    [Fact]
    public async Task A_wrong_pin_stays_on_the_screen()
    {
        var sale = new object();
        shell.Show(sale);
        f.Dialogs.OnSyncStatus = _ => true;
        f.Dialogs.Pins.Enqueue("1111");

        await SyncStatusViewModel.OpenAsync(f.Ctx, shell);

        Assert.Null(f.Navigator.Current);
    }

    [Fact]
    public void Upload_problems_cannot_be_opened_during_the_first_download()
    {
        shell.Show(new DownloadViewModel(["Items"]));
        Assert.False(Vm().CanOpenUploadProblems);
        shell.Show(new object());
        Assert.True(Vm().CanOpenUploadProblems);
    }

    // ---- The header ----

    [Fact]
    public void The_header_counts_read_uploaded_today_waiting_and_failed()
    {
        shell.UploadedToday = 12;
        shell.PendingUploads = 0;
        shell.FailedUploads = 1;
        Assert.Equal("12 uploaded today · 0 waiting · 1 failed", shell.UploadCountsText);

        var changed = new List<string?>();
        shell.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        shell.UploadedToday = 13;
        Assert.Contains(nameof(ShellViewModel.UploadCountsText), changed);
    }

    [Fact]
    public async Task The_sync_pill_runs_the_open_action()
    {
        var opened = 0;
        shell.OpenSyncStatus = () => { opened++; return Task.CompletedTask; };
        await shell.SyncStatusCommand.ExecuteAsync(null);
        Assert.Equal(1, opened);
    }
}
