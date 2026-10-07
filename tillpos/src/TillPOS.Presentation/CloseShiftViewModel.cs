using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
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

/// <summary>What the confirmed counts of one shift have shown so far (kv <c>close_count:{shift}</c>). It outlives the close
/// screen, so going back, reopening it or restarting the till never gives a fresh blind count: the first difference is never
/// overwritten and OverLimit only ever turns on.</summary>
public sealed record CloseCountState(decimal? FirstCashDifference, bool OverLimit, int Confirmations);

/// <summary>Close shift in two stages. Stage 1 is a blind count: the cashier counts the cash (by note and coin, or one total)
/// and enters the card machine's settlement total; nothing here shows or depends on the expected amounts, which are not even
/// worked out until the count is confirmed. Stage 2 shows expected / counted / difference per mode with the bill count and
/// totals; closing with a cash difference over <see cref="VarianceLimit"/> needs a supervisor. A recount cannot avoid that:
/// once any confirmed count of the shift was over the limit, the supervisor is needed until the shift closes, and the first
/// count's difference goes in the approval and on the report. That state is stored per shift (<see cref="CloseCountState"/>),
/// every confirmed count is logged (<see cref="ApprovalAction.ShiftCount"/>), and once a count is recorded there is no way
/// back to the sale: recount or close. Closing stores the closing, prints the shift (Z) report, logs the cashier out and goes
/// to the login screen.</summary>
public sealed class CloseShiftViewModel : ObservableObject
{
    /// <summary>AED notes and coins, largest first.</summary>
    public static readonly IReadOnlyList<decimal> AedDenominations = [500m, 200m, 100m, 50m, 20m, 10m, 5m, 1m, 0.50m, 0.25m];

    /// <summary>A cash difference above this (either way) needs a supervisor; exactly this much does not.</summary>
    public const decimal VarianceLimit = 5.00m;

    public const string CountRecordedMessage = "The count is recorded — recount or close the shift";
    public const string NothingCountedMessage = "Enter the counted cash (type 0 if the drawer is empty)";

    private readonly TillContext ctx;
    private readonly SessionState session;
    private readonly SupervisorGate gate;
    private readonly Action back;
    private readonly Action done;
    private ShiftClosing? closing;
    private CloseCountState state;
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
        Denominations = AedDenominations.Select(v => new Denomination(v)).ToList();
        foreach (var d in Denominations) d.Count.Changed += CountChanged;
        UseTotalInstead.Changed += CountChanged;
        CardTotal.Changed += () => OnPropertyChanged(nameof(CountedCard));

        state = LoadState();
        ConfirmCountCommand = new RelayCommand(ConfirmCount);
        BackCommand = new RelayCommand(Back, () => !closed && !busy && state.Confirmations == 0);
        CloseCommand = new AsyncRelayCommand(CloseAsync);
        RecountCommand = new RelayCommand(Recount, () => closing is not null && !closed && !busy);
        if (state.Confirmations > 0) Info("A count is already recorded for this shift — count again, then close the shift.");
    }

    /// <summary>The kv key holding a shift's <see cref="CloseCountState"/>.</summary>
    public static string StateKey(string shiftClientId) => "close_count:" + shiftClientId;

    // ---- Stage 1: blind count ----

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
    public bool NeedsSupervisor => closing is not null && (state.OverLimit || Math.Abs(CashDifference) > VarianceLimit);

    /// <summary>The cash difference of the shift's first confirmed count (also from an earlier visit to this screen or before
    /// a restart); null until a count is confirmed.</summary>
    public decimal? FirstCashDifference => state.FirstCashDifference;

    public AsyncRelayCommand CloseCommand { get; }
    public RelayCommand RecountCommand { get; }

    public string Message { get => message; private set => SetProperty(ref message, value); }
    public bool MessageIsError { get => messageIsError; private set => SetProperty(ref messageIsError, value); }

    /// <summary>Works out the expected amounts (only now) against the confirmed count and shows the result.</summary>
    public void ConfirmCount()
    {
        if (closed || busy || closing is not null) return;
        // A blank count is a slip, not "the drawer is empty": refuse it before anything is worked out or recorded.
        if (!CashEntered) { Error(NothingCountedMessage); return; }
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

        // Record the count (state, then the audit row) before anything is shown; if that fails, nothing is revealed.
        var diff = result.Modes.FirstOrDefault(m => m.ModeOfPayment == ctx.Modes.Cash)?.Difference ?? 0m;
        var next = new CloseCountState(state.FirstCashDifference ?? diff, state.OverLimit || Math.Abs(diff) > VarianceLimit,
            state.Confirmations + 1);
        try
        {
            ctx.Kv.SetValue(StateKey(opening.ClientId), JsonSerializer.Serialize(next));
            state = next;
            ctx.Approvals.Add(new ApprovalRecord(Guid.NewGuid().ToString("N"), ApprovalAction.ShiftCount, session.Cashier?.Id ?? "", "",
                opening.ClientId, null, null, diff,
                $"Count {next.Confirmations.ToString(CultureInfo.InvariantCulture)}: cash difference {Format.Money(diff)}", ctx.Clock.Now));
        }
        catch (Exception ex)
        {
            CommandsChanged();
            Error($"Could not record the count ({ex.Message}) — confirm it again.");
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
        if (closed || busy || closing is not { } toClose) return;
        if (session.Shift is not { } opening) { Error("No open shift — log in again."); return; }
        var finalDifference = CashDifference;
        var firstDiffers = FirstCountDiffers();

        busy = true;
        CommandsChanged();
        try
        {
            string? approvedBy = null;
            if (NeedsSupervisor)
            {
                // The larger difference (first count or final), with its sign.
                var amount = firstDiffers is { } first && Math.Abs(first) > Math.Abs(finalDifference) ? first : finalDifference;
                var reason = $"Cash difference {Format.Money(finalDifference)}" +
                    (firstDiffers is { } f ? $" (first count {Format.Money(f)})" : "");
                approvedBy = await gate.ApproveAsync(ApprovalAction.ShiftVariance, reason, null, null, amount);
                if (approvedBy is null)
                {
                    Error("The shift is still open: a supervisor must approve the cash difference, or recount.");
                    return;
                }
            }

            var result = toClose with { ClosedAt = ctx.Clock.Now };
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
            Closed(opening, result, approvedBy, firstDiffers);
        }
        finally
        {
            busy = false;
            CommandsChanged();
        }
    }

    /// <summary>After the shift is closed: print the report (a failure is only reported), log out, show the login.</summary>
    private void Closed(ShiftOpening opening, ShiftClosing result, string? approvedBy, decimal? firstDiffers)
    {
        try
        {
            ctx.Output.PrintShiftReport(opening, result, session.Cashier?.Name ?? opening.Cashier, approvedBy, firstDiffers);
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

    /// <summary>The first count's cash difference when a recount changed it, otherwise null.</summary>
    private decimal? FirstCountDiffers() => state.FirstCashDifference is { } first && first != CashDifference ? first : null;

    /// <summary>The shift's stored count state. Unreadable state fails closed: treated as an over-limit count already
    /// recorded, so a supervisor is needed and there is no way back.</summary>
    private CloseCountState LoadState()
    {
        if (session.Shift is not { } shift) return new CloseCountState(null, false, 0);
        try
        {
            return ctx.Kv.GetValue(StateKey(shift.ClientId)) is { } json
                ? JsonSerializer.Deserialize<CloseCountState>(json) ?? throw new JsonException("empty")
                : new CloseCountState(null, false, 0);
        }
        catch (Exception)
        {
            return new CloseCountState(null, true, 1);
        }
    }

    private void CountChanged()
    {
        OnPropertyChanged(nameof(CountedCash));
        OnPropertyChanged(nameof(CashTotal));
    }

    /// <summary>Back to the sale, only while no count has been recorded for this shift (Esc calls this even when the button
    /// is disabled, so it says why).</summary>
    private void Back()
    {
        if (closed || busy) return;
        if (state.Confirmations > 0) { Error(CountRecordedMessage); return; }
        back();
    }

    /// <summary>Back to the count, keeping what was entered.</summary>
    private void Recount()
    {
        if (closed || busy || closing is null) return;
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
        OnPropertyChanged(nameof(FirstCashDifference));
        CommandsChanged();
    }

    private void CommandsChanged()
    {
        BackCommand.NotifyCanExecuteChanged();
        RecountCommand.NotifyCanExecuteChanged();
    }

    private void Info(string text) { Message = text; MessageIsError = false; }

    private void Error(string text) { Message = text; MessageIsError = true; }
}
