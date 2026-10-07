using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TillPOS.Core.Security;
using TillPOS.Core.Shifts;

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

/// <summary>One payment mode on the close-shift result (all amounts already formatted).</summary>
public sealed record ShiftResultRow(string Mode, string Expected, string Counted, string Difference, bool HasDifference);

/// <summary>Close shift in two stages. Stage 1 is a blind count: the cashier counts the cash (by note and coin, or one total)
/// and enters the card machine's settlement total; nothing here shows or depends on the expected amounts, which are not even
/// worked out until the count is confirmed. Stage 2 shows expected / counted / difference per mode with the bill count and
/// totals; closing with a cash difference over <see cref="VarianceLimit"/> needs a supervisor. Closing stores the closing,
/// prints the shift (Z) report, logs the cashier out and goes to the login screen.</summary>
public sealed class CloseShiftViewModel : ObservableObject
{
    /// <summary>AED notes and coins, largest first.</summary>
    public static readonly IReadOnlyList<decimal> AedDenominations = [500m, 200m, 100m, 50m, 20m, 10m, 5m, 1m, 0.50m, 0.25m];

    /// <summary>A cash difference above this (either way) needs a supervisor; exactly this much does not.</summary>
    public const decimal VarianceLimit = 5.00m;

    private readonly TillContext ctx;
    private readonly SessionState session;
    private readonly SupervisorGate gate;
    private readonly Action back;
    private readonly Action done;
    private ShiftClosing? closing;
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
        Denominations = AedDenominations.Select(v => new Denomination(v)).ToList();
        foreach (var d in Denominations) d.Count.Changed += CountChanged;
        UseTotalInstead.Changed += CountChanged;
        CardTotal.Changed += () => OnPropertyChanged(nameof(CountedCard));

        ConfirmCountCommand = new RelayCommand(ConfirmCount);
        BackCommand = new RelayCommand(Back);
        CloseCommand = new AsyncRelayCommand(CloseAsync);
        RecountCommand = new RelayCommand(Recount);
    }

    // ---- Stage 1: blind count ----

    public IReadOnlyList<Denomination> Denominations { get; }

    /// <summary>The counted cash as one amount; when filled it wins over the denominations.</summary>
    public NumericEntry UseTotalInstead { get; } = new();

    /// <summary>The card machine's settlement total (blank counts as 0).</summary>
    public NumericEntry CardTotal { get; } = new();

    public decimal CountedCash => UseTotalInstead.Value ?? Denominations.Sum(d => d.Total);
    public string CashTotal => Format.Money(CountedCash);
    public decimal CountedCard => CardTotal.Value ?? 0m;

    public RelayCommand ConfirmCountCommand { get; }
    public RelayCommand BackCommand { get; }

    // ---- Stage 2: result (empty until the count is confirmed) ----

    public bool IsCounting => closing is null;
    public bool IsResult => closing is not null;
    public ObservableCollection<ShiftResultRow> Rows { get; } = [];
    public string SalesCount => closing?.Sales.ToString(CultureInfo.InvariantCulture) ?? "";
    public string GrandTotalText => closing is null ? "" : Format.Money(closing.GrandTotal);
    public string VatText => closing is null ? "" : Format.Money(closing.TotalTaxes);
    public decimal CashDifference => closing?.Modes.FirstOrDefault(m => m.ModeOfPayment == ctx.Modes.Cash)?.Difference ?? 0m;
    public string CashDifferenceText => closing is null ? "" : Format.Money(CashDifference);
    public bool NeedsSupervisor => closing is not null && Math.Abs(CashDifference) > VarianceLimit;

    public AsyncRelayCommand CloseCommand { get; }
    public RelayCommand RecountCommand { get; }

    public string Message { get => message; private set => SetProperty(ref message, value); }
    public bool MessageIsError { get => messageIsError; private set => SetProperty(ref messageIsError, value); }

    /// <summary>Works out the expected amounts (only now) against the confirmed count and shows the result.</summary>
    public void ConfirmCount()
    {
        if (closed || closing is not null) return;
        if (session.Shift is not { } opening) { Error("No open shift — log in again."); return; }
        ShiftClosing result;
        try
        {
            var counted = new Dictionary<string, decimal> { [ctx.Modes.Cash] = CountedCash, [ctx.Modes.Card] = CountedCard };
            result = ShiftCalculator.Close(opening, ctx.Receipts.ByShift(opening.ClientId), counted, ctx.Modes, ctx.Clock.Now,
                ctx.NewSaleContext().Money);
        }
        catch (Exception ex)
        {
            Error($"Could not work out the shift totals: {ex.Message}");
            return;
        }

        closing = result;
        Rows.Clear();
        foreach (var m in result.Modes)
            Rows.Add(new ShiftResultRow(m.ModeOfPayment, Format.Money(m.Expected), Format.Money(m.Counted), Format.Money(m.Difference),
                m.Difference != 0m));
        ResultChanged();
        if (NeedsSupervisor) Error($"The cash difference is over {Format.Money(VarianceLimit)} — a supervisor must approve closing the shift.");
        else Info("");
    }

    /// <summary>Closes the shift: supervisor approval for a cash difference over the limit (a refusal leaves the shift open),
    /// then store the closing, print the report (a printer failure does not undo the close), log out and show the login.</summary>
    public async Task CloseAsync()
    {
        if (closed || closing is null) return;
        if (session.Shift is not { } opening) { Error("No open shift — log in again."); return; }

        string? approvedBy = null;
        if (NeedsSupervisor)
        {
            var diff = CashDifference;
            approvedBy = await gate.ApproveAsync(ApprovalAction.ShiftVariance, $"Cash difference {Format.Money(diff)}", null, null, diff);
            if (approvedBy is null)
            {
                Error("The shift is still open: a supervisor must approve the cash difference, or recount.");
                return;
            }
        }

        var result = closing with { ClosedAt = ctx.Clock.Now };
        try
        {
            ctx.Shifts.Close(result);
        }
        catch (Exception ex)
        {
            Error($"Could not close the shift: {ex.Message}");
            return;
        }
        closed = true;

        try
        {
            ctx.Output.PrintShiftReport(opening, result, session.Cashier?.Name ?? opening.Cashier, approvedBy);
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

    private void Back()
    {
        if (!closed) back();
    }

    /// <summary>Back to the count, keeping what was entered.</summary>
    private void Recount()
    {
        if (closed || closing is null) return;
        closing = null;
        Rows.Clear();
        ResultChanged();
        Info("");
    }

    private void ResultChanged()
    {
        OnPropertyChanged(nameof(IsCounting));
        OnPropertyChanged(nameof(IsResult));
        OnPropertyChanged(nameof(SalesCount));
        OnPropertyChanged(nameof(GrandTotalText));
        OnPropertyChanged(nameof(VatText));
        OnPropertyChanged(nameof(CashDifference));
        OnPropertyChanged(nameof(CashDifferenceText));
        OnPropertyChanged(nameof(NeedsSupervisor));
    }

    private void Info(string text) { Message = text; MessageIsError = false; }

    private void Error(string text) { Message = text; MessageIsError = true; }
}
