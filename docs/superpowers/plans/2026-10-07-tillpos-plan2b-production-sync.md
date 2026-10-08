# TillPOS Plan 2b: Production Sync (Upload to ERPNext)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`) syntax.
> **Status: PLAN ONLY.** No transaction is sent to ERPNext until the shop owner starts a live sync test (Task 9) or the production cutover (Task 10). Every build stays at `Upload = Off` until then.

**Goal:** Upload everything the tills record offline into the live ERPNext, the same way POS Awesome does today:
- POS Opening Shift, POS Invoices (sales and returns), POS Closing Shift and supervisor approvals.
- Never upload a bill twice. Never lose one. Keep selling when the internet is down.
- Make every failure visible and retryable.

**Architecture:**
- **Upload pipeline:** a new pipeline in `TillPOS.Sync` (namespace `TillPOS.Sync.Upload`) reads the local outbox (receipts, shifts, approvals) and builds ERPNext payloads with pure, unit-tested builders. It sends them in a fixed order through `IErpClient`.
- **Three modes:**
  - **Off** builds nothing.
  - **DryRun** builds the payloads, checks every reference read-only (items, UOMs, warehouse, accounts, modes of payment, customer, the shift) and writes the payload JSON to `outbox-preview\` for inspection, with no writes.
  - **Live** inserts and submits.
- **Background loop:** the upload runs in the existing background sync loop (after the catalog pull), with backoff.

**Spec:** `docs/superpowers/specs/2026-10-05-tillpos-offline-pos-design.md` §4 (ERPNext setup), §6.2 (push), §10 (error handling), §13a/§13b (POS Awesome shifts, client IDs, payments in ERPNext terms), §14 (verification items).

## What live ERPNext looks like today (read-only check, 2026-10-07)
POS Awesome writes these documents (field names from the newest live documents):

- **POS Opening Shift**
  - Fields: `period_start_date`, `posting_date`, `company`, `pos_profile`, `user`, `status`.
  - Child `balance_details`: `mode_of_payment`, `amount`.
- **POS Invoice**
  - Shift and client fields: `posa_pos_opening_shift`, `posa_cashier`, `posa_client_request_id`, `posa_is_printed`.
  - Standard fields: `is_pos`, `pos_profile`, `customer`, `posting_date`/`posting_time`, `selling_price_list`, `set_warehouse`, `taxes_and_charges`, totals including `rounded_total`, `account_for_change_amount`, `write_off_account`/`write_off_cost_center`.
  - `items`: `barcode`, `item_code`, `qty`, `uom`, `conversion_factor`, `stock_qty`, `price_list_rate`, `rate`, `amount`, `warehouse`, `item_tax_rate`, `posa_row_id`, `posa_offers`, `income_account`, `cost_center`.
  - `payments`: `mode_of_payment`, `amount`, `account`, `type`, `default`, `posa_*` currency fields.
  - `taxes`: `charge_type`, `account_head`, `rate`, `included_in_print_rate`, `item_wise_tax_detail`.
- **POS Closing Shift**
  - Fields: `pos_opening_shift`, `period_start_date`/`period_end_date`, `posting_date`, `company`, `pos_profile`, `user`, `grand_total`, `net_total`, `total_quantity`.
  - Children: `pos_transactions` (`pos_invoice`, `posting_date`, `customer`, `grand_total`), `pos_payments`, `payment_reconciliation` (`mode_of_payment`, `expected_amount`, `difference`, plus the opening/closing amounts in the full schema) and `taxes` (`account_head`, `rate`, `amount`).

The till fills the same fields, so the back office keeps one shift and closing process for POS Awesome and TillPOS.

## Global Constraints
- **No ERPNext writes unless `Upload = Live`.** `Upload` is a till setting (`Off` | `DryRun` | `Live`) with default `Off`. Field-test packages are always built with `Off`. Changing it needs the supervisor PIN in Settings and is written to the approval log. Live mode shows a permanent "LIVE UPLOAD" badge in the header.
- **Idempotency:**
  - Every document carries the till's client ID: invoices in `posa_client_request_id`, and shifts in a `custom_offline_id` field, which is admin setup.
  - Before any insert, look the ID up (read-only). If it already exists, adopt its ERPNext name and mark it synced. The client ID never changes.
- **Upload order per shift:**
  1. Opening Shift.
  2. Invoices, oldest first. A return goes only after its original when the original is on this till.
  3. Closing Shift, only when every invoice of that shift is synced.
  4. Approvals, after the invoices they mention.
- **Money as charged:** `ignore_pricing_rule = 1`. The rates, VAT and rounding the customer was charged are sent; ERPNext must not recalculate a different price. Any difference beyond the POS Profile write-off limit is a failure, not a silent change.
- **Rounding follows the till's POS Profile.** Real counters use a rounded total (`disable_rounded_total = 0`); "Test Counter" does not. Verify `PaymentCalculator` honours `DisableRoundedTotal`; the final Plan 3 review noted it may ignore it, so fix that here if needed.
- **One ERPNext user, API key and POS Profile per till** (spec §4). A key is never shared between tills in production.
- **Never block selling.** Uploads run in the background; a failure shows in the header and in "Upload problems", and the other bills keep uploading.
- **Test data never reaches production.** The field-test databases are discarded at cutover (Task 10).

## Review Focus
1. **Connection drop after ERPNext saved the invoice but before the till heard back:** the next attempt finds it by client ID and does not create a duplicate.
2. **A return whose original sale is still waiting or failed:** the return waits; it is never sent without `return_against` unless it was a no-receipt return.
3. **A shift closed while some bills failed:** the Closing Shift waits, and the header says why.
4. **The rounding or VAT that ERPNext calculates differs by 0.01 from the till:** the result is within the write-off limit or a visible failure, never silently re-priced.
5. **DryRun / Off mode:** a code path that writes anyway is impossible. Enforce this structurally, not by convention: a write-capable client exists only in Live mode.

---

### Task 1: Upload mode switch and the write guard
- `TillSettings.Upload` (`Off`|`DryRun`|`Live`, default `Off`).
- `IErpWriter`, the only interface with Insert/Submit methods. It is constructed only in Live mode. In Off and DryRun mode the app holds a `NoWriteErpWriter` that throws if anything calls it, and a test enforces that.
- Settings screen: an Upload mode selector behind the supervisor PIN, logged as `ApprovalAction.UploadModeChange`. Header badge for Live.
- `publish-field.ps1` always writes `"Upload": "Off"`.

### Task 2: Payload builders (pure, unit-tested)
- **`PosInvoicePayload.FromReceipt(Receipt, ShiftUploadInfo, PosSettings, CashierMap)`** handles sales and returns:
  - `is_pos=1`, `pos_profile`, `company`, `customer` (the profile default), `set_posting_time=1`, `posting_date`/`posting_time` (sale time), `selling_price_list`, `set_warehouse`, `ignore_pricing_rule=1`, `taxes_and_charges`.
  - Items: `item_code`, `qty`, `uom`, `conversion_factor`, `price_list_rate`, `rate`, `barcode`, `warehouse`, `item_tax_template`, `posa_row_id` = the till line number.
  - Payments per mode: cash = tendered, plus `change_amount`. Split bills follow the §13b.4 rule, verified in Task 9.
  - `disable_rounded_total` per the profile and the payment kind.
  - `posa_pos_opening_shift` (the ERPNext name of the uploaded opening shift), `posa_cashier` (the cashier's ERPNext user), `posa_client_request_id` (the client ID), `custom_till`.
  - Returns add `is_return=1`, negative quantities, and `return_against` = the ERPNext name of the original.
- **`OpeningShiftPayload`** and **`ClosingShiftPayload`**. The closing payload holds `pos_transactions` (all the shift's invoice names), `payment_reconciliation` (opening, expected, counted, difference from the local `ShiftClosing`) and the totals. Prefer POS Awesome's own server method for building and submitting the closing shift if one exists (check read-only in Task 9 step 1); otherwise use REST insert and submit.
- **`ApprovalPayload`** for the custom DocType `TillPOS Approval` (Task 7).
- **Golden tests:** build payloads for representative bills (cash rounded, card exact, split, weighed, unit fallback, offer, return with receipt, return without receipt). Compare their shape to the field list above, and their totals to the till's own totals.

### Task 3: Upload engine
- **`Uploader.RunOnceAsync(ct)`** is called by the background loop after the catalog pull:
  - It walks the shifts in order and applies the Global Constraints order.
  - For each document it looks up the client ID, then inserts with `docstatus=1` (Live) or writes the preview (DryRun).
  - It records the ERPNext name, or the failure with the ERPNext error text and an attempt count.
- **Backoff:** 30 s, doubling to 5 min, per document. The other documents continue.
- **`return_against` resolution:**
  - a local original: its synced ERPNext name, or wait if it isn't synced yet;
  - a cross-till original (Task 5): the name recorded at download.
- **Closing Shift:** sent only when every invoice of the shift is synced.
- **Existing outbox fields:** sync_status, error and attempts already exist in `ReceiptStore`; the same fields are added for shifts and approvals.
- **Tests:** FakeErp-based coverage of the ordering, idempotency (an insert succeeds but the response is lost, so the retry adopts the existing document), failures, backoff, closing waiting, return waiting, and DryRun writing previews with zero insert calls.

### Task 4: DryRun validation (read-only)
In DryRun mode, before writing a preview, check every reference with read-only calls (cached per run):
- item exists and is enabled; the UOM is on the item (or `UomFallbackFrom` is handled);
- the warehouse, mode of payment and accounts on the POS Profile exist;
- the customer exists; tax template accounts exist;
- the shift's opening exists (DryRun treats the till's local opening as "would be created").

Problems show in "Upload problems" exactly as Live failures would. **This mode can run safely against live ERPNext now**, and is the main tool to prove the payloads before any write.

### Task 5: Cross-till returns (read-only download)
- **Pull feed `RecentInvoicesFeed`:** submitted POS Invoices of the company for the last 30 days, with items, stored as "remote receipts" (ERPNext name, `posa_client_request_id`, lines, payments, already-returned quantities from return invoices).
- **Return screen:** when the receipt number isn't local, search the remote receipts by client ID or ERPNext name. A return builds `return_against` = the ERPNext name. Returnable quantities count returns from all tills.
- Read-only, so it can be built and tested against live now.

### Task 6: Upload problems and header
- **Header:** "N waiting · M failed". Clicking it, with the supervisor PIN, opens **Upload problems**: a list of failed documents with the ERPNext error, Retry (resets backoff), View (preview JSON), and Mark as handled (supervisor, logged; for documents fixed by hand in ERPNext).
- **Close shift:** warns when bills of this shift are still waiting ("They will upload automatically; the Closing Shift waits for them").

### Task 7: Approvals upload
- **Admin creates** the custom DocType **TillPOS Approval** (the Task 8 checklist): action, cashier, supervisor, shift (Link POS Opening Shift), invoice (Link POS Invoice, optional), item_code, amount, reason, at (Datetime), till, custom_offline_id (unique).
- **Upload:** the uploader sends approval rows after the invoices they reference, with the same idempotency and failure handling.

### Task 8: Production ERPNext setup checklist (admin, through the ERPNext UI)
Written as an admin document in `docs/erpnext-production-setup.md`:
1. **One ERPNext user per till** (`till1@…` … `till4@…`), each with its own API key, and the role `TillPOS Device`, with read permissions per spec §4 plus create/submit on POS Invoice, POS Opening Shift, POS Closing Shift and TillPOS Approval.
2. **One POS Profile per till, copied from a real counter profile:** rounded total on, Cash/Card modes, write-off account and limit (e.g. 0.05), change account, warehouse, price list "Standard Selling", taxes "UAE VAT 5% - AAML", linked to that till's user.
3. **The POS Cashier DocType** with `user` link and PIN at permission level 1. Create the shop's cashiers and supervisors; the test PINs stop working once these sync.
4. **Custom fields:** `custom_offline_id` (unique) on POS Opening Shift, POS Closing Shift and TillPOS Approval, and `custom_till` on POS Invoice. Invoices use the existing `posa_client_request_id`.
5. **The TillPOS Approval DocType.**
6. **Stock Settings → Allow Negative Stock = on** (agreed).
7. **Company Tax ID (TRN) and a Company address** marked "Is Your Company Address", linked on each POS Profile. The real QR replaces the sample QR automatically.

### Task 9: Live sync test (only when the shop owner says go)
Run with the owner present, against the live site (or a test site if one is available), using the "Test Counter" profile and the test user.
1. **Read-only first:**
   - check whether POS Awesome exposes server methods for opening/closing shifts (`posawesome.posawesome.api.*`), and which ones the till user may call;
   - confirm `posa_client_request_id` can be filtered with `get_list`;
   - confirm a POS Invoice return without `return_against` is accepted (spec §14), otherwise use the Sales Invoice return fallback.
2. **DryRun** on a till with real test bills; fix every reported problem.
3. **Switch one till to Live** for a short session of about 5 bills: cash rounded, card, split, a weighed item, a return; then close the shift.
4. **Verify in ERPNext:**
   - the documents and their totals match the receipts;
   - the stock moved (after consolidation);
   - the Closing Shift reconciles and the approvals are present.
5. **Cancel the test documents in ERPNext** (the owner decides: cancel/amend or keep), or run the test on a dedicated test warehouse and customer so nothing touches real stock reports.
6. **Record the results** in this plan, and fix anything found.

### Task 10: Production cutover
1. **ERPNext checklist done** (Task 8).
2. **For each till:**
   - install the production build (no LocalTestCashiers, no sample QR, Upload = Live, the till's own user, key and POS Profile);
   - **start with an empty database**, discarding the field-test data;
   - complete first-run setup and a test print.
3. **First day:** watch the header counts, Upload problems and the ERPNext POS Invoice list; close shifts as normal and check each Closing Shift.
4. **Rollback:** set Upload = Off (supervisor). The tills keep selling and queue the bills; the queue uploads when it is switched back on.

## Order of work
- **Safe to build now (no writes):** Tasks 1, 2, 3 (tested with FakeErp), 4 (DryRun against live, read-only), 5 and 6.
- **Needs the ERPNext admin:** Tasks 7 and 8.
- **Needs the owner's go-ahead:** Task 9 (live writes) and Task 10 (cutover).

---

## Sandbox DryRun result (2026-10-08, controller)

Build: commit fd211a00 (Debug), `Upload = DryRun`, pointed at the dev sandbox (then `http://93.127.195.63:8090`, now `https://dev.quickgroc.com/`), using a copy of an earlier test database (one closed shift: 1 cash sale, 7 approvals).

- First sync cycle: **10 documents previewed, 0 problems, nothing sent** (`outbox-preview\_summary.txt`). Header showed the amber "DRY RUN" badge and "10 waiting · 0 failed".
- Previews: `TILL1-…-000001.json` (POS Invoice), `TILL1-SHIFT-…-opening.json`, `TILL1-SHIFT-…-closing.json` (with `pos_transactions`, `pos_payments`, `taxes`, `payment_reconciliation`), 7 `APPROVAL-*.json`. Field names match the live POS Awesome documents. Every item, UOM, warehouse, mode of payment, customer, tax template and profile reference was checked read-only on the sandbox.
- Sandbox prepared via `tillpos/tools/erpnext-setup.mjs`: custom fields, TillPOS Approval, POS Cashier (+ two sandbox-only cashiers), `account_for_change_amount` on the three profiles (owner approved).
- Next: Live upload on the **sandbox only** (Task 9), after the fix-wave review and the owner's go-ahead.

## Build status (2026-10-08)

Tasks 1–6 and the fix waves are implemented and reviewed on `feat/tillpos-ui-and-sync` (HEAD ee8bae0a): write guard (Off/DryRun/Live, Live refused in test builds and while a shift is open), payload builders, upload engine (lookup-first idempotency, in-flight marker, draft→check→submit, error classes, backoff, escalation), DryRun validation, cross-till returns with double-refund checks, Upload problems screen (Failed / Excluded / Handled, all supervisor-gated and logged), schema downgrade guard. 853 tests, 0 warnings. Test package 0.3.7 built (Upload = Off).

Sandbox prepared (metadata + 2 cashiers + change accounts). **Task 9 (Live on the sandbox) waits for the owner's go-ahead.** Task 7/8 production items remain for the admin (TRN, cashiers, roles, Allow Negative Stock).

## Sandbox Live test log (2026-10-08, https://dev.quickgroc.com, till 7, Test Counter)

| Step | Result |
|---|---|
| Go Live with no shift open | OK; "LIVE UPLOAD" badge |
| POS Opening Shift | **POSA-OS-26-0000045**, submitted, status Open, `custom_offline_id` set |
| Cash bill 3.333, paid 10 | **ACC-PSINV-2026-05626** Paid; change 6.667; outstanding 0; VAT 0.159; shift + cashier linked |
| Card bill 11.429 | **ACC-PSINV-2026-05627** Paid |
| Split bill 5.923 (card 2 + cash 5) incl. weighed 0.740 Kg | **ACC-PSINV-2026-05628** Paid; change 1.077 |
| Further sales #5, #6 | **ACC-PSINV-2026-05629 / 05630** Paid |
| Finding 1 | Till user needed Accounts/Sales/Stock User roles to create POS Invoices (added; checklist updated) |
| Finding 2 | Return refused: needs `pos_invoice_item` per line (fixed 62fb0509) **and** `paid_amount`/`base_paid_amount`/`change_amount 0` (ERPNext crashes with a 500 TypeError in `validate_change_amount` otherwise — verified by a hand probe, probe draft deleted) |
| Finding 3 | A deterministic 500 on one document paused the whole queue until escalation; escalation → Failed → supervisor Retry worked as designed |
| Finding 4 | Login screen's "Upload problems" count only refreshed on login |
| Return (after fixes 62fb0509 + ce3823cc2) | **ACC-PSINV-2026-05631**: is_return, against 05626, −3.333, paid −3.333, change 0, outstanding 0, line linked via `pos_invoice_item`; consolidated at closing |
| Shift 1 closing | **POSA-CS-26-0000044**: 6 invoices (incl. the return), grand 105.328, VAT 5.016; opening POSA-OS-26-0000045 now Closed. Counted amounts showed 0 (sent in the wrong field) → fixed 9121aa443 |
| Finding 5 | Second shift's opening refused while the first was still open in ERPNext ("already has an open POS shift") → openings now wait for the previous shift of that counter to close (627910ede) |
| Shift 2 (after fixes) | Opening **POSA-OS-26-0000046**, sale **ACC-PSINV-2026-05632** (51), closing **POSA-CS-26-0000045**: Cash Counter 2 expected 51 / counted 51 / diff 0 — counted amounts correct; opening now Closed |
| Approvals | 19 TillPOS Approval records uploaded (LineVoid, ShiftCount ×6, ShiftVariance, SettingsChange ×6, UploadRetry ×3, UploadModeChange, FailedSupervisorPin) |
| Final state | Till queue empty: 0 bills, 0 shifts, 0 approvals waiting |

**Result: Plan 2b Task 9 passed on the sandbox** after 6 fixes (roles, `pos_invoice_item`, `paid_amount` on returns, server errors don't pause the queue, login count refresh, shift ordering, closing currency fields). Production cutover (Task 10) still needs: the admin checklist on production, a per-till user/key, removal of test cashiers/sample QR, and the owner's go-ahead.
