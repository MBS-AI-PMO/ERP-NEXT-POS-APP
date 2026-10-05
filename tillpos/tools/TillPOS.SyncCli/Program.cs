using System.Diagnostics;
using System.Globalization;
using TillPOS.Core.Money;
using TillPOS.Core.Pricing;
using TillPOS.Core.Sales;
using TillPOS.Data;
using TillPOS.Erp;
using TillPOS.Erp.Mapping;
using TillPOS.Sync;
using TillPOS.SyncCli;

if (args.Length == 0)
{
    Console.WriteLine("usage: TillPOS.SyncCli pull | scan <barcode>... | basket <barcode>... | parity <barcode>...");
    return 1;
}

var config = CliConfig.Load(Path.Combine(AppContext.BaseDirectory, "tillpos.cli.json"));
var db = new TillDb(config.DbPath);
db.Migrate();
var store = new CatalogStore(db);
var catalog = new SqliteCatalog(db);
var erp = ErpClient.Create(new ErpConnection(new Uri(config.BaseUrl), config.ApiKey, config.ApiSecret), TimeSpan.FromSeconds(60));

switch (args[0])
{
    case "pull":
    {
        var ctx = new SyncContext(erp, store, new KeysetPager(erp, new KvSyncStateStore(store)), config.PosProfile);
        var total = Stopwatch.StartNew();
        var report = await CatalogPuller.CreateDefault(ctx, catalog.Reload).RunAsync();
        foreach (var f in report.Feeds)
            Console.WriteLine($"{f.Feed,-34} {f.Rows,7} rows {f.Duration.TotalSeconds,7:0.0}s  {f.Error}");
        Console.WriteLine($"TOTAL {total.Elapsed.TotalSeconds:0.0}s — {(report.Ok ? "OK" : "WITH ERRORS")}; items in catalog: {store.AllItemCodes().Count}");
        return report.Ok ? 0 : 1;
    }
    case "scan":
    case "basket":
    case "parity":
    {
        var settings = store.LoadPosSettings() ?? throw new InvalidOperationException("Run 'pull' first.");
        var saleCtx = SaleContext.Create(catalog, settings, catalog.FindSalesTaxTemplate, config.Precision, config.Rounding,
            () => DateOnly.FromDateTime(DateTime.Now));
        var template = saleCtx.TaxTemplate;
        var cart = new Cart(saleCtx);

        foreach (var barcode in args.Skip(1))
        {
            var sw = Stopwatch.StartNew();
            var result = cart.AddBarcode(barcode);
            Console.WriteLine($"scan {barcode,-16} {result.Outcome,-16} {sw.Elapsed.TotalMilliseconds,6:0.0} ms");
        }
        foreach (var l in cart.Lines)
            Console.WriteLine($"  {l.Item.ItemCode,-14} {l.Item.ItemName,-30} {l.Qty,5} {l.Uom,-5} {l.PriceListRate,10:0.00} {l.Rule?.Label,-9} {l.Rate,10:0.00}");
        var t = cart.Totals();
        Console.WriteLine($"Total {t.Total:0.00}  Net {t.NetTotal:0.00}  VAT {t.TotalTaxes:0.00}  Grand {t.GrandTotal:0.00}  Rounded {t.RoundedTotal:0.00}  DUE {t.AmountDue:0.00}  Saved {cart.DiscountSaved():0.00}");
        if (args[0] != "parity") return 0;

        if (!config.AllowWrites)
        {
            Console.Error.WriteLine("parity creates and deletes a DRAFT POS Invoice. Set \"AllowWrites\": true ONLY for a TEST site.");
            return 2;
        }
        var now = DateTime.Now;
        var doc = new Dictionary<string, object?>
        {
            ["company"] = settings.Company,
            ["pos_profile"] = settings.PosProfile,
            ["customer"] = settings.Customer,
            ["is_pos"] = 1,
            ["update_stock"] = 1,
            ["set_posting_time"] = 1,
            ["posting_date"] = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["posting_time"] = now.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            ["selling_price_list"] = settings.PriceList,
            ["ignore_pricing_rule"] = 1,
            ["taxes_and_charges"] = settings.TaxesAndCharges,
            ["taxes"] = template?.Rows.Select(r => new Dictionary<string, object?>
            {
                ["charge_type"] = "On Net Total", ["account_head"] = r.AccountHead, ["description"] = r.Description,
                ["rate"] = r.Rate, ["included_in_print_rate"] = r.IncludedInPrintRate ? 1 : 0,
            }).ToList(),
            ["items"] = cart.Lines.Select(l => new Dictionary<string, object?>
            {
                ["item_code"] = l.Item.ItemCode, ["qty"] = l.Qty, ["uom"] = l.Uom, ["conversion_factor"] = l.ConversionFactor,
                ["price_list_rate"] = l.PriceListRate,
                ["discount_percentage"] = l.Rule?.Kind == RuleKind.DiscountPercentage ? l.Rule.Value : 0m,
                ["discount_amount"] = l.PriceListRate - l.Rate,
                ["rate"] = l.Rate, ["warehouse"] = settings.Warehouse, ["item_tax_template"] = l.ItemTaxTemplate,
            }).ToList(),
            ["payments"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["mode_of_payment"] = (settings.PaymentModes.FirstOrDefault(p => p.IsDefault) ?? settings.PaymentModes[0]).ModeOfPayment,
                    ["amount"] = t.AmountDue,
                },
            },
        };

        var created = await erp.InsertAsync("POS Invoice", doc);
        var name = created.Str("name");
        try
        {
            var rows = new (string Field, decimal Till, decimal Erp)[]
            {
                ("net_total", t.NetTotal, created.Dec("net_total")),
                ("total_taxes_and_charges", t.TotalTaxes, created.Dec("total_taxes_and_charges")),
                ("grand_total", t.GrandTotal, created.Dec("grand_total")),
                ("rounded_total", t.RoundedTotal, created.Dec("rounded_total")),
            };
            foreach (var (field, till, erpValue) in rows)
                Console.WriteLine($"{field,-26} till {till,12:0.00}  erpnext {erpValue,12:0.00}  {(till == erpValue ? "OK" : "MISMATCH")}");
            return rows.All(r => r.Till == r.Erp) ? 0 : 1;
        }
        finally
        {
            await erp.DeleteAsync("POS Invoice", name);
            Console.WriteLine($"draft {name} deleted");
        }
    }
    default:
        Console.Error.WriteLine($"unknown command '{args[0]}'");
        return 1;
}
