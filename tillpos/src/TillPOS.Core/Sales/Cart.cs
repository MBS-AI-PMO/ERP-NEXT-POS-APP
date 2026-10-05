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
    Func<DateOnly> Today);

public sealed class CartLine
{
    internal CartLine(Item item, string uom, decimal conversionFactor, decimal priceListRate, AppliedRule? rule, decimal rate, string? itemTaxTemplate)
    {
        Item = item;
        Uom = uom;
        ConversionFactor = conversionFactor;
        PriceListRate = priceListRate;
        Rule = rule;
        Rate = rate;
        ItemTaxTemplate = itemTaxTemplate;
    }

    public Guid Id { get; } = Guid.NewGuid();
    public Item Item { get; }
    public string Uom { get; }
    public decimal ConversionFactor { get; }
    public decimal Qty { get; internal set; } = 1m;
    public decimal PriceListRate { get; }
    public AppliedRule? Rule { get; }
    /// <summary>Unit rate after the offer; fixed when the line is added (cashiers cannot change it).</summary>
    public decimal Rate { get; }
    public string? ItemTaxTemplate { get; }
}

public enum AddOutcome { Added, UnknownBarcode, UnknownItem, ItemNotSellable, UnknownUom, NoPrice }

public sealed record AddResult(AddOutcome Outcome, CartLine? Line);

public sealed class Cart(SaleContext ctx)
{
    private readonly List<CartLine> lines = [];
    private readonly PriceResolver prices = new(ctx.Catalog);
    private readonly PricingRuleSelector rules = new(ctx.Catalog, ctx.Money, ctx.PriceList, ctx.Warehouse);
    private readonly ItemTaxResolver itemTaxes = new(ctx.Catalog, ctx.TaxCategory);
    private readonly TaxCalculator taxes = new(ctx.Money, ctx.Catalog.FindItemTaxTemplate);

    public IReadOnlyList<CartLine> Lines => lines;

    public AddResult AddBarcode(string barcode)
    {
        var found = ctx.Catalog.FindBarcode(barcode.Trim());
        return found is null ? new AddResult(AddOutcome.UnknownBarcode, null) : AddItem(found.ItemCode, found.Uom);
    }

    public AddResult AddItem(string itemCode, string? uom = null)
    {
        var item = ctx.Catalog.FindItem(itemCode);
        if (item is null) return new AddResult(AddOutcome.UnknownItem, null);
        if (item.Disabled || !item.IsSalesItem) return new AddResult(AddOutcome.ItemNotSellable, null);

        var lineUom = string.IsNullOrEmpty(uom) ? item.StockUom : uom;
        var existing = lines.FirstOrDefault(l => l.Item.ItemCode == item.ItemCode && l.Uom == lineUom);
        if (existing is not null)
        {
            existing.Qty += 1m;
            return new AddResult(AddOutcome.Added, existing);
        }

        var cf = ctx.Catalog.ConversionFactor(item.ItemCode, lineUom);
        if (cf is null) return new AddResult(AddOutcome.UnknownUom, null);

        var date = ctx.Today();
        var priceListRate = prices.PriceListRate(item, lineUom, cf.Value, date);
        if (priceListRate is null) return new AddResult(AddOutcome.NoPrice, null);

        var rule = rules.Select(item, priceListRate.Value, cf.Value, date);
        var rate = LineMath.RateAfterRule(priceListRate.Value, cf.Value, rule, ctx.Money);
        var line = new CartLine(item, lineUom, cf.Value, priceListRate.Value, rule, rate, itemTaxes.TemplateFor(item, date));
        lines.Add(line);
        return new AddResult(AddOutcome.Added, line);
    }

    public void SetQty(Guid lineId, decimal qty)
    {
        if (qty <= 0) throw new ArgumentOutOfRangeException(nameof(qty), "Quantity must be greater than zero.");
        Find(lineId).Qty = qty;
    }

    public void Increment(Guid lineId) => Find(lineId).Qty += 1m;

    public void Decrement(Guid lineId)
    {
        var line = Find(lineId);
        if (line.Qty > 1m) line.Qty -= 1m;
    }

    public void Remove(Guid lineId) => lines.Remove(Find(lineId));

    public void Clear() => lines.Clear();

    public BillTotals Totals() =>
        taxes.Calculate(lines.Select(l => new TaxLineInput(l.Qty, l.Rate, l.ItemTaxTemplate)).ToList(), ctx.TaxTemplate);

    public decimal DiscountSaved() =>
        Rounder.Round(lines.Sum(l => (l.PriceListRate - l.Rate) * l.Qty), ctx.Money);

    private CartLine Find(Guid lineId) =>
        lines.FirstOrDefault(l => l.Id == lineId) ?? throw new KeyNotFoundException($"Line {lineId} is not in the cart.");
}
