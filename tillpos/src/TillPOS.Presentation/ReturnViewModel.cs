using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TillPOS.Core.Catalog;
using TillPOS.Core.Payments;
using TillPOS.Core.Sales;
using TillPOS.Core.Security;
using TillPOS.Data;

namespace TillPOS.Presentation;

public enum ReturnStage { Find, Choose, NoReceipt }

/// <summary>A recent sale on the Returns screen's find stage. <see cref="Returned"/> is "", "partly returned" or "all returned".</summary>
public sealed record RecentBill(string ClientId, string Time, string Total, string Returned)
{
    public bool HasReturns => Returned.Length > 0;
}

/// <summary>How a finished refund went, for the sale screen: its message, and a barcode scanned to close the credit-note popup
/// (it goes on the next bill), if any.</summary>
public sealed record ReturnDone(Receipt Receipt, string Message, bool IsError, string? ScannedCode);

/// <summary>One line of the original sale on the "with receipt" stage: what was sold, already returned and can still be
/// returned, and how much to return now (whole numbers only for piece lines; a scale-label or weighed line takes any weight).</summary>
public sealed class ReturnLine : ObservableObject
{
    public ReturnLine(ReceiptLine sold, decimal returned)
    {
        Sold = sold;
        Returnable = sold.Qty - returned;
        IsWeighed = sold.FromScaleLabel || sold.Qty != decimal.Truncate(sold.Qty);
        SoldText = Qty(sold.Qty);
        ReturnedText = Qty(returned);
        ReturnableText = Qty(Returnable);
        ReturnQty = new NumericEntry(wholeNumbers: !IsWeighed);
        IncrementCommand = new RelayCommand(() => Increment());
        DecrementCommand = new RelayCommand(Decrement);
        AllCommand = new RelayCommand(All);
    }

    public ReceiptLine Sold { get; }
    public int LineNo => Sold.LineNo;
    public string ItemName => Sold.ItemName;
    public string Rate => Format.Money(Sold.Rate);
    /// <summary>A scale-label line, or a line sold in part units: its return quantity may have decimals.</summary>
    public bool IsWeighed { get; }
    public decimal Returnable { get; }
    public string SoldText { get; }
    public string ReturnedText { get; }
    public string ReturnableText { get; }
    public NumericEntry ReturnQty { get; }
    /// <summary>The quantity chosen for this return (0 when blank).</summary>
    public decimal Chosen => ReturnQty.Value ?? 0m;

    public RelayCommand IncrementCommand { get; }
    public RelayCommand DecrementCommand { get; }
    public RelayCommand AllCommand { get; }

    /// <summary>One more piece, up to what can still be returned; a weighed line goes straight to its whole remaining weight.
    /// False when nothing more can be added.</summary>
    public bool Increment()
    {
        if (Chosen >= Returnable) return false;
        ReturnQty.Set(IsWeighed ? Returnable : Math.Min(Chosen + 1m, Returnable));
        return true;
    }

    /// <summary>One piece less; a weighed line is cleared.</summary>
    public void Decrement()
    {
        if (IsWeighed || Chosen <= 1m) ReturnQty.Clear();
        else ReturnQty.Set(Chosen - 1m);
    }

    public void All()
    {
        if (Returnable > 0m) ReturnQty.Set(Returnable);
        else ReturnQty.Clear();
    }

    private string Qty(decimal qty) => IsWeighed
        ? $"{qty.ToString("0.000", CultureInfo.InvariantCulture)} {Sold.Uom}"
        : qty.ToString("0", CultureInfo.InvariantCulture);
}

/// <summary>The Returns screen (F6). Find the sale (typed or scanned receipt number, or a recent bill), choose the lines and
/// quantities and a reason, then confirm: one supervisor PIN covers every reason the return needs (one approval row is
/// logged per reason), the credit note is saved, then the drawer opens and it prints (a printer failure never loses it).
/// A return without a receipt scans the items into a return list at today's prices and always needs a supervisor.
/// A number that is not on this till is looked up in the bills of the other tills downloaded from ERPNext (by their number or
/// ERPNext name): such a bill is returned against its ERPNext name, and the returns of every till count toward what is left.
/// The refund is always cash.</summary>
public sealed class ReturnViewModel : ObservableObject
{
    /// <summary>Refunds on one receipt that add up to more than this need a supervisor.</summary>
    public const decimal ApprovalLimit = 50m;
    /// <summary>Receipts older than this many calendar days need a supervisor.</summary>
    public const int MaxAgeDays = 7;
    public const int RecentCount = 50;
    public const string SomethingChangedMessage = "Something changed — confirm again";
    public const string FromOtherTillMessage = "Bill from another till — found in ERPNext.";
    public const string CannotRepriceMessage = "This bill has a discount or tax the till can't re-price — return it in ERPNext.";
    /// <summary>How far the till's own pricing of another till's bill may be from ERPNext's grand total.</summary>
    public const decimal RepriceTolerance = 0.01m;

    public static readonly IReadOnlyList<string> Reasons = ["Changed mind", "Damaged", "Expired", "Wrong item", "Other"];

    private readonly TillContext ctx;
    private readonly SessionState session;
    private readonly SupervisorGate gate;
    private readonly Action back;
    private readonly Action<ReturnDone>? done;
    private readonly SaleContext saleContext;
    private readonly ReturnBuilder builder;
    private ReturnStage stage = ReturnStage.Find;
    private Receipt? original;
    private string? originalNumber;
    private Cart? returnCart;
    private ReturnPreview? preview;
    private string findText = "";
    private string scanText = "";
    private string searchText = "";
    private string? reason;
    private string message = "";
    private bool messageIsError;
    private bool previewFailed;
    private bool busy;
    private bool completed;

    /// <param name="back">Back to the sale screen (Esc on the find stage).</param>
    /// <param name="done">Called once the refund is saved, printed and the popup closed (back to the sale with the message);
    /// when null, <paramref name="back"/> is called.</param>
    public ReturnViewModel(TillContext ctx, SessionState session, SupervisorGate gate, Action back, Action<ReturnDone>? done = null)
    {
        this.ctx = ctx;
        this.session = session;
        this.gate = gate;
        this.back = back;
        this.done = done;
        var counter = ctx.CounterOf(session);
        saleContext = ctx.NewSaleContextFor(counter.PosProfile);
        builder = new ReturnBuilder(ctx.Receipts, saleContext, ctx.TillNumber, counter.Modes, () => ctx.Clock.Now, ApprovalLimit, MaxAgeDays,
            counter.DisplayName, ctx.RemoteReceipts);

        FindCommand = new RelayCommand(() => Find(FindText));
        OpenBillCommand = new RelayCommand<string>(id => { if (id is not null) Find(id); });
        NoReceiptCommand = new RelayCommand(StartWithoutReceipt);
        ReturnAllCommand = new RelayCommand(ReturnAll);
        SetReasonCommand = new RelayCommand<string>(r => { if (r is not null && Reasons.Contains(r)) Reason = r; });
        ScanEnteredCommand = new RelayCommand(() => { var code = ScanText.Trim(); ScanText = ""; if (code.Length > 0) Scan(code); });
        AddFromSearchCommand = new RelayCommand<string>(code => { if (code is not null) AddFromSearch(code); });
        DecrementCartLineCommand = new RelayCommand<Guid>(DecrementCartLine);
        RemoveCartLineCommand = new RelayCommand<Guid>(RemoveCartLine);
        ConfirmCommand = new AsyncRelayCommand(ConfirmAsync, () => !completed && !busy);
        BackCommand = new RelayCommand(Back, () => !completed && !busy);

        LoadRecentBills();
    }

    // ---- Stage ----

    public ReturnStage Stage
    {
        get => stage;
        private set
        {
            if (!SetProperty(ref stage, value)) return;
            OnPropertyChanged(nameof(IsFind));
            OnPropertyChanged(nameof(IsChoose));
            OnPropertyChanged(nameof(IsNoReceipt));
        }
    }

    public bool IsFind => stage == ReturnStage.Find;
    public bool IsChoose => stage == ReturnStage.Choose;
    public bool IsNoReceipt => stage == ReturnStage.NoReceipt;

    // ---- 1. Find ----

    public string FindText { get => findText; set => SetProperty(ref findText, value); }
    public ObservableCollection<RecentBill> RecentBills { get; } = [];
    public RelayCommand FindCommand { get; }
    public RelayCommand<string> OpenBillCommand { get; }
    public RelayCommand NoReceiptCommand { get; }

    // ---- 2. Choose (with receipt) ----

    /// <summary>The receipt being returned against (null on the other stages).</summary>
    public Receipt? Original => original;
    /// <summary>The receipt's number (a bill of another till: its ERPNext name, then its till number), time and total.</summary>
    public string OriginalText => original is null ? "" :
        $"{original.ClientId}{(originalNumber is { } number ? $" ({number})" : "")} · " +
        $"{original.CreatedAt.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)} · {Format.Money(original.GrandTotal)}";
    public ObservableCollection<ReturnLine> Lines { get; } = [];
    public RelayCommand ReturnAllCommand { get; }

    // ---- 3b. Without receipt ----

    public string ScanText { get => scanText; set => SetProperty(ref scanText, value); }
    public ObservableCollection<SaleLine> CartLines { get; } = [];
    public RelayCommand ScanEnteredCommand { get; }
    /// <summary>Items matching <see cref="SearchText"/> (two letters or more), for an item without a readable barcode.</summary>
    public ObservableCollection<Item> SearchResults { get; } = [];
    public RelayCommand<string> AddFromSearchCommand { get; }

    public string SearchText
    {
        get => searchText;
        set
        {
            if (!SetProperty(ref searchText, value)) return;
            SearchResults.Clear();
            if (value.Trim().Length < 2) return;
            try
            {
                foreach (var item in ctx.Search(value.Trim())) SearchResults.Add(item);
            }
            catch (Exception ex)
            {
                Error($"Could not search the items: {ex.Message}");
            }
        }
    }
    public RelayCommand<Guid> DecrementCartLineCommand { get; }
    public RelayCommand<Guid> RemoveCartLineCommand { get; }

    // ---- Reason, refund panel, confirm ----

    public string? Reason { get => reason; set => SetProperty(ref reason, value); }
    public RelayCommand<string> SetReasonCommand { get; }

    /// <summary>What the return would refund now (null when nothing is chosen or the choice is not valid).</summary>
    public ReturnPreview? Preview { get => preview; private set => SetProperty(ref preview, value); }
    /// <summary>The refund before rounding (shown positive).</summary>
    public string RefundTotalText => preview is null ? "" : Format.Money(-preview.GrandTotal);
    public string VatText => preview is null ? "" : Format.Money(-preview.TotalTaxes);
    /// <summary>The cash to pay out (rounded, shown positive).</summary>
    public string RefundDueText => preview is null ? "" : Format.Money(-preview.RefundDue);
    /// <summary>"Needs supervisor: …" listing every approval the return needs, or "".</summary>
    public string NeedsText => preview is null || preview.Needs.Count == 0 ? "" : $"Needs supervisor: {NeedsList(preview.Needs)}";

    public string Message { get => message; private set => SetProperty(ref message, value); }
    public bool MessageIsError { get => messageIsError; private set => SetProperty(ref messageIsError, value); }

    public AsyncRelayCommand ConfirmCommand { get; }
    public RelayCommand BackCommand { get; }

    /// <summary>A scanned code: a receipt number on the find stage, an item of the receipt on the choose stage (one more piece,
    /// or a weighed line's whole remaining weight), an item for the return list without a receipt.</summary>
    public void Scan(string code)
    {
        code = code.Trim();
        if (code.Length == 0 || busy || completed) return;
        switch (stage)
        {
            case ReturnStage.Find: Find(code); break;
            case ReturnStage.Choose: ScanOnReceipt(code); break;
            case ReturnStage.NoReceipt: ScanIntoCart(code); break;
        }
    }

    /// <summary>Opens a receipt by its number: a bill of this till, else a bill of another till downloaded from ERPNext (by its
    /// number or its ERPNext name).</summary>
    public void Find(string id)
    {
        if (busy || completed) return;
        id = id.Trim().ToUpperInvariant();
        if (id.Length == 0) { Error("Type or scan the receipt number."); return; }
        Receipt? receipt;
        RemoteReceipt? remote = null;
        IReadOnlyList<Receipt> returns;
        try
        {
            receipt = ctx.Receipts.Get(id);
            if (receipt is null && ctx.RemoteReceipts is { } remotes)
            {
                remote = remotes.FindByClientId(id) ?? remotes.FindByErpName(id);
                receipt = remote?.ToReceipt();
            }
            returns = receipt is null ? [] : builder.ReturnsOf(receipt);
        }
        catch (Exception ex)
        {
            Error($"Could not look up the receipt: {ex.Message}");
            return;
        }
        if (receipt is null) { Error($"Receipt {id} not found on this till. Ask a supervisor for a return without receipt."); return; }
        if (receipt.Kind != ReceiptKind.Sale) { Error($"{receipt.ClientId} is a credit note, not a sale — open the original sale."); return; }
        if (remote is not null && !CanReprice(remote, receipt)) { Error(CannotRepriceMessage); return; }

        var lines = receipt.Lines.Select(l => new ReturnLine(l, Returned(returns, l.LineNo))).ToList();
        if (lines.All(l => l.Returnable <= 0m)) { Error($"Everything on {receipt.ClientId} has already been returned."); return; }

        original = receipt;
        originalNumber = remote?.ClientRequestId;
        returnCart = null;
        CartLines.Clear();
        Lines.Clear();
        foreach (var line in lines)
        {
            line.ReturnQty.Changed += Recompute;
            Lines.Add(line);
        }
        FindText = "";
        Reason = null;
        Stage = ReturnStage.Choose;
        OnPropertyChanged(nameof(Original));
        OnPropertyChanged(nameof(OriginalText));
        Recompute();
        Info((remote is null ? "" : $"{FromOtherTillMessage} ") + (returns.Count > 0
            ? $"Part of {receipt.ClientId} was already returned — only what is left is shown."
            : $"Choose the items to return from {receipt.ClientId}."));
    }

    /// <summary>A bill of another till can be returned here only when the till prices it as ERPNext did: no whole-bill discount,
    /// and the till's own pricing of its lines within <see cref="RepriceTolerance"/> of its grand total.</summary>
    private bool CanReprice(RemoteReceipt remote, Receipt receipt)
    {
        if (remote.DiscountAmount != 0m || remote.AdditionalDiscountPercentage != 0m) return false;
        try
        {
            return Math.Abs(builder.RepricedGrandTotal(receipt) - remote.GrandTotal) <= RepriceTolerance;
        }
        catch (Exception)
        {
            return false;                                           // e.g. a tax template the till does not have
        }
    }

    /// <summary>Every line to its whole returnable quantity.</summary>
    public void ReturnAll()
    {
        if (stage != ReturnStage.Choose || busy || completed) return;
        foreach (var line in Lines) line.All();
    }

    /// <summary>Confirms the refund: checks, one supervisor PIN for every reason it needs, save, print with the drawer, popup,
    /// back to the sale. A second press (or Enter while the PIN prompt is open) does nothing.</summary>
    public async Task ConfirmAsync()
    {
        if (completed || busy) return;
        if (session.Cashier is not { } cashier || session.Shift is not { } shift) { Error("No open shift — log in again."); return; }
        if (stage == ReturnStage.Find) { Error("Find the receipt first, or choose a return without receipt."); return; }

        var against = original;
        var cart = returnCart;
        var requests = Lines.Where(l => l.Chosen > 0m).Select(l => new ReturnLineRequest(l.LineNo, l.Chosen)).ToList();
        if (stage == ReturnStage.Choose ? requests.Count == 0 : cart is null || cart.Lines.Count == 0)
        {
            Error(stage == ReturnStage.Choose ? "Choose what is being returned first." : "Scan the items being returned first.");
            return;
        }
        if (Reason is not { } why) { Error("Choose a reason for the return."); return; }

        ReturnPreview check;
        try
        {
            check = stage == ReturnStage.Choose ? builder.Preview(against!, requests) : builder.PreviewWithoutReceipt(cart!);
        }
        catch (Exception ex)
        {
            Error(Clean(ex));
            return;
        }

        busy = true;
        CommandsChanged();
        try
        {
            string? approvedBy = null;
            if (check.Needs.Count > 0)
            {
                var approvalReason = $"Return: {NeedsList(check.Needs)}";
                approvedBy = await gate.ApproveAsync(check.Needs[0], approvalReason, against?.ClientId, null, -check.RefundDue);
                if (approvedBy is null)
                {
                    Error("Not refunded: a supervisor must approve this return.");
                    return;
                }
                try
                {
                    foreach (var need in check.Needs.Skip(1))
                        ctx.Approvals.Add(new ApprovalRecord(Guid.NewGuid().ToString("N"), need, cashier.Id, approvedBy, shift.ClientId,
                            against?.ClientId, null, -check.RefundDue, approvalReason, ctx.Clock.Now));
                }
                catch (Exception ex)
                {
                    Error($"Not refunded: could not record the approval ({ex.Message}).");
                    return;
                }
            }

            Receipt receipt;
            try
            {
                // The supervisor approved exactly what the preview needed; if the return now needs more (say the receipt
                // just turned too old), nothing is saved and the cashier confirms again.
                receipt = against is not null
                    ? builder.Build(against, requests, TenderKind.Cash, cashier.Id, shift.ClientId, approvedBy, why, cashier.User, cashier.Name,
                        check.Needs)
                    : builder.BuildWithoutReceipt(cart!, TenderKind.Cash, cashier.Id, shift.ClientId, approvedBy, why, cashier.User,
                        cashier.Name, check.Needs);
            }
            catch (ApprovalRequiredException)
            {
                Recompute();
                Error(SomethingChangedMessage);
                return;
            }
            catch (Exception ex)
            {
                Error($"Could not save the return: {Clean(ex)}");
                return;
            }
            completed = true;
            Refunded(receipt);
        }
        finally
        {
            busy = false;
            CommandsChanged();
        }
    }

    /// <summary>After the credit note is saved: open the drawer and print, remember it for Ctrl+P, show the popup, back to the sale.</summary>
    private void Refunded(Receipt receipt)
    {
        string? printError = null;
        try
        {
            ctx.Output.Print(receipt, openDrawer: true);
        }
        catch (Exception ex)
        {
            printError = ex.Message;
        }
        SaleViewModel.WriteLastReceipt(ctx.Kv, receipt.ClientId, printed: printError is null);

        var refund = Format.Money(-receipt.Payments.Sum(p => p.Amount));
        // The drawer opens through the printer, so when the printer failed the drawer stayed shut.
        if (printError is null) Info($"Refund {refund} — credit note {receipt.ClientId}");
        else Error($"Refund {refund} saved — the printer failed ({printError}). Open the drawer with the key to pay the refund, " +
            "then reprint the credit note with Ctrl+P.");

        string? code = null;
        if (ctx.ShowReceiptPreview)
        {
            var printed = printError is null;
            code = ctx.Dialogs.ShowReceipt(receipt, printError, () =>
            {
                try
                {
                    ctx.Output.Print(receipt, openDrawer: false, copy: printed);
                    printed = true;
                    SaleViewModel.WriteLastReceipt(ctx.Kv, receipt.ClientId, printed: true);
                    return null;
                }
                catch (Exception ex)
                {
                    return ex.Message;
                }
            });
        }

        if (done is not null) done(new ReturnDone(receipt, Message, MessageIsError, string.IsNullOrWhiteSpace(code) ? null : code.Trim()));
        else back();
    }

    private void ScanOnReceipt(string code)
    {
        var itemCode = ResolveItemCode(code);
        var matches = Lines.Where(l => l.Sold.Barcode == code || l.Sold.ItemCode == code || (itemCode is not null && l.Sold.ItemCode == itemCode))
            .ToList();
        if (matches.Count == 0) { Error("That item is not on this receipt"); return; }
        // The very label scanned first, then any line of that item with something left to add.
        var line = matches.FirstOrDefault(l => l.Sold.Barcode == code && l.Chosen < l.Returnable)
            ?? matches.FirstOrDefault(l => l.Chosen < l.Returnable);
        if (line is null) { Error($"No more {matches[0].ItemName} left to return on this receipt"); return; }
        line.Increment();
        if (!previewFailed) Info($"Returning {line.ReturnQty.Text} × {line.ItemName}");
    }

    /// <summary>The item a scanned code stands for in the catalog (a barcode, or a scale label's item), or null.</summary>
    private string? ResolveItemCode(string code)
    {
        try
        {
            if (saleContext.Catalog.FindBarcode(code) is { } exact) return exact.ItemCode;
            if (ScaleLabel.IsScaleLabelShape(code) && ScaleLabel.TryParse(code) is { } label)
                foreach (var key in label.LookupKeys)
                    if (saleContext.Catalog.FindBarcode(key) is { } match) return match.ItemCode;
        }
        catch (Exception)
        {
            // Only the receipt's own barcodes and item codes match then.
        }
        return null;
    }

    private void StartWithoutReceipt()
    {
        if (busy || completed) return;
        original = null;
        originalNumber = null;
        Lines.Clear();
        returnCart = new Cart(saleContext);
        CartLines.Clear();
        Reason = null;
        FindText = "";
        SearchText = "";
        Stage = ReturnStage.NoReceipt;
        OnPropertyChanged(nameof(Original));
        OnPropertyChanged(nameof(OriginalText));
        Recompute();
        Info("Scan the items being returned. A return without a receipt needs a supervisor.");
    }

    /// <summary>Adds a searched item to the return list (without receipt only) and clears the search.</summary>
    public void AddFromSearch(string itemCode)
    {
        if (stage != ReturnStage.NoReceipt || busy || completed || returnCart is not { } cart) return;
        SearchText = "";
        AddToCart(itemCode, () => cart.AddItem(itemCode));
    }

    private void ScanIntoCart(string code)
    {
        if (returnCart is not { } cart) return;
        AddToCart(code, () => cart.AddBarcode(code));
    }

    private void AddToCart(string code, Func<AddResult> add)
    {
        AddResult result;
        try
        {
            result = add();
        }
        catch (Exception ex)
        {
            Error($"Cannot add {code}: {ex.Message}");
            return;
        }
        CartChanged();
        if (previewFailed) return;
        switch (result.Outcome)
        {
            case AddOutcome.Added: Info($"Returning {result.Line!.Item.ItemName}"); break;
            case AddOutcome.UnknownBarcode: Error($"Unknown barcode {code}"); break;
            case AddOutcome.UnknownItem: Error($"Unknown item {code}"); break;
            case AddOutcome.InvalidScaleLabel: Error("Scale label could not be read — scan it again."); break;
            case AddOutcome.ItemNotSellable: Error("This item cannot be sold, so it cannot be returned here."); break;
            case AddOutcome.NoPrice: Error("No price for this item — call a supervisor."); break;
            case AddOutcome.UnsupportedTax: Error("Tax setup missing for this item — call a supervisor."); break;
            default: Error($"Cannot add {code} ({result.Outcome})."); break;
        }
    }

    /// <summary>One piece less on the return list; a last piece or a scale-label line is removed.</summary>
    private void DecrementCartLine(Guid id)
    {
        if (busy || completed || returnCart is not { } cart || cart.Lines.FirstOrDefault(l => l.Id == id) is not { } line) return;
        if (line.FromScaleLabel || line.Qty <= 1m) cart.Remove(id);
        else cart.Decrement(id);
        CartChanged();
    }

    private void RemoveCartLine(Guid id)
    {
        if (busy || completed || returnCart is not { } cart || cart.Lines.All(l => l.Id != id)) return;
        cart.Remove(id);
        CartChanged();
    }

    private void CartChanged()
    {
        CartLines.Clear();
        if (returnCart is { } cart)
        {
            var totals = cart.Totals();
            for (var i = 0; i < cart.Lines.Count; i++)
            {
                var l = cart.Lines[i];
                var qty = l.FromScaleLabel || l.Qty != decimal.Truncate(l.Qty)
                    ? $"{l.Qty.ToString("0.000", CultureInfo.InvariantCulture)} {l.Uom}"
                    : l.Qty.ToString("0", CultureInfo.InvariantCulture);
                CartLines.Add(new SaleLine(l.Id, i + 1, l.Item.ItemName, l.Barcode, qty, Format.Money(l.Rate), l.Rule?.Label ?? "",
                    Format.Money(totals.Lines[i].Amount), l.FromScaleLabel));
            }
        }
        Recompute();
    }

    /// <summary>Works out the refund for what is chosen now. A choice that is not valid (more than is left, a part piece) shows
    /// its reason as the message.</summary>
    private void Recompute()
    {
        ReturnPreview? next = null;
        string? error = null;
        try
        {
            if (stage == ReturnStage.Choose && original is { } receipt)
            {
                var requests = Lines.Where(l => l.Chosen > 0m).Select(l => new ReturnLineRequest(l.LineNo, l.Chosen)).ToList();
                if (requests.Count > 0) next = builder.Preview(receipt, requests);
            }
            else if (stage == ReturnStage.NoReceipt && returnCart is { Lines.Count: > 0 } cart)
            {
                next = builder.PreviewWithoutReceipt(cart);
            }
        }
        catch (Exception ex)
        {
            error = Clean(ex);
        }

        Preview = next;
        OnPropertyChanged(nameof(RefundTotalText));
        OnPropertyChanged(nameof(VatText));
        OnPropertyChanged(nameof(RefundDueText));
        OnPropertyChanged(nameof(NeedsText));
        if (error is not null)
        {
            previewFailed = true;
            Error(error);
        }
        else if (previewFailed)
        {
            previewFailed = false;
            Info("");
        }
    }

    private void LoadRecentBills()
    {
        RecentBills.Clear();
        try
        {
            foreach (var sale in ctx.Receipts.RecentSales(RecentCount))
            {
                var returns = builder.ReturnsOf(sale);
                var tag = returns.Count == 0 ? ""
                    : sale.Lines.All(l => l.Qty - Returned(returns, l.LineNo) <= 0m) ? "all returned" : "partly returned";
                RecentBills.Add(new RecentBill(sale.ClientId, sale.CreatedAt.ToString("dd/MM HH:mm", CultureInfo.InvariantCulture),
                    Format.Money(sale.GrandTotal), tag));
            }
        }
        catch (Exception ex)
        {
            Error($"Could not load the recent bills: {ex.Message}");
        }
    }

    /// <summary>Esc: from choosing (or the return list) back to finding the receipt; from finding, back to the sale.
    /// Not once the refund is confirmed.</summary>
    private void Back()
    {
        if (busy || completed) return;
        if (stage == ReturnStage.Find)
        {
            back();
            return;
        }
        original = null;
        originalNumber = null;
        returnCart = null;
        Lines.Clear();
        CartLines.Clear();
        SearchText = "";
        Reason = null;
        Stage = ReturnStage.Find;
        OnPropertyChanged(nameof(Original));
        OnPropertyChanged(nameof(OriginalText));
        Recompute();
        LoadRecentBills();
        Info("");
    }

    private static decimal Returned(IReadOnlyList<Receipt> returns, int lineNo) =>
        returns.SelectMany(r => r.Lines).Where(l => l.LineNo == lineNo).Sum(l => -l.Qty);

    private static string NeedsList(IReadOnlyList<ApprovalAction> needs) => string.Join("; ", needs.Select(need => need switch
    {
        ApprovalAction.ReturnOverLimit => $"over AED {ApprovalLimit.ToString("0", CultureInfo.InvariantCulture)} on this receipt",
        ApprovalAction.ReturnOldReceipt => $"receipt older than {MaxAgeDays.ToString(CultureInfo.InvariantCulture)} days",
        ApprovalAction.ReturnWithoutReceipt => "return without receipt",
        _ => need.ToString(),
    }));

    /// <summary>An exception's message without ArgumentException's " (Parameter '…')" tail.</summary>
    private static string Clean(Exception ex) => ex.Message.Split(" (Parameter", StringSplitOptions.None)[0];

    private void CommandsChanged()
    {
        ConfirmCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
    }

    private void Info(string text) { Message = text; MessageIsError = false; }

    private void Error(string text) { Message = text; MessageIsError = true; }
}
