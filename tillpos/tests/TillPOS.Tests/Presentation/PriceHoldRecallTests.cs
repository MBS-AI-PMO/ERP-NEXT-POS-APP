using TillPOS.Core.Catalog;
using TillPOS.Core.Payments;
using TillPOS.Core.Pricing;
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

    private SaleViewModel NewSale() => new(f.Ctx, f.Session, Gate(), (sale, kind) => new PaymentViewModel(f.Ctx, f.Session, sale, kind));

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
