using System.Collections;
using System.Globalization;
using TillPOS.Core.Sales;
using TillPOS.Core.Security;
using TillPOS.Core.Shifts;
using TillPOS.Presentation;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Presentation;

/// <summary>Close shift (blind count, result, variance gate, Z report) and log out.</summary>
public sealed class CloseShiftTests : IDisposable
{
    private const string ShiftId = "TILL2-SHIFT-20261007080000";
    private readonly PresentationFixture f = new();
    private readonly object login = new();

    /// <summary>Opening float 200 + a cash bill of 123.45 (150 tendered, 26.55 change) → expected cash 323.45.
    /// A card bill of 84.25 → expected card 84.25.</summary>
    public CloseShiftTests()
    {
        f.LogInWithOpenShift();
        Save("TILL2-20261007090000-000001", M("123.45"), M("5.88"), [new ReceiptPayment("Cash Counter 2", 150m)], M("26.55"));
        Save("TILL2-20261007091000-000002", M("84.25"), M("4.01"), [new ReceiptPayment("Credit Card", M("84.25"))], 0m);
    }

    public void Dispose() => f.Dispose();

    private void Save(string id, decimal grand, decimal vat, IReadOnlyList<ReceiptPayment> payments, decimal change) =>
        f.Ctx.Receipts.Save(new Receipt(id, ReceiptKind.Sale, null, ShiftId, "simran", f.Clock.Now.AddMinutes(-30), [], grand, grand - vat, vat,
            grand, false, 0m, 0m, payments, change, 0m, null));

    private SaleViewModel NewSale() =>
        new(f.Ctx, f.Session, new SupervisorGate(f.Ctx, f.Session), (sale, kind) => "payment", () => login);

    private CloseShiftViewModel OpenClose()
    {
        var sale = NewSale();
        sale.CloseShift();
        return Assert.IsType<CloseShiftViewModel>(f.Navigator.Current);
    }

    private CloseShiftViewModel Counted(string cash, string card = "84.25")
    {
        var vm = OpenClose();
        vm.UseTotalInstead.Text = cash;
        vm.CardTotal.Text = card;
        vm.ConfirmCount();
        Assert.True(vm.IsResult);
        return vm;
    }

    // ---- Starting a close ----

    [Fact]
    public void Close_shift_is_refused_with_lines_on_the_bill()
    {
        var sale = NewSale();
        sale.Scan("111");
        f.Navigator.Show(sale);

        sale.CloseShiftCommand.Execute(null);

        Assert.Same(sale, f.Navigator.Current);
        Assert.True(sale.MessageIsError);
        Assert.Equal("Finish, hold or void the current bill first", sale.Message);
        Assert.NotNull(f.Ctx.Shifts.Current());
    }

    [Fact]
    public void Close_shift_is_refused_with_held_bills()
    {
        var sale = NewSale();
        sale.Scan("111");
        sale.Hold();
        sale.Scan("111");
        sale.Hold();
        f.Navigator.Show(sale);

        sale.CloseShift();

        Assert.Same(sale, f.Navigator.Current);
        Assert.Equal("2 bill(s) are on hold — recall or delete them before closing the shift", sale.Message);
        Assert.Equal(2, f.Ctx.Held.List().Count);
    }

    [Fact]
    public void Back_returns_to_the_same_sale_screen()
    {
        var sale = NewSale();
        sale.CloseShift();
        var vm = Assert.IsType<CloseShiftViewModel>(f.Navigator.Current);

        vm.BackCommand.Execute(null);

        Assert.Same(sale, f.Navigator.Current);
        Assert.NotNull(f.Ctx.Shifts.Current());
    }

    // ---- Blind count ----

    [Fact]
    public void Nothing_in_the_count_stage_exposes_the_expected_amounts()
    {
        var vm = OpenClose();
        vm.Denominations[2].Count.Text = "3";                                   // 3 × 100
        vm.CardTotal.Text = "80";

        Assert.True(vm.IsCounting);
        Assert.Empty(vm.Rows);
        Assert.Equal("", vm.SalesCount);
        Assert.Equal("", vm.GrandTotalText);
        Assert.Equal("", vm.VatText);
        Assert.Equal("", vm.CashDifferenceText);
        Assert.Equal(0m, vm.CashDifference);
        Assert.False(vm.NeedsSupervisor);
        foreach (var value in PublicValues(vm))
        {
            Assert.DoesNotContain("323.45", value);                             // expected cash
            Assert.DoesNotContain("84.25", value);                              // expected card
            Assert.DoesNotContain("23.45", value);                              // the cash difference (300 − 323.45)
            Assert.DoesNotContain("207.7", value);                              // bills' grand total
        }
    }

    /// <summary>Every public property value of the view model (and of its denominations and rows), as invariant text.</summary>
    private static IEnumerable<string> PublicValues(object vm)
    {
        foreach (var p in vm.GetType().GetProperties())
        {
            if (p.GetIndexParameters().Length > 0 || p.PropertyType.IsSubclassOf(typeof(Delegate))) continue;
            var value = p.GetValue(vm);
            if (value is string or decimal or int or bool or null) yield return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            else if (value is NumericEntry entry) yield return entry.Text;
            else if (value is IEnumerable items and not string)
                foreach (var item in items)
                    if (item is Denomination d) { yield return d.Count.Text; yield return d.LineTotal; }
                    else yield return Convert.ToString(item, CultureInfo.InvariantCulture) ?? "";
        }
    }

    [Fact]
    public void Denominations_add_up()
    {
        var vm = OpenClose();
        Assert.Equal(new[] { 500m, 200m, 100m, 50m, 20m, 10m, 5m, 1m, 0.50m, 0.25m }, vm.Denominations.Select(d => d.Value));
        vm.Denominations.Single(d => d.Value == 500m).Count.Text = "2";
        vm.Denominations.Single(d => d.Value == 0.25m).Count.Text = "3";

        Assert.Equal(M("1000.75"), vm.CountedCash);
        Assert.Equal("1000.75", vm.CashTotal);
        Assert.Equal("0.75", vm.Denominations.Single(d => d.Value == 0.25m).LineTotal);
    }

    [Fact]
    public void Fractional_counts_are_rejected()
    {
        var vm = OpenClose();
        var fives = vm.Denominations.Single(d => d.Value == 5m);
        fives.Count.Text = "1.5";
        Assert.Equal("", fives.Count.Text);
        fives.Count.Digit('4');
        fives.Count.Dot();
        fives.Count.Digit('5');
        Assert.Equal("45", fives.Count.Text);
        Assert.Equal(225m, vm.CountedCash);
    }

    [Fact]
    public void A_cash_total_overrides_the_denominations()
    {
        var vm = OpenClose();
        vm.Denominations[0].Count.Text = "2";
        vm.UseTotalInstead.Text = "323.5";
        Assert.Equal(M("323.5"), vm.CountedCash);
        vm.UseTotalInstead.Clear();
        Assert.Equal(1000m, vm.CountedCash);
    }

    // ---- Result ----

    [Fact]
    public void Confirming_the_count_shows_expected_counted_and_difference()
    {
        var vm = OpenClose();
        vm.UseTotalInstead.Text = "323.2";
        vm.ConfirmCountCommand.Execute(null);                                    // card left blank = 0

        Assert.True(vm.IsResult);
        Assert.False(vm.IsCounting);
        Assert.Equal(new[]
        {
            new ShiftResultRow("Cash Counter 2", "323.45", "323.20", "-0.25", true),
            new ShiftResultRow("Credit Card", "84.25", "0.00", "-84.25", true),
        }, vm.Rows);
        Assert.Equal("2", vm.SalesCount);
        Assert.Equal("207.70", vm.GrandTotalText);
        Assert.Equal("9.89", vm.VatText);
        Assert.Equal(M("-0.25"), vm.CashDifference);
        Assert.False(vm.NeedsSupervisor);                                        // card differences are not gated
    }

    [Fact]
    public void Recount_goes_back_to_the_count_keeping_the_entries()
    {
        var vm = Counted("300", "80");
        vm.RecountCommand.Execute(null);

        Assert.True(vm.IsCounting);
        Assert.Empty(vm.Rows);
        Assert.Equal("300", vm.UseTotalInstead.Text);
        Assert.Equal("80", vm.CardTotal.Text);
        Assert.Equal("", vm.CashDifferenceText);
        Assert.NotNull(f.Ctx.Shifts.Current());
    }

    // ---- Variance gate ----

    [Fact]
    public async Task Exactly_five_short_closes_without_a_supervisor()
    {
        var vm = Counted("318.45");
        Assert.Equal(-5m, vm.CashDifference);
        Assert.False(vm.NeedsSupervisor);

        await vm.CloseAsync();

        Assert.Equal(0, f.Dialogs.PinRequests);
        Assert.Null(f.Ctx.Shifts.Current());
        Assert.Null(Assert.Single(f.Output.ShiftReports).ApprovedBy);
    }

    [Fact]
    public async Task Exactly_five_over_closes_without_a_supervisor()
    {
        var vm = Counted("328.45");
        Assert.False(vm.NeedsSupervisor);
        await vm.CloseAsync();
        Assert.Equal(0, f.Dialogs.PinRequests);
        Assert.Null(f.Ctx.Shifts.Current());
    }

    [Theory]
    [InlineData("318.44", "-5.01")]
    [InlineData("328.46", "5.01")]
    public async Task Over_five_asks_for_a_pin_and_a_refusal_leaves_the_shift_open(string cash, string diff)
    {
        var vm = Counted(cash);
        Assert.Equal(M(diff), vm.CashDifference);
        Assert.True(vm.NeedsSupervisor);
        f.Dialogs.Pins.Enqueue(null);                                           // cancelled

        await vm.CloseAsync();

        Assert.Equal(1, f.Dialogs.PinRequests);
        Assert.NotNull(f.Ctx.Shifts.Current());
        Assert.Empty(f.Output.ShiftReports);
        Assert.Same(vm, f.Navigator.Current);
        Assert.NotNull(f.Session.Shift);
        Assert.NotNull(f.Session.Cashier);
        Assert.True(vm.MessageIsError);

        f.Dialogs.Pins.Enqueue("1111");                                         // a cashier PIN is not a supervisor PIN
        await vm.CloseAsync();
        Assert.NotNull(f.Ctx.Shifts.Current());
        Assert.Equal(ApprovalAction.FailedSupervisorPin, Assert.Single(f.Ctx.Approvals.Unsynced()).Action);
    }

    [Theory]
    [InlineData("318.44", "-5.01")]
    [InlineData("328.46", "5.01")]
    public async Task Over_five_closes_with_a_supervisor_and_logs_the_variance(string cash, string diff)
    {
        var vm = Counted(cash);
        f.Dialogs.Pins.Enqueue("9999");

        await vm.CloseAsync();

        Assert.Null(f.Ctx.Shifts.Current());
        var approval = Assert.Single(f.Ctx.Approvals.Unsynced());
        Assert.Equal(ApprovalAction.ShiftVariance, approval.Action);
        Assert.Equal("sup", approval.SupervisorId);
        Assert.Equal("simran", approval.CashierId);
        Assert.Equal(ShiftId, approval.ShiftClientId);
        Assert.Equal(M(diff), approval.Amount);
        Assert.Equal($"Cash difference {diff}", approval.Reason);
        Assert.Equal("sup", Assert.Single(f.Output.ShiftReports).ApprovedBy);
    }

    // ---- Recount cannot skip the gate ----

    [Fact]
    public async Task A_recount_after_a_count_over_the_limit_still_needs_a_supervisor()
    {
        var vm = Counted("313.45");                                              // 10 short
        Assert.Equal(-10m, vm.CashDifference);
        vm.RecountCommand.Execute(null);
        vm.UseTotalInstead.Text = "323.45";                                      // now exact
        vm.ConfirmCount();

        Assert.Equal(0m, vm.CashDifference);
        Assert.Equal(-10m, vm.FirstCashDifference);
        Assert.True(vm.NeedsSupervisor);

        f.Dialogs.Pins.Enqueue("1111");                                         // a cashier PIN is refused
        await vm.CloseAsync();
        Assert.NotNull(f.Ctx.Shifts.Current());
        Assert.Empty(f.Output.ShiftReports);

        f.Dialogs.Pins.Enqueue("9999");
        await vm.CloseAsync();

        Assert.Null(f.Ctx.Shifts.Current());
        var approval = Assert.Single(f.Ctx.Approvals.Unsynced(), a => a.Action == ApprovalAction.ShiftVariance);
        Assert.Equal("Cash difference 0.00 (first count -10.00)", approval.Reason);
        Assert.Equal(0m, approval.Amount);
        var report = Assert.Single(f.Output.ShiftReports);
        Assert.Equal(-10m, report.FirstCountDifference);
        Assert.Equal("sup", report.ApprovedBy);
    }

    [Fact]
    public async Task A_recount_after_a_count_within_the_limit_needs_no_supervisor()
    {
        var vm = Counted("320");                                                 // 3.45 short
        vm.RecountCommand.Execute(null);
        vm.UseTotalInstead.Text = "323.45";
        vm.ConfirmCount();

        Assert.False(vm.NeedsSupervisor);
        await vm.CloseAsync();

        Assert.Equal(0, f.Dialogs.PinRequests);
        Assert.Null(f.Ctx.Shifts.Current());
        Assert.Equal(M("-3.45"), Assert.Single(f.Output.ShiftReports).FirstCountDifference);
    }

    [Fact]
    public async Task A_recount_to_the_same_difference_prints_no_first_count_line()
    {
        var vm = Counted("320");
        vm.RecountCommand.Execute(null);
        vm.ConfirmCount();
        await vm.CloseAsync();
        Assert.Null(Assert.Single(f.Output.ShiftReports).FirstCountDifference);
    }

    // ---- Close ----

    [Fact]
    public async Task Closing_stores_the_closing_prints_the_report_and_goes_to_login()
    {
        var vm = Counted("323.45");
        f.Clock.Now = f.Clock.Now.AddMinutes(5);

        vm.CloseCommand.Execute(null);
        await vm.CloseCommand.ExecutionTask!;

        var stored = f.Ctx.Shifts.Get(ShiftId)!.Value;
        var closing = Assert.IsType<ShiftClosing>(stored.Closing);
        Assert.Equal(f.Clock.Now, closing.ClosedAt);
        Assert.Equal(M("323.45"), closing.Modes.Single(m => m.ModeOfPayment == "Cash Counter 2").Counted);
        Assert.Equal(M("84.25"), closing.Modes.Single(m => m.ModeOfPayment == "Credit Card").Counted);
        Assert.Equal(2, closing.Sales);

        var report = Assert.Single(f.Output.ShiftReports);
        Assert.Equal(ShiftId, report.Opening.ClientId);
        Assert.Equal(closing.ClosedAt, report.Closing.ClosedAt);
        Assert.Equal(closing.Modes, report.Closing.Modes);
        Assert.Equal("Simran", report.CashierName);
        Assert.Empty(f.Output.Printed);                                          // no receipt print, no drawer kick

        Assert.Null(f.Session.Shift);
        Assert.Null(f.Session.Cashier);
        Assert.Same(login, f.Navigator.Current);
    }

    [Fact]
    public async Task A_printer_failure_still_closes_the_shift()
    {
        var vm = Counted("323.45");
        f.Output.Fail = true;

        await vm.CloseAsync();

        Assert.Null(f.Ctx.Shifts.Current());
        Assert.NotNull(f.Ctx.Shifts.Get(ShiftId)!.Value.Closing);
        Assert.Contains(f.Dialogs.Infos, m => m.Contains("shift is closed", StringComparison.Ordinal) && m.Contains("Printer offline", StringComparison.Ordinal));
        Assert.Null(f.Session.Shift);
        Assert.Same(login, f.Navigator.Current);
    }

    [Fact]
    public async Task There_is_no_way_back_after_the_close()
    {
        var vm = Counted("323.45");
        await vm.CloseAsync();

        vm.BackCommand.Execute(null);
        vm.RecountCommand.Execute(null);
        await vm.CloseAsync();

        Assert.Same(login, f.Navigator.Current);
        Assert.True(vm.IsResult);
        Assert.Single(f.Output.ShiftReports);
    }

    [Fact]
    public async Task After_the_close_the_next_login_opens_a_new_shift()
    {
        await Counted("323.45").CloseAsync();

        var next = new LoginViewModel(f.Ctx, f.Session, () => "sale");
        foreach (var c in "1111") next.DigitCommand.Execute(c.ToString());
        next.LoginCommand.Execute(null);

        Assert.IsType<OpenShiftViewModel>(f.Navigator.Current);
    }

    // ---- Log out ----

    [Fact]
    public void Log_out_keeps_the_shift_open_and_the_next_cashier_joins_it()
    {
        var sale = NewSale();
        sale.LogOutCommand.Execute(null);

        Assert.Null(f.Session.Cashier);
        Assert.Same(login, f.Navigator.Current);
        Assert.Equal(ShiftId, f.Ctx.Shifts.Current()!.ClientId);

        var next = new LoginViewModel(f.Ctx, f.Session, () => "sale");
        foreach (var c in "9999") next.DigitCommand.Execute(c.ToString());
        next.LoginCommand.Execute(null);

        Assert.Equal("sale", f.Navigator.Current);
        Assert.Equal("sup", f.Session.Cashier!.Id);
        Assert.Equal(ShiftId, f.Session.Shift!.ClientId);
    }

    [Fact]
    public void Log_out_is_refused_with_lines_on_the_bill()
    {
        var sale = NewSale();
        sale.Scan("111");
        f.Navigator.Show(sale);

        sale.LogOut();

        Assert.Equal("Finish, hold or void the current bill first", sale.Message);
        Assert.NotNull(f.Session.Cashier);
        Assert.Same(sale, f.Navigator.Current);
    }
}
