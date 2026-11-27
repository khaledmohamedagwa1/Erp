# Inventory module

Schema `inventory`, migration order 80, module code `inventory`. The module is optional: every endpoint uses `.RequireModule("inventory")`.

The module owns the stock ledger, the balances, the valuation (weighted average or FIFO), cost layers, batches, serials and reservations. It also owns four documents of its own: opening stock, adjustments, transfers and counts. Other modules (procurement, sales, POS, manufacturing) move stock **only** through the contracts below.

## Contracts (`Erp.Modules.Inventory.Contracts`)

```csharp
public interface IInventoryPostingService
{
    Task<Result<InventoryPostingResult>> PostAsync(InventoryPostingRequest request, CancellationToken ct = default);
    Task<Result<InventoryPostingResult>> ReverseAsync(string sourceDocType, Guid sourceDocId, DateOnly reversalDate, CancellationToken ct = default);
    Task<Result<CostAdjustmentResult>> AdjustCostAsync(CostAdjustmentRequest request, CancellationToken ct = default);
}

public sealed record InventoryPostingRequest(Guid? LegalEntityId, DateOnly TransactionDate, string SourceModule, string SourceDocType,
    Guid SourceDocId, string? ReferenceNo, IReadOnlyList<InventoryPostingLine> Lines, bool PostJournal = false, string? JournalDescription = null);

public sealed record InventoryPostingLine(Guid LineId, Guid ProductId, Guid? VariantId, Guid WarehouseId, Guid UomId, decimal Quantity,
    string MovementType, decimal? UnitCost = null, BatchInput? Batch = null, IReadOnlyList<string>? Serials = null,
    Guid? CostSourceMovementId = null, ReservationRef? ConsumeReservation = null, Guid? CounterWarehouseId = null, string? CounterRole = null);

public interface IReservationService
{
    Task<Result<ReservationInfo>> ReserveAsync(ReservationRequest request, CancellationToken ct = default);
    Task<Result<ReservationInfo>> ReleaseAsync(ReservationRef reference, decimal? baseQuantity = null, CancellationToken ct = default);
    Task<IReadOnlyList<ReservationInfo>> GetAsync(string demandType, Guid demandId, CancellationToken ct = default);
}

public interface IStockQueries
{
    Task<IReadOnlyList<StockLevel>> GetStockAsync(IReadOnlyCollection<Guid> productIds, Guid? warehouseId = null, CancellationToken ct = default);
    Task<ItemCost?> GetItemCostAsync(Guid productId, Guid? variantId, Guid warehouseId, CancellationToken ct = default);
    Task<decimal> GetValuationTotalAsync(Guid legalEntityId, CancellationToken ct = default);
    Task<MovementInfo?> GetMovementAsync(Guid movementId, CancellationToken ct = default);
    Task<IReadOnlyList<MovementInfo>> GetMovementsBySourceAsync(string sourceDocType, Guid sourceDocId, CancellationToken ct = default);
}
```

The movement types are the constants in `InventoryMovementTypes`: OpeningStock, PurchaseReceipt, PurchaseReturn, SalesIssue, SalesReturn, TransferOut, TransferIn, AdjustmentGain, AdjustmentLoss, ProductionIssue, ProductionReturn, ProductionReceipt and CostAdjustment. The `Inbound` and `Outbound` sets decide each type's direction.

### Posting rules

- **Run inside the caller's transaction** (ADR-006). Without one the call returns `inventory.transaction-required`. Stock, valuation and any journal commit or roll back together with the source document.
- **Idempotent per source document.** Posting a `(SourceDocType, SourceDocId)` a second time returns the first result with `AlreadyPosted = true`. The database also guards this with `UX_InventoryMovement_Source`.
- **Journals.** The posting service writes stock movements only. It writes a journal only when `PostJournal = true`, and then only for lines that carry a `CounterRole`. Each such line books Dr/Cr `Inventory` (or `InventoryInTransit` for a transit warehouse) against its counter role, with source module = the caller and purpose `inventory`. Procurement, sales and similar modules usually build their own journals from the returned costs.
- **Units.** Quantities are converted to the base unit with the catalog's unit factors. Serial-tracked items need whole base quantities and exactly one serial per unit.
- **Cost.** Inbound movements need `UnitCost`, except returns that cost from `CostSourceMovementId`. A missing cost returns `inventory.cost-required`. Outbound movements are valued by the item's method. An explicit `UnitCost` or a `CostSourceMovementId` on an outbound line is used as given; returns to a supplier at the receipt cost are the example.
- **Batches.** Inbound batch-tracked lines need `Batch`; the batch is found or created by number. Outbound lines without a batch are allocated FEFO, skipping expired batches. Issuing from an expired batch returns `inventory.batch-expired`.
- **Reservations.** `ConsumeReservation` consumes the open quantity of that reservation, up to the issued quantity, in the same statement that reduces stock.
- **Reversal.** `ReverseAsync` posts opposite movements at the original cost. It restores or reduces FIFO layers and puts serials back. It reverses each journal that the original posting wrote. If the received stock has already been consumed, the call returns `inventory.reversal-stock-consumed`.
- **Cost adjustment.** `AdjustCostAsync` handles landed cost and invoice price variance. The part of the amount that matches stock still on hand is capitalised (WAC: in proportion to quantity on hand; FIFO: in proportion to what remains of the receipt's layer). The rest is returned as `ToVariance` for the caller to expense.

## Costing engine

| Rule | Behaviour |
|---|---|
| Valuation area | Per legal entity (default) or per warehouse (`InventoryPolicySettings.ValuationArea = "Warehouse"`). |
| Weighted average | A receipt recalculates the average. An issue is valued at the average. The issue that empties the item takes the exact remaining value, so no residue is left. Money is held to 4 dp and unit cost to 6 dp. |
| Negative stock | Allowed only when the company policy `AllowNegativeStock` is on. The issue is valued at the last average. The receipt that covers the shortfall trues it up: `covered × (receiptCost − issuedAverage)` goes to `CostTrueUp` (5130) in a separate journal with purpose `inventory-true-up`. |
| FIFO | Every receipt opens a layer; issues consume the oldest layers first and record each consumption. FIFO items never go negative (`inventory.fifo-negative-not-allowed`). |
| Concurrency | Rows are locked in a fixed order: product, variant, warehouse, batch. Each balance changes in a single `UPDATE … WHERE OnHand - @q >= 0` statement, so concurrent issues cannot oversell. Valuation rows are locked with `UPDLOCK, HOLDLOCK`. |
| Immutability | The trigger `inventory.TR_InventoryMovement_Immutable` refuses UPDATE and DELETE on movements. Corrections are made by reversal only. |

## Documents and their journals

| Document | Flow | Journal (purpose `inventory`, module `inventory`) |
|---|---|---|
| Opening stock (`OS-`) | Draft → InReview → Approved → Posted (or Cancelled) | Dr Inventory / Cr OpeningBalanceEquity (3300) |
| Adjustment (`ADJ-`) | Draft → Approved → Posted. Reasons: Damage, Loss, Found, CountVariance, Expiry, Other | Gain: Dr Inventory / Cr InventoryAdjustmentGain (4910). Loss: Dr InventoryAdjustmentLoss (5210) / Cr Inventory |
| Transfer (`TRF-`) | Draft → Approved → InTransit → PartiallyReceived → Completed | Issue: Dr InventoryInTransit (1320) / Cr Inventory. Receipt: Dr Inventory / Cr InventoryInTransit. Close with a discrepancy: Dr InventoryAdjustmentLoss / Cr InventoryInTransit |
| Count (`CNT-`) | Open → Counting (snapshot) → VarianceCalculated → Approved → Posted | Posting creates and posts a `CountVariance` adjustment. Variance = counted − (snapshot + movements since the snapshot). Uncounted lines are ignored. |

Stock leaves the source warehouse when the transfer is issued and sits in the legal entity's Transit warehouse until it is received. Transfers between legal entities are refused (`inventory.cross-legal-entity-transfer`).

## Endpoints (`/api/v1/inventory`)

Every endpoint needs authentication, an active company and the module. An endpoint marked † also needs an `Idempotency-Key` header.

| Area | Endpoints | Permission |
|---|---|---|
| Inquiry | `GET stock`, `GET movements`, `GET valuation`, `GET batches`, `GET serials` | `inventory.read` |
| Opening stock | `GET/POST opening-stock`, `GET opening-stock/{id}`, `PUT …/{id}/lines`, `POST …/{id}/submit` · `return-to-draft` · `cancel` | `inventory.document.manage` |
| | `POST …/{id}/approve` | `inventory.document.approve` |
| | `POST …/{id}/post` † | `inventory.document.post` |
| Adjustments | `GET/POST adjustments`, `GET adjustments/{id}`, `POST …/{id}/cancel` / `approve` / `post` † | manage / approve / post |
| Transfers | `GET/POST transfers`, `GET transfers/{id}`, `POST …/{id}/approve` · `cancel` · `issue` † · `receive` † · `close` † | manage / approve / post |
| Counts | `POST counts`, `GET counts/{id}`, `POST …/{id}/snapshot`, `PUT …/{id}/counts`, `POST …/{id}/finish` · `reopen` · `cancel` · `approve` · `post` † | manage / approve / post |
| Reservations | `GET reservations?demandType&demandId`, `POST reservations` †, `POST reservations/release` † | `inventory.reservation.manage` |

## Cross-module additions

- Organization contracts: `GetWarehousesAsync(IReadOnlyCollection<Guid>)` and `GetTransitWarehouseAsync(Guid legalEntityId)`, both returning `WarehouseSummary`.
- Configuration contracts: `IPolicyReader` with `GetInventoryPolicyAsync`, `GetSalesPolicyAsync` and `GetProcurementPolicyAsync`, returning the typed `InventoryPolicySettings`, `SalesPolicySettings` and `ProcurementPolicySettings`. Each falls back to defaults when no policy is stored.
- Catalog: `IProductUsageProbe` is implemented. A product with any stock movement counts as used, so Catalog applies its rules for products in use.

## Decisions and limitations

- Transfers always pass through InventoryInTransit (1320), even within one valuation area. Legs within one area move cost without revaluing the item.
- The posting sequence is kept per item within its valuation area.
- Serial-tracked items are left out of counts.
- Document lines are stored as JSON columns. Document numbers are assigned when the document is created.
- Reservations are held on the aggregate (non-batch) balance row.
- Reversing a FIFO cost adjustment changes the layer's cost only approximately when the layer has been partly consumed since.
- `Incoming` stock is reported as 0 until procurement exists.
- Partial transfer receipts can leave rounding residues of a fraction of a cent in 1320.
