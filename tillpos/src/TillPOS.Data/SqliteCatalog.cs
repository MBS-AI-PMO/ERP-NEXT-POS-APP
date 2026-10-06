using System.Text.Json;
using Microsoft.Data.Sqlite;
using TillPOS.Core.Catalog;
using TillPOS.Core.Pricing;
using static TillPOS.Data.SqlExt;

namespace TillPOS.Data;

/// <summary>ICatalog over SQLite. Items, barcodes and prices are read per call (indexed);
/// small tables (groups, rules, item tax templates) are cached and refreshed by Reload().</summary>
public sealed class SqliteCatalog : ICatalog
{
    private const string ItemColumns = "item_code, item_name, item_group, brand, stock_uom, disabled, is_sales_item";
    private readonly TillDb db;

    private sealed record CachedTables(
        IReadOnlyDictionary<string, ItemGroupNode> Groups,
        IReadOnlyDictionary<string, List<ItemTaxAssignment>> GroupTaxes,
        IReadOnlyList<PricingRule> Rules,
        IReadOnlyDictionary<string, ItemTaxTemplate> ItemTaxTemplates);

    private volatile CachedTables cache = new(
        new Dictionary<string, ItemGroupNode>(), new Dictionary<string, List<ItemTaxAssignment>>(), [], new Dictionary<string, ItemTaxTemplate>());

    public SqliteCatalog(TillDb db)
    {
        this.db = db;
        Reload();
    }

    /// <summary>Reloads the cached tables and swaps them in one assignment, so a background sync never
    /// exposes a half-updated mix to the till's UI thread.</summary>
    public void Reload()
    {
        using var c = db.Open();
        var groups = c.Query("SELECT name, parent, lft, rgt FROM item_group",
            r => new ItemGroupNode(r.GetString(0), Str(r, 1), r.GetInt32(2), r.GetInt32(3))).ToDictionary(g => g.Name);
        var groupTaxes = c.Query("SELECT parent, item_tax_template, tax_category, valid_from, idx FROM item_tax WHERE parent_type = 'Item Group'",
                r => (Parent: r.GetString(0), Row: ReadTax(r, 1)))
            .GroupBy(x => x.Parent).ToDictionary(g => g.Key, g => g.Select(x => x.Row).ToList());
        var rules = c.Query("SELECT json FROM pricing_rule", r => JsonSerializer.Deserialize<PricingRule>(r.GetString(0))!);
        var itemTaxTemplates = c.Query("SELECT json FROM item_tax_template", r => JsonSerializer.Deserialize<ItemTaxTemplate>(r.GetString(0))!)
            .ToDictionary(t => t.Name);
        cache = new CachedTables(groups, groupTaxes, rules, itemTaxTemplates);
    }

    public Item? FindItem(string itemCode)
    {
        using var c = db.Open();
        return c.Query($"SELECT {ItemColumns} FROM item WHERE item_code = @c", ReadItem, ("@c", itemCode)).FirstOrDefault();
    }

    public ItemBarcode? FindBarcode(string barcode)
    {
        using var c = db.Open();
        return c.Query("SELECT barcode, item_code, uom FROM item_barcode WHERE barcode = @b",
            r => new ItemBarcode(r.GetString(0), r.GetString(1), Str(r, 2)), ("@b", barcode)).FirstOrDefault();
    }

    public decimal? ConversionFactor(string itemCode, string uom)
    {
        var item = FindItem(itemCode);
        if (item is null) return null;
        if (item.StockUom == uom) return 1m;
        using var c = db.Open();
        return c.Query("SELECT conversion_factor FROM item_uom WHERE item_code = @c AND uom = @u",
            r => (decimal?)Dec(r, 0), ("@c", itemCode), ("@u", uom)).FirstOrDefault();
    }

    public IReadOnlyList<ItemPrice> PricesFor(string itemCode)
    {
        using var c = db.Open();
        return c.Query("SELECT name, item_code, uom, price_list_rate, valid_from, valid_upto FROM item_price WHERE item_code = @c",
            r => new ItemPrice(r.GetString(0), r.GetString(1), Str(r, 2), Dec(r, 3), Date(r, 4), Date(r, 5)), ("@c", itemCode));
    }

    public ItemGroupNode? FindGroup(string name) => cache.Groups.GetValueOrDefault(name);

    public IReadOnlyList<PricingRule> PricingRules() => cache.Rules;

    public IReadOnlyList<ItemTaxAssignment> ItemTaxes(string itemCode)
    {
        using var c = db.Open();
        return c.Query("SELECT item_tax_template, tax_category, valid_from, idx FROM item_tax WHERE parent_type = 'Item' AND parent = @c",
            r => ReadTax(r, 0), ("@c", itemCode));
    }

    public IReadOnlyList<ItemTaxAssignment> ItemGroupTaxes(string itemGroup) =>
        cache.GroupTaxes.TryGetValue(itemGroup, out var rows) ? rows : [];

    public ItemTaxTemplate? FindItemTaxTemplate(string name) => cache.ItemTaxTemplates.GetValueOrDefault(name);

    public SalesTaxTemplate? FindSalesTaxTemplate(string name)
    {
        using var c = db.Open();
        return c.Query("SELECT json FROM sales_tax_template WHERE name = @n",
            r => JsonSerializer.Deserialize<SalesTaxTemplate>(r.GetString(0)), ("@n", name)).FirstOrDefault();
    }

    /// <summary>Name search: every typed word must prefix-match a word of the item name.
    /// Disabled and non-sales items are excluded before the limit is applied.</summary>
    public IReadOnlyList<Item> Search(string text, int limit = 20)
    {
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => "\"" + t.Replace("\"", "\"\"") + "\"*")
            .ToList();
        if (tokens.Count == 0) return [];
        using var c = db.Open();
        return c.Query($"""
            SELECT i.item_code, i.item_name, i.item_group, i.brand, i.stock_uom, i.disabled, i.is_sales_item
            FROM item_fts JOIN item i ON i.id = item_fts.rowid
            WHERE item_fts MATCH @q AND i.disabled = 0 AND i.is_sales_item = 1
            ORDER BY item_fts.rank
            LIMIT @l
            """, ReadItem, ("@q", string.Join(' ', tokens)), ("@l", limit));
    }

    private static Item ReadItem(SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.GetString(2), Str(r, 3), r.GetString(4), r.GetInt64(5) != 0, r.GetInt64(6) != 0);

    private static ItemTaxAssignment ReadTax(SqliteDataReader r, int first) =>
        new(r.GetString(first), Str(r, first + 1), Date(r, first + 2), r.GetInt32(first + 3));
}
