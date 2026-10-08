using TillPOS.Core.Catalog;
using TillPOS.Core.Payments;
using TillPOS.Core.Pricing;
using TillPOS.Core.Sales;
using TillPOS.Core.Security;
using TillPOS.Presentation;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Presentation;

/// <summary>Price check (F4), hold (F5), recall (F7) with supervisor-gated delete, and reprint last (Ctrl+P).</summary>
public sealed class PriceHoldRecallTests : IDisposable
{
    private readonly PresentationFixture f = new();

    public PriceHoldRecallTests()
    {
        f.LogInWithOpenShift();
        f.Catalog.Items.Add(new Item("RICE", "Basmati Rice 5Kg", "Rice", null, "PCS", false, true));
        f.Catalog.Prices.Add(new ItemPrice("P-RICE", "RICE", "PCS", M("40.00"), null, null));
        f.Catalog.Barcodes.Add(new ItemBarcode("222", "RICE", null));
        f.Catalog.Rules.Add(new PricingRule("R-RICE", RuleApplyOn.ItemCode, ["RICE"], RuleKind.DiscountPercentage, 10m, 0, null, null, null, null, null));
        f.Catalog.Items.Add(new Item("NOPRICE", "Unpriced Thing", "Household", null, "PCS", false, true));
        f.Catalog.Barcodes.Add(new ItemBarcode("333", "NOPRICE", null));
    }

    public void Dispose() => f.Dispose();

    private SupervisorGate Gate() => new(f.Ctx, f.Session);

    private SaleViewModel NewSale() =>
        new(f.Ctx, f.Session, Gate(), (sale, kind) => new PaymentViewModel(f.Ctx, f.Session, sale, kind), () => "login");

    // ---- Price check ----

    [Fact]
    public void Price_check_by_barcode_shows_the_price_and_leaves_the_bill_alone()
    {
        var sale = NewSale();
        sale.Scan("111");
        var autosave = f.Ctx.Kv.GetValue(SaleViewModel.AutosaveKey);
        var pc = new PriceCheckViewModel(f.Ctx);

        pc.Lookup("111");
        pc.Lookup("111");

        Assert.True(pc.HasResult);
        Assert.Equal("Full Cream Milk 1L", pc.Name);
        Assert.Equal("MILK", pc.ItemCode);
        Assert.Equal("111", pc.Barcode);
        Assert.Equal("per PCS", pc.UnitText);
        Assert.Equal("6.79", pc.PriceText);
        Assert.Equal("", pc.OfferText);
        Assert.Equal("", pc.ScaleText);
        Assert.Equal("", pc.Message);
        Assert.Equal(new PriceCheckPick("111", false), pc.AddToBill);
        Assert.Equal("111", pc.AddToBillCode);
        Assert.Equal("1", Assert.Single(sale.Lines).Qty);
        Assert.Single(sale.Cart.Lines);
        Assert.Equal(autosave, f.Ctx.Kv.GetValue(SaleViewModel.AutosaveKey));
    }

    [Fact]
    public void Price_check_shows_an_offer_below_the_list_price()
    {
        var pc = new PriceCheckViewModel(f.Ctx);
        pc.Lookup("222");
        Assert.Equal("40.00", pc.PriceText);
        Assert.Equal("Offer: 36.00 (10% OFF)", pc.OfferText);
    }

    [Fact]
    public void Price_check_of_a_scale_label_shows_weight_times_price()
    {
        var pc = new PriceCheckViewModel(f.Ctx);
        pc.Lookup("2000089007400");
        Assert.True(pc.HasResult);
        Assert.Equal("CUCUMBER/KIYAR", pc.Name);
        Assert.Equal("per Kg", pc.UnitText);
        Assert.Equal("3.50", pc.PriceText);
        Assert.Equal("0.740 Kg × 3.50 = 2.59", pc.ScaleText);
        Assert.Equal("2000089007400", pc.AddToBillCode);
    }

    [Fact]
    public void Price_check_of_an_unknown_or_unpriced_code_gives_a_message_and_clears_the_result()
    {
        var pc = new PriceCheckViewModel(f.Ctx);
        pc.Lookup("111");

        pc.Lookup("123");
        Assert.False(pc.HasResult);
        Assert.Equal("Unknown barcode 123", pc.Message);
        Assert.Equal("", pc.Name);
        Assert.Equal("", pc.PriceText);
        Assert.Null(pc.AddToBill);

        pc.Lookup("333");
        Assert.False(pc.HasResult);
        Assert.Equal("No price for this item", pc.Message);
        Assert.Null(pc.AddToBillCode);
    }

    [Fact]
    public void Price_check_search_and_select()
    {
        var pc = new PriceCheckViewModel(f.Ctx);
        pc.SearchText = "c";
        Assert.Empty(pc.SearchResults);
        pc.SearchText = "cucu";
        Assert.Equal("000089", Assert.Single(pc.SearchResults).ItemCode);

        pc.Select("000089");

        Assert.True(pc.HasResult);
        Assert.Equal("000089", pc.ItemCode);
        Assert.Equal("3.50", pc.PriceText);
        Assert.Equal("", pc.ScaleText);
        Assert.Equal(new PriceCheckPick("000089", true), pc.AddToBill);
        Assert.Equal("", pc.SearchText);
        Assert.Empty(pc.SearchResults);
    }

    [Fact]
    public void Add_to_bill_adds_through_the_sale_and_closing_adds_nothing()
    {
        var sale = NewSale();
        f.Dialogs.OnPriceCheck = vm => { vm.Lookup("111"); return null; };
        sale.PriceCheckCommand.Execute(null);
        Assert.Empty(sale.Lines);

        f.Dialogs.OnPriceCheck = vm => { vm.Lookup("2000089007400"); return vm.AddToBill; };
        sale.PriceCheck();
        f.Dialogs.OnPriceCheck = vm => { vm.SearchText = "milk"; vm.Select("MILK"); return vm.AddToBill; };
        sale.PriceCheck();

        Assert.Equal(3, f.Dialogs.PriceCheckRequests);
        Assert.Equal(new[] { "0.740 Kg", "1" }, sale.Lines.Select(l => l.Qty));
        Assert.Equal("2000089007400", sale.Lines[0].Barcode);
        Assert.Null(sale.Lines[1].Barcode);
        Assert.Equal("Added Full Cream Milk 1L", sale.Message);
    }

    // ---- Hold ----

    [Fact]
    public void Holding_an_empty_bill_is_refused()
    {
        var sale = NewSale();
        sale.HoldCommand.Execute(null);
        Assert.True(sale.MessageIsError);
        Assert.Empty(f.Ctx.Held.List());
        Assert.Equal(0, sale.HeldCount);
    }

    [Fact]
    public void Hold_clears_the_bill_and_the_autosave_and_writes_the_label()
    {
        var sale = NewSale();
        sale.Scan("111");
        sale.Scan("111");
        sale.Scan("2000089007400");

        sale.Hold();

        Assert.Empty(sale.Lines);
        Assert.Equal("0.00", sale.Total);
        Assert.Equal("Bill put on hold", sale.Message);
        Assert.False(sale.MessageIsError);
        Assert.Equal(1, sale.HeldCount);
        var held = Assert.Single(f.Ctx.Held.List());
        Assert.Equal("10:00 · Simran · 2 items · 16.17", held.Label);
        Assert.Equal(f.Clock.Now, held.HeldAt);
        Assert.Equal(2, held.Lines.Count);
        Assert.Equal("[]", f.Ctx.Kv.GetValue(SaleViewModel.AutosaveKey));
        Assert.Empty(NewSale().Lines);                                  // a restart does not bring the held bill back
        Assert.Equal(1, NewSale().HeldCount);
    }

    [Fact]
    public void The_21st_hold_is_refused()
    {
        var sale = NewSale();
        for (var i = 0; i < 20; i++)
        {
            sale.Scan("111");
            sale.Hold();
        }
        Assert.Equal(20, sale.HeldCount);
        sale.Scan("111");

        sale.Hold();

        Assert.True(sale.MessageIsError);
        Assert.Equal("20 bills are already on hold — recall or delete one first", sale.Message);
        Assert.Single(sale.Lines);
        Assert.Equal(20, f.Ctx.Held.List().Count);
    }

    // ---- Recall ----

    [Fact]
    public void Recall_onto_a_non_empty_bill_is_refused()
    {
        var sale = NewSale();
        sale.Scan("111");
        sale.Hold();
        sale.Scan("2000089007400");
        f.Dialogs.OnHeldBills = vm => vm.Bills[0].Id;

        sale.RecallCommand.Execute(null);

        Assert.True(sale.MessageIsError);
        Assert.Equal("Finish or hold the current bill first", sale.Message);
        Assert.Equal(0, f.Dialogs.HeldBillsRequests);
        Assert.Equal("0.740 Kg", Assert.Single(sale.Lines).Qty);
        Assert.Single(f.Ctx.Held.List());
    }

    [Fact]
    public void Recall_with_nothing_held_says_so()
    {
        var sale = NewSale();
        sale.Recall();
        Assert.Equal("No bills on hold", sale.Message);
        Assert.Equal(0, f.Dialogs.HeldBillsRequests);
    }

    [Fact]
    public void Recall_restores_the_lines_and_saves_the_autosave()
    {
        var sale = NewSale();
        sale.Scan("111");
        sale.Scan("111");
        sale.Scan("2000089007400");
        sale.Hold();
        HeldBillsViewModel? shown = null;
        f.Dialogs.OnHeldBills = vm => { shown = vm; return vm.Selected!.Id; };

        sale.Recall();

        var row = Assert.Single(shown!.Bills);
        Assert.Equal("10:00 · Simran · 2 items · 16.17", row.Label);
        Assert.Equal("07/10 10:00", row.HeldAt);
        Assert.Equal(new[] { "2", "0.740 Kg" }, sale.Lines.Select(l => l.Qty));
        Assert.Equal("16.17", sale.Total);
        Assert.False(sale.MessageIsError);
        Assert.Empty(f.Ctx.Held.List());
        Assert.Equal(0, sale.HeldCount);
        Assert.Equal(new[] { "2", "0.740 Kg" }, NewSale().Lines.Select(l => l.Qty));
    }

    [Fact]
    public void Recall_reports_lines_that_can_no_longer_be_sold()
    {
        var sale = NewSale();
        sale.Scan("111");
        sale.Scan("2000089007400");
        sale.Hold();
        f.Catalog.Items[0] = f.Catalog.Items[0] with { Disabled = true };   // milk
        f.Dialogs.OnHeldBills = vm => vm.Bills[0].Id;

        sale.Recall();

        Assert.True(sale.MessageIsError);
        Assert.Equal("1 item(s) on the held bill can no longer be sold", sale.Message);
        Assert.Equal("0.740 Kg", Assert.Single(sale.Lines).Qty);
        Assert.Empty(f.Ctx.Held.List());
    }

    [Fact]
    public void Recall_of_a_bill_already_taken_says_so()
    {
        var sale = NewSale();
        sale.Scan("111");
        sale.Hold();
        f.Dialogs.OnHeldBills = vm => { f.Ctx.Held.Take(vm.Bills[0].Id); return vm.Bills[0].Id; };

        sale.Recall();

        Assert.True(sale.MessageIsError);
        Assert.Equal("That bill was already recalled", sale.Message);
        Assert.Empty(sale.Lines);
        Assert.Equal(0, sale.HeldCount);
    }

    [Fact]
    public void A_recall_that_fails_part_way_leaves_the_bill_on_hold_and_the_screen_empty()
    {
        // A stored bill whose second line is broken: Restore adds the milk, then throws on the null line.
        var broken = new HeldCart("BROKEN", "09:00 · Simran · 2 items · 9.38", f.Clock.Now.AddHours(-1),
            [new HeldLine("MILK", "PCS", 1m, "111", false), null!]);
        f.Ctx.Held.Put(broken);
        var sale = NewSale();
        f.Dialogs.OnHeldBills = vm => vm.Bills[0].Id;

        sale.Recall();

        Assert.True(sale.MessageIsError);
        Assert.Equal("Could not recall the bill — it is still on hold", sale.Message);
        Assert.Empty(sale.Lines);
        Assert.Empty(sale.Cart.Lines);
        Assert.Equal("[]", f.Ctx.Kv.GetValue(SaleViewModel.AutosaveKey));
        var back = Assert.Single(f.Ctx.Held.List());
        Assert.Equal("BROKEN", back.Id);
        Assert.Equal(broken.Label, back.Label);
        Assert.Equal(broken.HeldAt, back.HeldAt);
        Assert.Equal(1, sale.HeldCount);
    }

    [Fact]
    public void The_held_bill_is_removed_only_after_it_is_on_the_screen()
    {
        var sale = NewSale();
        sale.Scan("111");
        sale.Hold();
        var stillHeld = new List<int>();
        f.Catalog.OnFindItem = _ => stillHeld.Add(f.Ctx.Held.List().Count);
        f.Dialogs.OnHeldBills = vm => vm.Bills[0].Id;

        sale.Recall();

        Assert.NotEmpty(stillHeld);
        Assert.All(stillHeld, n => Assert.Equal(1, n));                 // still on hold while the lines are restored
        Assert.Empty(f.Ctx.Held.List());
        Assert.Equal("Bill recalled", sale.Message);
    }

    [Fact]
    public void A_bill_taken_elsewhere_during_the_recall_stays_on_the_screen()
    {
        var sale = NewSale();
        sale.Scan("111");
        sale.Scan("111");
        sale.Hold();
        f.Dialogs.OnHeldBills = vm =>
        {
            var id = vm.Bills[0].Id;
            f.Catalog.OnFindItem = _ => { f.Catalog.OnFindItem = null; f.Ctx.Held.Take(id); };   // another till/window takes it
            return id;
        };

        sale.Recall();

        Assert.True(sale.MessageIsError);
        Assert.Equal("That bill was already recalled elsewhere — check the items", sale.Message);
        Assert.Equal("2", Assert.Single(sale.Lines).Qty);
        Assert.Empty(f.Ctx.Held.List());
        Assert.Equal("2", Assert.Single(NewSale().Lines).Qty);           // and it is in the autosave
    }

    [Fact]
    public async Task Deleting_a_held_bill_needs_a_supervisor_and_is_logged()
    {
        var sale = NewSale();
        sale.Scan("111");
        sale.Hold();
        var label = Assert.Single(f.Ctx.Held.List()).Label;
        var vm = new HeldBillsViewModel(f.Ctx, f.Session, Gate());
        Assert.Equal(label, vm.Selected!.Label);

        f.Dialogs.Pins.Enqueue("1111");                                 // a cashier PIN
        await vm.DeleteSelectedAsync();
        Assert.Single(vm.Bills);
        Assert.Single(f.Ctx.Held.List());
        Assert.Equal(ApprovalAction.FailedSupervisorPin, Assert.Single(f.Ctx.Approvals.Unsynced()).Action);

        f.Dialogs.Pins.Enqueue("9999");
        await vm.DeleteSelectedCommand.ExecuteAsync(null);

        Assert.Empty(vm.Bills);
        Assert.Null(vm.Selected);
        Assert.Empty(f.Ctx.Held.List());
        var approval = Assert.Single(f.Ctx.Approvals.Unsynced(), a => a.Action == ApprovalAction.HeldBillDelete);
        Assert.Equal("sup", approval.SupervisorId);
        Assert.Equal("simran", approval.CashierId);
        Assert.Equal($"Delete held bill {label}", approval.Reason);
        Assert.Equal(0m, approval.Amount);
    }

    [Fact]
    public void Deleting_in_the_recall_dialog_updates_the_held_count()
    {
        var sale = NewSale();
        sale.Scan("111");
        sale.Hold();
        f.Dialogs.Pins.Enqueue("9999");
        f.Dialogs.OnHeldBills = vm => { vm.DeleteSelectedAsync().GetAwaiter().GetResult(); return null; };

        sale.Recall();

        Assert.Equal(0, sale.HeldCount);
        Assert.Empty(sale.Lines);
    }

    // ---- Store and catalog failures become messages ----

    private void Sql(string sql)
    {
        using var c = f.Temp.Db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void A_catalog_failure_during_a_price_check_is_a_message()
    {
        var pc = new PriceCheckViewModel(f.Ctx);
        pc.Lookup("222");
        Assert.True(pc.HasResult);
        f.Catalog.OnFindItem = _ => throw new InvalidOperationException("database is locked");

        pc.Lookup("111");
        Assert.False(pc.HasResult);
        Assert.Null(pc.AddToBill);
        Assert.Equal("", pc.PriceText);
        Assert.Equal("Could not price 111: database is locked", pc.Message);

        pc.Select("MILK");
        Assert.False(pc.HasResult);
        Assert.Equal("Could not price MILK: database is locked", pc.Message);
    }

    [Fact]
    public void A_search_failure_during_a_price_check_is_a_message()
    {
        var pc = new PriceCheckViewModel(f.Ctx with { Search = _ => throw new InvalidOperationException("database is locked") });

        pc.SearchText = "milk";

        Assert.Empty(pc.SearchResults);
        Assert.Equal("Search failed: database is locked", pc.Message);
    }

    [Fact]
    public void A_held_bills_store_failure_is_a_message()
    {
        Sql("DROP TABLE held_cart");

        var vm = new HeldBillsViewModel(f.Ctx, f.Session, Gate());

        Assert.Empty(vm.Bills);
        Assert.Null(vm.Selected);
        Assert.StartsWith("Could not read the bills on hold: ", vm.Message);
    }

    [Fact]
    public async Task A_delete_that_fails_after_approval_leaves_the_bill_on_hold()
    {
        var sale = NewSale();
        sale.Scan("111");
        sale.Hold();
        var vm = new HeldBillsViewModel(f.Ctx, f.Session, Gate());
        Sql("CREATE TRIGGER no_delete BEFORE DELETE ON held_cart BEGIN SELECT RAISE(ABORT, 'disk I/O error'); END");
        f.Dialogs.Pins.Enqueue("9999");

        await vm.DeleteSelectedAsync();

        Assert.Equal("Could not delete the bill — it is still on hold", vm.Message);
        Assert.Single(vm.Bills);
        Assert.Single(f.Ctx.Held.List());
        Assert.Single(f.Ctx.Approvals.Unsynced(), a => a.Action == ApprovalAction.HeldBillDelete);
    }

    // ---- Reprint last ----

    private string CompleteCashSale(SaleViewModel sale)
    {
        sale.Scan("111");
        var pay = new PaymentViewModel(f.Ctx, f.Session, sale, TenderKind.Cash);
        pay.QuickCashCommand.Execute(20m);
        pay.CompleteCommand.Execute(null);
        return Assert.Single(f.Ctx.Receipts.ListPending(10)).ClientId;
    }

    [Fact]
    public void Reprint_last_prints_a_copy_without_the_drawer()
    {
        var sale = NewSale();
        var id = CompleteCashSale(sale);
        Assert.True(Assert.Single(f.Output.Printed).OpenDrawer);

        sale.ReprintLastCommand.Execute(null);

        Assert.Equal(2, f.Output.Printed.Count);
        var (receipt, drawer, copy) = f.Output.Printed[1];
        Assert.Equal(id, receipt.ClientId);
        Assert.False(drawer);
        Assert.True(copy);
        Assert.Equal($"Reprinted {id}", sale.Message);
        Assert.False(sale.MessageIsError);
    }

    [Fact]
    public void Reprint_last_works_after_a_restart()
    {
        var id = CompleteCashSale(NewSale());

        var afterRestart = NewSale();
        afterRestart.ReprintLast();

        var (receipt, drawer, copy) = f.Output.Printed[^1];
        Assert.Equal(id, receipt.ClientId);
        Assert.False(drawer);
        Assert.True(copy);
    }

    [Fact]
    public void After_a_failed_original_print_the_first_reprint_is_not_a_copy_and_the_next_one_is()
    {
        var sale = NewSale();
        f.Output.Fail = true;
        var id = CompleteCashSale(sale);
        Assert.Empty(f.Output.Printed);
        f.Output.Fail = false;

        sale.ReprintLast();
        NewSale().ReprintLast();                                        // also after a restart

        Assert.Equal(2, f.Output.Printed.Count);
        Assert.All(f.Output.Printed, p => Assert.Equal(id, p.Receipt.ClientId));
        Assert.All(f.Output.Printed, p => Assert.False(p.OpenDrawer));
        Assert.False(f.Output.Printed[0].Copy);
        Assert.True(f.Output.Printed[1].Copy);
    }

    [Fact]
    public void A_failed_reprint_does_not_make_the_next_one_a_copy()
    {
        var sale = NewSale();
        f.Output.Fail = true;
        CompleteCashSale(sale);
        sale.ReprintLast();
        f.Output.Fail = false;

        sale.ReprintLast();

        Assert.False(Assert.Single(f.Output.Printed).Copy);
    }

    [Fact]
    public void Popup_print_again_after_a_failed_original_is_not_a_copy_until_it_has_printed()
    {
        var sale = NewSale();
        f.Output.Fail = true;
        CompleteCashSale(sale);
        f.Output.Fail = false;
        var printAgain = Assert.IsType<Func<string?>>(f.Dialogs.LastReprint);

        Assert.Null(printAgain());
        Assert.Null(printAgain());
        sale.ReprintLast();

        Assert.Equal(new[] { false, true, true }, f.Output.Printed.Select(p => p.Copy));
        Assert.All(f.Output.Printed, p => Assert.False(p.OpenDrawer));
    }

    [Fact]
    public void A_new_bill_does_not_inherit_the_previous_bills_printed_flag()
    {
        var sale = NewSale();
        CompleteCashSale(sale);                                         // printed fine: flag "1"
        f.Output.Fail = true;
        sale.Scan("111");
        var pay = new PaymentViewModel(f.Ctx, f.Session, sale, TenderKind.Card);
        pay.CompleteCommand.Execute(null);                             // second bill's print fails
        f.Output.Fail = false;

        sale.ReprintLast();

        Assert.False(f.Output.Printed[^1].Copy);
    }

    [Fact]
    public void The_last_receipt_and_its_printed_flag_are_one_kv_value()
    {
        var sale = NewSale();
        f.Output.Fail = true;
        var id = CompleteCashSale(sale);
        Assert.Equal($"{id}|0", f.Ctx.Kv.GetValue(SaleViewModel.LastReceiptKey));
        f.Output.Fail = false;

        sale.ReprintLast();

        Assert.Equal($"{id}|1", f.Ctx.Kv.GetValue(SaleViewModel.LastReceiptKey));
        Assert.Null(f.Ctx.Kv.GetValue(SaleViewModel.LastReceiptPrintedKey));
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData(null, false)]
    public void The_older_two_key_format_is_still_read(string? printedFlag, bool expectCopy)
    {
        var id = CompleteCashSale(NewSale());
        f.Ctx.Kv.SetValue(SaleViewModel.LastReceiptKey, id);                   // as written by 0.3.3
        if (printedFlag is not null) f.Ctx.Kv.SetValue(SaleViewModel.LastReceiptPrintedKey, printedFlag);

        NewSale().ReprintLast();

        var (receipt, drawer, copy) = f.Output.Printed[^1];
        Assert.Equal(id, receipt.ClientId);
        Assert.False(drawer);
        Assert.Equal(expectCopy, copy);
        Assert.Equal($"{id}|1", f.Ctx.Kv.GetValue(SaleViewModel.LastReceiptKey));
    }

    [Fact]
    public void Reprint_last_always_shows_the_receipt_on_screen_marked_copy()
    {
        var sale = NewSale();
        var id = CompleteCashSale(sale);
        f.Dialogs.Receipts.Clear();
        f.Dialogs.ReceiptPopups.Clear();

        sale.ReprintLastCommand.Execute(null);

        // Without a printer the copy only goes to a file: the popup is what shows that it worked.
        var (shown, printError) = Assert.Single(f.Dialogs.Receipts);
        Assert.Equal(id, shown.ClientId);
        Assert.Null(printError);
        Assert.Equal((true, true), Assert.Single(f.Dialogs.ReceiptPopups));
        Assert.Equal($"Reprinted {id}", sale.Message);
    }

    [Fact]
    public void Reprint_last_shows_the_popup_even_when_the_invoice_preview_is_off()
    {
        var sale = new SaleViewModel(f.Ctx with { ShowReceiptPreview = false }, f.Session, Gate(),
            (s, kind) => new PaymentViewModel(f.Ctx with { ShowReceiptPreview = false }, f.Session, s, kind), () => new object());
        var id = CompleteCashSale(sale);
        Assert.Empty(f.Dialogs.Receipts);

        sale.ReprintLast();

        Assert.Equal(id, Assert.Single(f.Dialogs.Receipts).Receipt.ClientId);
    }

    [Fact]
    public void Pressing_reprint_again_shows_the_message_again()
    {
        var sale = NewSale();
        var id = CompleteCashSale(sale);
        sale.ReprintLast();
        var changes = new List<string?>();
        sale.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        sale.ReprintLast();

        Assert.Contains(nameof(SaleViewModel.Message), changes);
        Assert.Equal($"Reprinted {id}", sale.Message);
        Assert.Equal(2, f.Dialogs.ReceiptPopups.Count(p => p.Reprinted));
    }

    [Fact]
    public void A_failed_reprint_still_shows_the_receipt_with_the_printer_problem()
    {
        var sale = NewSale();
        var id = CompleteCashSale(sale);
        f.Dialogs.Receipts.Clear();
        f.Output.Fail = true;

        sale.ReprintLast();

        Assert.Equal((id, "Printer offline"), (Assert.Single(f.Dialogs.Receipts).Receipt.ClientId, f.Dialogs.Receipts[0].PrintError));
        Assert.True(sale.MessageIsError);
    }

    [Fact]
    public void Print_again_in_the_reprint_popup_prints_another_copy()
    {
        var sale = NewSale();
        CompleteCashSale(sale);
        sale.ReprintLast();
        var printed = f.Output.Printed.Count;

        Assert.Null(f.Dialogs.LastReprint!());

        Assert.Equal(printed + 1, f.Output.Printed.Count);
        Assert.True(f.Output.Printed[^1].Copy);
        Assert.False(f.Output.Printed[^1].OpenDrawer);
    }

    [Fact]
    public void Print_again_in_the_reprint_popup_matches_the_popups_copy_mark()
    {
        var sale = NewSale();
        f.Output.Fail = true;
        CompleteCashSale(sale);                                       // the original never printed
        f.Output.Fail = false;

        sale.ReprintLast();                                           // its first print: not a copy, and the popup says so
        Assert.Equal((false, true), f.Dialogs.ReceiptPopups[^1]);
        Assert.Null(f.Dialogs.LastReprint!());

        Assert.Equal(new[] { false, false }, f.Output.Printed.Select(p => p.Copy));
        sale.ReprintLast();                                           // the next reprint is a copy, popup and paper alike
        Assert.Null(f.Dialogs.LastReprint!());
        Assert.Equal(new[] { false, false, true, true }, f.Output.Printed.Select(p => p.Copy));
        Assert.Equal((true, true), f.Dialogs.ReceiptPopups[^1]);
    }

    [Fact]
    public void A_barcode_scanned_on_the_reprint_popup_goes_on_the_next_bill()
    {
        var sale = NewSale();
        CompleteCashSale(sale);
        f.Dialogs.ReceiptScans.Enqueue("111");

        sale.ReprintLast();

        Assert.Single(sale.Lines);
    }

    [Fact]
    public void Reprint_with_no_receipt_yet_says_so()
    {
        var sale = NewSale();
        sale.ReprintLast();
        Assert.Equal("No receipt to reprint yet", sale.Message);
        Assert.Empty(f.Output.Printed);
    }

    [Fact]
    public void A_failed_reprint_is_an_error_message()
    {
        var sale = NewSale();
        var id = CompleteCashSale(sale);
        f.Output.Fail = true;

        sale.ReprintLast();

        Assert.True(sale.MessageIsError);
        Assert.Contains(id, sale.Message);
        Assert.Contains("Printer offline", sale.Message);
    }
}
