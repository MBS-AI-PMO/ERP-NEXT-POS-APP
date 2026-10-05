using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TillPOS.Core.Catalog;
using TillPOS.Core.Pricing;
using static TillPOS.Data.SqlExt;

namespace TillPOS.Data;

public sealed record ItemSnapshot(Item Item, IReadOnlyList<ItemBarcode> Barcodes, IReadOnlyList<ItemUom> Uoms, IReadOnlyList<ItemTaxAssignment> Taxes);

public sealed record ItemGroupSnapshot(ItemGroupNode Node, IReadOnlyList<ItemTaxAssignment> Taxes);

/// <summary>All writes to the local catalog. Each call is one transaction.</summary>
public sealed class CatalogStore(TillDb db)
{
    private const string PosSettingsKey = "pos_settings";

    public void UpsertItems(IEnumerable<ItemSnapshot> items)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        foreach (var s in items)
        {
            var i = s.Item;
            var id = Convert.ToInt64(c.Scalar(tx, """
                INSERT INTO item (item_code, item_name, item_group, brand, stock_uom, disabled, is_sales_item)
                VALUES (@c, @n, @g, @b, @u, @d, @s)
                ON CONFLICT(item_code) DO UPDATE SET item_name = excluded.item_name, item_group = excluded.item_group,
                    brand = excluded.brand, stock_uom = excluded.stock_uom, disabled = excluded.disabled, is_sales_item = excluded.is_sales_item
                RETURNING id
                """,
                ("@c", i.ItemCode), ("@n", i.ItemName), ("@g", i.ItemGroup), ("@b", i.Brand), ("@u", i.StockUom),
                ("@d", i.Disabled ? 1 : 0), ("@s", i.IsSalesItem ? 1 : 0)), CultureInfo.InvariantCulture);

            c.Exec(tx, "DELETE FROM item_fts WHERE rowid = @id", ("@id", id));
            c.Exec(tx, "INSERT INTO item_fts (rowid, item_name) VALUES (@id, @n)", ("@id", id), ("@n", i.ItemName));

            c.Exec(tx, "DELETE FROM item_barcode WHERE item_code = @c", ("@c", i.ItemCode));
            foreach (var b in s.Barcodes)
                c.Exec(tx, "INSERT OR REPLACE INTO item_barcode (barcode, item_code, uom) VALUES (@b, @c, @u)",
                    ("@b", b.Barcode), ("@c", i.ItemCode), ("@u", b.Uom));

            c.Exec(tx, "DELETE FROM item_uom WHERE item_code = @c", ("@c", i.ItemCode));
            foreach (var u in s.Uoms)
                c.Exec(tx, "INSERT OR REPLACE INTO item_uom (item_code, uom, conversion_factor) VALUES (@c, @u, @f)",
                    ("@c", i.ItemCode), ("@u", u.Uom), ("@f", Dec(u.ConversionFactor)));

            c.Exec(tx, "DELETE FROM item_tax WHERE parent_type = 'Item' AND parent = @c", ("@c", i.ItemCode));
            foreach (var t in s.Taxes) InsertTax(c, tx, "Item", i.ItemCode, t);
        }
        tx.Commit();
    }

    public void ReplaceItemGroups(IEnumerable<ItemGroupSnapshot> groups)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        c.Exec(tx, "DELETE FROM item_group");
        c.Exec(tx, "DELETE FROM item_tax WHERE parent_type = 'Item Group'");
        foreach (var g in groups)
        {
            c.Exec(tx, "INSERT INTO item_group (name, parent, lft, rgt) VALUES (@n, @p, @l, @r)",
                ("@n", g.Node.Name), ("@p", g.Node.Parent), ("@l", g.Node.Lft), ("@r", g.Node.Rgt));
            foreach (var t in g.Taxes) InsertTax(c, tx, "Item Group", g.Node.Name, t);
        }
        tx.Commit();
    }

    public void UpsertPrices(IEnumerable<ItemPrice> prices)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        foreach (var p in prices)
            c.Exec(tx, """
                INSERT OR REPLACE INTO item_price (name, item_code, uom, price_list_rate, valid_from, valid_upto)
                VALUES (@n, @c, @u, @r, @f, @t)
                """,
                ("@n", p.Name), ("@c", p.ItemCode), ("@u", p.Uom), ("@r", Dec(p.PriceListRate)),
                ("@f", Date(p.ValidFrom)), ("@t", Date(p.ValidUpto)));
        tx.Commit();
    }

    public void UpsertPricingRule(PricingRule rule) => UpsertJson("pricing_rule", rule.Name, rule);
    public void DeletePricingRule(string name) => DeleteDocument("Pricing Rule", name);
    public void UpsertItemTaxTemplate(ItemTaxTemplate t) => UpsertJson("item_tax_template", t.Name, t);
    public void DeleteItemTaxTemplate(string name) => DeleteDocument("Item Tax Template", name);
    public void UpsertSalesTaxTemplate(SalesTaxTemplate t) => UpsertJson("sales_tax_template", t.Name, t);
    public void DeleteSalesTaxTemplate(string name) => DeleteDocument("Sales Taxes and Charges Template", name);

    public void DeleteDocument(string doctype, string name)
    {
        string[] statements = doctype switch
        {
            "Item" =>
            [
                "DELETE FROM item_fts WHERE rowid IN (SELECT id FROM item WHERE item_code = @n)",
                "DELETE FROM item WHERE item_code = @n",
                "DELETE FROM item_barcode WHERE item_code = @n",
                "DELETE FROM item_uom WHERE item_code = @n",
                "DELETE FROM item_price WHERE item_code = @n",
                "DELETE FROM item_tax WHERE parent_type = 'Item' AND parent = @n",
            ],
            "Item Price" => ["DELETE FROM item_price WHERE name = @n"],
            "Pricing Rule" => ["DELETE FROM pricing_rule WHERE name = @n"],
            "Item Tax Template" => ["DELETE FROM item_tax_template WHERE name = @n"],
            "Sales Taxes and Charges Template" => ["DELETE FROM sales_tax_template WHERE name = @n"],
            _ => throw new ArgumentException($"Deleting {doctype} is not supported.", nameof(doctype)),
        };
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        foreach (var sql in statements) c.Exec(tx, sql, ("@n", name));
        tx.Commit();
    }

    public IReadOnlyList<string> AllItemCodes()
    {
        using var c = db.Open();
        return c.Query("SELECT item_code FROM item", r => r.GetString(0));
    }

    public IReadOnlyList<string> AllPriceNames()
    {
        using var c = db.Open();
        return c.Query("SELECT name FROM item_price", r => r.GetString(0));
    }

    public void SavePosSettings(PosSettings settings) => SetValue(PosSettingsKey, JsonSerializer.Serialize(settings));

    public PosSettings? LoadPosSettings() =>
        GetValue(PosSettingsKey) is { } json ? JsonSerializer.Deserialize<PosSettings>(json) : null;

    public string? GetValue(string key)
    {
        using var c = db.Open();
        return c.Scalar(null, "SELECT value FROM kv WHERE key = @k", ("@k", key)) as string;
    }

    public void SetValue(string key, string value)
    {
        using var c = db.Open();
        c.Exec(null, "INSERT OR REPLACE INTO kv (key, value) VALUES (@k, @v)", ("@k", key), ("@v", value));
    }

    private void UpsertJson<T>(string table, string name, T value)
    {
        using var c = db.Open();
        c.Exec(null, $"INSERT OR REPLACE INTO {table} (name, json) VALUES (@n, @j)", ("@n", name), ("@j", JsonSerializer.Serialize(value)));
    }

    private static void InsertTax(SqliteConnection c, SqliteTransaction tx, string parentType, string parent, ItemTaxAssignment t) =>
        c.Exec(tx, """
            INSERT OR REPLACE INTO item_tax (parent_type, parent, idx, item_tax_template, tax_category, valid_from)
            VALUES (@pt, @p, @i, @t, @c, @f)
            """,
            ("@pt", parentType), ("@p", parent), ("@i", t.Idx), ("@t", t.ItemTaxTemplate), ("@c", t.TaxCategory), ("@f", Date(t.ValidFrom)));
}
