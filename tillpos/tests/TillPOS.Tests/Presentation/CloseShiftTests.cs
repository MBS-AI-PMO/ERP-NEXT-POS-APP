using System.Collections;
using System.Globalization;
using TillPOS.Core.Sales;
using TillPOS.Core.Security;
using TillPOS.Core.Shifts;
using TillPOS.Presentation;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Presentation;

/// <summary>Close shift (blind count, supervisor PIN, Z report) and log out.</summary>
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
    public void Nothing_on_the_close_screen_exposes_the_expected_amounts()
    {
        var vm = OpenClose();
        vm.Denominations[2].Count.Text = "3";                                   // 3 × 100
        vm.CardTotal.Text = "80";

        foreach (var value in PublicValues(vm))
        {
            Assert.DoesNotContain("323.45", value);                             // expected cash
            Assert.DoesNotContain("84.25", value);                              // expected card
            Assert.DoesNotContain("23.45", value);                              // the cash difference (300 − 323.45)
            Assert.DoesNotContain("207.7", value);                              // bills' grand total
        }
    }

    /// <summary>Every public property value of the view model (and of its denominations), as invariant text.</summary>
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

    [Fact]
    public async Task Closing_with_no_cash_entered_is_refused_before_the_pin()
    {
        var vm = OpenClose();
        vm.CardTotal.Text = "84.25";                                             // the card total alone is not a cash count

        await vm.CloseAsync();

        Assert.True(vm.MessageIsError);
        Assert.Equal(CloseShiftViewModel.NothingCountedMessage, vm.Message);
        Assert.Equal(0, f.Dialogs.PinRequests);
        Assert.NotNull(f.Ctx.Shifts.Current());
        Assert.True(vm.BackCommand.CanExecute(null));
    }

    [Fact]
    public async Task An_explicit_zero_is_a_count()
    {
        var vm = OpenClose();
        vm.Denominations[0].Count.Text = "0";
        f.Dialogs.Pins.Enqueue("9999");

        await vm.CloseAsync();

        Assert.Null(f.Ctx.Shifts.Current());
        Assert.Equal(M("-323.45"), Assert.Single(f.Output.ShiftReports).Closing.Modes.Single(m => m.ModeOfPayment == "Cash Counter 2").Difference);
    }

    // ---- Supervisor PIN ----

    [Fact]
    public async Task Closing_always_needs_a_supervisor_and_a_refusal_leaves_the_shift_open()
    {
        var vm = Counted("323.45");                                              // exact: still needs the PIN
        f.Dialogs.Pins.Enqueue(null);                                           // cancelled

        await vm.CloseAsync();

        Assert.Equal(1, f.Dialogs.PinRequests);
        Assert.NotNull(f.Ctx.Shifts.Current());
        Assert.Empty(f.Output.ShiftReports);
        Assert.Same(vm, f.Navigator.Current);
        Assert.NotNull(f.Session.Shift);
        Assert.NotNull(f.Session.Cashier);
        Assert.True(vm.MessageIsError);
        Assert.Equal("323.45", vm.UseTotalInstead.Text);                         // the count is kept
        Assert.True(vm.BackCommand.CanExecute(null));

        f.Dialogs.Pins.Enqueue("1111");                                         // a cashier PIN is not a supervisor PIN
        await vm.CloseAsync();
        Assert.NotNull(f.Ctx.Shifts.Current());
        Assert.Single(f.Ctx.Approvals.Unsynced(), a => a.Action == ApprovalAction.FailedSupervisorPin);
        Assert.DoesNotContain(f.Ctx.Approvals.Unsynced(), a => a.Action == ApprovalAction.ShiftClose);
    }

    [Theory]
    [InlineData("323.45", "0.00", "323.45")]
    [InlineData("300", "-23.45", "300.00")]
    [InlineData("400", "76.55", "400.00")]
    public async Task The_supervisor_close_is_logged_with_the_cash_difference(string cash, string diff, string shown)
    {
        var vm = Counted(cash);
        f.Dialogs.Pins.Enqueue("9999");

        await vm.CloseAsync();

        Assert.Null(f.Ctx.Shifts.Current());
        var approval = Assert.Single(f.Ctx.Approvals.Unsynced(), a => a.Action == ApprovalAction.ShiftClose);
        Assert.Equal("sup", approval.SupervisorId);
        Assert.Equal("simran", approval.CashierId);
        Assert.Equal(ShiftId, approval.ShiftClientId);
        Assert.Equal(M(diff), approval.Amount);
        // The PIN prompt (and the reason) shows only what the cashier entered, never the expected amount or the difference.
        Assert.Equal($"Close shift: counted cash {shown}, card machine 84.25", approval.Reason);
        var report = Assert.Single(f.Output.ShiftReports);
        Assert.Equal("Supervisor", report.ApprovedBy);                         // the name, not the id
        Assert.Null(report.FirstCountDifference);
    }

    [Fact]
    public async Task Back_and_a_second_close_are_refused_while_the_close_waits_for_the_supervisor()
    {
        var vm = Counted("313.45");
        var pin = new TaskCompletionSource<string?>();
        f.Dialogs.PendingPin = pin;

        var close = vm.CloseAsync();
        Assert.False(vm.BackCommand.CanExecute(null));
        vm.BackCommand.Execute(null);
        Assert.Same(vm, f.Navigator.Current);
        await vm.CloseAsync();                                                   // a second close does nothing
        Assert.Equal(1, f.Dialogs.PinRequests);

        pin.SetResult("9999");
        await close;
        Assert.Null(f.Ctx.Shifts.Current());
        Assert.Equal(-10m, Assert.Single(f.Output.ShiftReports).Closing.Modes.Single(m => m.ModeOfPayment == "Cash Counter 2").Difference);
    }

    // ---- Close ----

    [Fact]
    public async Task Closing_stores_the_closing_prints_the_report_and_goes_to_login()
    {
        var vm = Counted("323.45");
        f.Clock.Now = f.Clock.Now.AddMinutes(5);
        f.Dialogs.Pins.Enqueue("9999");

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
    public async Task The_card_total_left_blank_counts_as_zero()
    {
        var vm = OpenClose();
        vm.UseTotalInstead.Text = "323.2";
        f.Dialogs.Pins.Enqueue("9999");

        await vm.CloseAsync();

        var modes = Assert.Single(f.Output.ShiftReports).Closing.Modes;
        Assert.Equal(M("-0.25"), modes.Single(m => m.ModeOfPayment == "Cash Counter 2").Difference);
        Assert.Equal(M("-84.25"), modes.Single(m => m.ModeOfPayment == "Credit Card").Difference);
    }

    [Fact]
    public async Task A_printer_failure_still_closes_the_shift()
    {
        var vm = Counted("323.45");
        f.Output.Fail = true;
        f.Dialogs.Pins.Enqueue("9999");

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
        f.Dialogs.Pins.Enqueue("9999");
        await vm.CloseAsync();

        vm.BackCommand.Execute(null);
        await vm.CloseAsync();

        Assert.Same(login, f.Navigator.Current);
        Assert.Single(f.Output.ShiftReports);
        Assert.Equal(1, f.Dialogs.PinRequests);
    }

    [Fact]
    public async Task After_the_close_the_next_login_opens_a_new_shift()
    {
        var vm = Counted("323.45");
        f.Dialogs.Pins.Enqueue("9999");
        await vm.CloseAsync();

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
