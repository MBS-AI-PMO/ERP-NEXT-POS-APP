using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TillPOS.Core.Money;
using TillPOS.Core.Payments;
using TillPOS.Core.Sales;
using TillPOS.Core.Security;
using TillPOS.Core.Shifts;

namespace TillPOS.Presentation;

/// <summary>One open delivery in the list. <see cref="Old"/>: out for more than 7 days (shown in red).</summary>
public sealed record DeliveryRow(string ClientId, string Made, string Cashier, string Amount, string Age, bool Old);

/// <summary>The Deliveries screen: the open deliveries (oldest first) and the selected one's lines. Pay opens the normal payment
/// screen (exact cash prefilled); the paid bill keeps the delivery's number, is a sale of the current shift and counter, and is
/// saved together with the delivery's status. Removing / reducing lines and cancelling need a supervisor and are logged.</summary>
public sealed class DeliveriesViewModel : ObservableObject, IPaymentHost
{
    public const int OldAfterDays = 7;
    public static readonly IReadOnlyList<string> CancelReasons = ["Refused", "Not delivered", "Other"];

    private readonly TillContext ctx;
    private readonly SessionState session;
    private readonly SupervisorGate gate;
    private readonly SaleViewModel sale;
    private Delivery? selected;
    private string? selectedId;
    private string message = "";
    private bool messageIsError;

    public DeliveriesViewModel(TillContext ctx, SessionState session, SupervisorGate gate, SaleViewModel sale, string? select = null)
    {
        this.ctx = ctx;
        this.session = session;
        this.gate = gate;
        this.sale = sale;
        Cart = new Cart(ctx.SaleContextFor(session));
        PayCashCommand = new RelayCommand(() => Pay(TenderKind.Cash));
        PayCardCommand = new RelayCommand(() => Pay(TenderKind.Card));
        PaySplitCommand = new RelayCommand(() => Pay(TenderKind.Split));
        ReduceCommand = new AsyncRelayCommand<Guid>(ReduceLineAsync);
        RemoveCommand = new AsyncRelayCommand<Guid>(RemoveLineAsync);
        CancelCommand = new AsyncRelayCommand<string>(r => CancelAsync(r ?? "Other"));
        ReprintCommand = new RelayCommand(Reprint);
        BackCommand = new RelayCommand(Back);
        ScanCommand = new RelayCommand<string>(c => Scan(c ?? ""));
        Reload(select);
    }

    public ObservableCollection<DeliveryRow> Rows { get; } = [];
    public ObservableCollection<SaleLine> SelectedLines { get; } = [];
    public Cart Cart { get; private set; }
    public MoneySettings Money => Cart.Context.Money;
    public bool PrefillExactCash => true;
    public bool HasSelection => selected is not null;
    public string CashToCollect => selected is null ? "" : Format.Money(selected.CashToCollect);
    public string CardToCollect => selected is null ? "" : Format.Money(selected.CardToCollect);
    public string ItemCount => Cart.Lines.Count.ToString(CultureInfo.InvariantCulture);
    public string Discount => Format.Money(Cart.DiscountSaved());
    public string Vat => Cart.Lines.Count == 0 ? "0.00" : Format.Money(Cart.Totals().TotalTaxes);
    IReadOnlyList<SaleLine> IPaymentHost.Lines => SelectedLines;
    public string Message { get => message; private set => SetProperty(ref message, value); }
    public bool MessageIsError { get => messageIsError; private set => SetProperty(ref messageIsError, value); }

    /// <summary>The selected delivery's number; setting it loads that delivery's lines (at its stored prices).</summary>
    public string? SelectedId
    {
        get => selectedId;
        set
        {
            if (!SetProperty(ref selectedId, value)) return;
            Load(value);
        }
    }

    public RelayCommand PayCashCommand { get; }
    public RelayCommand PayCardCommand { get; }
    public RelayCommand PaySplitCommand { get; }
    public AsyncRelayCommand<Guid> ReduceCommand { get; }
    public AsyncRelayCommand<Guid> RemoveCommand { get; }
    public AsyncRelayCommand<string> CancelCommand { get; }
    public RelayCommand ReprintCommand { get; }
    public RelayCommand BackCommand { get; }
    public RelayCommand<string> ScanCommand { get; }

    /// <summary>Reduces a line's quantity: asks "Keep how many?" once, then one supervisor approval. A scale-label line can only be removed.</summary>
    public async Task ReduceLineAsync(Guid id)
    {
        if (selected is null || Cart.Lines.FirstOrDefault(l => l.Id == id) is not { } line) return;
        if (line.FromScaleLabel) { await RemoveLineAsync(id); return; }
        var answer = await ctx.Dialogs.AskNumberAsync("Keep how many?", $"{line.Item.ItemName} (now {line.Qty.ToString("0.###", CultureInfo.InvariantCulture)})");
        if (answer is not { } keep) return;
        if (keep <= 0m) { Error("To remove the line use ✕"); return; }
        if (keep >= line.Qty) { Info("Nothing to reduce"); return; }
        if (keep != decimal.Truncate(keep) && !Cart.Context.WeightUoms.Contains(line.Uom)) { Error("This item is sold in whole units."); return; }
        if (await gate.ApproveAsync(ApprovalAction.DeliveryChange,
                $"Delivery {selected.ClientId}: keep {keep.ToString("0.###", CultureInfo.InvariantCulture)} of {line.Item.ItemName}",
                selected.ClientId, line.Item.ItemCode, (line.Qty - keep) * line.Rate) is null) return;
        Cart.SetQty(id, keep);
        SaveChange();
    }

    /// <summary>Removes a line (supervisor); removing the last line cancels the delivery (approved and logged as a cancel).</summary>
    public async Task RemoveLineAsync(Guid id)
    {
        if (selected is null || Cart.Lines.FirstOrDefault(l => l.Id == id) is not { } line) return;
        var last = Cart.Lines.Count == 1;
        var supervisor = last
            ? await gate.ApproveBySupervisorAsync(ApprovalAction.DeliveryCancel, $"Cancel delivery {selected.ClientId}: all items removed",
                selected.ClientId, null, selected.Bill.GrandTotal)
            : await gate.ApproveBySupervisorAsync(ApprovalAction.DeliveryChange, $"Delivery {selected.ClientId}: remove {line.Item.ItemName}",
                selected.ClientId, line.Item.ItemCode, line.Qty * line.Rate);
        if (supervisor is null) return;
        Cart.Remove(id);
        if (last)
        {
            CancelStored(selected.ClientId, supervisor.Id, "All items removed", $"Delivery {selected.ClientId} cancelled (all items removed)");
            return;
        }
        SaveChange();
    }

    public async Task CancelAsync(string reason)
    {
        if (selected is null) return;
        var supervisor = await gate.ApproveBySupervisorAsync(ApprovalAction.DeliveryCancel, $"Cancel delivery {selected.ClientId}: {reason}",
            selected.ClientId, null, selected.Bill.GrandTotal);
        if (supervisor is null) return;
        CancelStored(selected.ClientId, supervisor.Id, reason, $"Delivery {selected.ClientId} cancelled ({reason})");
    }

    /// <summary>Scanned delivery slip: selects that open delivery, or says why not.</summary>
    public void Scan(string code)
    {
        code = (code ?? "").Trim();
        Delivery? d = null;
        try
        {
            d = code.Length == 0 ? null : ctx.Deliveries.Get(code);
        }
        catch (Exception)
        {
            // Treated as not found below.
        }
        if (d is null) { Error($"No open delivery {code} on this till"); return; }
        if (d.Status == DeliveryStatus.Paid) { Error($"Delivery {code} is already paid"); return; }
        if (d.Status == DeliveryStatus.Cancelled) { Error($"Delivery {code} is already cancelled"); return; }
        if (Rows.All(r => r.ClientId != code)) Reload(code);
        else SelectedId = code;
        Info($"Delivery {code} opened");
    }

    private void CancelStored(string clientId, string by, string reason, string text)
    {
        bool done;
        try
        {
            done = ctx.Deliveries.Cancel(clientId, ctx.Clock.Now, by, reason);
        }
        catch (Exception ex)
        {
            ReloadAfterFailure($"Could not cancel: {ex.Message}");
            return;
        }
        Close(done, text);
    }

    /// <summary>A store call failed: show the error and bring the screen and cart back to what is stored.</summary>
    private void ReloadAfterFailure(string error)
    {
        try
        {
            Reload(selected?.ClientId);
        }
        catch (Exception)
        {
            // Nothing more can be done; the error below still tells the cashier.
        }
        Error(error);
    }

    public Receipt Record(PaymentPlan plan, Cashier cashier, ShiftOpening shift)
    {
        if (selected is not { } d) throw new InvalidOperationException("No delivery is selected.");
        var counter = ctx.CounterOf(session);
        var receipt = new SaleRecorder(ctx.Receipts, ctx.TillNumber, counter.Modes, () => ctx.Clock.Now, counter.DisplayName)
            .Build(Cart, plan, cashier.Id, shift.ClientId, cashier.User, cashier.Name, d.ClientId);
        if (!ctx.Deliveries.Pay(receipt, receipt.CreatedAt, cashier.Id))
            throw new InvalidOperationException($"Delivery {d.ClientId} was already paid or cancelled.");
        Cart.Clear();
        return receipt;
    }

    public void Completed(Receipt receipt, string? printError) => sale.SaleCompleted(receipt, printError);

    public void BackFromPayment() => ctx.Navigator.Show(this);

    private void Pay(TenderKind kind)
    {
        if (session.Shift is null) { Error("Open a shift first"); return; }
        if (selected is null) { Error("Choose a delivery first."); return; }
        ctx.Navigator.Show(new PaymentViewModel(ctx, session, this, kind));
    }

    private void Reprint()
    {
        if (selected is null) return;
        try
        {
            ctx.Output.PrintDelivery(selected, copy: true);
            Info($"Delivery slip {selected.ClientId} printed again");
        }
        catch (Exception ex)
        {
            Error($"The slip did not print ({ex.Message}).");
        }
    }

    private void Back()
    {
        sale.RefreshDeliveryCount();
        ctx.Navigator.Show(sale);
    }

    private void SaveChange()
    {
        if (selected is null) return;
        var changed = Deliveries.Rebill(selected, Cart);
        bool updated;
        try
        {
            updated = ctx.Deliveries.Update(changed);
        }
        catch (Exception ex)
        {
            ReloadAfterFailure($"Could not save the change: {ex.Message}");
            return;
        }
        if (!updated) { Error($"Delivery {selected.ClientId} is no longer open."); Reload(null); return; }
        selected = changed;
        Info($"Delivery {changed.ClientId} changed: collect {Format.Money(changed.CashToCollect)} cash or {Format.Money(changed.CardToCollect)} card");
        Reload(changed.ClientId);
    }

    private void Close(bool done, string text)
    {
        if (done) Info(text);
        else Error("That delivery is no longer open.");
        Reload(null);
        sale.RefreshDeliveryCount();
    }

    private void Reload(string? select)
    {
        Rows.Clear();
        var now = ctx.Clock.Now;
        foreach (var d in ctx.Deliveries.Open())
        {
            var age = now - d.CreatedAt;
            Rows.Add(new DeliveryRow(d.ClientId, d.CreatedAt.ToString("dd/MM HH:mm", CultureInfo.InvariantCulture), d.CashierName ?? d.Cashier,
                Format.Money(d.CardToCollect), Age(age), age > TimeSpan.FromDays(OldAfterDays)));
        }
        var target = select is not null && Rows.Any(r => r.ClientId == select) ? select : Rows.FirstOrDefault()?.ClientId;
        selectedId = null;                                  // force a reload even when the same id stays selected
        SelectedId = target;
        if (target is null) Load(null);
    }

    private void Load(string? id)
    {
        selected = id is null ? null : ctx.Deliveries.Get(id);
        Cart = new Cart(ctx.SaleContextFor(session));
        if (selected is not null) Cart.RestoreFixed(selected.Bill.Lines);
        SelectedLines.Clear();
        foreach (var line in SaleViewModel.LinesOf(Cart)) SelectedLines.Add(line);
        OnPropertyChanged(nameof(Cart));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CashToCollect));
        OnPropertyChanged(nameof(CardToCollect));
        OnPropertyChanged(nameof(ItemCount));
        OnPropertyChanged(nameof(Vat));
        OnPropertyChanged(nameof(Discount));
    }

    private static string Age(TimeSpan age) =>
        age.TotalDays >= 1 ? $"{(int)age.TotalDays} d {age.Hours} h"
        : age.TotalHours >= 1 ? $"{(int)age.TotalHours} h {age.Minutes} min"
        : $"{Math.Max(0, (int)age.TotalMinutes)} min";

    private void Info(string text) { Message = text; MessageIsError = false; }
    private void Error(string text) { Message = text; MessageIsError = true; }
}
