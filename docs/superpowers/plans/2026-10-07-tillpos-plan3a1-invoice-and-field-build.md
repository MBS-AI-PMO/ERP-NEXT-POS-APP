# TillPOS Plan 3a.1: Invoice Layout, Receipt Preview and Field Test Build

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Give the shop a build it can test in the field with a real barcode reader, thermal printer and cash drawer. That needs four things:
- a proper English tax-invoice layout
- the invoice shown on screen after "Complete & print"
- a first-run setup screen (choose the printer, test-print and kick the drawer)
- a self-contained zip that runs on any Windows 10/11 x64 PC without installing .NET

**Architecture:**
- **Invoice:** the layout becomes a list of styled lines (`PrintLine`). One layout feeds three outputs: the text file, the ESC/POS bytes (bold and double-size text) and the WPF preview.
- **Preview:** a modal popup opened through `IDialogs.ShowReceipt`. A barcode scan closes it and adds the scanned item to the next bill.
- **Setup and secret:**
  - Setup is a WPF window that saves `settings.json`. The app then restarts to pick up the new settings.
  - On first start, a plain `ApiSecret` shipped beside the exe is encrypted with DPAPI for that PC.
- **Packaging:** a PowerShell script builds the zip into the git-ignored `publish/` folder. The secret is never committed.

**User decisions (2026-10-07):**
- English only, text mode. No Arabic or logo.
- Preview as a popup that closes on Enter/Esc or on the next scan.
- Dummy address "Nuaimiya 1, Al Ain Market, Ajman, UAE" and dummy phone "+971 6 000 0000".
- No TRN until the shop provides one. Never invent a TRN.

## Global Constraints
- Everything in Plan 3a's Global Constraints still holds (net10.0 libraries, net10.0-windows app only, 0 warnings, `0.00#` money format with invariant culture, no ERPNext writes).
- **Paper width:** 48 columns at 80 mm, 32 columns at 58 mm. No line may be wider than the paper, including double-height lines. Double-WIDTH lines may use at most half the columns.
- **Secrets:**
  - The API secret is never committed.
  - The packaged `settings.json` holds the plain secret only until the first start on the target PC.
  - On that first start the app writes `ApiSecretProtected` (DPAPI, machine scope) and removes the plain `ApiSecret` from every settings file it can write.
- **Feedback:** the build version (e.g. `0.3.1-field`) is visible on the login screen and in `errors.log` lines, so field feedback can name the build.

## Review Focus
1. A long item name, or a long weighed quantity, still keeps Qty / Price / Amount right-aligned and inside the paper width, at 58 mm too.
2. After a sale the popup is showing when the cashier scans the next item. The popup closes and the item is added; the scan is never lost or typed into the popup.
3. On the client PC the settings file shipped with a plain secret ends up holding only the protected secret after the first start, and the till works.
4. Wrong printer chosen, or the printer is off, in setup: "Test print" shows the error and the till still starts.
5. Reprint from the popup never opens the cash drawer.

---

### Task 1: Invoice layout (Core + Printing)

**Files:**
- Modify: `tillpos/src/TillPOS.Core/Sales/Receipt.cs` and `SaleRecorder.cs`: add `public string? CashierName { get; init; }` to `Receipt`, and an optional `cashierName` parameter (last) to `SaleRecorder.CompleteSale` that sets it.
- Modify: `tillpos/src/TillPOS.Presentation/PaymentViewModel.cs`: pass `cashier.Name`.
- Modify: `tillpos/src/TillPOS.Printing/ReceiptRenderer.cs`.
- Tests: `tillpos/tests/TillPOS.Tests/Printing/ReceiptRendererTests.cs`, plus a SaleRecorder test that CashierName is stored and survives `ReceiptStore` save/load (the receipt is stored as JSON; check that).

**Interfaces (Printing):**
- `public enum LineStyle { Normal, Bold, Title, Big }`
  - Title = double width + double height + bold + centered.
  - Big = double height + bold.
- `public sealed record PrintLine(string Text, LineStyle Style = LineStyle.Normal)`
- `public static IReadOnlyList<PrintLine> Layout(Receipt r, ReceiptHeader h, PaperWidth paper)`
- `TextLines(...)` returns `Layout(...).Select(l => l.Text)` and stays for the text files.
- `EscPosBytes(...)` prints each line in its style (ESC E for bold, GS ! for size, ESC a for centering) and resets to Normal after each styled line. It keeps the QR rule (GrandTotal, only with a TRN), the feed, the cut and the drawer kick.
- `EscPos` gets `Style(LineStyle)` or the existing `Bold`/`Size`/`Align` calls are used. Title text must fit `width / 2` columns.

**Layout (80 mm shown; 58 mm uses the same order with narrower columns):**

```
             AL AIN MARKETING L.L.C              (Bold, centered, wrapped)
      Nuaimiya 1, Al Ain Market, Ajman, UAE      (centered, wrapped)
              Tel: +971 6 000 0000               (only if Phone)
              TRN: 100000000000003               (only if Trn)
================================================
          TAX INVOICE                            (Title; return: "CREDIT NOTE" title + "Tax Credit Note" line)
================================================
Invoice No: TILL1-20261007134500-000001          (label/value; value never truncated, wraps to next line if needed)
Return of : TILL1-...                            (returns only)
Date      : 07/10/2026 13:45
Cashier   : Test Cashier                         (CashierName ?? Cashier)
Till      : Till 1
------------------------------------------------
Item                     Qty     Price    Amount (Bold header)
------------------------------------------------
FULL CREAM MILK 1L                               (name wrapped to full width)
                           2      6.79     13.58
CUCUMBER/KIYAR
                    0.740 Kg      3.50      2.59
  Offer: -0.50                                   (only when Rate < PriceListRate: "Offer" + saved amount on the line)
------------------------------------------------
Items: 2                                          (line count)
Total excl. VAT                            15.40
VAT 5%                                      0.77  (label "VAT" + rate if all lines share one rate; else "VAT")
TOTAL AED                                  16.17  (Big)
Rounding                                   -0.02  (only when amount due differs from GrandTotal, as today)
AMOUNT DUE                                 16.15  (Big; only with Rounding)
------------------------------------------------
Cash Counter 2                             20.00  (each payment)
Change                                      3.85  (only if non-zero)
You saved                                   0.50  (only if total discount > 0; sum of (PriceListRate-Rate)*Qty, rounded to precision)
------------------------------------------------
        Thank you for shopping with us           (footer, centered)
          Prices include 5% VAT                  (fixed line)
```

- **Column widths:**
  - 80 mm: Qty column right-aligned ending at column 28 (10 wide), Price 10 wide, Amount 10 wide.
  - 58 mm: Qty 9, Price 0 (omitted; the qty cell shows "2 x 6.79"), Amount 9.
  - The implementer may adjust widths as long as Review Focus 1 holds and the tests below pass.
- **VAT rate:** compute it as `TotalTaxes / NetTotal × 100` rounded to a whole number when NetTotal > 0, otherwise omit the rate. Simple and good enough for a single 5% VAT.
- **Date format:** `dd/MM/yyyy HH:mm` (UAE convention), invariant culture.

**Tests (replace or extend the existing renderer tests; keep the QR, drawer and credit-note tests):**
- A sale receipt contains, in order: company, address, Tel, TRN, `TAX INVOICE`, `Invoice No`, `Cashier   : Test Cashier` (CashierName set), the item header, both items, `Total excl. VAT`, `VAT 5%`, `TOTAL AED`, the payment, `Change`, the footer and `Prices include 5% VAT`.
- Every line is no wider than the paper at both widths, using a fixture with a 60-character item name, a 30-character payment-mode name and a return on 58 mm. Every `Title` line fits half the width.
- Qty / Price / Amount right edges line up across all item value rows at 80 mm.
- "You saved" and the "Offer" line appear only when a line has Rate < PriceListRate.
- `Layout` styles: exactly one Title line, a Big `TOTAL AED` line, and Bold for the company name.
- The ESC/POS output contains GS ! 0x11 (title) and GS ! 0x01 (big), and ends with GS ! 0x00 before the cut. The drawer kick appears only when asked.
- Without CashierName the receipt falls back to the cashier ID.

### Task 2: Receipt preview popup + reprint (Presentation + App)

**Files:**
- Modify: `tillpos/src/TillPOS.Presentation/Abstractions.cs`: add to `IDialogs` the method `string? ShowReceipt(Receipt receipt, string? printError, Func<string?> reprint)`.
  - It is modal and returns a barcode if a scan closed the popup, otherwise null.
  - `reprint` re-sends the receipt without opening the drawer and returns an error message, or null on success.
- Modify: `SaleViewModel.SaleCompleted`: after setting the message and navigating back to the sale screen, call `ShowReceipt` when `ShowReceiptPreview` is true.
  - Add `bool ShowReceiptPreview` to `TillContext` as a new last parameter, and update every construction site including the test fixture.
  - If `ShowReceipt` returns a code, call `Scan(code)`.
  - `reprint` = `() => { try { ctx.Output.Print(receipt, openDrawer: false); return null; } catch (Exception ex) { return ex.Message; } }`.
- Modify: `tillpos/tests/.../Presentation/Fakes.cs`: `FakeDialogs.ShowReceipt` records the receipts, returns a queued scan code (`Queue<string?> ReceiptScans`), and stores the last `reprint` func for tests.
- Create: `tillpos/src/TillPOS.App/Dialogs/ReceiptDialog.xaml(.cs)`.
  - **Window:** owner-centered, about 420 px wide, max height 90% of the screen.
  - **Paper look:** white "paper" in a ScrollViewer. Each `PrintLine` is a TextBlock in Consolas; Title is bigger and bold, Big is larger and bold, Bold is bold. Lines are rendered exactly as `ReceiptRenderer.Layout` gives them.
  - **Print error:** shown as a red banner at the top when `printError` is set ("Printer problem: … The bill is saved.").
  - **Buttons:** "Print again" calls reprint and shows its result in the banner (green "Sent to printer" or red error). "Close (Enter)" is IsDefault and IsCancel.
  - **Scans:** a `ScanBuffer` like NumberDialog. A detected scan sets the returned code and closes the dialog.
  - **Scan guard:** the dialog must not let a scan's Enter close it as a plain "close" without capturing the code. Handle Enter in PreviewKeyDown through the ScanBuffer first.
  - **QR:** when the header has a TRN, show a small grey box with the text "QR code printed on paper" (no QR library).
- Modify: `WpfDialogs`: implement `ShowReceipt`. It needs the header and the paper width, so give `WpfDialogs` a `Func<(ReceiptHeader Header, PaperWidth Paper)>` constructor parameter. `ReceiptOutput` exposes `public ReceiptHeader Header()` (the same header it prints with), and AppHost/App wire them.
- Modify: `TillSettings`: add `bool ShowReceiptPreview = true` as a new last parameter.
- **Tests (Presentation):**
  - After a completed sale the fake records one `ShowReceipt` call with the saved receipt and the print error, if any.
  - A queued scan code from `ShowReceipt` ends up as a line on the new bill.
  - `ShowReceiptPreview = false` means no call.
  - Calling the recorded reprint prints with `openDrawer == false` and returns null; with `FakeOutput.Fail` it returns the message.

### Task 3: First-run setup, secret import, version (App)

**Files:** `tillpos/src/TillPOS.App/` — `TillSettings.cs`, `App.xaml.cs`, new `SettingsStore.cs`, new `Dialogs/SetupDialog.xaml(.cs)`, `Views/LoginView.xaml(.cs)`, `Presentation/LoginViewModel.cs` (Settings command), `TillPOS.App.csproj` (Version).

1. **Version.**
   - In the csproj set `<Version>0.3.1</Version>` and `<InformationalVersion>0.3.1-field</InformationalVersion>`.
   - `ShellViewModel` gets `string Version`. The App sets it from `Assembly.GetEntryAssembly()!.GetCustomAttribute<AssemblyInformationalVersionAttribute>()` and strips any "+commit" suffix.
   - LoginView shows "TillPOS {Version}" small and grey under the PIN pad.
   - `LogError` prefixes each line with the version.
2. **Settings location and import (`SettingsStore`):**
   - `static string ProgramDataPath`: `%ProgramData%\TillPOS\settings.json`, or the `TILLPOS_SETTINGS` path.
   - `static string BesideExePath`: `Path.Combine(AppContext.BaseDirectory, "settings.json")`.
   - **Load order:**
     1. If the ProgramData file exists, load it.
     2. Otherwise, if the beside-exe file exists, load it, set `DbPath` to `%ProgramData%\TillPOS\till.db` when it is blank, and save to ProgramData. Creating the folder is fine.
     3. Otherwise show "Settings not found …" and exit, as today.
   - **Plain secret:** if the loaded settings have a non-empty plain `ApiSecret`, set `ApiSecretProtected = SecretProtector.Protect(ApiSecret)` and `ApiSecret = null`, then save to ProgramData. Also try to rewrite the beside-exe file without the plain secret (store `ApiSecretProtected`). Ignore IO or permission errors there, but log them.
   - `TillSettings` gets `string? ApiSecret = null` and `bool SetupDone = false` as new last parameters. `ApiSecretProtected` becomes optional (default `""`).
   - `Save(TillSettings, string path)` writes indented JSON with string enums, writing a temp file then replacing the target.
   - `AppHost` uses `ApiSecretProtected`. If it is empty and there is no plain secret, it fails with a clear message ("API secret missing in settings.json").
3. **Setup window (`SetupDialog`)**, shown at startup when `SetupDone == false`, before AppHost starts:
   - **Printer:** a ComboBox listing installed printers from `new System.Printing.LocalPrintServer().GetPrintQueues(new[] { EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections })`, names only, plus the first item "(No printer — save receipts as files)". Preselect the current `PrinterName`, or the Windows default printer (`LocalPrintServer.GetDefaultPrintQueue()`) if it is set.
   - **Paper width:** radio buttons for 80 mm and 58 mm.
   - **Till number:** 1–99. **Show invoice on screen after each sale:** a checkbox (`ShowReceiptPreview`).
   - **"Test print":** sends `ReceiptRenderer`-free test bytes via `RawPrinter`: Init, centered bold "TillPOS test print", printer name, paper width, a 48- or 32-column ruler line `123456789012…`, the date, feed, cut and drawer kick. It shows "Sent. Did it print and did the drawer open?" or the error.
   - **"Save and start":** saves the settings with `SetupDone = true` to ProgramData, then continues startup in the same process (no restart needed at first run, since AppHost is not built yet).
   - **"Cancel":** exits the app.
   - The dialog only touches printer/paper/till/preview fields and preserves every other setting.
4. **Changing settings later:**
   - LoginView gets a small "Settings" button (bottom-right, non-focusable) bound to `LoginViewModel.SettingsCommand`.
   - It asks for a supervisor PIN through a `SupervisorGate`. The LoginViewModel builds the gate from ctx with an empty session, so `CashierId` is "".
   - The approval action needs a value: append `ApprovalAction.SettingsChange` to the enum (last).
   - When approved, it calls a new `IDialogs.ShowSetup()` method that returns `bool` (saved). On true, the app restarts itself:
     - release and dispose the single-instance mutex first;
     - `Process.Start(Environment.ProcessPath!)`, then `Application.Current.Shutdown()`.
   - Put the restart logic in App and expose it to WpfDialogs as an `Action restart` constructor parameter.
   - Tests (Presentation): the Settings command with the supervisor PIN calls `ShowSetup` once and logs a `SettingsChange` approval. With a cashier PIN it does not call it.

### Task 4: Field package script + START HERE

**Files:**
- Create: `tillpos/tools/publish-field.ps1` (committed; contains no secrets).
- Create: `tillpos/tools/field/START HERE.txt` (committed).

**What `publish-field.ps1 -Version 0.3.1` does:**
1. Runs `dotnet publish src/TillPOS.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o ..\publish\TillPOS-field\TillPOS`. Single-file is off because WPF self-extraction is slower to start.
2. Reads `..\publish\TillPOS.SyncCli\tillpos.cli.json` (local, git-ignored) for BaseUrl, ApiKey, ApiSecret and PosProfile.
3. Writes `..\publish\TillPOS-field\TillPOS\settings.json` with the plain `ApiSecret` (imported and protected on the target PC at first start) and `SetupDone=false`. The remaining fields:
   - `TillNumber` 1, `CashMode` "Cash Counter 2", `CardMode` "Credit Card", `PrinterName` "", `PaperWidth` "Mm80"
   - `ReceiptFooter` "Thank you for shopping with us", `Precision` 3, `Rounding` "Bankers"
   - `DbPath` "" (meaning ProgramData), `SyncIntervalSeconds` 90, `ShowReceiptPreview` true
   - `ShopAddress` "Nuaimiya 1, Al Ain Market, Ajman, UAE", `ShopPhone` "+971 6 000 0000"
   - `LocalTestCashiers`: cashier1 / Test Cashier / 1234, and supervisor1 / Test Supervisor / 9876 (IsSupervisor)
4. Copies `START HERE.txt` into `..\publish\TillPOS-field\`.
5. Zips `..\publish\TillPOS-field\*` to `..\publish\TillPOS-field-0.3.1.zip` and prints the path and size.

`publish/` is already git-ignored. Confirm with `git check-ignore`, and make the script refuse to run if the output path is not ignored.

**`START HERE.txt`** is plain English for a shop manager. It covers:
- what this test build is (bills stay on this PC; nothing goes to ERPNext yet);
- Windows 10/11 64-bit with internet for the first start;
- unzip anywhere, e.g. the Desktop, and run `TillPOS\TillPOS.exe`;
- the SmartScreen "More info → Run anyway" warning;
- the first-run setup: pick the receipt printer, Test print, check the drawer opens, Save;
- the PINs (cashier 1234, supervisor 9876);
- the keys: F2 search, F3 quantity, F11 card, F12 cash, Delete remove line (supervisor), Enter, Esc;
- the scanner must send Enter after the code, which is the default on most USB scanners;
- the cash drawer connects to the printer's DK/RJ11 port and opens on cash sales only;
- receipts are saved as files in `C:\ProgramData\TillPOS\receipts` when no printer is chosen;
- changing the printer later: Login screen → Settings → supervisor PIN;
- what to send back: `C:\ProgramData\TillPOS\errors.log`, photos of printed receipts, and the version shown on the login screen;
- not in this build yet: returns, hold/recall, price check, close shift.

Test: run the script; the zip exists; the zip's `settings.json` has a non-empty `ApiSecret` and `SetupDone=false`; `git status` shows nothing new to commit except the two committed files.

### Task 5: Controller acceptance (no subagent)

Unzip the package into a scratch folder. Point `TILLPOS_SETTINGS` at an empty scratch path so the import path is exercised. Then check:
- the setup window appears;
- the plain secret gets protected;
- the till starts;
- a sale completes and the popup shows the new invoice;
- a scan closes the popup and adds the item;
- "Print again" works with no printer (writes the text file again).

Record the results in this plan's acceptance section and commit.
