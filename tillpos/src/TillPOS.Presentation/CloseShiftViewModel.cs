using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TillPOS.Core.Security;
using TillPOS.Core.Shifts;
using TillPOS.Data;

namespace TillPOS.Presentation;

/// <summary>One note or coin in the cash count: how many (whole numbers only) and their value.</summary>
public sealed class Denomination : ObservableObject
{
    public Denomination(decimal value)
    {
        Value = value;
        Count.Changed += () =>
        {
            OnPropertyChanged(nameof(Total));
            OnPropertyChanged(nameof(LineTotal));
        };
    }

    public decimal Value { get; }
    public string Label => Value >= 1m ? Value.ToString("0", CultureInfo.InvariantCulture) : Value.ToString("0.00", CultureInfo.InvariantCulture);
    public NumericEntry Count { get; } = new(wholeNumbers: true);
    public decimal Total => (Count.Value ?? 0m) * Value;
    public string LineTotal => Format.Money(Total);
}

/// <summary>Close shift, blind: the cashier counts the cash (by note and coin, or one total) and enters the card machine's
/// settlement total, then closes. The screen never shows (or works out before the close) the expected amounts or a
/// difference. Closing always needs a supervisor PIN (a refusal leaves the shift open); then the closing is stored, the shift
/// (Z) report prints with expected / counted / difference per mode for the supervisor, and the cashier is logged out.</summary>
public sealed class CloseShiftViewModel : ObservableObject
{
    /// <summary>AED notes and coins, largest first.</summary>
    public static readonly IReadOnlyList<decimal> AedDenominations = [500m, 200m, 100m, 50m, 20m, 10m, 5m, 1m, 0.50m, 0.25m];

    public const string NothingCountedMessage = "Enter the counted cash (type 0 if the drawer is empty)";

    private readonly TillContext ctx;
    private readonly SessionState session;
    private readonly SupervisorGate gate;
    private readonly Action back;
    private readonly Action done;
    private readonly CounterSettings counter;
    private bool busy;
    private bool closed;
    private string message = "";
    private bool messageIsError;

    public CloseShiftViewModel(TillContext ctx, SessionState session, SupervisorGate gate, Action back, Action done)
    {
        this.ctx = ctx;
        this.session = session;
        this.gate = gate;
        this.back = back;
        this.done = done;
        counter = ctx.CounterOf(session);
        Denominations = AedDenominations.Select(v => new Denomination(v)).ToList();
        foreach (var d in Denominations) d.Count.Changed += CountChanged;
        UseTotalInstead.Changed += CountChanged;
        CardTotal.Changed += () => OnPropertyChanged(nameof(CountedCard));

        BackCommand = new RelayCommand(Back, () => !closed && !busy);
        CloseCommand = new AsyncRelayCommand(CloseAsync);
        UploadWarning = WaitingBillsWarning();
    }

    /// <summary>"N bill(s) of this shift are still waiting to upload…" while bills of the shift are not in ERPNext yet, else "".</summary>
    public string UploadWarning { get; }

    private string WaitingBillsWarning()
    {
        try
        {
            if (session.Shift is not { } shift) return "";
            var waiting = ctx.Receipts.Outbox(shift.ClientId)
                .Count(e => e.Sync.Status is ReceiptSyncStatus.Pending or ReceiptSyncStatus.Failed);
            return waiting == 0 ? "" : string.Create(CultureInfo.InvariantCulture,
                $"{waiting} bill(s) of this shift are still waiting to upload. They will upload automatically; the Closing Shift waits for them.");
        }
        catch (Exception)
        {
            return "";
        }
    }

    public IReadOnlyList<Denomination> Denominations { get; }

    /// <summary>The counted cash as one amount; when filled it wins over the denominations.</summary>
    public NumericEntry UseTotalInstead { get; } = new();

    /// <summary>The card machine's settlement total (blank counts as 0).</summary>
    public NumericEntry CardTotal { get; } = new();

    public decimal CountedCash => UseTotalInstead.Value ?? Denominations.Sum(d => d.Total);
    public string CashTotal => Format.Money(CountedCash);
    public decimal CountedCard => CardTotal.Value ?? 0m;

    /// <summary>Whether any cash was entered: a count in some note or coin box (0 counts), or the total (0 allowed). The card
    /// total alone is not a cash count.</summary>
    public bool CashEntered => UseTotalInstead.Value is not null || Denominations.Any(d => d.Count.Value is not null);

    public RelayCommand BackCommand { get; }
    public AsyncRelayCommand CloseCommand { get; }

    public string Message { get => message; private set => SetProperty(ref message, value); }
    public bool MessageIsError { get => messageIsError; private set => SetProperty(ref messageIsError, value); }

    /// <summary>Closes the shift with the entered count: a supervisor PIN (a refusal leaves the shift open and the count as
    /// entered), then the closing is worked out and stored, the report prints (a printer failure does not undo the close), the
    /// cashier is logged out and the login shows.</summary>
    public async Task CloseAsync()
    {
        if (closed || busy) return;
        // A blank count is a slip, not "the drawer is empty".
        if (!CashEntered) { Error(NothingCountedMessage); return; }
        if (session.Shift is not { } opening) { Error("No open shift — log in again."); return; }
        ShiftClosing result;
        try
        {
            var counted = new Dictionary<string, decimal> { [counter.CashMode] = CountedCash, [counter.CardMode] = CountedCard };
            result = ShiftCalculator.Close(opening, ctx.Receipts.ByShift(opening.ClientId), counted, counter.Modes, ctx.Clock.Now,
                ctx.NewSaleContextFor(counter.PosProfile).Money);
        }
        catch (Exception ex)
        {
            Error($"Could not work out the shift totals: {ex.Message}");
            return;
        }

        busy = true;
        CommandsChanged();
        try
        {
            // The PIN prompt shows only what the cashier entered; the difference goes in the approval log and on the report.
            var cashDifference = result.Modes.FirstOrDefault(m => m.ModeOfPayment == counter.CashMode)?.Difference ?? 0m;
            var reason = $"Close shift: counted cash {Format.Money(CountedCash)}, card machine {Format.Money(CountedCard)}";
            var supervisor = await gate.ApproveBySupervisorAsync(ApprovalAction.ShiftClose, reason, null, null, cashDifference);
            if (supervisor is null)
            {
                Error("The shift is still open: closing it needs a supervisor PIN.");
                return;
            }

            var toStore = result with { ClosedAt = ctx.Clock.Now };
            try
            {
                ctx.Shifts.Close(toStore);
            }
            catch (Exception ex)
            {
                Error($"Could not close the shift: {ex.Message}");
                return;
            }
            closed = true;
            Closed(opening, toStore, supervisor.Name);   // the report names the supervisor (the approval log keeps the id)
        }
        finally
        {
            busy = false;
            CommandsChanged();
        }
    }

    /// <summary>After the shift is closed: print the report (a failure is only reported), log out, show the login.</summary>
    private void Closed(ShiftOpening opening, ShiftClosing result, string approvedBy)
    {
        try
        {
            ctx.Output.PrintShiftReport(opening, result, session.Cashier?.Name ?? opening.Cashier, approvedBy, null);
        }
        catch (Exception ex)
        {
            Error($"The shift is closed, but the shift report did not print ({ex.Message}).");
            ctx.Dialogs.Info(Message);
        }

        session.Shift = null;
        session.Cashier = null;
        done();
    }

    private void CountChanged()
    {
        OnPropertyChanged(nameof(CountedCash));
        OnPropertyChanged(nameof(CashTotal));
    }

    /// <summary>Back to the sale (not while the close waits for the supervisor, nor after it).</summary>
    private void Back()
    {
        if (closed || busy) return;
        back();
    }

    private void CommandsChanged() => BackCommand.NotifyCanExecuteChanged();

    private void Error(string text) { Message = text; MessageIsError = true; }
}
