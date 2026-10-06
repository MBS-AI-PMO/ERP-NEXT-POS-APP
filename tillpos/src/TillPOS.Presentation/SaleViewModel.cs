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

        RestoreAutosave();
        Refresh();
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

    public void ClearAutosave() => ctx.Kv.SetValue(AutosaveKey, "[]");

    /// <summary>Called by the payment screen after the bill was saved (the cart is already empty).</summary>
    public void SaleCompleted(Receipt receipt, string? printError)
    {
        Refresh();
        if (printError is null) Info($"Saved {receipt.ClientId}. Change {Format.Money(receipt.Change)}");
        else Error($"Saved {receipt.ClientId}, but the printer failed ({printError}). Note bill number {receipt.ClientId} — reprint is not available yet.");
        ctx.Navigator.Show(this);
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

    private CartLine? Find(Guid id) => Cart.Lines.FirstOrDefault(l => l.Id == id);

    private void Info(string text) { Message = text; MessageIsError = false; }

    private void Error(string text) { Message = text; MessageIsError = true; }
}
