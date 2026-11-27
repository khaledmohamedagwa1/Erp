# Sales module

Schema `sales`, migration order 110, module code `sales`. The module is optional: every endpoint uses `.RequireModule("sales")`, `.RequireCompany()` and a permission.

The module owns quotations, sales orders, delivery notes, sales invoices and sales returns (credit notes). It never writes to another module's tables. Stock moves through `IInventoryPostingService` and `IReservationService`. Journals go through `IJournalPostingService`, receivables through Finance `ISubledgerService`, and approvals through `IApprovalService`. Prices come from `IPriceCalculator` and taxes from `ITaxCalculator`.

## Contracts (`Erp.Modules.Sales.Contracts`)

```csharp
public interface ISalesQueries
{
    // Confirmed, undelivered order quantities in base units (MRP / purchasing demand), active company.
    Task<IReadOnlyList<OpenSalesDemand>> GetOpenDemandAsync(IReadOnlyCollection<Guid> productIds, Guid? warehouseId = null, CancellationToken ct = default);

    // Gross functional-currency value of confirmed order quantities not yet invoiced (credit exposure).
    Task<decimal> GetUninvoicedOrderExposureAsync(Guid customerId, CancellationToken ct = default);
}

public sealed record OpenSalesDemand(Guid OrderId, string OrderNumber, Guid OrderLineId, Guid ProductId, Guid? VariantId,
    Guid WarehouseId, DateOnly RequiredDate, decimal OpenBaseQuantity);
```

`SalesPermissions`:
- `sales.read`
- `sales.quotation.manage`
- `sales.order.manage`
- `sales.delivery.manage`, `sales.delivery.post`
- `sales.invoice.manage`, `sales.invoice.post`, `sales.invoice.cancel`
- `sales.return.manage`, `sales.return.post`
- `sales.invoice.discount`: required for any line discount above 0 %.
- `sales.price.override`: required to type a unit price instead of taking it from the price list.

`SalesDocumentTypes` gives the source document types used in inventory, journal and open-item references: `SalesQuotation`, `SalesOrder`, `DeliveryNote`, `SalesInvoice`, `SalesReturn`.

**Events consumed:**
- `ApprovalCompleted` (Workflow), handled exactly once through the inbox. It confirms or rejects pending orders, and approves or rejects pending invoices.
- `InvoiceSettled` (Finance). It mirrors the receivable status (`Open` / `PartiallyPaid` / `Paid`) and the open amount on the invoice. The handler sets values rather than adding them, so it is idempotent.

## Documents and states

Every document carries:
- tenant, company, legal entity (taken from the warehouse), branch and warehouse
- customer and the customer's posting group
- currency and exchange rate (a spot rate, or 1 for the functional currency)
- price list, the tax-inclusive flag and the date
- `[Audited]`, a rowversion, and lines stored as JSON (`Lines`, each with a stable `Uid` and nested per-code `Taxes`)

Drafts carry a draft number (`D-<id>`). The legal number comes from `IDocumentNumberGenerator` at the step that makes the document legal:

| Document | Number given at | Prefix |
|---|---|---|
| Quotation | send | `QUO` |
| Sales order | confirm | `SO` |
| Delivery note | post | `DN` |
| Sales invoice | post | `INV` |
| Sales return | post | `CN` |

**State machines:**
- **Quotation:** Draft → Sent → Accepted / Rejected / Expired → Converted.
  - Revise: Sent → Draft, and the revision number increases.
  - Accepting after `ValidUntil` expires the quotation instead, and the expiry is saved.
  - Convert creates a draft order at the quoted prices and discounts; taxes are recomputed.
- **Sales order:** Draft → PendingApproval → Confirmed → PartiallyDelivered → FullyDelivered → Closed, plus Cancelled.
  - Status follows the line progress (delivered and invoiced quantities).
  - Reversing a delivery or cancelling an invoice reopens a closed order.
  - Cancel is allowed only when nothing has been delivered or invoiced. Short-close closes an order that still has open quantities.
  - Confirmation reserves stock when the inventory policy enables reservations and the inventory module is on. It reserves what is available (a partial reservation is allowed). Cancel and short-close release whatever is still reserved.
- **Delivery note:** Draft → Picked → Posted → Reversed, plus Cancelled.
  - Created from an order; quantities ≤ open on the order minus quantities on other draft or picked notes.
  - Pick records the batch and serials per line. A line without a batch is allocated FEFO by inventory at posting (the suggestion is the inventory engine's FEFO allocation).
  - Reverse is allowed only while nothing on the note is invoiced.
- **Sales invoice:** Draft → PendingApproval → Approved → Posted → PartiallyPaid / Paid, plus Cancelled.
  - Created from free lines, from an order (lines or all open quantities), or from a posted delivery note.
  - A draft, pending or approved invoice can be discarded. A posted invoice is cancelled only by full reversal, and only in status Posted: no payments and no returns.
  - `Closed` exists in the enum for a later period-close step. Nothing sets it yet.
- **Sales return (credit note):** Draft → Posted, plus Cancelled (drafts only).
  - The returned quantity must be ≤ invoiced − already returned − quantities on other draft returns. The error is `sales.return-exceeds-invoiced`.

## Pricing (always on the server)

`SalesPricing` handles every create, edit and posting step:
1. **Unit price:** from the document's price list, else the customer's. The price list currency must equal the customer currency.
   - A typed price needs `sales.price.override`.
   - Lines copied from a source document (quotation → order, order → delivery or invoice, delivery → invoice) keep their agreed price and discount, and are not re-checked.
   - With no price, the error is `sales.price-not-found`.
2. **Discount:** a line discount above 0 % needs `sales.invoice.discount`, else `sales.discount-forbidden`.
3. **Amounts:** line amount = round(qty × price) and discount = round(line × %), both half away from zero to the currency's minor units.
4. **Tax:** `ITaxCalculator.CalculateForCategoryAsync(product.TaxCategory, Sales, afterDiscount, inclusive, …)`.
   - An inclusive price list extracts the tax from the price.
   - Withholding lines are ignored on sales documents.
5. **Re-pricing:** posting re-prices the approved lines with the same prices and discounts and the current tax rules. Line identities are kept, so returns and links stay valid.

## Credit check (at order confirmation, invoice submission and invoice posting)

The exposure is computed as follows:
- exposure = Finance `ICreditExposureQueries` (open receivables)
  + uninvoiced confirmed orders (excluding the order being invoiced)
  + this document's gross amount in the functional currency.
- The customer's credit action is used, falling back to the sales policy `CreditLimitAction`.
- A limit of 0 means no limit, and limits only apply when `EnableCreditLimits` is on.
- A customer on hold is always refused with `sales.customer-on-hold`.

| Action | Result |
|---|---|
| Warn | The document goes through. `warnings` in the response carries the message. |
| Block | `sales.credit-limit-exceeded` (409). |
| RequireApproval | `IApprovalService.SubmitAsync` with the fact `creditExceeded = 1`. The document goes to PendingApproval and `CreditApproved` is remembered, so posting does not ask again. |

**Decision (documented limitation):** while the workflow engine is not installed, `NoApprovalRequired` is registered with TryAdd and answers "no approval needed". A RequireApproval breach is then refused with `sales.credit-approval-unavailable` (409), meaning the document behaves as a block with a message saying why. At posting, RequireApproval passes only when the invoice was credit-approved.

Approval facts sent with every submission are `maxDiscountPercent`, `creditExceeded` and `documentAmount`.

## Posting

**Stock issue timing** is set by the sales policy `StockIssueTiming`:
- **AtInvoice** (default): delivery notes are refused with `sales.deliveries-disabled`. Invoice posting issues the stock inside the same transaction through `IInventoryPostingService`:
  - `SalesIssue`, with `PostJournal: true` and `CounterRole: CostOfGoodsSold`, so the inventory engine books Dr COGS / Cr Inventory.
  - Order lines consume their reservation.
- **AtDelivery:** delivery posting issues the stock the same way, consuming the reservation. Invoices for stockable lines must come from posted delivery lines (`sales.invoice-requires-delivery`), and post revenue only; the delivery's cost is copied to the invoice line for margins and returns. Service lines can be invoiced straight from the order.

Service products never touch stock. Stockable products with the inventory module off are refused with `sales.inventory-disabled`.

**Journals.** Each posting writes one sales journal through `IJournalPostingService`, with source module `sales`. Revenue is booked net of discount; there is no separate discount account.

| Document | Purpose | Lines |
|---|---|---|
| Invoice | `invoice` | Dr CustomerReceivable (gross, party = customer, party posting group). Cr SalesRevenue for goods or ServiceRevenue for services, per product posting group. Cr OutputTax per tax code. |
| Return | `return` | Dr SalesReturns (net). Dr OutputTax per code. Cr CustomerReceivable (gross). |

COGS is booked by the inventory engine as a separate journal: purpose `inventory`, with the same source document.

**Receivables:**
- The invoice creates open items through `ISubledgerService.CreateOpenItemsAsync`, using the payment terms installments.
- A return creates a negative open item and applies it to the invoice with `ApplyCreditNoteAsync`.

**Returns:**
- Restock lines go back as `SalesReturn` movements at the original issue cost (`CostSourceMovementId` = the invoice or delivery line's first issue movement) and the original batch. The inventory engine books Dr Inventory / Cr COGS.
- Non-restock lines (credit only) touch no stock.

**Cancellation (full reversal)** happens in one transaction:
1. `CancelOpenItemsAsync`. Finance refuses if anything is allocated.
2. Journal `ReverseAsync` for purpose `invoice`.
3. Inventory `ReverseAsync`, which also reverses the COGS journal, when the invoice issued the stock.
4. Order and delivery progress is rolled back.

**Idempotency and concurrency:**
- Every post, reverse, cancel, confirm, short-close, convert and quotation action uses `.RequireIdempotency()`, so a replay with the same key returns the first response.
- A second post with a new key fails on the state machine.
- Inventory postings are idempotent per (doc type, doc id).
- Stock is serialized by the inventory engine's row locks, so two invoices racing for the last units: one wins, the other gets `inventory.insufficient-stock` and rolls back completely (numbers, journal and open items).

## Endpoints (`/api/v1/sales`)

| Area | Endpoints |
|---|---|
| Quotations | `GET /quotations`, `GET /quotations/{id}`, `POST /quotations`, `PUT /quotations/{id}/lines`, `POST /quotations/{id}/send|revise|accept|reject`, `POST /quotations/{id}/convert` |
| Orders | `GET /orders`, `GET /orders/{id}`, `POST /orders`, `PUT /orders/{id}/lines`, `POST /orders/{id}/confirm|cancel|short-close` |
| Deliveries | `GET /deliveries`, `GET /deliveries/{id}`, `POST /deliveries`, `POST /deliveries/{id}/pick|post|reverse|cancel` |
| Invoices | `GET /invoices`, `GET /invoices/{id}`, `POST /invoices/preview`, `POST /invoices`, `PUT /invoices/{id}/lines`, `POST /invoices/{id}/submit|post|cancel` |
| Returns | `GET /returns`, `GET /returns/{id}`, `POST /returns`, `POST /returns/{id}/post|cancel` |

**Lists:**
- Paged, with the filters `status`, `customerId`, `from`, `to` (document date), `branchId`, `page` and `pageSize`.
- The response carries `netTotal`, `taxTotal` and `grossTotal` computed on the server over the whole filtered set.

**Other details:**
- `POST /invoices/preview` takes the create body and returns the computed document without saving it.
- Walk-in sales use the Parties walk-in customer (`POST /api/v1/parties/customers/walk-in`).

## Known limitations

- A return line restocks what its source line actually issued: Inventory splits it across the line's issue movements (batches/serials) in proportion to what each can still take back, each at its own cost. A FIFO line issued from several layers without batches is one movement and returns at its blended cost.
- Order lines are invoiced at the order's unit; there is no unit change between documents.
- Invoices created from an order or delivery cannot have their lines edited. Discard and recreate them instead.
- `Closed` (invoice) is not set yet.
- Multi-currency invoices use the spot rate of the document date. Realized FX is handled by Finance at settlement.

## Tests

**Unit** (`tests/Erp.UnitTests/SalesTests.cs`):
- pricing: exclusive and inclusive VAT, units, the discount and price-override permissions, trusted source prices, repricing that keeps line identity
- rounding and proration
- the credit decision table
- state machines for quotations, orders (including approval, short-close and reopening), invoices and deliveries
- return limits and return-line proration

**Integration** (`tests/Erp.IntegrationTests/SalesTests.cs`):
- order-to-cash (S-1). After every flow the trial balance balances, the AR control equals the open items, and the inventory GL equals the valuation.
- delivery-driven policy (J-2)
- return restock at the original cost (J-8)
- cancellation by full reversal (J-13)
- the Finance settlement event (PartiallyPaid → Paid)
- credit Block / RequireApproval / Warn
- idempotent posting
- concurrent posting for the last units
- tenant isolation and the module switch
- quotation conversion
