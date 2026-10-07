using TillPOS.Core.Payments;
using TillPOS.Core.Security;
using TillPOS.Presentation;

namespace TillPOS.Tests.Presentation;

public sealed class SaleViewModelTests : IDisposable
{
    private readonly PresentationFixture f = new();
    private (SaleViewModel Sale, TenderKind Kind)? payRequest;

    public SaleViewModelTests() => f.LogInWithOpenShift();

    public void Dispose() => f.Dispose();

    private SaleViewModel NewSale() =>
        new(f.Ctx, f.Session, new SupervisorGate(f.Ctx, f.Session), (sale, kind) => { payRequest = (sale, kind); return "payment"; },
            () => "login");

    [Fact]
    public void Scanning_adds_a_line_and_updates_totals()
    {
        var vm = NewSale();
        vm.Scan("111");

        var line = Assert.Single(vm.Lines);
        Assert.Equal("Full Cream Milk 1L", line.Name);
        Assert.Equal("6.79", vm.Total);
        Assert.Equal("Added Full Cream Milk 1L", vm.Message);
        Assert.False(vm.MessageIsError);
    }

    [Fact]
    public void Unknown_barcode_is_an_error_message()
    {
        var vm = NewSale();
        vm.Scan("999");
        Assert.True(vm.MessageIsError);
        Assert.Equal("Unknown barcode 999", vm.Message);
        Assert.Empty(vm.Lines);
    }

    [Fact]
    public void Scale_label_shows_weight_and_amount()
    {
        var vm = NewSale();
        vm.Scan("2000089007400");
        Assert.Equal("0.740 Kg", vm.Lines[0].Qty);
        Assert.Equal("2.59", vm.Lines[0].Amount);
    }

    [Fact]
    public void Scan_box_enter_scans_and_clears()
    {
        var vm = NewSale();
        vm.ScanText = "111";
        vm.ScanEnteredCommand.Execute(null);
        Assert.Single(vm.Lines);
        Assert.Equal("", vm.ScanText);
    }

    [Fact]
    public async Task Removing_a_line_needs_a_supervisor_and_is_logged()
    {
        var vm = NewSale();
        vm.Scan("111");
        f.Dialogs.Pins.Enqueue("9999");

        await vm.RemoveLineAsync(vm.Lines[0].Id);

        Assert.Empty(vm.Lines);
        var record = Assert.Single(f.Ctx.Approvals.Unsynced());
        Assert.Equal(ApprovalAction.LineVoid, record.Action);
        Assert.Equal("MILK", record.ItemCode);
    }

    [Fact]
    public async Task Remove_last_needs_a_supervisor_and_removes_the_last_line()
    {
        var vm = NewSale();
        vm.Scan("111");
        vm.Scan("2000089007400");
        Assert.Equal(2, vm.Lines.Count);
        f.Dialogs.Pins.Enqueue("9999");

        await vm.RemoveLastCommand.ExecuteAsync(null);

        var line = Assert.Single(vm.Lines);
        Assert.Equal("111", line.Barcode);
    }

    [Fact]
    public async Task Without_a_supervisor_the_line_stays()
    {
        var vm = NewSale();
        vm.Scan("111");
        f.Dialogs.Pins.Enqueue("1111");

        await vm.RemoveLineAsync(vm.Lines[0].Id);

        Assert.Single(vm.Lines);
    }

    [Fact]
    public async Task Lowering_a_quantity_needs_a_supervisor()
    {
        var vm = NewSale();
        vm.Scan("111");
        vm.Increment(vm.Lines[0].Id);
        Assert.Equal("2", vm.Lines[0].Qty);

        await vm.DecrementAsync(vm.Lines[0].Id);
        Assert.Equal("2", vm.Lines[0].Qty);

        f.Dialogs.Pins.Enqueue("9999");
        await vm.DecrementAsync(vm.Lines[0].Id);
        Assert.Equal("1", vm.Lines[0].Qty);
    }

    [Fact]
    public async Task Setting_a_higher_quantity_needs_no_supervisor_but_lower_does()
    {
        var vm = NewSale();
        vm.Scan("111");
        f.Dialogs.Numbers.Enqueue(5m);
        await vm.SetQtyAsync(vm.Lines[0].Id);
        Assert.Equal("5", vm.Lines[0].Qty);
        Assert.Equal(0, f.Dialogs.PinRequests);

        f.Dialogs.Numbers.Enqueue(2m);
        await vm.SetQtyAsync(vm.Lines[0].Id);
        Assert.Equal("5", vm.Lines[0].Qty);
        Assert.Equal(1, f.Dialogs.PinRequests);
    }

    [Fact]
    public async Task A_quantity_over_999_is_refused()
    {
        var vm = NewSale();
        vm.Scan("111");
        f.Dialogs.Numbers.Enqueue(5000m);

        await vm.SetQtyAsync(vm.Lines[0].Id);

        Assert.Equal("1", vm.Lines[0].Qty);
        Assert.True(vm.MessageIsError);
        Assert.Equal("Quantity must be 999 or less.", vm.Message);
        Assert.Equal(0, f.Dialogs.PinRequests);
    }

    [Fact]
    public async Task A_fractional_quantity_of_a_piece_item_is_refused()
    {
        var vm = NewSale();
        vm.Scan("111");
        f.Dialogs.Numbers.Enqueue(1.5m);

        await vm.SetQtyAsync(vm.Lines[0].Id);

        Assert.Equal("1", vm.Lines[0].Qty);
        Assert.True(vm.MessageIsError);
        Assert.Equal("This item is sold in whole units.", vm.Message);
        Assert.Equal(0, f.Dialogs.PinRequests);
    }

    [Fact]
    public async Task A_fractional_quantity_of_a_weight_item_is_accepted()
    {
        var vm = NewSale();
        vm.AddFromSearch("000089");
        f.Dialogs.Numbers.Enqueue(1.250m);

        await vm.SetQtyAsync(vm.Lines[0].Id);

        Assert.Equal("1.250 Kg", vm.Lines[0].Qty);
        Assert.False(vm.MessageIsError);
    }

    [Fact]
    public void The_selected_line_stays_selected_after_a_change()
    {
        var vm = NewSale();
        vm.Scan("111");
        vm.Scan("2000089007400");
        vm.SelectedLine = vm.Lines[0];

        vm.Increment(vm.Lines[0].Id);

        Assert.NotNull(vm.SelectedLine);
        Assert.Equal("111", vm.SelectedLine!.Barcode);
        Assert.Equal("2", vm.SelectedLine.Qty);
        Assert.Same(vm.Lines[0], vm.SelectedLine);
    }

    [Fact]
    public void Plus_on_a_scale_label_line_is_refused()
    {
        var vm = NewSale();
        vm.Scan("2000089007400");
        vm.Increment(vm.Lines[0].Id);
        Assert.True(vm.MessageIsError);
        Assert.Equal("0.740 Kg", vm.Lines[0].Qty);
    }

    [Fact]
    public async Task Voiding_the_bill_needs_a_supervisor()
    {
        var vm = NewSale();
        vm.Scan("111");
        f.Dialogs.Pins.Enqueue("9999");
        await vm.VoidBillAsync();
        Assert.Empty(vm.Lines);
        Assert.Equal(ApprovalAction.BillVoid, Assert.Single(f.Ctx.Approvals.Unsynced()).Action);
    }

    [Fact]
    public void Cart_is_restored_after_a_restart()
    {
        var first = NewSale();
        first.Scan("111");
        first.Scan("111");
        first.Scan("2000089007400");

        var second = NewSale();

        Assert.Equal(new[] { "2", "0.740 Kg" }, second.Lines.Select(l => l.Qty));
    }

    [Fact]
    public void Corrupt_autosave_starts_an_empty_bill()
    {
        f.Ctx.Kv.SetValue(SaleViewModel.AutosaveKey, "{not json");
        var vm = NewSale();
        Assert.Empty(vm.Lines);
        Assert.True(vm.MessageIsError);
        Assert.Equal("{not json", f.Ctx.Kv.GetValue("current_cart_bad"));
    }

    [Fact]
    public async Task Removing_with_a_stale_selection_does_nothing()
    {
        var vm = NewSale();
        vm.Scan("111");
        vm.SelectedLine = vm.Lines[0];
        f.Dialogs.Pins.Enqueue("9999");
        await vm.VoidBillAsync();
        await vm.RemoveSelectedCommand.ExecuteAsync(null);
        Assert.Null(vm.SelectedLine);
    }

    [Fact]
    public void Search_finds_items_and_adds_one()
    {
        var vm = NewSale();
        vm.SearchText = "cucu";
        Assert.Equal("000089", Assert.Single(vm.SearchResults).ItemCode);

        vm.AddFromSearch("000089");

        Assert.Single(vm.Lines);
        Assert.Equal("", vm.SearchText);
        Assert.Empty(vm.SearchResults);
    }

    [Fact]
    public void Pay_needs_items_and_then_opens_payment()
    {
        var vm = NewSale();
        vm.Pay(TenderKind.Cash);
        Assert.True(vm.MessageIsError);
        Assert.Null(payRequest);

        vm.Scan("111");
        vm.PayCardCommand.Execute(null);

        Assert.Equal(TenderKind.Card, payRequest!.Value.Kind);
        Assert.Equal("payment", f.Navigator.Current);
    }
}
