using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TillPOS.Core.Catalog;
using TillPOS.Core.Money;
using TillPOS.Core.Payments;
using TillPOS.Core.Sales;
using TillPOS.Core.Security;

namespace TillPOS.Presentation;

public sealed record SaleLine(Guid Id, int No, string Name, string? Barcode, string Qty, string Price, string Offer, string Amount, bool FromScaleLabel);

/// <summary>The main sale screen. Removing a line, lowering a quantity and voiding the bill need a supervisor.
/// The cart is saved after every change and restored on start (power cut / crash).</summary>
public sealed class SaleViewModel : ObservableObject
{
    public const string AutosaveKey = "current_cart";
    /// <summary>The kv key holding the client id of the last completed bill (Ctrl+P reprints it, also after a restart).</summary>
    public const string LastReceiptKey = "last_receipt";
    /// <summary>"1" once the last bill has printed successfully (a later print is then a "*** COPY ***"), otherwise "0".</summary>
    public const string LastReceiptPrintedKey = "last_receipt_printed";
    public const int MaxHeld = 20;
    private const decimal MaxQty = 999m;
    private readonly TillContext ctx;
    private readonly SessionState session;
    private readonly SupervisorGate gate;
    private readonly Func<SaleViewModel, TenderKind, object> newPayment;
    private readonly SaleContext saleContext;
    private SaleLine? selectedLine;
    private string scanText = "";
    private string searchText = "";
    private string itemCount = "0";
    private string discount = "0.00";
    private string vat = "0.00";
    private string total = "0.00";
    private string message = "";
    private bool messageIsError;
    private int heldCount;

    public SaleViewModel(TillContext ctx, SessionState session, SupervisorGate gate, Func<SaleViewModel, TenderKind, object> newPayment)
    {
        this.ctx = ctx;
        this.session = session;
        this.gate = gate;
        this.newPayment = newPayment;
        saleContext = ctx.NewSaleContext();
        Cart = new Cart(saleContext);

        ScanEnteredCommand = new RelayCommand(() => { var code = ScanText.Trim(); ScanText = ""; if (code.Length > 0) Scan(code); });
        AddFromSearchCommand = new RelayCommand<string>(code => { if (code is not null) AddFromSearch(code); });
        IncrementCommand = new RelayCommand<Guid>(Increment);
        DecrementCommand = new AsyncRelayCommand<Guid>(DecrementAsync);
        RemoveCommand = new AsyncRelayCommand<Guid>(RemoveLineAsync);
        RemoveSelectedCommand = new AsyncRelayCommand(() => SelectedLine is { } l ? RemoveLineAsync(l.Id) : Task.CompletedTask);
        RemoveLastCommand = new AsyncRelayCommand(() => Cart.Lines.Count > 0 ? RemoveLineAsync(Cart.Lines[^1].Id) : Task.CompletedTask);
        SetQtySelectedCommand = new AsyncRelayCommand(() => SelectedLine is { } l ? SetQtyAsync(l.Id) : Task.CompletedTask);
        VoidBillCommand = new AsyncRelayCommand(VoidBillAsync);
        PayCashCommand = new RelayCommand(() => Pay(TenderKind.Cash));
        PayCardCommand = new RelayCommand(() => Pay(TenderKind.Card));
        PriceCheckCommand = new RelayCommand(PriceCheck);
        HoldCommand = new RelayCommand(Hold);
        RecallCommand = new RelayCommand(Recall);
        ReprintLastCommand = new RelayCommand(ReprintLast);

        RestoreAutosave();
        Refresh();
        RefreshHeldCount();
    }

    public Cart Cart { get; }
    public MoneySettings Money => saleContext.Money;
    public ObservableCollection<SaleLine> Lines { get; } = [];
    public ObservableCollection<Item> SearchResults { get; } = [];
    public SaleLine? SelectedLine { get => selectedLine; set => SetProperty(ref selectedLine, value); }
    public string ScanText { get => scanText; set => SetProperty(ref scanText, value); }
    public string ItemCount { get => itemCount; private set => SetProperty(ref itemCount, value); }
    public string Discount { get => discount; private set => SetProperty(ref discount, value); }
    public string Vat { get => vat; private set => SetProperty(ref vat, value); }
    public string Total { get => total; private set => SetProperty(ref total, value); }
    public string Message { get => message; private set => SetProperty(ref message, value); }
    public bool MessageIsError { get => messageIsError; private set => SetProperty(ref messageIsError, value); }
    /// <summary>Bills on hold (for the "Recall (n)" badge).</summary>
    public int HeldCount { get => heldCount; private set => SetProperty(ref heldCount, value); }

    public string SearchText
    {
        get => searchText;
        set
        {
            if (!SetProperty(ref searchText, value)) return;
            SearchResults.Clear();
            if (value.Trim().Length >= 2)
                foreach (var item in ctx.Search(value.Trim())) SearchResults.Add(item);
        }
    }

    public RelayCommand ScanEnteredCommand { get; }
    public RelayCommand<string> AddFromSearchCommand { get; }
    public RelayCommand<Guid> IncrementCommand { get; }
    public AsyncRelayCommand<Guid> DecrementCommand { get; }
    public AsyncRelayCommand<Guid> RemoveCommand { get; }
    public AsyncRelayCommand RemoveSelectedCommand { get; }
    public AsyncRelayCommand RemoveLastCommand { get; }
    public AsyncRelayCommand SetQtySelectedCommand { get; }
    public AsyncRelayCommand VoidBillCommand { get; }
    public RelayCommand PayCashCommand { get; }
    public RelayCommand PayCardCommand { get; }
    public RelayCommand PriceCheckCommand { get; }
    public RelayCommand HoldCommand { get; }
    public RelayCommand RecallCommand { get; }
    public RelayCommand ReprintLastCommand { get; }

    public void Scan(string code)
    {
        Feedback(Cart.AddBarcode(code), code);
        Changed();
    }

    public void AddFromSearch(string itemCode)
    {
        Feedback(Cart.AddItem(itemCode), itemCode);
        SearchText = "";
        Changed();
    }

    public void Increment(Guid id)
    {
        if (Find(id) is null) return;
        try
        {
            Cart.Increment(id);
            Changed();
        }
        catch (InvalidOperationException ex)
        {
            Error(ex.Message);
        }
    }

    public async Task DecrementAsync(Guid id)
    {
        if (Find(id) is not { } line) return;
        if (line.FromScaleLabel) { Error("Lines from a scale label take their quantity from the label."); return; }
        if (line.Qty <= 1m) return;
        if (await gate.ApproveAsync(ApprovalAction.LineVoid, $"Reduce {line.Item.ItemName}", null, line.Item.ItemCode, line.Rate) is null) return;
        Cart.Decrement(id);
        Changed();
    }

    public async Task SetQtyAsync(Guid id)
    {
        if (Find(id) is not { } line) return;
        if (line.FromScaleLabel) { Error("Lines from a scale label take their quantity from the label."); return; }
        var qty = await ctx.Dialogs.AskNumberAsync("Quantity", line.Item.ItemName);
        if (qty is not { } newQty || newQty <= 0m) return;
        if (newQty > MaxQty) { Error("Quantity must be 999 or less."); return; }
        if (newQty != decimal.Truncate(newQty) && !saleContext.WeightUoms.Contains(line.Uom)) { Error("This item is sold in whole units."); return; }
        if (newQty < line.Qty &&
            await gate.ApproveAsync(ApprovalAction.LineVoid, $"Lower {line.Item.ItemName} to {newQty.ToString(CultureInfo.InvariantCulture)}",
                null, line.Item.ItemCode, (line.Qty - newQty) * line.Rate) is null)
            return;
        Cart.SetQty(id, newQty);
        Changed();
    }

    public async Task RemoveLineAsync(Guid id)
    {
        if (Find(id) is not { } line) return;
        if (await gate.ApproveAsync(ApprovalAction.LineVoid, $"Remove {line.Item.ItemName}", null, line.Item.ItemCode, line.Qty * line.Rate) is null)
            return;
        Cart.Remove(id);
        Changed();
    }

    public async Task VoidBillAsync()
    {
        if (Cart.Lines.Count == 0) return;
        if (await gate.ApproveAsync(ApprovalAction.BillVoid, "Void the whole bill", null, null, Cart.Totals().GrandTotal) is null) return;
        Cart.Clear();
        Changed();
        Info("Bill voided.");
    }

    public void Pay(TenderKind kind)
    {
        if (Cart.Lines.Count == 0) { Error("Scan an item first."); return; }
        ctx.Navigator.Show(newPayment(this, kind));
    }

    /// <summary>F4: shows the price check; its "Add to bill" adds the item through the normal scan / search path.</summary>
    public void PriceCheck()
    {
        var pick = ctx.Dialogs.ShowPriceCheck(new PriceCheckViewModel(ctx));
        if (pick is null) return;
        if (pick.IsItemCode) AddFromSearch(pick.Code);
        else Scan(pick.Code);
    }

    /// <summary>F5: parks the bill (at most <see cref="MaxHeld"/>) and starts an empty one.</summary>
    public void Hold()
    {
        if (Cart.Lines.Count == 0) { Error("The bill is empty — nothing to hold."); return; }
        try
        {
            if (ctx.Held.List().Count >= MaxHeld)
            {
                Error($"{MaxHeld} bills are already on hold — recall or delete one first");
                return;
            }
            var now = ctx.Clock.Now;
            var label = $"{now.ToString("HH:mm", CultureInfo.InvariantCulture)} · {session.Cashier?.Name ?? ""} · " +
                $"{Cart.Lines.Count.ToString(CultureInfo.InvariantCulture)} items · {Format.Money(Cart.Totals().GrandTotal)}";
            ctx.Held.Hold(Cart, label, now);
        }
        catch (Exception ex)
        {
            Error($"Could not hold the bill: {ex.Message}");
            return;
        }
        Info("Bill put on hold");
        Changed();                                    // the cart is empty now: saves "[]" as the autosave
        RefreshHeldCount();
    }

    /// <summary>F7: recalls a held bill onto an empty bill (never onto, or merged with, the current one). Lines are re-priced.</summary>
    public void Recall()
    {
        if (Cart.Lines.Count > 0) { Error("Finish or hold the current bill first"); return; }
        RefreshHeldCount();
        if (HeldCount == 0) { Info("No bills on hold"); return; }

        var id = ctx.Dialogs.ShowHeldBills(new HeldBillsViewModel(ctx, session, gate));
        RefreshHeldCount();                           // a held bill may have been deleted in the dialog
        if (id is null) return;
        if (Cart.Lines.Count > 0) { Error("Finish or hold the current bill first"); return; }

        HeldCart? held;
        try
        {
            held = ctx.Held.Take(id);
        }
        catch (Exception ex)
        {
            Error($"Could not recall the bill: {ex.Message}");
            return;
        }
        if (held is null) { Error("That bill was already recalled"); RefreshHeldCount(); return; }
        IReadOnlyList<(HeldLine Line, AddOutcome Outcome)> failed;
        try
        {
            failed = Cart.Restore(held.Lines);
        }
        catch (Exception)
        {
            RecallFailed(held);
            return;
        }
        if (failed.Count > 0) Error($"{failed.Count.ToString(CultureInfo.InvariantCulture)} item(s) on the held bill can no longer be sold");
        else Info("Bill recalled");
        Changed();
        RefreshHeldCount();
    }

    /// <summary>Ctrl+P: prints the last completed bill again, marked "*** COPY ***". Never opens the drawer.</summary>
    public void ReprintLast()
    {
        Receipt? receipt;
        try
        {
            var id = ctx.Kv.GetValue(LastReceiptKey);
            receipt = string.IsNullOrEmpty(id) ? null : ctx.Receipts.Get(id);
        }
        catch (Exception ex)
        {
            Error($"Could not load the last receipt: {ex.Message}");
            return;
        }
        if (receipt is null) { Info("No receipt to reprint yet"); return; }
        try
        {
            ctx.Output.Print(receipt, openDrawer: false, copy: LastReceiptPrinted());
            Info($"Reprinted {receipt.ClientId}");
        }
        catch (Exception ex)
        {
            Error($"Reprint of {receipt.ClientId} failed: {ex.Message}");
            return;
        }
        MarkLastReceiptPrinted(true);
    }

    /// <summary>The recall could not restore the bill: it goes back on hold (same id, label and time) and the screen is left
    /// empty. If even that fails, the restored lines stay on the bill (and in the autosave) so nothing is lost.</summary>
    private void RecallFailed(HeldCart held)
    {
        try
        {
            ctx.Held.Put(held);
        }
        catch (Exception ex)
        {
            Changed();
            Error($"Could not recall the whole bill ({ex.Message}) — check the lines on the screen against the customer's items.");
            RefreshHeldCount();
            return;
        }
        Cart.Clear();
        Changed();
        Error("Could not recall the bill — it is still on hold");
        RefreshHeldCount();
    }

    /// <summary>Whether the last bill has already printed once (unknown counts as not printed: no COPY mark).</summary>
    private bool LastReceiptPrinted()
    {
        try
        {
            return ctx.Kv.GetValue(LastReceiptPrintedKey) == "1";
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void MarkLastReceiptPrinted(bool printed)
    {
        try
        {
            ctx.Kv.SetValue(LastReceiptPrintedKey, printed ? "1" : "0");
        }
        catch (Exception)
        {
            // Only decides whether a later reprint carries the COPY mark.
        }
    }

    public void ClearAutosave() => ctx.Kv.SetValue(AutosaveKey, "[]");

    /// <summary>Called by the payment screen after the bill was saved (the cart is already empty). Then shows the invoice
    /// popup (when enabled); a barcode scanned while it is open closes it and goes on the next bill.</summary>
    public void SaleCompleted(Receipt receipt, string? printError)
    {
        MarkLastReceiptPrinted(false);                // never let the previous bill's flag mark this one's first print as a copy
        try
        {
            ctx.Kv.SetValue(LastReceiptKey, receipt.ClientId);
        }
        catch (Exception)
        {
            // The bill is saved; only Ctrl+P "reprint last" misses it.
        }
        if (printError is null) MarkLastReceiptPrinted(true);
        Refresh();
        if (printError is null) Info($"Saved {receipt.ClientId}. Change {Format.Money(receipt.Change)}");
        else if (ctx.ShowReceiptPreview) Error($"Saved {receipt.ClientId}, but the printer failed ({printError}). Use Print again on the invoice, or note bill number {receipt.ClientId}.");
        else Error($"Saved {receipt.ClientId}, but the printer failed ({printError}). Reprint it with Ctrl+P, or note bill number {receipt.ClientId}.");
        ctx.Navigator.Show(this);
        if (!ctx.ShowReceiptPreview) return;

        // "Print again" is a COPY only once this bill has printed: the original, or an earlier Print again in this popup.
        var printed = printError is null;
        var code = ctx.Dialogs.ShowReceipt(receipt, printError, () =>
        {
            try
            {
                ctx.Output.Print(receipt, openDrawer: false, copy: printed);
                printed = true;
                MarkLastReceiptPrinted(true);
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        });
        if (!string.IsNullOrWhiteSpace(code)) Scan(code.Trim());
    }

    private void Feedback(AddResult result, string code)
    {
        switch (result.Outcome)
        {
            case AddOutcome.Added: Info($"Added {result.Line!.Item.ItemName}"); break;
            case AddOutcome.UnknownBarcode: Error($"Unknown barcode {code}"); break;
            case AddOutcome.InvalidScaleLabel: Error("Scale label could not be read — scan it again."); break;
            case AddOutcome.ItemNotSellable: Error("This item cannot be sold."); break;
            case AddOutcome.NoPrice: Error("No price for this item — call a supervisor."); break;
            case AddOutcome.UnsupportedTax: Error("Tax setup missing for this item — call a supervisor."); break;
            default: Error($"Cannot add {code} ({result.Outcome})."); break;
        }
    }

    private void Changed()
    {
        Refresh();
        try
        {
            ctx.Kv.SetValue(AutosaveKey, JsonSerializer.Serialize(Cart.Snapshot()));
        }
        catch (Exception)
        {
            Error("Could not save the unfinished bill — finish this sale normally.");
        }
    }

    private void RestoreAutosave()
    {
        if (ctx.Kv.GetValue(AutosaveKey) is not { } json) return;
        try
        {
            var held = JsonSerializer.Deserialize<List<HeldLine>>(json) ?? [];
            if (held.Count == 0) return;
            var failed = Cart.Restore(held);
            if (failed.Count > 0) Error($"{failed.Count} item(s) from the unfinished bill can no longer be sold.");
            else Info("Unfinished bill restored.");
        }
        catch (Exception)
        {
            ctx.Kv.SetValue("current_cart_bad", json);
            ClearAutosave();
            Cart.Clear();
            Error("The unfinished bill could not be restored.");
        }
    }

    private void Refresh()
    {
        var totals = Cart.Totals();
        var selectedId = SelectedLine?.Id;
        Lines.Clear();
        for (var i = 0; i < Cart.Lines.Count; i++)
        {
            var l = Cart.Lines[i];
            var qty = l.FromScaleLabel || l.Qty != decimal.Truncate(l.Qty)
                ? $"{l.Qty.ToString("0.000", CultureInfo.InvariantCulture)} {l.Uom}"
                : l.Qty.ToString("0", CultureInfo.InvariantCulture);
            Lines.Add(new SaleLine(l.Id, i + 1, l.Item.ItemName, l.Barcode, qty, Format.Money(l.Rate), l.Rule?.Label ?? "",
                Format.Money(totals.Lines[i].Amount), l.FromScaleLabel));
        }
        SelectedLine = Lines.FirstOrDefault(l => l.Id == selectedId);
        ItemCount = Cart.Lines.Count.ToString(CultureInfo.InvariantCulture);
        Discount = Format.Money(Cart.DiscountSaved());
        Vat = Format.Money(totals.TotalTaxes);
        Total = Format.Money(totals.GrandTotal);
    }

    private void RefreshHeldCount()
    {
        try
        {
            HeldCount = ctx.Held.List().Count;
        }
        catch (Exception)
        {
            // Keep the last count; the badge is only a hint.
        }
    }

    private CartLine? Find(Guid id) => Cart.Lines.FirstOrDefault(l => l.Id == id);

    private void Info(string text) { Message = text; MessageIsError = false; }

    private void Error(string text) { Message = text; MessageIsError = true; }
}
