using TillPOS.Core.Payments;
using TillPOS.Core.Sales;
using TillPOS.Core.Security;
using TillPOS.Presentation;

namespace TillPOS.Tests.Presentation;

public sealed class ReturnViewModelTests : IDisposable
{
    private const string CucumberLabel = "2000089007400";           // 0.740 Kg of CUCUMBER/KIYAR at 3.50
    private readonly PresentationFixture f = new();
    private readonly SupervisorGate gate;
    private SaleViewModel? sale;
    private int sold;

    public ReturnViewModelTests()
    {
        f.LogInWithOpenShift();
        gate = new SupervisorGate(f.Ctx, f.Session);
    }

    public void Dispose() => f.Dispose();

    /// <summary>A card sale of the scanned codes, <paramref name="daysAgo"/> days before the fixture's clock.</summary>
    private Receipt Sell(int daysAgo, params string[] codes)
    {
        var saleContext = f.Ctx.NewSaleContext();
        var cart = new Cart(saleContext);
        foreach (var code in codes) Assert.Equal(AddOutcome.Added, cart.AddBarcode(code).Outcome);
        var plan = new PaymentCalculator(saleContext.Money).Plan(cart.Totals().GrandTotal, Tender.Card());
        var at = f.Clock.Now.AddDays(-daysAgo).AddMinutes(-60 + sold++);
        return new SaleRecorder(f.Ctx.Receipts, 2, f.Ctx.Modes, () => at).CompleteSale(cart, plan, "simran", "TILL2-SHIFT-20261007080000");
    }

    private static string[] Milk(int count) => Enumerable.Repeat("111", count).ToArray();

    private ReturnViewModel OpenReturns()
    {
        sale = new SaleViewModel(f.Ctx, f.Session, gate, (_, _) => "payment", () => "login");
        sale.Return();
        return Assert.IsType<ReturnViewModel>(f.Navigator.Current);
    }

    private ReturnViewModel Opened(Receipt receipt)
    {
        var vm = OpenReturns();
        vm.FindText = receipt.ClientId;
        vm.FindCommand.Execute(null);
        Assert.True(vm.IsChoose, vm.Message);
        return vm;
    }

    private List<Receipt> Returns() => f.Ctx.Receipts.ListPending(100).Where(r => r.Kind == ReceiptKind.Return).ToList();

    // ---- Find ----

    [Fact]
    public void Finding_by_number_opens_the_receipt_with_sold_returned_and_returnable()
    {
        var receipt = Sell(0, "111", "111", CucumberLabel);
        var vm = OpenReturns();
        Assert.True(vm.IsFind);

        vm.FindText = receipt.ClientId.ToLowerInvariant();          // typed in lower case
        vm.FindCommand.Execute(null);

        Assert.True(vm.IsChoose);
        Assert.Equal(receipt.ClientId, vm.Original!.ClientId);
        Assert.Equal(2, vm.Lines.Count);
        var milk = vm.Lines[0];
        Assert.Equal("Full Cream Milk 1L", milk.ItemName);
        Assert.Equal(("2", "0", "2", "6.79"), (milk.SoldText, milk.ReturnedText, milk.ReturnableText, milk.Rate));
        Assert.True(milk.ReturnQty.WholeNumbers);
        var cucumber = vm.Lines[1];
        Assert.Equal(("0.740 Kg", "0.740 Kg"), (cucumber.SoldText, cucumber.ReturnableText));
        Assert.False(cucumber.ReturnQty.WholeNumbers);
        Assert.Null(vm.Preview);
        Assert.Equal("", vm.NeedsText);
    }

    [Fact]
    public void A_scanned_receipt_barcode_finds_the_receipt()
    {
        var receipt = Sell(0, "111");
        var vm = OpenReturns();
        vm.Scan(receipt.ClientId);
        Assert.True(vm.IsChoose);
        Assert.Single(vm.Lines);
    }

    [Fact]
    public void An_unknown_number_says_ask_a_supervisor_for_a_return_without_receipt()
    {
        var vm = OpenReturns();
        vm.FindText = "TILL7-20261001100000-000001";
        vm.FindCommand.Execute(null);

        Assert.True(vm.IsFind);
        Assert.True(vm.MessageIsError);
        Assert.Equal("Receipt TILL7-20261001100000-000001 not found on this till. Ask a supervisor for a return without receipt.", vm.Message);
    }

    [Fact]
    public void Recent_bills_are_newest_first_and_tagged_partly_returned()
    {
        var first = Sell(0, "111", "111");
        var second = Sell(0, "111");
        var returned = Opened(first);
        returned.Lines[0].IncrementCommand.Execute(null);
        returned.SetReasonCommand.Execute("Damaged");
        returned.ConfirmCommand.Execute(null);

        var vm = OpenReturns();

        Assert.Equal(new[] { second.ClientId, first.ClientId }, vm.RecentBills.Select(b => b.ClientId));
        Assert.Equal(("", false), (vm.RecentBills[0].Returned, vm.RecentBills[0].PartlyReturned));
        Assert.Equal(("partly returned", true), (vm.RecentBills[1].Returned, vm.RecentBills[1].PartlyReturned));
        Assert.Equal("13.58", vm.RecentBills[1].Total);

        vm.OpenBillCommand.Execute(first.ClientId);
        Assert.True(vm.IsChoose);
        Assert.Equal(("1", "1"), (vm.Lines[0].ReturnedText, vm.Lines[0].ReturnableText));
    }

    [Fact]
    public void A_credit_note_or_a_fully_returned_sale_cannot_be_opened()
    {
        var receipt = Sell(0, "111");
        var first = Opened(receipt);
        first.ReturnAllCommand.Execute(null);
        first.SetReasonCommand.Execute("Expired");
        first.ConfirmCommand.Execute(null);
        var credit = Assert.Single(Returns());

        var vm = OpenReturns();
        Assert.Equal("all returned", vm.RecentBills.Single().Returned);
        vm.OpenBillCommand.Execute(receipt.ClientId);
        Assert.True(vm.IsFind);
        Assert.Equal($"Everything on {receipt.ClientId} has already been returned.", vm.Message);

        vm.Scan(credit.ClientId);
        Assert.True(vm.IsFind);
        Assert.True(vm.MessageIsError);
        Assert.Contains("credit note", vm.Message);
    }

    // ---- Choose ----

    [Fact]
    public void Choosing_part_then_all_updates_the_refund_and_pieces_take_whole_numbers_only()
    {
        var vm = Opened(Sell(0, Milk(3)));
        var milk = vm.Lines[0];

        milk.IncrementCommand.Execute(null);
        Assert.Equal("1", milk.ReturnQty.Text);
        Assert.Equal(("6.79", "6.75"), (vm.RefundTotalText, vm.RefundDueText));
        Assert.Equal(-6.75m, vm.Preview!.RefundDue);

        milk.ReturnQty.Text = "1.5";                                // refused: whole units only
        Assert.Equal("1", milk.ReturnQty.Text);

        vm.ReturnAllCommand.Execute(null);
        Assert.Equal("3", milk.ReturnQty.Text);
        Assert.Equal("20.37", vm.RefundTotalText);
        Assert.Equal("20.25", vm.RefundDueText);

        milk.IncrementCommand.Execute(null);                        // never above what can be returned
        Assert.Equal("3", milk.ReturnQty.Text);
        milk.DecrementCommand.Execute(null);
        Assert.Equal("2", milk.ReturnQty.Text);

        milk.ReturnQty.Text = "4";
        Assert.Null(vm.Preview);
        Assert.True(vm.MessageIsError);
        Assert.StartsWith("Only 3 of Full Cream Milk 1L can still be returned", vm.Message);

        milk.ReturnQty.Text = "2";
        Assert.NotNull(vm.Preview);
        Assert.False(vm.MessageIsError);
    }

    [Fact]
    public void A_scale_line_returns_part_of_its_weight()
    {
        var vm = Opened(Sell(0, CucumberLabel));
        var cucumber = vm.Lines[0];

        cucumber.ReturnQty.Text = "0.5";
        Assert.NotNull(vm.Preview);
        vm.SetReasonCommand.Execute("Damaged");
        vm.ConfirmCommand.Execute(null);

        var credit = Assert.Single(Returns());
        Assert.Equal(-0.5m, Assert.Single(credit.Lines).Qty);
        Assert.Equal("Damaged", credit.Reason);
    }

    [Fact]
    public void Scanning_a_scale_label_or_pressing_plus_takes_the_whole_remaining_weight()
    {
        var vm = Opened(Sell(0, CucumberLabel));
        vm.Scan(CucumberLabel);
        Assert.Equal(0.74m, vm.Lines[0].Chosen);
        vm.Lines[0].DecrementCommand.Execute(null);
        Assert.Equal("", vm.Lines[0].ReturnQty.Text);
        vm.Lines[0].IncrementCommand.Execute(null);
        Assert.Equal(0.74m, vm.Lines[0].Chosen);
    }

    [Fact]
    public void A_scanned_item_increments_its_line_and_a_foreign_item_gives_a_message()
    {
        f.Catalog.Items.Add(new TillPOS.Core.Catalog.Item("BREAD", "Bread", "Bakery", null, "PCS", false, true));
        f.Catalog.Barcodes.Add(new TillPOS.Core.Catalog.ItemBarcode("222", "BREAD", null));
        var vm = Opened(Sell(0, "111", "111"));

        vm.Scan("111");
        Assert.Equal("1", vm.Lines[0].ReturnQty.Text);
        vm.Scan("MILK");                                            // the item code works too
        Assert.Equal("2", vm.Lines[0].ReturnQty.Text);
        vm.Scan("111");
        Assert.Equal("2", vm.Lines[0].ReturnQty.Text);
        Assert.Equal("No more Full Cream Milk 1L left to return on this receipt", vm.Message);

        vm.Scan("222");
        Assert.True(vm.MessageIsError);
        Assert.Equal("That item is not on this receipt", vm.Message);
        Assert.Equal("2", vm.Lines[0].ReturnQty.Text);
    }

    [Fact]
    public void Confirm_needs_something_chosen_and_a_reason()
    {
        var vm = Opened(Sell(0, "111"));

        vm.ConfirmCommand.Execute(null);
        Assert.Equal("Choose what is being returned first.", vm.Message);

        vm.ReturnAllCommand.Execute(null);
        vm.ConfirmCommand.Execute(null);
        Assert.Equal("Choose a reason for the return.", vm.Message);
        Assert.True(vm.MessageIsError);
        Assert.Empty(Returns());
        Assert.Empty(f.Output.Printed);

        vm.SetReasonCommand.Execute("Not a reason");                 // only the listed reasons
        Assert.Null(vm.Reason);
        vm.SetReasonCommand.Execute("Wrong item");
        vm.ConfirmCommand.Execute(null);
        Assert.Single(Returns());
        Assert.Equal(0, f.Dialogs.PinRequests);                     // 6.79 today: no supervisor needed
        Assert.Empty(f.Ctx.Approvals.Unsynced());
    }

    // ---- Supervisor approvals ----

    [Fact]
    public async Task Over_limit_and_old_receipt_ask_once_and_log_one_row_per_reason()
    {
        var receipt = Sell(9, Milk(10));                            // 67.90, nine days ago
        var vm = Opened(receipt);
        vm.ReturnAllCommand.Execute(null);
        vm.SetReasonCommand.Execute("Changed mind");
        Assert.Equal("Needs supervisor: over AED 50 on this receipt; receipt older than 7 days", vm.NeedsText);
        f.Dialogs.Pins.Enqueue("9999");

        await vm.ConfirmAsync();

        Assert.Equal(1, f.Dialogs.PinRequests);
        var credit = Assert.Single(Returns());
        Assert.Equal("sup", credit.ApprovedBy);
        Assert.Equal(receipt.ClientId, credit.ReturnAgainst);
        var rows = f.Ctx.Approvals.Unsynced();
        Assert.Equal(new[] { ApprovalAction.ReturnOverLimit, ApprovalAction.ReturnOldReceipt }, rows.Select(r => r.Action).Order());
        Assert.All(rows, r =>
        {
            Assert.Equal("sup", r.SupervisorId);
            Assert.Equal("simran", r.CashierId);
            Assert.Equal(receipt.ClientId, r.ReceiptClientId);
            Assert.Equal("TILL2-SHIFT-20261007080000", r.ShiftClientId);
            Assert.Equal("Return: over AED 50 on this receipt; receipt older than 7 days", r.Reason);
            Assert.Equal(credit.Payments.Sum(p => p.Amount), r.Amount);
        });
    }

    [Fact]
    public async Task An_old_receipt_alone_asks()
    {
        var vm = Opened(Sell(8, "111"));
        vm.ReturnAllCommand.Execute(null);
        vm.SetReasonCommand.Execute("Expired");
        Assert.Equal("Needs supervisor: receipt older than 7 days", vm.NeedsText);
        f.Dialogs.Pins.Enqueue("9999");

        await vm.ConfirmAsync();

        Assert.Equal(ApprovalAction.ReturnOldReceipt, Assert.Single(f.Ctx.Approvals.Unsynced()).Action);
        Assert.Single(Returns());
    }

    [Fact]
    public async Task A_cashier_PIN_is_refused_and_nothing_is_saved()
    {
        var vm = Opened(Sell(9, "111"));
        vm.ReturnAllCommand.Execute(null);
        vm.SetReasonCommand.Execute("Damaged");
        f.Dialogs.Pins.Enqueue("1111");

        await vm.ConfirmAsync();

        Assert.Empty(Returns());
        Assert.Empty(f.Output.Printed);
        Assert.Equal(ApprovalAction.FailedSupervisorPin, Assert.Single(f.Ctx.Approvals.Unsynced()).Action);
        Assert.True(vm.MessageIsError);
        Assert.Same(vm, f.Navigator.Current);
        Assert.True(vm.ConfirmCommand.CanExecute(null));            // can still be approved and confirmed

        f.Dialogs.Pins.Enqueue("9999");
        await vm.ConfirmAsync();
        Assert.Single(Returns());
    }

    [Fact]
    public async Task A_second_return_shows_only_what_is_left_and_the_cumulative_limit_asks()
    {
        var receipt = Sell(0, Milk(10));
        var first = Opened(receipt);
        first.Lines[0].ReturnQty.Text = "4";                        // 27.16: under the limit
        first.SetReasonCommand.Execute("Changed mind");
        await first.ConfirmAsync();
        Assert.Equal(0, f.Dialogs.PinRequests);

        var second = Opened(receipt);
        Assert.Equal(("10", "4", "6"), (second.Lines[0].SoldText, second.Lines[0].ReturnedText, second.Lines[0].ReturnableText));
        Assert.Contains("already returned", second.Message);
        second.Lines[0].ReturnQty.Text = "7";
        Assert.StartsWith("Only 6 of", second.Message);
        second.Lines[0].ReturnQty.Text = "4";                       // another 27.16: 54.32 on this receipt
        Assert.Equal("Needs supervisor: over AED 50 on this receipt", second.NeedsText);
        second.SetReasonCommand.Execute("Changed mind");

        await second.ConfirmAsync();                                // no PIN given: refused
        Assert.Equal(1, f.Dialogs.PinRequests);
        Assert.Single(Returns());

        f.Dialogs.Pins.Enqueue("9999");
        await second.ConfirmAsync();
        Assert.Equal(2, Returns().Count);
        Assert.Equal(ApprovalAction.ReturnOverLimit, Assert.Single(f.Ctx.Approvals.Unsynced()).Action);
    }

    // ---- Without receipt ----

    [Fact]
    public async Task A_return_without_receipt_scans_items_and_always_asks()
    {
        var vm = OpenReturns();
        vm.NoReceiptCommand.Execute(null);
        Assert.True(vm.IsNoReceipt);

        vm.Scan("111");
        vm.Scan("111");
        vm.Scan(CucumberLabel);
        vm.Scan("999");
        Assert.Equal("Unknown barcode 999", vm.Message);
        Assert.Equal(2, vm.CartLines.Count);
        Assert.Equal("2", vm.CartLines[0].Qty);
        Assert.Equal("0.740 Kg", vm.CartLines[1].Qty);
        Assert.Equal("Needs supervisor: return without receipt", vm.NeedsText);

        vm.DecrementCartLineCommand.Execute(vm.CartLines[0].Id);    // no supervisor needed on the return list
        Assert.Equal("1", vm.CartLines[0].Qty);
        vm.RemoveCartLineCommand.Execute(vm.CartLines[1].Id);
        Assert.Single(vm.CartLines);
        Assert.Equal(0, f.Dialogs.PinRequests);

        vm.SetReasonCommand.Execute("Other");
        await vm.ConfirmAsync();                                    // PIN prompt closed: nothing saved
        Assert.Equal(1, f.Dialogs.PinRequests);
        Assert.Empty(Returns());

        f.Dialogs.Pins.Enqueue("9999");
        await vm.ConfirmAsync();
        var credit = Assert.Single(Returns());
        Assert.Null(credit.ReturnAgainst);
        Assert.Equal("sup", credit.ApprovedBy);
        Assert.Equal("Other", credit.Reason);
        Assert.Equal(-1m, Assert.Single(credit.Lines).Qty);
        var row = Assert.Single(f.Ctx.Approvals.Unsynced());
        Assert.Equal(ApprovalAction.ReturnWithoutReceipt, row.Action);
        Assert.Null(row.ReceiptClientId);
    }

    [Fact]
    public void A_return_without_receipt_finds_items_by_search_and_adds_one()
    {
        var vm = OpenReturns();
        vm.NoReceiptCommand.Execute(null);

        vm.SearchText = "c";                                        // one letter: no search yet
        Assert.Empty(vm.SearchResults);
        vm.SearchText = "cucu";
        Assert.Equal("000089", Assert.Single(vm.SearchResults).ItemCode);

        vm.AddFromSearchCommand.Execute("000089");

        var line = Assert.Single(vm.CartLines);
        Assert.Equal("CUCUMBER/KIYAR", line.Name);
        Assert.Equal("", vm.SearchText);
        Assert.Empty(vm.SearchResults);
        Assert.False(vm.MessageIsError);
        Assert.Equal("Needs supervisor: return without receipt", vm.NeedsText);
        Assert.NotEqual("", vm.RefundDueText);
    }

    [Fact]
    public void Adding_an_unknown_item_from_search_gives_a_message()
    {
        var vm = OpenReturns();
        vm.NoReceiptCommand.Execute(null);

        vm.AddFromSearchCommand.Execute("NOPE");

        Assert.Empty(vm.CartLines);
        Assert.True(vm.MessageIsError);
        Assert.Equal("Unknown item NOPE", vm.Message);
    }

    [Fact]
    public void Search_adds_nothing_outside_the_without_receipt_stage_and_is_cleared_on_back()
    {
        var receipt = Sell(0, "111");
        var vm = Opened(receipt);
        vm.AddFromSearchCommand.Execute("000089");
        Assert.Single(vm.Lines);
        Assert.Equal("", vm.Lines[0].ReturnQty.Text);

        vm.BackCommand.Execute(null);
        vm.NoReceiptCommand.Execute(null);
        vm.SearchText = "cucu";
        Assert.Single(vm.SearchResults);
        vm.BackCommand.Execute(null);
        Assert.Equal("", vm.SearchText);
        Assert.Empty(vm.SearchResults);
    }

    [Fact]
    public void A_return_without_receipt_needs_items()
    {
        var vm = OpenReturns();
        vm.NoReceiptCommand.Execute(null);
        vm.SetReasonCommand.Execute("Other");
        vm.ConfirmCommand.Execute(null);
        Assert.Equal("Scan the items being returned first.", vm.Message);
        Assert.Equal(0, f.Dialogs.PinRequests);
    }

    // ---- Refund, print, popup ----

    [Fact]
    public void The_refund_is_saved_then_printed_with_the_drawer_and_the_popup_is_shown()
    {
        var vm = Opened(Sell(0, "111"));
        vm.ReturnAllCommand.Execute(null);
        vm.SetReasonCommand.Execute("Damaged");
        Receipt? savedWhenPrinted = null;
        f.Output.OnPrint = () => savedWhenPrinted = Returns().SingleOrDefault();

        vm.ConfirmCommand.Execute(null);

        var credit = Assert.Single(Returns());
        Assert.Equal(credit.ClientId, savedWhenPrinted?.ClientId);  // saved before the drawer opens
        Assert.Equal("simran", credit.Cashier);
        Assert.Equal("p.simran@quickgroc.com", credit.CashierUser);
        Assert.Equal("Simran", credit.CashierName);
        Assert.Equal("TILL2-SHIFT-20261007080000", credit.ShiftClientId);
        Assert.Equal(new[] { new ReceiptPayment("Cash Counter 2", -6.75m) }, credit.Payments);
        var (printed, drawer, copy) = Assert.Single(f.Output.Printed);
        Assert.Equal(credit.ClientId, printed.ClientId);
        Assert.True(drawer);
        Assert.False(copy);
        var (shown, printError) = Assert.Single(f.Dialogs.Receipts);
        Assert.Equal(credit.ClientId, shown.ClientId);
        Assert.Null(printError);
        Assert.Same(sale, f.Navigator.Current);
        Assert.Equal($"Refund 6.75 — credit note {credit.ClientId}", sale!.Message);
        Assert.False(sale.MessageIsError);
        Assert.Equal($"{credit.ClientId}|1", f.Ctx.Kv.GetValue(SaleViewModel.LastReceiptKey));
        Assert.False(vm.ConfirmCommand.CanExecute(null));
        Assert.False(vm.BackCommand.CanExecute(null));

        Assert.Null(f.Dialogs.LastReprint!());                      // Print again in the popup: a COPY, no drawer
        Assert.Equal((false, true), (f.Output.Printed[^1].OpenDrawer, f.Output.Printed[^1].Copy));
    }

    [Fact]
    public void A_printer_failure_still_saves_the_return()
    {
        var vm = Opened(Sell(0, "111"));
        vm.ReturnAllCommand.Execute(null);
        vm.SetReasonCommand.Execute("Damaged");
        f.Output.Fail = true;

        vm.ConfirmCommand.Execute(null);

        var credit = Assert.Single(Returns());
        Assert.Equal("Printer offline", Assert.Single(f.Dialogs.Receipts).PrintError);
        Assert.True(sale!.MessageIsError);
        Assert.StartsWith($"Refund 6.75 — credit note {credit.ClientId}, but the printer failed (Printer offline)", sale.Message);
        Assert.Equal($"{credit.ClientId}|0", f.Ctx.Kv.GetValue(SaleViewModel.LastReceiptKey));

        f.Output.Fail = false;                                      // the first successful print is not a COPY
        Assert.Null(f.Dialogs.LastReprint!());
        Assert.False(Assert.Single(f.Output.Printed).Copy);
    }

    [Fact]
    public async Task A_double_confirm_saves_one_credit_note()
    {
        var vm = Opened(Sell(9, "111", "111"));                      // needs a supervisor: the PIN prompt stays open
        vm.Lines[0].IncrementCommand.Execute(null);                 // one of two: a second save would still fit
        vm.SetReasonCommand.Execute("Damaged");
        var pin = new TaskCompletionSource<string?>();
        f.Dialogs.PendingPin = pin;

        var firstPress = vm.ConfirmAsync();
        Assert.False(vm.ConfirmCommand.CanExecute(null));
        await vm.ConfirmAsync();                                    // a second press while the PIN prompt is open
        pin.SetResult("9999");
        await firstPress;
        await vm.ConfirmAsync();                                    // and one after it finished

        Assert.Equal(1, f.Dialogs.PinRequests);
        Assert.Single(Returns());
        Assert.Single(f.Output.Printed);
    }

    [Fact]
    public void Quick_confirm_presses_without_a_supervisor_save_once()
    {
        var vm = Opened(Sell(0, "111", "111"));
        vm.Lines[0].IncrementCommand.Execute(null);                 // one of two: a second save would still fit
        vm.SetReasonCommand.Execute("Damaged");
        vm.ConfirmCommand.Execute(null);
        vm.ConfirmCommand.Execute(null);
        vm.Scan("111");
        Assert.Equal("1", vm.Lines[0].ReturnQty.Text);              // the screen is finished: scans are ignored
        Assert.Single(Returns());
    }

    [Fact]
    public void A_barcode_scanned_to_close_the_popup_goes_on_the_next_bill()
    {
        var vm = Opened(Sell(0, "111"));
        vm.ReturnAllCommand.Execute(null);
        vm.SetReasonCommand.Execute("Damaged");
        f.Dialogs.ReceiptScans.Enqueue("111");

        vm.ConfirmCommand.Execute(null);

        Assert.Single(sale!.Lines);
    }

    // ---- Entry point, back, reprint ----

    [Fact]
    public void F6_is_refused_while_the_bill_has_lines()
    {
        var s = new SaleViewModel(f.Ctx, f.Session, gate, (_, _) => "payment", () => "login");
        f.Navigator.Show(s);
        s.Scan("111");

        s.ReturnCommand.Execute(null);

        Assert.Same(s, f.Navigator.Current);
        Assert.True(s.MessageIsError);
        Assert.Equal("Finish, hold or void the current bill first", s.Message);
    }

    [Fact]
    public void Esc_goes_back_a_stage_then_to_the_same_sale_screen()
    {
        var vm = Opened(Sell(0, "111"));
        vm.BackCommand.Execute(null);
        Assert.True(vm.IsFind);
        Assert.Empty(vm.Lines);
        Assert.Same(vm, f.Navigator.Current);

        vm.BackCommand.Execute(null);
        Assert.Same(sale, f.Navigator.Current);
        Assert.Empty(Returns());
    }

    [Fact]
    public void Ctrl_P_after_a_return_reprints_the_credit_note_as_a_copy()
    {
        var vm = Opened(Sell(0, "111"));
        vm.ReturnAllCommand.Execute(null);
        vm.SetReasonCommand.Execute("Damaged");
        vm.ConfirmCommand.Execute(null);
        var credit = Assert.Single(Returns());

        sale!.ReprintLastCommand.Execute(null);

        var (printed, drawer, copy) = f.Output.Printed[^1];
        Assert.Equal(credit.ClientId, printed.ClientId);
        Assert.False(drawer);
        Assert.True(copy);
    }
}
