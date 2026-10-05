using System.Text.Json;
using TillPOS.Core.Pricing;
using TillPOS.Erp.Mapping;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Erp;

public class CatalogMapperTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Maps_item_group_pricing_rule_with_text_priority()
    {
        var rule = CatalogMapper.PricingRule(J("""
            {"name":"PRLE-0001","apply_on":"Item Group","item_groups":[{"item_group":"Rice"}],"selling":1,"disable":0,
             "price_or_product_discount":"Price","rate_or_discount":"Discount Percentage","discount_percentage":10,
             "priority":"3","valid_from":"2026-10-01","valid_upto":null,"for_price_list":"","min_qty":0,"max_qty":0}
            """))!;
        Assert.Equal(RuleApplyOn.ItemGroup, rule.ApplyOn);
        Assert.Equal(new[] { "Rice" }, rule.Targets);
        Assert.Equal(10m, rule.Value);
        Assert.Equal(3, rule.Priority);
        Assert.Equal(new DateOnly(2026, 10, 1), rule.ValidFrom);
        Assert.Null(rule.ForPriceList);
        Assert.Null(rule.UnsupportedReason);
    }

    [Theory]
    [InlineData("\"min_qty\":3", "quantity")]
    [InlineData("\"applicable_for\":\"Customer\"", "customer")]
    [InlineData("\"price_or_product_discount\":\"Product\"", "free-item")]
    [InlineData("\"condition\":\"doc.total > 100\"", "custom")]
    [InlineData("\"coupon_code_based\":1", "coupon")]
    [InlineData("\"margin_rate_or_amount\":5", "margin")]
    [InlineData("\"apply_rule_on_other\":\"Item Code\"", "other items")]
    public void Marks_unsupported_rules(string extra, string reasonContains)
    {
        var rule = CatalogMapper.PricingRule(J($$"""
            {"name":"R","apply_on":"Item Code","items":[{"item_code":"RICE5"}],"selling":1,"rate_or_discount":"Discount Percentage","discount_percentage":10,{{extra}}}
            """))!;
        Assert.Contains(reasonContains, rule.UnsupportedReason);
    }

    [Fact]
    public void Disabled_or_buying_only_rule_maps_to_null()
    {
        Assert.Null(CatalogMapper.PricingRule(J("""{"name":"R","apply_on":"Item Code","selling":1,"disable":1}""")));
        Assert.Null(CatalogMapper.PricingRule(J("""{"name":"R","apply_on":"Item Code","selling":0,"buying":1}""")));
    }

    [Fact]
    public void Sales_tax_template_flags_unsupported_charge_types()
    {
        var ok = CatalogMapper.SalesTaxTemplate(J("""
            {"name":"UAE VAT 5%","taxes":[{"idx":1,"charge_type":"On Net Total","account_head":"VAT 5% - S","description":"VAT 5%","rate":5,"included_in_print_rate":1}]}
            """))!;
        Assert.Null(ok.UnsupportedReason);
        Assert.True(ok.Rows[0].IncludedInPrintRate);

        var bad = CatalogMapper.SalesTaxTemplate(J("""
            {"name":"X","taxes":[{"idx":1,"charge_type":"Actual","account_head":"Freight - S","rate":0,"tax_amount":10}]}
            """))!;
        Assert.Contains("Actual", bad.UnsupportedReason);
    }

    [Fact]
    public void Pos_settings_require_a_default_customer()
    {
        var profile = J("""{"name":"Till 1","company":"Shop LLC","warehouse":"Stores - S","selling_price_list":"Retail","payments":[]}""");
        var company = J("""{"name":"Shop LLC","company_name":"Shop LLC","default_currency":"AED","tax_id":"100000000000003"}""");
        var ex = Assert.Throws<FormatException>(() => CatalogMapper.PosSettings(profile, company, null, null));
        Assert.Contains("default customer", ex.Message);
    }

    [Fact]
    public void Flags_rules_from_another_company()
    {
        var json = """{"name":"R","apply_on":"Item Code","selling":1,"rate_or_discount":"Discount Percentage","discount_percentage":10,"company":"Other LLC"}""";
        Assert.Contains("Other LLC", CatalogMapper.PricingRule(J(json), "Shop LLC")!.UnsupportedReason);
        Assert.Null(CatalogMapper.PricingRule(J(json.Replace("Other LLC", "Shop LLC")), "Shop LLC")!.UnsupportedReason);
    }

    [Fact]
    public void Template_items_are_not_sellable()
    {
        var item = CatalogMapper.Item(J("""{"name":"T","item_name":"T","item_group":"G","stock_uom":"Nos","is_sales_item":1,"has_variants":1}"""));
        Assert.False(item.IsSalesItem);
    }

    [Fact]
    public void Maps_pos_settings()
    {
        var profile = J("""
            {"name":"Till 1","company":"Shop LLC","warehouse":"Stores - S","selling_price_list":"Retail","customer":"Walk-in Customer",
             "taxes_and_charges":"UAE VAT 5%","disable_rounded_total":0,"write_off_limit":0.05,
             "payments":[{"mode_of_payment":"Cash","default":1},{"mode_of_payment":"Card","default":0}]}
            """);
        var company = J("""{"name":"Shop LLC","company_name":"Shop LLC","default_currency":"AED","tax_id":"100000000000003"}""");
        var currency = J("""{"name":"AED","smallest_currency_fraction_value":0.25}""");
        var address = J("""{"address_line1":"Shop 4, Al Quoz","city":"Dubai"}""");

        var s = CatalogMapper.PosSettings(profile, company, currency, address);

        Assert.Equal("AED", s.Currency);
        Assert.Equal(M("0.25"), s.SmallestCurrencyFraction);
        Assert.Equal(M("0.05"), s.WriteOffLimit);
        Assert.Equal("Shop 4, Al Quoz, Dubai", s.AddressText);
        Assert.True(s.PaymentModes[0].IsDefault);
    }
}
