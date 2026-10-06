# TillPOS Plan 3a — Till App Core (login, sale, payment, printing, sync) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A runnable Windows till app (`TillPOS.exe`) where a cashier logs in with a PIN, opens a shift, scans and sells (incl. scale labels), takes cash/card/split payment, and gets a printed UAE tax-invoice receipt with the FTA QR code — while the catalog syncs from ERPNext in the background. Bills are stored in the outbox; uploading them is Plan 2b.

**Architecture:** View models live in a new UI-free class library `TillPOS.Presentation` (net10.0, CommunityToolkit.Mvvm without source generators) so their behaviour is unit-tested. Receipt rendering (text + ESC/POS + FTA QR) lives in `TillPOS.Printing` (net10.0, tested). `TillPOS.App` (net10.0-windows, WPF) holds only views, Windows-specific services (raw printing, DPAPI) and composition, verified manually in Task 11.

**Tech Stack:** .NET 10, WPF, CommunityToolkit.Mvvm 8.x (ObservableObject / RelayCommand / AsyncRelayCommand only), Microsoft.Data.Sqlite, System.Security.Cryptography.ProtectedData, xUnit.

**Spec:** `docs/superpowers/specs/2026-10-05-tillpos-offline-pos-design.md` (§0, §8, §9, §10, §13b) and Master Spec §2, §5, §6, §8 (hotkeys). Mockups: https://claude.ai/artifact/F4cMYKqs2xX5ctonfYCt2g

## Global Constraints

- Phase 1A till only. Core/Data/Sync/Printing/Presentation target `net10.0`; only `TillPOS.App` targets `net10.0-windows`. `TreatWarningsAsErrors` and nullable on everywhere.
- Money is `decimal`; display format `0.00#` with `CultureInfo.InvariantCulture` (live precision 3, e.g. 9.994).
- **Hotkeys follow the Master Spec:** F1 help, F2 search, F3 set quantity, F4 price check (3b), F5 hold (3b), F6 return (3b), F7 recall (3b), F11 card payment, F12 cash payment, Enter confirm, Esc back, Delete remove line. Reprint moves to Ctrl+P (3b).
- **Supervisor approval** (Master Spec §5, spec §13b.5): removing a line, voiding the bill, and **lowering a line's quantity** (a partial line void) need a supervisor PIN; every approval and every failed supervisor PIN is logged with cashier, supervisor and shift. Wrong PINs are throttled (5 failures → 60 s lockout) at login and at the supervisor prompt.
- **Cash drawer opens only when cash was taken** (Master Spec §2); never for card-only.
- Receipt = UAE simplified tax invoice (spec §9, §7.1): "TAX INVOICE" / "TAX CREDIT NOTE", company, address, TRN, receipt number, date, till, cashier, lines, totals, VAT, payments, change, footer, and the FTA TLV QR code (seller, TRN, timestamp, total, VAT) when a TRN exists.
- The till never writes to ERPNext in this plan. Completed bills only go to the local outbox.
- The current cart is saved after every change and restored after a crash or power cut (spec §10).
- Paper width 80 mm (48 columns, default) or 58 mm (32 columns), from settings.
- Settings live in `%ProgramData%\TillPOS\settings.json` (or the path in env var `TILLPOS_SETTINGS`); the API secret is stored DPAPI-protected (machine scope).

## Review Focus

1. **A barcode scan while the search box has focus** → the item is added to the bill and the scanned digits do not stay in the search box (Task 10 manual check M4; Task 5 test `Scan_burst_is_detected_regardless_of_slow_typing_before_it`).
2. **The till crashes with items on the bill** → on restart the same items and quantities are back (Task 7 test `Cart_is_restored_after_a_restart`).
3. **Printer offline when a sale completes** → the bill is still saved and the cashier is told to reprint (Task 8 test `Printer_failure_still_saves_the_bill`).
4. **Cashier tries to lower a quantity to avoid a line void** → asked for a supervisor; refused without one (Task 7 test `Lowering_a_quantity_needs_a_supervisor`).
5. **Five wrong supervisor PINs** → the prompt locks for 60 s and each failure is logged (Task 5 test `Five_wrong_supervisor_pins_lock_the_prompt`).

---

## File Structure

```
tillpos/src/TillPOS.Core/Security/PinAttemptLimiter.cs       wrong-PIN throttle
tillpos/src/TillPOS.Core/Security/Security.cs                (modify) ApprovalAction.FailedSupervisorPin
tillpos/src/TillPOS.Data/SqliteCatalog.cs                    (modify) atomic Reload (background sync safety)
tillpos/src/TillPOS.Data/ReceiptStore.cs                     (modify) CountPending
tillpos/src/TillPOS.Sync/CatalogPuller.cs                    (modify) extra feeds in CreateDefault
tillpos/src/TillPOS.Printing/   EscPos.cs, FtaQr.cs, ReceiptRenderer.cs
tillpos/src/TillPOS.Presentation/
  Abstractions.cs        INavigator, IDialogs, IClock, IReceiptOutput, TillContext
  SessionState.cs, ShellViewModel.cs, StatusViewModel.cs, Format.cs
  NumericEntry.cs, ScanBuffer.cs, SupervisorGate.cs
  LoginViewModel.cs, OpenShiftViewModel.cs, SaleViewModel.cs, PaymentViewModel.cs
tillpos/src/TillPOS.App/   (WPF)
  TillPOS.App.csproj, App.xaml(.cs), Theme.xaml, MainWindow.xaml(.cs)
  TillSettings.cs, SecretProtector.cs, RawPrinter.cs, ReceiptOutput.cs, SyncService.cs, AppHost.cs, WpfDialogs.cs, SystemClock.cs
  Views/StatusView, LoginView, OpenShiftView, SaleView, PaymentView (.xaml/.xaml.cs)
  Dialogs/PinDialog, NumberDialog (.xaml/.xaml.cs)
tillpos/tests/TillPOS.Tests/
  Core/PinAttemptLimiterTests.cs, Printing/*Tests.cs, Presentation/*Tests.cs, Presentation/Fakes.cs, Presentation/PresentationFixture.cs
```

---

### Task 1: Wrong-PIN throttle

**Files:**
- Create: `tillpos/src/TillPOS.Core/Security/PinAttemptLimiter.cs`
- Modify: `tillpos/src/TillPOS.Core/Security/Security.cs` (append enum value)
- Test: `tillpos/tests/TillPOS.Tests/Core/PinAttemptLimiterTests.cs`

**Interfaces:**
- Produces: `sealed class PinAttemptLimiter(Func<DateTimeOffset> now, int maxFailures = 5, TimeSpan? lockout = null)` with `bool IsLocked`, `TimeSpan Remaining`, `void Failed()`, `void Succeeded()`; `ApprovalAction.FailedSupervisorPin` (appended last).

- [ ] **Step 1: Write the failing tests**

`tillpos/tests/TillPOS.Tests/Core/PinAttemptLimiterTests.cs`:

```csharp
using TillPOS.Core.Security;

namespace TillPOS.Tests.Core;

public class PinAttemptLimiterTests
{
    private DateTimeOffset now = new(2026, 10, 7, 9, 0, 0, TimeSpan.FromHours(4));

    [Fact]
    public void Locks_after_five_failures_for_a_minute()
    {
        var limiter = new PinAttemptLimiter(() => now);
        for (var i = 0; i < 4; i++) limiter.Failed();
        Assert.False(limiter.IsLocked);

        limiter.Failed();

        Assert.True(limiter.IsLocked);
        Assert.Equal(TimeSpan.FromSeconds(60), limiter.Remaining);
        now = now.AddSeconds(61);
        Assert.False(limiter.IsLocked);
        Assert.Equal(TimeSpan.Zero, limiter.Remaining);
    }

    [Fact]
    public void Success_resets_the_count()
    {
        var limiter = new PinAttemptLimiter(() => now);
        for (var i = 0; i < 4; i++) limiter.Failed();
        limiter.Succeeded();
        for (var i = 0; i < 4; i++) limiter.Failed();
        Assert.False(limiter.IsLocked);
    }

    [Fact]
    public void After_a_lockout_the_count_starts_again()
    {
        var limiter = new PinAttemptLimiter(() => now);
        for (var i = 0; i < 5; i++) limiter.Failed();
        now = now.AddSeconds(61);
        for (var i = 0; i < 4; i++) limiter.Failed();
        Assert.False(limiter.IsLocked);
        limiter.Failed();
        Assert.True(limiter.IsLocked);
    }

    [Fact]
    public void Failed_supervisor_pin_is_an_approval_action() =>
        Assert.Equal("FailedSupervisorPin", ApprovalAction.FailedSupervisorPin.ToString());
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter PinAttemptLimiterTests`
Expected: build FAIL — `PinAttemptLimiter` not found.

- [ ] **Step 3: Implement**

`tillpos/src/TillPOS.Core/Security/PinAttemptLimiter.cs`:

```csharp
namespace TillPOS.Core.Security;

/// <summary>Throttles wrong PINs: after <c>maxFailures</c> consecutive failures the PIN prompt is locked for the lockout period.
/// One instance is shared by the login screen and the supervisor prompt of a till.</summary>
public sealed class PinAttemptLimiter(Func<DateTimeOffset> now, int maxFailures = 5, TimeSpan? lockout = null)
{
    private readonly TimeSpan lockFor = lockout ?? TimeSpan.FromSeconds(60);
    private int failures;
    private DateTimeOffset? lockedUntil;

    public bool IsLocked => lockedUntil is { } until && now() < until;

    public TimeSpan Remaining => IsLocked ? lockedUntil!.Value - now() : TimeSpan.Zero;

    public void Failed()
    {
        failures++;
        if (failures < maxFailures) return;
        lockedUntil = now() + lockFor;
        failures = 0;
    }

    public void Succeeded()
    {
        failures = 0;
        lockedUntil = null;
    }
}
```

In `tillpos/src/TillPOS.Core/Security/Security.cs` change the enum to:

```csharp
public enum ApprovalAction { LineVoid, BillVoid, ReturnWithoutReceipt, ReturnOverLimit, NoSaleDrawerOpen, FailedSupervisorPin }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter PinAttemptLimiterTests` → PASS (4). Then the full suite.

- [ ] **Step 5: Commit**

```powershell
git add tillpos
git commit -m "feat(core): wrong-PIN throttle and failed-supervisor-PIN audit action"
```

---

### Task 2: Background-sync safety and helpers in Data/Sync

**Files:**
- Modify: `tillpos/src/TillPOS.Data/SqliteCatalog.cs`, `tillpos/src/TillPOS.Data/ReceiptStore.cs`, `tillpos/src/TillPOS.Sync/CatalogPuller.cs`
- Test: `tillpos/tests/TillPOS.Tests/Data/ReceiptStoreTests.cs` (add), `tillpos/tests/TillPOS.Tests/Sync/CatalogPullerExtraFeedTests.cs` (new)

**Interfaces:**
- Produces: `SqliteCatalog.Reload()` swaps its cached tables in one assignment (safe while the UI thread reads); `ReceiptStore.CountPending()`; `CatalogPuller.CreateDefault(SyncContext ctx, Action afterPull, Func<DateTimeOffset>? now = null, params ISyncFeed[] extraFeeds)` (extra feeds run after the defaults).

- [ ] **Step 1: Write the failing tests**

Add to `tillpos/tests/TillPOS.Tests/Data/ReceiptStoreTests.cs`:

```csharp
    [Fact]
    public void Counts_pending_receipts()
    {
        store.Save(Sale("A"));
        store.Save(Sale("B", minute: 1));
        store.MarkSynced("A", "ACC-1");
        Assert.Equal(1, store.CountPending());
    }
```

`tillpos/tests/TillPOS.Tests/Sync/CatalogPullerExtraFeedTests.cs`:

```csharp
using TillPOS.Data;
using TillPOS.Sync;
using TillPOS.Sync.Feeds;
using TillPOS.Tests.Data;
using TillPOS.Tests.Fakes;

namespace TillPOS.Tests.Sync;

public sealed class CatalogPullerExtraFeedTests : IDisposable
{
    private readonly TempDb temp = new();

    public void Dispose() => temp.Dispose();

    private sealed class CountingFeed : ISyncFeed
    {
        public int Runs { get; private set; }
        public string Name => "Extra";
        public Task<int> RunAsync(CancellationToken ct) { Runs++; return Task.FromResult(0); }
    }

    [Fact]
    public async Task Extra_feeds_run_after_the_default_feeds()
    {
        var erp = new FakeErp();
        var ctx = new SyncContext(erp, new CatalogStore(temp.Db), new KeysetPager(erp, new InMemorySyncState()), "Test Counter");
        var extra = new CountingFeed();

        var report = await CatalogPuller.CreateDefault(ctx, () => { }, null, extra).RunAsync();

        Assert.Equal(1, extra.Runs);
        Assert.Equal("Extra", report.Feeds[^1].Feed);
        Assert.Null(report.Feeds[^1].Error);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "Counts_pending_receipts|CatalogPullerExtraFeedTests"`
Expected: build FAIL — `CountPending` and the `CreateDefault` overload don't exist.

- [ ] **Step 3: Implement**

`ReceiptStore` — add:

```csharp
    public int CountPending()
    {
        using var c = db.Open();
        return Convert.ToInt32(c.Scalar(null, "SELECT COUNT(*) FROM receipt WHERE sync_status = 'Pending'"), CultureInfo.InvariantCulture);
    }
```

`CatalogPuller.CreateDefault` — replace with:

```csharp
    public static CatalogPuller CreateDefault(SyncContext ctx, Action afterPull, Func<DateTimeOffset>? now = null, params ISyncFeed[] extraFeeds) => new(
    [
        new PosProfileFeed(ctx),
        new ItemGroupFeed(ctx),
        new DocFeed<ItemTaxTemplate>(ctx, "Item Tax Template", CatalogMapper.ItemTaxTemplate, ctx.Store.UpsertItemTaxTemplate, ctx.Store.DeleteItemTaxTemplate),
        new DocFeed<SalesTaxTemplate>(ctx, "Sales Taxes and Charges Template", CatalogMapper.SalesTaxTemplate, ctx.Store.UpsertSalesTaxTemplate, ctx.Store.DeleteSalesTaxTemplate),
        new DocFeed<PricingRule>(ctx, "Pricing Rule", d => CatalogMapper.PricingRule(d, ctx.Store.LoadPosSettings()?.Company), ctx.Store.UpsertPricingRule, ctx.Store.DeletePricingRule),
        new ItemFeed(ctx),
        new ItemPriceFeed(ctx),
        new DeletionFeed(ctx),
        new ReconcileFeed(ctx, now ?? (() => DateTimeOffset.UtcNow)),
        .. extraFeeds,
    ], afterPull);
```

`SqliteCatalog` — replace the four cache fields, `Reload`, and the four cache readers with a single immutable snapshot:

```csharp
    private sealed record CachedTables(
        IReadOnlyDictionary<string, ItemGroupNode> Groups,
        IReadOnlyDictionary<string, List<ItemTaxAssignment>> GroupTaxes,
        IReadOnlyList<PricingRule> Rules,
        IReadOnlyDictionary<string, ItemTaxTemplate> ItemTaxTemplates);

    private volatile CachedTables cache = new(
        new Dictionary<string, ItemGroupNode>(), new Dictionary<string, List<ItemTaxAssignment>>(), [], new Dictionary<string, ItemTaxTemplate>());

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
```

and the readers become:

```csharp
    public ItemGroupNode? FindGroup(string name) => cache.Groups.GetValueOrDefault(name);
    public IReadOnlyList<PricingRule> PricingRules() => cache.Rules;
    public IReadOnlyList<ItemTaxAssignment> ItemGroupTaxes(string itemGroup) =>
        cache.GroupTaxes.TryGetValue(itemGroup, out var rows) ? rows : [];
    public ItemTaxTemplate? FindItemTaxTemplate(string name) => cache.ItemTaxTemplates.GetValueOrDefault(name);
```

(Existing `CatalogStoreTests` cover Reload behaviour; the change is a refactor.)

- [ ] **Step 4: Run tests to verify they pass**

Run: the two new tests, then the full suite (all pass, 0 warnings).

- [ ] **Step 5: Commit**

```powershell
git add tillpos
git commit -m "feat: atomic catalog reload, pending count, extra sync feeds"
```

---

### Task 3: ESC/POS builder and FTA QR

**Files:**
- Create project: `tillpos/src/TillPOS.Printing` (classlib, references TillPOS.Core); add to the solution; tests reference it
- Create: `tillpos/src/TillPOS.Printing/EscPos.cs`, `tillpos/src/TillPOS.Printing/FtaQr.cs`
- Test: `tillpos/tests/TillPOS.Tests/Printing/EscPosTests.cs`, `tillpos/tests/TillPOS.Tests/Printing/FtaQrTests.cs`

**Interfaces:**
- Produces (namespace `TillPOS.Printing`): `enum Alignment : byte { Left, Center, Right }`; `sealed class EscPos` with fluent `Init()`, `Align(Alignment)`, `Bold(bool)`, `Size(bool doubleWidth, bool doubleHeight)`, `Line(string text = "")`, `Qr(string data, byte moduleSize = 6)`, `Feed(int lines)`, `Cut()`, `KickDrawer()`, `byte[] ToArray()`; `static class FtaQr` with `string Encode(string sellerName, string trn, DateTimeOffset timestamp, decimal total, decimal vat)`.

- [ ] **Step 1: Create the project**

```powershell
cd D:\XAMPP\htdocs\ERP-NEXT\tillpos
dotnet new classlib -n TillPOS.Printing -o src/TillPOS.Printing
Remove-Item src/TillPOS.Printing/Class1.cs
dotnet sln add src/TillPOS.Printing
dotnet add src/TillPOS.Printing reference src/TillPOS.Core
dotnet add tests/TillPOS.Tests reference src/TillPOS.Printing
```

Remove `<TargetFramework>`, `<Nullable>`, `<ImplicitUsings>` from the new csproj (Directory.Build.props supplies them).

- [ ] **Step 2: Write the failing tests**

`tillpos/tests/TillPOS.Tests/Printing/EscPosTests.cs`:

```csharp
using TillPOS.Printing;

namespace TillPOS.Tests.Printing;

public class EscPosTests
{
    [Fact]
    public void Init_text_and_cut_produce_the_expected_bytes() =>
        Assert.Equal(new byte[] { 0x1B, 0x40, 0x41, 0x42, 0x0A, 0x1D, 0x56, 0x42, 0x00 },
            new EscPos().Init().Line("AB").Cut().ToArray());

    [Fact]
    public void Characters_the_printer_cannot_show_become_question_marks() =>
        Assert.Equal(new byte[] { 0x3F, 0x0A }, new EscPos().Line("أ").ToArray());

    [Fact]
    public void Drawer_kick_is_esc_p_0_25_250() =>
        Assert.Equal(new byte[] { 0x1B, 0x70, 0x00, 0x19, 0xFA }, new EscPos().KickDrawer().ToArray());

    [Fact]
    public void Qr_stores_the_data_and_prints_it()
    {
        var bytes = new EscPos().Qr("ABC").ToArray();

        var store = new byte[] { 0x1D, 0x28, 0x6B, 0x06, 0x00, 0x31, 0x50, 0x30, 0x41, 0x42, 0x43 };
        Assert.True(Contains(bytes, store));
        Assert.Equal(new byte[] { 0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x51, 0x30 }, bytes[^8..]);
    }

    [Fact]
    public void Alignment_bold_and_size_commands()
    {
        Assert.Equal(new byte[] { 0x1B, 0x61, 0x01 }, new EscPos().Align(Alignment.Center).ToArray());
        Assert.Equal(new byte[] { 0x1B, 0x45, 0x01 }, new EscPos().Bold(true).ToArray());
        Assert.Equal(new byte[] { 0x1D, 0x21, 0x11 }, new EscPos().Size(true, true).ToArray());
    }

    internal static bool Contains(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return true;
        return false;
    }
}
```

`tillpos/tests/TillPOS.Tests/Printing/FtaQrTests.cs`:

```csharp
using System.Text;
using TillPOS.Printing;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Printing;

public class FtaQrTests
{
    private static Dictionary<int, string> Decode(string base64)
    {
        var bytes = Convert.FromBase64String(base64);
        var fields = new Dictionary<int, string>();
        for (var i = 0; i < bytes.Length;)
        {
            int tag = bytes[i], length = bytes[i + 1];
            fields[tag] = Encoding.UTF8.GetString(bytes, i + 2, length);
            i += 2 + length;
        }
        return fields;
    }

    [Fact]
    public void Encodes_the_five_tlv_fields()
    {
        var qr = FtaQr.Encode("AL AIN MARKETING L.L.C", "100000000000003",
            new DateTimeOffset(2026, 10, 6, 15, 30, 0, TimeSpan.FromHours(4)), M("9.994"), M("0.476"));

        var f = Decode(qr);
        Assert.Equal("AL AIN MARKETING L.L.C", f[1]);
        Assert.Equal("100000000000003", f[2]);
        Assert.Equal("2026-10-06T11:30:00Z", f[3]);
        Assert.Equal("9.994", f[4]);
        Assert.Equal("0.476", f[5]);
    }

    [Fact]
    public void Two_decimal_amounts_keep_two_decimals()
    {
        var f = Decode(FtaQr.Encode("S", "1", DateTimeOffset.UnixEpoch, 10m, M("0.48")));
        Assert.Equal("10.00", f[4]);
        Assert.Equal("0.48", f[5]);
    }

    [Fact]
    public void A_field_longer_than_255_bytes_is_refused() =>
        Assert.Throws<ArgumentException>(() => FtaQr.Encode(new string('A', 256), "1", DateTimeOffset.UnixEpoch, 1m, 0m));
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test --filter "EscPosTests|FtaQrTests"` → build FAIL (types missing).

- [ ] **Step 4: Implement**

`tillpos/src/TillPOS.Printing/EscPos.cs`:

```csharp
namespace TillPOS.Printing;

public enum Alignment : byte { Left = 0, Center = 1, Right = 2 }

/// <summary>Builds a raw ESC/POS byte stream for 80/58 mm thermal printers. Text is printable ASCII;
/// any other character prints as '?' (printer code pages differ; item names are English on live).</summary>
public sealed class EscPos
{
    private readonly List<byte> bytes = [];

    public EscPos Init() => Raw(0x1B, 0x40);
    public EscPos Align(Alignment alignment) => Raw(0x1B, 0x61, (byte)alignment);
    public EscPos Bold(bool on) => Raw(0x1B, 0x45, on ? (byte)1 : (byte)0);
    public EscPos Size(bool doubleWidth, bool doubleHeight) =>
        Raw(0x1D, 0x21, (byte)((doubleWidth ? 0x10 : 0) | (doubleHeight ? 0x01 : 0)));

    public EscPos Line(string text = "")
    {
        foreach (var ch in text) bytes.Add(ch is >= ' ' and <= '~' ? (byte)ch : (byte)'?');
        bytes.Add(0x0A);
        return this;
    }

    /// <summary>QR code (model 2, error correction M) via GS ( k.</summary>
    public EscPos Qr(string data, byte moduleSize = 6)
    {
        var payload = System.Text.Encoding.ASCII.GetBytes(data);
        var length = payload.Length + 3;
        Raw(0x1D, 0x28, 0x6B, 0x04, 0x00, 0x31, 0x41, 0x32, 0x00);
        Raw(0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x43, moduleSize);
        Raw(0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x45, 0x31);
        Raw(0x1D, 0x28, 0x6B, (byte)(length % 256), (byte)(length / 256), 0x31, 0x50, 0x30);
        bytes.AddRange(payload);
        return Raw(0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x51, 0x30);
    }

    public EscPos Feed(int lines) => Raw(0x1B, 0x64, (byte)Math.Clamp(lines, 0, 255));
    public EscPos Cut() => Raw(0x1D, 0x56, 0x42, 0x00);
    /// <summary>ESC p 0 25 250 — pulse on drawer pin 2.</summary>
    public EscPos KickDrawer() => Raw(0x1B, 0x70, 0x00, 0x19, 0xFA);

    public byte[] ToArray() => bytes.ToArray();

    private EscPos Raw(params byte[] values)
    {
        bytes.AddRange(values);
        return this;
    }
}
```

`tillpos/src/TillPOS.Printing/FtaQr.cs`:

```csharp
using System.Globalization;
using System.Text;

namespace TillPOS.Printing;

/// <summary>TLV Base64 payload for the receipt QR code: 1 seller name, 2 TRN, 3 timestamp (UTC, yyyy-MM-ddTHH:mm:ssZ),
/// 4 invoice total incl. VAT, 5 VAT amount (Master Spec §3).</summary>
public static class FtaQr
{
    public static string Encode(string sellerName, string trn, DateTimeOffset timestamp, decimal total, decimal vat)
    {
        using var stream = new MemoryStream();
        Write(stream, 1, sellerName);
        Write(stream, 2, trn);
        Write(stream, 3, timestamp.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        Write(stream, 4, total.ToString("0.00#", CultureInfo.InvariantCulture));
        Write(stream, 5, vat.ToString("0.00#", CultureInfo.InvariantCulture));
        return Convert.ToBase64String(stream.ToArray());
    }

    private static void Write(Stream stream, byte tag, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > 255) throw new ArgumentException($"QR field {tag} is longer than 255 bytes.", nameof(value));
        stream.WriteByte(tag);
        stream.WriteByte((byte)bytes.Length);
        stream.Write(bytes);
    }
}
```

- [ ] **Step 5: Run tests to verify they pass, then commit**

Run: `dotnet test --filter "EscPosTests|FtaQrTests"` → PASS (8). Full suite passes.

```powershell
git add tillpos
git commit -m "feat(printing): ESC/POS builder and FTA QR payload"
```

---

### Task 4: Receipt rendering

**Files:**
- Create: `tillpos/src/TillPOS.Printing/ReceiptRenderer.cs`
- Test: `tillpos/tests/TillPOS.Tests/Printing/ReceiptRendererTests.cs`

**Interfaces:**
- Consumes: `Receipt`, `ReceiptLine`, `ReceiptKind` (Core), `EscPos`, `FtaQr`.
- Produces: `enum PaperWidth { Mm80 = 48, Mm58 = 32 }`; `record ReceiptHeader(string CompanyName, string? Address, string? Trn, string TillName, string? Footer)`; `static class ReceiptRenderer` with `IReadOnlyList<string> TextLines(Receipt, ReceiptHeader, PaperWidth)` and `byte[] EscPosBytes(Receipt, ReceiptHeader, PaperWidth, bool openDrawer)`.

- [ ] **Step 1: Write the failing tests**

`tillpos/tests/TillPOS.Tests/Printing/ReceiptRendererTests.cs`:

```csharp
using TillPOS.Core.Sales;
using TillPOS.Printing;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Printing;

public class ReceiptRendererTests
{
    private static readonly ReceiptHeader Header = new("AL AIN MARKETING L.L.C", "Shop 4, Al Quoz, Ajman", "100000000000003", "Till 2",
        "Thank you for shopping with us");

    private static Receipt Sale(ReceiptKind kind = ReceiptKind.Sale, string? returnAgainst = null) => new(
        "TILL2-20261006153000-000001", kind, returnAgainst, "S1", "simran", new DateTimeOffset(2026, 10, 6, 15, 30, 0, TimeSpan.FromHours(4)),
        [
            new ReceiptLine(1, "MILK", "FULL CREAM MILK 1L", "111", "PCS", 1m, 2m, M("6.79"), M("6.79"), M("13.580"), null, null, false, null),
            new ReceiptLine(2, "000089", "CUCUMBER/KIYAR", "2000089007400", "Kg", 1m, M("0.740"), M("3.50"), M("3.50"), M("2.590"), null, null, true, null),
        ],
        M("16.170"), M("15.400"), M("0.770"), M("16.170"), true, M("16.250"), M("0.080"),
        [new ReceiptPayment("Cash Counter 2", 20m)], M("3.75"), M("0.080"), null);

    [Fact]
    public void Sale_receipt_has_the_tax_invoice_parts()
    {
        var text = string.Join("\n", ReceiptRenderer.TextLines(Sale(), Header, PaperWidth.Mm80));

        Assert.Contains("TAX INVOICE", text);
        Assert.Contains("AL AIN MARKETING L.L.C", text);
        Assert.Contains("TRN: 100000000000003", text);
        Assert.Contains("TILL2-20261006153000-000001", text);
        Assert.Contains("FULL CREAM MILK 1L", text);
        Assert.Contains("  2 x 6.79", text);
        Assert.Contains("  0.740 Kg x 3.50", text);
        Assert.Contains("13.58", text);
        Assert.Contains("2.59", text);
        Assert.Contains("VAT included", text);
        Assert.Contains("Amount due", text);
        Assert.Contains("16.25", text);
        Assert.Contains("Change", text);
        Assert.Contains("Thank you for shopping with us", text);
    }

    [Theory]
    [InlineData(PaperWidth.Mm80, 48)]
    [InlineData(PaperWidth.Mm58, 32)]
    public void No_line_is_wider_than_the_paper(PaperWidth paper, int columns) =>
        Assert.All(ReceiptRenderer.TextLines(Sale(), Header, paper), line => Assert.True(line.Length <= columns, line));

    [Fact]
    public void Return_receipt_is_a_tax_credit_note_referencing_the_sale()
    {
        var text = string.Join("\n", ReceiptRenderer.TextLines(Sale(ReceiptKind.Return, "TILL2-20261006120000-000000"), Header, PaperWidth.Mm80));
        Assert.Contains("TAX CREDIT NOTE", text);
        Assert.Contains("TILL2-20261006120000-000000", text);
        Assert.DoesNotContain("TAX INVOICE", text);
    }

    [Fact]
    public void Escpos_output_has_qr_cut_and_drawer_only_when_asked()
    {
        var withDrawer = ReceiptRenderer.EscPosBytes(Sale(), Header, PaperWidth.Mm80, openDrawer: true);
        var without = ReceiptRenderer.EscPosBytes(Sale(), Header, PaperWidth.Mm80, openDrawer: false);

        Assert.True(EscPosTests.Contains(withDrawer, [0x1D, 0x28, 0x6B]));
        Assert.True(EscPosTests.Contains(without, [0x1D, 0x56, 0x42, 0x00]));
        Assert.Equal(new byte[] { 0x1B, 0x70, 0x00, 0x19, 0xFA }, withDrawer[^5..]);
        Assert.False(EscPosTests.Contains(without, [0x1B, 0x70, 0x00, 0x19, 0xFA]));
    }

    [Fact]
    public void Without_a_trn_there_is_no_qr_code() =>
        Assert.False(EscPosTests.Contains(ReceiptRenderer.EscPosBytes(Sale(), Header with { Trn = null }, PaperWidth.Mm80, false),
            [0x1D, 0x28, 0x6B]));
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter ReceiptRendererTests` → build FAIL.

- [ ] **Step 3: Implement**

`tillpos/src/TillPOS.Printing/ReceiptRenderer.cs`:

```csharp
using System.Globalization;
using TillPOS.Core.Sales;

namespace TillPOS.Printing;

public enum PaperWidth { Mm80 = 48, Mm58 = 32 }

public sealed record ReceiptHeader(string CompanyName, string? Address, string? Trn, string TillName, string? Footer);

/// <summary>UAE simplified tax invoice / tax credit note layout (spec §9), as plain text lines and as ESC/POS bytes.</summary>
public static class ReceiptRenderer
{
    public static IReadOnlyList<string> TextLines(Receipt r, ReceiptHeader h, PaperWidth paper)
    {
        var w = (int)paper;
        var lines = new List<string>();
        lines.AddRange(Wrap(h.CompanyName, w).Select(x => Center(x, w)));
        if (h.Address is { } address) lines.AddRange(Wrap(address, w).Select(x => Center(x, w)));
        if (h.Trn is { } trn) lines.Add(Center("TRN: " + trn, w));
        lines.Add(Center(r.Kind == ReceiptKind.Return ? "TAX CREDIT NOTE" : "TAX INVOICE", w));
        lines.Add(Pair("No.", r.ClientId, w));
        if (r.ReturnAgainst is { } original) lines.Add(Pair("Return of", original, w));
        lines.Add(Pair(r.CreatedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), h.TillName, w));
        lines.Add(Pair("Cashier", r.Cashier, w));
        lines.Add(new string('-', w));
        foreach (var line in r.Lines)
        {
            lines.AddRange(Wrap(line.ItemName, w));
            lines.Add(Pair($"  {Qty(line)} x {Money(line.Rate)}", Money(line.Amount), w));
        }
        lines.Add(new string('-', w));
        lines.Add(Pair("Total incl. VAT", Money(r.GrandTotal), w));
        lines.Add(Pair("VAT included", Money(r.TotalTaxes), w));
        if (r.UsesErpRoundedTotal && r.RoundingAdjustment != 0m)
        {
            lines.Add(Pair("Rounding", Money(r.RoundingAdjustment), w));
            lines.Add(Pair("Amount due", Money(r.RoundedTotal), w));
        }
        foreach (var payment in r.Payments) lines.Add(Pair(payment.ModeOfPayment, Money(payment.Amount), w));
        if (r.Change != 0m) lines.Add(Pair("Change", Money(r.Change), w));
        lines.Add(new string('-', w));
        if (h.Footer is { } footer) lines.AddRange(Wrap(footer, w).Select(x => Center(x, w)));
        return lines;
    }

    public static byte[] EscPosBytes(Receipt r, ReceiptHeader h, PaperWidth paper, bool openDrawer)
    {
        var printer = new EscPos().Init().Align(Alignment.Left);
        foreach (var line in TextLines(r, h, paper)) printer.Line(line);
        if (h.Trn is { } trn)
        {
            var total = r.UsesErpRoundedTotal ? r.RoundedTotal : r.GrandTotal;
            printer.Align(Alignment.Center).Qr(FtaQr.Encode(h.CompanyName, trn, r.CreatedAt, total, r.TotalTaxes)).Align(Alignment.Left);
        }
        printer.Feed(3).Cut();
        if (openDrawer) printer.KickDrawer();
        return printer.ToArray();
    }

    private static string Money(decimal value) => value.ToString("0.00#", CultureInfo.InvariantCulture);

    private static string Qty(ReceiptLine line) =>
        line.FromScaleLabel || line.Qty != decimal.Truncate(line.Qty)
            ? $"{line.Qty.ToString("0.000", CultureInfo.InvariantCulture)} {line.Uom}"
            : line.Qty.ToString("0", CultureInfo.InvariantCulture);

    private static string Pair(string left, string right, int width)
    {
        if (right.Length >= width) return right[..width];
        var room = width - right.Length - 1;
        if (left.Length > room) left = left[..room];
        return left + new string(' ', width - left.Length - right.Length) + right;
    }

    private static string Center(string text, int width) =>
        text.Length >= width ? text[..width] : new string(' ', (width - text.Length) / 2) + text;

    private static IEnumerable<string> Wrap(string text, int width)
    {
        var line = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var piece = word.Length > width ? word[..width] : word;
            if (line.Length == 0) line = piece;
            else if (line.Length + 1 + piece.Length <= width) line += " " + piece;
            else { yield return line; line = piece; }
        }
        if (line.Length > 0) yield return line;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass, then commit**

Run: `dotnet test --filter ReceiptRendererTests` → PASS (6). Full suite passes.

```powershell
git add tillpos
git commit -m "feat(printing): UAE tax invoice receipt layout"
```

---

### Task 5: Presentation foundations (shell, scanner, numeric entry, supervisor gate)

**Files:**
- Create project: `tillpos/src/TillPOS.Presentation` (classlib; references Core, Data, Sync; package `CommunityToolkit.Mvvm`); add to the solution; tests reference it
- Create: `Abstractions.cs`, `SessionState.cs`, `ShellViewModel.cs`, `StatusViewModel.cs`, `Format.cs`, `NumericEntry.cs`, `ScanBuffer.cs`, `SupervisorGate.cs` (all in `tillpos/src/TillPOS.Presentation/`)
- Create: `tillpos/tests/TillPOS.Tests/Presentation/Fakes.cs`, `tillpos/tests/TillPOS.Tests/Presentation/PresentationFixture.cs`
- Test: `tillpos/tests/TillPOS.Tests/Presentation/ScanBufferTests.cs`, `NumericEntryTests.cs`, `SupervisorGateTests.cs`

**Interfaces:**
- Produces (namespace `TillPOS.Presentation`):
  - `interface INavigator { void Show(object viewModel); }`
  - `interface IDialogs { Task<string?> AskPinAsync(string title, string reason); Task<decimal?> AskNumberAsync(string title, string prompt); void Info(string message); }`
  - `interface IClock { DateTimeOffset Now { get; } }`
  - `interface IReceiptOutput { void Print(Receipt receipt, bool openDrawer); }`
  - `record TillContext(int TillNumber, TenderModes Modes, Func<SaleContext> NewSaleContext, Func<string, IReadOnlyList<Item>> Search, Authenticator Authenticator, PinAttemptLimiter LoginLimiter, PinAttemptLimiter SupervisorLimiter, ShiftStore Shifts, ReceiptStore Receipts, ApprovalStore Approvals, CatalogStore Kv, IClock Clock, IReceiptOutput Output, INavigator Navigator, IDialogs Dialogs)`
  - `sealed class SessionState : ObservableObject` — `Cashier? Cashier`, `ShiftOpening? Shift`
  - `sealed class ShellViewModel : ObservableObject, INavigator` — `object? Current`, `SessionState Session`, `string ShopName`, `string TillName`, `string SyncStatus`, `bool Online`, `int PendingUploads`, `string Clock`
  - `sealed record StatusViewModel(string Message)`
  - `static class Format` — `string Money(decimal)`
  - `sealed class NumericEntry : ObservableObject` — `string Text` (validated setter), `decimal? Value`, `event Action? Changed`, `Digit(char)`, `Dot()`, `Backspace()`, `Clear()`, `Set(decimal)`
  - `sealed class ScanBuffer(Func<DateTimeOffset> now, TimeSpan? maxGap = null, int minLength = 4)` — `string? OnChar(char c)`
  - `sealed class SupervisorGate(TillContext ctx, SessionState session)` — `Task<string?> ApproveAsync(ApprovalAction action, string reason, string? receiptClientId = null, string? itemCode = null, decimal amount = 0m)`

- [ ] **Step 1: Create the project**

```powershell
cd D:\XAMPP\htdocs\ERP-NEXT\tillpos
dotnet new classlib -n TillPOS.Presentation -o src/TillPOS.Presentation
Remove-Item src/TillPOS.Presentation/Class1.cs
dotnet sln add src/TillPOS.Presentation
dotnet add src/TillPOS.Presentation reference src/TillPOS.Core src/TillPOS.Data src/TillPOS.Sync
dotnet add src/TillPOS.Presentation package CommunityToolkit.Mvvm
dotnet add tests/TillPOS.Tests reference src/TillPOS.Presentation
```

Remove `<TargetFramework>`, `<Nullable>`, `<ImplicitUsings>` from the new csproj.

- [ ] **Step 2: Write the fakes, fixture and failing tests**

`tillpos/tests/TillPOS.Tests/Presentation/Fakes.cs`:

```csharp
using TillPOS.Core.Sales;
using TillPOS.Presentation;

namespace TillPOS.Tests.Presentation;

public sealed class FakeDialogs : IDialogs
{
    public Queue<string?> Pins { get; } = new();
    public Queue<decimal?> Numbers { get; } = new();
    public List<string> Infos { get; } = [];
    public int PinRequests { get; private set; }

    public Task<string?> AskPinAsync(string title, string reason)
    {
        PinRequests++;
        return Task.FromResult(Pins.Count > 0 ? Pins.Dequeue() : null);
    }

    public Task<decimal?> AskNumberAsync(string title, string prompt) => Task.FromResult(Numbers.Count > 0 ? Numbers.Dequeue() : null);
    public void Info(string message) => Infos.Add(message);
}

public sealed class FakeNavigator : INavigator
{
    public object? Current { get; private set; }
    public void Show(object viewModel) => Current = viewModel;
}

public sealed class FakeClock(DateTimeOffset start) : IClock
{
    public DateTimeOffset Now { get; set; } = start;
}

public sealed class FakeOutput : IReceiptOutput
{
    public List<(Receipt Receipt, bool OpenDrawer)> Printed { get; } = [];
    public bool Fail { get; set; }

    public void Print(Receipt receipt, bool openDrawer)
    {
        if (Fail) throw new InvalidOperationException("Printer offline");
        Printed.Add((receipt, openDrawer));
    }
}
```

`tillpos/tests/TillPOS.Tests/Presentation/PresentationFixture.cs`:

```csharp
using TillPOS.Core.Catalog;
using TillPOS.Core.Money;
using TillPOS.Core.Sales;
using TillPOS.Core.Security;
using TillPOS.Core.Shifts;
using TillPOS.Data;
using TillPOS.Presentation;
using TillPOS.Tests.Data;
using TillPOS.Tests.Fakes;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Presentation;

public sealed class PresentationFixture : IDisposable
{
    public static readonly Cashier Simran = new("simran", "Simran", "p.simran@quickgroc.com", PinHasher.Hash("1111"), false, true);
    public static readonly Cashier Sup = new("sup", "Supervisor", "sup@quickgroc.com", PinHasher.Hash("9999"), true, true);
    private static readonly SalesTaxTemplate Vat = new("UAE VAT 5% - AAML", [new TaxRow(1, "VAT 5% - AAML", "VAT 5%", 5m, true)], null);

    public TempDb Temp { get; } = new();
    public InMemoryCatalog Catalog { get; } = InMemoryCatalog.WithStandardGroups();
    public FakeDialogs Dialogs { get; } = new();
    public FakeNavigator Navigator { get; } = new();
    public FakeClock Clock { get; } = new(new DateTimeOffset(2026, 10, 7, 10, 0, 0, TimeSpan.FromHours(4)));
    public FakeOutput Output { get; } = new();
    public SessionState Session { get; } = new();
    public TillContext Ctx { get; }

    public PresentationFixture()
    {
        Catalog.Items.Add(new Item("MILK", "Full Cream Milk 1L", "Dairy", null, "PCS", false, true));
        Catalog.Prices.Add(new ItemPrice("P-MILK", "MILK", "PCS", M("6.79"), null, null));
        Catalog.Barcodes.Add(new ItemBarcode("111", "MILK", null));
        Catalog.Items.Add(new Item("000089", "CUCUMBER/KIYAR", "Food", null, "Kg", false, true));
        Catalog.Prices.Add(new ItemPrice("P-CUC", "000089", "Kg", M("3.50"), null, null));
        Catalog.Barcodes.Add(new ItemBarcode("000089", "000089", "Kg"));

        var db = Temp.Db;
        Ctx = new TillContext(
            2, new TenderModes("Cash Counter 2", "Credit Card"),
            () => new SaleContext(Catalog, new MoneySettings(3, RoundingMethod.Bankers, 0.25m), "Standard Selling", "Stores - AAML",
                null, Vat, () => new DateOnly(2026, 10, 7)),
            text => Catalog.Items.Where(i => i.ItemName.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList(),
            new Authenticator(() => [Simran, Sup]), new PinAttemptLimiter(() => Clock.Now), new PinAttemptLimiter(() => Clock.Now),
            new ShiftStore(db), new ReceiptStore(db), new ApprovalStore(db), new CatalogStore(db),
            Clock, Output, Navigator, Dialogs);
    }

    public void LogInWithOpenShift()
    {
        Session.Cashier = Simran;
        var shift = new ShiftOpening("TILL2-SHIFT-20261007080000", "simran", Clock.Now.AddHours(-2), [new ReceiptPayment("Cash Counter 2", 200m)]);
        Ctx.Shifts.Open(shift);
        Session.Shift = shift;
    }

    public void Dispose() => Temp.Dispose();
}
```

`tillpos/tests/TillPOS.Tests/Presentation/ScanBufferTests.cs`:

```csharp
using TillPOS.Presentation;

namespace TillPOS.Tests.Presentation;

public class ScanBufferTests
{
    private DateTimeOffset now = new(2026, 10, 7, 10, 0, 0, TimeSpan.Zero);

    private string? Type(ScanBuffer buffer, string text, int msBetween)
    {
        string? result = null;
        foreach (var c in text)
        {
            now = now.AddMilliseconds(msBetween);
            result = buffer.OnChar(c) ?? result;
        }
        return result;
    }

    [Fact]
    public void Fast_burst_ending_in_enter_is_a_scan() =>
        Assert.Equal("6291105656948", Type(new ScanBuffer(() => now), "6291105656948\r", 10));

    [Fact]
    public void Slow_typing_is_not_a_scan() =>
        Assert.Null(Type(new ScanBuffer(() => now), "1234\r", 200));

    [Fact]
    public void Too_short_burst_is_not_a_scan() =>
        Assert.Null(Type(new ScanBuffer(() => now), "12\r", 10));

    [Fact]
    public void Scan_burst_is_detected_regardless_of_slow_typing_before_it()
    {
        var buffer = new ScanBuffer(() => now);
        Assert.Null(Type(buffer, "mil", 250));
        now = now.AddMilliseconds(300);
        Assert.Equal("2000089007400", Type(buffer, "2000089007400\r", 8));
    }

    [Fact]
    public void Enter_long_after_the_burst_is_not_a_scan()
    {
        var buffer = new ScanBuffer(() => now);
        Type(buffer, "6291105656948", 10);
        now = now.AddMilliseconds(500);
        Assert.Null(buffer.OnChar('\r'));
    }
}
```

`tillpos/tests/TillPOS.Tests/Presentation/NumericEntryTests.cs`:

```csharp
using TillPOS.Presentation;
using static TillPOS.Tests.TestUtil;

namespace TillPOS.Tests.Presentation;

public class NumericEntryTests
{
    [Fact]
    public void Digits_dot_and_backspace()
    {
        var e = new NumericEntry();
        foreach (var c in "025") e.Digit(c);
        e.Dot();
        e.Digit('5');
        Assert.Equal("25.5", e.Text);
        Assert.Equal(M("25.5"), e.Value);
        e.Backspace();
        e.Backspace();
        Assert.Equal("25", e.Text);
    }

    [Fact]
    public void At_most_three_decimals_and_one_dot()
    {
        var e = new NumericEntry();
        e.Digit('1');
        e.Dot();
        e.Dot();
        foreach (var c in "2345") e.Digit(c);
        Assert.Equal("1.234", e.Text);
    }

    [Fact]
    public void Typed_text_is_validated()
    {
        var e = new NumericEntry { Text = "12.50" };
        e.Text = "12a";
        Assert.Equal("12.50", e.Text);
        e.Text = "";
        Assert.Null(e.Value);
    }

    [Fact]
    public void Set_and_change_notification()
    {
        var e = new NumericEntry();
        var changes = 0;
        e.Changed += () => changes++;
        e.Set(50m);
        Assert.Equal("50", e.Text);
        Assert.Equal(1, changes);
    }
}
```

`tillpos/tests/TillPOS.Tests/Presentation/SupervisorGateTests.cs`:

```csharp
using TillPOS.Core.Security;
using TillPOS.Presentation;

namespace TillPOS.Tests.Presentation;

public sealed class SupervisorGateTests : IDisposable
{
    private readonly PresentationFixture f = new();
    private readonly SupervisorGate gate;

    public SupervisorGateTests()
    {
        f.LogInWithOpenShift();
        gate = new SupervisorGate(f.Ctx, f.Session);
    }

    public void Dispose() => f.Dispose();

    [Fact]
    public async Task Supervisor_pin_approves_and_logs_who_asked_and_who_approved()
    {
        f.Dialogs.Pins.Enqueue("9999");

        var approver = await gate.ApproveAsync(ApprovalAction.LineVoid, "Remove Milk", null, "MILK", 6.79m);

        Assert.Equal("sup", approver);
        var record = Assert.Single(f.Ctx.Approvals.Unsynced());
        Assert.Equal(ApprovalAction.LineVoid, record.Action);
        Assert.Equal("simran", record.CashierId);
        Assert.Equal("sup", record.SupervisorId);
        Assert.Equal("TILL2-SHIFT-20261007080000", record.ShiftClientId);
    }

    [Fact]
    public async Task Cashier_pin_is_refused_and_the_failure_is_logged()
    {
        f.Dialogs.Pins.Enqueue("1111");

        Assert.Null(await gate.ApproveAsync(ApprovalAction.BillVoid, "Void bill"));

        Assert.Equal(ApprovalAction.FailedSupervisorPin, Assert.Single(f.Ctx.Approvals.Unsynced()).Action);
    }

    [Fact]
    public async Task Cancelled_prompt_approves_nothing_and_logs_nothing()
    {
        Assert.Null(await gate.ApproveAsync(ApprovalAction.BillVoid, "Void bill"));
        Assert.Empty(f.Ctx.Approvals.Unsynced());
    }

    [Fact]
    public async Task Five_wrong_supervisor_pins_lock_the_prompt()
    {
        for (var i = 0; i < 5; i++)
        {
            f.Dialogs.Pins.Enqueue("0000");
            await gate.ApproveAsync(ApprovalAction.LineVoid, "x");
        }
        f.Dialogs.Pins.Enqueue("9999");
        var asked = f.Dialogs.PinRequests;

        Assert.Null(await gate.ApproveAsync(ApprovalAction.LineVoid, "x"));

        Assert.Equal(asked, f.Dialogs.PinRequests);
        Assert.Contains(f.Dialogs.Infos, m => m.Contains("Too many wrong PINs"));
        Assert.Equal(5, f.Ctx.Approvals.Unsynced().Count);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test --filter "ScanBufferTests|NumericEntryTests|SupervisorGateTests"` → build FAIL.

- [ ] **Step 4: Implement**

`tillpos/src/TillPOS.Presentation/Abstractions.cs`:

```csharp
using TillPOS.Core.Catalog;
using TillPOS.Core.Sales;
using TillPOS.Core.Security;
using TillPOS.Data;

namespace TillPOS.Presentation;

public interface INavigator
{
    void Show(object viewModel);
}

public interface IDialogs
{
    Task<string?> AskPinAsync(string title, string reason);
    Task<decimal?> AskNumberAsync(string title, string prompt);
    void Info(string message);
}

public interface IClock
{
    DateTimeOffset Now { get; }
}

public interface IReceiptOutput
{
    /// <summary>Prints the receipt; throws if the printer fails (the bill is already saved).</summary>
    void Print(Receipt receipt, bool openDrawer);
}

/// <summary>Everything the view models need from the rest of the till, assembled once by the app.</summary>
public sealed record TillContext(
    int TillNumber,
    TenderModes Modes,
    Func<SaleContext> NewSaleContext,
    Func<string, IReadOnlyList<Item>> Search,
    Authenticator Authenticator,
    PinAttemptLimiter LoginLimiter,
    PinAttemptLimiter SupervisorLimiter,
    ShiftStore Shifts,
    ReceiptStore Receipts,
    ApprovalStore Approvals,
    CatalogStore Kv,
    IClock Clock,
    IReceiptOutput Output,
    INavigator Navigator,
    IDialogs Dialogs);
```

`tillpos/src/TillPOS.Presentation/SessionState.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using TillPOS.Core.Security;
using TillPOS.Core.Shifts;

namespace TillPOS.Presentation;

public sealed class SessionState : ObservableObject
{
    private Cashier? cashier;
    private ShiftOpening? shift;

    public Cashier? Cashier { get => cashier; set => SetProperty(ref cashier, value); }
    public ShiftOpening? Shift { get => shift; set => SetProperty(ref shift, value); }
}
```

`tillpos/src/TillPOS.Presentation/ShellViewModel.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;

namespace TillPOS.Presentation;

/// <summary>The window frame: which screen is showing, plus the header (shop, till, cashier, sync status, clock).</summary>
public sealed class ShellViewModel : ObservableObject, INavigator
{
    private object? current;
    private string shopName = "";
    private string tillName = "";
    private string syncStatus = "Starting…";
    private bool online;
    private int pendingUploads;
    private string clock = "";

    public SessionState Session { get; } = new();
    public object? Current { get => current; private set => SetProperty(ref current, value); }
    public string ShopName { get => shopName; set => SetProperty(ref shopName, value); }
    public string TillName { get => tillName; set => SetProperty(ref tillName, value); }
    public string SyncStatus { get => syncStatus; set => SetProperty(ref syncStatus, value); }
    public bool Online { get => online; set => SetProperty(ref online, value); }
    public int PendingUploads { get => pendingUploads; set => SetProperty(ref pendingUploads, value); }
    public string Clock { get => clock; set => SetProperty(ref clock, value); }

    public void Show(object viewModel) => Current = viewModel;
}
```

`tillpos/src/TillPOS.Presentation/StatusViewModel.cs`:

```csharp
namespace TillPOS.Presentation;

/// <summary>A full-screen message (e.g. "Downloading items from ERPNext…").</summary>
public sealed record StatusViewModel(string Message);
```

`tillpos/src/TillPOS.Presentation/Format.cs`:

```csharp
using System.Globalization;

namespace TillPOS.Presentation;

public static class Format
{
    public static string Money(decimal value) => value.ToString("0.00#", CultureInfo.InvariantCulture);
}
```

`tillpos/src/TillPOS.Presentation/NumericEntry.cs`:

```csharp
using System.Globalization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TillPOS.Presentation;

/// <summary>An amount typed on the keypad or keyboard: digits, one dot, at most 3 decimals, at most 7 whole digits.</summary>
public sealed partial class NumericEntry : ObservableObject
{
    private string text = "";

    public event Action? Changed;

    public string Text
    {
        get => text;
        set
        {
            if (!Valid().IsMatch(value)) return;
            if (!SetProperty(ref text, value)) return;
            OnPropertyChanged(nameof(Value));
            Changed?.Invoke();
        }
    }

    public decimal? Value => decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : null;

    public void Digit(char digit)
    {
        if (!char.IsAsciiDigit(digit)) return;
        Text = text == "0" ? digit.ToString() : text + digit;
    }

    public void Dot()
    {
        if (!text.Contains('.')) Text = text.Length == 0 ? "0." : text + ".";
    }

    public void Backspace()
    {
        if (text.Length > 0) Text = text[..^1];
    }

    public void Clear() => Text = "";

    public void Set(decimal value) => Text = value.ToString("0.###", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^\d{0,7}(\.\d{0,3})?$")]
    private static partial Regex Valid();
}
```

`tillpos/src/TillPOS.Presentation/ScanBuffer.cs`:

```csharp
using System.Text;

namespace TillPOS.Presentation;

/// <summary>Tells barcode-scanner input (a fast burst of characters ending in Enter) apart from a person typing,
/// whatever control has focus (Master Spec §2: scanner as keyboard wedge, &lt;50 ms between keys).</summary>
public sealed class ScanBuffer(Func<DateTimeOffset> now, TimeSpan? maxGap = null, int minLength = 4)
{
    private readonly TimeSpan gap = maxGap ?? TimeSpan.FromMilliseconds(50);
    private readonly StringBuilder buffer = new();
    private DateTimeOffset last = DateTimeOffset.MinValue;

    /// <summary>Feed every typed character (Enter as '\r'); returns the barcode when a scan completes, otherwise null.</summary>
    public string? OnChar(char c)
    {
        var time = now();
        var fast = time - last <= gap;
        last = time;

        if (c is '\r' or '\n')
        {
            var code = fast && buffer.Length >= minLength ? buffer.ToString() : null;
            buffer.Clear();
            return code;
        }

        if (!fast) buffer.Clear();
        buffer.Append(c);
        return null;
    }
}
```

`tillpos/src/TillPOS.Presentation/SupervisorGate.cs`:

```csharp
using System.Globalization;
using TillPOS.Core.Security;

namespace TillPOS.Presentation;

/// <summary>Asks for a supervisor PIN, throttles wrong PINs, and logs every approval and every failed supervisor PIN
/// with the requesting cashier and the shift (spec §13b.5).</summary>
public sealed class SupervisorGate(TillContext ctx, SessionState session)
{
    public async Task<string?> ApproveAsync(ApprovalAction action, string reason, string? receiptClientId = null, string? itemCode = null,
        decimal amount = 0m)
    {
        if (ctx.SupervisorLimiter.IsLocked)
        {
            ctx.Dialogs.Info($"Too many wrong PINs. Try again in {Math.Ceiling(ctx.SupervisorLimiter.Remaining.TotalSeconds).ToString(CultureInfo.InvariantCulture)} s.");
            return null;
        }

        var pin = await ctx.Dialogs.AskPinAsync("Supervisor approval", reason);
        if (pin is null) return null;

        var supervisor = ctx.Authenticator.Supervisor(pin);
        if (supervisor is null)
        {
            ctx.SupervisorLimiter.Failed();
            Log(ApprovalAction.FailedSupervisorPin, "", reason, receiptClientId, itemCode, amount);
            ctx.Dialogs.Info("That is not a supervisor PIN.");
            return null;
        }

        ctx.SupervisorLimiter.Succeeded();
        Log(action, supervisor.Id, reason, receiptClientId, itemCode, amount);
        return supervisor.Id;
    }

    private void Log(ApprovalAction action, string supervisorId, string reason, string? receiptClientId, string? itemCode, decimal amount) =>
        ctx.Approvals.Add(new ApprovalRecord(Guid.NewGuid().ToString("N"), action, session.Cashier?.Id ?? "", supervisorId,
            session.Shift?.ClientId ?? "", receiptClientId, itemCode, amount, reason, ctx.Clock.Now));
}
```

- [ ] **Step 5: Run tests to verify they pass, then commit**

Run: `dotnet test --filter "ScanBufferTests|NumericEntryTests|SupervisorGateTests"` → PASS (13). Full suite passes, 0 warnings.

```powershell
git add tillpos
git commit -m "feat(presentation): shell, scanner detection, numeric entry, supervisor gate"
```

---

### Task 6: Login and open-shift view models

**Files:**
- Create: `tillpos/src/TillPOS.Presentation/LoginViewModel.cs`, `tillpos/src/TillPOS.Presentation/OpenShiftViewModel.cs`
- Test: `tillpos/tests/TillPOS.Tests/Presentation/LoginViewModelTests.cs`

**Interfaces:**
- Consumes: Task 5 types; `ShiftStore`, `ShiftOpening`, `ClientIds`, `ReceiptPayment`.
- Produces: `sealed class LoginViewModel(TillContext ctx, SessionState session, Func<object> newSale)` — `string Pin`, `string PinMask`, `string Message`, `RelayCommand<string> DigitCommand`, `RelayCommand BackspaceCommand`, `RelayCommand ClearCommand`, `RelayCommand LoginCommand`, `void Login()`; `sealed class OpenShiftViewModel(TillContext ctx, SessionState session, Func<object> newSale)` — `NumericEntry OpeningCash`, `string Message`, `RelayCommand OpenCommand`, `void Open()`.

- [ ] **Step 1: Write the failing tests**

`tillpos/tests/TillPOS.Tests/Presentation/LoginViewModelTests.cs`:

```csharp
using TillPOS.Presentation;

namespace TillPOS.Tests.Presentation;

public sealed class LoginViewModelTests : IDisposable
{
    private readonly PresentationFixture f = new();
    private readonly object saleMarker = new();

    public void Dispose() => f.Dispose();

    private LoginViewModel Login() => new(f.Ctx, f.Session, () => saleMarker);

    private static void Enter(LoginViewModel vm, string pin)
    {
        foreach (var c in pin) vm.DigitCommand.Execute(c.ToString());
        vm.LoginCommand.Execute(null);
    }

    [Fact]
    public void Wrong_pin_shows_a_message_and_clears()
    {
        var vm = Login();
        Enter(vm, "4321");
        Assert.Equal("Wrong PIN.", vm.Message);
        Assert.Equal("", vm.Pin);
        Assert.Null(f.Session.Cashier);
    }

    [Fact]
    public void Five_wrong_pins_lock_login_even_for_the_right_pin()
    {
        var vm = Login();
        for (var i = 0; i < 5; i++) Enter(vm, "4321");
        Enter(vm, "1111");
        Assert.StartsWith("Too many wrong PINs", vm.Message);
        Assert.Null(f.Session.Cashier);
    }

    [Fact]
    public void Right_pin_without_an_open_shift_goes_to_open_shift()
    {
        Enter(Login(), "1111");
        Assert.Equal("simran", f.Session.Cashier!.Id);
        Assert.IsType<OpenShiftViewModel>(f.Navigator.Current);
    }

    [Fact]
    public void Right_pin_with_an_open_shift_goes_to_the_sale_screen()
    {
        f.LogInWithOpenShift();
        f.Session.Cashier = null;
        f.Session.Shift = null;

        Enter(Login(), "1111");

        Assert.Same(saleMarker, f.Navigator.Current);
        Assert.Equal("TILL2-SHIFT-20261007080000", f.Session.Shift!.ClientId);
    }

    [Fact]
    public void Pin_is_at_most_six_digits_and_masked()
    {
        var vm = Login();
        foreach (var c in "12345678") vm.DigitCommand.Execute(c.ToString());
        Assert.Equal("123456", vm.Pin);
        Assert.Equal("●●●●●●", vm.PinMask);
    }

    [Fact]
    public void Opening_a_shift_records_the_float_and_goes_to_sale()
    {
        f.Session.Cashier = PresentationFixture.Simran;
        var vm = new OpenShiftViewModel(f.Ctx, f.Session, () => saleMarker);
        vm.OpeningCash.Text = "200";

        vm.OpenCommand.Execute(null);

        var shift = f.Ctx.Shifts.Current()!;
        Assert.Equal("TILL2-SHIFT-20261007100000", shift.ClientId);
        Assert.Equal(200m, Assert.Single(shift.OpeningAmounts).Amount);
        Assert.Equal("Cash Counter 2", shift.OpeningAmounts[0].ModeOfPayment);
        Assert.Same(saleMarker, f.Navigator.Current);
    }

    [Fact]
    public void Opening_a_shift_needs_an_amount()
    {
        f.Session.Cashier = PresentationFixture.Simran;
        var vm = new OpenShiftViewModel(f.Ctx, f.Session, () => saleMarker);
        vm.OpenCommand.Execute(null);
        Assert.Null(f.Ctx.Shifts.Current());
        Assert.Equal("Enter the cash in the drawer.", vm.Message);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter LoginViewModelTests` → build FAIL.

- [ ] **Step 3: Implement**

`tillpos/src/TillPOS.Presentation/LoginViewModel.cs`:

```csharp
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TillPOS.Presentation;

public sealed class LoginViewModel : ObservableObject
{
    private readonly TillContext ctx;
    private readonly SessionState session;
    private readonly Func<object> newSale;
    private string pin = "";
    private string message = "";

    public LoginViewModel(TillContext ctx, SessionState session, Func<object> newSale)
    {
        this.ctx = ctx;
        this.session = session;
        this.newSale = newSale;
        DigitCommand = new RelayCommand<string>(d => { if (d is { Length: 1 } && char.IsAsciiDigit(d[0]) && Pin.Length < 6) Pin += d; });
        BackspaceCommand = new RelayCommand(() => { if (Pin.Length > 0) Pin = Pin[..^1]; });
        ClearCommand = new RelayCommand(() => Pin = "");
        LoginCommand = new RelayCommand(Login);
    }

    public string Pin
    {
        get => pin;
        set { if (SetProperty(ref pin, value)) OnPropertyChanged(nameof(PinMask)); }
    }

    public string PinMask => new('●', pin.Length);
    public string Message { get => message; private set => SetProperty(ref message, value); }
    public RelayCommand<string> DigitCommand { get; }
    public RelayCommand BackspaceCommand { get; }
    public RelayCommand ClearCommand { get; }
    public RelayCommand LoginCommand { get; }

    public void Login()
    {
        var typed = Pin;
        Pin = "";
        if (ctx.LoginLimiter.IsLocked)
        {
            Message = $"Too many wrong PINs — wait {Math.Ceiling(ctx.LoginLimiter.Remaining.TotalSeconds).ToString(CultureInfo.InvariantCulture)} s.";
            return;
        }

        var cashier = ctx.Authenticator.Login(typed);
        if (cashier is null)
        {
            ctx.LoginLimiter.Failed();
            Message = "Wrong PIN.";
            return;
        }

        ctx.LoginLimiter.Succeeded();
        Message = "";
        session.Cashier = cashier;
        session.Shift = ctx.Shifts.Current();
        ctx.Navigator.Show(session.Shift is null ? new OpenShiftViewModel(ctx, session, newSale) : newSale());
    }
}
```

`tillpos/src/TillPOS.Presentation/OpenShiftViewModel.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TillPOS.Core.Sales;
using TillPOS.Core.Shifts;

namespace TillPOS.Presentation;

/// <summary>Opening float → POS Opening Shift (uploaded in Plan 2b).</summary>
public sealed class OpenShiftViewModel : ObservableObject
{
    private readonly TillContext ctx;
    private readonly SessionState session;
    private readonly Func<object> newSale;
    private string message = "";

    public OpenShiftViewModel(TillContext ctx, SessionState session, Func<object> newSale)
    {
        this.ctx = ctx;
        this.session = session;
        this.newSale = newSale;
        OpenCommand = new RelayCommand(Open);
        KeyCommand = new RelayCommand<string>(key =>
        {
            if (key == ".") OpeningCash.Dot();
            else if (key == "⌫") OpeningCash.Backspace();
            else if (key is { Length: 1 }) OpeningCash.Digit(key[0]);
        });
    }

    public NumericEntry OpeningCash { get; } = new();
    public string Message { get => message; private set => SetProperty(ref message, value); }
    public RelayCommand OpenCommand { get; }
    public RelayCommand<string> KeyCommand { get; }

    public void Open()
    {
        if (OpeningCash.Value is not { } amount || amount < 0m)
        {
            Message = "Enter the cash in the drawer.";
            return;
        }
        var now = ctx.Clock.Now;
        var shift = new ShiftOpening(ClientIds.Shift(ctx.TillNumber, now), session.Cashier!.Id, now,
            [new ReceiptPayment(ctx.Modes.Cash, amount)]);
        ctx.Shifts.Open(shift);
        session.Shift = shift;
        ctx.Navigator.Show(newSale());
    }
}
```

- [ ] **Step 4: Run tests to verify they pass, then commit**

Run: `dotnet test --filter LoginViewModelTests` → PASS (7). Full suite passes.

```powershell
git add tillpos
git commit -m "feat(presentation): PIN login and open shift"
```

---

### Task 7: Sale screen view model

**Files:**
- Create: `tillpos/src/TillPOS.Presentation/SaleViewModel.cs`
- Test: `tillpos/tests/TillPOS.Tests/Presentation/SaleViewModelTests.cs`

**Interfaces:**
- Consumes: `Cart`, `AddOutcome`, `HeldLine`, `TenderKind`, Task 5 types.
- Produces:
  - `sealed record SaleLine(Guid Id, int No, string Name, string? Barcode, string Qty, string Price, string Offer, string Amount, bool FromScaleLabel)`
  - `sealed class SaleViewModel(TillContext ctx, SessionState session, SupervisorGate gate, Func<SaleViewModel, TenderKind, object> newPayment)` — `Cart Cart`, `MoneySettings Money`, `ObservableCollection<SaleLine> Lines`, `ObservableCollection<Item> SearchResults`, `SaleLine? SelectedLine`, `string ScanText`, `string SearchText`, `string ItemCount`, `string Discount`, `string Vat`, `string Total`, `string Message`, `bool MessageIsError`; methods `Scan(string)`, `AddFromSearch(string)`, `Increment(Guid)`, `Task DecrementAsync(Guid)`, `Task SetQtyAsync(Guid)`, `Task RemoveLineAsync(Guid)`, `Task VoidBillAsync()`, `Pay(TenderKind)`, `SaleCompleted(Receipt, string? printError)`; commands `ScanEnteredCommand`, `AddFromSearchCommand(string)`, `IncrementCommand(Guid)`, `DecrementCommand(Guid)`, `RemoveCommand(Guid)`, `RemoveSelectedCommand`, `SetQtySelectedCommand`, `VoidBillCommand`, `PayCashCommand`, `PayCardCommand`

- [ ] **Step 1: Write the failing tests**

`tillpos/tests/TillPOS.Tests/Presentation/SaleViewModelTests.cs`:

```csharp
using TillPOS.Core.Payments;
using TillPOS.Core.Security;
using TillPOS.Presentation;

namespace TillPOS.Tests.Presentation;

public sealed class SaleViewModelTests : IDisposable
{
    private readonly PresentationFixture f = new();
    private (SaleViewModel Sale, TenderKind Kind)? payRequest;

    public SaleViewModelTests() => f.LogInWithOpenShift();

    public void Dispose() => f.Dispose();

    private SaleViewModel NewSale() =>
        new(f.Ctx, f.Session, new SupervisorGate(f.Ctx, f.Session), (sale, kind) => { payRequest = (sale, kind); return "payment"; });

    [Fact]
    public void Scanning_adds_a_line_and_updates_totals()
    {
        var vm = NewSale();
        vm.Scan("111");

        var line = Assert.Single(vm.Lines);
        Assert.Equal("Full Cream Milk 1L", line.Name);
        Assert.Equal("6.79", vm.Total);
        Assert.Equal("Added Full Cream Milk 1L", vm.Message);
        Assert.False(vm.MessageIsError);
    }

    [Fact]
    public void Unknown_barcode_is_an_error_message()
    {
        var vm = NewSale();
        vm.Scan("999");
        Assert.True(vm.MessageIsError);
        Assert.Equal("Unknown barcode 999", vm.Message);
        Assert.Empty(vm.Lines);
    }

    [Fact]
    public void Scale_label_shows_weight_and_amount()
    {
        var vm = NewSale();
        vm.Scan("2000089007400");
        Assert.Equal("0.740 Kg", vm.Lines[0].Qty);
        Assert.Equal("2.59", vm.Lines[0].Amount);
    }

    [Fact]
    public void Scan_box_enter_scans_and_clears()
    {
        var vm = NewSale();
        vm.ScanText = "111";
        vm.ScanEnteredCommand.Execute(null);
        Assert.Single(vm.Lines);
        Assert.Equal("", vm.ScanText);
    }

    [Fact]
    public async Task Removing_a_line_needs_a_supervisor_and_is_logged()
    {
        var vm = NewSale();
        vm.Scan("111");
        f.Dialogs.Pins.Enqueue("9999");

        await vm.RemoveLineAsync(vm.Lines[0].Id);

        Assert.Empty(vm.Lines);
        var record = Assert.Single(f.Ctx.Approvals.Unsynced());
        Assert.Equal(ApprovalAction.LineVoid, record.Action);
        Assert.Equal("MILK", record.ItemCode);
    }

    [Fact]
    public async Task Without_a_supervisor_the_line_stays()
    {
        var vm = NewSale();
        vm.Scan("111");
        f.Dialogs.Pins.Enqueue("1111");

        await vm.RemoveLineAsync(vm.Lines[0].Id);

        Assert.Single(vm.Lines);
    }

    [Fact]
    public async Task Lowering_a_quantity_needs_a_supervisor()
    {
        var vm = NewSale();
        vm.Scan("111");
        vm.Increment(vm.Lines[0].Id);
        Assert.Equal("2", vm.Lines[0].Qty);

        await vm.DecrementAsync(vm.Lines[0].Id);
        Assert.Equal("2", vm.Lines[0].Qty);

        f.Dialogs.Pins.Enqueue("9999");
        await vm.DecrementAsync(vm.Lines[0].Id);
        Assert.Equal("1", vm.Lines[0].Qty);
    }

    [Fact]
    public async Task Setting_a_higher_quantity_needs_no_supervisor_but_lower_does()
    {
        var vm = NewSale();
        vm.Scan("111");
        f.Dialogs.Numbers.Enqueue(5m);
        await vm.SetQtyAsync(vm.Lines[0].Id);
        Assert.Equal("5", vm.Lines[0].Qty);
        Assert.Equal(0, f.Dialogs.PinRequests);

        f.Dialogs.Numbers.Enqueue(2m);
        await vm.SetQtyAsync(vm.Lines[0].Id);
        Assert.Equal("5", vm.Lines[0].Qty);
        Assert.Equal(1, f.Dialogs.PinRequests);
    }

    [Fact]
    public void Plus_on_a_scale_label_line_is_refused()
    {
        var vm = NewSale();
        vm.Scan("2000089007400");
        vm.Increment(vm.Lines[0].Id);
        Assert.True(vm.MessageIsError);
        Assert.Equal("0.740 Kg", vm.Lines[0].Qty);
    }

    [Fact]
    public async Task Voiding_the_bill_needs_a_supervisor()
    {
        var vm = NewSale();
        vm.Scan("111");
        f.Dialogs.Pins.Enqueue("9999");
        await vm.VoidBillAsync();
        Assert.Empty(vm.Lines);
        Assert.Equal(ApprovalAction.BillVoid, Assert.Single(f.Ctx.Approvals.Unsynced()).Action);
    }

    [Fact]
    public void Cart_is_restored_after_a_restart()
    {
        var first = NewSale();
        first.Scan("111");
        first.Scan("111");
        first.Scan("2000089007400");

        var second = NewSale();

        Assert.Equal(new[] { "2", "0.740 Kg" }, second.Lines.Select(l => l.Qty));
    }

    [Fact]
    public void Search_finds_items_and_adds_one()
    {
        var vm = NewSale();
        vm.SearchText = "cucu";
        Assert.Equal("000089", Assert.Single(vm.SearchResults).ItemCode);

        vm.AddFromSearch("000089");

        Assert.Single(vm.Lines);
        Assert.Equal("", vm.SearchText);
        Assert.Empty(vm.SearchResults);
    }

    [Fact]
    public void Pay_needs_items_and_then_opens_payment()
    {
        var vm = NewSale();
        vm.Pay(TenderKind.Cash);
        Assert.True(vm.MessageIsError);
        Assert.Null(payRequest);

        vm.Scan("111");
        vm.PayCardCommand.Execute(null);

        Assert.Equal(TenderKind.Card, payRequest!.Value.Kind);
        Assert.Equal("payment", f.Navigator.Current);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter SaleViewModelTests` → build FAIL.

- [ ] **Step 3: Implement**

`tillpos/src/TillPOS.Presentation/SaleViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TillPOS.Core.Catalog;
using TillPOS.Core.Money;
using TillPOS.Core.Payments;
using TillPOS.Core.Sales;
using TillPOS.Core.Security;

namespace TillPOS.Presentation;

public sealed record SaleLine(Guid Id, int No, string Name, string? Barcode, string Qty, string Price, string Offer, string Amount, bool FromScaleLabel);

/// <summary>The main sale screen. Removing a line, lowering a quantity and voiding the bill need a supervisor.
/// The cart is saved after every change and restored on start (power cut / crash).</summary>
public sealed class SaleViewModel : ObservableObject
{
    public const string AutosaveKey = "current_cart";
    private readonly TillContext ctx;
    private readonly SessionState session;
    private readonly SupervisorGate gate;
    private readonly Func<SaleViewModel, TenderKind, object> newPayment;
    private readonly SaleContext saleContext;
    private SaleLine? selectedLine;
    private string scanText = "";
    private string searchText = "";
    private string itemCount = "0";
    private string discount = "0.00";
    private string vat = "0.00";
    private string total = "0.00";
    private string message = "";
    private bool messageIsError;

    public SaleViewModel(TillContext ctx, SessionState session, SupervisorGate gate, Func<SaleViewModel, TenderKind, object> newPayment)
    {
        this.ctx = ctx;
        this.session = session;
        this.gate = gate;
        this.newPayment = newPayment;
        saleContext = ctx.NewSaleContext();
        Cart = new Cart(saleContext);

        ScanEnteredCommand = new RelayCommand(() => { var code = ScanText.Trim(); ScanText = ""; if (code.Length > 0) Scan(code); });
        AddFromSearchCommand = new RelayCommand<string>(code => { if (code is not null) AddFromSearch(code); });
        IncrementCommand = new RelayCommand<Guid>(Increment);
        DecrementCommand = new AsyncRelayCommand<Guid>(DecrementAsync);
        RemoveCommand = new AsyncRelayCommand<Guid>(RemoveLineAsync);
        RemoveSelectedCommand = new AsyncRelayCommand(() => SelectedLine is { } l ? RemoveLineAsync(l.Id) : Task.CompletedTask);
        SetQtySelectedCommand = new AsyncRelayCommand(() => SelectedLine is { } l ? SetQtyAsync(l.Id) : Task.CompletedTask);
        VoidBillCommand = new AsyncRelayCommand(VoidBillAsync);
        PayCashCommand = new RelayCommand(() => Pay(TenderKind.Cash));
        PayCardCommand = new RelayCommand(() => Pay(TenderKind.Card));

        RestoreAutosave();
        Refresh();
    }

    public Cart Cart { get; }
    public MoneySettings Money => saleContext.Money;
    public ObservableCollection<SaleLine> Lines { get; } = [];
    public ObservableCollection<Item> SearchResults { get; } = [];
    public SaleLine? SelectedLine { get => selectedLine; set => SetProperty(ref selectedLine, value); }
    public string ScanText { get => scanText; set => SetProperty(ref scanText, value); }
    public string ItemCount { get => itemCount; private set => SetProperty(ref itemCount, value); }
    public string Discount { get => discount; private set => SetProperty(ref discount, value); }
    public string Vat { get => vat; private set => SetProperty(ref vat, value); }
    public string Total { get => total; private set => SetProperty(ref total, value); }
    public string Message { get => message; private set => SetProperty(ref message, value); }
    public bool MessageIsError { get => messageIsError; private set => SetProperty(ref messageIsError, value); }

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
    public RelayCommand<string> AddFromSearchCommand { get; }
    public RelayCommand<Guid> IncrementCommand { get; }
    public AsyncRelayCommand<Guid> DecrementCommand { get; }
    public AsyncRelayCommand<Guid> RemoveCommand { get; }
    public AsyncRelayCommand RemoveSelectedCommand { get; }
    public AsyncRelayCommand SetQtySelectedCommand { get; }
    public AsyncRelayCommand VoidBillCommand { get; }
    public RelayCommand PayCashCommand { get; }
    public RelayCommand PayCardCommand { get; }

    public void Scan(string code)
    {
        Feedback(Cart.AddBarcode(code), code);
        Changed();
    }

    public void AddFromSearch(string itemCode)
    {
        Feedback(Cart.AddItem(itemCode), itemCode);
        SearchText = "";
        Changed();
    }

    public void Increment(Guid id)
    {
        try
        {
            Cart.Increment(id);
            Changed();
        }
        catch (InvalidOperationException ex)
        {
            Error(ex.Message);
        }
    }

    public async Task DecrementAsync(Guid id)
    {
        var line = Find(id);
        if (line.FromScaleLabel) { Error("Lines from a scale label take their quantity from the label."); return; }
        if (line.Qty <= 1m) return;
        if (await gate.ApproveAsync(ApprovalAction.LineVoid, $"Reduce {line.Item.ItemName}", null, line.Item.ItemCode, line.Rate) is null) return;
        Cart.Decrement(id);
        Changed();
    }

    public async Task SetQtyAsync(Guid id)
    {
        var line = Find(id);
        if (line.FromScaleLabel) { Error("Lines from a scale label take their quantity from the label."); return; }
        var qty = await ctx.Dialogs.AskNumberAsync("Quantity", line.Item.ItemName);
        if (qty is not { } newQty || newQty <= 0m) return;
        if (newQty < line.Qty &&
            await gate.ApproveAsync(ApprovalAction.LineVoid, $"Lower {line.Item.ItemName} to {newQty.ToString(CultureInfo.InvariantCulture)}",
                null, line.Item.ItemCode, (line.Qty - newQty) * line.Rate) is null)
            return;
        Cart.SetQty(id, newQty);
        Changed();
    }

    public async Task RemoveLineAsync(Guid id)
    {
        var line = Find(id);
        if (await gate.ApproveAsync(ApprovalAction.LineVoid, $"Remove {line.Item.ItemName}", null, line.Item.ItemCode, line.Qty * line.Rate) is null)
            return;
        Cart.Remove(id);
        Changed();
    }

    public async Task VoidBillAsync()
    {
        if (Cart.Lines.Count == 0) return;
        if (await gate.ApproveAsync(ApprovalAction.BillVoid, "Void the whole bill", null, null, Cart.Totals().GrandTotal) is null) return;
        Cart.Clear();
        Changed();
        Info("Bill voided.");
    }

    public void Pay(TenderKind kind)
    {
        if (Cart.Lines.Count == 0) { Error("Scan an item first."); return; }
        ctx.Navigator.Show(newPayment(this, kind));
    }

    /// <summary>Called by the payment screen after the bill was saved (the cart is already empty).</summary>
    public void SaleCompleted(Receipt receipt, string? printError)
    {
        ctx.Kv.SetValue(AutosaveKey, "[]");
        Refresh();
        if (printError is null) Info($"Saved {receipt.ClientId}. Change {Format.Money(receipt.Change)}");
        else Error($"Saved {receipt.ClientId}, but the printer failed ({printError}). Reprint with Ctrl+P.");
        ctx.Navigator.Show(this);
    }

    private void Feedback(AddResult result, string code)
    {
        switch (result.Outcome)
        {
            case AddOutcome.Added: Info($"Added {result.Line!.Item.ItemName}"); break;
            case AddOutcome.UnknownBarcode: Error($"Unknown barcode {code}"); break;
            case AddOutcome.InvalidScaleLabel: Error("Scale label could not be read — scan it again."); break;
            case AddOutcome.ItemNotSellable: Error("This item cannot be sold."); break;
            case AddOutcome.NoPrice: Error("No price for this item — call a supervisor."); break;
            case AddOutcome.UnsupportedTax: Error("Tax setup missing for this item — call a supervisor."); break;
            default: Error($"Cannot add {code} ({result.Outcome})."); break;
        }
    }

    private void Changed()
    {
        ctx.Kv.SetValue(AutosaveKey, JsonSerializer.Serialize(Cart.Snapshot()));
        Refresh();
    }

    private void RestoreAutosave()
    {
        if (ctx.Kv.GetValue(AutosaveKey) is not { } json) return;
        var held = JsonSerializer.Deserialize<List<HeldLine>>(json) ?? [];
        if (held.Count == 0) return;
        var failed = Cart.Restore(held);
        if (failed.Count > 0) Error($"{failed.Count} item(s) from the unfinished bill can no longer be sold.");
        else Info("Unfinished bill restored.");
    }

    private void Refresh()
    {
        var totals = Cart.Totals();
        Lines.Clear();
        for (var i = 0; i < Cart.Lines.Count; i++)
        {
            var l = Cart.Lines[i];
            var qty = l.FromScaleLabel || l.Qty != decimal.Truncate(l.Qty)
                ? $"{l.Qty.ToString("0.000", CultureInfo.InvariantCulture)} {l.Uom}"
                : l.Qty.ToString("0", CultureInfo.InvariantCulture);
            Lines.Add(new SaleLine(l.Id, i + 1, l.Item.ItemName, l.Barcode, qty, Format.Money(l.Rate), l.Rule?.Label ?? "",
                Format.Money(totals.Lines[i].Amount), l.FromScaleLabel));
        }
        ItemCount = Cart.Lines.Count.ToString(CultureInfo.InvariantCulture);
        Discount = Format.Money(Cart.DiscountSaved());
        Vat = Format.Money(totals.TotalTaxes);
        Total = Format.Money(totals.GrandTotal);
    }

    private CartLine Find(Guid id) => Cart.Lines.First(l => l.Id == id);

    private void Info(string text) { Message = text; MessageIsError = false; }

    private void Error(string text) { Message = text; MessageIsError = true; }
}
```

- [ ] **Step 4: Run tests to verify they pass, then commit**

Run: `dotnet test --filter SaleViewModelTests` → PASS (13). Full suite passes.

```powershell
git add tillpos
git commit -m "feat(presentation): sale screen with supervisor-gated voids and autosave"
```

---

### Task 8: Payment screen view model

**Files:**
- Create: `tillpos/src/TillPOS.Presentation/PaymentViewModel.cs`
- Test: `tillpos/tests/TillPOS.Tests/Presentation/PaymentViewModelTests.cs`

**Interfaces:**
- Consumes: `SaleViewModel` (Cart, Money, SaleCompleted), `PaymentCalculator`, `Tender`, `PaymentPlan`, `SaleRecorder`.
- Produces: `sealed class PaymentViewModel(TillContext ctx, SessionState session, SaleViewModel sale, TenderKind initialKind)` — `TenderKind Kind`, `NumericEntry Cash`, `NumericEntry Card`, `bool EditCard`, `PaymentPlan? Plan`, `string AmountDue`, `string Change`, `string Shortfall`, `string Message`, `bool IsSplit`, `IReadOnlyList<decimal> QuickCash` (10, 20, 50, 100, 200, 500), commands `SetKindCommand(string)`, `QuickCashCommand(decimal)`, `KeyCommand(string)`, `CompleteCommand`, `BackCommand`; `void Complete()`.

- [ ] **Step 1: Write the failing tests**

`tillpos/tests/TillPOS.Tests/Presentation/PaymentViewModelTests.cs`:

```csharp
using TillPOS.Core.Payments;
using TillPOS.Presentation;

namespace TillPOS.Tests.Presentation;

public sealed class PaymentViewModelTests : IDisposable
{
    private readonly PresentationFixture f = new();
    private readonly SaleViewModel sale;

    public PaymentViewModelTests()
    {
        f.LogInWithOpenShift();
        sale = new SaleViewModel(f.Ctx, f.Session, new SupervisorGate(f.Ctx, f.Session),
            (s, kind) => new PaymentViewModel(f.Ctx, f.Session, s, kind));
        sale.Scan("111");                                              // 6.79
    }

    public void Dispose() => f.Dispose();

    private PaymentViewModel Pay(TenderKind kind) => new(f.Ctx, f.Session, sale, kind);

    [Fact]
    public void Cash_payment_rounds_saves_prints_with_drawer_and_returns_to_sale()
    {
        var vm = Pay(TenderKind.Cash);
        vm.QuickCashCommand.Execute(20m);

        Assert.Equal("6.75", vm.AmountDue);
        Assert.Equal("13.25", vm.Change);

        vm.CompleteCommand.Execute(null);

        var receipt = Assert.Single(f.Ctx.Receipts.ListPending(10));
        Assert.Equal("simran", receipt.Cashier);
        Assert.Equal("p.simran@quickgroc.com", receipt.CashierUser);
        Assert.Equal("TILL2-SHIFT-20261007080000", receipt.ShiftClientId);
        var (printed, drawer) = Assert.Single(f.Output.Printed);
        Assert.Equal(receipt.ClientId, printed.ClientId);
        Assert.True(drawer);
        Assert.Same(sale, f.Navigator.Current);
        Assert.Empty(sale.Lines);
        Assert.Equal("[]", f.Ctx.Kv.GetValue(SaleViewModel.AutosaveKey));
    }

    [Fact]
    public void Card_payment_is_exact_and_does_not_open_the_drawer()
    {
        var vm = Pay(TenderKind.Card);
        Assert.Equal("6.79", vm.AmountDue);

        vm.CompleteCommand.Execute(null);

        Assert.False(Assert.Single(f.Output.Printed).OpenDrawer);
        Assert.Equal("Credit Card", Assert.Single(f.Ctx.Receipts.ListPending(10)).Payments[0].ModeOfPayment);
    }

    [Fact]
    public void Not_enough_cash_saves_nothing()
    {
        var vm = Pay(TenderKind.Cash);
        vm.Cash.Text = "5";

        vm.CompleteCommand.Execute(null);

        Assert.Equal("1.75", vm.Shortfall);
        Assert.Equal("Still to pay 1.75", vm.Message);
        Assert.Empty(f.Ctx.Receipts.ListPending(10));
    }

    [Fact]
    public void Split_payment_charges_card_exactly_and_rounds_cash()
    {
        var vm = Pay(TenderKind.Split);
        vm.Card.Text = "5";
        vm.Cash.Text = "2";

        Assert.Equal("6.75", vm.AmountDue);
        Assert.Equal("0.25", vm.Change);
        vm.CompleteCommand.Execute(null);

        Assert.True(Assert.Single(f.Output.Printed).OpenDrawer);
    }

    [Fact]
    public void Invalid_split_shows_why()
    {
        var vm = Pay(TenderKind.Split);
        vm.Card.Text = "10";
        Assert.Null(vm.Plan);
        Assert.StartsWith("The card part must be", vm.Message);
    }

    [Fact]
    public void Printer_failure_still_saves_the_bill()
    {
        f.Output.Fail = true;
        var vm = Pay(TenderKind.Card);

        vm.CompleteCommand.Execute(null);

        Assert.Single(f.Ctx.Receipts.ListPending(10));
        Assert.True(sale.MessageIsError);
        Assert.Contains("printer failed", sale.Message);
        Assert.Same(sale, f.Navigator.Current);
    }

    [Fact]
    public void Back_returns_to_the_sale_without_saving()
    {
        var vm = Pay(TenderKind.Cash);
        vm.BackCommand.Execute(null);
        Assert.Same(sale, f.Navigator.Current);
        Assert.Empty(f.Ctx.Receipts.ListPending(10));
        Assert.Single(sale.Lines);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter PaymentViewModelTests` → build FAIL.

- [ ] **Step 3: Implement**

`tillpos/src/TillPOS.Presentation/PaymentViewModel.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TillPOS.Core.Payments;
using TillPOS.Core.Sales;

namespace TillPOS.Presentation;

/// <summary>Payment screen: cash (rounded to the currency fraction), card (exact) or split. Completing saves the bill first,
/// then prints; the drawer opens only when cash was taken. A printer failure never loses the bill.</summary>
public sealed class PaymentViewModel : ObservableObject
{
    private readonly TillContext ctx;
    private readonly SessionState session;
    private readonly SaleViewModel sale;
    private readonly PaymentCalculator calculator;
    private readonly SaleRecorder recorder;
    private readonly decimal grandTotal;
    private TenderKind kind;
    private bool editCard;
    private PaymentPlan? plan;
    private string message = "";

    public PaymentViewModel(TillContext ctx, SessionState session, SaleViewModel sale, TenderKind initialKind)
    {
        this.ctx = ctx;
        this.session = session;
        this.sale = sale;
        calculator = new PaymentCalculator(sale.Money);
        recorder = new SaleRecorder(ctx.Receipts, ctx.TillNumber, ctx.Modes, () => ctx.Clock.Now);
        grandTotal = sale.Cart.Totals().GrandTotal;
        Cash.Changed += Recalculate;
        Card.Changed += Recalculate;

        SetKindCommand = new RelayCommand<string>(k => { if (Enum.TryParse<TenderKind>(k, out var parsed)) Kind = parsed; });
        QuickCashCommand = new RelayCommand<decimal>(amount => Cash.Set(amount));
        KeyCommand = new RelayCommand<string>(Key);
        CompleteCommand = new RelayCommand(Complete);
        BackCommand = new RelayCommand(() => ctx.Navigator.Show(sale));

        kind = initialKind;
        Recalculate();
    }

    public NumericEntry Cash { get; } = new();
    public NumericEntry Card { get; } = new();
    public IReadOnlyList<decimal> QuickCash { get; } = [10m, 20m, 50m, 100m, 200m, 500m];
    public string GrandTotal => Format.Money(grandTotal);

    public TenderKind Kind
    {
        get => kind;
        set
        {
            if (!SetProperty(ref kind, value)) return;
            OnPropertyChanged(nameof(IsSplit));
            Recalculate();
        }
    }

    public bool IsSplit => kind == TenderKind.Split;
    public bool EditCard { get => editCard; set => SetProperty(ref editCard, value); }
    public PaymentPlan? Plan { get => plan; private set => SetProperty(ref plan, value); }
    public string AmountDue => Plan is null ? "" : Format.Money(Plan.AmountDue);
    public string Change => Plan is null ? "" : Format.Money(Plan.Change);
    public string Shortfall => Plan is null ? "" : Format.Money(Plan.Shortfall);
    public string Message { get => message; private set => SetProperty(ref message, value); }

    public RelayCommand<string> SetKindCommand { get; }
    public RelayCommand<decimal> QuickCashCommand { get; }
    public RelayCommand<string> KeyCommand { get; }
    public RelayCommand CompleteCommand { get; }
    public RelayCommand BackCommand { get; }

    public void Complete()
    {
        if (Plan is not { } p) return;
        if (!p.IsComplete)
        {
            Message = $"Still to pay {Format.Money(p.Shortfall)}";
            return;
        }

        var receipt = recorder.CompleteSale(sale.Cart, p, session.Cashier!.Id, session.Shift!.ClientId, session.Cashier.User);
        string? printError = null;
        try
        {
            ctx.Output.Print(receipt, openDrawer: p.CashTendered > 0m);
        }
        catch (Exception ex)
        {
            printError = ex.Message;
        }
        sale.SaleCompleted(receipt, printError);
    }

    private void Key(string? key)
    {
        var entry = IsSplit && EditCard ? Card : Cash;
        if (key == ".") entry.Dot();
        else if (key == "⌫") entry.Backspace();
        else if (key == "C") entry.Clear();
        else if (key is { Length: 1 }) entry.Digit(key[0]);
    }

    private void Recalculate()
    {
        try
        {
            Plan = kind switch
            {
                TenderKind.Card => calculator.Plan(grandTotal, Tender.Card()),
                TenderKind.Split => calculator.Plan(grandTotal, Tender.Split(Card.Value ?? 0m, Cash.Value ?? 0m)),
                _ => calculator.Plan(grandTotal, Tender.Cash(Cash.Value ?? 0m)),
            };
            Message = "";
        }
        catch (ArgumentOutOfRangeException ex)
        {
            Plan = null;
            Message = ex.Message.Split(" (Parameter", StringSplitOptions.None)[0];
        }
        OnPropertyChanged(nameof(AmountDue));
        OnPropertyChanged(nameof(Change));
        OnPropertyChanged(nameof(Shortfall));
    }
}
```

- [ ] **Step 4: Run tests to verify they pass, then commit**

Run: `dotnet test --filter PaymentViewModelTests` → PASS (7). Full suite passes.

```powershell
git add tillpos
git commit -m "feat(presentation): payment screen (cash, card, split) with save-then-print"
```

---

### Task 9: WPF app — project, settings, printing, sync, composition

**Files:**
- Create: `tillpos/src/TillPOS.App/TillPOS.App.csproj`, `TillSettings.cs`, `SecretProtector.cs`, `RawPrinter.cs`, `ReceiptOutput.cs`, `SystemClock.cs`, `SyncService.cs`, `AppHost.cs`
- Modify: `tillpos/TillPOS.slnx` (add the project)

**Interfaces:**
- Consumes: everything above.
- Produces: `TillSettings` (JSON at `%ProgramData%\TillPOS\settings.json` or `TILLPOS_SETTINGS`), `SecretProtector.Protect/Unprotect` (DPAPI, LocalMachine), `RawPrinter.Send(string printer, byte[] data)`, `ReceiptOutput : IReceiptOutput`, `SyncService.RunAsync(CancellationToken)`, `AppHost` (`Shell`, `StartAsync()`, `NewLogin()`).

There are no unit tests in this task (Windows/UI glue); it is verified by building here and by Task 11.

- [ ] **Step 1: Project file**

`tillpos/src/TillPOS.App/TillPOS.App.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <AssemblyName>TillPOS</AssemblyName>
    <RootNamespace>TillPOS.App</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\TillPOS.Core\TillPOS.Core.csproj" />
    <ProjectReference Include="..\TillPOS.Data\TillPOS.Data.csproj" />
    <ProjectReference Include="..\TillPOS.Erp\TillPOS.Erp.csproj" />
    <ProjectReference Include="..\TillPOS.Sync\TillPOS.Sync.csproj" />
    <ProjectReference Include="..\TillPOS.Printing\TillPOS.Printing.csproj" />
    <ProjectReference Include="..\TillPOS.Presentation\TillPOS.Presentation.csproj" />
  </ItemGroup>
</Project>
```

Run: `dotnet sln add src/TillPOS.App`. (No package needed: `ProtectedData` ships with the Windows Desktop framework that `UseWPF` brings in.)

- [ ] **Step 2: Settings and secret protection**

`tillpos/src/TillPOS.App/TillSettings.cs`:

```csharp
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using TillPOS.Core.Money;
using TillPOS.Printing;

namespace TillPOS.App;

public sealed record LocalTestCashier(string Id, string Name, string Pin, bool IsSupervisor);

/// <summary>Per-till settings (settings.json). ApiSecretProtected is DPAPI-protected (see SecretProtector).
/// LocalTestCashiers are only used while ERPNext has no POS Cashier list yet (testing; removed in Plan 4).</summary>
public sealed record TillSettings(
    string BaseUrl,
    string ApiKey,
    string ApiSecretProtected,
    string PosProfile,
    int TillNumber,
    string CashMode,
    string CardMode,
    string PrinterName = "",
    PaperWidth PaperWidth = PaperWidth.Mm80,
    string? ReceiptFooter = null,
    int Precision = 3,
    RoundingMethod Rounding = RoundingMethod.Bankers,
    string DbPath = @"C:\ProgramData\TillPOS\till.db",
    int SyncIntervalSeconds = 90,
    IReadOnlyList<LocalTestCashier>? LocalTestCashiers = null)
{
    public static string DefaultPath =>
        Environment.GetEnvironmentVariable("TILLPOS_SETTINGS")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TillPOS", "settings.json");

    public static TillSettings Load(string path) =>
        JsonSerializer.Deserialize<TillSettings>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } })
        ?? throw new InvalidDataException($"{path} is empty.");
}
```

`tillpos/src/TillPOS.App/SecretProtector.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;

namespace TillPOS.App;

/// <summary>DPAPI (machine scope): the API secret in settings.json can only be decrypted on this PC.</summary>
public static class SecretProtector
{
    public static string Protect(string secret) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.LocalMachine));

    public static string Unprotect(string protectedSecret) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedSecret), null, DataProtectionScope.LocalMachine));
}
```

- [ ] **Step 3: Raw printing and receipt output**

`tillpos/src/TillPOS.App/RawPrinter.cs`:

```csharp
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace TillPOS.App;

/// <summary>Sends raw ESC/POS bytes to a Windows printer queue (no print dialog, no GDI rendering).</summary>
public static class RawPrinter
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class DocInfo
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string DocName = "TillPOS receipt";
        [MarshalAs(UnmanagedType.LPWStr)] public string? OutputFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string DataType = "RAW";
    }

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool OpenPrinter(string printerName, out IntPtr handle, IntPtr defaults);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr handle);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int StartDocPrinter(IntPtr handle, int level, [In] DocInfo info);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr handle);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr handle);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr handle);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr handle, byte[] bytes, int count, out int written);

    public static void Send(string printerName, byte[] data)
    {
        if (!OpenPrinter(printerName, out var handle, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error(), $"Printer '{printerName}' not found");
        try
        {
            if (StartDocPrinter(handle, 1, new DocInfo()) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                if (!StartPagePrinter(handle)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (!WritePrinter(handle, data, data.Length, out var written) || written != data.Length)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Printer did not accept the receipt");
                EndPagePrinter(handle);
            }
            finally
            {
                EndDocPrinter(handle);
            }
        }
        finally
        {
            ClosePrinter(handle);
        }
    }
}
```

`tillpos/src/TillPOS.App/ReceiptOutput.cs`:

```csharp
using System.IO;
using TillPOS.Core.Sales;
using TillPOS.Data;
using TillPOS.Presentation;
using TillPOS.Printing;

namespace TillPOS.App;

/// <summary>Prints to the configured thermal printer; with no printer configured (testing) the receipt text is written
/// to a file next to the database instead.</summary>
public sealed class ReceiptOutput(TillSettings settings, CatalogStore store) : IReceiptOutput
{
    public void Print(Receipt receipt, bool openDrawer)
    {
        var pos = store.LoadPosSettings() ?? throw new InvalidOperationException("POS settings are not downloaded yet.");
        var header = new ReceiptHeader(pos.CompanyName, pos.AddressText, pos.TaxId, $"Till {settings.TillNumber}", settings.ReceiptFooter);
        if (string.IsNullOrWhiteSpace(settings.PrinterName))
        {
            var folder = Path.Combine(Path.GetDirectoryName(settings.DbPath)!, "receipts");
            Directory.CreateDirectory(folder);
            File.WriteAllLines(Path.Combine(folder, receipt.ClientId + ".txt"), ReceiptRenderer.TextLines(receipt, header, settings.PaperWidth));
            return;
        }
        RawPrinter.Send(settings.PrinterName, ReceiptRenderer.EscPosBytes(receipt, header, settings.PaperWidth, openDrawer));
    }
}
```

`tillpos/src/TillPOS.App/SystemClock.cs`:

```csharp
using TillPOS.Presentation;

namespace TillPOS.App;

public sealed class SystemClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.Now;
}
```

- [ ] **Step 4: Background sync and composition**

`tillpos/src/TillPOS.App/SyncService.cs`:

```csharp
using System.Globalization;
using System.Windows.Threading;
using TillPOS.Data;
using TillPOS.Erp;
using TillPOS.Presentation;
using TillPOS.Sync;

namespace TillPOS.App;

/// <summary>Every interval: check ERPNext is reachable, pull catalog changes (incl. cashiers), and update the header.
/// Runs on a background thread; never blocks the cashier.</summary>
public sealed class SyncService(Func<CatalogPuller> newPuller, IErpClient erp, ReceiptStore receipts, ShellViewModel shell,
    Dispatcher dispatcher, TimeSpan interval)
{
    public async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval);
        do
        {
            bool online;
            string status;
            try
            {
                await erp.PingAsync(ct);
                var report = await newPuller().RunAsync(ct);
                online = true;
                status = report.Ok
                    ? $"Online · synced {DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture)}"
                    : $"Online · {report.Feeds.Count(f => f.Error is not null)} sync problem(s)";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                online = false;
                status = "Offline";
            }
            var pending = receipts.CountPending();
            await dispatcher.InvokeAsync(() =>
            {
                shell.Online = online;
                shell.SyncStatus = status;
                shell.PendingUploads = pending;
            });
        }
        while (await timer.WaitForNextTickAsync(ct));
    }
}
```

`tillpos/src/TillPOS.App/AppHost.cs`:

```csharp
using System.IO;
using System.Windows.Threading;
using TillPOS.Core.Payments;
using TillPOS.Core.Sales;
using TillPOS.Core.Security;
using TillPOS.Data;
using TillPOS.Erp;
using TillPOS.Presentation;
using TillPOS.Sync;
using TillPOS.Sync.Feeds;

namespace TillPOS.App;

/// <summary>Builds the till: database, catalog, ERPNext client, background sync and the view models.</summary>
public sealed class AppHost
{
    private readonly TillSettings settings;
    private readonly Dispatcher dispatcher;
    private readonly CatalogStore store;
    private readonly SqliteCatalog catalog;
    private readonly CashierStore cashiers;
    private readonly ErpClient erp;
    private readonly SyncContext syncContext;
    private readonly TillContext ctx;
    private readonly CancellationTokenSource stop = new();

    public AppHost(TillSettings settings, Dispatcher dispatcher, IDialogs dialogs)
    {
        this.settings = settings;
        this.dispatcher = dispatcher;
        Directory.CreateDirectory(Path.GetDirectoryName(settings.DbPath)!);
        var db = new TillDb(settings.DbPath);
        db.Migrate();
        store = new CatalogStore(db);
        catalog = new SqliteCatalog(db);
        cashiers = new CashierStore(db);
        erp = ErpClient.Create(new ErpConnection(new Uri(settings.BaseUrl), settings.ApiKey, SecretProtector.Unprotect(settings.ApiSecretProtected)),
            TimeSpan.FromSeconds(60));
        syncContext = new SyncContext(erp, store, new KeysetPager(erp, new KvSyncStateStore(store)), settings.PosProfile);

        Shell.TillName = $"Till {settings.TillNumber}";
        ctx = new TillContext(
            settings.TillNumber, new TenderModes(settings.CashMode, settings.CardMode),
            () => SaleContext.Create(catalog, store.LoadPosSettings()!, catalog.FindSalesTaxTemplate, settings.Precision, settings.Rounding,
                () => DateOnly.FromDateTime(DateTime.Now)),
            text => catalog.Search(text),
            new Authenticator(cashiers.All), new PinAttemptLimiter(() => DateTimeOffset.Now), new PinAttemptLimiter(() => DateTimeOffset.Now),
            new ShiftStore(db), new ReceiptStore(db), new ApprovalStore(db), store,
            new SystemClock(), new ReceiptOutput(settings, store), Shell, dialogs);
    }

    public ShellViewModel Shell { get; } = new();

    public async Task StartAsync()
    {
        // CatalogPuller.RunAsync reports feed failures in its PullReport instead of throwing.
        while (store.LoadPosSettings() is null)
        {
            Shell.Show(new StatusViewModel("Downloading items and prices from ERPNext…"));
            string problem;
            try
            {
                var report = await NewPuller().RunAsync();
                if (store.LoadPosSettings() is not null) break;
                problem = report.Feeds.FirstOrDefault(f => f.Error is not null)?.Error ?? "POS profile not found";
            }
            catch (Exception ex)
            {
                problem = ex.Message;
            }
            Shell.Show(new StatusViewModel($"Cannot reach ERPNext ({problem}). The first start needs the internet — retrying in 30 seconds."));
            await Task.Delay(TimeSpan.FromSeconds(30));
        }

        AddLocalTestCashiersIfNoneSynced();
        Shell.ShopName = store.LoadPosSettings()!.CompanyName;
        Shell.Show(NewLogin());

        var sync = new SyncService(NewPuller, erp, ctx.Receipts, Shell, dispatcher, TimeSpan.FromSeconds(settings.SyncIntervalSeconds));
        _ = Task.Run(() => sync.RunAsync(stop.Token));
    }

    public void Stop() => stop.Cancel();

    public object NewLogin() => new LoginViewModel(ctx, Shell.Session, NewSale);

    private object NewSale() =>
        new SaleViewModel(ctx, Shell.Session, new SupervisorGate(ctx, Shell.Session),
            (sale, kind) => new PaymentViewModel(ctx, Shell.Session, sale, kind));

    private CatalogPuller NewPuller() => CatalogPuller.CreateDefault(syncContext, catalog.Reload, null, new CashierFeed(syncContext, cashiers));

    private void AddLocalTestCashiersIfNoneSynced()
    {
        if (cashiers.All().Count > 0 || settings.LocalTestCashiers is not { Count: > 0 } local) return;
        cashiers.ReplaceAll(local.Where(c => PinHasher.IsValidPin(c.Pin))
            .Select(c => new Cashier(c.Id, c.Name, null, PinHasher.Hash(c.Pin), c.IsSupervisor, true)));
    }
}
```

- [ ] **Step 5: Build**

Run (from `tillpos/`): `dotnet build` — expected 0 warnings, 0 errors. (Views come in Task 10; a minimal `App.xaml`/`App.xaml.cs` is needed for the build — create them now as in Task 10 Step 1 if the build requires an entry point.)

- [ ] **Step 6: Commit**

```powershell
git add tillpos
git commit -m "feat(app): WPF app host, settings, DPAPI secret, raw printing, background sync"
```

---

### Task 10: WPF app — shell window, screens, dialogs, scanner and hotkeys

**Files:**
- Create: `tillpos/src/TillPOS.App/App.xaml`, `App.xaml.cs`, `Theme.xaml`, `MainWindow.xaml`, `MainWindow.xaml.cs`, `WpfDialogs.cs`
- Create: `tillpos/src/TillPOS.App/Views/StatusView.xaml(.cs)`, `LoginView.xaml(.cs)`, `OpenShiftView.xaml(.cs)`, `SaleView.xaml(.cs)`, `PaymentView.xaml(.cs)`
- Create: `tillpos/src/TillPOS.App/Dialogs/PinDialog.xaml(.cs)`, `NumberDialog.xaml(.cs)`

**Interfaces:**
- Consumes: all Presentation view models; `AppHost`.
- Produces: the runnable `TillPOS.exe`; `TillPOS.exe --protect-secret <secret>` copies the DPAPI-protected secret to the clipboard.

- [ ] **Step 1: Application and theme**

`tillpos/src/TillPOS.App/App.xaml`:

```xml
<Application x:Class="TillPOS.App.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             ShutdownMode="OnMainWindowClose">
  <Application.Resources>
    <ResourceDictionary Source="Theme.xaml" />
  </Application.Resources>
</Application>
```

`tillpos/src/TillPOS.App/App.xaml.cs`:

```csharp
using System.Windows;

namespace TillPOS.App;

public partial class App : Application
{
    private AppHost? host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args is ["--protect-secret", var secret])
        {
            Clipboard.SetText(SecretProtector.Protect(secret));
            MessageBox.Show("The protected secret was copied to the clipboard. Paste it into settings.json as ApiSecretProtected.", "TillPOS");
            Shutdown();
            return;
        }

        TillSettings settings;
        try
        {
            settings = TillSettings.Load(TillSettings.DefaultPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Settings could not be read from {TillSettings.DefaultPath}:\n{ex.Message}", "TillPOS");
            Shutdown(1);
            return;
        }

        var window = new MainWindow();
        host = new AppHost(settings, Dispatcher, new WpfDialogs(window));
        window.DataContext = host.Shell;
        MainWindow = window;
        window.Show();
        await host.StartAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        host?.Stop();
        base.OnExit(e);
    }
}
```

`tillpos/src/TillPOS.App/Theme.xaml`:

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <SolidColorBrush x:Key="Ink" Color="#15191E" />
  <SolidColorBrush x:Key="Ground" Color="#EEF0EC" />
  <SolidColorBrush x:Key="Panel" Color="#FFFFFF" />
  <SolidColorBrush x:Key="Accent" Color="#0F6B4B" />
  <SolidColorBrush x:Key="Warn" Color="#8A4205" />
  <SolidColorBrush x:Key="Danger" Color="#A3261B" />
  <SolidColorBrush x:Key="Muted" Color="#5B6470" />
  <SolidColorBrush x:Key="Line" Color="#CBD0C8" />

  <Style TargetType="Button" x:Key="KeyButton">
    <Setter Property="MinHeight" Value="56" />
    <Setter Property="MinWidth" Value="56" />
    <Setter Property="Margin" Value="4" />
    <Setter Property="FontSize" Value="18" />
    <Setter Property="Background" Value="{StaticResource Panel}" />
    <Setter Property="BorderBrush" Value="{StaticResource Line}" />
    <Setter Property="Foreground" Value="{StaticResource Ink}" />
  </Style>
  <Style TargetType="Button" x:Key="PrimaryButton" BasedOn="{StaticResource KeyButton}">
    <Setter Property="Background" Value="{StaticResource Accent}" />
    <Setter Property="Foreground" Value="White" />
    <Setter Property="FontWeight" Value="Bold" />
    <Setter Property="FontSize" Value="22" />
  </Style>
  <Style TargetType="Button" x:Key="DangerButton" BasedOn="{StaticResource KeyButton}">
    <Setter Property="Foreground" Value="{StaticResource Danger}" />
  </Style>
  <Style TargetType="TextBlock" x:Key="Money">
    <Setter Property="FontFamily" Value="Consolas" />
    <Setter Property="FontWeight" Value="SemiBold" />
  </Style>
</ResourceDictionary>
```

- [ ] **Step 2: Shell window with scanner detection**

`tillpos/src/TillPOS.App/MainWindow.xaml`:

```xml
<Window x:Class="TillPOS.App.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:vm="clr-namespace:TillPOS.Presentation;assembly=TillPOS.Presentation"
        xmlns:views="clr-namespace:TillPOS.App.Views"
        Title="TillPOS" Width="1366" Height="768" WindowState="Maximized"
        Background="{StaticResource Ground}" FontFamily="Segoe UI" FontSize="15">
  <Window.Resources>
    <DataTemplate DataType="{x:Type vm:StatusViewModel}"><views:StatusView /></DataTemplate>
    <DataTemplate DataType="{x:Type vm:LoginViewModel}"><views:LoginView /></DataTemplate>
    <DataTemplate DataType="{x:Type vm:OpenShiftViewModel}"><views:OpenShiftView /></DataTemplate>
    <DataTemplate DataType="{x:Type vm:SaleViewModel}"><views:SaleView /></DataTemplate>
    <DataTemplate DataType="{x:Type vm:PaymentViewModel}"><views:PaymentView /></DataTemplate>
  </Window.Resources>
  <DockPanel>
    <Border DockPanel.Dock="Top" Background="{StaticResource Ink}" Height="56" Padding="20,0">
      <DockPanel LastChildFill="False">
        <StackPanel DockPanel.Dock="Left" Orientation="Horizontal" VerticalAlignment="Center">
          <TextBlock Text="{Binding ShopName}" Foreground="White" FontWeight="Bold" FontSize="17" />
          <TextBlock Text="{Binding TillName}" Foreground="#C9CFD6" Margin="12,0,0,0" />
        </StackPanel>
        <StackPanel DockPanel.Dock="Right" Orientation="Horizontal" VerticalAlignment="Center">
          <TextBlock Foreground="White" Margin="0,0,24,0">
            <Run Text="Cashier: " /><Run Text="{Binding Session.Cashier.Name, Mode=OneWay}" FontWeight="Bold" />
          </TextBlock>
          <Border CornerRadius="12" Background="#262C33" Padding="12,4" Margin="0,0,16,0">
            <StackPanel Orientation="Horizontal">
              <Ellipse Width="10" Height="10" Margin="0,0,8,0">
                <Ellipse.Style>
                  <Style TargetType="Ellipse">
                    <Setter Property="Fill" Value="#F5A524" />
                    <Style.Triggers>
                      <DataTrigger Binding="{Binding Online}" Value="True"><Setter Property="Fill" Value="#3CCB7F" /></DataTrigger>
                    </Style.Triggers>
                  </Style>
                </Ellipse.Style>
              </Ellipse>
              <TextBlock Text="{Binding SyncStatus}" Foreground="White" />
              <TextBlock Foreground="#C9CFD6" Margin="8,0,0,0">
                <Run Text="· " /><Run Text="{Binding PendingUploads, Mode=OneWay}" /><Run Text=" waiting" />
              </TextBlock>
            </StackPanel>
          </Border>
          <TextBlock Text="{Binding Clock}" Foreground="White" FontFamily="Consolas" FontSize="16" />
        </StackPanel>
      </DockPanel>
    </Border>
    <ContentControl Content="{Binding Current}" Margin="16" />
  </DockPanel>
</Window>
```

`tillpos/src/TillPOS.App/MainWindow.xaml.cs`:

```csharp
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TillPOS.Presentation;

namespace TillPOS.App;

/// <summary>Hosts the screens. Watches all keyboard input for scanner bursts so a scan always reaches the sale screen,
/// whatever has focus, and removes the scanned digits from a focused text box.</summary>
public partial class MainWindow : Window
{
    private readonly ScanBuffer scanBuffer = new(() => DateTimeOffset.Now);
    private readonly DispatcherTimer clock = new() { Interval = TimeSpan.FromSeconds(15) };

    public MainWindow()
    {
        InitializeComponent();
        PreviewTextInput += (_, e) => { foreach (var c in e.Text) scanBuffer.OnChar(c); };
        PreviewKeyDown += OnPreviewKeyDown;
        clock.Tick += (_, _) => UpdateClock();
        clock.Start();
        Loaded += (_, _) => UpdateClock();
    }

    private void UpdateClock()
    {
        if (DataContext is ShellViewModel shell) shell.Clock = DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var code = scanBuffer.OnChar('\r');
        if (code is null || DataContext is not ShellViewModel { Current: SaleViewModel sale }) return;

        if (Keyboard.FocusedElement is TextBox box && box.Text.EndsWith(code, StringComparison.Ordinal))
            box.Text = box.Text[..^code.Length];
        sale.Scan(code);
        e.Handled = true;
    }
}
```

- [ ] **Step 3: Dialogs**

`tillpos/src/TillPOS.App/Dialogs/PinDialog.xaml`:

```xml
<Window x:Class="TillPOS.App.Dialogs.PinDialog"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Width="380" SizeToContent="Height" WindowStartupLocation="CenterOwner" ResizeMode="NoResize"
        FontFamily="Segoe UI" FontSize="16" Background="{StaticResource Ground}">
  <StackPanel Margin="20">
    <TextBlock x:Name="ReasonText" TextWrapping="Wrap" FontWeight="SemiBold" Margin="0,0,0,12" />
    <PasswordBox x:Name="PinBox" FontSize="24" Height="48" MaxLength="6" />
    <UniformGrid Columns="3" Margin="0,12,0,0">
      <Button Style="{StaticResource KeyButton}" Content="1" Click="Digit" /><Button Style="{StaticResource KeyButton}" Content="2" Click="Digit" /><Button Style="{StaticResource KeyButton}" Content="3" Click="Digit" />
      <Button Style="{StaticResource KeyButton}" Content="4" Click="Digit" /><Button Style="{StaticResource KeyButton}" Content="5" Click="Digit" /><Button Style="{StaticResource KeyButton}" Content="6" Click="Digit" />
      <Button Style="{StaticResource KeyButton}" Content="7" Click="Digit" /><Button Style="{StaticResource KeyButton}" Content="8" Click="Digit" /><Button Style="{StaticResource KeyButton}" Content="9" Click="Digit" />
      <Button Style="{StaticResource KeyButton}" Content="Cancel" IsCancel="True" /><Button Style="{StaticResource KeyButton}" Content="0" Click="Digit" /><Button Style="{StaticResource PrimaryButton}" Content="OK" IsDefault="True" Click="Ok" />
    </UniformGrid>
  </StackPanel>
</Window>
```

`tillpos/src/TillPOS.App/Dialogs/PinDialog.xaml.cs`:

```csharp
using System.Windows;
using System.Windows.Controls;

namespace TillPOS.App.Dialogs;

public partial class PinDialog : Window
{
    public PinDialog(string title, string reason)
    {
        InitializeComponent();
        Title = title;
        ReasonText.Text = reason;
        Loaded += (_, _) => PinBox.Focus();
    }

    public string Pin => PinBox.Password;

    private void Digit(object sender, RoutedEventArgs e)
    {
        if (PinBox.Password.Length < 6) PinBox.Password += ((Button)sender).Content;
    }

    private void Ok(object sender, RoutedEventArgs e) => DialogResult = true;
}
```

`tillpos/src/TillPOS.App/Dialogs/NumberDialog.xaml`:

```xml
<Window x:Class="TillPOS.App.Dialogs.NumberDialog"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Width="360" SizeToContent="Height" WindowStartupLocation="CenterOwner" ResizeMode="NoResize"
        FontFamily="Segoe UI" FontSize="16" Background="{StaticResource Ground}">
  <StackPanel Margin="20">
    <TextBlock x:Name="PromptText" TextWrapping="Wrap" FontWeight="SemiBold" Margin="0,0,0,12" />
    <TextBox x:Name="ValueBox" FontSize="24" Height="48" />
    <StackPanel Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,12,0,0">
      <Button Style="{StaticResource KeyButton}" Content="Cancel" IsCancel="True" Width="110" />
      <Button Style="{StaticResource PrimaryButton}" Content="OK" IsDefault="True" Width="110" Click="Ok" />
    </StackPanel>
  </StackPanel>
</Window>
```

`tillpos/src/TillPOS.App/Dialogs/NumberDialog.xaml.cs`:

```csharp
using System.Globalization;
using System.Windows;

namespace TillPOS.App.Dialogs;

public partial class NumberDialog : Window
{
    public NumberDialog(string title, string prompt)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        Loaded += (_, _) => ValueBox.Focus();
    }

    public decimal? Value { get; private set; }

    private void Ok(object sender, RoutedEventArgs e)
    {
        if (!decimal.TryParse(ValueBox.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var v)) return;
        Value = v;
        DialogResult = true;
    }
}
```

`tillpos/src/TillPOS.App/WpfDialogs.cs`:

```csharp
using System.Windows;
using TillPOS.App.Dialogs;
using TillPOS.Presentation;

namespace TillPOS.App;

public sealed class WpfDialogs(Window owner) : IDialogs
{
    public Task<string?> AskPinAsync(string title, string reason)
    {
        var dialog = new PinDialog(title, reason) { Owner = owner };
        return Task.FromResult(dialog.ShowDialog() == true ? dialog.Pin : null);
    }

    public Task<decimal?> AskNumberAsync(string title, string prompt)
    {
        var dialog = new NumberDialog(title, prompt) { Owner = owner };
        return Task.FromResult(dialog.ShowDialog() == true ? dialog.Value : null);
    }

    public void Info(string message) => MessageBox.Show(owner, message, "TillPOS");
}
```

- [ ] **Step 4: Screens**

Every view's code-behind is the standard constructor:

```csharp
namespace TillPOS.App.Views;

public partial class StatusView : System.Windows.Controls.UserControl
{
    public StatusView() => InitializeComponent();
}
```

(Same for `LoginView`, `OpenShiftView`, `PaymentView`; `SaleView` adds focus handling below.)

`tillpos/src/TillPOS.App/Views/StatusView.xaml`:

```xml
<UserControl x:Class="TillPOS.App.Views.StatusView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <TextBlock Text="{Binding Message}" FontSize="24" TextWrapping="Wrap" HorizontalAlignment="Center" VerticalAlignment="Center" MaxWidth="700" TextAlignment="Center" />
</UserControl>
```

`tillpos/src/TillPOS.App/Views/LoginView.xaml`:

```xml
<UserControl x:Class="TillPOS.App.Views.LoginView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <UserControl.InputBindings>
    <KeyBinding Key="Enter" Command="{Binding LoginCommand}" />
    <KeyBinding Key="Back" Command="{Binding BackspaceCommand}" />
  </UserControl.InputBindings>
  <StackPanel Width="380" HorizontalAlignment="Center" VerticalAlignment="Center">
    <TextBlock Text="Enter your PIN" FontSize="26" FontWeight="Bold" HorizontalAlignment="Center" />
    <TextBlock Text="{Binding PinMask}" FontSize="32" HorizontalAlignment="Center" Margin="0,12" MinHeight="44" />
    <TextBlock Text="{Binding Message}" Foreground="{StaticResource Danger}" HorizontalAlignment="Center" MinHeight="22" />
    <UniformGrid Columns="3" Margin="0,8">
      <Button Style="{StaticResource KeyButton}" Content="1" Command="{Binding DigitCommand}" CommandParameter="1" />
      <Button Style="{StaticResource KeyButton}" Content="2" Command="{Binding DigitCommand}" CommandParameter="2" />
      <Button Style="{StaticResource KeyButton}" Content="3" Command="{Binding DigitCommand}" CommandParameter="3" />
      <Button Style="{StaticResource KeyButton}" Content="4" Command="{Binding DigitCommand}" CommandParameter="4" />
      <Button Style="{StaticResource KeyButton}" Content="5" Command="{Binding DigitCommand}" CommandParameter="5" />
      <Button Style="{StaticResource KeyButton}" Content="6" Command="{Binding DigitCommand}" CommandParameter="6" />
      <Button Style="{StaticResource KeyButton}" Content="7" Command="{Binding DigitCommand}" CommandParameter="7" />
      <Button Style="{StaticResource KeyButton}" Content="8" Command="{Binding DigitCommand}" CommandParameter="8" />
      <Button Style="{StaticResource KeyButton}" Content="9" Command="{Binding DigitCommand}" CommandParameter="9" />
      <Button Style="{StaticResource KeyButton}" Content="C" Command="{Binding ClearCommand}" />
      <Button Style="{StaticResource KeyButton}" Content="0" Command="{Binding DigitCommand}" CommandParameter="0" />
      <Button Style="{StaticResource KeyButton}" Content="⌫" Command="{Binding BackspaceCommand}" />
    </UniformGrid>
    <Button Style="{StaticResource PrimaryButton}" Content="Log in" Height="64" Command="{Binding LoginCommand}" />
  </StackPanel>
</UserControl>
```

`tillpos/src/TillPOS.App/Views/OpenShiftView.xaml`:

```xml
<UserControl x:Class="TillPOS.App.Views.OpenShiftView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <UserControl.InputBindings>
    <KeyBinding Key="Enter" Command="{Binding OpenCommand}" />
  </UserControl.InputBindings>
  <StackPanel Width="380" HorizontalAlignment="Center" VerticalAlignment="Center">
    <TextBlock Text="Open shift" FontSize="26" FontWeight="Bold" />
    <TextBlock Text="Count the cash in the drawer and enter it." Foreground="{StaticResource Muted}" Margin="0,4,0,12" />
    <TextBox Text="{Binding OpeningCash.Text, UpdateSourceTrigger=PropertyChanged}" FontSize="28" Height="56" FontFamily="Consolas" />
    <TextBlock Text="{Binding Message}" Foreground="{StaticResource Danger}" MinHeight="22" Margin="0,4" />
    <UniformGrid Columns="3">
      <Button Style="{StaticResource KeyButton}" Content="1" Command="{Binding KeyCommand}" CommandParameter="1" />
      <Button Style="{StaticResource KeyButton}" Content="2" Command="{Binding KeyCommand}" CommandParameter="2" />
      <Button Style="{StaticResource KeyButton}" Content="3" Command="{Binding KeyCommand}" CommandParameter="3" />
      <Button Style="{StaticResource KeyButton}" Content="4" Command="{Binding KeyCommand}" CommandParameter="4" />
      <Button Style="{StaticResource KeyButton}" Content="5" Command="{Binding KeyCommand}" CommandParameter="5" />
      <Button Style="{StaticResource KeyButton}" Content="6" Command="{Binding KeyCommand}" CommandParameter="6" />
      <Button Style="{StaticResource KeyButton}" Content="7" Command="{Binding KeyCommand}" CommandParameter="7" />
      <Button Style="{StaticResource KeyButton}" Content="8" Command="{Binding KeyCommand}" CommandParameter="8" />
      <Button Style="{StaticResource KeyButton}" Content="9" Command="{Binding KeyCommand}" CommandParameter="9" />
      <Button Style="{StaticResource KeyButton}" Content="." Command="{Binding KeyCommand}" CommandParameter="." />
      <Button Style="{StaticResource KeyButton}" Content="0" Command="{Binding KeyCommand}" CommandParameter="0" />
      <Button Style="{StaticResource KeyButton}" Content="⌫" Command="{Binding KeyCommand}" CommandParameter="⌫" />
    </UniformGrid>
    <Button Style="{StaticResource PrimaryButton}" Content="Open shift" Height="64" Command="{Binding OpenCommand}" />
  </StackPanel>
</UserControl>
```

`tillpos/src/TillPOS.App/Views/SaleView.xaml`:

```xml
<UserControl x:Class="TillPOS.App.Views.SaleView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <UserControl.InputBindings>
    <KeyBinding Key="F3" Command="{Binding SetQtySelectedCommand}" />
    <KeyBinding Key="F11" Command="{Binding PayCardCommand}" />
    <KeyBinding Key="F12" Command="{Binding PayCashCommand}" />
    <KeyBinding Key="Delete" Command="{Binding RemoveSelectedCommand}" />
  </UserControl.InputBindings>
  <Grid>
    <Grid.ColumnDefinitions>
      <ColumnDefinition Width="*" />
      <ColumnDefinition Width="404" />
    </Grid.ColumnDefinitions>

    <DockPanel Grid.Column="0" Margin="0,0,16,0">
      <TextBox x:Name="ScanBox" DockPanel.Dock="Top" Height="56" FontSize="20" BorderBrush="{StaticResource Accent}" BorderThickness="2"
               Text="{Binding ScanText, UpdateSourceTrigger=PropertyChanged}">
        <TextBox.InputBindings>
          <KeyBinding Key="Enter" Command="{Binding ScanEnteredCommand}" />
        </TextBox.InputBindings>
      </TextBox>
      <TextBlock DockPanel.Dock="Top" Text="{Binding Message}" Margin="0,8" FontSize="15" MinHeight="22">
        <TextBlock.Style>
          <Style TargetType="TextBlock">
            <Setter Property="Foreground" Value="{StaticResource Accent}" />
            <Style.Triggers>
              <DataTrigger Binding="{Binding MessageIsError}" Value="True"><Setter Property="Foreground" Value="{StaticResource Danger}" /></DataTrigger>
            </Style.Triggers>
          </Style>
        </TextBlock.Style>
      </TextBlock>
      <DockPanel DockPanel.Dock="Bottom" Margin="0,8,0,0">
        <TextBlock DockPanel.Dock="Left" Text="Search (F2):" VerticalAlignment="Center" Margin="0,0,8,0" />
        <TextBox x:Name="SearchBox" Height="40" FontSize="16" Text="{Binding SearchText, UpdateSourceTrigger=PropertyChanged}" />
      </DockPanel>
      <ListBox DockPanel.Dock="Bottom" MaxHeight="160" ItemsSource="{Binding SearchResults}" Visibility="{Binding SearchResults.Count, Converter={StaticResource CountToVisibility}}">
        <ListBox.ItemTemplate>
          <DataTemplate>
            <Button Style="{StaticResource KeyButton}" HorizontalContentAlignment="Left" MinHeight="40"
                    Command="{Binding DataContext.AddFromSearchCommand, RelativeSource={RelativeSource AncestorType=UserControl}}"
                    CommandParameter="{Binding ItemCode}">
              <TextBlock><Run Text="{Binding ItemName, Mode=OneWay}" FontWeight="SemiBold" /><Run Text="  " /><Run Text="{Binding ItemCode, Mode=OneWay}" Foreground="{StaticResource Muted}" /></TextBlock>
            </Button>
          </DataTemplate>
        </ListBox.ItemTemplate>
      </ListBox>
      <ListBox ItemsSource="{Binding Lines}" SelectedItem="{Binding SelectedLine}" HorizontalContentAlignment="Stretch" Background="{StaticResource Panel}">
        <ListBox.ItemTemplate>
          <DataTemplate>
            <Grid Height="56">
              <Grid.ColumnDefinitions>
                <ColumnDefinition Width="36" /><ColumnDefinition Width="*" /><ColumnDefinition Width="170" />
                <ColumnDefinition Width="90" /><ColumnDefinition Width="90" /><ColumnDefinition Width="100" /><ColumnDefinition Width="56" />
              </Grid.ColumnDefinitions>
              <TextBlock Grid.Column="0" Text="{Binding No}" VerticalAlignment="Center" Foreground="{StaticResource Muted}" />
              <StackPanel Grid.Column="1" VerticalAlignment="Center">
                <TextBlock Text="{Binding Name}" FontWeight="SemiBold" TextTrimming="CharacterEllipsis" />
                <TextBlock Text="{Binding Barcode}" FontSize="12" Foreground="{StaticResource Muted}" FontFamily="Consolas" />
              </StackPanel>
              <StackPanel Grid.Column="2" Orientation="Horizontal" VerticalAlignment="Center">
                <Button Style="{StaticResource KeyButton}" Content="−" MinHeight="44" MinWidth="44"
                        Command="{Binding DataContext.DecrementCommand, RelativeSource={RelativeSource AncestorType=UserControl}}" CommandParameter="{Binding Id}" />
                <TextBlock Text="{Binding Qty}" Width="70" TextAlignment="Center" VerticalAlignment="Center" Style="{StaticResource Money}" />
                <Button Style="{StaticResource KeyButton}" Content="+" MinHeight="44" MinWidth="44"
                        Command="{Binding DataContext.IncrementCommand, RelativeSource={RelativeSource AncestorType=UserControl}}" CommandParameter="{Binding Id}" />
              </StackPanel>
              <TextBlock Grid.Column="3" Text="{Binding Price}" TextAlignment="Right" VerticalAlignment="Center" Style="{StaticResource Money}" />
              <TextBlock Grid.Column="4" Text="{Binding Offer}" TextAlignment="Right" VerticalAlignment="Center" Foreground="{StaticResource Warn}" />
              <TextBlock Grid.Column="5" Text="{Binding Amount}" TextAlignment="Right" VerticalAlignment="Center" FontSize="17" Style="{StaticResource Money}" />
              <Button Grid.Column="6" Style="{StaticResource DangerButton}" Content="✕" MinHeight="44" MinWidth="44"
                      Command="{Binding DataContext.RemoveCommand, RelativeSource={RelativeSource AncestorType=UserControl}}" CommandParameter="{Binding Id}" />
            </Grid>
          </DataTemplate>
        </ListBox.ItemTemplate>
      </ListBox>
    </DockPanel>

    <StackPanel Grid.Column="1">
      <Border Background="{StaticResource Panel}" CornerRadius="12" Padding="20">
        <StackPanel>
          <DockPanel><TextBlock Text="Lines" Foreground="{StaticResource Muted}" /><TextBlock Text="{Binding ItemCount}" HorizontalAlignment="Right" Style="{StaticResource Money}" /></DockPanel>
          <DockPanel><TextBlock Text="Offers &amp; discounts" Foreground="{StaticResource Warn}" /><TextBlock Text="{Binding Discount}" HorizontalAlignment="Right" Foreground="{StaticResource Warn}" Style="{StaticResource Money}" /></DockPanel>
          <DockPanel><TextBlock Text="VAT 5% (included)" Foreground="{StaticResource Muted}" /><TextBlock Text="{Binding Vat}" HorizontalAlignment="Right" Style="{StaticResource Money}" /></DockPanel>
          <Separator Margin="0,8" />
          <DockPanel><TextBlock Text="TOTAL AED" FontWeight="Bold" VerticalAlignment="Bottom" /><TextBlock Text="{Binding Total}" HorizontalAlignment="Right" FontSize="40" Style="{StaticResource Money}" /></DockPanel>
        </StackPanel>
      </Border>
      <Button Style="{StaticResource PrimaryButton}" Height="96" Margin="0,12,0,0" Content="PAY CASH  (F12)" Command="{Binding PayCashCommand}" />
      <UniformGrid Columns="2" Margin="0,8,0,0">
        <Button Style="{StaticResource KeyButton}" Height="64" Content="Card (F11)" Command="{Binding PayCardCommand}" />
        <Button Style="{StaticResource KeyButton}" Height="64" Content="Set qty (F3)" Command="{Binding SetQtySelectedCommand}" />
        <Button Style="{StaticResource KeyButton}" Height="64" Content="Search (F2)" Click="FocusSearch" />
        <Button Style="{StaticResource DangerButton}" Height="64" Content="Void bill" Command="{Binding VoidBillCommand}" />
      </UniformGrid>
    </StackPanel>
  </Grid>
</UserControl>
```

Add to `Theme.xaml` (used by the search list):

```xml
  <BooleanToVisibilityConverter x:Key="BoolToVisibility" />
```

and create `tillpos/src/TillPOS.App/CountToVisibilityConverter.cs`, registered in `Theme.xaml` as `<local:CountToVisibilityConverter x:Key="CountToVisibility" />` with `xmlns:local="clr-namespace:TillPOS.App"`:

```csharp
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TillPOS.App;

public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int count && count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
```

`tillpos/src/TillPOS.App/Views/SaleView.xaml.cs`:

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TillPOS.App.Views;

public partial class SaleView : UserControl
{
    public SaleView()
    {
        InitializeComponent();
        Loaded += (_, _) => ScanBox.Focus();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F2) { SearchBox.Focus(); e.Handled = true; }
            else if (e.Key == Key.Escape) { ScanBox.Focus(); e.Handled = true; }
        };
    }

    private void FocusSearch(object sender, RoutedEventArgs e) => SearchBox.Focus();
}
```

`tillpos/src/TillPOS.App/Views/PaymentView.xaml`:

```xml
<UserControl x:Class="TillPOS.App.Views.PaymentView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <UserControl.InputBindings>
    <KeyBinding Key="Enter" Command="{Binding CompleteCommand}" />
    <KeyBinding Key="Escape" Command="{Binding BackCommand}" />
  </UserControl.InputBindings>
  <Grid MaxWidth="900">
    <Grid.ColumnDefinitions><ColumnDefinition Width="*" /><ColumnDefinition Width="320" /></Grid.ColumnDefinitions>
    <StackPanel Grid.Column="0" Margin="0,0,16,0">
      <Border Background="{StaticResource Ink}" CornerRadius="12" Padding="20">
        <StackPanel>
          <TextBlock Text="AMOUNT DUE" Foreground="#C9CFD6" />
          <TextBlock Text="{Binding AmountDue}" Foreground="White" FontSize="46" Style="{StaticResource Money}" />
          <TextBlock Foreground="#C9CFD6"><Run Text="Bill total " /><Run Text="{Binding GrandTotal, Mode=OneWay}" /></TextBlock>
        </StackPanel>
      </Border>
      <UniformGrid Columns="3" Margin="0,12">
        <Button Style="{StaticResource KeyButton}" Height="60" Content="Cash" Command="{Binding SetKindCommand}" CommandParameter="Cash" />
        <Button Style="{StaticResource KeyButton}" Height="60" Content="Card" Command="{Binding SetKindCommand}" CommandParameter="Card" />
        <Button Style="{StaticResource KeyButton}" Height="60" Content="Split: card + cash" Command="{Binding SetKindCommand}" CommandParameter="Split" />
      </UniformGrid>
      <StackPanel Visibility="{Binding IsSplit, Converter={StaticResource BoolToVisibility}}">
        <TextBlock Text="Card part (charge on the card machine)" />
        <TextBox Text="{Binding Card.Text, UpdateSourceTrigger=PropertyChanged}" FontSize="26" Height="52" FontFamily="Consolas" GotFocus="CardFocused" />
      </StackPanel>
      <TextBlock Text="Cash received" Margin="0,8,0,0" />
      <TextBox Text="{Binding Cash.Text, UpdateSourceTrigger=PropertyChanged}" FontSize="26" Height="52" FontFamily="Consolas" GotFocus="CashFocused" />
      <ItemsControl ItemsSource="{Binding QuickCash}" Margin="0,8">
        <ItemsControl.ItemsPanel><ItemsPanelTemplate><UniformGrid Columns="6" /></ItemsPanelTemplate></ItemsControl.ItemsPanel>
        <ItemsControl.ItemTemplate>
          <DataTemplate>
            <Button Style="{StaticResource KeyButton}" Content="{Binding}"
                    Command="{Binding DataContext.QuickCashCommand, RelativeSource={RelativeSource AncestorType=UserControl}}" CommandParameter="{Binding}" />
          </DataTemplate>
        </ItemsControl.ItemTemplate>
      </ItemsControl>
      <DockPanel Margin="0,8"><TextBlock Text="Change" FontSize="20" /><TextBlock Text="{Binding Change}" HorizontalAlignment="Right" FontSize="32" Foreground="{StaticResource Accent}" Style="{StaticResource Money}" /></DockPanel>
      <TextBlock Text="{Binding Message}" Foreground="{StaticResource Danger}" FontSize="16" TextWrapping="Wrap" MinHeight="22" />
      <DockPanel Margin="0,12,0,0">
        <Button DockPanel.Dock="Left" Style="{StaticResource KeyButton}" Width="200" Height="72" Content="Back (Esc)" Command="{Binding BackCommand}" />
        <Button Style="{StaticResource PrimaryButton}" Height="72" Content="Complete &amp; print (Enter)" Command="{Binding CompleteCommand}" />
      </DockPanel>
    </StackPanel>
    <UniformGrid Grid.Column="1" Columns="3" VerticalAlignment="Top">
      <Button Style="{StaticResource KeyButton}" Height="72" Content="1" Command="{Binding KeyCommand}" CommandParameter="1" />
      <Button Style="{StaticResource KeyButton}" Height="72" Content="2" Command="{Binding KeyCommand}" CommandParameter="2" />
      <Button Style="{StaticResource KeyButton}" Height="72" Content="3" Command="{Binding KeyCommand}" CommandParameter="3" />
      <Button Style="{StaticResource KeyButton}" Height="72" Content="4" Command="{Binding KeyCommand}" CommandParameter="4" />
      <Button Style="{StaticResource KeyButton}" Height="72" Content="5" Command="{Binding KeyCommand}" CommandParameter="5" />
      <Button Style="{StaticResource KeyButton}" Height="72" Content="6" Command="{Binding KeyCommand}" CommandParameter="6" />
      <Button Style="{StaticResource KeyButton}" Height="72" Content="7" Command="{Binding KeyCommand}" CommandParameter="7" />
      <Button Style="{StaticResource KeyButton}" Height="72" Content="8" Command="{Binding KeyCommand}" CommandParameter="8" />
      <Button Style="{StaticResource KeyButton}" Height="72" Content="9" Command="{Binding KeyCommand}" CommandParameter="9" />
      <Button Style="{StaticResource KeyButton}" Height="72" Content="." Command="{Binding KeyCommand}" CommandParameter="." />
      <Button Style="{StaticResource KeyButton}" Height="72" Content="0" Command="{Binding KeyCommand}" CommandParameter="0" />
      <Button Style="{StaticResource KeyButton}" Height="72" Content="⌫" Command="{Binding KeyCommand}" CommandParameter="⌫" />
    </UniformGrid>
  </Grid>
</UserControl>
```

`tillpos/src/TillPOS.App/Views/PaymentView.xaml.cs`:

```csharp
using System.Windows;
using System.Windows.Controls;
using TillPOS.Presentation;

namespace TillPOS.App.Views;

public partial class PaymentView : UserControl
{
    public PaymentView() => InitializeComponent();

    private void CardFocused(object sender, RoutedEventArgs e) { if (DataContext is PaymentViewModel vm) vm.EditCard = true; }

    private void CashFocused(object sender, RoutedEventArgs e) { if (DataContext is PaymentViewModel vm) vm.EditCard = false; }
}
```

- [ ] **Step 5: Build and commit**

Run (from `tillpos/`): `dotnet build` (0 warnings) and `dotnet test` (all pass).

```powershell
git add tillpos
git commit -m "feat(app): till screens, dialogs, scanner detection and hotkeys"
```

---

### Task 11: Manual acceptance on the laptop (read-only against live)

No code. Uses live ERPNext **read-only** (catalog download); completed bills go only to the local outbox (no uploader exists yet). Record results in `docs/superpowers/plans/2026-10-07-tillpos-plan3a-acceptance.md`.

- [ ] **Step 1: Settings**

Run once: `tillpos\src\TillPOS.App\bin\Debug\net10.0-windows\TillPOS.exe --protect-secret <test user API secret>` → paste into the settings below.

Create `C:\ProgramData\TillPOS\settings.json`:

```json
{
  "BaseUrl": "https://erp.quickgroc.com/",
  "ApiKey": "<test user API key>",
  "ApiSecretProtected": "<pasted value>",
  "PosProfile": "Test Counter",
  "TillNumber": 9,
  "CashMode": "Cash Counter 2",
  "CardMode": "Credit Card",
  "PrinterName": "",
  "PaperWidth": "Mm80",
  "ReceiptFooter": "Thank you for shopping with us",
  "Precision": 3,
  "Rounding": "Bankers",
  "DbPath": "C:\\ProgramData\\TillPOS\\till.db",
  "SyncIntervalSeconds": 90,
  "LocalTestCashiers": [
    { "Id": "test", "Name": "Test Cashier", "Pin": "1234", "IsSupervisor": false },
    { "Id": "testsup", "Name": "Test Supervisor", "Pin": "9876", "IsSupervisor": true }
  ]
}
```

- [ ] **Step 2: Checks** (record pass/fail and notes for each)

| # | Check | Expected |
|---|---|---|
| M1 | Start the app | "Downloading items and prices…", then the PIN screen in about a minute; header shows shop name, Till 9, sync status |
| M2 | Log in with 1234; open shift with 200 | Sale screen |
| M3 | Type a real barcode in the scan box + Enter | Line added, total updated, VAT shown |
| M4 | Click the search box, type "milk", then scan a barcode (or paste a barcode and press Enter quickly) | Item added to the bill; search box does not keep the digits |
| M5 | Type the cucumber label `2000089007400` + Enter | 0.740 Kg line, 2.59 |
| M6 | Press + on the cucumber line | Refused with message |
| M7 | Press ✕ on a line, enter 1234 then 9876 | 1234 refused; 9876 removes the line |
| M8 | Close the app with items on the bill, start again | Same items restored |
| M9 | F12, quick cash 50, Enter | Receipt text file appears in `C:\ProgramData\TillPOS\receipts\`; content shows TAX INVOICE, TRN (if set on Company), lines, VAT, change |
| M10 | F11 (card) on a new bill | Exact amount; receipt saved |
| M11 | Header "waiting" count | Increases by one per completed bill |
| M12 | Unplug the network for 2 minutes | Header turns "Offline"; selling still works; reconnect → "Online" |

- [ ] **Step 3: Commit the results**

```powershell
git add docs/superpowers/plans/2026-10-07-tillpos-plan3a-acceptance.md
git commit -m "docs: Plan 3a manual acceptance results"
```

---

## Self-review notes (completed while writing)

- **Spec coverage (Plan 3a scope):** spec §8 screens 1–4 (login, open shift, sale, payment), §9 printing (ESC/POS, paper width, receipt contents, FTA QR, drawer on cash only), §10 autosave/printer-failure/offline status, §13b.5 supervisor gating with audit and throttling, Master Spec §2 scanner as keyboard wedge, §6 hotkeys (3a subset). Plan 3b: return screen, hold/recall UI (F5/F7), price check (F4), close-shift blind count, reprint (Ctrl+P), upload problems (with 2b). Plan 4: installer, first-run setup, removing LocalTestCashiers, DB file ACLs.
- **Review Focus** items each have a named test or manual check in the owning task.
- Names checked across tasks: `TillContext` fields, `SessionState`, `SupervisorGate.ApproveAsync`, `SaleViewModel.Money/Cart/SaleCompleted/AutosaveKey`, `PaymentViewModel(ctx, session, sale, kind)`, `CatalogPuller.CreateDefault(..., params extraFeeds)`, `ReceiptStore.CountPending`.
