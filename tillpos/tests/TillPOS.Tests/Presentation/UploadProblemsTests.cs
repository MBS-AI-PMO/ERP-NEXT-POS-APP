using TillPOS.Core.Catalog;
using TillPOS.Core.Sales;
using TillPOS.Core.Security;
using TillPOS.Core.Shifts;
using TillPOS.Data;
using TillPOS.Presentation;
using TillPOS.Sync.Upload;
using TillPOS.Tests.Sync.Upload;

namespace TillPOS.Tests.Presentation;

public sealed class UploadProblemsTests : IDisposable
{
    private readonly PresentationFixture f = new();
    private readonly object loginMarker = new();
    private const string OldShift = "TILL2-SHIFT-20261001080000";
    private const string Shift = "TILL2-SHIFT-20261007080000";

    public UploadProblemsTests()
    {
        // An old shift, excluded when the till went Live, and today's shift with one failed bill.
        var old = f.Clock.Now.AddDays(-6);
        f.Ctx.Shifts.Open(new ShiftOpening(OldShift, "simran", "Al Ain Counter 2", old, [new ReceiptPayment("Cash Counter 2", 100m)]));
        f.Ctx.Receipts.Save(Bill("TILL2-OLD", OldShift, old.AddHours(1)));
        f.Ctx.Shifts.Close(new ShiftClosing(OldShift, old.AddHours(2), [], 1, 0, 1m, 1m, 0m));
        UploadHistory.SwitchToLive(f.Ctx.Shifts, f.Ctx.Kv, f.Clock.Now.AddDays(-1), includeHistory: false);

        f.LogInWithOpenShift();
        f.Ctx.Receipts.Save(Bill("TILL2-A", Shift, f.Clock.Now.AddMinutes(-30)));
        f.Ctx.Receipts.MarkFailed("TILL2-A", "Item RICE5 is disabled", f.Clock.Now.AddMinutes(5));
        f.Ctx.Shifts.MarkSynced(Shift, ShiftDocument.Opening, "POS-OPE-1");
    }

    public void Dispose() => f.Dispose();

    private static Receipt Bill(string id, string shift, DateTimeOffset at) => UploadTestData.Sale(id, shift, at);

    private UploadProblemsViewModel Vm() => new(f.Ctx, new SupervisorGate(f.Ctx, new SessionState()), () => loginMarker);

    [Fact]
    public void Lists_failed_and_excluded_documents_apart()
    {
        var vm = Vm();

        var failed = Assert.Single(vm.Failed);
        Assert.Equal(("Bill", "TILL2-A", Shift, "Item RICE5 is disabled", 1), (failed.Kind, failed.Id, failed.Shift, failed.Error, failed.Attempts));
        Assert.False(failed.IsExcluded);
        Assert.Equal(["Opening shift", "Bill", "Closing shift"], vm.Excluded.Select(r => r.Kind));
        Assert.All(vm.Excluded, r => Assert.Equal(OldShift, r.Shift));
        Assert.Equal(4, UploadProblemsViewModel.Count(f.Ctx));
    }

    [Fact]
    public async Task Retry_needs_a_supervisor_resets_the_backoff_and_is_logged()
    {
        var vm = Vm();
        f.Dialogs.Pins.Enqueue("1111");
        await vm.RetryCommand.ExecuteAsync(vm.Failed[0]);
        Assert.Equal(ReceiptSyncStatus.Failed, f.Ctx.Receipts.SyncInfo("TILL2-A").Status);

        f.Dialogs.Pins.Enqueue("9999");
        await vm.RetryCommand.ExecuteAsync(vm.Failed[0]);

        Assert.Equal(new ReceiptSyncInfo(ReceiptSyncStatus.Pending, null, "Item RICE5 is disabled", 0), f.Ctx.Receipts.SyncInfo("TILL2-A"));
        var logged = Assert.Single(f.Ctx.Approvals.Unsynced(), a => a.Action == ApprovalAction.UploadRetry);
        Assert.Equal(("sup", "TILL2-A"), (logged.SupervisorId, logged.ReceiptClientId));
        Assert.Empty(vm.Failed);
        Assert.Equal("Bill TILL2-A will be uploaded again shortly.", vm.Message);
    }

    [Fact]
    public async Task Mark_as_handled_takes_the_document_out_of_the_upload_for_good()
    {
        var vm = Vm();
        f.Dialogs.TextAnswers.Enqueue("Entered by hand in ERPNext");
        f.Dialogs.TextAnswers.Enqueue("ACC-PSINV-2026-00099");
        f.Dialogs.Pins.Enqueue("9999");

        await vm.MarkHandledCommand.ExecuteAsync(vm.Failed[0]);

        var info = f.Ctx.Receipts.SyncInfo("TILL2-A");
        Assert.Equal(ReceiptSyncStatus.Handled, info.Status);
        Assert.Equal("Handled by sup (07/10 10:00): Entered by hand in ERPNext ACC-PSINV-2026-00099", info.LastError);
        var logged = Assert.Single(f.Ctx.Approvals.Unsynced(), a => a.Action == ApprovalAction.UploadMarkHandled);
        Assert.Contains("Entered by hand in ERPNext", logged.Reason);
        Assert.Empty(vm.Failed);
        var handled = Assert.Single(vm.Handled);
        Assert.Equal("Handled by sup (07/10 10:00): Entered by hand in ERPNext ACC-PSINV-2026-00099", handled.Error);
        Assert.Equal(0, f.Ctx.Receipts.CountFailed());
        Assert.Equal(0, f.Ctx.Receipts.CountPending());
        Assert.Equal(3, UploadProblemsViewModel.Count(f.Ctx));   // handled ones need no look
    }

    [Fact]
    public async Task Mark_as_handled_needs_a_reason()
    {
        var vm = Vm();
        f.Dialogs.TextAnswers.Enqueue("  ");

        await vm.MarkHandledCommand.ExecuteAsync(vm.Failed[0]);

        Assert.Equal(ReceiptSyncStatus.Failed, f.Ctx.Receipts.SyncInfo("TILL2-A").Status);
        Assert.Equal(0, f.Dialogs.PinRequests);
        Assert.Equal("Not marked: a reason is needed.", vm.Message);
    }

    [Fact]
    public async Task Un_handle_puts_a_handled_document_back_in_the_queue_and_is_logged()
    {
        var vm = Vm();
        f.Dialogs.TextAnswers.Enqueue("Duplicate");
        f.Dialogs.TextAnswers.Enqueue(null);
        f.Dialogs.Pins.Enqueue("9999");
        await vm.MarkHandledCommand.ExecuteAsync(vm.Failed[0]);
        Assert.Equal("Handled by sup (07/10 10:00): Duplicate", f.Ctx.Receipts.SyncInfo("TILL2-A").LastError);

        f.Dialogs.Pins.Enqueue("9999");
        await vm.UnhandleCommand.ExecuteAsync(vm.Handled[0]);

        Assert.Equal(ReceiptSyncStatus.Pending, f.Ctx.Receipts.SyncInfo("TILL2-A").Status);
        Assert.Single(f.Ctx.Approvals.Unsynced(), a => a.Action == ApprovalAction.UploadUnhandle);
        Assert.Empty(vm.Handled);
    }

    [Fact]
    public async Task Including_an_excluded_document_brings_back_its_whole_shift()
    {
        var vm = Vm();
        f.Dialogs.Pins.Enqueue("9999");

        await vm.IncludeCommand.ExecuteAsync(vm.Excluded.Single(r => r.Kind == "Bill"));

        Assert.Equal(UploadStatus.Pending, f.Ctx.Shifts.SyncInfo(OldShift)!.OpeningStatus);
        Assert.Equal(UploadStatus.Pending, f.Ctx.Shifts.SyncInfo(OldShift)!.ClosingStatus);
        Assert.Equal(ReceiptSyncStatus.Pending, f.Ctx.Receipts.SyncInfo("TILL2-OLD").Status);
        Assert.Empty(vm.Excluded);
        Assert.Single(f.Ctx.Approvals.Unsynced(), a => a.Action == ApprovalAction.UploadIncludeHistory);
    }

    [Fact]
    public async Task Retry_is_not_offered_for_excluded_documents_nor_include_for_failed_ones()
    {
        var vm = Vm();

        await vm.RetryCommand.ExecuteAsync(vm.Excluded[0]);
        await vm.IncludeCommand.ExecuteAsync(vm.Failed[0]);

        Assert.Equal(0, f.Dialogs.PinRequests);
    }

    [Fact]
    public void View_shows_what_would_be_sent()
    {
        f.Ctx.Kv.SavePosSettings("Al Ain Counter 2", PayloadTests.Counter1 with { PosProfile = "Al Ain Counter 2" });
        var vm = Vm();

        vm.ViewCommand.Execute(vm.Failed[0]);

        var (title, text) = Assert.Single(f.Dialogs.Texts);
        Assert.Equal("Bill TILL2-A", title);
        Assert.Contains("Error: Item RICE5 is disabled", text);
        Assert.Contains("\"posa_client_request_id\": \"TILL2-A\"", text);
        Assert.Contains("\"posa_pos_opening_shift\": \"POS-OPE-1\"", text);
    }

    [Fact]
    public void View_of_a_closing_carries_the_taxes_like_the_upload()
    {
        f.Ctx.Kv.SavePosSettings("Al Ain Counter 2", PayloadTests.Counter1 with { PosProfile = "Al Ain Counter 2" });
        var vat = new SalesTaxTemplate("UAE VAT 5% - AAML", [new TaxRow(1, "VAT 5% - AAML", "VAT 5%", 5m, true)], null);
        var ctx = f.Ctx with { TaxTemplates = name => name == vat.Name ? vat : null };
        var vm = new UploadProblemsViewModel(ctx, new SupervisorGate(ctx, new SessionState()), () => loginMarker);

        vm.ViewCommand.Execute(vm.Excluded.Single(r => r.Kind == "Closing shift"));

        var text = Assert.Single(f.Dialogs.Texts).Text;
        Assert.Contains("\"account_head\": \"VAT 5% - AAML\"", text);
        Assert.Contains("\"pos_payments\": []", text);
    }

    [Fact]
    public void View_without_the_counter_settings_says_so()
    {
        var vm = Vm();

        vm.ViewCommand.Execute(vm.Failed[0]);

        Assert.Contains("cannot be shown", Assert.Single(f.Dialogs.Texts).Text);
    }

    [Fact]
    public void Back_returns_to_where_it_came_from()
    {
        Vm().BackCommand.Execute(null);
        Assert.Same(loginMarker, f.Navigator.Current);
    }

    [Fact]
    public void The_login_button_stays_when_only_handled_documents_are_left()
    {
        f.Ctx.Receipts.MarkHandled("TILL2-A", "Handled by sup: by hand");
        f.Ctx.Receipts.MarkHandled("TILL2-OLD", "Handled by sup: test data");
        f.Ctx.Shifts.MarkHandled(OldShift, ShiftDocument.Opening, "Handled by sup: test data");
        f.Ctx.Shifts.MarkHandled(OldShift, ShiftDocument.Closing, "Handled by sup: test data");

        var login = new LoginViewModel(f.Ctx, f.Session, () => new object());

        Assert.Equal((0, 4, 4), (login.UploadProblemsCount, login.HandledUploads, login.UploadProblemsTotal));
        Assert.Equal("Upload problems (0 · 4 handled)", login.UploadProblemsLabel);
    }

    [Fact]
    public async Task The_login_screen_opens_upload_problems_behind_the_supervisor_pin()
    {
        var login = new LoginViewModel(f.Ctx, f.Session, () => new object());
        Assert.Equal(4, login.UploadProblemsCount);
        Assert.Equal("Upload problems (4)", login.UploadProblemsLabel);

        f.Dialogs.Pins.Enqueue("1111");
        await login.UploadProblemsCommand.ExecuteAsync(null);
        Assert.IsNotType<UploadProblemsViewModel>(f.Navigator.Current);

        f.Dialogs.Pins.Enqueue("9999");
        await login.UploadProblemsCommand.ExecuteAsync(null);
        var vm = Assert.IsType<UploadProblemsViewModel>(f.Navigator.Current);
        vm.BackCommand.Execute(null);
        Assert.IsType<LoginViewModel>(f.Navigator.Current);
    }

    [Fact]
    public void Close_shift_warns_while_bills_of_the_shift_are_still_waiting()
    {
        var vm = new CloseShiftViewModel(f.Ctx, f.Session, new SupervisorGate(f.Ctx, f.Session), () => { }, () => { });
        Assert.Equal("1 bill(s) of this shift are still waiting to upload. They will upload automatically; the Closing Shift waits for them.",
            vm.UploadWarning);

        f.Ctx.Receipts.MarkSynced("TILL2-A", "ACC-1");
        Assert.Equal("", new CloseShiftViewModel(f.Ctx, f.Session, new SupervisorGate(f.Ctx, f.Session), () => { }, () => { }).UploadWarning);
    }
}
