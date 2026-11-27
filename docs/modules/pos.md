# POS module

Schema `pos`, migration order 120, module code `pos`. The module is optional: every endpoint uses `.RequireModule("pos")`, `.RequireCompany()` and a permission. Its manifest depends on `sales`, `inventory`, `accounting` and `finance`. The `retail` preset enables it.

The module owns POS terminals, cashier shifts and POS receipts (sales and refunds). It never writes to another module's tables. It uses:
- `IInventoryPostingService` for stock;
- `IJournalPostingService` for journals;
- `IPriceCalculator` for prices, and `IPriceListSnapshotQueries` (new in Catalog) for the catalog snapshot;
- `ITaxCalculator` for taxes;
- `ICashBankAccountQueries` (new in Finance) for drawer accounts.

POS creates **no receivables**: a receipt is paid in full when it is taken.

## Contracts (`Erp.Modules.Pos.Contracts`)

`PosPermissions`:
- `pos.read`: view terminals, shifts, receipts and Z reports.
- `pos.terminal.manage` (sensitive): set up terminals and register devices.
- `pos.shift.manage`: open, pay in/out, count and close shifts; X reports.
- `pos.sell`: submit sales; read the catalog snapshot.
- `pos.refund` (sensitive): refund receipts.
- `pos.discount` (sensitive): any line discount above 0 %, never above the terminal's `MaxDiscountPercent`.

`PosDocumentTypes` (source document types for stock and journals): `pos.receipt`, `pos.refund`, `pos.shift`.

### Contracts added to other modules (additive)

- **Finance:** `ICashBankAccountQueries.GetAsync(id)` returns `CashBankAccountInfo(Id, LegalEntityId, Kind, Code, Name, Currency, GlAccountId, IsActive)`. POS validates a terminal's drawer account with it and decides where payments post.
- **Catalog:** `IPriceListSnapshotQueries`:
  - `GetSnapshotAsync(priceListId, date)` returns `PriceListSnapshot(PriceListId, Code, Currency, TaxInclusive, Version, Items, Barcodes)`.
  - `GetVersionAsync(priceListId)` returns the version alone.
  - The version is the price list's rowversion in Base64. Any change to the list or its items bumps it, because the aggregate is touched when its children change.

## Domain

### PosTerminal (`[Audited]`)

| Field | Meaning |
|---|---|
| `Code` | Up to 12 letters, digits or dashes; unique per company. |
| `WarehouseId` | Sells from here. The legal entity and branch come from this warehouse, which must be active, physical and in this company. |
| `PriceListId` | Its currency must equal the legal entity's functional currency (`pos.currency-unsupported`). |
| `Currency` | The functional currency. POS sells in one currency, at rate 1. |
| `CashAccountId` | Finance cash account (the drawer). Must be active, of kind Cash, in the same legal entity and currency. |
| `CardAccountId`, `WalletAccountId` | Optional Finance cash/bank accounts used as card or wallet clearing. |
| `MaxDiscountPercent` | 0–100. |
| `StalePricePolicy` | `Reject` (default) or `AcceptServerPrices`. |
| `DefaultCustomerId` | Recorded on receipts that name no customer. Defaults to the company's walk-in customer, which is resolved when the terminal is saved. |
| `DeviceKeyHash`, `DeviceRegisteredAtUtc` | Device binding: SHA-256 of the device key (see Security). |
| `ReceiptPrefix` | Always `POS`. |
| `IsActive` | |

Terminals can be deactivated and reactivated. A terminal with a shift that is not closed cannot be deactivated, and cannot have its warehouse, legal entity or price list changed.

### CashierShift (`[Audited]`)

States: `Open → Closing → Closed`. Closing directly from `Open` is allowed. A filtered unique index on (TenantId, TerminalId) where `Status <> 'Closed'` enforces **one shift that is not closed per terminal**.

- **BusinessDate:** set at open (default: today). Every receipt in the shift is dated with it, for stock, journals and the fiscal period.
- **Number:** gapless per terminal, e.g. `SH-T01-2026-000001`.
- **Opening float.**
- **Pay-in / pay-out cash movements:** JSON, with a reason, user and time. They change only the expected drawer cash. There is **no journal**: the safe and the drawer share the GL cash account. Cash expenses go through Finance payments.
- **Counts:** JSON, per payment method, with `Expected`, `Counted` and `Variance = Counted − Expected`. Counts are blind: the client sends only the counted amounts.

The shift holds **no running sales totals**. They are summed from its receipts, so selling never updates the shift row and never conflicts on its rowversion.

Expected amounts:
- **Cash** = opening float + cash sales net of change − cash refunds + pay-ins − pay-outs.
- **Card, Wallet, Other** = sales − refunds.

### PosReceipt (`[Audited]`)

`Kind` is `Sale` or `Refund`. `Status` is `Completed`, `PartiallyRefunded` or `Refunded`; only sales change status.

- **ClientReceiptId:** unique per terminal through a unique index on (TenantId, TerminalId, ClientReceiptId). This is the idempotency key.
- **Number:** unique per company and gapless per terminal: `POS-{terminal code}-{year}-000001` for sales, `PRF-{code}-{year}-000001` for refunds.
- **PriceVersion:** the version the receipt was priced with.
- **Lines** (JSON):
  - product, unit, quantity and base quantity;
  - server unit price, discount %, line, discount, net, tax and gross amounts;
  - taxes per code;
  - cost amount and cost movement;
  - for sales: `RefundedQuantity`, `RefundedNetAmount`, and `RefundedAmount` per tax;
  - for refunds: `OriginalLineUid`.
- **Payments** (JSON): `Cash | Card | Wallet | Other`, the tendered amount, and an optional reference.
- **Totals:** `NetTotal`, `TaxTotal`, `GrossTotal`, `DiscountTotal`, `RoundingAdjustment` (= `AmountDue − GrossTotal`), `AmountDue`, `Tendered`, `Change`, `CashNet` (cash kept or paid out), `JournalId`.

### Payment and rounding rules (`PosMath`)

- At least one payment; each amount must be > 0; the method must be known.
- **Cash rounding** uses the currency's `CashRoundingIncrement`, rounding half away from zero. It applies only when every payment is cash; a card pays the exact amount.
- **Foreign cash:** a payment may carry `currency` (cash only, `pos.foreign-currency-cash-only`). It is converted at the spot rate of the business date (`pos.rate-missing` when none) and rounded to the terminal currency's minor units; the payment keeps `foreignCurrency`, `foreignAmount` and `exchangeRate`. Change is always given in the terminal currency.
- **Sale:**
  - Non-cash total must be ≤ amount due (`pos.overpaid-non-cash`).
  - Total tendered must be ≥ amount due (`pos.underpaid`).
  - Change = tendered − due, and comes only from cash. Cash net = cash tendered − change.
- **Refund:**
  - Payments must equal the amount due exactly (`pos.refund-payment-mismatch`).
  - No payments given means all cash (cash-rounded).
- **Refund amounts** are prorated from the sale line. The refund that completes a line takes the remainder of net and of each tax, so a fully refunded line nets to exactly zero.
- **Refund quantity** must be ≤ quantity − refunded quantity (`pos.refund-exceeds-sold`).

## Posting

Every receipt posts **atomically**, in the request's single transaction (ADR-006):

1. **Lock** the terminal's open shift row with `UPDLOCK, ROWLOCK` on the existing row. There are no range locks: the row must exist, which follows the locking rule. Receipts and the shift close take the same lock, so a shift cannot close while a receipt is posting into it. Receipts on different terminals lock different rows and never wait on each other.
2. Re-check the idempotency key (a duplicate of the same terminal waited on the lock and now sees the original).
3. **Price** on the server from the terminal's price list (`IPriceCalculator`, with the shift's business date). Check the discount permission and the terminal maximum, then compute taxes with `ITaxCalculator` by the product's tax category. Withholding tax is ignored.
4. **Check the price version** (see below).
5. **Settle** the payments (rules above).
6. **Insert** the receipt row. This claims the (terminal, ClientReceiptId) unique key before any number, stock or journal.
7. **Number** the receipt: gapless, per terminal (scope `pos.receipt:{terminalId}`), by fiscal year.
8. **Stock:** for stockable lines, `IInventoryPostingService.PostAsync` with `PostJournal: true` and counter role `CostOfGoodsSold`.
   - A sale issues stock (`SalesIssue`). The inventory engine writes the COGS journal (Dr COGS / Cr Inventory, purpose `inventory`). The line keeps its cost and first movement.
   - A refund returns stock (`SalesReturn`) with `CostSourceMovementId` = the sale line's cost movement, so stock comes back **at the original cost**; Inventory splits it across the sale line's batches/serials in proportion (see the inventory module).
9. **Journal** (`IJournalPostingService`, source `pos`, purpose `receipt`, rate 1):

   | Sale | Debit | Credit |
   |---|---|---|
   | Cash net of change | Drawer: the Finance account's GL account, else role `CashAccount` | |
   | Card / Wallet | Configured bank account's GL account, else role `CardClearing` (1140) | |
   | Other | `BankAccount` | |
   | Goods / services net, per product posting group | | `SalesRevenue` / `ServiceRevenue` |
   | Tax, per tax code | | `OutputTax` |
   | Cash rounding up / down | `CashRounding` (5430, rounded down) | `CashRounding` (rounded up) |

   A refund is the mirror image: Dr `SalesReturns` per posting group, Dr `OutputTax`; Cr the payment accounts; rounding mirrored. There is no AR line.
10. Set the number and journal on the receipt and save. When refunding, the sale's refunded quantities and status are saved in the same transaction; the sale's rowversion stops two concurrent refunds over-refunding.

Any failure (insufficient stock, closed period, missing posting rule) rolls back everything, the number included.

### Shift close (J-11)

`POST /shifts/{id}/close {counts}`:
1. Lock the shift.
2. Compute the expected amount per method from the receipts.
3. Record the counts and close.
4. Post one journal for the variances (source `pos`, doc type `pos.shift`, purpose `over-short`):
   - shortage: Dr `CashOverShort` / Cr the method's account; overage: the reverse;
   - **cash** always (an uncounted drawer counts as 0);
   - **card, wallet and other** only when that method was counted (e.g. from the terminal's settlement slip) — against `CardClearing` or the configured bank account. A method that is not counted is reported, not posted.

### X and Z reports

- **X report** (`pos.shift.manage`): running totals at any time.
- **Z report** (`pos.read`): final, only after close (`pos.shift-not-closed` otherwise).

Both show:
- sales and refund counts;
- gross sales and gross refunds;
- net sales, tax, discounts and rounding, all net of refunds;
- float, pay-ins and pay-outs;
- per method: sales, refunds, expected, counted and variance;
- the cash variance and its journal;
- foreign cash per currency (`foreignCash`: amount in that currency and its terminal-currency value).

## Idempotency and price versions

- **Resubmission:**
  - The same (TerminalId, ClientReceiptId) returns the original receipt with `replayed: true` (HTTP 200 instead of 201). Nothing is posted again, and no number, stock movement or journal is consumed.
  - This holds whatever `Idempotency-Key` the resend carries. The endpoint additionally has `.RequireIdempotency()`, which replays the exact response for the same key.
  - Refunds behave the same, keyed on the refund's ClientReceiptId at the refunding terminal.
- **Concurrent duplicates:** a duplicate on the same terminal waits on the shift lock and then finds the original. As a last resort, a duplicate-key error on insert is detected (SQL 2601/2627, read by reflection: Application may not reference SqlClient) and the winner is returned.
- **Stale prices:**
  - The client sends the `priceVersion` of its catalog snapshot, and optionally `expectedGross`.
  - If the version differs from the price list's current version, the terminal policy is `Reject`, and the server gross differs from `expectedGross`, then nothing is posted. The response is **409 `pos.price-version-stale`**, with the repriced cart (server lines, totals and the current version) in the `repriced` extension.
  - With `AcceptServerPrices`, or when the totals still match, the sale goes through at server prices.
  - A request without `priceVersion` is an online submission and takes server prices.
  - Client prices are never used.

### Offline mode (server side)

1. **Snapshot:** the device caches `GET /terminals/{id}/catalog-snapshot` (products, units, tax categories, prices, barcodes, price version, cash rounding, minor units, maximum discount) and refreshes it when online.
2. **Number range:** while online it reserves numbers with `POST /terminals/{id}/offline-range {size}` (1–10,000; device key required when bound). Ranges are consecutive per terminal; offline receipts are numbered `OFF-{terminal}-{n}` from them. The number becomes the receipt's legal number (unique per company).
3. **Sync:** `POST /terminals/{id}/offline-sync {receipts:[{clientReceiptId, offlineNumber, createdAtUtc, expectedGross, customerId, lines, payments}]}` processes the receipts in creation order, **each in its own transaction**, and answers per receipt `Accepted` / `Replayed` / `Rejected` with conflicts:
   - idempotent on (terminal, ClientReceiptId) — resending a batch is always safe;
   - the receipt is dated on its creation date (`pos.offline-date-invalid` for a future date) and posted into the terminal's open shift (`pos.no-open-shift` otherwise — sync before closing);
   - prices are recomputed by the server for that date; a different total is reported as `pos.price-changed` but the sale stands when the payments cover the server total (otherwise it is rejected with `pos.underpaid`);
   - stock follows the inventory policy: without `AllowNegativeStock` a shortage rejects the receipt with `inventory.insufficient-stock` and nothing is posted;
   - an unallocated number is rejected (`pos.offline-number-invalid`).
4. Rejected receipts are left to a supervisor (fix stock or prices, then resend the same receipt).

Not built: the device application itself (local queue, provisional printing), signing the queue with the device key, accepting receipts into a shift already closed.

## Security

- **Device binding:** `POST /terminals/{id}/register-device` (`pos.terminal.manage`) generates a 256-bit random key. The key is returned **once**; only its SHA-256 hex is stored. Registering again replaces it.
  - From then on, receipts and refunds at that terminal require the header `X-Pos-Device-Key`. A missing or wrong key gives 403 `pos.device-key-invalid`; the comparison is constant-time.
  - Device binding is in addition to user authentication, not instead of it.
- **Server-side amounts:** prices, discounts, taxes, rounding and totals are always computed on the server.
- **Tenant isolation:** all tables carry the shadow TenantId and the tenant/company query filters. Reading another tenant's terminal, shift or receipt returns 404.

## Salesperson (wave 6)

Receipts carry an optional `SalespersonId`: `salespersonId` in the submit body (must be an active salesperson, `pos.salesperson-invalid`), otherwise
the customer's default salesperson, otherwise the cashier's linked employee when it is a salesperson. Refunds inherit the sale's. `IPosSalesQueries`
(Pos.Contracts) exposes completed receipts and refunds as sales facts for the by-salesperson report and commissions. Receipt and over/short journals
pass the terminal's branch, so they default to the branch's profit center.

## Endpoints (`/api/v1/pos`)

All require authorization, an active company and the module.

| Method | Path | Permission | Notes |
|---|---|---|---|
| GET | `/terminals?includeInactive=` | read | |
| GET | `/terminals/{id}` | read | ETag |
| POST | `/terminals` | terminal.manage | `{code, name, warehouseId, priceListId, cashAccountId, cardAccountId?, walletAccountId?, maxDiscountPercent, stalePricePolicy?, defaultCustomerId?}` |
| PUT | `/terminals/{id}` | terminal.manage | If-Match; same body without `code` |
| POST | `/terminals/{id}/deactivate`, `/activate` | terminal.manage | |
| POST | `/terminals/{id}/register-device` | terminal.manage | Idempotency-Key; returns `{terminalId, deviceKey, headerName}` |
| GET | `/terminals/{id}/catalog-snapshot?date=` | sell | |
| GET | `/shifts?terminalId=&status=&from=&to=` | read | |
| GET | `/shifts/{id}` | read | |
| POST | `/shifts` | shift.manage | Idempotency-Key; `{terminalId, openingFloat, businessDate?}` |
| POST | `/shifts/{id}/cash-movements` | shift.manage | Idempotency-Key; `{kind: PayIn\|PayOut, amount, reason}` |
| POST | `/shifts/{id}/start-close` | shift.manage | Idempotency-Key |
| POST | `/shifts/{id}/close` | shift.manage | Idempotency-Key; `{counts: [{method, amount}]}` |
| GET | `/shifts/{id}/x-report` | shift.manage | |
| GET | `/shifts/{id}/z-report` | read | closed shifts only |
| GET | `/shifts/{id}/receipts` | read | |
| POST | `/receipts` | sell | Idempotency-Key, `X-Pos-Device-Key` when registered; `{terminalId, clientReceiptId, priceVersion?, expectedGross?, customerId?, lines: [{productId, variantId?, uomId?, quantity, discountPercent?, batchNumber?, serials?}], payments: [{method, amount, reference?}]}`. Returns 201 (new), 200 (replayed) or 409 (stale price with `repriced`). |
| GET | `/receipts/{id}` | read | |
| POST | `/receipts/{id}/refund` | refund | Idempotency-Key, device key; `{clientReceiptId, lines: [{lineId, quantity}], payments?, terminalId?}`. Uses the open shift of `terminalId` (default: the sale's terminal), which must be in the same legal entity and currency. |
| POST | `/terminals/{id}/offline-range` | sell | Idempotency-Key, device key; `{size}` → `{terminalId, prefix, from, to}` |
| POST | `/terminals/{id}/offline-sync` | sell | device key; `{receipts: [...]}` → `{accepted, replayed, rejected, results: [{clientReceiptId, offlineNumber, status, receipt?, conflicts}]}` |

Error codes:
- `pos.terminal-not-found`, `pos.shift-not-found`, `pos.receipt-not-found`
- `pos.terminal-inactive`, `pos.terminal-code-taken`, `pos.terminal-has-open-shift`
- `pos.shift-already-open`, `pos.no-open-shift`, `pos.shift-not-closed`
- `pos.warehouse-invalid`, `pos.price-list-invalid`, `pos.currency-unsupported`, `pos.cash-account-invalid`, `pos.payment-account-invalid`
- `pos.customer-not-found`, `pos.product-invalid`, `pos.uom-invalid`, `pos.price-not-found`
- `pos.discount-forbidden`, `pos.discount-exceeds-limit`
- `pos.device-key-invalid`
- `pos.not-a-sale`, `pos.refund-terminal-mismatch`, `pos.refund-exceeds-sold`, `pos.refund-payment-mismatch`
- `pos.invalid-payment-method`, `pos.overpaid-non-cash`, `pos.underpaid`
- `pos.price-version-stale`
- `pos.invalid-state-transition`
- `pos.invalid-quantity`, `pos.invalid-discount`, `pos.invalid-amount`, `pos.invalid-float`, `pos.invalid-count`, `pos.invalid-movement-kind`, `pos.invalid-code`, `pos.invalid-max-discount`, `pos.invalid-stale-price-policy`
- `pos.lines-required`, `pos.payments-required`
- `pos.foreign-currency-cash-only`, `pos.rate-missing`, `pos.invalid-offline-range`, `pos.offline-number-invalid`, `pos.offline-date-invalid`, `pos.price-changed` (offline conflict, informational)

## Decisions and limitations

- **Cash rounding posts to `CashRounding` (5430)**; card and wallet takings without a configured bank account go to `CardClearing` (1140) until settled.
- **Selling currency is the functional currency.** Prices, the journal and change are in the terminal currency; foreign *cash* is accepted at the day rate and converted on receipt (no FX gain/loss is booked on the drawer — the foreign notes are valued at the rate of the day they were taken; their later deposit is a Finance matter). The shift count is in terminal-currency value; the X/Z report lists foreign cash per currency to help counting.
- **Pay-in and pay-out do not post a journal** (same GL cash account as the safe). The Finance cash-account balance does not include POS takings until they are deposited through Finance.
- **Refunds restock what the sale line issued**, split across its batches/serials by Inventory. A FIFO line without batches is one movement and returns at its blended cost.
- **The receipt journal is per receipt,** not summarised per shift. This keeps postings atomic and idempotent per receipt.
- **Cashier assignment is not enforced.** The cashier is the user who opened the shift, and receipts record the submitting user, but any user with `pos.sell` may sell on an open shift.
- **Offline mode:** number ranges and batch sync are implemented server-side; the device application is not part of this repository.
