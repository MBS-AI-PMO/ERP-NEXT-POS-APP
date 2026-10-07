using System.ComponentModel;
using TillPOS.Core.Payments;
using TillPOS.Core.Sales;
using TillPOS.Core.Shifts;
using TillPOS.Presentation;
using static TillPOS.Tests.Presentation.PresentationFixture;

namespace TillPOS.Tests.Presentation;

/// <summary>Plan 3d: the cashier picks the counter (POS Profile) when opening a shift; the shift's counter drives the payment
/// modes, rounding and sale context until the shift is closed.</summary>
public sealed class CounterSelectionTests : IDisposable
{
    private readonly PresentationFixture f = new();
    private readonly object saleMarker = new();

    public void Dispose() => f.Dispose();

    private OpenShiftViewModel OpenShift(TillContext? ctx = null)
    {
        f.Session.Cashier = Simran;
        return new OpenShiftViewModel(ctx ?? f.Ctx, f.Session, () => saleMarker);
    }

    private SaleViewModel NewSale() =>
        new(f.Ctx, f.Session, new SupervisorGate(f.Ctx, f.Session), (s, kind) => new PaymentViewModel(f.Ctx, f.Session, s, kind),
            () => "login");

    // ---- Open shift ----

    [Fact]
    public void Open_shift_lists_the_counters_and_preselects_the_default()
    {
        var vm = OpenShift();

        Assert.True(vm.ShowCounters);
        Assert.Equal(new[] { "Counter 2", "Counter 1" }, vm.Counters.Select(c => c.Name));
        Assert.All(vm.Counters, c => Assert.True(c.Available));
        Assert.Same(vm.Counters[0], vm.SelectedCounter);
        Assert.True(vm.Counters[0].IsSelected);
    }

    [Fact]
    public void A_single_counter_needs_no_choice()
    {
        var vm = OpenShift(f.Ctx with { Counters = [CounterTwo] });
        Assert.False(vm.ShowCounters);
        Assert.Equal(CounterTwo, vm.SelectedCounter!.Counter);
    }

    [Fact]
    public void Opening_at_another_counter_counts_the_float_in_its_cash_mode()
    {
        var vm = OpenShift();
        vm.SelectCounterCommand.Execute(vm.Counters[1]);
        Assert.True(vm.Counters[1].IsSelected);
        Assert.False(vm.Counters[0].IsSelected);
        vm.OpeningCash.Text = "150";

        vm.OpenCommand.Execute(null);

        var shift = f.Ctx.Shifts.Current()!;
        Assert.Equal("Al Ain Counter 1", shift.Counter);
        Assert.Equal("Counter 1", shift.CounterName);
        Assert.Equal(new ReceiptPayment("Cash Counter 1", 150m), Assert.Single(shift.OpeningAmounts));
        Assert.Equal(CounterOne, f.Session.Counter);
        Assert.Same(saleMarker, f.Navigator.Current);
    }

    [Fact]
    public void The_last_used_counter_is_preselected()
    {
        var first = OpenShift();
        first.SelectCounterCommand.Execute(first.Counters[1]);
        first.OpeningCash.Text = "100";
        first.OpenCommand.Execute(null);

        var next = OpenShift();

        Assert.Equal(CounterOne, next.SelectedCounter!.Counter);
    }

    [Fact]
    public void A_counter_without_downloaded_settings_is_shown_unavailable_and_cannot_be_chosen()
    {
        f.Ctx.Kv.SetValue(OpenShiftViewModel.LastCounterKey, CounterNine.PosProfile);
        var vm = OpenShift(f.Ctx with { Counters = [CounterNine, CounterTwo] });

        var nine = vm.Counters[0];
        Assert.False(nine.Available);
        Assert.Contains("not available", nine.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(CounterTwo, vm.SelectedCounter!.Counter);       // the first available one, not the unavailable last one

        vm.SelectCounterCommand.Execute(nine);

        Assert.Equal(CounterTwo, vm.SelectedCounter!.Counter);
        Assert.Contains("Counter 9", vm.Message);
    }

    [Fact]
    public void No_available_counter_means_no_shift()
    {
        var vm = OpenShift(f.Ctx with { Counters = [CounterNine] });
        vm.OpeningCash.Text = "100";

        vm.OpenCommand.Execute(null);

        Assert.Null(f.Ctx.Shifts.Current());
        Assert.Equal("Choose your counter.", vm.Message);
    }

    // ---- Joining an open shift ----

    [Fact]
    public void Logging_in_joins_the_open_shift_at_its_counter()
    {
        f.LogInWithOpenShift(CounterOne);
        f.Session.Cashier = null;
        f.Session.Shift = null;
        f.Session.Counter = null;

        var login = new LoginViewModel(f.Ctx, f.Session, () => saleMarker);
        Assert.Equal("Counter 1 · shift open since 08:00", login.ShiftInfo);
        foreach (var c in "1111") login.DigitCommand.Execute(c.ToString());
        login.LoginCommand.Execute(null);

        Assert.Same(saleMarker, f.Navigator.Current);
        Assert.Equal(CounterOne, f.Session.Counter);
    }

    [Fact]
    public void A_shift_opened_before_counters_existed_joins_the_default_counter()
    {
        f.Ctx.Shifts.Open(new ShiftOpening("TILL2-SHIFT-OLD", "simran", "", f.Clock.Now.AddHours(-1), [new ReceiptPayment("Cash Counter 2", 50m)]));

        var login = new LoginViewModel(f.Ctx, f.Session, () => saleMarker);
        foreach (var c in "1111") login.DigitCommand.Execute(c.ToString());
        login.LoginCommand.Execute(null);

        Assert.Equal(CounterTwo, f.Session.Counter);
        Assert.Equal("Counter 2 · shift open since 09:00", new LoginViewModel(f.Ctx, f.Session, () => saleMarker).ShiftInfo);
    }

    [Fact]
    public void No_open_shift_shows_no_shift_info() =>
        Assert.Equal("", new LoginViewModel(f.Ctx, f.Session, () => saleMarker).ShiftInfo);

    // ---- Selling at the shift's counter ----

    [Fact]
    public void A_cash_sale_uses_the_counters_cash_mode_and_rounding_and_names_the_counter()
    {
        f.LogInWithOpenShift(CounterOne);
        var sale = NewSale();
        sale.Scan("111");                                              // 6.79; Counter 1 does not round cash

        var pay = new PaymentViewModel(f.Ctx, f.Session, sale, TenderKind.Cash);
        pay.QuickCashCommand.Execute(10m);
        Assert.Equal("6.79", pay.AmountDue);
        Assert.Equal("3.21", pay.Change);
        pay.CompleteCommand.Execute(null);

        var receipt = Assert.Single(f.Ctx.Receipts.ListPending(10));
        Assert.Equal(new[] { new ReceiptPayment("Cash Counter 1", 10m) }, receipt.Payments);
        Assert.False(receipt.UsesErpRoundedTotal);
        Assert.Equal(0m, receipt.RoundedTotal);
        Assert.Equal("Counter 1", receipt.CounterName);
    }

    [Fact]
    public void The_default_counter_still_rounds_cash()
    {
        f.LogInWithOpenShift();
        var sale = NewSale();
        sale.Scan("111");

        var pay = new PaymentViewModel(f.Ctx, f.Session, sale, TenderKind.Cash);
        pay.QuickCashCommand.Execute(10m);
        pay.CompleteCommand.Execute(null);

        var receipt = Assert.Single(f.Ctx.Receipts.ListPending(10));
        Assert.Equal("Cash Counter 2", receipt.Payments[0].ModeOfPayment);
        Assert.Equal(6.75m, receipt.RoundedTotal);
        Assert.Equal("Counter 2", receipt.CounterName);
    }

    [Fact]
    public void A_cash_refund_uses_the_counters_cash_mode()
    {
        f.LogInWithOpenShift(CounterOne);
        var sale = NewSale();
        sale.Scan("111");
        var pay = new PaymentViewModel(f.Ctx, f.Session, sale, TenderKind.Card);
        pay.CompleteCommand.Execute(null);
        var sold = Assert.Single(f.Ctx.Receipts.ListPending(10));

        sale.Return();
        var vm = Assert.IsType<ReturnViewModel>(f.Navigator.Current);
        vm.FindText = sold.ClientId;
        vm.FindCommand.Execute(null);
        vm.ReturnAllCommand.Execute(null);
        vm.SetReasonCommand.Execute("Damaged");
        vm.ConfirmCommand.Execute(null);

        var credit = f.Ctx.Receipts.ListPending(10).Single(r => r.Kind == ReceiptKind.Return);
        Assert.Equal(new[] { new ReceiptPayment("Cash Counter 1", -6.79m) }, credit.Payments);
        Assert.Equal("Counter 1", credit.CounterName);
    }

    [Fact]
    public void Closing_the_shift_expects_cash_in_the_counters_cash_mode()
    {
        f.LogInWithOpenShift(CounterOne);
        var sale = NewSale();
        sale.Scan("111");
        var pay = new PaymentViewModel(f.Ctx, f.Session, sale, TenderKind.Cash);
        pay.QuickCashCommand.Execute(10m);
        pay.CompleteCommand.Execute(null);

        sale.CloseShift();
        var close = Assert.IsType<CloseShiftViewModel>(f.Navigator.Current);
        close.UseTotalInstead.Text = "206.79";
        close.ConfirmCount();

        Assert.Equal(0m, close.CashDifference);
        Assert.Contains(close.Rows, r => r.Mode == "Cash Counter 1");
        Assert.DoesNotContain(close.Rows, r => r.Mode == "Cash Counter 2");
    }

    [Fact]
    public void Price_check_prices_at_the_shifts_counter()
    {
        f.LogInWithOpenShift(CounterOne);
        PriceCheckViewModel? seen = null;
        f.Dialogs.OnPriceCheck = vm => { seen = vm; vm.ScanText = "111"; vm.ScanEnteredCommand.Execute(null); return null; };

        NewSale().PriceCheck();

        Assert.True(seen!.HasResult, seen.Message);
    }

    // ---- Header ----

    [Fact]
    public void The_header_names_the_till_and_the_counter()
    {
        var shell = new ShellViewModel { TillName = "Till 2" };
        var changed = new List<string?>();
        ((INotifyPropertyChanged)shell).PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Assert.Equal("Till 2", shell.TillHeader);

        shell.Session.Counter = CounterOne;

        Assert.Equal("Till 2 · Counter 1", shell.TillHeader);
        Assert.Contains(nameof(ShellViewModel.TillHeader), changed);

        shell.Session.Shift = null;      // closing the shift forgets the counter
        Assert.Equal("Till 2", shell.TillHeader);
    }
}
