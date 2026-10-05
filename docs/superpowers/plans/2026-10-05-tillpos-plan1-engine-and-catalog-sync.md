# TillPOS Plan 1 — Pricing/VAT Engine and Catalog Sync — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the till's core engine — item lookup, Retail prices, Pricing Rule offers, UAE VAT and bill totals that match ERPNext to the fils — plus the background "pull" that keeps a local SQLite catalog in sync with ERPNext, proven end-to-end with a command-line tool against a **test copy** of the shop's ERPNext.

**Architecture:** Pure C# domain logic in `TillPOS.Core` (no I/O, fully unit-tested), SQLite storage in `TillPOS.Data`, a thin ERPNext REST client in `TillPOS.Erp`, incremental keyset-paged pulls in `TillPOS.Sync`, and a console tool `TillPOS.SyncCli` that exercises everything against the test site. The WPF app (Plan 3) will reuse all of this unchanged.

**Tech Stack:** .NET 10 (C#), xUnit, Microsoft.Data.Sqlite (FTS5), System.Text.Json, HttpClient.

**Spec:** `docs/superpowers/specs/2026-10-05-tillpos-offline-pos-design.md`

**Roadmap (later plans, written after this one ships):**
- Plan 2 — Sales, returns, shifts and push sync (POS Invoice, Opening/Closing Entry, outbox, idempotency).
- Plan 3 — WPF till app (screens from the approved mockups), PIN login, ESC/POS printing and cash drawer.
- Plan 4 — Installer, first-run setup, on-till acceptance testing.

## Global Constraints

- Target framework `net10.0` (WPF app in Plan 3 will be `net10.0-windows`); tills run Windows 10/11 64-bit.
- Money is always `decimal`, never `float`/`double`; stored in SQLite as invariant-culture text.
- All parsing/formatting uses `CultureInfo.InvariantCulture`.
- Currency precision 2; rounding method configurable (`Bankers` default, `Commercial`), matching the ERPNext System Settings value recorded in Task 1.
- Nothing in this plan writes to the **live** ERPNext. The only write (Task 12 `parity`) is guarded by `"AllowWrites": true` and is run only against the test site.
- Single retail price list (from POS Profile); Pricing Rules limited to item-level Price discounts by Item Code / Item Group / Brand (spec §7). Unsupported rules are skipped, never guessed.
- Tax rows supported: `On Net Total` only, inclusive or exclusive (spec §7.1).
- `TreatWarningsAsErrors` and nullable reference types on in every project.
- API keys/secrets never committed (`tillpos.cli.json` is git-ignored).

## Review Focus

1. **Item renamed or deleted in ERPNext without a Deleted Document entry** → it must disappear from the till within 24 h (daily reconcile, Task 11 test `Reconcile_removes_items_missing_on_server`).
2. **Barcode moved from one item to another** → scanning must return the new item, never the old one (Task 8 test `Barcode_moved_to_another_item_points_to_new_item`).
3. **Item Price for another price list or a specific customer** → must never be used as the till price; a price moved off Retail must be removed locally (Task 11 `FeedTests`: `Item_price_feed_keeps_only_retail_prices_without_customer`, `Item_price_moved_off_retail_is_removed_locally`).
4. **One doctype not readable (403) or failing mid-pull** → the rest of the pull still runs and the failure is reported (Task 11 test `Puller_continues_after_a_feed_fails`).
5. **Network drop in the middle of paging** → the next pull resumes from the last saved page with no skipped or duplicated rows (Task 10 test `Resumes_after_handler_failure_without_skipping`).

---

## File Structure

```
D:\XAMPP\htdocs\ERP-NEXT\
  .gitignore
  docs/erp-api-notes.md                       (Task 1: results of API checks on the test site)
  tillpos/
    TillPOS.slnx
    Directory.Build.props
    src/TillPOS.Core/
      Money/Rounder.cs                        rounding exactly like Frappe/ERPNext
      Catalog/CatalogModels.cs                Item, barcode, UOM, price, group, tax records
      Catalog/ICatalog.cs                     read-only catalog lookup used by pricing
      Catalog/PosSettings.cs                  synced POS Profile + Company values
      Pricing/PricingRule.cs                  rule model + AppliedRule
      Pricing/PriceResolver.cs                Retail base price for item/UOM/date
      Pricing/PricingRuleSelector.cs          choose the one offer that applies
      Pricing/LineMath.cs                     rate after offer
      Tax/TaxModels.cs                        TaxLineInput, BillTotals, exception
      Tax/TaxCalculator.cs                    port of ERPNext taxes_and_totals (On Net Total)
      Tax/ItemTaxResolver.cs                  which Item Tax Template applies to an item
      Sales/Cart.cs                           bill in progress: add/scan/qty/remove/totals
    src/TillPOS.Data/
      SqlExt.cs                               tiny ADO.NET helpers
      Migrations.cs                           schema v1
      TillDb.cs                               open connection (WAL) + migrate
      CatalogStore.cs                         writes from sync + key/value store
      SqliteCatalog.cs                        ICatalog over SQLite + name search
    src/TillPOS.Erp/
      ErpModels.cs                            ErpConnection, ListQuery, ServerInfo
      ErpException.cs                         ERPNext error → message
      IErpClient.cs / ErpClient.cs            REST client
      Mapping/JsonFields.cs                   safe JsonElement accessors
      Mapping/CatalogMapper.cs                ERPNext JSON → Core models
    src/TillPOS.Sync/
      SyncState.cs                            SyncMark, ISyncStateStore, KvSyncStateStore
      KeysetPager.cs                          incremental, resumable paging by modified
      SyncContext.cs                          shared dependencies for feeds
      Feeds/*.cs                              one feed per doctype
      CatalogPuller.cs                        runs all feeds, reports results
    tools/TillPOS.SyncCli/
      CliConfig.cs, Program.cs                pull / scan / basket / parity commands
    tests/TillPOS.Tests/
      TestUtil.cs, Fakes/*.cs, Core/*.cs, Data/*.cs, Erp/*.cs, Sync/*.cs
```

---

### Task 0: Toolchain, repository and solution skeleton

**Files:**
- Create: `.gitignore`, `tillpos/Directory.Build.props`, `tillpos/TillPOS.slnx`, the 6 project folders.

**Interfaces:**
- Consumes: nothing.
- Produces: projects `TillPOS.Core`, `TillPOS.Data`, `TillPOS.Erp`, `TillPOS.Sync`, `TillPOS.SyncCli`, `TillPOS.Tests` with references Data→Core, Erp→Core, Sync→Core/Data/Erp, SyncCli→all, Tests→all.

- [ ] **Step 1: Install the .NET 10 SDK**

Run (PowerShell): `winget install --id Microsoft.DotNet.SDK.10 --exact --accept-source-agreements --accept-package-agreements`
Then open a new terminal and run: `dotnet --list-sdks`
Expected: a line starting with `10.0.`

- [ ] **Step 2: Initialise git at the workspace root**

```powershell
cd D:\XAMPP\htdocs\ERP-NEXT
git init
```

Create `D:\XAMPP\htdocs\ERP-NEXT\.gitignore`:

```gitignore
bin/
obj/
.vs/
*.user
*.db
*.db-wal
*.db-shm
tillpos/tools/TillPOS.SyncCli/tillpos.cli.json
backup/
```

- [ ] **Step 3: Create the solution and projects**

```powershell
mkdir tillpos; cd tillpos
dotnet new sln -n TillPOS
dotnet new classlib -n TillPOS.Core -o src/TillPOS.Core
dotnet new classlib -n TillPOS.Data -o src/TillPOS.Data
dotnet new classlib -n TillPOS.Erp -o src/TillPOS.Erp
dotnet new classlib -n TillPOS.Sync -o src/TillPOS.Sync
dotnet new console -n TillPOS.SyncCli -o tools/TillPOS.SyncCli
dotnet new xunit -n TillPOS.Tests -o tests/TillPOS.Tests
dotnet sln add src/TillPOS.Core src/TillPOS.Data src/TillPOS.Erp src/TillPOS.Sync tools/TillPOS.SyncCli tests/TillPOS.Tests
dotnet add src/TillPOS.Data reference src/TillPOS.Core
dotnet add src/TillPOS.Erp reference src/TillPOS.Core
dotnet add src/TillPOS.Sync reference src/TillPOS.Core src/TillPOS.Data src/TillPOS.Erp
dotnet add tools/TillPOS.SyncCli reference src/TillPOS.Core src/TillPOS.Data src/TillPOS.Erp src/TillPOS.Sync
dotnet add tests/TillPOS.Tests reference src/TillPOS.Core src/TillPOS.Data src/TillPOS.Erp src/TillPOS.Sync
dotnet add src/TillPOS.Data package Microsoft.Data.Sqlite
Remove-Item src/*/Class1.cs
```

(`dotnet new sln` on .NET 10 creates `TillPOS.slnx`; `dotnet sln add` finds it automatically.)

- [ ] **Step 4: Shared build settings**

Create `tillpos/Directory.Build.props` (applies to every project; it overrides the template's own `TargetFramework`):

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>
```

Delete the `<TargetFramework>`, `<Nullable>` and `<ImplicitUsings>` lines from each generated `.csproj` so the shared file is the single source.

- [ ] **Step 5: Build and run the template test**

Run: `dotnet build` then `dotnet test`
Expected: build succeeds with 0 warnings; 1 test passed (the template `UnitTest1`).

- [ ] **Step 6: Commit**

```powershell
cd D:\XAMPP\htdocs\ERP-NEXT
git add .gitignore tillpos docs
git commit -m "chore: TillPOS solution skeleton, spec and plan"
```

---

### Task 1: Test copy of ERPNext, admin setup and API checks (manual)

This task has no code. It produces the test site every later verification uses, and a notes file recording facts the code depends on. **Never point any TillPOS tool at the live site.**

**Files:**
- Create: `docs/erp-api-notes.md`

**Interfaces:**
- Produces: a test site at `http://localhost:8080` with a till API user; `docs/erp-api-notes.md` with the answers below (Tasks 11–12 read them).

- [ ] **Step 1: Copy the live Docker image to the development laptop**

On the live server (SSH):

```bash
docker inspect --format '{{.Config.Image}}' $(docker ps --format '{{.Names}}' | grep backend | head -1)
# note the image name, e.g. registry/erp-custom:15
docker save <image-name> | gzip > ~/erp-image.tar.gz
```

On the laptop (PowerShell, Docker Desktop installed and running):

```powershell
scp user@server-ip:~/erp-image.tar.gz D:\erp-test\
docker load -i D:\erp-test\erp-image.tar.gz
```

- [ ] **Step 2: Start a local stack with that image**

Download `pwd.yml` from https://github.com/frappe/frappe_docker into `D:\erp-test\`, replace every `image: frappe/erpnext:...` line with the loaded image name, then:

```powershell
cd D:\erp-test
docker compose -p erptest -f pwd.yml up -d
```

Wait until `http://localhost:8080` shows the login page (site name in `pwd.yml` is `frontend`).

- [ ] **Step 3: Restore the live backup into the test site (with emails muted)**

Copy the backup files from the earlier backup (`*-database.sql.gz`, `*-files.tar`, `*-private-files.tar`, `site_config_backup.json`) into the backend container and restore:

```powershell
docker cp D:\XAMPP\htdocs\ERP-NEXT\backup\. erptest-backend-1:/tmp/backup/
docker exec -it erptest-backend-1 bash
```

```bash
cd /home/frappe/frappe-bench
bench --site frontend set-config mute_emails 1
bench --site frontend restore /tmp/backup/<name>-database.sql.gz \
  --with-public-files /tmp/backup/<name>-files.tar \
  --with-private-files /tmp/backup/<name>-private-files.tar
bench --site frontend set-config encryption_key "<encryption_key from site_config_backup.json>"
bench --site frontend set-config mute_emails 1
bench --site frontend disable-scheduler
bench --site frontend migrate
```

Expected: you can log in at `http://localhost:8080` with a live user's credentials and see live items.

- [ ] **Step 4: Do the spec §4 admin setup on the test site**

In the test site UI (as Administrator), exactly as the admin will later do on live:
1. Stock Settings → Allow Negative Stock = ✓.
2. Role `TillPOS Device`; Role Permission Manager: Read for Item, Item Price, Item Group, Brand, Pricing Rule, POS Profile, Sales Taxes and Charges Template, Item Tax Template, Company, Currency, Address, Mode of Payment, Deleted Document; Read+Create+Submit for POS Invoice, POS Opening Entry, POS Closing Entry.
3. User `till1@shop.local` with only role `TillPOS Device`; User → Settings → API Access → Generate Keys; save key and secret.
4. POS Profile for Till 1 (copy of the current one) with user `till1@shop.local`, Retail price list, the UAE VAT template, payment modes Cash (default) and Card, write-off limit 0.05.

- [ ] **Step 5: Run the API checks as the till user**

Create `D:\erp-test\q-barcodes.json`:

```json
{"doctype":"Item","fields":["name","`tabItem Barcode`.barcode as barcode","`tabItem Barcode`.uom as barcode_uom"],"filters":[],"order_by":"name asc","limit_start":0,"limit_page_length":5}
```

Create `D:\erp-test\q-prices.json`:

```json
{"doctype":"Item Price","fields":["name","item_code","uom","price_list_rate","valid_from","valid_upto","price_list","customer","batch_no","modified"],"filters":[],"order_by":"modified asc","limit_start":0,"limit_page_length":3}
```

Create `D:\erp-test\q-deleted.json`:

```json
{"doctype":"Deleted Document","fields":["name","deleted_doctype","deleted_name","modified"],"filters":[],"order_by":"modified asc","limit_start":0,"limit_page_length":3}
```

Run each (replace KEY:SECRET):

```powershell
$h = @("-H","Authorization: token KEY:SECRET","-H","Content-Type: application/json")
curl.exe -s -X POST http://localhost:8080/api/method/frappe.client.get_list @h -d "@D:\erp-test\q-barcodes.json"
curl.exe -s -X POST http://localhost:8080/api/method/frappe.client.get_list @h -d "@D:\erp-test\q-prices.json"
curl.exe -s -X POST http://localhost:8080/api/method/frappe.client.get_list @h -d "@D:\erp-test\q-deleted.json"
curl.exe -s "http://localhost:8080/api/resource/POS%20Profile/<till 1 profile name>" @h
curl.exe -s "http://localhost:8080/api/resource/Pricing%20Rule/<any active rule name>" @h
curl.exe -s "http://localhost:8080/api/resource/Currency/AED" @h
curl.exe -s http://localhost:8080/api/method/frappe.auth.get_logged_user @h -i
```

- [ ] **Step 6: Record the answers**

Create `docs/erp-api-notes.md`:

```markdown
# ERPNext API notes (test site, ERPNext 15.114.0 / Frappe 15.113.0)

| # | Check | Result (paste/describe) | OK? |
|---|---|---|---|
| A | get_list with child fields `` `tabItem Barcode`.barcode `` returns one row per barcode (items without barcode → barcode null) | | |
| B | Item Price list accepts fields `customer` and `batch_no` | | |
| C | Till user can read Deleted Document | | |
| D | Pricing Rule doc has `items` / `item_groups` / `brands` child tables, `priority` as text, `rate_or_discount` | | |
| E | Till user can read POS Profile, Company, Currency, Address | | |
| F | System Settings → Rounding Method (read in UI as Administrator) | | |
| G | Currency AED `smallest_currency_fraction_value` | | |
| H | `get_logged_user` response has an HTTP `Date` header | | |
```

Fill every row. If **A** fails, Task 11 uses the per-item fallback given there. If **B** says `batch_no` is not a valid field, remove `"batch_no"` from the field list in Task 11 `ItemPriceFeed`. Record **F** as `Bankers` or `Commercial` for the CLI config in Task 12 ("Banker's Rounding (legacy)" and "Banker's Rounding" both → `Bankers`).

- [ ] **Step 7: Commit**

```powershell
git add docs/erp-api-notes.md
git commit -m "docs: ERPNext API checks on test site"
```

---

### Task 2: Money rounding identical to Frappe

**Files:**
- Create: `tillpos/src/TillPOS.Core/Money/Rounder.cs`
- Create: `tillpos/tests/TillPOS.Tests/TestUtil.cs`
- Test: `tillpos/tests/TillPOS.Tests/Core/RounderTests.cs`
- Delete: `tillpos/tests/TillPOS.Tests/UnitTest1.cs`

**Interfaces:**
- Produces:
  - `enum RoundingMethod { Bankers, Commercial }`
  - `record MoneySettings(int Precision = 2, RoundingMethod Rounding = RoundingMethod.Bankers, decimal SmallestCurrencyFraction = 0m, bool DisableRoundedTotal = false)`
  - `static decimal Rounder.Round(decimal value, int precision, RoundingMethod method)`
  - `static decimal Rounder.Round(decimal value, MoneySettings money)`
  - `static decimal Rounder.RoundToSmallestFraction(decimal value, MoneySettings money)`
  - Test helper `static decimal TestUtil.M(string s)` (invariant decimal parse)

- [ ] **Step 1: Write the failing tests**

`tillpos/tests/TillPOS.Tests/TestUtil.cs`:

```csharp
using System.Globalization;

namespace TillPOS.Tests;

public static class TestUtil
{
    public static decimal M(string s) => decimal.Parse(s, CultureInfo.InvariantCulture);
}
```

`tillpos/tests/TillPOS.Tests/Core/RounderTests.cs`:

```csharp
using TillPOS.Core.Money;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Core;

public class RounderTests
{
    [Theory]
    [InlineData("2.345", "2.34")]
    [InlineData("2.355", "2.36")]
    [InlineData("-2.345", "-2.34")]
    [InlineData("0.015", "0.02")]
    public void Bankers_rounds_half_to_even(string input, string expected) =>
        Assert.Equal(M(expected), Rounder.Round(M(input), 2, RoundingMethod.Bankers));

    [Theory]
    [InlineData("2.345", "2.35")]
    [InlineData("-2.345", "-2.35")]
    public void Commercial_rounds_half_away_from_zero(string input, string expected) =>
        Assert.Equal(M(expected), Rounder.Round(M(input), 2, RoundingMethod.Commercial));

    [Theory]
    [InlineData("10.12", "10.00")]
    [InlineData("10.13", "10.25")]
    [InlineData("10.375", "10.25")]
    [InlineData("-10.13", "-10.25")]
    public void Rounds_to_smallest_currency_fraction_like_erpnext(string input, string expected)
    {
        var money = new MoneySettings(SmallestCurrencyFraction: M("0.25"));
        Assert.Equal(M(expected), Rounder.RoundToSmallestFraction(M(input), money));
    }

    [Theory]
    [InlineData("10.5", "Bankers", "10.00")]
    [InlineData("11.5", "Bankers", "12.00")]
    [InlineData("10.5", "Commercial", "11.00")]
    [InlineData("10.49", "Commercial", "10.00")]
    public void Without_fraction_rounds_to_whole_units(string input, string method, string expected)
    {
        var money = new MoneySettings(Rounding: Enum.Parse<RoundingMethod>(method));
        Assert.Equal(M(expected), Rounder.RoundToSmallestFraction(M(input), money));
    }
}
```

Delete `tillpos/tests/TillPOS.Tests/UnitTest1.cs`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter RounderTests`
Expected: build FAIL — `The type or namespace name 'Money' does not exist in the namespace 'TillPOS.Core'`.

- [ ] **Step 3: Implement**

`tillpos/src/TillPOS.Core/Money/Rounder.cs`:

```csharp
namespace TillPOS.Core.Money;

public enum RoundingMethod { Bankers, Commercial }

public sealed record MoneySettings(
    int Precision = 2,
    RoundingMethod Rounding = RoundingMethod.Bankers,
    decimal SmallestCurrencyFraction = 0m,
    bool DisableRoundedTotal = false);

/// <summary>Rounding that reproduces Frappe's flt()/rounded() and ERPNext's
/// round_based_on_smallest_currency_fraction, so till totals equal ERPNext totals.</summary>
public static class Rounder
{
    public static decimal Round(decimal value, int precision, RoundingMethod method) =>
        Math.Round(value, precision,
            method == RoundingMethod.Bankers ? MidpointRounding.ToEven : MidpointRounding.AwayFromZero);

    public static decimal Round(decimal value, MoneySettings money) =>
        Round(value, money.Precision, money.Rounding);

    public static decimal RoundToSmallestFraction(decimal value, MoneySettings money)
    {
        var fraction = money.SmallestCurrencyFraction;
        if (fraction > 0)
        {
            var remainder = PythonRemainder(value, fraction, money.Precision, money.Rounding);
            value = remainder > fraction / 2 ? value + (fraction - remainder) : value - remainder;
        }
        else
        {
            value = Round(value, 0, money.Rounding);
        }
        return Round(value, money);
    }

    // Python's % takes the sign of the divisor, so for a positive fraction the
    // remainder is never negative (-10.13 % 0.25 == 0.12). ERPNext relies on that.
    private static decimal PythonRemainder(decimal numerator, decimal denominator, int precision, RoundingMethod method)
    {
        var r = numerator % denominator;
        if (r != 0 && (r < 0) != (denominator < 0)) r += denominator;
        return Round(r, precision, method);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter RounderTests`
Expected: PASS (14 tests).

- [ ] **Step 5: Commit**

```powershell
git add tillpos
git commit -m "feat(core): Frappe-compatible money rounding"
```

---

### Task 3: Catalog models, ICatalog and the in-memory test catalog

**Files:**
- Create: `tillpos/src/TillPOS.Core/Catalog/CatalogModels.cs`
- Create: `tillpos/src/TillPOS.Core/Catalog/ICatalog.cs`
- Create: `tillpos/src/TillPOS.Core/Catalog/PosSettings.cs`
- Create: `tillpos/src/TillPOS.Core/Pricing/PricingRule.cs`
- Create: `tillpos/tests/TillPOS.Tests/Fakes/InMemoryCatalog.cs`
- Test: `tillpos/tests/TillPOS.Tests/Core/InMemoryCatalogTests.cs`

**Interfaces:**
- Produces (namespace `TillPOS.Core.Catalog` unless noted):
  - `record Item(string ItemCode, string ItemName, string ItemGroup, string? Brand, string StockUom, bool Disabled, bool IsSalesItem)`
  - `record ItemBarcode(string Barcode, string ItemCode, string? Uom)`
  - `record ItemUom(string ItemCode, string Uom, decimal ConversionFactor)`
  - `record ItemPrice(string Name, string ItemCode, string? Uom, decimal PriceListRate, DateOnly? ValidFrom, DateOnly? ValidUpto)`
  - `record ItemGroupNode(string Name, string? Parent, int Lft, int Rgt)`
  - `record ItemTaxAssignment(string ItemTaxTemplate, string? TaxCategory, DateOnly? ValidFrom, int Idx)`
  - `record ItemTaxTemplate(string Name, IReadOnlyDictionary<string, decimal> RatesByAccount)`
  - `record TaxRow(int Idx, string AccountHead, string Description, decimal Rate, bool IncludedInPrintRate)`
  - `record SalesTaxTemplate(string Name, IReadOnlyList<TaxRow> Rows, string? UnsupportedReason)`
  - `record PaymentMode(string ModeOfPayment, bool IsDefault)`
  - `record PosSettings(string PosProfile, string Company, string CompanyName, string? TaxId, string? AddressText, string Currency, string Warehouse, string PriceList, string Customer, string? TaxesAndCharges, string? TaxCategory, bool DisableRoundedTotal, decimal SmallestCurrencyFraction, decimal WriteOffLimit, IReadOnlyList<PaymentMode> PaymentModes)`
  - `interface ICatalog` (members below)
  - namespace `TillPOS.Core.Pricing`: `enum RuleApplyOn { ItemCode, ItemGroup, Brand }`, `enum RuleKind { DiscountPercentage, DiscountAmount, Rate }`, `record PricingRule(string Name, RuleApplyOn ApplyOn, IReadOnlyList<string> Targets, RuleKind Kind, decimal Value, int Priority, DateOnly? ValidFrom, DateOnly? ValidUpto, string? ForPriceList, string? Warehouse, string? UnsupportedReason)`, `record AppliedRule(string RuleName, RuleKind Kind, decimal Value)` with `string Label`
  - Test fake `InMemoryCatalog : ICatalog` and `InMemoryCatalog.WithStandardGroups()`

- [ ] **Step 1: Write the failing test**

`tillpos/tests/TillPOS.Tests/Core/InMemoryCatalogTests.cs`:

```csharp
using TillPOS.Core.Catalog;
using TillPOS.Core.Pricing;
using TillPOS.Tests.Fakes;

namespace TillPOS.Tests.Core;

public class InMemoryCatalogTests
{
    [Fact]
    public void Stock_uom_conversion_factor_is_one_and_other_uoms_come_from_item_uoms()
    {
        var c = InMemoryCatalog.WithStandardGroups();
        c.Items.Add(new Item("WATER", "Water 500ml", "Food", null, "Nos", false, true));
        c.Uoms.Add(new ItemUom("WATER", "Box", 12m));

        Assert.Equal(1m, c.ConversionFactor("WATER", "Nos"));
        Assert.Equal(12m, c.ConversionFactor("WATER", "Box"));
        Assert.Null(c.ConversionFactor("WATER", "Pallet"));
    }

    [Theory]
    [InlineData(RuleKind.DiscountPercentage, "10", "10% OFF")]
    [InlineData(RuleKind.DiscountPercentage, "12.5", "12.5% OFF")]
    [InlineData(RuleKind.DiscountAmount, "2", "2.00 OFF")]
    [InlineData(RuleKind.Rate, "9.99", "OFFER")]
    public void Applied_rule_label(RuleKind kind, string value, string expected) =>
        Assert.Equal(expected, new AppliedRule("R1", kind, TestUtil.M(value)).Label);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter InMemoryCatalogTests`
Expected: build FAIL — `The type or namespace name 'Catalog' does not exist`.

- [ ] **Step 3: Implement the models**

`tillpos/src/TillPOS.Core/Catalog/CatalogModels.cs`:

```csharp
namespace TillPOS.Core.Catalog;

public sealed record Item(string ItemCode, string ItemName, string ItemGroup, string? Brand, string StockUom, bool Disabled, bool IsSalesItem);

public sealed record ItemBarcode(string Barcode, string ItemCode, string? Uom);

public sealed record ItemUom(string ItemCode, string Uom, decimal ConversionFactor);

/// <summary>An Item Price row of the Retail list. A null/blank Uom means the item's stock UOM.</summary>
public sealed record ItemPrice(string Name, string ItemCode, string? Uom, decimal PriceListRate, DateOnly? ValidFrom, DateOnly? ValidUpto);

/// <summary>Item Group tree node with nested-set bounds (a group contains every node whose lft/rgt lie inside its own).</summary>
public sealed record ItemGroupNode(string Name, string? Parent, int Lft, int Rgt);

/// <summary>A row of an Item's or Item Group's "Taxes" table.</summary>
public sealed record ItemTaxAssignment(string ItemTaxTemplate, string? TaxCategory, DateOnly? ValidFrom, int Idx);

/// <summary>Item Tax Template: overrides the tax rate per tax account.</summary>
public sealed record ItemTaxTemplate(string Name, IReadOnlyDictionary<string, decimal> RatesByAccount);

public sealed record TaxRow(int Idx, string AccountHead, string Description, decimal Rate, bool IncludedInPrintRate);

/// <summary>Sales Taxes and Charges Template. UnsupportedReason is set when a row is not "On Net Total".</summary>
public sealed record SalesTaxTemplate(string Name, IReadOnlyList<TaxRow> Rows, string? UnsupportedReason);
```

`tillpos/src/TillPOS.Core/Catalog/PosSettings.cs`:

```csharp
namespace TillPOS.Core.Catalog;

public sealed record PaymentMode(string ModeOfPayment, bool IsDefault);

/// <summary>Values synced from this till's POS Profile, its Company and Currency.</summary>
public sealed record PosSettings(
    string PosProfile,
    string Company,
    string CompanyName,
    string? TaxId,
    string? AddressText,
    string Currency,
    string Warehouse,
    string PriceList,
    string Customer,
    string? TaxesAndCharges,
    string? TaxCategory,
    bool DisableRoundedTotal,
    decimal SmallestCurrencyFraction,
    decimal WriteOffLimit,
    IReadOnlyList<PaymentMode> PaymentModes);
```

`tillpos/src/TillPOS.Core/Pricing/PricingRule.cs`:

```csharp
using System.Globalization;

namespace TillPOS.Core.Pricing;

public enum RuleApplyOn { ItemCode, ItemGroup, Brand }

public enum RuleKind { DiscountPercentage, DiscountAmount, Rate }

/// <summary>An ERPNext selling Pricing Rule reduced to what the till supports.
/// UnsupportedReason != null means the rule is stored but never applied.</summary>
public sealed record PricingRule(
    string Name,
    RuleApplyOn ApplyOn,
    IReadOnlyList<string> Targets,
    RuleKind Kind,
    decimal Value,
    int Priority,
    DateOnly? ValidFrom,
    DateOnly? ValidUpto,
    string? ForPriceList,
    string? Warehouse,
    string? UnsupportedReason);

public sealed record AppliedRule(string RuleName, RuleKind Kind, decimal Value)
{
    public string Label => Kind switch
    {
        RuleKind.DiscountPercentage => Value.ToString("0.##", CultureInfo.InvariantCulture) + "% OFF",
        RuleKind.DiscountAmount => Value.ToString("0.00", CultureInfo.InvariantCulture) + " OFF",
        _ => "OFFER",
    };
}
```

`tillpos/src/TillPOS.Core/Catalog/ICatalog.cs`:

```csharp
using TillPOS.Core.Pricing;

namespace TillPOS.Core.Catalog;

/// <summary>Read-only view of the local catalog used by pricing and the cart.</summary>
public interface ICatalog
{
    Item? FindItem(string itemCode);
    ItemBarcode? FindBarcode(string barcode);
    /// <summary>1 for the item's stock UOM; null when the UOM is not defined for the item.</summary>
    decimal? ConversionFactor(string itemCode, string uom);
    IReadOnlyList<ItemPrice> PricesFor(string itemCode);
    ItemGroupNode? FindGroup(string name);
    IReadOnlyList<PricingRule> PricingRules();
    IReadOnlyList<ItemTaxAssignment> ItemTaxes(string itemCode);
    IReadOnlyList<ItemTaxAssignment> ItemGroupTaxes(string itemGroup);
    ItemTaxTemplate? FindItemTaxTemplate(string name);
}
```

`tillpos/tests/TillPOS.Tests/Fakes/InMemoryCatalog.cs`:

```csharp
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
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter InMemoryCatalogTests`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```powershell
git add tillpos
git commit -m "feat(core): catalog models, ICatalog and pricing rule model"
```

---

### Task 4: Retail base price resolution

**Files:**
- Create: `tillpos/src/TillPOS.Core/Pricing/PriceResolver.cs`
- Test: `tillpos/tests/TillPOS.Tests/Core/PriceResolverTests.cs`

**Interfaces:**
- Consumes: `ICatalog`, `Item`, `ItemPrice` (Task 3).
- Produces: `sealed class PriceResolver(ICatalog catalog)` with `decimal? PriceListRate(Item item, string uom, decimal conversionFactor, DateOnly date)`.

- [ ] **Step 1: Write the failing tests**

`tillpos/tests/TillPOS.Tests/Core/PriceResolverTests.cs`:

```csharp
using TillPOS.Core.Catalog;
using TillPOS.Core.Pricing;
using TillPOS.Tests.Fakes;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Core;

public class PriceResolverTests
{
    private static readonly DateOnly Today = new(2026, 10, 5);
    private readonly InMemoryCatalog catalog = InMemoryCatalog.WithStandardGroups();
    private readonly Item rice = new("RICE5", "Basmati Rice 5kg", "Rice", null, "Nos", false, true);

    private decimal? Price(string uom = "Nos", decimal cf = 1m, DateOnly? date = null) =>
        new PriceResolver(catalog).PriceListRate(rice, uom, cf, date ?? Today);

    [Fact]
    public void Uses_price_valid_today()
    {
        catalog.Prices.Add(new ItemPrice("P1", "RICE5", "Nos", M("2150"), null, null));
        Assert.Equal(M("2150"), Price());
    }

    [Fact]
    public void Latest_valid_from_wins_once_it_starts()
    {
        catalog.Prices.Add(new ItemPrice("P1", "RICE5", "Nos", M("2150"), null, null));
        catalog.Prices.Add(new ItemPrice("P2", "RICE5", "Nos", M("2000"), new DateOnly(2026, 10, 10), null));
        Assert.Equal(M("2150"), Price());
        Assert.Equal(M("2000"), Price(date: new DateOnly(2026, 10, 12)));
    }

    [Fact]
    public void Expired_price_is_ignored()
    {
        catalog.Prices.Add(new ItemPrice("P1", "RICE5", "Nos", M("2150"), null, new DateOnly(2026, 10, 1)));
        Assert.Null(Price());
    }

    [Fact]
    public void Blank_uom_price_counts_as_stock_uom()
    {
        catalog.Prices.Add(new ItemPrice("P1", "RICE5", null, M("2150"), null, null));
        Assert.Equal(M("2150"), Price());
    }

    [Fact]
    public void Falls_back_to_stock_uom_price_times_conversion_factor()
    {
        catalog.Prices.Add(new ItemPrice("P1", "RICE5", "Nos", M("2.00"), null, null));
        Assert.Equal(M("24.00"), Price("Box", 12m));
    }

    [Fact]
    public void Uom_specific_price_beats_fallback()
    {
        catalog.Prices.Add(new ItemPrice("P1", "RICE5", "Nos", M("2.00"), null, null));
        catalog.Prices.Add(new ItemPrice("P2", "RICE5", "Box", M("20.00"), null, null));
        Assert.Equal(M("20.00"), Price("Box", 12m));
    }

    [Fact]
    public void No_price_returns_null() => Assert.Null(Price());
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter PriceResolverTests`
Expected: build FAIL — `The type or namespace name 'PriceResolver' could not be found`.

- [ ] **Step 3: Implement**

`tillpos/src/TillPOS.Core/Pricing/PriceResolver.cs`:

```csharp
using TillPOS.Core.Catalog;

namespace TillPOS.Core.Pricing;

/// <summary>Finds the Retail price list rate for an item in a UOM on a date, the way ERPNext does:
/// a price in that UOM if one exists, else the stock-UOM price times the conversion factor.</summary>
public sealed class PriceResolver(ICatalog catalog)
{
    public decimal? PriceListRate(Item item, string uom, decimal conversionFactor, DateOnly date)
    {
        var valid = catalog.PricesFor(item.ItemCode)
            .Where(p => (p.ValidFrom is null || p.ValidFrom <= date) && (p.ValidUpto is null || p.ValidUpto >= date))
            .ToList();

        var exact = Latest(valid.Where(p => UomOf(p, item) == uom));
        if (exact is not null) return exact.PriceListRate;
        if (uom == item.StockUom) return null;

        var stock = Latest(valid.Where(p => UomOf(p, item) == item.StockUom));
        return stock is null ? null : stock.PriceListRate * conversionFactor;
    }

    private static string UomOf(ItemPrice p, Item item) => string.IsNullOrEmpty(p.Uom) ? item.StockUom : p.Uom;

    private static ItemPrice? Latest(IEnumerable<ItemPrice> rows) =>
        rows.OrderByDescending(p => p.ValidFrom ?? DateOnly.MinValue).FirstOrDefault();
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter PriceResolverTests`
Expected: PASS (7 tests).

- [ ] **Step 5: Commit**

```powershell
git add tillpos
git commit -m "feat(core): Retail price resolution with UOM fallback"
```

---

### Task 5: Pricing Rule selection and line rate

**Files:**
- Create: `tillpos/src/TillPOS.Core/Pricing/LineMath.cs`
- Create: `tillpos/src/TillPOS.Core/Pricing/PricingRuleSelector.cs`
- Test: `tillpos/tests/TillPOS.Tests/Core/LineMathTests.cs`
- Test: `tillpos/tests/TillPOS.Tests/Core/PricingRuleSelectorTests.cs`

**Interfaces:**
- Consumes: `ICatalog`, `Item`, `ItemGroupNode`, `PricingRule`, `AppliedRule`, `MoneySettings`, `Rounder`.
- Produces:
  - `static decimal LineMath.RateAfterRule(decimal priceListRate, decimal conversionFactor, AppliedRule? rule, MoneySettings money)`
  - `sealed class PricingRuleSelector(ICatalog catalog, MoneySettings money, string priceList, string warehouse)` with `AppliedRule? Select(Item item, decimal priceListRate, decimal conversionFactor, DateOnly date)`

- [ ] **Step 1: Write the failing tests**

`tillpos/tests/TillPOS.Tests/Core/LineMathTests.cs`:

```csharp
using TillPOS.Core.Money;
using TillPOS.Core.Pricing;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Core;

public class LineMathTests
{
    private static readonly MoneySettings Money = new();

    [Theory]
    [InlineData("2150", "1", "DiscountPercentage", "10", "1935.00")]
    [InlineData("610", "1", "DiscountPercentage", "5", "579.50")]
    [InlineData("340", "1", "DiscountPercentage", "15", "289.00")]
    [InlineData("10.00", "1", "DiscountAmount", "2.50", "7.50")]
    [InlineData("24.00", "12", "DiscountAmount", "0.50", "18.00")]
    [InlineData("10.00", "1", "Rate", "8.99", "8.99")]
    [InlineData("24.00", "12", "Rate", "1.50", "18.00")]
    [InlineData("5.00", "1", "DiscountAmount", "7.00", "0.00")]
    public void Applies_rule(string plr, string cf, string kind, string value, string expected)
    {
        var rule = new AppliedRule("R", Enum.Parse<RuleKind>(kind), M(value));
        Assert.Equal(M(expected), LineMath.RateAfterRule(M(plr), M(cf), rule, Money));
    }

    [Fact]
    public void No_rule_keeps_price_list_rate() =>
        Assert.Equal(M("12.34"), LineMath.RateAfterRule(M("12.34"), 1m, null, Money));
}
```

`tillpos/tests/TillPOS.Tests/Core/PricingRuleSelectorTests.cs`:

```csharp
using TillPOS.Core.Catalog;
using TillPOS.Core.Money;
using TillPOS.Core.Pricing;
using TillPOS.Tests.Fakes;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Core;

public class PricingRuleSelectorTests
{
    private static readonly DateOnly Today = new(2026, 10, 5);
    private readonly InMemoryCatalog catalog = InMemoryCatalog.WithStandardGroups();
    private readonly Item rice = new("RICE5", "Basmati Rice 5kg", "Rice", "Tilda", "Nos", false, true);

    private static PricingRule Rule(string name, RuleApplyOn on, string target, string pct, int priority = 0,
        DateOnly? from = null, DateOnly? upto = null, string? priceList = null, string? warehouse = null, string? unsupported = null,
        RuleKind kind = RuleKind.DiscountPercentage) =>
        new(name, on, [target], kind, M(pct), priority, from, upto, priceList, warehouse, unsupported);

    private AppliedRule? Select(DateOnly? date = null) =>
        new PricingRuleSelector(catalog, new MoneySettings(), "Retail", "Stores - S").Select(rice, M("2150"), 1m, date ?? Today);

    [Fact]
    public void No_rules_means_no_offer() => Assert.Null(Select());

    [Fact]
    public void Item_code_rule_applies() 
    {
        catalog.Rules.Add(Rule("R-ITEM", RuleApplyOn.ItemCode, "RICE5", "10"));
        Assert.Equal("R-ITEM", Select()!.RuleName);
    }

    [Fact]
    public void Brand_rule_applies()
    {
        catalog.Rules.Add(Rule("R-BRAND", RuleApplyOn.Brand, "Tilda", "7"));
        Assert.Equal("R-BRAND", Select()!.RuleName);
    }

    [Fact]
    public void Parent_group_rule_applies_to_child_group_items()
    {
        catalog.Rules.Add(Rule("R-FOOD", RuleApplyOn.ItemGroup, "Food", "5"));
        Assert.Equal("R-FOOD", Select()!.RuleName);
    }

    [Fact]
    public void Sibling_group_rule_does_not_apply()
    {
        catalog.Rules.Add(Rule("R-DAIRY", RuleApplyOn.ItemGroup, "Dairy", "5"));
        Assert.Null(Select());
    }

    [Fact]
    public void Item_code_beats_brand_beats_group_at_equal_priority()
    {
        catalog.Rules.Add(Rule("R-FOOD", RuleApplyOn.ItemGroup, "Food", "30"));
        catalog.Rules.Add(Rule("R-BRAND", RuleApplyOn.Brand, "Tilda", "20"));
        Assert.Equal("R-BRAND", Select()!.RuleName);
        catalog.Rules.Add(Rule("R-ITEM", RuleApplyOn.ItemCode, "RICE5", "10"));
        Assert.Equal("R-ITEM", Select()!.RuleName);
    }

    [Fact]
    public void Higher_priority_beats_more_specific()
    {
        catalog.Rules.Add(Rule("R-ITEM", RuleApplyOn.ItemCode, "RICE5", "10", priority: 1));
        catalog.Rules.Add(Rule("R-FOOD", RuleApplyOn.ItemGroup, "Food", "5", priority: 5));
        Assert.Equal("R-FOOD", Select()!.RuleName);
    }

    [Fact]
    public void Tie_goes_to_the_bigger_saving_for_the_customer()
    {
        catalog.Rules.Add(Rule("R-PCT", RuleApplyOn.ItemCode, "RICE5", "10"));                         // saves 215
        catalog.Rules.Add(Rule("R-AMT", RuleApplyOn.ItemCode, "RICE5", "300", kind: RuleKind.DiscountAmount)); // saves 300
        Assert.Equal("R-AMT", Select()!.RuleName);
    }

    [Fact]
    public void Rules_outside_their_dates_are_ignored()
    {
        catalog.Rules.Add(Rule("R-OLD", RuleApplyOn.ItemCode, "RICE5", "10", upto: new DateOnly(2026, 10, 4)));
        catalog.Rules.Add(Rule("R-NEW", RuleApplyOn.ItemCode, "RICE5", "10", from: new DateOnly(2026, 10, 6)));
        Assert.Null(Select());
        Assert.Equal("R-NEW", Select(new DateOnly(2026, 10, 6))!.RuleName);
    }

    [Fact]
    public void Rules_for_other_price_lists_or_warehouses_are_ignored()
    {
        catalog.Rules.Add(Rule("R-WHOLESALE", RuleApplyOn.ItemCode, "RICE5", "10", priceList: "Wholesale"));
        catalog.Rules.Add(Rule("R-OTHER-WH", RuleApplyOn.ItemCode, "RICE5", "10", warehouse: "Branch - S"));
        Assert.Null(Select());
    }

    [Fact]
    public void Unsupported_rules_are_never_applied()
    {
        catalog.Rules.Add(Rule("R-QTY", RuleApplyOn.ItemCode, "RICE5", "50", unsupported: "quantity conditions are not supported"));
        Assert.Null(Select());
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "LineMathTests|PricingRuleSelectorTests"`
Expected: build FAIL — `LineMath` / `PricingRuleSelector` not found.

- [ ] **Step 3: Implement**

`tillpos/src/TillPOS.Core/Pricing/LineMath.cs`:

```csharp
using TillPOS.Core.Money;

namespace TillPOS.Core.Pricing;

public static class LineMath
{
    /// <summary>Unit rate after an offer, rounded to currency precision and never negative.
    /// Amount and Rate rules are defined per stock unit, so they scale with the conversion factor.</summary>
    public static decimal RateAfterRule(decimal priceListRate, decimal conversionFactor, AppliedRule? rule, MoneySettings money)
    {
        if (rule is null) return priceListRate;
        var rate = rule.Kind switch
        {
            RuleKind.DiscountPercentage => priceListRate * (1m - rule.Value / 100m),
            RuleKind.DiscountAmount => priceListRate - rule.Value * conversionFactor,
            RuleKind.Rate => rule.Value * conversionFactor,
            _ => priceListRate,
        };
        return Math.Max(0m, Rounder.Round(rate, money));
    }
}
```

`tillpos/src/TillPOS.Core/Pricing/PricingRuleSelector.cs`:

```csharp
using TillPOS.Core.Catalog;
using TillPOS.Core.Money;

namespace TillPOS.Core.Pricing;

/// <summary>Chooses the single Pricing Rule for an item: highest priority, then most specific
/// (item code &gt; brand &gt; item group), then the biggest saving for the customer.</summary>
public sealed class PricingRuleSelector(ICatalog catalog, MoneySettings money, string priceList, string warehouse)
{
    public AppliedRule? Select(Item item, decimal priceListRate, decimal conversionFactor, DateOnly date)
    {
        var itemGroup = catalog.FindGroup(item.ItemGroup);
        var best = catalog.PricingRules()
            .Where(r => r.UnsupportedReason is null)
            .Where(r => (r.ValidFrom is null || r.ValidFrom <= date) && (r.ValidUpto is null || r.ValidUpto >= date))
            .Where(r => string.IsNullOrEmpty(r.ForPriceList) || r.ForPriceList == priceList)
            .Where(r => string.IsNullOrEmpty(r.Warehouse) || r.Warehouse == warehouse)
            .Select(r => (Rule: r, Specificity: Specificity(r, item, itemGroup)))
            .Where(x => x.Specificity > 0)
            .OrderByDescending(x => x.Rule.Priority)
            .ThenByDescending(x => x.Specificity)
            .ThenByDescending(x => priceListRate - LineMath.RateAfterRule(priceListRate, conversionFactor, ToApplied(x.Rule), money))
            .Select(x => x.Rule)
            .FirstOrDefault();
        return best is null ? null : ToApplied(best);
    }

    private static AppliedRule ToApplied(PricingRule r) => new(r.Name, r.Kind, r.Value);

    // 3 = item code, 2 = brand, 1 = item group (incl. parent groups), 0 = no match
    private int Specificity(PricingRule rule, Item item, ItemGroupNode? itemGroup) => rule.ApplyOn switch
    {
        RuleApplyOn.ItemCode => rule.Targets.Contains(item.ItemCode) ? 3 : 0,
        RuleApplyOn.Brand => item.Brand is not null && rule.Targets.Contains(item.Brand) ? 2 : 0,
        RuleApplyOn.ItemGroup => itemGroup is not null && rule.Targets.Any(t => Contains(catalog.FindGroup(t), itemGroup)) ? 1 : 0,
        _ => 0,
    };

    private static bool Contains(ItemGroupNode? ancestor, ItemGroupNode node) =>
        ancestor is not null && ancestor.Lft <= node.Lft && node.Rgt <= ancestor.Rgt;
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "LineMathTests|PricingRuleSelectorTests"`
Expected: PASS (20 tests).

- [ ] **Step 5: Commit**

```powershell
git add tillpos
git commit -m "feat(core): pricing rule selection and offer rate"
```

---

### Task 6: VAT and bill totals (port of ERPNext taxes_and_totals) + item tax templates

**Files:**
- Create: `tillpos/src/TillPOS.Core/Tax/TaxModels.cs`
- Create: `tillpos/src/TillPOS.Core/Tax/TaxCalculator.cs`
- Create: `tillpos/src/TillPOS.Core/Tax/ItemTaxResolver.cs`
- Test: `tillpos/tests/TillPOS.Tests/Core/TaxCalculatorTests.cs`
- Test: `tillpos/tests/TillPOS.Tests/Core/ItemTaxResolverTests.cs`

**Interfaces:**
- Consumes: `MoneySettings`, `Rounder`, `SalesTaxTemplate`, `TaxRow`, `ItemTaxTemplate`, `ICatalog`.
- Produces:
  - `record TaxLineInput(decimal Qty, decimal Rate, string? ItemTaxTemplate)`
  - `record TaxLineResult(decimal Amount, decimal NetRate, decimal NetAmount)`
  - `record TaxRowResult(string AccountHead, string Description, decimal Rate, bool IncludedInPrintRate, decimal TaxAmount, decimal Total)`
  - `record BillTotals(decimal Total, decimal NetTotal, decimal TotalTaxes, decimal GrandTotal, decimal RoundingAdjustment, decimal RoundedTotal, bool RoundedTotalDisabled, IReadOnlyList<TaxLineResult> Lines, IReadOnlyList<TaxRowResult> Taxes)` with `decimal AmountDue`
  - `class UnsupportedTaxSetupException(string reason) : Exception`
  - `sealed class TaxCalculator(MoneySettings money, Func<string, ItemTaxTemplate?> findItemTaxTemplate)` with `BillTotals Calculate(IReadOnlyList<TaxLineInput> lines, SalesTaxTemplate? template)`
  - `sealed class ItemTaxResolver(ICatalog catalog, string? taxCategory)` with `string? TemplateFor(Item item, DateOnly date)`

- [ ] **Step 1: Write the failing tests**

`tillpos/tests/TillPOS.Tests/Core/TaxCalculatorTests.cs`:

```csharp
using TillPOS.Core.Catalog;
using TillPOS.Core.Money;
using TillPOS.Core.Tax;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Core;

public class TaxCalculatorTests
{
    private const string VatAccount = "VAT 5% - S";
    private static readonly SalesTaxTemplate Inclusive = new("UAE VAT 5%", [new TaxRow(1, VatAccount, "VAT 5%", 5m, true)], null);
    private static readonly SalesTaxTemplate Exclusive = new("UAE VAT 5% Excl", [new TaxRow(1, VatAccount, "VAT 5%", 5m, false)], null);
    private static readonly ItemTaxTemplate ZeroRated = new("Zero Rated", new Dictionary<string, decimal> { [VatAccount] = 0m });

    private static BillTotals Calc(SalesTaxTemplate? template, MoneySettings? money, params (string qty, string rate, string? itt)[] lines) =>
        new TaxCalculator(money ?? new MoneySettings(), name => name == ZeroRated.Name ? ZeroRated : null)
            .Calculate(lines.Select(l => new TaxLineInput(M(l.qty), M(l.rate), l.itt)).ToList(), template);

    [Fact]
    public void Inclusive_vat_is_back_calculated_and_total_is_unchanged()
    {
        var t = Calc(Inclusive, null, ("1", "105", null));
        Assert.Equal(M("105.00"), t.GrandTotal);
        Assert.Equal(M("100.00"), t.NetTotal);
        Assert.Equal(M("5.00"), t.TotalTaxes);
        Assert.Equal(M("100.00"), t.Lines[0].NetAmount);
        Assert.Equal(M("5.00"), t.Taxes[0].TaxAmount);
    }

    [Fact]
    public void Mockup_basket_matches_hand_calculation()
    {
        var t = Calc(Inclusive, null,
            ("2", "290", null), ("1", "1935", null), ("1", "180", null), ("1", "420", null),
            ("2", "579.50", null), ("1", "395", null), ("1", "289", null), ("3", "160", null));
        Assert.Equal(M("5438.00"), t.Total);
        Assert.Equal(M("5179.05"), t.NetTotal);
        Assert.Equal(M("258.95"), t.TotalTaxes);
        Assert.Equal(M("5438.00"), t.GrandTotal);
        Assert.Equal(M("5438.00"), t.AmountDue);
    }

    [Fact]
    public void Inclusive_rounding_difference_is_absorbed_so_grand_total_equals_shelf_total()
    {
        // Each 0.10 line nets to 0.10 after rounding, VAT 0.015 rounds to 0.02, row total 0.32;
        // ERPNext's grand_total_diff (-0.02) brings the grand total back to 0.30.
        var t = Calc(Inclusive, null, ("1", "0.10", null), ("1", "0.10", null), ("1", "0.10", null));
        Assert.Equal(M("0.30"), t.NetTotal);
        Assert.Equal(M("0.02"), t.TotalTaxes);
        Assert.Equal(M("0.30"), t.GrandTotal);
    }

    [Fact]
    public void Exclusive_vat_is_added_on_top()
    {
        var t = Calc(Exclusive, null, ("1", "100", null));
        Assert.Equal(M("100.00"), t.NetTotal);
        Assert.Equal(M("5.00"), t.TotalTaxes);
        Assert.Equal(M("105.00"), t.GrandTotal);
    }

    [Fact]
    public void Item_tax_template_overrides_rate_for_its_line_only()
    {
        var t = Calc(Inclusive, null, ("1", "105", null), ("1", "50", ZeroRated.Name));
        Assert.Equal(M("150.00"), t.NetTotal);
        Assert.Equal(M("5.00"), t.TotalTaxes);
        Assert.Equal(M("155.00"), t.GrandTotal);
        Assert.Equal(M("50.00"), t.Lines[1].NetAmount);
    }

    [Fact]
    public void Rounded_total_to_whole_units_when_no_fraction_is_set()
    {
        var t = Calc(Exclusive, null, ("1", "99.40", null));
        Assert.Equal(M("104.37"), t.GrandTotal);
        Assert.Equal(M("104.00"), t.RoundedTotal);
        Assert.Equal(M("-0.37"), t.RoundingAdjustment);
        Assert.Equal(M("104.00"), t.AmountDue);
    }

    [Fact]
    public void Rounded_total_to_quarter_fraction()
    {
        var t = Calc(Exclusive, new MoneySettings(SmallestCurrencyFraction: M("0.25")), ("1", "99.40", null));
        Assert.Equal(M("104.25"), t.RoundedTotal);
        Assert.Equal(M("-0.12"), t.RoundingAdjustment);
    }

    [Fact]
    public void Disabled_rounded_total_means_amount_due_is_grand_total()
    {
        var t = Calc(Exclusive, new MoneySettings(DisableRoundedTotal: true), ("1", "99.40", null));
        Assert.Equal(0m, t.RoundedTotal);
        Assert.Equal(M("104.37"), t.AmountDue);
    }

    [Fact]
    public void No_template_means_no_tax()
    {
        var t = Calc(null, null, ("2", "10.50", null));
        Assert.Equal(M("21.00"), t.GrandTotal);
        Assert.Equal(0m, t.TotalTaxes);
        Assert.Empty(t.Taxes);
    }

    [Fact]
    public void Empty_bill_is_zero()
    {
        var t = Calc(Inclusive, null);
        Assert.Equal(0m, t.GrandTotal);
        Assert.Equal(0m, t.AmountDue);
    }

    [Fact]
    public void Unsupported_template_throws()
    {
        var bad = new SalesTaxTemplate("Bad", [], "tax charge type 'Actual' is not supported");
        var ex = Assert.Throws<UnsupportedTaxSetupException>(() => Calc(bad, null, ("1", "1", null)));
        Assert.Contains("Actual", ex.Message);
    }
}
```

`tillpos/tests/TillPOS.Tests/Core/ItemTaxResolverTests.cs`:

```csharp
using TillPOS.Core.Catalog;
using TillPOS.Core.Tax;
using TillPOS.Tests.Fakes;

namespace TillPOS.Tests.Core;

public class ItemTaxResolverTests
{
    private static readonly DateOnly Today = new(2026, 10, 5);
    private readonly InMemoryCatalog catalog = InMemoryCatalog.WithStandardGroups();
    private readonly Item rice = new("RICE5", "Basmati Rice 5kg", "Rice", null, "Nos", false, true);

    private string? Resolve(string? category = null) => new ItemTaxResolver(catalog, category).TemplateFor(rice, Today);

    [Fact]
    public void No_rows_means_no_item_template() => Assert.Null(Resolve());

    [Fact]
    public void Item_row_beats_group_row()
    {
        catalog.GroupTaxRows["Rice"] = [new ItemTaxAssignment("Group T", null, null, 1)];
        catalog.ItemTaxRows["RICE5"] = [new ItemTaxAssignment("Item T", null, null, 1)];
        Assert.Equal("Item T", Resolve());
    }

    [Fact]
    public void Parent_group_row_is_inherited()
    {
        catalog.GroupTaxRows["Food"] = [new ItemTaxAssignment("Food T", null, null, 1)];
        Assert.Equal("Food T", Resolve());
    }

    [Fact]
    public void Rows_for_another_tax_category_are_skipped()
    {
        catalog.ItemTaxRows["RICE5"] = [new ItemTaxAssignment("Export T", "Export", null, 1)];
        Assert.Null(Resolve());
        Assert.Equal("Export T", Resolve("Export"));
    }

    [Fact]
    public void Future_rows_are_skipped_and_latest_started_row_wins()
    {
        catalog.ItemTaxRows["RICE5"] =
        [
            new ItemTaxAssignment("Old T", null, new DateOnly(2025, 1, 1), 1),
            new ItemTaxAssignment("Current T", null, new DateOnly(2026, 1, 1), 2),
            new ItemTaxAssignment("Future T", null, new DateOnly(2027, 1, 1), 3),
        ];
        Assert.Equal("Current T", Resolve());
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "TaxCalculatorTests|ItemTaxResolverTests"`
Expected: build FAIL — namespace `TillPOS.Core.Tax` not found.

- [ ] **Step 3: Implement**

`tillpos/src/TillPOS.Core/Tax/TaxModels.cs`:

```csharp
namespace TillPOS.Core.Tax;

public sealed record TaxLineInput(decimal Qty, decimal Rate, string? ItemTaxTemplate);

public sealed record TaxLineResult(decimal Amount, decimal NetRate, decimal NetAmount);

public sealed record TaxRowResult(string AccountHead, string Description, decimal Rate, bool IncludedInPrintRate, decimal TaxAmount, decimal Total);

public sealed record BillTotals(
    decimal Total,
    decimal NetTotal,
    decimal TotalTaxes,
    decimal GrandTotal,
    decimal RoundingAdjustment,
    decimal RoundedTotal,
    bool RoundedTotalDisabled,
    IReadOnlyList<TaxLineResult> Lines,
    IReadOnlyList<TaxRowResult> Taxes)
{
    /// <summary>What the customer pays: ERPNext's rounded total, or the grand total when rounding is disabled.</summary>
    public decimal AmountDue => RoundedTotalDisabled ? GrandTotal : RoundedTotal;
}

public sealed class UnsupportedTaxSetupException(string reason)
    : Exception($"Tax setup not supported — contact admin ({reason}).");
```

`tillpos/src/TillPOS.Core/Tax/TaxCalculator.cs`:

```csharp
using TillPOS.Core.Catalog;
using TillPOS.Core.Money;

namespace TillPOS.Core.Tax;

/// <summary>Port of erpnext/controllers/taxes_and_totals.py for "On Net Total" rows:
/// determine_exclusive_rate → calculate_net_total → calculate_taxes →
/// adjust_grand_total_for_inclusive_tax → calculate_totals → set_rounded_total.</summary>
public sealed class TaxCalculator(MoneySettings money, Func<string, ItemTaxTemplate?> findItemTaxTemplate)
{
    private static readonly IReadOnlyDictionary<string, decimal> NoOverrides = new Dictionary<string, decimal>();

    public BillTotals Calculate(IReadOnlyList<TaxLineInput> lines, SalesTaxTemplate? template)
    {
        if (template?.UnsupportedReason is { } reason) throw new UnsupportedTaxSetupException(reason);

        var rows = template?.Rows.OrderBy(r => r.Idx).ToList() ?? [];
        var hasInclusive = rows.Any(r => r.IncludedInPrintRate);
        var overrides = lines
            .Select(l => l.ItemTaxTemplate is null ? NoOverrides : findItemTaxTemplate(l.ItemTaxTemplate)?.RatesByAccount ?? NoOverrides)
            .ToList();

        var n = lines.Count;
        var amount = new decimal[n];
        var netAmount = new decimal[n];
        var netRate = new decimal[n];
        for (var i = 0; i < n; i++)
        {
            amount[i] = R(lines[i].Rate * lines[i].Qty);
            netAmount[i] = amount[i];
            netRate[i] = lines[i].Rate;
        }

        // determine_exclusive_rate
        if (hasInclusive)
        {
            for (var i = 0; i < n; i++)
            {
                if (lines[i].Qty == 0) continue;
                var fraction = rows.Where(r => r.IncludedInPrintRate).Sum(r => RateFor(r, overrides[i]) / 100m);
                if (fraction == 0) continue;
                netAmount[i] = R(amount[i] / (1m + fraction));
                netRate[i] = R(netAmount[i] / lines[i].Qty);
            }
        }

        var total = R(amount.Sum());
        var netTotal = R(netAmount.Sum());

        // calculate_taxes: accumulate unrounded per item, round once at the end
        var taxAmount = new decimal[rows.Count];
        for (var i = 0; i < n; i++)
            for (var t = 0; t < rows.Count; t++)
                taxAmount[t] += RateFor(rows[t], overrides[i]) / 100m * netAmount[i];

        var rowTotal = new decimal[rows.Count];
        for (var t = 0; t < rows.Count; t++)
        {
            taxAmount[t] = R(taxAmount[t]);
            rowTotal[t] = R((t == 0 ? netTotal : rowTotal[t - 1]) + taxAmount[t]);
        }

        // adjust_grand_total_for_inclusive_tax
        var grandTotalDiff = 0m;
        if (hasInclusive)
        {
            var nonInclusive = Enumerable.Range(0, rows.Count).Where(t => !rows[t].IncludedInPrintRate).Sum(t => taxAmount[t]);
            var diff = R(total + nonInclusive - rowTotal[^1]);
            var limit = 5m / Pow10(money.Precision);
            if (diff != 0 && Math.Abs(diff) <= limit) grandTotalDiff = diff;
        }

        // calculate_totals
        var grandTotal = rows.Count > 0 ? R(rowTotal[^1] + grandTotalDiff) : netTotal;
        var totalTaxes = rows.Count > 0 ? R(grandTotal - netTotal - grandTotalDiff) : 0m;

        // set_rounded_total
        decimal roundedTotal = 0m, roundingAdjustment = 0m;
        if (!money.DisableRoundedTotal)
        {
            roundedTotal = Rounder.RoundToSmallestFraction(grandTotal, money);
            roundingAdjustment = R(roundedTotal - grandTotal);
        }

        return new BillTotals(
            total, netTotal, totalTaxes, grandTotal, roundingAdjustment, roundedTotal, money.DisableRoundedTotal,
            Enumerable.Range(0, n).Select(i => new TaxLineResult(amount[i], netRate[i], netAmount[i])).ToList(),
            Enumerable.Range(0, rows.Count).Select(t => new TaxRowResult(
                rows[t].AccountHead, rows[t].Description, rows[t].Rate, rows[t].IncludedInPrintRate, taxAmount[t], rowTotal[t])).ToList());
    }

    private decimal R(decimal value) => Rounder.Round(value, money);

    private static decimal RateFor(TaxRow row, IReadOnlyDictionary<string, decimal> overrides) =>
        overrides.TryGetValue(row.AccountHead, out var rate) ? rate : row.Rate;

    private static decimal Pow10(int p)
    {
        var result = 1m;
        for (var i = 0; i < p; i++) result *= 10m;
        return result;
    }
}
```

`tillpos/src/TillPOS.Core/Tax/ItemTaxResolver.cs`:

```csharp
using TillPOS.Core.Catalog;

namespace TillPOS.Core.Tax;

/// <summary>Picks the Item Tax Template for an item: the item's own Taxes rows first, then its
/// item group and each parent group. Rows must match the tax category (or have none) and have started.</summary>
public sealed class ItemTaxResolver(ICatalog catalog, string? taxCategory)
{
    public string? TemplateFor(Item item, DateOnly date)
    {
        var own = Pick(catalog.ItemTaxes(item.ItemCode), date);
        if (own is not null) return own;

        for (var group = catalog.FindGroup(item.ItemGroup); group is not null;
             group = group.Parent is null ? null : catalog.FindGroup(group.Parent))
        {
            var inherited = Pick(catalog.ItemGroupTaxes(group.Name), date);
            if (inherited is not null) return inherited;
        }
        return null;
    }

    private string? Pick(IReadOnlyList<ItemTaxAssignment> rows, DateOnly date) =>
        rows.Where(r => r.ValidFrom is null || r.ValidFrom <= date)
            .Where(r => string.IsNullOrEmpty(r.TaxCategory) || r.TaxCategory == taxCategory)
            .OrderByDescending(r => r.ValidFrom ?? DateOnly.MinValue)
            .ThenBy(r => r.Idx)
            .Select(r => r.ItemTaxTemplate)
            .FirstOrDefault();
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "TaxCalculatorTests|ItemTaxResolverTests"`
Expected: PASS (16 tests).

- [ ] **Step 5: Commit**

```powershell
git add tillpos
git commit -m "feat(core): UAE VAT and bill totals ported from ERPNext"
```

---

### Task 7: Cart (bill in progress)

**Files:**
- Create: `tillpos/src/TillPOS.Core/Sales/Cart.cs`
- Test: `tillpos/tests/TillPOS.Tests/Core/CartTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 2–6.
- Produces (namespace `TillPOS.Core.Sales`):
  - `record SaleContext(ICatalog Catalog, MoneySettings Money, string PriceList, string Warehouse, string? TaxCategory, SalesTaxTemplate? TaxTemplate, Func<DateOnly> Today)`
  - `sealed class CartLine` — `Guid Id`, `Item Item`, `string Uom`, `decimal ConversionFactor`, `decimal Qty`, `decimal PriceListRate`, `AppliedRule? Rule`, `decimal Rate`, `string? ItemTaxTemplate`
  - `enum AddOutcome { Added, UnknownBarcode, UnknownItem, ItemNotSellable, UnknownUom, NoPrice }`, `record AddResult(AddOutcome Outcome, CartLine? Line)`
  - `sealed class Cart(SaleContext ctx)` — `IReadOnlyList<CartLine> Lines`, `AddResult AddBarcode(string)`, `AddResult AddItem(string itemCode, string? uom = null)`, `void SetQty(Guid, decimal)`, `void Increment(Guid)`, `void Decrement(Guid)`, `void Remove(Guid)`, `void Clear()`, `BillTotals Totals()`, `decimal DiscountSaved()`

- [ ] **Step 1: Write the failing tests**

`tillpos/tests/TillPOS.Tests/Core/CartTests.cs`:

```csharp
using TillPOS.Core.Catalog;
using TillPOS.Core.Money;
using TillPOS.Core.Pricing;
using TillPOS.Core.Sales;
using TillPOS.Tests.Fakes;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Core;

public class CartTests
{
    private static readonly SalesTaxTemplate Vat = new("UAE VAT 5%", [new TaxRow(1, "VAT 5% - S", "VAT 5%", 5m, true)], null);
    private readonly InMemoryCatalog catalog = InMemoryCatalog.WithStandardGroups();

    private Cart NewCart() => new(new SaleContext(catalog, new MoneySettings(), "Retail", "Stores - S", null, Vat, () => new DateOnly(2026, 10, 5)));

    private void AddItem(string code, string name, string group, string price, string barcode, string? pctOff = null)
    {
        catalog.Items.Add(new Item(code, name, group, null, "Nos", false, true));
        catalog.Barcodes.Add(new ItemBarcode(barcode, code, null));
        catalog.Prices.Add(new ItemPrice("P-" + code, code, "Nos", M(price), null, null));
        if (pctOff is not null)
            catalog.Rules.Add(new PricingRule("R-" + code, RuleApplyOn.ItemCode, [code], RuleKind.DiscountPercentage, M(pctOff), 0, null, null, null, null, null));
    }

    [Fact]
    public void Scanning_a_known_barcode_adds_a_priced_line_with_offer()
    {
        AddItem("RICE5", "Basmati Rice 5kg", "Rice", "2150", "8901234500024", "10");
        var cart = NewCart();

        var result = cart.AddBarcode("8901234500024");

        Assert.Equal(AddOutcome.Added, result.Outcome);
        var line = Assert.Single(cart.Lines);
        Assert.Equal(M("2150"), line.PriceListRate);
        Assert.Equal(M("1935.00"), line.Rate);
        Assert.Equal("10% OFF", line.Rule!.Label);
    }

    [Fact]
    public void Scanning_the_same_item_twice_increases_quantity()
    {
        AddItem("MILK", "Full Cream Milk 1L", "Dairy", "290", "111");
        var cart = NewCart();
        cart.AddBarcode("111");
        cart.AddBarcode(" 111 ");
        Assert.Equal(2m, Assert.Single(cart.Lines).Qty);
    }

    [Fact]
    public void Unknown_barcode_adds_nothing()
    {
        var cart = NewCart();
        Assert.Equal(AddOutcome.UnknownBarcode, cart.AddBarcode("999").Outcome);
        Assert.Empty(cart.Lines);
    }

    [Fact]
    public void Disabled_item_cannot_be_sold()
    {
        catalog.Items.Add(new Item("OLD", "Old item", "Food", null, "Nos", true, true));
        catalog.Barcodes.Add(new ItemBarcode("222", "OLD", null));
        Assert.Equal(AddOutcome.ItemNotSellable, NewCart().AddBarcode("222").Outcome);
    }

    [Fact]
    public void Item_without_price_cannot_be_sold()
    {
        catalog.Items.Add(new Item("NOPRICE", "No price", "Food", null, "Nos", false, true));
        catalog.Barcodes.Add(new ItemBarcode("333", "NOPRICE", null));
        Assert.Equal(AddOutcome.NoPrice, NewCart().AddBarcode("333").Outcome);
    }

    [Fact]
    public void Carton_barcode_uses_its_uom_and_conversion_factor()
    {
        AddItem("WATER", "Water 500ml", "Food", "2.00", "444");
        catalog.Uoms.Add(new ItemUom("WATER", "Box", 12m));
        catalog.Barcodes.Add(new ItemBarcode("445", "WATER", "Box"));
        var cart = NewCart();

        cart.AddBarcode("445");

        var line = Assert.Single(cart.Lines);
        Assert.Equal("Box", line.Uom);
        Assert.Equal(12m, line.ConversionFactor);
        Assert.Equal(M("24.00"), line.Rate);
    }

    [Fact]
    public void Quantity_changes_and_removal()
    {
        AddItem("MILK", "Full Cream Milk 1L", "Dairy", "290", "111");
        var cart = NewCart();
        var id = cart.AddBarcode("111").Line!.Id;

        cart.Increment(id);
        Assert.Equal(2m, cart.Lines[0].Qty);
        cart.Decrement(id);
        cart.Decrement(id);
        Assert.Equal(1m, cart.Lines[0].Qty);
        cart.SetQty(id, 6m);
        Assert.Equal(6m, cart.Lines[0].Qty);
        Assert.Throws<ArgumentOutOfRangeException>(() => cart.SetQty(id, 0m));
        cart.Remove(id);
        Assert.Empty(cart.Lines);
    }

    [Fact]
    public void Mockup_basket_totals_and_savings()
    {
        AddItem("MILK", "Full Cream Milk 1L", "Dairy", "290", "1");
        AddItem("RICE5", "Basmati Rice 5kg", "Rice", "2150", "2", "10");
        AddItem("BREAD", "White Bread Large", "Food", "180", "3");
        AddItem("EGGS", "Eggs Tray (12)", "Food", "420", "4");
        AddItem("OIL", "Cooking Oil 1L Pouch", "Food", "610", "5", "5");
        AddItem("KETCHUP", "Tomato Ketchup 800g", "Food", "395", "6");
        AddItem("DISH", "Dishwash Liquid 500ml", "Household", "340", "7", "15");
        AddItem("SUGAR", "Sugar 1kg", "Food", "160", "8");
        var cart = NewCart();
        foreach (var b in new[] { "1", "1", "2", "3", "4", "5", "5", "6", "7", "8", "8", "8" }) cart.AddBarcode(b);

        var t = cart.Totals();

        Assert.Equal(8, cart.Lines.Count);
        Assert.Equal(M("5438.00"), t.GrandTotal);
        Assert.Equal(M("258.95"), t.TotalTaxes);
        Assert.Equal(M("327.00"), cart.DiscountSaved());
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter CartTests`
Expected: build FAIL — namespace `TillPOS.Core.Sales` not found.

- [ ] **Step 3: Implement**

`tillpos/src/TillPOS.Core/Sales/Cart.cs`:

```csharp
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
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter CartTests`
Expected: PASS (8 tests). Then run `dotnet test` — all tests pass.

- [ ] **Step 5: Commit**

```powershell
git add tillpos
git commit -m "feat(core): cart with scan, quantity, offers and totals"
```

---

### Task 8: SQLite catalog storage and name search

**Files:**
- Create: `tillpos/src/TillPOS.Data/SqlExt.cs`, `Migrations.cs`, `TillDb.cs`, `CatalogStore.cs`, `SqliteCatalog.cs`
- Create: `tillpos/tests/TillPOS.Tests/Data/TempDb.cs`
- Test: `tillpos/tests/TillPOS.Tests/Data/CatalogStoreTests.cs`

**Interfaces:**
- Consumes: Core catalog models, `PricingRule`, `PosSettings`, `ICatalog`.
- Produces (namespace `TillPOS.Data`):
  - `sealed class TillDb(string path)` — `SqliteConnection Open()`, `void Migrate()`
  - `record ItemSnapshot(Item Item, IReadOnlyList<ItemBarcode> Barcodes, IReadOnlyList<ItemUom> Uoms, IReadOnlyList<ItemTaxAssignment> Taxes)`
  - `record ItemGroupSnapshot(ItemGroupNode Node, IReadOnlyList<ItemTaxAssignment> Taxes)`
  - `sealed class CatalogStore(TillDb db)` — `UpsertItems(IEnumerable<ItemSnapshot>)`, `ReplaceItemGroups(IEnumerable<ItemGroupSnapshot>)`, `UpsertPrices(IEnumerable<ItemPrice>)`, `UpsertPricingRule(PricingRule)`, `DeletePricingRule(string)`, `UpsertItemTaxTemplate(ItemTaxTemplate)`, `DeleteItemTaxTemplate(string)`, `UpsertSalesTaxTemplate(SalesTaxTemplate)`, `DeleteSalesTaxTemplate(string)`, `DeleteDocument(string doctype, string name)`, `IReadOnlyList<string> AllItemCodes()`, `IReadOnlyList<string> AllPriceNames()`, `SavePosSettings(PosSettings)`, `PosSettings? LoadPosSettings()`, `string? GetValue(string key)`, `void SetValue(string key, string value)`
  - `sealed class SqliteCatalog : ICatalog` — ctor `(TillDb db)`, `void Reload()`, `SalesTaxTemplate? FindSalesTaxTemplate(string name)`, `IReadOnlyList<Item> Search(string text, int limit = 20)`

- [ ] **Step 1: Write the failing tests**

`tillpos/tests/TillPOS.Tests/Data/TempDb.cs`:

```csharp
using Microsoft.Data.Sqlite;
using TillPOS.Data;

namespace TillPOS.Tests.Data;

/// <summary>A migrated SQLite file in %TEMP%, deleted after the test.</summary>
public sealed class TempDb : IDisposable
{
    public TempDb()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tillpos-test-{Guid.NewGuid():N}.db");
        Db = new TillDb(Path);
        Db.Migrate();
    }

    public string Path { get; }
    public TillDb Db { get; }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { Path, Path + "-wal", Path + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }
}
```

`tillpos/tests/TillPOS.Tests/Data/CatalogStoreTests.cs`:

```csharp
using TillPOS.Core.Catalog;
using TillPOS.Core.Pricing;
using TillPOS.Data;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Data;

public sealed class CatalogStoreTests : IDisposable
{
    private readonly TempDb temp = new();
    private readonly CatalogStore store;

    public CatalogStoreTests() => store = new CatalogStore(temp.Db);

    public void Dispose() => temp.Dispose();

    private static ItemSnapshot Snap(string code, string name, params string[] barcodes) => new(
        new Item(code, name, "Food", null, "Nos", false, true),
        barcodes.Select(b => new ItemBarcode(b, code, null)).ToList(),
        [new ItemUom(code, "Nos", 1m)],
        []);

    [Fact]
    public void Migrate_is_idempotent()
    {
        temp.Db.Migrate();
        Assert.Empty(store.AllItemCodes());
    }

    [Fact]
    public void Item_with_barcodes_round_trips()
    {
        store.UpsertItems([Snap("RICE5", "Basmati Rice 5kg", "8901234500024")]);
        var catalog = new SqliteCatalog(temp.Db);

        Assert.Equal("RICE5", catalog.FindBarcode("8901234500024")!.ItemCode);
        Assert.Equal("Basmati Rice 5kg", catalog.FindItem("RICE5")!.ItemName);
        Assert.Equal(1m, catalog.ConversionFactor("RICE5", "Nos"));
    }

    [Fact]
    public void Updating_an_item_replaces_its_barcodes()
    {
        store.UpsertItems([Snap("RICE5", "Basmati Rice 5kg", "OLD")]);
        store.UpsertItems([Snap("RICE5", "Basmati Rice 5kg", "NEW")]);
        var catalog = new SqliteCatalog(temp.Db);

        Assert.Null(catalog.FindBarcode("OLD"));
        Assert.NotNull(catalog.FindBarcode("NEW"));
    }

    [Fact]
    public void Barcode_moved_to_another_item_points_to_new_item()
    {
        store.UpsertItems([Snap("A", "Item A", "123")]);
        store.UpsertItems([Snap("B", "Item B", "123")]);
        Assert.Equal("B", new SqliteCatalog(temp.Db).FindBarcode("123")!.ItemCode);
    }

    [Fact]
    public void Prices_keep_exact_decimals()
    {
        store.UpsertItems([Snap("OIL", "Cooking Oil", "5")]);
        store.UpsertPrices([new ItemPrice("P1", "OIL", "Nos", M("579.50"), new DateOnly(2026, 10, 1), null)]);
        var price = Assert.Single(new SqliteCatalog(temp.Db).PricesFor("OIL"));
        Assert.Equal(M("579.50"), price.PriceListRate);
        Assert.Equal(new DateOnly(2026, 10, 1), price.ValidFrom);
    }

    [Fact]
    public void Deleting_an_item_removes_barcodes_prices_and_search_entry()
    {
        store.UpsertItems([Snap("RICE5", "Basmati Rice 5kg", "1")]);
        store.UpsertPrices([new ItemPrice("P1", "RICE5", "Nos", 1m, null, null)]);
        store.DeleteDocument("Item", "RICE5");
        var catalog = new SqliteCatalog(temp.Db);

        Assert.Null(catalog.FindItem("RICE5"));
        Assert.Null(catalog.FindBarcode("1"));
        Assert.Empty(catalog.PricesFor("RICE5"));
        Assert.Empty(catalog.Search("basmati"));
    }

    [Fact]
    public void Search_matches_word_prefixes_including_arabic()
    {
        store.UpsertItems([Snap("RICE5", "Basmati Rice 5kg", "1"), Snap("RICE-AR", "أرز بسمتي", "2"), Snap("MILK", "Full Cream Milk", "3")]);
        var catalog = new SqliteCatalog(temp.Db);

        Assert.Equal("RICE5", Assert.Single(catalog.Search("basm ric")).ItemCode);
        Assert.Equal("RICE-AR", Assert.Single(catalog.Search("أرز")).ItemCode);
        Assert.Empty(catalog.Search("   "));
    }

    [Fact]
    public void Groups_rules_and_templates_round_trip()
    {
        store.ReplaceItemGroups([
            new ItemGroupSnapshot(new ItemGroupNode("All Item Groups", null, 1, 4), []),
            new ItemGroupSnapshot(new ItemGroupNode("Food", "All Item Groups", 2, 3), [new ItemTaxAssignment("Zero Rated", null, null, 1)]),
        ]);
        store.UpsertPricingRule(new PricingRule("R1", RuleApplyOn.ItemGroup, ["Food"], RuleKind.DiscountPercentage, 10m, 2, null, null, null, null, null));
        store.UpsertItemTaxTemplate(new ItemTaxTemplate("Zero Rated", new Dictionary<string, decimal> { ["VAT 5% - S"] = 0m }));
        store.UpsertSalesTaxTemplate(new SalesTaxTemplate("UAE VAT 5%", [new TaxRow(1, "VAT 5% - S", "VAT 5%", 5m, true)], null));
        var catalog = new SqliteCatalog(temp.Db);

        Assert.Equal(2, catalog.FindGroup("Food")!.Lft);
        Assert.Equal("Zero Rated", Assert.Single(catalog.ItemGroupTaxes("Food")).ItemTaxTemplate);
        Assert.Equal(new[] { "Food" }, Assert.Single(catalog.PricingRules()).Targets);
        Assert.Equal(0m, catalog.FindItemTaxTemplate("Zero Rated")!.RatesByAccount["VAT 5% - S"]);
        Assert.True(catalog.FindSalesTaxTemplate("UAE VAT 5%")!.Rows[0].IncludedInPrintRate);

        store.DeletePricingRule("R1");
        catalog.Reload();
        Assert.Empty(catalog.PricingRules());
    }

    [Fact]
    public void Pos_settings_and_values_round_trip()
    {
        var s = new PosSettings("Till 1", "Shop LLC", "Shop LLC", "100000000000003", "Al Quoz, Dubai", "AED", "Stores - S", "Retail",
            "Walk-in Customer", "UAE VAT 5%", null, false, 0m, M("0.05"), [new PaymentMode("Cash", true), new PaymentMode("Card", false)]);
        store.SavePosSettings(s);
        store.SetValue("k", "v");

        var loaded = store.LoadPosSettings()!;
        Assert.Equal(s with { PaymentModes = loaded.PaymentModes }, loaded);   // records compare lists by reference
        Assert.Equal(s.PaymentModes, loaded.PaymentModes);                      // so compare the list elements separately
        Assert.Equal("v", store.GetValue("k"));
        Assert.Null(store.GetValue("missing"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter CatalogStoreTests`
Expected: build FAIL — `TillDb` / `CatalogStore` not found.

- [ ] **Step 3: Implement**

`tillpos/src/TillPOS.Data/SqlExt.cs`:

```csharp
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TillPOS.Data;

internal static class SqlExt
{
    public static int Exec(this SqliteConnection c, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] ps)
    {
        using var cmd = Cmd(c, tx, sql, ps);
        return cmd.ExecuteNonQuery();
    }

    public static object? Scalar(this SqliteConnection c, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] ps)
    {
        using var cmd = Cmd(c, tx, sql, ps);
        return cmd.ExecuteScalar();
    }

    public static List<T> Query<T>(this SqliteConnection c, string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] ps)
    {
        using var cmd = Cmd(c, null, sql, ps);
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(map(r));
        return list;
    }

    public static string Dec(decimal d) => d.ToString(CultureInfo.InvariantCulture);
    public static decimal Dec(SqliteDataReader r, int i) => decimal.Parse(r.GetString(i), NumberStyles.Number, CultureInfo.InvariantCulture);
    public static string? Date(DateOnly? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public static DateOnly? Date(SqliteDataReader r, int i) =>
        r.IsDBNull(i) ? null : DateOnly.ParseExact(r.GetString(i), "yyyy-MM-dd", CultureInfo.InvariantCulture);
    public static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static SqliteCommand Cmd(SqliteConnection c, SqliteTransaction? tx, string sql, (string Name, object? Value)[] ps)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var (name, value) in ps) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }
}
```

`tillpos/src/TillPOS.Data/Migrations.cs`:

```csharp
namespace TillPOS.Data;

/// <summary>Schema versions; index i upgrades user_version i → i+1. Never edit a shipped entry — append.</summary>
internal static class Migrations
{
    public static readonly string[] All =
    [
        """
        CREATE TABLE item (
            id INTEGER PRIMARY KEY,
            item_code TEXT NOT NULL UNIQUE,
            item_name TEXT NOT NULL,
            item_group TEXT NOT NULL,
            brand TEXT,
            stock_uom TEXT NOT NULL,
            disabled INTEGER NOT NULL,
            is_sales_item INTEGER NOT NULL);
        CREATE VIRTUAL TABLE item_fts USING fts5(item_name, tokenize = 'unicode61 remove_diacritics 2');
        CREATE TABLE item_barcode (barcode TEXT PRIMARY KEY, item_code TEXT NOT NULL, uom TEXT);
        CREATE INDEX ix_item_barcode_item ON item_barcode(item_code);
        CREATE TABLE item_uom (item_code TEXT NOT NULL, uom TEXT NOT NULL, conversion_factor TEXT NOT NULL, PRIMARY KEY (item_code, uom));
        CREATE TABLE item_tax (
            parent_type TEXT NOT NULL, parent TEXT NOT NULL, idx INTEGER NOT NULL,
            item_tax_template TEXT NOT NULL, tax_category TEXT, valid_from TEXT,
            PRIMARY KEY (parent_type, parent, idx));
        CREATE TABLE item_price (
            name TEXT PRIMARY KEY, item_code TEXT NOT NULL, uom TEXT,
            price_list_rate TEXT NOT NULL, valid_from TEXT, valid_upto TEXT);
        CREATE INDEX ix_item_price_item ON item_price(item_code);
        CREATE TABLE item_group (name TEXT PRIMARY KEY, parent TEXT, lft INTEGER NOT NULL, rgt INTEGER NOT NULL);
        CREATE TABLE pricing_rule (name TEXT PRIMARY KEY, json TEXT NOT NULL);
        CREATE TABLE item_tax_template (name TEXT PRIMARY KEY, json TEXT NOT NULL);
        CREATE TABLE sales_tax_template (name TEXT PRIMARY KEY, json TEXT NOT NULL);
        CREATE TABLE kv (key TEXT PRIMARY KEY, value TEXT NOT NULL);
        """,
    ];
}
```

`tillpos/src/TillPOS.Data/TillDb.cs`:

```csharp
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TillPOS.Data;

public sealed class TillDb(string path)
{
    public string Path { get; } = path;

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = true }.ToString());
        c.Open();
        c.Exec(null, "PRAGMA journal_mode = WAL; PRAGMA busy_timeout = 5000;");
        return c;
    }

    public void Migrate()
    {
        using var c = Open();
        var version = Convert.ToInt32(c.Scalar(null, "PRAGMA user_version"), CultureInfo.InvariantCulture);
        for (var i = version; i < Migrations.All.Length; i++)
        {
            using var tx = c.BeginTransaction();
            c.Exec(tx, Migrations.All[i]);
            c.Exec(tx, $"PRAGMA user_version = {i + 1}");
            tx.Commit();
        }
    }
}
```

`tillpos/src/TillPOS.Data/CatalogStore.cs`:

```csharp
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
```

`tillpos/src/TillPOS.Data/SqliteCatalog.cs`:

```csharp
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
    private Dictionary<string, ItemGroupNode> groups = [];
    private Dictionary<string, List<ItemTaxAssignment>> groupTaxes = [];
    private List<PricingRule> rules = [];
    private Dictionary<string, ItemTaxTemplate> itemTaxTemplates = [];

    public SqliteCatalog(TillDb db)
    {
        this.db = db;
        Reload();
    }

    public void Reload()
    {
        using var c = db.Open();
        groups = c.Query("SELECT name, parent, lft, rgt FROM item_group",
            r => new ItemGroupNode(r.GetString(0), Str(r, 1), r.GetInt32(2), r.GetInt32(3))).ToDictionary(g => g.Name);
        groupTaxes = c.Query("SELECT parent, item_tax_template, tax_category, valid_from, idx FROM item_tax WHERE parent_type = 'Item Group'",
                r => (Parent: r.GetString(0), Row: ReadTax(r, 1)))
            .GroupBy(x => x.Parent).ToDictionary(g => g.Key, g => g.Select(x => x.Row).ToList());
        rules = c.Query("SELECT json FROM pricing_rule", r => JsonSerializer.Deserialize<PricingRule>(r.GetString(0))!);
        itemTaxTemplates = c.Query("SELECT json FROM item_tax_template", r => JsonSerializer.Deserialize<ItemTaxTemplate>(r.GetString(0))!)
            .ToDictionary(t => t.Name);
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

    public ItemGroupNode? FindGroup(string name) => groups.GetValueOrDefault(name);

    public IReadOnlyList<PricingRule> PricingRules() => rules;

    public IReadOnlyList<ItemTaxAssignment> ItemTaxes(string itemCode)
    {
        using var c = db.Open();
        return c.Query("SELECT item_tax_template, tax_category, valid_from, idx FROM item_tax WHERE parent_type = 'Item' AND parent = @c",
            r => ReadTax(r, 0), ("@c", itemCode));
    }

    public IReadOnlyList<ItemTaxAssignment> ItemGroupTaxes(string itemGroup) =>
        groupTaxes.TryGetValue(itemGroup, out var rows) ? rows : [];

    public ItemTaxTemplate? FindItemTaxTemplate(string name) => itemTaxTemplates.GetValueOrDefault(name);

    public SalesTaxTemplate? FindSalesTaxTemplate(string name)
    {
        using var c = db.Open();
        return c.Query("SELECT json FROM sales_tax_template WHERE name = @n",
            r => JsonSerializer.Deserialize<SalesTaxTemplate>(r.GetString(0)), ("@n", name)).FirstOrDefault();
    }

    /// <summary>Name search: every typed word must prefix-match a word of the item name.</summary>
    public IReadOnlyList<Item> Search(string text, int limit = 20)
    {
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => "\"" + t.Replace("\"", "\"\"") + "\"*")
            .ToList();
        if (tokens.Count == 0) return [];
        using var c = db.Open();
        return c.Query($"""
            SELECT {ItemColumns} FROM item i
            JOIN (SELECT rowid AS rid, rank AS score FROM item_fts WHERE item_fts MATCH @q ORDER BY rank LIMIT @l) f ON i.id = f.rid
            WHERE i.disabled = 0
            ORDER BY f.score
            """, ReadItem, ("@q", string.Join(' ', tokens)), ("@l", limit));
    }

    private static Item ReadItem(SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.GetString(2), Str(r, 3), r.GetString(4), r.GetInt64(5) != 0, r.GetInt64(6) != 0);

    private static ItemTaxAssignment ReadTax(SqliteDataReader r, int first) =>
        new(r.GetString(first), Str(r, first + 1), Date(r, first + 2), r.GetInt32(first + 3));
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter CatalogStoreTests`
Expected: PASS (9 tests).

- [ ] **Step 5: Commit**

```powershell
git add tillpos
git commit -m "feat(data): SQLite catalog store, ICatalog and name search"
```

---

### Task 9: ERPNext REST client

**Files:**
- Create: `tillpos/src/TillPOS.Erp/ErpModels.cs`, `ErpException.cs`, `IErpClient.cs`, `ErpClient.cs`
- Create: `tillpos/tests/TillPOS.Tests/Fakes/StubHandler.cs`
- Test: `tillpos/tests/TillPOS.Tests/Erp/ErpClientTests.cs`

**Interfaces:**
- Produces (namespace `TillPOS.Erp`):
  - `record ErpConnection(Uri BaseUrl, string ApiKey, string ApiSecret)`
  - `record ListQuery(string Doctype, IReadOnlyList<string> Fields, IReadOnlyList<object[]> Filters, string OrderBy = "modified asc", int Start = 0, int PageLength = 500)` (PageLength 0 = all rows)
  - `record ServerInfo(string User, DateTimeOffset? ServerTime)`
  - `sealed class ErpException : Exception` — `int StatusCode`, `string? ExcType`, `static ErpException From(int status, JsonElement? body, string raw)`
  - `interface IErpClient` — `GetListAsync(ListQuery, CancellationToken)`, `GetDocAsync(string doctype, string name, CancellationToken)`, `PingAsync(CancellationToken)`, `InsertAsync(string doctype, object doc, CancellationToken)`, `DeleteAsync(string doctype, string name, CancellationToken)`
  - `sealed class ErpClient(HttpClient http, ErpConnection conn) : IErpClient`, `static ErpClient Create(ErpConnection conn, TimeSpan? timeout = null)`

- [ ] **Step 1: Write the failing tests**

`tillpos/tests/TillPOS.Tests/Fakes/StubHandler.cs`:

```csharp
using System.Net;
using System.Text;

namespace TillPOS.Tests.Fakes;

public sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = [];

    public static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        Requests.Add((request, body));
        return respond(request);
    }
}
```

`tillpos/tests/TillPOS.Tests/Erp/ErpClientTests.cs`:

```csharp
using System.Net;
using System.Text.Json;
using TillPOS.Erp;
using TillPOS.Tests.Fakes;

namespace TillPOS.Tests.Erp;

public class ErpClientTests
{
    private static (ErpClient Client, StubHandler Handler) Make(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        return (new ErpClient(new HttpClient(handler), new ErpConnection(new Uri("https://erp.test/"), "key1", "secret1")), handler);
    }

    [Fact]
    public async Task Sends_token_auth_and_posts_get_list()
    {
        var (client, handler) = Make(_ => StubHandler.Json("""{"message":[{"name":"RICE5"},{"name":"MILK"}]}"""));

        var rows = await client.GetListAsync(new ListQuery("Item", ["name"], [["disabled", "=", 0]], "modified asc", 0, 500));

        Assert.Equal(new[] { "RICE5", "MILK" }, rows.Select(r => r.GetProperty("name").GetString()));
        var (req, body) = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("https://erp.test/api/method/frappe.client.get_list", req.RequestUri!.ToString());
        Assert.Equal("token key1:secret1", req.Headers.Authorization!.ToString());
        using var sent = JsonDocument.Parse(body!);
        Assert.Equal("Item", sent.RootElement.GetProperty("doctype").GetString());
        Assert.Equal(500, sent.RootElement.GetProperty("limit_page_length").GetInt32());
        Assert.Equal("disabled", sent.RootElement.GetProperty("filters")[0][0].GetString());
    }

    [Fact]
    public async Task Get_doc_escapes_doctype_and_name()
    {
        var (client, handler) = Make(_ => StubHandler.Json("""{"data":{"name":"Offer 10/24"}}"""));
        var doc = await client.GetDocAsync("Pricing Rule", "Offer 10/24");
        Assert.Equal("Offer 10/24", doc.GetProperty("name").GetString());
        Assert.Equal("/api/resource/Pricing%20Rule/Offer%2010%2F24", handler.Requests.Single().Request.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Ping_returns_user_and_server_date()
    {
        var (client, _) = Make(_ =>
        {
            var r = StubHandler.Json("""{"message":"till1@shop.local"}""");
            r.Headers.Date = new DateTimeOffset(2026, 10, 5, 10, 0, 0, TimeSpan.Zero);
            return r;
        });
        var info = await client.PingAsync();
        Assert.Equal("till1@shop.local", info.User);
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 10, 0, 0, TimeSpan.Zero), info.ServerTime);
    }

    [Fact]
    public async Task Error_response_becomes_erp_exception_with_server_message()
    {
        const string body = """{"exc_type":"ValidationError","_server_messages":"[\"{\\\"message\\\": \\\"Item <b>RICE5</b> is disabled\\\"}\"]"}""";
        var (client, _) = Make(_ => StubHandler.Json(body, HttpStatusCode.ExpectationFailed));

        var ex = await Assert.ThrowsAsync<ErpException>(() => client.GetDocAsync("Item", "RICE5"));

        Assert.Equal(417, ex.StatusCode);
        Assert.Equal("ValidationError", ex.ExcType);
        Assert.Equal("Item RICE5 is disabled", ex.Message);
    }

    [Fact]
    public async Task Non_json_error_still_gives_a_readable_message()
    {
        var (client, _) = Make(_ => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("<html>502 Bad Gateway</html>") });
        var ex = await Assert.ThrowsAsync<ErpException>(() => client.PingAsync());
        Assert.Equal(502, ex.StatusCode);
        Assert.Contains("HTTP 502", ex.Message);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter ErpClientTests`
Expected: build FAIL — namespace `TillPOS.Erp` types not found.

- [ ] **Step 3: Implement**

`tillpos/src/TillPOS.Erp/ErpModels.cs`:

```csharp
namespace TillPOS.Erp;

public sealed record ErpConnection(Uri BaseUrl, string ApiKey, string ApiSecret);

/// <summary>Arguments of frappe.client.get_list. Filters are [field, operator, value] triples.
/// PageLength 0 returns all rows.</summary>
public sealed record ListQuery(
    string Doctype,
    IReadOnlyList<string> Fields,
    IReadOnlyList<object[]> Filters,
    string OrderBy = "modified asc",
    int Start = 0,
    int PageLength = 500);

public sealed record ServerInfo(string User, DateTimeOffset? ServerTime);
```

`tillpos/src/TillPOS.Erp/ErpException.cs`:

```csharp
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TillPOS.Erp;

public sealed partial class ErpException(int statusCode, string message, string? excType) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string? ExcType { get; } = excType;

    public static ErpException From(int status, JsonElement? body, string raw)
    {
        string? excType = null, message = null;
        if (body is { ValueKind: JsonValueKind.Object } b)
        {
            if (b.TryGetProperty("exc_type", out var t) && t.ValueKind == JsonValueKind.String) excType = t.GetString();
            if (b.TryGetProperty("_server_messages", out var sm) && sm.ValueKind == JsonValueKind.String) message = FirstServerMessage(sm.GetString()!);
            if (message is null && b.TryGetProperty("exception", out var ex) && ex.ValueKind == JsonValueKind.String) message = ex.GetString();
            if (message is null && b.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String) message = m.GetString();
        }
        return new ErpException(status, message ?? $"ERPNext returned HTTP {status}: {Truncate(raw, 200)}", excType);
    }

    // _server_messages is a JSON array of JSON-encoded objects: "[\"{\\\"message\\\": \\\"...\\\"}\"]"
    private static string? FirstServerMessage(string serverMessages)
    {
        try
        {
            using var outer = JsonDocument.Parse(serverMessages);
            foreach (var item in outer.RootElement.EnumerateArray())
            {
                if (item.GetString() is not { } inner) continue;
                using var msg = JsonDocument.Parse(inner);
                if (msg.RootElement.TryGetProperty("message", out var m) && m.GetString() is { } text) return Html().Replace(text, "");
            }
        }
        catch (JsonException) { }
        return null;
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];

    [GeneratedRegex("<.*?>")]
    private static partial Regex Html();
}
```

`tillpos/src/TillPOS.Erp/IErpClient.cs`:

```csharp
using System.Text.Json;

namespace TillPOS.Erp;

public interface IErpClient
{
    Task<IReadOnlyList<JsonElement>> GetListAsync(ListQuery query, CancellationToken ct = default);
    Task<JsonElement> GetDocAsync(string doctype, string name, CancellationToken ct = default);
    Task<ServerInfo> PingAsync(CancellationToken ct = default);
    Task<JsonElement> InsertAsync(string doctype, object doc, CancellationToken ct = default);
    Task DeleteAsync(string doctype, string name, CancellationToken ct = default);
}
```

`tillpos/src/TillPOS.Erp/ErpClient.cs`:

```csharp
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace TillPOS.Erp;

/// <summary>Thin client for the Frappe REST API using token (API key/secret) auth.</summary>
public sealed class ErpClient : IErpClient
{
    private static readonly JsonSerializerOptions JsonOptions = new();
    private readonly HttpClient http;

    public ErpClient(HttpClient http, ErpConnection conn)
    {
        this.http = http;
        http.BaseAddress = conn.BaseUrl;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", $"{conn.ApiKey}:{conn.ApiSecret}");
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public static ErpClient Create(ErpConnection conn, TimeSpan? timeout = null) =>
        new(new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(30) }, conn);

    public async Task<IReadOnlyList<JsonElement>> GetListAsync(ListQuery query, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["doctype"] = query.Doctype,
            ["fields"] = query.Fields,
            ["filters"] = query.Filters,
            ["order_by"] = query.OrderBy,
            ["limit_start"] = query.Start,
            ["limit_page_length"] = query.PageLength,
        };
        var root = await SendAsync(HttpMethod.Post, "api/method/frappe.client.get_list", body, ct);
        return root.GetProperty("message").EnumerateArray().Select(e => e.Clone()).ToList();
    }

    public async Task<JsonElement> GetDocAsync(string doctype, string name, CancellationToken ct = default) =>
        (await SendAsync(HttpMethod.Get, ResourcePath(doctype, name), null, ct)).GetProperty("data").Clone();

    public async Task<ServerInfo> PingAsync(CancellationToken ct = default)
    {
        using var resp = await http.GetAsync("api/method/frappe.auth.get_logged_user", ct);
        var root = await ReadAsync(resp, ct);
        return new ServerInfo(root.GetProperty("message").GetString() ?? "", resp.Headers.Date);
    }

    public async Task<JsonElement> InsertAsync(string doctype, object doc, CancellationToken ct = default) =>
        (await SendAsync(HttpMethod.Post, ResourcePath(doctype, null), doc, ct)).GetProperty("data").Clone();

    public async Task DeleteAsync(string doctype, string name, CancellationToken ct = default) =>
        await SendAsync(HttpMethod.Delete, ResourcePath(doctype, name), null, ct);

    private static string ResourcePath(string doctype, string? name) =>
        name is null
            ? $"api/resource/{Uri.EscapeDataString(doctype)}"
            : $"api/resource/{Uri.EscapeDataString(doctype)}/{Uri.EscapeDataString(name)}";

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, path);
        if (body is not null) req.Content = JsonContent.Create(body, options: JsonOptions);
        using var resp = await http.SendAsync(req, ct);
        return await ReadAsync(resp, ct);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var text = await resp.Content.ReadAsStringAsync(ct);
        JsonElement? root = null;
        if (!string.IsNullOrWhiteSpace(text))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                root = doc.RootElement.Clone();
            }
            catch (JsonException) { }
        }
        if (!resp.IsSuccessStatusCode) throw ErpException.From((int)resp.StatusCode, root, text);
        return root ?? throw new ErpException((int)resp.StatusCode, "Empty or non-JSON response from ERPNext.", null);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter ErpClientTests`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```powershell
git add tillpos
git commit -m "feat(erp): ERPNext REST client with error parsing"
```

---

### Task 10: Resumable keyset paging and sync state

**Files:**
- Create: `tillpos/src/TillPOS.Sync/SyncState.cs`, `tillpos/src/TillPOS.Sync/KeysetPager.cs`
- Create: `tillpos/tests/TillPOS.Tests/Fakes/FakeErp.cs`, `tillpos/tests/TillPOS.Tests/Fakes/InMemorySyncState.cs`
- Test: `tillpos/tests/TillPOS.Tests/Sync/KeysetPagerTests.cs`

**Interfaces:**
- Consumes: `IErpClient`, `ListQuery`, `CatalogStore.GetValue/SetValue`.
- Produces (namespace `TillPOS.Sync`):
  - `record SyncMark(string Modified, IReadOnlyList<string> NamesAtMark)` with `static SyncMark Start`
  - `interface ISyncStateStore { SyncMark Get(string key); void Set(string key, SyncMark mark); }`
  - `sealed class KvSyncStateStore(CatalogStore store) : ISyncStateStore`
  - `sealed class KeysetPager(IErpClient erp, ISyncStateStore state)` with `Task<int> PullAsync(string key, string doctype, IReadOnlyList<string> fields, IReadOnlyList<object[]> extraFilters, Func<IReadOnlyList<JsonElement>, Task> handlePage, int pageSize = 500, CancellationToken ct = default)` and `static string NormalizeTimestamp(string raw)`
  - Test fakes `FakeErp : IErpClient` (filters `>=`, `>`, `=`, `in`; `Override`, `Fail`, `Docs`, `ListCalls`) and `InMemorySyncState : ISyncStateStore`

- [ ] **Step 1: Write the fakes and failing tests**

`tillpos/tests/TillPOS.Tests/Fakes/FakeErp.cs`:

```csharp
using System.Text.Json;
using TillPOS.Erp;

namespace TillPOS.Tests.Fakes;

/// <summary>In-memory ERPNext: list queries filter/sort rows of a doctype; GetDoc returns Docs entries.</summary>
public sealed class FakeErp : IErpClient
{
    private readonly Dictionary<string, List<Dictionary<string, object?>>> tables = [];

    public Dictionary<(string Doctype, string Name), object> Docs { get; } = [];
    public List<ListQuery> ListCalls { get; } = [];
    /// <summary>Return a non-null list to answer a query yourself (e.g. child-table joins).</summary>
    public Func<ListQuery, IReadOnlyList<object>?>? Override { get; set; }
    /// <summary>Return an exception to make a query fail.</summary>
    public Func<ListQuery, Exception?>? Fail { get; set; }

    public void AddRow(string doctype, Dictionary<string, object?> row)
    {
        if (!tables.TryGetValue(doctype, out var t)) tables[doctype] = t = [];
        t.Add(row);
    }

    public Task<IReadOnlyList<JsonElement>> GetListAsync(ListQuery q, CancellationToken ct = default)
    {
        ListCalls.Add(q);
        if (Fail?.Invoke(q) is { } ex) throw ex;
        var rows = Override?.Invoke(q) ?? Filtered(q);
        return Task.FromResult<IReadOnlyList<JsonElement>>(rows.Select(r => JsonSerializer.SerializeToElement(r)).ToList());
    }

    public Task<JsonElement> GetDocAsync(string doctype, string name, CancellationToken ct = default) =>
        Docs.TryGetValue((doctype, name), out var d)
            ? Task.FromResult(JsonSerializer.SerializeToElement(d))
            : throw new ErpException(404, $"{doctype} {name} not found", "DoesNotExistError");

    public Task<ServerInfo> PingAsync(CancellationToken ct = default) =>
        Task.FromResult(new ServerInfo("till1@shop.local", DateTimeOffset.UtcNow));

    public List<(string Doctype, JsonElement Doc)> Inserted { get; } = [];

    public Task<JsonElement> InsertAsync(string doctype, object doc, CancellationToken ct = default)
    {
        var e = JsonSerializer.SerializeToElement(doc);
        Inserted.Add((doctype, e));
        return Task.FromResult(e);
    }

    public Task DeleteAsync(string doctype, string name, CancellationToken ct = default) => Task.CompletedTask;

    private List<object> Filtered(ListQuery q)
    {
        IEnumerable<Dictionary<string, object?>> rows = tables.TryGetValue(q.Doctype, out var t) ? t : [];
        foreach (var f in q.Filters)
        {
            var field = (string)f[0];
            var op = (string)f[1];
            var value = f[2];
            rows = rows.Where(r => Match(r.GetValueOrDefault(field), op, value));
        }
        var ordered = rows
            .OrderBy(r => r.GetValueOrDefault("modified")?.ToString(), StringComparer.Ordinal)
            .ThenBy(r => r["name"]?.ToString(), StringComparer.Ordinal)
            .Skip(q.Start);
        return (q.PageLength > 0 ? ordered.Take(q.PageLength) : ordered).Cast<object>().ToList();
    }

    private static bool Match(object? actual, string op, object? expected) => op switch
    {
        ">=" => string.CompareOrdinal(actual?.ToString(), expected?.ToString()) >= 0,
        ">" => string.CompareOrdinal(actual?.ToString(), expected?.ToString()) > 0,
        "=" => Equals(actual?.ToString(), expected?.ToString()),
        "in" => expected is IEnumerable<string> set && set.Contains(actual?.ToString()),
        _ => throw new NotSupportedException($"FakeErp does not support operator '{op}'."),
    };
}
```

`tillpos/tests/TillPOS.Tests/Fakes/InMemorySyncState.cs`:

```csharp
using TillPOS.Sync;

namespace TillPOS.Tests.Fakes;

public sealed class InMemorySyncState : ISyncStateStore
{
    public Dictionary<string, SyncMark> Marks { get; } = [];
    public SyncMark Get(string key) => Marks.GetValueOrDefault(key, SyncMark.Start);
    public void Set(string key, SyncMark mark) => Marks[key] = mark;
}
```

`tillpos/tests/TillPOS.Tests/Sync/KeysetPagerTests.cs`:

```csharp
using System.Text.Json;
using TillPOS.Sync;
using TillPOS.Tests.Fakes;

namespace TillPOS.Tests.Sync;

public class KeysetPagerTests
{
    private readonly FakeErp erp = new();
    private readonly InMemorySyncState state = new();
    private readonly List<string> seen = [];

    private void Row(string name, string modified) =>
        erp.AddRow("Item", new() { ["name"] = name, ["modified"] = modified });

    private static string Ts(int second, int micro = 0) => $"2026-10-05 10:00:{second:00}.{micro:000000}";

    private Task<int> Pull(int pageSize = 2, Func<IReadOnlyList<JsonElement>, Task>? handler = null) =>
        new KeysetPager(erp, state).PullAsync("Item", "Item", ["item_name"], [],
            handler ?? (page => { seen.AddRange(page.Select(r => r.GetProperty("name").GetString()!)); return Task.CompletedTask; }),
            pageSize);

    [Fact]
    public async Task Pulls_every_row_across_pages_in_order()
    {
        for (var i = 1; i <= 5; i++) Row($"I{i}", Ts(i));
        Assert.Equal(5, await Pull());
        Assert.Equal(new[] { "I1", "I2", "I3", "I4", "I5" }, seen);
    }

    [Fact]
    public async Task Second_pull_only_gets_changes()
    {
        Row("I1", Ts(1));
        Row("I2", Ts(2));
        await Pull();
        seen.Clear();
        Assert.Equal(0, await Pull());

        Row("I3", Ts(3));
        Assert.Equal(1, await Pull());
        Assert.Equal(new[] { "I3" }, seen);
    }

    [Fact]
    public async Task Rows_sharing_a_timestamp_across_a_page_boundary_are_neither_skipped_nor_repeated()
    {
        foreach (var n in new[] { "A", "B", "C", "D", "E" }) Row(n, Ts(1));
        Row("F", Ts(2));
        await Pull(pageSize: 2);
        Assert.Equal(new[] { "A", "B", "C", "D", "E", "F" }, seen);
    }

    [Fact]
    public async Task Full_page_of_already_seen_rows_grows_the_page()
    {
        foreach (var n in new[] { "A", "B", "C" }) Row(n, Ts(1));
        state.Set("Item", new SyncMark(Ts(1), ["A", "B"]));
        await Pull(pageSize: 2);
        Assert.Equal(new[] { "C" }, seen);
    }

    [Fact]
    public async Task Resumes_after_handler_failure_without_skipping()
    {
        for (var i = 1; i <= 4; i++) Row($"I{i}", Ts(i));
        var calls = 0;
        await Assert.ThrowsAsync<IOException>(() => Pull(2, page =>
        {
            if (++calls == 2) throw new IOException("network dropped");
            seen.AddRange(page.Select(r => r.GetProperty("name").GetString()!));
            return Task.CompletedTask;
        }));
        Assert.Equal(new[] { "I1", "I2" }, seen);

        await Pull();
        Assert.Equal(new[] { "I1", "I2", "I3", "I4" }, seen);
    }

    [Theory]
    [InlineData("2026-10-05 10:00:01", "2026-10-05 10:00:01.000000")]
    [InlineData("2026-10-05 10:00:01.5", "2026-10-05 10:00:01.500000")]
    [InlineData("2026-10-05 10:00:01.123456", "2026-10-05 10:00:01.123456")]
    public void Normalizes_frappe_timestamps(string raw, string expected) =>
        Assert.Equal(expected, KeysetPager.NormalizeTimestamp(raw));
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter KeysetPagerTests`
Expected: build FAIL — namespace `TillPOS.Sync` types not found.

- [ ] **Step 3: Implement**

`tillpos/src/TillPOS.Sync/SyncState.cs`:

```csharp
using System.Text.Json;
using TillPOS.Data;

namespace TillPOS.Sync;

/// <summary>High-water mark: newest `modified` processed, plus the names already processed at exactly that time.</summary>
public sealed record SyncMark(string Modified, IReadOnlyList<string> NamesAtMark)
{
    public static readonly SyncMark Start = new("1900-01-01 00:00:00.000000", []);
}

public interface ISyncStateStore
{
    SyncMark Get(string key);
    void Set(string key, SyncMark mark);
}

public sealed class KvSyncStateStore(CatalogStore store) : ISyncStateStore
{
    public SyncMark Get(string key) =>
        store.GetValue("sync_mark:" + key) is { } json ? JsonSerializer.Deserialize<SyncMark>(json) ?? SyncMark.Start : SyncMark.Start;

    public void Set(string key, SyncMark mark) => store.SetValue("sync_mark:" + key, JsonSerializer.Serialize(mark));
}
```

`tillpos/src/TillPOS.Sync/KeysetPager.cs`:

```csharp
using System.Globalization;
using System.Text.Json;
using TillPOS.Erp;

namespace TillPOS.Sync;

/// <summary>Pulls rows changed since the saved mark, ordered by (modified, name), page by page.
/// The mark is saved after every page, so an interrupted pull resumes where it stopped.
/// Uses `modified >= mark` and skips names already processed at the mark, so rows sharing a
/// timestamp across a page boundary are never skipped or repeated.</summary>
public sealed class KeysetPager(IErpClient erp, ISyncStateStore state)
{
    public async Task<int> PullAsync(
        string key, string doctype, IReadOnlyList<string> fields, IReadOnlyList<object[]> extraFilters,
        Func<IReadOnlyList<JsonElement>, Task> handlePage, int pageSize = 500, CancellationToken ct = default)
    {
        var mark = state.Get(key);
        var processedAtMark = new HashSet<string>(mark.NamesAtMark);
        var allFields = fields.Union(["name", "modified"]).ToList();
        var size = pageSize;
        var total = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var filters = extraFilters.Append(["modified", ">=", mark.Modified]).ToList();
            var rows = await erp.GetListAsync(new ListQuery(doctype, allFields, filters, "modified asc, name asc", 0, size), ct);
            if (rows.Count == 0) break;

            var fresh = rows.Where(r => !(Modified(r) == mark.Modified && processedAtMark.Contains(Name(r)))).ToList();
            if (fresh.Count == 0)
            {
                if (rows.Count < size) break;
                size *= 2; // a full page of rows we already processed: widen the window
                continue;
            }

            await handlePage(fresh);
            total += fresh.Count;

            var last = Modified(rows[^1]);
            var namesAtLast = rows.Where(r => Modified(r) == last).Select(Name);
            processedAtMark = last == mark.Modified
                ? [.. processedAtMark, .. namesAtLast]
                : [.. namesAtLast];
            mark = new SyncMark(last, processedAtMark.ToList());
            state.Set(key, mark);

            if (rows.Count < size) break;
            size = pageSize;
        }
        return total;
    }

    public static string NormalizeTimestamp(string raw) =>
        DateTime.ParseExact(raw, ["yyyy-MM-dd HH:mm:ss.FFFFFF", "yyyy-MM-dd HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None)
            .ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);

    private static string Modified(JsonElement r) => NormalizeTimestamp(r.GetProperty("modified").GetString()!);
    private static string Name(JsonElement r) => r.GetProperty("name").GetString()!;
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter KeysetPagerTests`
Expected: PASS (8 tests).

- [ ] **Step 5: Commit**

```powershell
git add tillpos
git commit -m "feat(sync): resumable keyset paging with saved marks"
```

---

### Task 11: ERPNext mapping, catalog feeds and the puller

**Files:**
- Create: `tillpos/src/TillPOS.Erp/Mapping/JsonFields.cs`, `tillpos/src/TillPOS.Erp/Mapping/CatalogMapper.cs`
- Create: `tillpos/src/TillPOS.Sync/SyncContext.cs`, `tillpos/src/TillPOS.Sync/CatalogPuller.cs`
- Create: `tillpos/src/TillPOS.Sync/Feeds/ISyncFeed.cs`, `PosProfileFeed.cs`, `ItemGroupFeed.cs`, `DocFeed.cs`, `ItemFeed.cs`, `ItemPriceFeed.cs`, `DeletionFeed.cs`, `ReconcileFeed.cs`
- Test: `tillpos/tests/TillPOS.Tests/Erp/CatalogMapperTests.cs`
- Test: `tillpos/tests/TillPOS.Tests/Sync/FeedTests.cs`

**Interfaces:**
- Consumes: Tasks 3, 8, 9, 10.
- Produces:
  - namespace `TillPOS.Erp.Mapping`: `static class JsonFields` (`Str`, `StrOrNull`, `Dec`, `Int`, `Bool`, `Date`, `Rows` extension methods on `JsonElement`); `static class CatalogMapper` (`Item`, `Barcode`, `Uom`, `ItemTax`, `Price`, `Group`, `PricingRule`, `ItemTaxTemplate`, `SalesTaxTemplate`, `PosSettings`)
  - namespace `TillPOS.Sync`: `record SyncContext(IErpClient Erp, CatalogStore Store, KeysetPager Pager, string PosProfile)`; `interface ISyncFeed { string Name { get; } Task<int> RunAsync(CancellationToken ct); }`; feeds `PosProfileFeed`, `ItemGroupFeed`, `DocFeed<T>`, `ItemFeed`, `ItemPriceFeed`, `DeletionFeed`, `ReconcileFeed`; `record FeedResult(string Feed, int Rows, TimeSpan Duration, string? Error)`; `record PullReport(IReadOnlyList<FeedResult> Feeds)` with `bool Ok`; `sealed class CatalogPuller(IReadOnlyList<ISyncFeed> feeds, Action afterPull)` with `Task<PullReport> RunAsync(CancellationToken ct = default)` and `static CatalogPuller CreateDefault(SyncContext ctx, Action afterPull, Func<DateTimeOffset>? now = null)`

- [ ] **Step 1: Write the failing mapper tests**

`tillpos/tests/TillPOS.Tests/Erp/CatalogMapperTests.cs`:

```csharp
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
```

- [ ] **Step 2: Write the failing feed tests**

`tillpos/tests/TillPOS.Tests/Sync/FeedTests.cs`:

```csharp
using TillPOS.Core.Catalog;
using TillPOS.Core.Pricing;
using TillPOS.Data;
using TillPOS.Erp;
using TillPOS.Sync;
using TillPOS.Sync.Feeds;
using TillPOS.Tests.Data;
using TillPOS.Tests.Fakes;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Sync;

public sealed class FeedTests : IDisposable
{
    private const string T1 = "2026-10-05 10:00:01.000000";
    private readonly TempDb temp = new();
    private readonly FakeErp erp = new();
    private readonly CatalogStore store;
    private readonly SyncContext ctx;

    public FeedTests()
    {
        store = new CatalogStore(temp.Db);
        ctx = new SyncContext(erp, store, new KeysetPager(erp, new InMemorySyncState()), "Till 1");
        store.SavePosSettings(new PosSettings("Till 1", "Shop LLC", "Shop LLC", null, null, "AED", "Stores - S", "Retail",
            "Walk-in Customer", "UAE VAT 5%", null, false, 0m, 0m, [new PaymentMode("Cash", true)]));
    }

    public void Dispose() => temp.Dispose();

    private SqliteCatalog Catalog() => new(temp.Db);

    [Fact]
    public async Task Item_feed_stores_items_with_barcodes_uoms_and_taxes()
    {
        erp.AddRow("Item", new() { ["name"] = "WATER", ["modified"] = T1, ["item_name"] = "Water 500ml", ["item_group"] = "Food",
            ["brand"] = null, ["stock_uom"] = "Nos", ["disabled"] = 0, ["is_sales_item"] = 1 });
        erp.Override = q => q.Fields.Any(f => f.Contains("tabItem Barcode"))
                ? [new Dictionary<string, object?> { ["name"] = "WATER", ["barcode"] = "111", ["barcode_uom"] = null },
                   new Dictionary<string, object?> { ["name"] = "WATER", ["barcode"] = "112", ["barcode_uom"] = "Box" }]
            : q.Fields.Any(f => f.Contains("tabUOM Conversion Detail"))
                ? [new Dictionary<string, object?> { ["name"] = "WATER", ["uom"] = "Box", ["conversion_factor"] = 12 }]
            : q.Fields.Any(f => f.Contains("tabItem Tax"))
                ? [new Dictionary<string, object?> { ["name"] = "WATER", ["item_tax_template"] = null, ["tax_category"] = null, ["valid_from"] = null, ["idx"] = null }]
            : null;

        Assert.Equal(1, await new ItemFeed(ctx).RunAsync(default));

        var c = Catalog();
        Assert.Equal("Water 500ml", c.FindItem("WATER")!.ItemName);
        Assert.Equal("Box", c.FindBarcode("112")!.Uom);
        Assert.Equal(12m, c.ConversionFactor("WATER", "Box"));
        Assert.Empty(c.ItemTaxes("WATER"));
    }

    [Fact]
    public async Task Item_price_feed_keeps_only_retail_prices_without_customer()
    {
        void Price(string name, string list, string? customer, string rate) => erp.AddRow("Item Price", new()
        {
            ["name"] = name, ["modified"] = T1, ["item_code"] = "RICE5", ["uom"] = "Nos", ["price_list_rate"] = rate,
            ["valid_from"] = null, ["valid_upto"] = null, ["price_list"] = list, ["customer"] = customer, ["batch_no"] = null,
        });
        Price("P-RETAIL", "Retail", null, "2150");
        Price("P-WHOLESALE", "Wholesale", null, "1900");
        Price("P-CUSTOMER", "Retail", "Big Buyer LLC", "1800");

        await new ItemPriceFeed(ctx).RunAsync(default);

        Assert.Equal(new[] { "P-RETAIL" }, store.AllPriceNames());
    }

    [Fact]
    public async Task Item_price_moved_off_retail_is_removed_locally()
    {
        store.UpsertPrices([new ItemPrice("P1", "RICE5", "Nos", 2150m, null, null)]);
        erp.AddRow("Item Price", new()
        {
            ["name"] = "P1", ["modified"] = T1, ["item_code"] = "RICE5", ["uom"] = "Nos", ["price_list_rate"] = "2150",
            ["valid_from"] = null, ["valid_upto"] = null, ["price_list"] = "Wholesale", ["customer"] = null, ["batch_no"] = null,
        });

        await new ItemPriceFeed(ctx).RunAsync(default);

        Assert.Empty(store.AllPriceNames());
    }

    [Fact]
    public async Task Pricing_rule_feed_upserts_and_deletes_disabled_rules()
    {
        erp.AddRow("Pricing Rule", new() { ["name"] = "R-ON", ["modified"] = T1 });
        erp.AddRow("Pricing Rule", new() { ["name"] = "R-OFF", ["modified"] = T1 });
        erp.Docs[("Pricing Rule", "R-ON")] = new Dictionary<string, object?>
        {
            ["name"] = "R-ON", ["apply_on"] = "Item Code", ["items"] = new[] { new Dictionary<string, object?> { ["item_code"] = "RICE5" } },
            ["selling"] = 1, ["rate_or_discount"] = "Discount Percentage", ["discount_percentage"] = 10, ["priority"] = "1",
        };
        erp.Docs[("Pricing Rule", "R-OFF")] = new Dictionary<string, object?> { ["name"] = "R-OFF", ["apply_on"] = "Item Code", ["selling"] = 1, ["disable"] = 1 };
        store.UpsertPricingRule(new PricingRule("R-OFF", RuleApplyOn.ItemCode, ["X"], RuleKind.DiscountPercentage, 5m, 0, null, null, null, null, null));

        var feed = new DocFeed<PricingRule>(ctx, "Pricing Rule", TillPOS.Erp.Mapping.CatalogMapper.PricingRule, store.UpsertPricingRule, store.DeletePricingRule);
        await feed.RunAsync(default);

        Assert.Equal("R-ON", Assert.Single(Catalog().PricingRules()).Name);
    }

    [Fact]
    public async Task Deletion_feed_removes_deleted_items()
    {
        store.UpsertItems([new ItemSnapshot(new Item("GONE", "Gone", "Food", null, "Nos", false, true), [new ItemBarcode("9", "GONE", null)], [], [])]);
        erp.AddRow("Deleted Document", new() { ["name"] = "DD-1", ["modified"] = T1, ["deleted_doctype"] = "Item", ["deleted_name"] = "GONE" });

        await new DeletionFeed(ctx).RunAsync(default);

        Assert.Null(Catalog().FindBarcode("9"));
    }

    [Fact]
    public async Task Reconcile_removes_items_missing_on_server()
    {
        store.UpsertItems([
            new ItemSnapshot(new Item("KEEP", "Keep", "Food", null, "Nos", false, true), [], [], []),
            new ItemSnapshot(new Item("RENAMED-OLD", "Old name", "Food", null, "Nos", false, true), [], [], []),
        ]);
        erp.AddRow("Item", new() { ["name"] = "KEEP", ["modified"] = T1 });

        var removed = await new ReconcileFeed(ctx, () => DateTimeOffset.UtcNow).RunAsync(default);

        Assert.Equal(1, removed);
        Assert.Equal(new[] { "KEEP" }, store.AllItemCodes());
    }

    [Fact]
    public async Task Reconcile_refuses_to_wipe_everything_when_server_returns_no_items()
    {
        store.UpsertItems([new ItemSnapshot(new Item("KEEP", "Keep", "Food", null, "Nos", false, true), [], [], [])]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ReconcileFeed(ctx, () => DateTimeOffset.UtcNow).RunAsync(default));
        Assert.Equal(new[] { "KEEP" }, store.AllItemCodes());
    }

    [Fact]
    public async Task Reconcile_runs_at_most_once_a_day()
    {
        var now = new DateTimeOffset(2026, 10, 5, 8, 0, 0, TimeSpan.Zero);
        erp.AddRow("Item", new() { ["name"] = "KEEP", ["modified"] = T1 });
        store.UpsertItems([new ItemSnapshot(new Item("KEEP", "Keep", "Food", null, "Nos", false, true), [], [], [])]);
        await new ReconcileFeed(ctx, () => now).RunAsync(default);
        var callsAfterFirst = erp.ListCalls.Count;

        await new ReconcileFeed(ctx, () => now.AddHours(5)).RunAsync(default);

        Assert.Equal(callsAfterFirst, erp.ListCalls.Count);
    }

    [Fact]
    public async Task Puller_continues_after_a_feed_fails()
    {
        erp.Fail = q => q.Doctype == "Deleted Document" ? new ErpException(403, "Not permitted", "PermissionError") : null;
        erp.AddRow("Item", new() { ["name"] = "A", ["modified"] = T1, ["item_name"] = "A", ["item_group"] = "Food",
            ["brand"] = null, ["stock_uom"] = "Nos", ["disabled"] = 0, ["is_sales_item"] = 1 });
        var reloaded = false;
        var puller = new CatalogPuller([new DeletionFeed(ctx), new ItemFeed(ctx)], () => reloaded = true);

        var report = await puller.RunAsync();

        Assert.False(report.Ok);
        Assert.Equal("Not permitted", report.Feeds[0].Error);
        Assert.Null(report.Feeds[1].Error);
        Assert.NotNull(Catalog().FindItem("A"));
        Assert.True(reloaded);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test --filter "CatalogMapperTests|FeedTests"`
Expected: build FAIL — `TillPOS.Erp.Mapping` / `TillPOS.Sync.Feeds` not found.

- [ ] **Step 4: Implement the mapping**

`tillpos/src/TillPOS.Erp/Mapping/JsonFields.cs`:

```csharp
using System.Globalization;
using System.Text.Json;

namespace TillPOS.Erp.Mapping;

/// <summary>Tolerant accessors: Frappe returns numbers as numbers or strings, and blanks as "" or null.</summary>
public static class JsonFields
{
    public static string Str(this JsonElement e, string p) =>
        e.StrOrNull(p) ?? throw new FormatException($"Missing text field '{p}'.");

    public static string? StrOrNull(this JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    public static decimal Dec(this JsonElement e, string p)
    {
        if (!e.TryGetProperty(p, out var v)) return 0m;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDecimal(),
            JsonValueKind.String when decimal.TryParse(v.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) => d,
            _ => 0m,
        };
    }

    public static int Int(this JsonElement e, string p) => (int)e.Dec(p);

    public static bool Bool(this JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && (v.ValueKind == JsonValueKind.True || e.Dec(p) != 0);

    public static DateOnly? Date(this JsonElement e, string p) =>
        e.StrOrNull(p) is { Length: >= 10 } s ? DateOnly.ParseExact(s[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture) : null;

    public static IEnumerable<JsonElement> Rows(this JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : Enumerable.Empty<JsonElement>();
}
```

`tillpos/src/TillPOS.Erp/Mapping/CatalogMapper.cs`:

```csharp
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
```

- [ ] **Step 5: Implement the feeds and puller**

`tillpos/src/TillPOS.Sync/SyncContext.cs`:

```csharp
using TillPOS.Data;
using TillPOS.Erp;

namespace TillPOS.Sync;

public sealed record SyncContext(IErpClient Erp, CatalogStore Store, KeysetPager Pager, string PosProfile);
```

`tillpos/src/TillPOS.Sync/Feeds/ISyncFeed.cs`:

```csharp
namespace TillPOS.Sync.Feeds;

public interface ISyncFeed
{
    string Name { get; }
    /// <summary>Pulls this feed's changes into the local store; returns rows processed.</summary>
    Task<int> RunAsync(CancellationToken ct);
}
```

`tillpos/src/TillPOS.Sync/Feeds/PosProfileFeed.cs`:

```csharp
using System.Text.Json;
using TillPOS.Erp.Mapping;

namespace TillPOS.Sync.Feeds;

/// <summary>Re-reads this till's POS Profile, its Company, Currency and address on every pull (4 small requests).</summary>
public sealed class PosProfileFeed(SyncContext ctx) : ISyncFeed
{
    public string Name => "POS Profile";

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var profile = await ctx.Erp.GetDocAsync("POS Profile", ctx.PosProfile, ct);
        var company = await ctx.Erp.GetDocAsync("Company", profile.Str("company"), ct);
        var currency = await ctx.Erp.GetDocAsync("Currency", profile.StrOrNull("currency") ?? company.Str("default_currency"), ct);
        JsonElement? address = profile.StrOrNull("company_address") is { } a ? await ctx.Erp.GetDocAsync("Address", a, ct) : null;
        ctx.Store.SavePosSettings(CatalogMapper.PosSettings(profile, company, currency, address));
        return 1;
    }
}
```

`tillpos/src/TillPOS.Sync/Feeds/ItemGroupFeed.cs`:

```csharp
using TillPOS.Core.Catalog;
using TillPOS.Data;
using TillPOS.Erp;
using TillPOS.Erp.Mapping;

namespace TillPOS.Sync.Feeds;

/// <summary>Full refresh every pull: group trees are small, and ERPNext rebuilds lft/rgt without touching `modified`.</summary>
public sealed class ItemGroupFeed(SyncContext ctx) : ISyncFeed
{
    public string Name => "Item Group";

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var groups = await ctx.Erp.GetListAsync(new ListQuery("Item Group", ["name", "parent_item_group", "lft", "rgt"], [], "lft asc", 0, 0), ct);
        var taxes = (await ctx.Erp.GetListAsync(new ListQuery("Item Group",
            ["name", "`tabItem Tax`.item_tax_template as item_tax_template", "`tabItem Tax`.tax_category as tax_category",
             "`tabItem Tax`.valid_from as valid_from", "`tabItem Tax`.idx as idx"], [], "name asc", 0, 0), ct))
            .ToLookup(r => r.Str("name"));

        ctx.Store.ReplaceItemGroups(groups.Select(g => new ItemGroupSnapshot(
            CatalogMapper.Group(g),
            taxes[g.Str("name")].Select(CatalogMapper.ItemTax).OfType<ItemTaxAssignment>().ToList())));
        return groups.Count;
    }
}
```

`tillpos/src/TillPOS.Sync/Feeds/DocFeed.cs`:

```csharp
using TillPOS.Erp.Mapping;

namespace TillPOS.Sync.Feeds;

/// <summary>For small doctypes with child tables (Pricing Rule, tax templates): list changed names,
/// fetch each full document, then upsert it — or delete it locally when the mapper returns null.</summary>
public sealed class DocFeed<T>(
    SyncContext ctx, string doctype, Func<System.Text.Json.JsonElement, T?> map, Action<T> upsert, Action<string> delete) : ISyncFeed
    where T : class
{
    public string Name => doctype;

    public Task<int> RunAsync(CancellationToken ct) =>
        ctx.Pager.PullAsync(doctype, doctype, [], [], async page =>
        {
            foreach (var row in page)
            {
                var name = row.Str("name");
                var mapped = map(await ctx.Erp.GetDocAsync(doctype, name, ct));
                if (mapped is null) delete(name); else upsert(mapped);
            }
        }, ct: ct);
}
```

`tillpos/src/TillPOS.Sync/Feeds/ItemFeed.cs`:

```csharp
using System.Text.Json;
using TillPOS.Core.Catalog;
using TillPOS.Data;
using TillPOS.Erp;
using TillPOS.Erp.Mapping;

namespace TillPOS.Sync.Feeds;

/// <summary>Changed items, 500 per page; for each page, three join queries fetch barcodes, UOMs and item taxes.
/// Saving an item in ERPNext updates the item's `modified` when any child row changes.</summary>
public sealed class ItemFeed(SyncContext ctx) : ISyncFeed
{
    private static readonly string[] Fields = ["item_name", "item_group", "brand", "stock_uom", "disabled", "is_sales_item"];

    public string Name => "Item";

    public Task<int> RunAsync(CancellationToken ct) =>
        ctx.Pager.PullAsync("Item", "Item", Fields, [], async page =>
        {
            var names = page.Select(r => r.Str("name")).ToList();
            var barcodes = await Children(names, ["`tabItem Barcode`.barcode as barcode", "`tabItem Barcode`.uom as barcode_uom"], ct);
            var uoms = await Children(names, ["`tabUOM Conversion Detail`.uom as uom", "`tabUOM Conversion Detail`.conversion_factor as conversion_factor"], ct);
            var taxes = await Children(names, ["`tabItem Tax`.item_tax_template as item_tax_template", "`tabItem Tax`.tax_category as tax_category",
                "`tabItem Tax`.valid_from as valid_from", "`tabItem Tax`.idx as idx"], ct);

            ctx.Store.UpsertItems(page.Select(r =>
            {
                var code = r.Str("name");
                return new ItemSnapshot(
                    CatalogMapper.Item(r),
                    barcodes[code].Select(CatalogMapper.Barcode).OfType<ItemBarcode>().ToList(),
                    uoms[code].Select(CatalogMapper.Uom).OfType<ItemUom>().ToList(),
                    taxes[code].Select(CatalogMapper.ItemTax).OfType<ItemTaxAssignment>().ToList());
            }));
        }, ct: ct);

    private async Task<ILookup<string, JsonElement>> Children(List<string> names, string[] fields, CancellationToken ct)
    {
        var rows = await ctx.Erp.GetListAsync(new ListQuery("Item", ["name", .. fields], [["name", "in", names]], "name asc", 0, 0), ct);
        return rows.ToLookup(r => r.Str("name"));
    }
}
```

> **Fallback if Task 1 check A failed** (child fields not allowed in `get_list`): replace the body of `Children` with a per-item document fetch, 8 at a time, and read the child tables from the document instead:
>
> ```csharp
> private async Task<ILookup<string, JsonElement>> Children(List<string> names, string[] fields, CancellationToken ct)
> {
>     var table = fields[0].Contains("Item Barcode") ? "barcodes" : fields[0].Contains("UOM Conversion") ? "uoms" : "taxes";
>     var rows = new System.Collections.Concurrent.ConcurrentBag<(string Name, JsonElement Row)>();
>     await Parallel.ForEachAsync(names, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (name, token) =>
>     {
>         var doc = await ctx.Erp.GetDocAsync("Item", name, token);
>         foreach (var child in doc.Rows(table))
>         {
>             var merged = new Dictionary<string, object?> { ["name"] = name };
>             foreach (var p in child.EnumerateObject()) merged[p.Name == "uom" && table == "barcodes" ? "barcode_uom" : p.Name] = p.Value.Clone();
>             rows.Add((name, JsonSerializer.SerializeToElement(merged)));
>         }
>     });
>     return rows.ToLookup(x => x.Name, x => x.Row);
> }
> ```
>
> The `ItemFeed` test then needs `erp.Docs[("Item","WATER")]` with `barcodes`/`uoms`/`taxes` arrays instead of the `Override`.

`tillpos/src/TillPOS.Sync/Feeds/ItemPriceFeed.cs`:

```csharp
using TillPOS.Erp.Mapping;

namespace TillPOS.Sync.Feeds;

/// <summary>All changed Item Prices; keeps rows of the POS Profile's price list that are not customer- or
/// batch-specific, and deletes any other changed row locally (e.g. a price moved to another list).</summary>
public sealed class ItemPriceFeed(SyncContext ctx) : ISyncFeed
{
    private static readonly string[] Fields =
        ["item_code", "uom", "price_list_rate", "valid_from", "valid_upto", "price_list", "customer", "batch_no"];

    public string Name => "Item Price";

    public Task<int> RunAsync(CancellationToken ct)
    {
        var settings = ctx.Store.LoadPosSettings()
            ?? throw new InvalidOperationException("POS Profile has not been synced yet, so the price list is unknown.");

        return ctx.Pager.PullAsync("Item Price", "Item Price", Fields, [], page =>
        {
            var keep = page.Where(r => r.StrOrNull("price_list") == settings.PriceList
                                       && r.StrOrNull("customer") is null
                                       && r.StrOrNull("batch_no") is null).ToList();
            var keepNames = keep.Select(r => r.Str("name")).ToHashSet();
            ctx.Store.UpsertPrices(keep.Select(CatalogMapper.Price));
            foreach (var name in page.Select(r => r.Str("name")).Where(n => !keepNames.Contains(n)))
                ctx.Store.DeleteDocument("Item Price", name);
            return Task.CompletedTask;
        }, ct: ct);
    }
}
```

`tillpos/src/TillPOS.Sync/Feeds/DeletionFeed.cs`:

```csharp
using TillPOS.Erp.Mapping;

namespace TillPOS.Sync.Feeds;

public sealed class DeletionFeed(SyncContext ctx) : ISyncFeed
{
    private static readonly string[] Tracked = ["Item", "Item Price", "Pricing Rule", "Item Tax Template", "Sales Taxes and Charges Template"];

    public string Name => "Deleted Document";

    public Task<int> RunAsync(CancellationToken ct) =>
        ctx.Pager.PullAsync("Deleted Document", "Deleted Document", ["deleted_doctype", "deleted_name"],
            [["deleted_doctype", "in", Tracked]], page =>
            {
                foreach (var r in page) ctx.Store.DeleteDocument(r.Str("deleted_doctype"), r.Str("deleted_name"));
                return Task.CompletedTask;
            }, ct: ct);
}
```

`tillpos/src/TillPOS.Sync/Feeds/ReconcileFeed.cs`:

```csharp
using System.Globalization;
using TillPOS.Erp;
using TillPOS.Erp.Mapping;

namespace TillPOS.Sync.Feeds;

/// <summary>Once a day, compares local item codes and Retail price names with the server and removes
/// anything the server no longer has (catches renames, which leave no Deleted Document).</summary>
public sealed class ReconcileFeed(SyncContext ctx, Func<DateTimeOffset> now) : ISyncFeed
{
    private const string Key = "reconciled_at";

    public string Name => "Reconcile";

    public async Task<int> RunAsync(CancellationToken ct)
    {
        if (ctx.Store.GetValue(Key) is { } last
            && now() - DateTimeOffset.Parse(last, CultureInfo.InvariantCulture) < TimeSpan.FromHours(24))
            return 0;

        var settings = ctx.Store.LoadPosSettings()
            ?? throw new InvalidOperationException("POS Profile has not been synced yet, so the price list is unknown.");

        var remoteItems = await Names(new ListQuery("Item", ["name"], [], "name asc", 0, 0), ct);
        var localItems = ctx.Store.AllItemCodes();
        if (remoteItems.Count == 0 && localItems.Count > 0)
            throw new InvalidOperationException("Server returned no items; refusing to delete the whole local catalog.");

        var removed = 0;
        foreach (var code in localItems.Where(c => !remoteItems.Contains(c)))
        {
            ctx.Store.DeleteDocument("Item", code);
            removed++;
        }

        var remotePrices = await Names(new ListQuery("Item Price", ["name"], [["price_list", "=", settings.PriceList]], "name asc", 0, 0), ct);
        foreach (var name in ctx.Store.AllPriceNames().Where(n => !remotePrices.Contains(n)))
        {
            ctx.Store.DeleteDocument("Item Price", name);
            removed++;
        }

        ctx.Store.SetValue(Key, now().ToString("O", CultureInfo.InvariantCulture));
        return removed;
    }

    private async Task<HashSet<string>> Names(ListQuery q, CancellationToken ct) =>
        (await ctx.Erp.GetListAsync(q, ct)).Select(r => r.Str("name")).ToHashSet();
}
```

`tillpos/src/TillPOS.Sync/CatalogPuller.cs`:

```csharp
using System.Diagnostics;
using TillPOS.Core.Catalog;
using TillPOS.Core.Pricing;
using TillPOS.Erp.Mapping;
using TillPOS.Sync.Feeds;

namespace TillPOS.Sync;

public sealed record FeedResult(string Feed, int Rows, TimeSpan Duration, string? Error);

public sealed record PullReport(IReadOnlyList<FeedResult> Feeds)
{
    public bool Ok => Feeds.All(f => f.Error is null);
}

/// <summary>Runs every feed in order. A failing feed is reported and the rest still run;
/// afterPull (e.g. SqliteCatalog.Reload) always runs at the end.</summary>
public sealed class CatalogPuller(IReadOnlyList<ISyncFeed> feeds, Action afterPull)
{
    public async Task<PullReport> RunAsync(CancellationToken ct = default)
    {
        var results = new List<FeedResult>();
        foreach (var feed in feeds)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                results.Add(new FeedResult(feed.Name, await feed.RunAsync(ct), sw.Elapsed, null));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                results.Add(new FeedResult(feed.Name, 0, sw.Elapsed, ex.Message));
            }
        }
        afterPull();
        return new PullReport(results);
    }

    public static CatalogPuller CreateDefault(SyncContext ctx, Action afterPull, Func<DateTimeOffset>? now = null) => new(
    [
        new PosProfileFeed(ctx),
        new ItemGroupFeed(ctx),
        new DocFeed<ItemTaxTemplate>(ctx, "Item Tax Template", CatalogMapper.ItemTaxTemplate, ctx.Store.UpsertItemTaxTemplate, ctx.Store.DeleteItemTaxTemplate),
        new DocFeed<SalesTaxTemplate>(ctx, "Sales Taxes and Charges Template", CatalogMapper.SalesTaxTemplate, ctx.Store.UpsertSalesTaxTemplate, ctx.Store.DeleteSalesTaxTemplate),
        new DocFeed<PricingRule>(ctx, "Pricing Rule", CatalogMapper.PricingRule, ctx.Store.UpsertPricingRule, ctx.Store.DeletePricingRule),
        new ItemFeed(ctx),
        new ItemPriceFeed(ctx),
        new DeletionFeed(ctx),
        new ReconcileFeed(ctx, now ?? (() => DateTimeOffset.UtcNow)),
    ], afterPull);
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test --filter "CatalogMapperTests|FeedTests"`
Expected: PASS (18 tests). Then `dotnet test` — everything passes.

- [ ] **Step 7: Commit**

```powershell
git add tillpos
git commit -m "feat(sync): catalog feeds, ERPNext mapping and puller"
```

---

### Task 12: SyncCli and end-to-end verification against the test site

**Files:**
- Create: `tillpos/tools/TillPOS.SyncCli/CliConfig.cs`
- Modify: `tillpos/tools/TillPOS.SyncCli/Program.cs` (replace template)
- Create (local only, git-ignored): `tillpos/tools/TillPOS.SyncCli/tillpos.cli.json`
- Modify: `docs/erp-api-notes.md` (append results)

**Interfaces:**
- Consumes: everything above.
- Produces: `TillPOS.SyncCli.exe` with commands `pull`, `scan <barcode> [more…]`, `basket <barcode> [more…]`, `parity <barcode> [more…]`.

- [ ] **Step 1: Write the config type**

`tillpos/tools/TillPOS.SyncCli/CliConfig.cs`:

```csharp
using System.Text.Json;
using TillPOS.Core.Money;

namespace TillPOS.SyncCli;

public sealed record CliConfig(
    string BaseUrl,
    string ApiKey,
    string ApiSecret,
    string PosProfile,
    string DbPath = "tillpos-cli.db",
    int Precision = 2,
    RoundingMethod Rounding = RoundingMethod.Bankers,
    bool AllowWrites = false)
{
    public static CliConfig Load(string path) =>
        JsonSerializer.Deserialize<CliConfig>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })
        ?? throw new InvalidOperationException($"Could not read {path}.");
}
```

- [ ] **Step 2: Write the program**

`tillpos/tools/TillPOS.SyncCli/Program.cs`:

```csharp
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
        var money = new MoneySettings(config.Precision, config.Rounding, settings.SmallestCurrencyFraction, settings.DisableRoundedTotal);
        var template = settings.TaxesAndCharges is null ? null : catalog.FindSalesTaxTemplate(settings.TaxesAndCharges);
        var cart = new Cart(new SaleContext(catalog, money, settings.PriceList, settings.Warehouse, settings.TaxCategory, template,
            () => DateOnly.FromDateTime(DateTime.Now)));

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
```

Add to `tillpos/tools/TillPOS.SyncCli/TillPOS.SyncCli.csproj` inside a new `<ItemGroup>` so the config is copied next to the exe:

```xml
<ItemGroup>
  <None Update="tillpos.cli.json" CopyToOutputDirectory="PreserveNewest" Condition="Exists('tillpos.cli.json')" />
</ItemGroup>
```

- [ ] **Step 3: Build**

Run: `dotnet build` (from `tillpos/`)
Expected: build succeeds, 0 warnings. Run `dotnet test` — all tests still pass.

- [ ] **Step 4: Configure against the TEST site**

Create `tillpos/tools/TillPOS.SyncCli/tillpos.cli.json` (git-ignored; values from Task 1):

```json
{
  "BaseUrl": "http://localhost:8080/",
  "ApiKey": "<till1 api key>",
  "ApiSecret": "<till1 api secret>",
  "PosProfile": "<Till 1 POS Profile name>",
  "DbPath": "D:\\erp-test\\tillpos-cli.db",
  "Precision": 2,
  "Rounding": "Bankers",
  "AllowWrites": true
}
```

Set `"Rounding"` to the value recorded in `docs/erp-api-notes.md` row F.

- [ ] **Step 5: First full pull (success criterion S6)**

Run: `dotnet run --project tools/TillPOS.SyncCli -- pull`
Expected: every feed line shows no error; `Item` ≈ the live item count (~12,000); `TOTAL` under 300 s. Run it again: expected all incremental feeds show `0 rows` and TOTAL a few seconds.

- [ ] **Step 6: Change detection (success criterion S5)**

On the test site UI: change the Retail price of one item, add a barcode to another, disable a third, and delete an Item Price. Run `pull` again.
Expected: `Item` ≥ 2 rows, `Item Price` ≥ 1 row, `Deleted Document` ≥ 1 row; `scan` of the new barcode returns `Added`; `scan` of the disabled item's barcode returns `ItemNotSellable`.

- [ ] **Step 7: Scan speed (success criterion S1)**

Run: `dotnet run --project tools/TillPOS.SyncCli -- scan <10 real barcodes from shelves>`
Expected: every scan after the first under 100 ms.

- [ ] **Step 8: Tax parity on 5 baskets**

Run `parity` with these baskets (real barcodes from the test site):
1. 3 regular items (inclusive VAT only).
2. 2 items that have an active Pricing Rule offer + 2 without.
3. A carton barcode (other UOM), if any exist.
4. An item with a 0% / exempt Item Tax Template, if any exist.
5. 15+ items with quantities, to exercise rounding.

Run: `dotnet run --project tools/TillPOS.SyncCli -- parity <barcodes...>` for each.
Expected: every line `OK` and `draft ... deleted`. Any `MISMATCH` must be investigated and fixed in `TaxCalculator`/`Rounder` with a new failing unit test reproducing the case **before** this plan is considered done.

- [ ] **Step 9: Record results and commit**

Append to `docs/erp-api-notes.md`:

```markdown
## Plan 1 verification (date)

| Check | Result |
|---|---|
| First full pull: items / prices / seconds | |
| Incremental pull seconds (no changes) | |
| Change detection (price, barcode, disable, delete) | |
| Scan time (max of 10, excluding first) | |
| Parity basket 1–5 | |
| Unsupported pricing rules reported (names + reasons) | |
```

```powershell
git add tillpos docs/erp-api-notes.md
git commit -m "feat(cli): SyncCli with pull, scan, basket and parity; verified against test site"
```

---

## Self-review notes (completed while writing)

- **Spec coverage for this plan's scope:** §5 catalog tables (Task 8), §6.1 pull incl. deletions and paging (Tasks 10–11), §7 pricing (Tasks 4–5), §7.1 VAT/rounding/item tax (Task 6), §4 admin setup and §14 verification items (Task 1, Task 12 parity). Push (§6.2–6.3), cashiers, receipts and remote receipts, screens (§8), printing (§9), most of §10, and packaging are deliberately in Plans 2–4.
- **Cashier list and recent POS Invoices pulls** (§6.1) are left to Plan 2, where login and returns use them; the feed pattern (`DocFeed`/`KeysetPager`) built here is reused.
- Type names checked across tasks: `MoneySettings`, `SaleContext`, `AppliedRule.Label`, `CatalogStore.*`, `SyncContext`, `ListQuery` signatures are identical in every task that uses them.
