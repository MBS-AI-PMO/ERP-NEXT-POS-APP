using TillPOS.Core.Payments;
using TillPOS.Presentation;

namespace TillPOS.Tests.Presentation;

public sealed class PaymentViewModelTests : IDisposable
{
    private readonly PresentationFixture f = new();
    private readonly SaleViewModel sale;

    public PaymentViewModelTests()
    {
        f.LogInWithOpenShift();
        sale = new SaleViewModel(f.Ctx, f.Session, new SupervisorGate(f.Ctx, f.Session),
            (s, kind) => new PaymentViewModel(f.Ctx, f.Session, s, kind));
        sale.Scan("111");                                              // 6.79
    }

    public void Dispose() => f.Dispose();

    private PaymentViewModel Pay(TenderKind kind) => new(f.Ctx, f.Session, sale, kind);

    [Fact]
    public void Cash_payment_rounds_saves_prints_with_drawer_and_returns_to_sale()
    {
        var vm = Pay(TenderKind.Cash);
        vm.QuickCashCommand.Execute(20m);

        Assert.Equal("6.75", vm.AmountDue);
        Assert.Equal("13.25", vm.Change);

        vm.CompleteCommand.Execute(null);

        var receipt = Assert.Single(f.Ctx.Receipts.ListPending(10));
        Assert.Equal("simran", receipt.Cashier);
        Assert.Equal("p.simran@quickgroc.com", receipt.CashierUser);
        Assert.Equal("Simran", receipt.CashierName);
        Assert.Equal("TILL2-SHIFT-20261007080000", receipt.ShiftClientId);
        var (printed, drawer, copy) = Assert.Single(f.Output.Printed);
        Assert.Equal(receipt.ClientId, printed.ClientId);
        Assert.True(drawer);
        Assert.False(copy);
        Assert.Equal(receipt.ClientId, f.Ctx.Kv.GetValue(SaleViewModel.LastReceiptKey));
        Assert.Same(sale, f.Navigator.Current);
        Assert.Empty(sale.Lines);
        Assert.Equal("[]", f.Ctx.Kv.GetValue(SaleViewModel.AutosaveKey));
    }

    [Fact]
    public void Card_payment_is_exact_and_does_not_open_the_drawer()
    {
        var vm = Pay(TenderKind.Card);
        Assert.Equal("6.79", vm.AmountDue);

        vm.CompleteCommand.Execute(null);

        Assert.False(Assert.Single(f.Output.Printed).OpenDrawer);
        Assert.Equal("Credit Card", Assert.Single(f.Ctx.Receipts.ListPending(10)).Payments[0].ModeOfPayment);
    }

    [Fact]
    public void Not_enough_cash_saves_nothing()
    {
        var vm = Pay(TenderKind.Cash);
        vm.Cash.Text = "5";

        vm.CompleteCommand.Execute(null);

        Assert.Equal("1.75", vm.Shortfall);
        Assert.Equal("Still to pay 1.75", vm.Message);
        Assert.Empty(f.Ctx.Receipts.ListPending(10));
    }

    [Fact]
    public void Split_payment_charges_card_exactly_and_rounds_cash()
    {
        var vm = Pay(TenderKind.Split);
        vm.Card.Text = "5";
        vm.Cash.Text = "2";

        Assert.Equal("6.75", vm.AmountDue);
        Assert.Equal("0.25", vm.Change);
        vm.CompleteCommand.Execute(null);

        Assert.True(Assert.Single(f.Output.Printed).OpenDrawer);
    }

    [Fact]
    public void Invalid_split_shows_why()
    {
        var vm = Pay(TenderKind.Split);
        vm.Card.Text = "10";
        Assert.Null(vm.Plan);
        Assert.StartsWith("The card part must be", vm.Message);
    }

    [Fact]
    public void Printer_failure_still_saves_the_bill()
    {
        f.Output.Fail = true;
        var vm = Pay(TenderKind.Card);

        vm.CompleteCommand.Execute(null);

        Assert.Single(f.Ctx.Receipts.ListPending(10));
        Assert.True(sale.MessageIsError);
        Assert.Contains("printer failed", sale.Message);
        Assert.Contains("Print again", sale.Message);
        Assert.DoesNotContain("Ctrl+P", sale.Message);
        Assert.Same(sale, f.Navigator.Current);
    }

    [Fact]
    public void Completed_sale_shows_the_saved_invoice()
    {
        Pay(TenderKind.Card).CompleteCommand.Execute(null);

        var saved = Assert.Single(f.Ctx.Receipts.ListPending(10));
        var (shown, printError) = Assert.Single(f.Dialogs.Receipts);
        Assert.Equal(saved.ClientId, shown.ClientId);
        Assert.Null(printError);
        Assert.Same(sale, f.Navigator.Current);
    }

    [Fact]
    public void Invoice_popup_gets_the_print_error()
    {
        f.Output.Fail = true;
        Pay(TenderKind.Card).CompleteCommand.Execute(null);

        Assert.Equal("Printer offline", Assert.Single(f.Dialogs.Receipts).PrintError);
    }

    [Fact]
    public void A_scan_that_closes_the_popup_goes_on_the_next_bill()
    {
        f.Dialogs.ReceiptScans.Enqueue("111");
        Pay(TenderKind.Card).CompleteCommand.Execute(null);

        var line = Assert.Single(sale.Lines);
        Assert.Equal("Full Cream Milk 1L", line.Name);
        Assert.Equal("6.79", sale.Total);
        Assert.Same(sale, f.Navigator.Current);
    }

    [Fact]
    public void No_popup_when_the_preview_is_turned_off()
    {
        var ctx = f.Ctx with { ShowReceiptPreview = false };
        var quiet = new SaleViewModel(ctx, f.Session, new SupervisorGate(ctx, f.Session), (s, kind) => new PaymentViewModel(ctx, f.Session, s, kind));
        quiet.Scan("111");

        new PaymentViewModel(ctx, f.Session, quiet, TenderKind.Card).CompleteCommand.Execute(null);

        Assert.Empty(f.Dialogs.Receipts);
        Assert.Single(f.Output.Printed);
        Assert.Contains("Saved", quiet.Message);
    }

    [Fact]
    public void Reprint_from_the_popup_never_opens_the_drawer()
    {
        var vm = Pay(TenderKind.Cash);
        vm.QuickCashCommand.Execute(20m);
        vm.CompleteCommand.Execute(null);
        var reprint = Assert.IsType<Func<string?>>(f.Dialogs.LastReprint);

        Assert.Null(reprint());

        Assert.Equal(2, f.Output.Printed.Count);
        Assert.True(f.Output.Printed[0].OpenDrawer);
        Assert.False(f.Output.Printed[1].OpenDrawer);
        Assert.False(f.Output.Printed[0].Copy);
        Assert.True(f.Output.Printed[1].Copy);
        Assert.Equal(f.Output.Printed[0].Receipt.ClientId, f.Output.Printed[1].Receipt.ClientId);
    }

    [Fact]
    public void Failed_reprint_returns_the_printer_error()
    {
        Pay(TenderKind.Card).CompleteCommand.Execute(null);
        f.Output.Fail = true;

        Assert.Equal("Printer offline", f.Dialogs.LastReprint!());
    }

    [Fact]
    public void Autosave_is_cleared_before_printing()
    {
        var checkedInHook = false;
        f.Output.OnPrint = () =>
        {
            Assert.Equal("[]", f.Ctx.Kv.GetValue(SaleViewModel.AutosaveKey));
            var fresh = new SaleViewModel(f.Ctx, f.Session, new SupervisorGate(f.Ctx, f.Session), (s, k) => "x");
            Assert.Empty(fresh.Lines);
            checkedInHook = true;
        };
        Pay(TenderKind.Card).CompleteCommand.Execute(null);
        Assert.True(checkedInHook);
    }

    [Fact]
    public void Completing_twice_saves_one_bill()
    {
        var vm = Pay(TenderKind.Card);
        vm.CompleteCommand.Execute(null);
        vm.Complete();
        Assert.Single(f.Ctx.Receipts.ListPending(10));
        Assert.Single(f.Output.Printed);
    }

    [Fact]
    public void Save_failure_stays_on_payment_with_a_message()
    {
        var vm = Pay(TenderKind.Card);
        sale.Cart.Clear();
        vm.CompleteCommand.Execute(null);
        Assert.StartsWith("Could not save the bill", vm.Message);
        Assert.Empty(f.Ctx.Receipts.ListPending(10));
        Assert.NotSame(sale, f.Navigator.Current);
    }

    [Fact]
    public void Back_returns_to_the_sale_without_saving()
    {
        var vm = Pay(TenderKind.Cash);
        vm.BackCommand.Execute(null);
        Assert.Same(sale, f.Navigator.Current);
        Assert.Empty(f.Ctx.Receipts.ListPending(10));
        Assert.Single(sale.Lines);
    }
}
