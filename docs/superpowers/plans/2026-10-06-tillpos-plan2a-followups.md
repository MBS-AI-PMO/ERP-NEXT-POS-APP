# TillPOS Plan 2a — follow-ups after implementation

Plan 2a (offline sales engine) is built and reviewed: 9 tasks + a final fix wave, **255 tests passing, 0 warnings**, branch `feat/tillpos-plan2a` (cut from `feat/tillpos-plan1`). Nothing in it writes to ERPNext.

## Decisions taken during implementation

| # | Decision | Cost if wrong |
|---|---|---|
| 1 | Branch cut from `feat/tillpos-plan1` (Plan 1 not merged yet). | The 2a PR targets `feat/tillpos-plan1`, or is merged after it. |
| 2 | Stale-payment check compares totals only (a plan for a different bill with the same total charges the same money). | None financially. |
| 3 | Outbox: a late "failed" report never overwrites an uploaded bill; unknown bill IDs throw; supervisor retry only moves failed bills. | A genuinely failed retry of an already-uploaded bill is not shown (it is already in ERPNext). |
| 4 | Local POS prototype HTML files in `docs/` are git-ignored (one contains the test API key). | Un-ignore to commit the prototype. |
| 5 | Refund limit (AED 50) applies to the **running total refunded on one receipt**; whole-unit lines return only in whole units; a blank supervisor ID is not an approval. | A supervisor is asked more often on multi-step returns. |
| 6 | Approval records store who asked (cashier), who approved (supervisor), and the shift. | None. |
| 7 | A corrupt stored PIN hash counts as "no match" instead of crashing logins. | None. |
| 8 | **Scale labels on Kg barcodes read grams even when the item's stock unit is PCS** (banana `000016`, cheese `003497` — confirmed intentional by the shop). | None — data unchanged. |
| 9 | Scale-label quantities can't be retyped (+, −, or Set quantity). | Cashier must remove and rescan a mislabelled item (removal needs a supervisor). |
| 10 | A split payment whose cash part rounds to 0 is refused ("take it all by card"). | None. |
| 11 | The cashier download keeps the till's list if ERPNext returns no usable cashiers. **To revoke a cashier, disable them in ERPNext — don't delete them.** | A deleted (not disabled) cashier keeps till access until disabled. |
| 12 | Shift close only counts that shift's bills; counted cash is rounded to precision. | None. |
| 13 | Each bill stores the cashier's ERPNext user and (for returns) a reason, ready for upload. | None. |

## Requirements carried into Plan 2b (upload)

- The uploader must skip a corrupt bill and keep uploading the others ("other bills keep uploading").
- Cross-till returns: re-check the returnable quantity and the running refund total against ERPNext before uploading a return.
- Cashier → ERPNext user mapping must survive the cashier being removed from the list later (bills already carry `CashierUser`).
- Check on the test site: how a split bill's small rounding difference posts (write-off vs change); whether a zero-total bill needs a zero payment row.
- **Check on live/test:** does the `PCS` unit have "Must be Whole Number" ticked? If yes, weighed sales of PCS items (banana, cheese) will be refused at upload and need a ruling.
- Upload supervisor approvals (with cashier, supervisor, shift).

## Requirements carried into Plan 3 (screens)

- Verify every supervisor approval through the PIN check and write it to the approval log (including returns).
- Limit repeated wrong PIN attempts (login and supervisor prompt) and log failed supervisor attempts.
- Take the shift ID from the open shift; refuse sales when no shift is open.
- Decide whether lowering a line's quantity needs a supervisor (it is a partial line void).
- On recall of a held bill, show any lines that could no longer be sold.
- Decide whether a supervisor may approve their own action.

## Requirements carried into Plan 4 (installer / hardening)

- File permissions on the till database (PIN hashes can be brute-forced offline from a copied file; write access could self-promote a cashier).
- Protect the device API credential (Windows DPAPI).

## Smaller deferred items (none affect money or data)

Case-insensitive unit comparison (`KG` vs `Kg` is flagged as a fallback although the quantity is right); the unit-fallback flag is lost when a scan merges into an existing line or on recall; returning an item sold at price 0; test coverage gaps listed in the reviews.
