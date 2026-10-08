using TillPOS.Core.Catalog;
using TillPOS.Core.Payments;
using TillPOS.Core.Sales;
using TillPOS.Core.Security;
using TillPOS.Presentation;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Presentation;

public sealed class DeliveryFlowTests : IDisposable
{
    private readonly PresentationFixture f = new();
    private readonly object login = new();
    private readonly SaleViewModel sale;

    public DeliveryFlowTests()
    {
        f.LogInWithOpenShift();
        sale = new SaleViewModel(f.Ctx, f.Session, new SupervisorGate(f.Ctx, f.Session),
            (s, kind) => new PaymentViewModel(f.Ctx, f.Session, s, kind), () => login);
        f.Navigator.Show(sale);
    }

    public void Dispose() => f.Dispose();

    /// <summary>Two milks (13.58) made into a delivery; returns it.</summary>
    private Delivery Make()
    {
        sale.Scan("111");
        sale.Scan("111");
        f.Dialogs.ConfirmAnswers.Enqueue(true);
        sale.MakeDelivery();
        return Assert.Single(f.Ctx.Deliveries.Open());
    }

    private DeliveriesViewModel Open(string? id = null)
    {
        sale.OpenDeliveries(id);
        return Assert.IsType<DeliveriesViewModel>(f.Navigator.Current);
    }

    [Fact]
    public void Making_a_delivery_saves_it_prints_the_slip_and_clears_the_bill()
    {
        var d = Make();

        Assert.StartsWith("TILL2-", d.ClientId);
        Assert.Equal(M("13.58"), d.Bill.GrandTotal);
        Assert.Equal(M("13.50"), d.CashToCollect);
        Assert.Equal(M("13.58"), d.CardToCollect);
        var (printed, copy) = Assert.Single(f.Output.Deliveries);
        Assert.Equal(d.ClientId, printed.ClientId);
        Assert.False(copy);
        Assert.Empty(f.Output.Printed);                            // no receipt, no drawer
        Assert.Empty(sale.Lines);
        Assert.Equal("[]", f.Ctx.Kv.GetValue(SaleViewModel.AutosaveKey));
        Assert.Equal(1, sale.DeliveryCount);
        Assert.Empty(f.Ctx.Receipts.ListPending(10));              // nothing to upload
    }

    [Fact]
    public void Declining_the_confirmation_keeps_the_bill()
    {
        sale.Scan("111");
        f.Dialogs.ConfirmAnswers.Enqueue(false);
        sale.MakeDelivery();
        Assert.Empty(f.Ctx.Deliveries.Open());
        Assert.Single(sale.Lines);
    }

    [Fact]
    public void An_empty_bill_cannot_become_a_delivery()
    {
        sale.MakeDelivery();
        Assert.True(sale.MessageIsError);
        Assert.Empty(f.Dialogs.Confirms);
    }

    [Fact]
    public void A_printer_failure_still_saves_the_delivery()
    {
        f.Output.Fail = true;
        var d = Make();
        Assert.Contains("Reprint it from Deliveries", sale.Message);
        Assert.Equal(DeliveryStatus.Open, f.Ctx.Deliveries.Get(d.ClientId)!.Status);
    }

    [Fact]
    public void The_list_shows_open_deliveries_and_selects_the_first()
    {
        var d = Make();
        var vm = Open();

        var row = Assert.Single(vm.Rows);
        Assert.Equal(d.ClientId, row.ClientId);
        Assert.Equal("13.58", row.Amount);
        Assert.Equal(d.ClientId, vm.SelectedId);
        Assert.Equal(2m, Assert.Single(vm.Cart.Lines).Qty);
    }

    [Fact]
    public void Paying_cash_is_exact_saves_a_delivery_sale_in_this_shift_and_closes_it()
    {
        var d = Make();
        var vm = Open();
        f.Clock.Now = f.Clock.Now.AddHours(1);

        vm.PayCashCommand.Execute(null);
        var pay = Assert.IsType<PaymentViewModel>(f.Navigator.Current);
        Assert.Equal("0.00", pay.Change);
        pay.CompleteCommand.Execute(null);

        var receipt = Assert.Single(f.Ctx.Receipts.ListPending(10));
        Assert.Equal(d.ClientId, receipt.ClientId);
        Assert.True(receipt.IsDelivery);
        Assert.Equal(f.Clock.Now, receipt.CreatedAt);
        Assert.Equal("TILL2-SHIFT-20261007080000", receipt.ShiftClientId);
        Assert.Equal(new[] { new ReceiptPayment("Cash Counter 2", M("13.50")) }, receipt.Payments);
        Assert.Equal(DeliveryStatus.Paid, f.Ctx.Deliveries.Get(d.ClientId)!.Status);
        Assert.Same(sale, f.Navigator.Current);
        Assert.True(Assert.Single(f.Output.Printed).OpenDrawer);
        Assert.Equal(0, sale.DeliveryCount);
    }

    [Fact]
    public void Paying_by_card_charges_the_total_to_two_decimals()
    {
        Make();
        var vm = Open();
        vm.PayCardCommand.Execute(null);
        Assert.IsType<PaymentViewModel>(f.Navigator.Current).CompleteCommand.Execute(null);

        Assert.Equal(new[] { new ReceiptPayment("Credit Card", M("13.58")) }, Assert.Single(f.Ctx.Receipts.ListPending(10)).Payments);
    }

    [Fact]
    public void Paying_uses_the_delivery_prices_after_a_price_change()
    {
        Make();
        f.Catalog.Prices.Clear();
        f.Catalog.Prices.Add(new ItemPrice("P-MILK", "MILK", "PCS", M("20.00"), null, null));

        var vm = Open();
        vm.PayCardCommand.Execute(null);
        Assert.IsType<PaymentViewModel>(f.Navigator.Current).CompleteCommand.Execute(null);

        Assert.Equal(M("13.58"), Assert.Single(f.Ctx.Receipts.ListPending(10)).GrandTotal);
    }

    [Fact]
    public void Paying_in_a_shift_at_another_counter_uses_that_counters_modes()
    {
        // Its own fixture: the shared one always opens the same shift id. The delivery was made at Counter 2; the shift now
        // open is at Test Counter ("Cash Counter 1").
        using var g = new PresentationFixture();
        g.LogInWithOpenShift(PresentationFixture.TestCounter);
        var made = new Cart(g.Ctx.NewSaleContextFor("Al Ain Counter 2"));
        made.AddBarcode("111");
        var d = Deliveries.Make(made, "TILL2-20261007090000-000077", g.Clock.Now.AddHours(-1), "TILL2-SHIFT-OLD", "simran", "Simran", "Counter 2");
        g.Ctx.Deliveries.Add(d);
        var here = new SaleViewModel(g.Ctx, g.Session, new SupervisorGate(g.Ctx, g.Session),
            (s, kind) => new PaymentViewModel(g.Ctx, g.Session, s, kind), () => login);

        here.OpenDeliveries(d.ClientId);
        Assert.IsType<DeliveriesViewModel>(g.Navigator.Current).PayCashCommand.Execute(null);
        Assert.IsType<PaymentViewModel>(g.Navigator.Current).CompleteCommand.Execute(null);

        var receipt = g.Ctx.Receipts.Get(d.ClientId)!;
        Assert.Equal("Cash Counter 1", Assert.Single(receipt.Payments).ModeOfPayment);
        Assert.Equal("Test Counter", receipt.PosProfile);
        Assert.Equal("TILL2-SHIFT-20261007080000", receipt.ShiftClientId);
    }

    [Fact]
    public void Back_from_the_payment_screen_returns_to_the_list_and_the_delivery_stays_open()
    {
        var d = Make();
        var vm = Open();
        vm.PayCashCommand.Execute(null);
        Assert.IsType<PaymentViewModel>(f.Navigator.Current).BackCommand.Execute(null);

        Assert.Same(vm, f.Navigator.Current);
        Assert.Equal(DeliveryStatus.Open, f.Ctx.Deliveries.Get(d.ClientId)!.Status);
    }

    [Fact]
    public async Task Reducing_a_line_asks_how_many_to_keep_and_needs_one_supervisor_approval()
    {
        var d = Make();
        var vm = Open();
        f.Dialogs.Numbers.Enqueue(1m);
        f.Dialogs.Pins.Enqueue("9999");

        await vm.ReduceLineAsync(vm.Cart.Lines[0].Id);

        var changed = f.Ctx.Deliveries.Get(d.ClientId)!;
        Assert.Equal(M("6.79"), changed.Bill.GrandTotal);
        Assert.Equal(M("6.75"), changed.CashToCollect);
        Assert.Single(f.Ctx.Approvals.Unsynced(), a => a.Action == ApprovalAction.DeliveryChange);
        Assert.Empty(f.Dialogs.Pins);
    }

    [Fact]
    public async Task Cancelling_the_keep_prompt_changes_nothing_and_asks_no_pin()
    {
        var d = Make();
        var vm = Open();
        f.Dialogs.Numbers.Enqueue(null);
        f.Dialogs.Pins.Enqueue("9999");

        await vm.ReduceLineAsync(vm.Cart.Lines[0].Id);

        Assert.Equal(M("13.58"), f.Ctx.Deliveries.Get(d.ClientId)!.Bill.GrandTotal);
        Assert.Single(f.Dialogs.Pins);                                // never asked
        Assert.Empty(f.Ctx.Approvals.Unsynced());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("2")]
    [InlineData("3")]
    [InlineData("1.5")]
    public async Task Keep_values_that_do_not_reduce_change_nothing_and_ask_no_pin(string keep)
    {
        var d = Make();
        var vm = Open();
        f.Dialogs.Numbers.Enqueue(decimal.Parse(keep, System.Globalization.CultureInfo.InvariantCulture));
        f.Dialogs.Pins.Enqueue("9999");

        await vm.ReduceLineAsync(vm.Cart.Lines[0].Id);

        Assert.Equal(M("13.58"), f.Ctx.Deliveries.Get(d.ClientId)!.Bill.GrandTotal);
        Assert.Single(f.Dialogs.Pins);
        Assert.NotEqual("", vm.Message);
    }

    [Fact]
    public async Task A_refused_pin_on_reduce_changes_nothing()
    {
        var d = Make();
        var vm = Open();
        f.Dialogs.Numbers.Enqueue(1m);
        f.Dialogs.Pins.Enqueue("1111");

        await vm.ReduceLineAsync(vm.Cart.Lines[0].Id);

        Assert.Equal(M("13.58"), f.Ctx.Deliveries.Get(d.ClientId)!.Bill.GrandTotal);
        Assert.Equal(2m, Assert.Single(vm.Cart.Lines).Qty);
    }

    [Fact]
    public async Task A_refused_pin_changes_nothing()
    {
        var d = Make();
        var vm = Open();
        f.Dialogs.Pins.Enqueue("1111");                              // a cashier PIN

        await vm.RemoveLineAsync(vm.Cart.Lines[0].Id);

        Assert.Equal(M("13.58"), f.Ctx.Deliveries.Get(d.ClientId)!.Bill.GrandTotal);
        Assert.Single(vm.Cart.Lines);
    }

    [Fact]
    public async Task Removing_the_last_line_cancels_the_delivery()
    {
        var d = Make();
        var vm = Open();
        f.Dialogs.Pins.Enqueue("9999");

        await vm.RemoveLineAsync(vm.Cart.Lines[0].Id);

        Assert.Equal(DeliveryStatus.Cancelled, f.Ctx.Deliveries.Get(d.ClientId)!.Status);
        Assert.Empty(vm.Rows);
        var approval = Assert.Single(f.Ctx.Approvals.Unsynced());
        Assert.Equal(ApprovalAction.DeliveryCancel, approval.Action);
        Assert.Contains("all items removed", approval.Reason);
    }

    [Fact]
    public async Task Removing_one_of_two_lines_is_logged_as_a_change()
    {
        f.Catalog.Items.Add(new Item("RICE", "Basmati Rice 5Kg", "Rice", null, "PCS", false, true));
        f.Catalog.Prices.Add(new ItemPrice("P-RICE", "RICE", "PCS", M("40.00"), null, null));
        f.Catalog.Barcodes.Add(new ItemBarcode("222", "RICE", null));
        sale.Scan("111");
        sale.Scan("222");
        f.Dialogs.ConfirmAnswers.Enqueue(true);
        sale.MakeDelivery();
        var vm = Open();
        f.Dialogs.Pins.Enqueue("9999");

        await vm.RemoveLineAsync(vm.Cart.Lines[0].Id);

        Assert.Equal(ApprovalAction.DeliveryChange, Assert.Single(f.Ctx.Approvals.Unsynced()).Action);
    }

    [Fact]
    public void A_delivery_that_cannot_be_saved_leaves_the_cart_in_the_autosave()
    {
        sale.Scan("111");
        sale.Scan("111");
        using (var conn = f.Temp.Db.Open())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TRIGGER no_add BEFORE INSERT ON delivery BEGIN SELECT RAISE(ABORT, 'locked'); END";
            cmd.ExecuteNonQuery();
        }
        f.Dialogs.ConfirmAnswers.Enqueue(true);

        sale.MakeDelivery();

        Assert.True(sale.MessageIsError);
        Assert.Contains("Could not save the delivery", sale.Message);
        Assert.Single(sale.Lines);
        Assert.NotEqual("[]", f.Ctx.Kv.GetValue(SaleViewModel.AutosaveKey));
        Assert.Empty(f.Ctx.Deliveries.Open());
    }

    [Fact]
    public void Paying_a_delivery_by_split_saves_both_payments_and_closes_it()
    {
        var d = Make();
        var vm = Open();
        vm.PaySplitCommand.Execute(null);
        var pay = Assert.IsType<PaymentViewModel>(f.Navigator.Current);
        pay.Card.Set(5m);
        pay.Cash.Set(9m);
        pay.CompleteCommand.Execute(null);

        var receipt = Assert.Single(f.Ctx.Receipts.ListPending(10));
        Assert.Equal(d.ClientId, receipt.ClientId);
        Assert.Equal(2, receipt.Payments.Count);
        Assert.Contains(receipt.Payments, p => p.ModeOfPayment == "Credit Card" && p.Amount == M("5"));
        Assert.Contains(receipt.Payments, p => p.ModeOfPayment == "Cash Counter 2" && p.Amount > 0m);
        Assert.Equal(DeliveryStatus.Paid, f.Ctx.Deliveries.Get(d.ClientId)!.Status);
    }

    [Theory]
    [InlineData("Refused")]
    [InlineData("Not delivered")]
    [InlineData("Other")]
    public async Task Cancel_needs_a_supervisor_and_records_the_reason(string reason)
    {
        var d = Make();
        var vm = Open();
        f.Dialogs.Pins.Enqueue("9999");

        await vm.CancelAsync(reason);

        var cancelled = f.Ctx.Deliveries.Get(d.ClientId)!;
        Assert.Equal((DeliveryStatus.Cancelled, reason, "sup"), (cancelled.Status, cancelled.Reason, cancelled.ClosedBy));
        Assert.Single(f.Ctx.Approvals.Unsynced(), a => a.Action == ApprovalAction.DeliveryCancel);
        Assert.Empty(f.Ctx.Receipts.ListPending(10));
    }

    [Fact]
    public void Reprint_prints_the_slip_marked_copy()
    {
        Make();
        Open().ReprintCommand.Execute(null);
        Assert.True(f.Output.Deliveries[^1].Copy);
    }

    [Fact]
    public void Scanning_a_delivery_slip_on_an_empty_bill_opens_it()
    {
        var d = Make();
        sale.Scan(d.ClientId);
        Assert.Equal(d.ClientId, Assert.IsType<DeliveriesViewModel>(f.Navigator.Current).SelectedId);
        Assert.Empty(sale.Lines);
    }

    [Fact]
    public void Scanning_a_slip_with_items_on_the_bill_asks_to_finish_first()
    {
        var d = Make();
        sale.Scan("111");
        sale.Scan(d.ClientId);
        Assert.Same(sale, f.Navigator.Current);
        Assert.Equal("Finish, hold or void the current bill first, then scan the delivery slip", sale.Message);
        Assert.Single(sale.Lines);
    }

    [Fact]
    public void Scanning_a_paid_delivery_slip_says_so()
    {
        var d = Make();
        var vm = Open();
        vm.PayCardCommand.Execute(null);
        Assert.IsType<PaymentViewModel>(f.Navigator.Current).CompleteCommand.Execute(null);
        f.Navigator.Show(sale);

        sale.Scan(d.ClientId);

        Assert.Equal($"Delivery {d.ClientId} is already paid", sale.Message);
        Assert.Empty(sale.Lines);
    }

    [Fact]
    public async Task The_z_report_lists_deliveries_paid_and_still_out()
    {
        var paid = Make();
        var vm = Open();
        vm.PayCardCommand.Execute(null);
        Assert.IsType<PaymentViewModel>(f.Navigator.Current).CompleteCommand.Execute(null);
        var stillOut = Make();

        sale.CloseShift();
        var close = Assert.IsType<CloseShiftViewModel>(f.Navigator.Current);
        close.UseTotalInstead.Text = "200";
        close.CardTotal.Text = "13.58";
        f.Dialogs.Pins.Enqueue("9999");
        await close.CloseAsync();

        var summary = Assert.Single(f.Output.ShiftReports).Deliveries!;
        Assert.Equal((1, M("13.58")), (summary.PaidCount, summary.PaidTotal));
        Assert.Equal(stillOut.ClientId, Assert.Single(summary.StillOut).ClientId);
        Assert.Equal(DeliveryStatus.Open, f.Ctx.Deliveries.Get(stillOut.ClientId)!.Status);   // stays for the next shift
    }

    [Fact]
    public void The_shift_can_close_with_deliveries_out()
    {
        Make();
        sale.CloseShift();
        Assert.IsType<CloseShiftViewModel>(f.Navigator.Current);
    }

    [Fact]
    public async Task A_failing_store_leaves_the_screen_matching_what_is_stored()
    {
        var d = Make();
        var vm = Open();
        using (var conn = f.Temp.Db.Open())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TRIGGER no_update BEFORE UPDATE ON delivery BEGIN SELECT RAISE(ABORT, 'locked'); END";
            cmd.ExecuteNonQuery();
        }
        f.Dialogs.Pins.Enqueue("9999");

        await vm.RemoveLineAsync(vm.Cart.Lines[0].Id);       // last line: cancel, which updates the row

        Assert.True(vm.MessageIsError);
        Assert.Contains("Could not cancel", vm.Message);
        Assert.Equal(DeliveryStatus.Open, f.Ctx.Deliveries.Get(d.ClientId)!.Status);
        Assert.Equal(2m, Assert.Single(vm.Cart.Lines).Qty);
        Assert.Single(vm.SelectedLines);

        f.Dialogs.Numbers.Enqueue(1m);
        f.Dialogs.Pins.Enqueue("9999");
        await vm.ReduceLineAsync(vm.Cart.Lines[0].Id);

        Assert.Contains("Could not save the change", vm.Message);
        Assert.Equal(2m, Assert.Single(vm.Cart.Lines).Qty);
        Assert.Equal("13.58", Assert.Single(vm.Rows).Amount);
        Assert.Equal(M("13.58"), f.Ctx.Deliveries.Get(d.ClientId)!.Bill.GrandTotal);
    }

    [Fact]
    public void Scanning_in_the_list_selects_open_and_explains_paid_cancelled_and_unknown()
    {
        var a = Make();
        sale.Scan("111");
        f.Dialogs.ConfirmAnswers.Enqueue(true);
        sale.MakeDelivery();
        var b = f.Ctx.Deliveries.Open().Single(x => x.ClientId != a.ClientId);
        var vm = Open();

        vm.ScanCommand.Execute($" {b.ClientId} ");
        Assert.Equal(b.ClientId, vm.SelectedId);

        vm.Scan("TILL2-NOPE");
        Assert.Equal("No open delivery TILL2-NOPE on this till", vm.Message);
        Assert.True(vm.MessageIsError);

        f.Ctx.Deliveries.Cancel(a.ClientId, f.Clock.Now, "sup", "Other");
        vm.Scan(a.ClientId);
        Assert.Equal($"Delivery {a.ClientId} is already cancelled", vm.Message);

        vm.SelectedId = b.ClientId;
        vm.PayCardCommand.Execute(null);
        Assert.IsType<PaymentViewModel>(f.Navigator.Current).CompleteCommand.Execute(null);
        vm.Scan(b.ClientId);
        Assert.Equal($"Delivery {b.ClientId} is already paid", vm.Message);
    }

    [Fact]
    public void Pay_is_refused_without_an_open_shift_but_the_list_still_shows()
    {
        Make();
        var vm = Open();
        f.Session.Shift = null;

        vm.PayCashCommand.Execute(null);

        Assert.Same(vm, f.Navigator.Current);
        Assert.Equal("Open a shift first", vm.Message);
        Assert.Single(vm.Rows);
    }
}
