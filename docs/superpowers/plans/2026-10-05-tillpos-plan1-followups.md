# TillPOS Plan 1 — follow-ups after implementation

Plan 1 code (Tasks 0, 2–11, Task 12 steps 1–3) is built and reviewed: 148 tests passing, 0 build warnings. This file records what still has to happen and the decisions taken during implementation.

## 1. Still to do by the shop / admin (needs the test ERPNext copy)

**Plan Task 1** (test copy of ERPNext, admin setup, API checks): do it exactly as written in the plan. When filling `docs/erp-api-notes.md`, also record these checks, which came out of the code reviews:

- [ ] Exact System Settings → Rounding Method text: "Banker's Rounding (legacy)" → set CLI `"Rounding": "BankersLegacy"`; "Banker's Rounding" → `"Bankers"`; "Commercial Rounding" → `"Commercial"`.
- [ ] Accounts Settings → "Round Tax Amount Row-wise" is OFF (the VAT port assumes off).
- [ ] The TillPOS Device role can read Deleted Document (otherwise deletions never reach the tills).
- [ ] No Item Tax rows use minimum/maximum net-rate bands.
- [ ] Whether the shop sells variant items (if yes, variant price fallback must be added — see §3).
- [ ] Whether any active Pricing Rule targets a specific UOM (those are skipped by the till, by design).

**Plan Task 12 steps 4–9** (pull, change detection, scan speed, tax parity on 5 baskets): run as written, with two adjustments:
- Step 5: a repeat pull is no longer exactly "0 rows" — each pull deliberately re-reads rows changed in the last 5 minutes.
- Step 8: add parity baskets that hit exact rounding midpoints (e.g. a % offer producing x.xx5, exclusive VAT on 0.50, a fractional-conversion-factor UOM line).

## 2. Decisions taken during implementation (rulings)

| # | Decision | Cost if wrong |
|---|---|---|
| 1 | Work on branch `feat/tillpos-plan1` (no worktree). | None material. |
| 2 | Task 1 and Task 12 steps 4–9 deferred to the shop (need server/Docker/admin access). | Parity and performance (S1, S5, S6) unverified until run. |
| 3 | Frappe's `remainder()` rounds the remainder; plan test corrected (10.375 → 10.26). | One test value; 2-decimal totals unaffected. |
| 4 | Rule priority order kept as spec §7 (priority → item code > brand > item group → biggest saving), not ERPNext's apply_on order. | With overlapping Brand and Item Group offers on one item, the till may pick a different offer than POS Awesome. |
| 5 | Item tax template choice mirrors ERPNext exactly (dated rows first, exact tax category). | Differs only on sites using tax categories. |
| 6 | A missing item tax template or POS Profile tax template stops the sale/billing instead of guessing VAT; the cashier sees "unsupported tax". | Such items can't be sold until the next sync fixes the data. |
| 7 | Sync re-reads a 5-minute overlap window every pull (late-committed ERPNext saves are never skipped). | Small re-processing each pull. |
| 8 | Deletions are applied only if the document no longer exists on the server (delete-then-recreate safe). | One small extra query per deletion page. |
| 9 | Price sync is tracked per price list and generation; daily reconcile re-downloads Retail prices both ways, with wipe guards. | One full price pull when the list changes; a daily ~12k-row fetch. |
| 10 | Offers that are coupon-based, margin-based, applied on other items, for another company, or UOM-specific are marked unsupported and never applied. | Those offers don't apply at the till. |
| 11 | Template items (with variants) are not sellable. | None. |
| 12 | Disabled and non-sales items are excluded from name search. | None. |
| 13 | Fallback prices (stock price × conversion factor) are rounded to 2 decimals, like ERPNext. | None. |
| 14 | Two commits carry a "Claude Sonnet 5.5" co-author trailer; left as is. | Cosmetic. |

## 3. Deferred to later plans

- **Variant items** (price fallback to the template item, offers set on templates) — only if the shop sells variants.
- **End-of-life items** — map to not sellable in Plan 2 (ERPNext would reject them on upload).
- **Net-rate tax bands** — flag as unsupported in Plan 2 if Task 1 finds any.
- **Daily reconcile of Pricing Rules and tax templates** — not needed while the Deleted Document feed works.
- **Plan 3 (WPF app):** make `SqliteCatalog.Reload()` atomic/thread-safe (background sync + UI); guard `AddBarcode(null)`; fractional-quantity decrement; round typed quantities like ERPNext (3 decimals); friendly errors instead of stack traces; HTTPS enforcement and base-URL trailing slash; distinguish HTTP timeouts from cancellation in retry logic.
- **Performance (measure in Task 12):** one SQLite connection per lookup (~5–6 per scan), sequential document fetches for pricing rules on first sync.
- **Test gaps worth adding:** mixed inclusive/exclusive tax rows, inclusive rounding difference > 0.05, `KvSyncStateStore` round-trip, ERPNext client insert/delete.
