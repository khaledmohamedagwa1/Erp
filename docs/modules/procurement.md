# Procurement module

Schema `procurement`, migration order 100, module code `procurement`. The module is optional: every endpoint uses `.RequireModule("procurement")`.

Procurement covers purchase requests, supplier quotations, purchase orders, goods receipts, supplier invoices (three-way match), purchase returns / debit notes and landed costs. It owns no ledger of its own. Stock moves through Inventory (`IInventoryPostingService`), journals through Accounting (`IJournalPostingService`), payables through the Finance sub-ledger (`ISubledgerService`), tax through `ITaxCalculator` and approvals through `IApprovalService` (Workflow.Contracts).

## Contracts (`Erp.Modules.Procurement.Contracts`)

```csharp
public interface IProcurementQueries
{
    // Open (ordered, not yet received) quantity of approved / sent / partially received orders, in base units.
    Task<IReadOnlyList<OpenSupplyLine>> GetOpenSupplyAsync(IReadOnlyCollection<Guid> productIds, Guid? warehouseId = null, CancellationToken ct = default);
}

public sealed record OpenSupplyLine(Guid ProductId, Guid? VariantId, Guid WarehouseId, Guid PurchaseOrderId, Guid PurchaseOrderLineId,
    string PurchaseOrderNumber, Guid SupplierId, decimal OpenBaseQuantity, DateOnly? ExpectedDate);

public static class ProcurementDocumentTypes { GoodsReceipt, PurchaseInvoice, PurchaseReturn, LandedCost }   // source doc types
public static class ProcurementPermissions   { procurement.read, .request.manage, .quotation.manage, .order.manage,
    .receipt.manage, .receipt.post, .invoice.manage, .invoice.post, .return.manage, .return.post, .landed-cost.manage, .landed-cost.post }
```

`IPurchaseOrderDraftService.CreateDraftAsync` (wave 6): creates a Draft purchase order for another module (Manufacturing converts MRP purchase suggestions with it) by sending the regular create command, so validation, numbering and tax are the same as for a buyer-entered order.

Events consumed (outbox, inbox-deduplicated): `ApprovalCompleted` (Workflow.Contracts) for purchase requests, orders and invoices, and `InvoiceSettled` (Finance.Contracts) for the invoice payment status. Until the Workflow module is installed `NoApprovalRequired` is registered with `TryAddScoped`.

## Documents and states

| Document | States |
|---|---|
| Purchase request | Draft → Submitted → Approved / Rejected → Ordered → Closed (Submitted → Approved directly when no approval applies) |
| Quotation | Draft → Received → Accepted / Rejected. `GET /quotations/compare?requestId=` compares offers per product in the functional currency and flags the cheapest (ties: shorter lead time). |
| Purchase order | Draft → PendingApproval → Approved → Sent → PartiallyReceived → FullyReceived → Closed; Cancelled (only before any receipt or invoice). Lines keep ordered, received (net of returns and reversals), invoiced and invoiced-not-received quantities. Currency and rate are fixed on the order. |
| Goods receipt | Draft → Inspected → Approved → Posted → Reversed; Cancelled before posting. Accepted vs rejected quantity per line; batch, manufacture/expiry date and serials go to Inventory. |
| Supplier invoice | Draft → PendingApproval / Approved → Posted → PartiallyPaid → Paid; Cancelled (a posted invoice is cancelled by reversal). The internal number is assigned on posting; the supplier's number is unique per supplier among non-cancelled invoices. |
| Purchase return | Draft → Approved → Posted; Cancelled before posting. |
| Landed cost | Draft → Posted; Cancelled before posting. |

## Posting

All amounts posted to the ledger are in the legal entity's functional currency (journal rate 1); every posting runs in one transaction and every posting endpoint requires an `Idempotency-Key`. Posting an already-posted document returns it unchanged.

**Goods receipt.** Stock IN at the order's net unit price × order rate ÷ unit factor (base currency per base unit). Inventory posts the journal: Dr Inventory / Cr **GoodsReceivedNotInvoiced** (GRNI). When the order line was invoiced before receipt, the receipt instead clears **GoodsInvoicedNotReceived** (GINR) at the invoiced value; a receipt line that is only partly covered by such an invoice is refused (`procurement.partial-invoiced-receipt`). Reversal (only when nothing was invoiced or returned) reverses stock and journal through Inventory and restores the order quantities.

**Supplier invoice.** Lines are `Receipt` (posted receipt line), `Order` (invoice before receipt, only when the procurement policy has `ReceiptBeforeInvoice = false`) or `Expense` (account must be an active, non-control posting account of type Expense or Asset).

* Three-way match on submit: invoiced quantity ≤ still-invoiceable receipt quantity × (1 + quantity tolerance), |invoice price − order price| / order price ≤ price tolerance. Tolerances come from the procurement policy (defaults: quantity 0 %, price 2 %). An invoice with match exceptions is refused unless an approval rule applies (`procurement.match-exceptions`); with approval it waits for `ApprovalCompleted`.
* Journal (purpose `invoice`): Dr GRNI for the receipt value of the invoiced quantity (the last invoice of a receipt line clears the remainder, so GRNI ends at exactly zero); the difference between the invoice cost (net + non-recoverable tax, converted at the invoice rate) and that value is sent to `AdjustCostAsync` on the receipt movement — the on-hand share is Dr **Inventory**, the consumed share Dr **PurchasePriceVariance**; Dr GINR for order lines; Dr the account for expense lines; Dr **InputTax** per recoverable tax code; Cr **SupplierPayable** (supplier party and posting group). Bucket conversion is fitted so the credit equals round(gross × rate, 2), the base of the Finance open item.
* AP open items are created with the supplier's payment terms; Finance reports payment progress with `InvoiceSettled`. Withholding tax is applied by Finance at payment.
* Cancel of a posted invoice: open items cancelled (refused once allocated), journal and cost adjustments reversed, receipt/order invoiced quantities restored. An invoice-before-receipt cannot be cancelled once the goods were received.

**Purchase return.** Stock OUT at the original receipt cost (`CostSourceMovementId`). The not-yet-invoiced quantity is returned first: Dr GRNI (receipt value) / Cr Inventory. The invoiced part becomes a debit note at the invoiced price and rate: Dr SupplierPayable (gross) / Cr InputTax / Cr Inventory, differences to PPV; a negative AP open item is created and applied to the supplier's open invoices (`ApplyCreditNoteAsync`). Returning goods that were invoiced before receipt is not supported (cancel or credit the invoice instead).

**Landed cost.** Charges (with optional purchase tax) are allocated to the posted lines of the selected receipts by value or by quantity (residual cent on the largest share). Each share goes through `AdjustCostAsync`: on-hand share Dr Inventory, consumed share Dr PPV. Credit: SupplierPayable with an open item when a supplier is given, otherwise **LandedCostClearing**.

## Endpoints (`/api/v1/procurement`)

| Resource | Endpoints |
|---|---|
| requests | `GET /`, `GET /{id}`, `POST /`, `POST /{id}/submit\|revise\|close` |
| quotations | `GET /?requestId=`, `GET /compare?requestId=`, `POST /`, `POST /{id}/receive\|accept\|reject` |
| orders | `GET /?supplierId&status`, `GET /{id}`, `POST /`, `PUT /{id}/lines`, `POST /{id}/submit\|send\|cancel\|close` |
| receipts | `GET /?purchaseOrderId&supplierId&status`, `GET /{id}`, `POST /`, `POST /{id}/inspect\|approve\|cancel`, `POST /{id}/post`*, `POST /{id}/reverse`* |
| invoices | `GET /?supplierId&status`, `GET /{id}`, `GET /{id}/match`, `POST /`, `POST /{id}/submit`, `POST /{id}/post`*, `POST /{id}/cancel`* |
| returns | `GET /?supplierId`, `GET /{id}`, `POST /`, `POST /{id}/approve\|cancel`, `POST /{id}/post`* |
| landed-costs | `GET /`, `GET /{id}`, `GET /{id}/allocation`, `POST /`, `POST /{id}/cancel`, `POST /{id}/post`* |

`*` requires `Idempotency-Key`. Lists are paged (`page`, `pageSize`). Enum inputs are strings (`kind`: Receipt/Order/Expense; `basis`: Value/Quantity).

## Decisions and limitations

* Receipts are valued at the order price and rate; exchange-rate differences between order and invoice are treated as price variance (on-hand share capitalised).
* Non-stockable products cannot be received; invoice them as expense lines.
* Serial-tracked receipt lines adjust cost through the first serial's movement (`AdjustCostAsync` works per receipt movement).
* A debit note uses one rate (that of the receipt's last invoice) for all invoiced lines of the return.
* Approval facts sent to the engine: amount and currency, `amountFunctional`, and for invoices `matchExceptions` (0/1) and `maxPriceVariancePercent`.
