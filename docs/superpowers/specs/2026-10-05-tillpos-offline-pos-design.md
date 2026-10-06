# TillPOS — Offline-first POS for ERPNext (Design Spec)

**Date:** 2026-10-05
**Status:** Draft for review
**Mockups:** https://claude.ai/artifact/F4cMYKqs2xX5ctonfYCt2g (approved 2026-10-05)

---

## 0. Current focus (recorded 2026-10-06)

**We are building the till / cash counter system only — Phase 1A (Al Ain Market cash counters).** Everything in this spec and in Plans 1–4 is for the till.

The company-wide roadmap is in `docs/Master Technical Specification & Architecture Blueprint_ Unified POS System.docx`:

| Phase | What | Status |
|---|---|---|
| **1A — Al Ain Market cash counters** | Offline-first supermarket till | **Current work** |
| 1B — QuickGroc dark store | Picker / fulfilment mode | Future — not planned or built now |
| 2 — Kattcho cloud kitchen | KOT routing, modifiers, aggregator orders | Future — not planned or built now |

The Master Specification is the product authority for Phase 1A requirements; this spec is the detailed till design and is being aligned with it. Only "don't paint ourselves into a corner" choices are made for 1B/2 (e.g. no till-only assumptions baked into shared sync code); no 1B/2 features are built.

**Till hardware (confirmed 2026-10-06):** the tills now have **8 GB RAM** (upgraded). The Master Specification's 4 GB / 90 MB working-set budget is therefore not a hard constraint; this spec's target S7 (< 300 MB) stays, and the app should still be kept lean.

### Decisions recorded 2026-10-06

1. **Follow the data.** The till never corrects or second-guesses ERPNext data: whatever item ERPNext returns for a barcode is what is sold, at that item's price. (Data clean-up, if any, is the back office's business, not the till's.)
2. **Scale labels** (example: CUCUMBER label `2000089007400` → 3.50/kg × 0.740 kg = 2.59): EAN-13 starting with `2` = **`2` + item code (6) + weight in grams (5) + check digit**. The till looks the label up against the barcodes in the database — first the full 13 digits, then the first 7 digits (e.g. `2000089`), then digits 2–7 (e.g. `000089`) — and uses the first match; quantity = weight digits ÷ 1000 kg. Check digit is validated; an invalid check digit is treated as a mis-scan.
3. **Barcode with a unit the item doesn't have** (118 such barcodes on live): follow the data — sell the barcode's item in its **stock UOM** at its stock-UOM price instead of refusing it, and log it.
4. **Payment rounding:** **card = exact total, no rounding. Cash = rounded to AED 0.25** (AED's smallest fraction in ERPNext), using **ERPNext's rounding rule** so totals always agree with ERPNext. **Split:** card part exact, the cash remainder rounded to 0.25.
5. **Releases (Phase 1A):**
   - **1A-1 — core till:** everything in this spec, plus hold & recall bill, price check, scale-label barcodes, blind cash count at shift close, supervisor approvals as in the Master Spec (line void, bill void, returns without receipt or above AED 50, no-sale drawer open), and the FTA QR code on receipts.
   - **1A-2 — follow-up:** staff / hotel-guest / line / bill discounts, loyalty points, foreign-currency cash, customer pole display, RS-232 bench scale, RFID supervisor badges.

## 1. Problem and goal

The shop runs ERPNext 15.114.0 / Frappe 15.113.0 / POS Awesome 15.35.2 on a Docker server. The 4 till machines (Intel i5 3rd gen, 2 cores, 8 GB RAM, touchscreen, USB thermal printer, USB barcode scanner, cash drawer on the printer) cannot run POS Awesome fast enough: loading ~12,000 items in the browser makes billing slow. The same POS runs fine on a modern laptop, and all POS Awesome tuning has already been tried.

**Goal:** a native Windows till application (`TillPOS.exe`) that bills instantly from a local database, keeps working without internet, and syncs with live ERPNext in the background — the same model as the shop's previous POS system.

**Success criteria**

| # | Criterion | Target |
|---|---|---|
| S1 | Barcode scan → line on bill | < 100 ms |
| S2 | App start → ready to log in | < 10 s |
| S3 | Billing continues with internet down | Unlimited time, unlimited bills |
| S4 | Every bill reaches ERPNext exactly once | 0 lost, 0 duplicated |
| S5 | Item / price / offer change on live → visible on till | ≤ 3 min while online |
| S6 | First full download of ~12,000 items | < 5 min |
| S7 | Memory use on till | < 300 MB |

## 2. Scope

**In scope (v1)**
- Cashier PIN login (works offline), supervisor PIN for protected actions.
- Open shift with opening cash → POS Opening Entry.
- Sale: scan barcode, search by name, change quantity (cashier cannot change price), remove line, void bill (supervisor).
- Automatic item-level offers from ERPNext Pricing Rules (by Item, Item Group or Brand, with dates).
- Payment: Cash (tendered + change), Card (recorded only — separate card machine), Split cash + card.
- Receipt printing (ESC/POS) and cash drawer opening.
- Returns against a receipt (from any till), and returns without receipt (supervisor PIN).
- Close shift with counted cash and card totals → POS Closing Entry.
- Background two-way sync with live ERPNext; sync status always visible.
- Single Retail price list; one shop; 4 tills.

**Out of scope (v1)** — can be added later
- Hold/park bills, customer selection, loyalty points, credit sales, coupons.
- Quantity-based or bill-level offers (buy X get Y, min qty, bill threshold).
- Multiple price lists, multi-currency, multiple companies.
- Tax rows other than "On Net Total" (e.g. Actual amount, On Previous Row).
- Weighing scales, price-embedded barcodes, customer-facing display.
- Editing items or prices on the till (admin does this in ERPNext only).

## 3. Architecture

**Approach chosen:** each till is standalone, with its own SQLite database, and syncs directly with live ERPNext over HTTPS using the standard Frappe REST API. No changes to the Docker image or server code. (A shop hub server and a local ERPNext replica were considered and rejected — see §12.)

```
┌─────────── Each till (x4) ───────────────┐          ┌──── Live ERPNext (Docker) ────┐
│  TillPOS.exe  (.NET 8, WPF)              │          │                               │
│                                          │  HTTPS   │  Standard REST API            │
│  Screens ──► Pricing ──► Local DB        │◄────────►│  (API key per till)           │
│              engine      (SQLite)        │          │                               │
│     │                       ▲            │          │  + custom fields              │
│     ▼                  Sync worker       │          │  + "POS Cashier" doctype      │
│  Printer + cash drawer  (background)     │          │  (all created in the UI)      │
└──────────────────────────────────────────┘          └───────────────────────────────┘
```

### 3.1 Technology
- .NET 10 (LTS, supported until November 2028), C#, WPF with MVVM (CommunityToolkit.Mvvm).
  *.NET 8 support ends November 2026 and .NET 9 is a short-term release, so .NET 10 is the current long-term choice.*
- SQLite via `Microsoft.Data.Sqlite` (plain ADO.NET, no ORM — money is stored as invariant-culture text and parsed to `decimal`, never floating point), WAL mode, FTS5 for name search.
- Published as a self-contained single-file `win-x64` build (the .NET runtime is bundled, nothing to install separately); installed with an Inno Setup installer.
- Requires Windows 10 or 11 (64-bit) on each till.

### 3.2 Projects (one job each)

| Project | Responsibility | Depends on |
|---|---|---|
| `TillPOS.Core` | Domain models, cart, pricing engine, totals and rounding, receipt numbering, return limits. Pure logic, no I/O. | — |
| `TillPOS.Data` | SQLite schema + migrations, repositories. | Core |
| `TillPOS.Erp` | ERPNext REST client and DTO ↔ domain mapping. The only code that knows ERPNext's API. | Core |
| `TillPOS.Sync` | Puller (live → till), Pusher (till → live), scheduler, connectivity and clock checks. | Core, Data, Erp |
| `TillPOS.Printing` | ESC/POS receipt builder, raw printing via the Windows spooler, cash drawer kick. | Core |
| `TillPOS.App` | WPF screens and view models, first-run setup, app startup. | all |
| `TillPOS.Tests` | xUnit tests for every project above. | all |

## 4. ERPNext setup (done once by the admin, all through the ERPNext UI)

1. **Stock Settings → Allow Negative Stock = on** (agreed): offline bills must never be rejected for stock.
2. **One ERPNext user per till** (e.g. `till1@shop.local` … `till4@shop.local`), with an API key/secret and a role `TillPOS Device` that can: read Item, Item Barcode, Item Price, Item Group, Brand, Pricing Rule, POS Profile, Sales Taxes and Charges Template, Item Tax Template, Company, Currency, Address, Mode of Payment, Deleted Document, POS Cashier, POS Invoice; create/submit POS Invoice, POS Opening Entry, POS Closing Entry.
   *Why one user per till:* ERPNext's POS Closing Entry collects the POS Invoices **owned by the closing user**, so all of a till's documents must be created by that till's user. The human cashier is recorded separately in a custom field.
3. **One POS Profile per till** (warehouse, Retail price list, payment modes Cash and Card, write-off/change accounts), each linked to that till's user.
4. **Custom fields** (Customize Form):
   - POS Invoice: `custom_offline_id` (Data, unique, read only), `custom_cashier` (Data, read only), `custom_till` (Data, read only).
   - POS Opening Entry and POS Closing Entry: `custom_offline_id` (Data, unique, read only).
5. **Custom DocType `POS Cashier`** (created with "Custom?" ticked): `cashier_name` (Data), `user` (Link → User; the cashier's ERPNext user, recorded on invoices as `posa_cashier`), `pin` (Data, permission level 1; 4–6 digits, unique), `is_supervisor` (Check), `enabled` (Check). Permission level 1 is readable only by System Manager and `TillPOS Device`, so other ERPNext users cannot see PINs. The till stores only a salted hash of each PIN locally.
   *Note:* a 4-digit PIN is a convenience lock against casual misuse, not strong security; the protection that matters is that only admins and till devices can read the field.

## 5. Local database (SQLite, one file per till)

| Table | Purpose |
|---|---|
| `item` | item_code, item_name, item_group, brand, stock_uom, disabled, is_sales_item, modified |
| `item_barcode` | barcode (unique index), item_code, uom |
| `item_uom` | item_code, uom, conversion_factor |
| `item_price` | item_code, uom, price_list_rate, valid_from, valid_upto (Retail only) |
| `item_group` | name, parent, lft, rgt (for group-level offers on parent groups) |
| `pricing_rule` + `pricing_rule_target` | rule header and its item / group / brand targets |
| `tax_template`, `tax_template_row` | the POS Profile's sales tax template: rate, account, included-in-price flag |
| `item_tax` | item or item group → Item Tax Template and its rate override |
| `cashier` | cashier id, name, pin_hash, pin_salt, is_supervisor, enabled |
| `pos_settings` | synced POS Profile and Company values: company, address, TRN, warehouse, customer, payment modes, tax template, write-off limit, currency smallest fraction, rounding, change account |
| `shift` | local shift: offline_id, cashier, opened_at, closed_at, opening/counted amounts, sync status, erp names |
| `receipt`, `receipt_line`, `receipt_payment` | every bill made on this till (sale or return), with sync status: `pending` / `synced` / `failed`, erp_name, last_error |
| `remote_receipt`, `remote_receipt_line` | last 30 days of POS Invoices from all tills, for returns |
| `sync_state` | per-doctype high-water mark (server `modified` timestamp) |
| `app_log` | rolling local log for troubleshooting |

Scanning uses the unique index on `item_barcode.barcode`; name search uses an SQLite FTS5 index on `item_name`, returning the top 20 matches.

## 6. Sync

### 6.1 Pull (live → till)
- Runs every **90 seconds** while online, and on demand after login.
- For each doctype, request rows with `modified > last high-water mark`, ordered by `modified`, in pages of 500, and store the newest `modified` seen as the new mark (server time, never till time).
- Doctypes: Item (with Item Barcode, UOM conversion and Item Tax child rows), Item Price (Retail only), Item Group (with Item Tax rows), Pricing Rule (selling, with its child target tables), POS Profile (this till's), Sales Taxes and Charges Template (the POS Profile's), Item Tax Template, Company (name, address, TRN), Mode of Payment, POS Cashier, POS Invoice (last 30 days, all tills, with items — for returns).
- **Deletions:** read `Deleted Document` entries for these doctypes created since the last mark and remove them locally. Disabled items and rules are kept but excluded from scanning/pricing.
- First run downloads everything (target < 5 min for ~12,000 items).
- Each page is written in one SQLite transaction so the cashier never sees half-updated data.

### 6.2 Push (till → live)
- Triggered immediately after each completed bill, shift open or shift close, and retried every 30 s with backoff up to 5 min while failing.
- **Order:** POS Opening Entry → that shift's POS Invoices (oldest first) → POS Closing Entry. A return is never pushed before the sale it returns (when that sale was made on this till).
- **Idempotency:** before creating a document, look it up by `custom_offline_id`; if it exists, record its ERPNext name and mark it synced. The ID is created on the till and never changes.
- **POS Invoice payload:** `is_pos=1`, `pos_profile`, `company`, `customer` (POS Profile default), `set_posting_time=1` with the real sale date and time, `selling_price_list=Retail`, `ignore_pricing_rule=1` (so ERPNext keeps the price the customer actually paid), items with `qty`, `uom`, `conversion_factor`, `price_list_rate`, `discount_percentage` / `discount_amount`, `rate`, `warehouse`, `item_tax_template`; `taxes_and_charges` (the POS Profile's template); `payments` per mode; `custom_offline_id`, `custom_cashier`, `custom_till`; submitted directly (`docstatus=1`).
- **Returns:** `is_return=1`, negative quantities, `return_against` = ERPNext name of the original (resolved at push time from the local or remote receipt). Returns without receipt are sent without `return_against`.
- **POS Closing Entry:** built from the shift's invoices (all must be synced first) with `payment_reconciliation` rows (opening, expected, counted). ERPNext then consolidates the POS Invoices into Sales Invoices as it does today.

### 6.3 Receipt numbers
- Format `T<till>-<6-digit sequence>`, e.g. `T2-000457`. The sequence is stored in SQLite and never reused.
- This number is the `custom_offline_id`, is printed on the receipt as text and as a CODE128 barcode, and is what the Return screen scans.

### 6.4 Connectivity and clock
- Online check: authenticated `GET /api/method/frappe.auth.get_logged_user` with a 5 s timeout.
- On each successful call the server's `Date` header is compared with the till clock; more than 5 minutes off shows a warning, because sale times come from the till clock.

## 7. Pricing engine (in `TillPOS.Core`)

For a line with item *I*, UOM *U*, quantity *q*, at sale time *t*:

1. **Base price** = Item Price (Retail, *I*, *U*, valid at *t*); if none for *U*, Item Price for the stock UOM × conversion factor. No price → the item cannot be sold and the cashier sees "No price — tell supervisor".
2. **Candidate rules** = enabled selling Pricing Rules, price list blank or Retail, valid at *t*, `price_or_product_discount = Price`, matching *I* by Item Code, by Brand, or by Item Group (including parent groups via `lft/rgt`).
3. Rules with conditions v1 does not support — `min_qty`/`max_qty`/`min_amt`/`max_amt` > 0, an `applicable_for` customer condition, or product (free item) discounts — are **skipped** and listed once in the log.
4. **Choose one rule:** highest `priority`; tie → most specific (Item Code > Brand > Item Group); tie → largest discount for the customer.
5. **Apply:** Discount Percentage, Discount Amount (per unit), or Rate (fixed price).
6. **Taxes (UAE VAT 5%):** see §7.1.
7. **Totals:** line amounts rounded to the currency precision; bill rounding follows the POS Profile / company setting (rounded total unless "Disable Rounded Total" is on), matching ERPNext so the paid amount always equals ERPNext's grand total.

### 7.1 Taxes (UAE VAT)

Taxes come from ERPNext exactly as they do today; the till does not invent tax rates.

- The till syncs the **Sales Taxes and Charges Template** set on its POS Profile (rows with `charge_type = On Net Total`, `rate`, `account_head`, and the **"Is this Tax included in Basic Rate?"** flag), plus any **Item Tax Template** attached to items or item groups (e.g. a 0% item overriding the 5% default).
- **Tax-inclusive prices** (flag on — the usual UAE retail setup, shelf price includes VAT): the bill total equals the sum of line amounts; VAT is back-calculated (`VAT = amount × 5 / 105`) and shown as "VAT 5% (included)".
- **Tax-exclusive prices** (flag off): VAT is added on top of the net total.
- The calculation is a port of ERPNext's own `taxes_and_totals` logic for "On Net Total" rows, including its per-line rounding, so the till's grand total equals ERPNext's. This is covered by tests that compare the till's totals with ERPNext's for a set of sample bills on the test site (§11).
- Safety net: the POS Profile's **write-off limit** is set to a small amount (e.g. 0.05) so a rounding difference of a few fils can never block an upload; any write-off is logged.
- The POS Invoice is sent with the same `taxes_and_charges` template, so ERPNext books VAT to the same accounts as today.
- Tax templates of other charge types (Actual, On Previous Row …) are not supported in v1; if the POS Profile uses one, the till refuses to start billing and shows "Tax setup not supported — contact admin".

**Receipt as a UAE simplified tax invoice:** the printed receipt carries the words "Tax Invoice", the shop name and address, the company **TRN** (from Company `tax_id`), date and time, receipt number, item descriptions with quantities and amounts, total including VAT, and the VAT amount. Return slips are titled "Tax Credit Note" and reference the original receipt number.

## 8. Screens (per approved mockups)

1. **Login** — PIN pad; works offline; then open shift with opening cash amount.
2. **Sale** — scan box always focused; last-added confirmation; bill lines with − / + / remove; offer tags; totals including a "VAT 5%" line (shown as "included" when prices are tax-inclusive); large PAY; shortcuts: F2 search, F3 set quantity, F6 return, F7 reprint, F12 pay, Void bill (supervisor), Close shift; footer with last receipt, bills waiting, item count and last update.
3. **Payment** — Cash / Card / Split; quick cash buttons; number pad; live change; Complete & print (Enter), Back (Esc).
4. **Return** — scan receipt barcode or type number; choose return quantities (cannot exceed sold minus already returned); refund by Cash or Card; reason; Confirm & print slip. Returns without receipt need supervisor PIN.
5. **Close shift** — summary; expected vs counted cash and card with difference; warning if bills are still waiting to upload; Print summary; Close shift & log out.
6. **Upload problems** (supervisor PIN) — list of failed bills with the ERPNext error, Retry and View buttons.
7. **First-run setup** (admin) — server URL, API key/secret, till number, printer selection, paper width; then initial download with progress.

All touch targets ≥ 44 px; every action has a keyboard shortcut; the scanner works as keyboard input without touching the screen. Minimum layout 1366×768, scaling up to larger screens.

## 9. Printing

- Receipts are built as ESC/POS bytes and sent raw through the Windows spooler to the configured printer (works with standard thermal printer drivers).
- Paper width is a setting on the till (first-run setup and supervisor settings): 80 mm (48 columns, default) or 58 mm (32 columns). The receipt layout adapts to the chosen width.
- Receipt (UAE simplified tax invoice, §7.1): "Tax Invoice" title, shop name and TRN (Company), address (POS Profile's company address), a footer line set on the till (e.g. return policy), receipt number, date/time, till, cashier, lines (name, qty × price, offer, amount), subtotal, discount saved, VAT 5% amount, total including VAT, payments, change, CODE128 barcode of the receipt number, paper cut. Return slips print as "Tax Credit Note" with the original receipt number.
- Cash drawer kick (`ESC p`) after cash and split payments and cash refunds.
- Printer errors never lose a bill: the bill is already saved; the cashier sees "Printer problem — Reprint (F7)".

## 10. Error handling

| Situation | Behaviour |
|---|---|
| Internet down | Billing continues; pull/push retry with backoff; status 🟡 with count of waiting bills. Queue has no size limit. |
| Power cut / crash mid-sale | Cart is saved after every change and restored on restart. Completed bills are committed to SQLite **before** printing. |
| Price/offer changed on live after an offline sale | Bill uploads with the charged price (`ignore_pricing_rule=1`). |
| ERPNext rejects a bill | Bill marked `failed` with the error text; status 🔴; other bills keep uploading; supervisor can view and retry from Upload problems. |
| Shift closed while bills are waiting | Closing Entry waits until all that shift's bills are synced, then uploads automatically. |
| No successful pull for 24 h | Banner: "Prices may be out of date". |
| Till clock off by > 5 min | Warning banner. |
| Duplicate upload after a dropped connection | Prevented by the `custom_offline_id` lookup. |
| Unknown barcode | Error beep and message; nothing added. |
| SQLite file damage | Daily copy of the database to a local backup folder (last 7 kept); restore procedure documented. |

**Security:** API key/secret encrypted on disk with Windows DPAPI; HTTPS only; each till user has only the permissions in §4; PIN hashes only on the till.

## 11. Testing

- **Unit tests (Core):** pricing rule selection and application, VAT (inclusive and exclusive, item tax overrides), rounding, totals, split payments, change, return limits, receipt numbering.
- **Data tests:** migrations and repositories against a temporary SQLite file.
- **Sync tests:** puller and pusher against a fake ERPNext client — paging, high-water marks, deletions, push order, idempotency, retry/backoff, failed bills.
- **Integration tests (opt-in):** against a **test copy** of the live site (restored from the backup taken earlier), never against live: create a POS Opening Entry, invoices, a return, and a POS Closing Entry, then verify ERPNext's totals match the till's.
- **On-till acceptance checklist:** first sync of 12,000 items, scan speed, 30 minutes of billing with the network cable unplugged then reconnected, power-off during a sale, printer out of paper, shift close with bills waiting.

## 12. Alternatives considered

- **Shop hub server + thin tills:** solves cross-till returns during an outage, but needs an always-on extra machine and, for resilience, a local cache on each till anyway — roughly double the work. Rejected for v1.
- **Local ERPNext replica in the shop:** too heavy for the tills; ERPNext has no supported two-way database sync and stock/accounting conflicts are likely. Rejected.

## 13. Decisions confirmed by the shop (2026-10-05)

1. **Taxes:** UAE VAT 5%; prices and taxes come from ERPNext and VAT shows on the bill → handled by §7.1 (inclusive or exclusive, following the template flag).
2. **Runtime:** .NET 10 LTS instead of .NET 8.
3. **Screen:** 1366×768 layout as in the mockups.
4. **Paper width:** configurable, 80 mm default, 58 mm supported.
5. **Tills run Windows 11 (some may run Windows 10)** — both are compatible with .NET 10 self-contained `win-x64`.

## 13a. Live findings (read-only checks on erp.quickgroc.com, 2026-10-06)

Full results: `docs/erp-api-notes.md`. Facts that change or sharpen this spec:

1. **Currency precision is 3**, not 2. All till money maths runs at precision 3 (configurable; verified: 40/40 real invoices recalculated identically).
2. **No Pricing Rules, POS Offers or POS Coupons exist.** Offers today are made by the admin editing Item Prices; the till already handles that. Pricing Rule support stays for when they are used.
3. **Weighed items are sold** (571 `Kg` items; weights like 0.305 kg on real bills, recorded with the item's short barcode such as `2000088`). This replaces the earlier answer "everything is a fixed-price pack". The till must accept weighed lines — **open decision D1** below.
4. **Shifts use POS Awesome's own doctypes:** `POS Opening Shift` (42) / `POS Closing Shift` (37), linked from each POS Invoice via `posa_pos_opening_shift`; ERPNext `POS Opening Entry` / `POS Closing Entry` have never been used (0). Consolidation into Sales Invoices happens via POS Awesome's closing (`POS Invoice Merge Log`: 9; 1,967 POS Invoices consolidated). §6.2 assumed ERPNext's entries — **open decision D2** below.
5. **Real counters round the total** (`disable_rounded_total = 0`); the "Test Counter" profile does not. Each till's POS Profile should be copied from a real counter.
6. **118 barcodes point to a UOM the item does not have**; the till refuses them (no guessing). The admin should fix them in ERPNext (list provided locally).

### Open decisions (needed before Plan 2/3)

- **D1 — Weighed items:** how is the weight captured today? (a) scale-printed labels with the weight inside the barcode (give the label format: prefix, item-code digits, weight/price digits, check digit), (b) cashier types the weight after scanning, or (c) a scale connected to the till PC. The till will support the chosen way; (a) and (b) need no extra hardware integration.
- **D2 — Shifts: DECIDED 2026-10-06 — use POS Awesome shifts (recommended option below).** Recommended: the till opens/closes shifts as **POS Awesome `POS Opening Shift` / `POS Closing Shift`** and sets `posa_pos_opening_shift` on each POS Invoice, so the back office keeps one shift and closing process for both POS Awesome and TillPOS. Alternative: ERPNext's standard POS Opening/Closing Entry (as originally written in §6.2), which would split shift reporting in two.

## 13b. Plan 2 design details (2026-10-06)

Plan 2 is split in two so each part is testable on its own:

- **Plan 2a — offline sales engine** (no ERPNext writes): scale labels and unit fallback in the cart; cash/card/split payment calculation; recording completed bills in an outbox with client IDs; returns (with and without receipt); hold & recall; shift open/close with blind count; supervisor PINs and the approval log; the cashier list download.
- **Plan 2b — upload to ERPNext** (needs a writable test site): POS Invoice upload, POS Awesome shift upload, recent-receipts download for cross-till returns, upload-problems handling, approvals upload.

Details decided for both:

1. **Client ID** per bill: `TILL{n}-{yyyyMMddHHmmss}-{6-digit sequence}` (Master Spec format), printed on the receipt. For upload, POS Awesome already stores a client request ID on every POS Invoice (`posa_client_request_id`); Plan 2b will use that field instead of adding `custom_pos_client_id`, if a test-site check confirms it can be looked up reliably — otherwise it falls back to the Master Spec's `custom_pos_client_id`.
2. **Cashiers:** the `POS Cashier` list (spec §4) gets a `user` link to the cashier's ERPNext user, because POS Awesome records the cashier on each invoice as an ERPNext user (`posa_cashier`). Supervisors are cashiers with `is_supervisor`. PINs are 4–6 digits and must be unique; the till stores only salted PBKDF2 hashes.
3. **Shifts upload** (2b): `POS Opening Shift` / `POS Closing Shift` are created under the till's ERPNext user; each invoice carries `posa_pos_opening_shift` and `posa_cashier`.
4. **Payments in ERPNext terms:** cash-only bills use ERPNext's rounded total (rounding adjustment → Round Off account); card-only bills set `disable_rounded_total = 1`; the cash amount on an invoice is the cash tendered and `change_amount` the change, as POS Awesome does. How the small rounding difference of a **split** bill is posted (write-off vs. change) is a 2b test-site check.
5. **Supervisor approval needed for** (Master Spec §5): removing a line, voiding the bill, a return without receipt, a refund above AED 50, opening the drawer without a sale. Each approval is logged locally (who, what, amount, when) and uploaded in 2b.
6. **Weighed lines** keep the label's weight as quantity and are never merged with another line; + / − do not apply to them.

## 14. Verification items for the implementation plan

These are known ERPNext v15 API details to confirm against the test site in the first tasks of the plan, each with a stated fallback:

- Fetching Item Barcode / UOM / Pricing Rule child rows in bulk via `get_list` with child-table fields. Fallback: list the child doctype directly with `parent` filter.
- POS Invoice return without `return_against` is accepted. Fallback: returns without receipt are pushed as a Sales Invoice return (`is_pos=1`, `is_return=1`, `update_stock=1`), which ERPNext accepts without `return_against`; the Closing Entry shows them as a separate line.
- The till's tax and rounding results equal ERPNext's for sample bills (inclusive VAT, mixed offers, a 0% item, a return). Fallback: rely on the POS Profile write-off limit and log every difference until the port is corrected.
- The exact payload ERPNext v15 requires for POS Closing Entry created via REST (including how `pos_transactions` is filled). Fallback: call ERPNext's own `get_pos_invoices` helper to build it.
