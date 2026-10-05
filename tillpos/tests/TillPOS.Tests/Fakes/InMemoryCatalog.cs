using TillPOS.Core.Catalog;
using TillPOS.Core.Pricing;

namespace TillPOS.Tests.Fakes;

public sealed class InMemoryCatalog : ICatalog
{
    public List<Item> Items { get; } = [];
    public List<ItemBarcode> Barcodes { get; } = [];
    public List<ItemUom> Uoms { get; } = [];
    public List<ItemPrice> Prices { get; } = [];
    public List<ItemGroupNode> Groups { get; } = [];
    public List<PricingRule> Rules { get; } = [];
    public Dictionary<string, List<ItemTaxAssignment>> ItemTaxRows { get; } = [];
    public Dictionary<string, List<ItemTaxAssignment>> GroupTaxRows { get; } = [];
    public List<ItemTaxTemplate> ItemTaxTemplates { get; } = [];

    /// <summary>All Item Groups(1,10) > Food(2,7) > {Rice(3,4), Dairy(5,6)}; All Item Groups > Household(8,9).</summary>
    public static InMemoryCatalog WithStandardGroups()
    {
        var c = new InMemoryCatalog();
        c.Groups.AddRange([
            new ItemGroupNode("All Item Groups", null, 1, 10),
            new ItemGroupNode("Food", "All Item Groups", 2, 7),
            new ItemGroupNode("Rice", "Food", 3, 4),
            new ItemGroupNode("Dairy", "Food", 5, 6),
            new ItemGroupNode("Household", "All Item Groups", 8, 9),
        ]);
        return c;
    }

    public Item? FindItem(string itemCode) => Items.FirstOrDefault(i => i.ItemCode == itemCode);
    public ItemBarcode? FindBarcode(string barcode) => Barcodes.FirstOrDefault(b => b.Barcode == barcode);

    public decimal? ConversionFactor(string itemCode, string uom)
    {
        var item = FindItem(itemCode);
        if (item is null) return null;
        if (item.StockUom == uom) return 1m;
        return Uoms.FirstOrDefault(u => u.ItemCode == itemCode && u.Uom == uom)?.ConversionFactor;
    }

    public IReadOnlyList<ItemPrice> PricesFor(string itemCode) => Prices.Where(p => p.ItemCode == itemCode).ToList();
    public ItemGroupNode? FindGroup(string name) => Groups.FirstOrDefault(g => g.Name == name);
    public IReadOnlyList<PricingRule> PricingRules() => Rules;
    public IReadOnlyList<ItemTaxAssignment> ItemTaxes(string itemCode) => ItemTaxRows.TryGetValue(itemCode, out var r) ? r : [];
    public IReadOnlyList<ItemTaxAssignment> ItemGroupTaxes(string itemGroup) => GroupTaxRows.TryGetValue(itemGroup, out var r) ? r : [];
    public ItemTaxTemplate? FindItemTaxTemplate(string name) => ItemTaxTemplates.FirstOrDefault(t => t.Name == name);
}
