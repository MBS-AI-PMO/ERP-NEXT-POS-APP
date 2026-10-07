using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TillPOS.Core.Catalog;
using TillPOS.Core.Sales;
using TillPOS.Core.Tax;

namespace TillPOS.Presentation;

/// <summary>What "Add to bill" adds: a scanned code (goes through <see cref="SaleViewModel.Scan"/>) or an item code picked from
/// the search (goes through <see cref="SaleViewModel.AddFromSearch"/>).</summary>
public sealed record PriceCheckPick(string Code, bool IsItemCode);

/// <summary>Price check (F4): shows an item's price without adding it to the bill. Every lookup prices the item in its own
/// throw-away cart, so pricing, offers, VAT, the unit fallback and scale labels work exactly as on the bill, and the sale's
/// cart is never touched.</summary>
public sealed class PriceCheckViewModel : ObservableObject
{
    private readonly TillContext ctx;
    private readonly SaleContext saleContext;
    private string scanText = "";
    private string searchText = "";
    private bool hasResult;
    private string name = "";
    private string itemCode = "";
    private string barcode = "";
    private string unitText = "";
    private string priceText = "";
    private string offerText = "";
    private string scaleText = "";
    private string message = "";
    private PriceCheckPick? addToBill;

    public PriceCheckViewModel(TillContext ctx)
    {
        this.ctx = ctx;
        saleContext = ctx.NewSaleContext();
        ScanEnteredCommand = new RelayCommand(() => { var code = ScanText.Trim(); ScanText = ""; if (code.Length > 0) Lookup(code); });
        SelectCommand = new RelayCommand<string>(code => { if (code is not null) Select(code); });
    }

    public ObservableCollection<Item> SearchResults { get; } = [];
    public string ScanText { get => scanText; set => SetProperty(ref scanText, value); }
    public bool HasResult { get => hasResult; private set => SetProperty(ref hasResult, value); }
    public string Name { get => name; private set => SetProperty(ref name, value); }
    public string ItemCode { get => itemCode; private set => SetProperty(ref itemCode, value); }
    public string Barcode { get => barcode; private set => SetProperty(ref barcode, value); }
    /// <summary>"per Kg", "per PCS".</summary>
    public string UnitText { get => unitText; private set => SetProperty(ref unitText, value); }
    /// <summary>The list price including VAT.</summary>
    public string PriceText { get => priceText; private set => SetProperty(ref priceText, value); }
    /// <summary>"Offer: 2.50 (10% OFF)" when an offer lowers the price, otherwise empty.</summary>
    public string OfferText { get => offerText; private set => SetProperty(ref offerText, value); }
    /// <summary>"0.740 Kg × 3.50 = 2.59" for a scale label, otherwise empty.</summary>
    public string ScaleText { get => scaleText; private set => SetProperty(ref scaleText, value); }
    public string Message { get => message; private set => SetProperty(ref message, value); }

    /// <summary>What "Add to bill" would add; null when there is no result.</summary>
    public PriceCheckPick? AddToBill
    {
        get => addToBill;
        private set
        {
            if (SetProperty(ref addToBill, value)) OnPropertyChanged(nameof(AddToBillCode));
        }
    }

    public string? AddToBillCode => AddToBill?.Code;

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
    public RelayCommand<string> SelectCommand { get; }

    /// <summary>A scanned barcode or scale label.</summary>
    public void Lookup(string code)
    {
        code = code.Trim();
        if (code.Length == 0) return;
        var cart = new Cart(saleContext);
        Show(cart, cart.AddBarcode(code), code, new PriceCheckPick(code, false));
    }

    /// <summary>An item picked from the search results.</summary>
    public void Select(string itemCode)
    {
        var cart = new Cart(saleContext);
        Show(cart, cart.AddItem(itemCode), itemCode, new PriceCheckPick(itemCode, true));
        SearchText = "";
    }

    private void Show(Cart cart, AddResult result, string code, PriceCheckPick pick)
    {
        Clear();
        if (result.Outcome != AddOutcome.Added)
        {
            Message = Problem(result.Outcome, code);
            return;
        }

        var line = result.Line!;
        try
        {
            var listPrice = Gross(line.PriceListRate, 1m, line.ItemTaxTemplate);
            var offerPrice = Gross(line.Rate, 1m, line.ItemTaxTemplate);
            Name = line.Item.ItemName;
            ItemCode = line.Item.ItemCode;
            Barcode = line.Barcode ?? "";
            UnitText = $"per {line.Uom}";
            PriceText = Format.Money(listPrice);
            if (line.Rate < line.PriceListRate)
                OfferText = line.Rule is { } rule ? $"Offer: {Format.Money(offerPrice)} ({rule.Label})" : $"Offer: {Format.Money(offerPrice)}";
            if (line.FromScaleLabel)
                ScaleText = $"{line.Qty.ToString("0.000", CultureInfo.InvariantCulture)} {line.Uom} × {Format.Money(offerPrice)} = {Format.Money(cart.Totals().GrandTotal)}";
        }
        catch (UnsupportedTaxSetupException)
        {
            Clear();
            Message = Problem(AddOutcome.UnsupportedTax, code);
            return;
        }
        HasResult = true;
        AddToBill = pick;
    }

    /// <summary>The amount including VAT (inclusive templates leave it unchanged; exclusive ones add the tax).</summary>
    private decimal Gross(decimal rate, decimal qty, string? itemTaxTemplate) =>
        new TaxCalculator(saleContext.Money, saleContext.Catalog.FindItemTaxTemplate)
            .Calculate([new TaxLineInput(qty, rate, itemTaxTemplate)], saleContext.TaxTemplate).GrandTotal;

    private void Clear()
    {
        HasResult = false;
        Name = ItemCode = Barcode = UnitText = PriceText = OfferText = ScaleText = Message = "";
        AddToBill = null;
    }

    private static string Problem(AddOutcome outcome, string code) => outcome switch
    {
        AddOutcome.UnknownBarcode => $"Unknown barcode {code}",
        AddOutcome.UnknownItem => $"Unknown item {code}",
        AddOutcome.InvalidScaleLabel => "Scale label could not be read — scan it again.",
        AddOutcome.ItemNotSellable => "This item cannot be sold.",
        AddOutcome.NoPrice => "No price for this item",
        AddOutcome.UnsupportedTax => "Tax setup missing for this item — call a supervisor.",
        _ => $"Cannot price {code} ({outcome}).",
    };
}
