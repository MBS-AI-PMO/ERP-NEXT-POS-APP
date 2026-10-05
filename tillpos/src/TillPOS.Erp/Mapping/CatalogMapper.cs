using System.Text.Json;
using TillPOS.Core.Catalog;
using TillPOS.Core.Pricing;

namespace TillPOS.Erp.Mapping;

/// <summary>ERPNext JSON → Core models. List rows carry the parent item code in "name".</summary>
public static class CatalogMapper
{
    public static Item Item(JsonElement r) => new(
        r.Str("name"), r.StrOrNull("item_name") ?? r.Str("name"), r.Str("item_group"), r.StrOrNull("brand"),
        r.Str("stock_uom"), r.Bool("disabled"), r.Bool("is_sales_item"));

    public static ItemBarcode? Barcode(JsonElement r) =>
        r.StrOrNull("barcode") is { } b ? new ItemBarcode(b, r.Str("name"), r.StrOrNull("barcode_uom")) : null;

    public static ItemUom? Uom(JsonElement r) =>
        r.StrOrNull("uom") is { } u ? new ItemUom(r.Str("name"), u, r.Dec("conversion_factor")) : null;

    public static ItemTaxAssignment? ItemTax(JsonElement r) =>
        r.StrOrNull("item_tax_template") is { } t ? new ItemTaxAssignment(t, r.StrOrNull("tax_category"), r.Date("valid_from"), r.Int("idx")) : null;

    public static ItemPrice Price(JsonElement r) => new(
        r.Str("name"), r.Str("item_code"), r.StrOrNull("uom"), r.Dec("price_list_rate"), r.Date("valid_from"), r.Date("valid_upto"));

    public static ItemGroupNode Group(JsonElement r) =>
        new(r.Str("name"), r.StrOrNull("parent_item_group"), r.Int("lft"), r.Int("rgt"));

    /// <summary>Null when the rule should not exist on the till (disabled, or not a selling rule).</summary>
    public static PricingRule? PricingRule(JsonElement d)
    {
        if (d.Bool("disable") || !d.Bool("selling")) return null;
        var applyOn = d.StrOrNull("apply_on");
        var (on, targets) = applyOn switch
        {
            "Item Code" => (RuleApplyOn.ItemCode, Targets(d, "items", "item_code")),
            "Item Group" => (RuleApplyOn.ItemGroup, Targets(d, "item_groups", "item_group")),
            "Brand" => (RuleApplyOn.Brand, Targets(d, "brands", "brand")),
            _ => (RuleApplyOn.ItemCode, new List<string>()),
        };
        var kind = d.StrOrNull("rate_or_discount") switch
        {
            "Discount Amount" => RuleKind.DiscountAmount,
            "Rate" => RuleKind.Rate,
            _ => RuleKind.DiscountPercentage,
        };
        var value = kind switch
        {
            RuleKind.DiscountAmount => d.Dec("discount_amount"),
            RuleKind.Rate => d.Dec("rate"),
            _ => d.Dec("discount_percentage"),
        };
        return new PricingRule(d.Str("name"), on, targets, kind, value, d.Int("priority"), d.Date("valid_from"), d.Date("valid_upto"),
            d.StrOrNull("for_price_list"), d.StrOrNull("warehouse"), UnsupportedReason(d, applyOn));
    }

    public static ItemTaxTemplate? ItemTaxTemplate(JsonElement d) =>
        d.Bool("disabled")
            ? null
            : new ItemTaxTemplate(d.Str("name"), d.Rows("taxes")
                .Where(t => t.StrOrNull("tax_type") is not null)
                .GroupBy(t => t.Str("tax_type"))
                .ToDictionary(g => g.Key, g => g.First().Dec("tax_rate")));

    public static SalesTaxTemplate? SalesTaxTemplate(JsonElement d)
    {
        if (d.Bool("disabled")) return null;
        var rows = d.Rows("taxes").ToList();
        var unsupported = rows.Select(t => t.StrOrNull("charge_type") ?? "(blank)").FirstOrDefault(c => c != "On Net Total");
        return new SalesTaxTemplate(
            d.Str("name"),
            rows.Select(t => new TaxRow(t.Int("idx"), t.Str("account_head"), t.StrOrNull("description") ?? t.Str("account_head"),
                t.Dec("rate"), t.Bool("included_in_print_rate"))).ToList(),
            unsupported is null ? null : $"tax charge type '{unsupported}' is not supported");
    }

    public static PosSettings PosSettings(JsonElement profile, JsonElement company, JsonElement? currency, JsonElement? address) => new(
        PosProfile: profile.Str("name"),
        Company: profile.Str("company"),
        CompanyName: company.StrOrNull("company_name") ?? company.Str("name"),
        TaxId: company.StrOrNull("tax_id"),
        AddressText: address is { } a
            ? string.Join(", ", new[] { a.StrOrNull("address_line1"), a.StrOrNull("address_line2"), a.StrOrNull("city") }.OfType<string>())
            : null,
        Currency: profile.StrOrNull("currency") ?? company.Str("default_currency"),
        Warehouse: Required(profile, "warehouse", "warehouse"),
        PriceList: Required(profile, "selling_price_list", "selling price list"),
        Customer: Required(profile, "customer", "default customer"),
        TaxesAndCharges: profile.StrOrNull("taxes_and_charges"),
        TaxCategory: profile.StrOrNull("tax_category"),
        DisableRoundedTotal: profile.Bool("disable_rounded_total"),
        SmallestCurrencyFraction: currency is { } c ? c.Dec("smallest_currency_fraction_value") : 0m,
        WriteOffLimit: profile.Dec("write_off_limit"),
        PaymentModes: profile.Rows("payments").Select(p => new PaymentMode(p.Str("mode_of_payment"), p.Bool("default"))).ToList());

    private static string Required(JsonElement profile, string field, string what) =>
        profile.StrOrNull(field) ?? throw new FormatException($"POS Profile '{profile.StrOrNull("name")}' has no {what} — set one in ERPNext.");

    private static List<string> Targets(JsonElement d, string table, string field) =>
        d.Rows(table).Select(x => x.StrOrNull(field)).OfType<string>().ToList();

    private static string? UnsupportedReason(JsonElement d, string? applyOn)
    {
        if (applyOn is not ("Item Code" or "Item Group" or "Brand")) return $"apply_on '{applyOn}' is not supported";
        if (d.StrOrNull("price_or_product_discount") == "Product") return "free-item (product) discounts are not supported";
        if (d.Dec("min_qty") > 0 || d.Dec("max_qty") > 0) return "quantity conditions are not supported";
        if (d.Dec("min_amt") > 0 || d.Dec("max_amt") > 0) return "amount conditions are not supported";
        if (d.StrOrNull("applicable_for") is not null) return "customer conditions are not supported";
        if (d.StrOrNull("condition") is not null) return "custom conditions are not supported";
        if (d.Bool("mixed_conditions") || d.Bool("is_cumulative")) return "mixed or cumulative conditions are not supported";
        return null;
    }
}
