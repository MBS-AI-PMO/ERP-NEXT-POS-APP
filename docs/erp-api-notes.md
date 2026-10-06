# ERPNext API notes — live site (read-only checks)

Site: erp.quickgroc.com · ERPNext 15.114.0 / Frappe 15.113.0 / POS Awesome 15.35.2 · checked 2026-10-06 with read-only user `till-test@quickgroc.com` (role TillPOS Device, read only). No writes were made to live.

## API checks (plan Task 1, step 6)

| # | Check | Result | OK? |
|---|---|---|---|
| A | `get_list` with child fields (`` `tabItem Barcode`.barcode ``) | Works; one row per barcode. **Order-by must be table-qualified** (`` `tabItem`.modified ``) when child fields are joined, otherwise MariaDB reports an ambiguous column. The till's feeds only order joined queries by `name`, which works. | ✅ |
| B | Item Price list accepts `customer`, `batch_no` | Yes | ✅ |
| C | Till user can read Deleted Document | Yes (3,963 rows) | ✅ |
| D | Pricing Rule shape | **No Pricing Rules exist (0).** POS Awesome POS Offer: 0, POS Coupon: 0. Offers today = admin editing Item Prices. | ✅ (n/a) |
| E | Till user can read POS Profile, Company, Currency, Address | Yes | ✅ |
| F | Rounding method | Not readable by the till user; replay of 40 real invoices matched with `Bankers`. Still to confirm in System Settings (midpoint cases may not have occurred). | ⚠️ confirm |
| G | AED smallest currency fraction | Synced from Currency AED (live counters round 9.994 → 10.00) | ✅ |
| H | `get_logged_user` returns HTTP `Date` header | Yes | ✅ |

## Live configuration facts that matter

- **Currency precision is 3** (e.g. net 9.518, VAT 0.476, grand 9.994). The till must run with `Precision: 3`.
- **VAT:** template `UAE VAT 5% - AAML`, On Net Total, **included in price**. Matches spec §7.1.
- **Price list:** `Standard Selling`; item prices are stored with up to 4 decimals (e.g. 11.4286).
- **POS Profile "Test Counter"** has `disable_rounded_total = 1` and write-off limit 1, while real counters (e.g. "Al Ain Counter 1") round the total (`disable_rounded_total = 0`). Tills should copy a real counter's profile.
- Payment modes on Test Counter: `Cash Counter 2` (default), `Credit Card`.
- **Weighed items:** 571 items have stock UOM `Kg`. Invoice lines show the item's short barcode (e.g. `2000088`) with precise weights (0.305, 1.555 kg) — suggests scale labels with embedded weight parsed by POS Awesome, or typed weights. **Label format still needed** (plan 3).
- **Barcode data issue:** 118 barcodes point to a UOM that is not set up on the item (e.g. barcode `003497` uses `Kg`, item only has `PCS`). The till refuses these (no guessing). List: `publish/reports/barcode-uom-issues.csv` (local only, not committed).
- POS Awesome stores extra fields on POS Invoice (`posa_pos_opening_shift`, `posa_cashier`, `posa_client_request_id`, …); relevant for Plan 2's upload design.

## Plan 1 verification results (plan Task 12 adapted: read-only against live)

| Check | Result | Target |
|---|---|---|
| First full pull | 58.4 s — 12,753 items, 14,442 prices, 640 item groups, 5+5 tax templates, 3,963 deleted docs; no errors | < 300 s ✅ |
| Incremental pull (no changes) | 4.9 s | ✅ |
| Scan time | first 25.9 ms (warm-up), then 0.1–0.7 ms | < 100 ms ✅ |
| Replay — 40 newest real POS invoices | **40 match, 0 mismatch, 0 skipped** (net, VAT, grand, rounded totals; incl. weighed items and 3-decimal precision) | all match ✅ |
| Change detection (edit price / add barcode / disable) | not run — would require edits on live; to do on a test site in Plan 2 | — |
| Write parity (`parity`) | not run on live by design (`AllowWrites: false`) | — |
