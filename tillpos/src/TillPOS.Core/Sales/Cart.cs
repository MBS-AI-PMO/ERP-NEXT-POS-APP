using TillPOS.Core.Catalog;
using TillPOS.Core.Money;
using TillPOS.Core.Pricing;
using TillPOS.Core.Tax;

namespace TillPOS.Core.Sales;

public sealed record SaleContext(
    ICatalog Catalog,
    MoneySettings Money,
    string PriceList,
    string Warehouse,
    string? TaxCategory,
    SalesTaxTemplate? TaxTemplate,
    Func<DateOnly> Today)
{
    public static SaleContext Create(ICatalog catalog, PosSettings settings, Func<string, SalesTaxTemplate?> findSalesTaxTemplate,
        int precision, RoundingMethod rounding, Func<DateOnly> today)
    {
        // The shop's POS Profiles all tick "Disable Rounded Total", yet POS Awesome rounds every bill to the currency's smallest
        // fraction (AED 0.25) by sending disable_rounded_total = 0 on the invoice. The till does the same, so cash is rounded
        // whatever the profile says (card stays exact: PaymentCalculator).
        var money = new MoneySettings(precision, rounding, settings.SmallestCurrencyFraction, DisableRoundedTotal: false);
        SalesTaxTemplate? template = null;
        if (settings.TaxesAndCharges is { } name)
            template = findSalesTaxTemplate(name)
                ?? throw new UnsupportedTaxSetupException($"sales tax template '{name}' is not in the local catalog");
        return new SaleContext(catalog, money, settings.PriceList, settings.Warehouse, settings.TaxCategory, template, today)
        {
            PosProfile = settings.PosProfile,
            Company = settings.Company,
        };
    }

    /// <summary>Units whose scale-label value is grams (quantity = value ÷ 1000); any other unit reads the value as a piece count.</summary>
    public IReadOnlySet<string> WeightUoms { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Kg" };

    /// <summary>The POS Profile these settings came from (saved on each bill for the upload); null when not built from one.</summary>
    public string? PosProfile { get; init; }

    /// <summary>The POS Profile's company; null when not built from one.</summary>
    public string? Company { get; init; }
}

public sealed class CartLine
{
    internal CartLine(Item item, string uom, decimal conversionFactor, decimal priceListRate, AppliedRule? rule, decimal rate,
        string? itemTaxTemplate, string? barcode, decimal? labelQty, string? uomFallbackFrom)
    {
        Item = item;
        Uom = uom;
        ConversionFactor = conversionFactor;
        PriceListRate = priceListRate;
        Rule = rule;
        Rate = rate;
        ItemTaxTemplate = itemTaxTemplate;
        Barcode = barcode;
        FromScaleLabel = labelQty is not null;
        Qty = labelQty ?? 1m;
        UomFallbackFrom = uomFallbackFrom;
    }

    public Guid Id { get; } = Guid.NewGuid();
    public Item Item { get; }
    public string Uom { get; }
    public decimal ConversionFactor { get; }
    public decimal Qty { get; internal set; }
    public decimal PriceListRate { get; }
    public AppliedRule? Rule { get; }
    /// <summary>Unit rate after the offer; fixed when the line is added (cashiers cannot change it).</summary>
    public decimal Rate { get; }
    public string? ItemTaxTemplate { get; }
    /// <summary>The code that was scanned (a scale label keeps the full 13 digits).</summary>
    public string? Barcode { get; }
    /// <summary>Quantity came from a scale label; it is never merged and + / − do not apply.</summary>
    public bool FromScaleLabel { get; }
    /// <summary>Set when the scanned barcode's unit is not set up on the item, so the line was sold in the stock unit.</summary>
    public string? UomFallbackFrom { get; }
}

public enum AddOutcome { Added, UnknownBarcode, UnknownItem, ItemNotSellable, UnknownUom, NoPrice, UnsupportedTax, InvalidScaleLabel }

public sealed record AddResult(AddOutcome Outcome, CartLine? Line);

public sealed class Cart(SaleContext ctx)
{
    private readonly List<CartLine> lines = [];
    private readonly PriceResolver prices = new(ctx.Catalog);
    private readonly PricingRuleSelector rules = new(ctx.Catalog, ctx.Money, ctx.PriceList, ctx.Warehouse);
    private readonly ItemTaxResolver itemTaxes = new(ctx.Catalog, ctx.TaxCategory);
    private readonly TaxCalculator taxes = new(ctx.Money, ctx.Catalog.FindItemTaxTemplate);

    /// <summary>The counter settings the cart prices with.</summary>
    public SaleContext Context => ctx;

    public IReadOnlyList<CartLine> Lines => lines;

    public AddResult AddBarcode(string barcode)
    {
        var code = barcode.Trim();
        if (ctx.Catalog.FindBarcode(code) is { } exact) return AddScanned(exact, code, null);
        if (!ScaleLabel.IsScaleLabelShape(code)) return new AddResult(AddOutcome.UnknownBarcode, null);

        var label = ScaleLabel.TryParse(code);
        if (label is null) return new AddResult(AddOutcome.InvalidScaleLabel, null);
        foreach (var key in label.LookupKeys)
            if (ctx.Catalog.FindBarcode(key) is { } match) return AddScanned(match, code, label);
        return new AddResult(AddOutcome.UnknownBarcode, null);
    }

    public AddResult AddItem(string itemCode, string? uom = null) => Add(itemCode, uom, null, null, null);

    private AddResult AddScanned(ItemBarcode found, string scanned, ScaleLabel? label)
    {
        var uom = found.Uom;
        string? fallbackFrom = null;
        var item = ctx.Catalog.FindItem(found.ItemCode);
        if (item is not null && !string.IsNullOrEmpty(uom) && ctx.Catalog.ConversionFactor(item.ItemCode, uom) is null)
        {
            fallbackFrom = uom; // follow the data: the barcode's unit isn't set up on the item, so sell in the stock unit
            uom = item.StockUom;
        }

        decimal? labelQty = null;
        if (label is not null)
        {
            var lineUom = string.IsNullOrEmpty(uom) ? item?.StockUom : uom;
            var weighed = (lineUom is not null && ctx.WeightUoms.Contains(lineUom))
                || (fallbackFrom is not null && ctx.WeightUoms.Contains(fallbackFrom));
            labelQty = weighed ? label.Value / 1000m : label.Value;
        }
        return Add(found.ItemCode, uom, scanned, labelQty, fallbackFrom);
    }

    private AddResult Add(string itemCode, string? uom, string? barcode, decimal? labelQty, string? uomFallbackFrom)
    {
        var item = ctx.Catalog.FindItem(itemCode);
        if (item is null) return new AddResult(AddOutcome.UnknownItem, null);
        if (item.Disabled || !item.IsSalesItem) return new AddResult(AddOutcome.ItemNotSellable, null);

        var lineUom = string.IsNullOrEmpty(uom) ? item.StockUom : uom;
        if (labelQty is null)
        {
            var existing = lines.FirstOrDefault(l => !l.FromScaleLabel && l.Item.ItemCode == item.ItemCode && l.Uom == lineUom);
            if (existing is not null)
            {
                existing.Qty += 1m;
                return new AddResult(AddOutcome.Added, existing);
            }
        }

        var cf = ctx.Catalog.ConversionFactor(item.ItemCode, lineUom);
        if (cf is null) return new AddResult(AddOutcome.UnknownUom, null);

        var date = ctx.Today();
        var priceListRate = prices.PriceListRate(item, lineUom, cf.Value, date);
        if (priceListRate is null) return new AddResult(AddOutcome.NoPrice, null);

        var plr = Rounder.Round(priceListRate.Value, ctx.Money);
        var rule = rules.Select(item, plr, cf.Value, date);
        var rate = LineMath.RateAfterRule(plr, cf.Value, rule, ctx.Money);
        var itemTaxTemplate = itemTaxes.TemplateFor(item, date);
        if (itemTaxTemplate is not null && ctx.Catalog.FindItemTaxTemplate(itemTaxTemplate) is null)
            return new AddResult(AddOutcome.UnsupportedTax, null);
        var line = new CartLine(item, lineUom, cf.Value, plr, rule, rate, itemTaxTemplate, barcode, labelQty, uomFallbackFrom);
        lines.Add(line);
        return new AddResult(AddOutcome.Added, line);
    }

    public void SetQty(Guid lineId, decimal qty)
    {
        if (qty <= 0) throw new ArgumentOutOfRangeException(nameof(qty), "Quantity must be greater than zero.");
        Countable(lineId).Qty = qty;
    }

    public void Increment(Guid lineId) => Countable(lineId).Qty += 1m;

    public void Decrement(Guid lineId)
    {
        var line = Countable(lineId);
        if (line.Qty > 1m) line.Qty -= 1m;
    }

    private CartLine Countable(Guid lineId)
    {
        var line = Find(lineId);
        if (line.FromScaleLabel) throw new InvalidOperationException("Lines from a scale label take their quantity from the label.");
        return line;
    }

    public void Remove(Guid lineId) => lines.Remove(Find(lineId));

    public void Clear() => lines.Clear();

    public IReadOnlyList<HeldLine> Snapshot() =>
        lines.Select(l => new HeldLine(l.Item.ItemCode, l.Uom, l.Qty, l.Barcode, l.FromScaleLabel)).ToList();

    /// <summary>Re-adds held lines at today's prices into an empty cart; returns the lines that can no longer be sold.</summary>
    public IReadOnlyList<(HeldLine Line, AddOutcome Outcome)> Restore(IEnumerable<HeldLine> held)
    {
        if (lines.Count > 0) throw new InvalidOperationException("Finish or hold the current bill before recalling another.");
        var failed = new List<(HeldLine, AddOutcome)>();
        foreach (var h in held)
        {
            var result = Add(h.ItemCode, h.Uom, h.Barcode, h.FromScaleLabel ? h.Qty : null, null);
            if (result.Outcome != AddOutcome.Added) failed.Add((h, result.Outcome));
            else if (!h.FromScaleLabel) result.Line!.Qty = h.Qty;
        }
        return failed;
    }

    /// <summary>Re-adds a stored bill's lines at their stored prices (a delivery: never re-priced), into an empty cart. An item no
    /// longer in the catalog keeps its stored name and unit.</summary>
    public void RestoreFixed(IEnumerable<ReceiptLine> stored)
    {
        if (lines.Count > 0) throw new InvalidOperationException("Finish or hold the current bill first.");
        foreach (var s in stored)
        {
            var item = ctx.Catalog.FindItem(s.ItemCode) ?? new Item(s.ItemCode, s.ItemName, "", null, s.Uom, false, true);
            var line = new CartLine(item, s.Uom, s.ConversionFactor, s.PriceListRate, null, s.Rate, s.ItemTaxTemplate, s.Barcode,
                s.FromScaleLabel ? s.Qty : null, s.UomFallbackFrom);
            if (!s.FromScaleLabel) line.Qty = s.Qty;
            lines.Add(line);
        }
    }

    public BillTotals Totals() =>
        taxes.Calculate(lines.Select(l => new TaxLineInput(l.Qty, l.Rate, l.ItemTaxTemplate)).ToList(), ctx.TaxTemplate);

    public decimal DiscountSaved() =>
        Rounder.Round(lines.Sum(l => (l.PriceListRate - l.Rate) * l.Qty), ctx.Money);

    private CartLine Find(Guid lineId) =>
        lines.FirstOrDefault(l => l.Id == lineId) ?? throw new KeyNotFoundException($"Line {lineId} is not in the cart.");
}
