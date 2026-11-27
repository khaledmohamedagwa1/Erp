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
    Guid? CostSourceMovementId = null, ReservationRef? ConsumeReservation = null, Guid? CounterWarehouseId = null, string? CounterRole = null,
    decimal? TotalCost = null, Guid? BinId = null);

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
    Task<IReadOnlyList<ReturnableIssue>> GetReturnableIssuesAsync(string sourceDocType, Guid sourceDocId, Guid sourceLineId, CancellationToken ct = default);
}
```

The movement types are the constants in `InventoryMovementTypes`: OpeningStock, PurchaseReceipt, PurchaseReturn, SalesIssue, SalesReturn, TransferOut, TransferIn, AdjustmentGain, AdjustmentLoss, ProductionIssue, ProductionReturn, ProductionReceipt and CostAdjustment. The `Inbound` and `Outbound` sets decide each type's direction.

### Posting rules

- **Run inside the caller's transaction** (ADR-006). Without one the call returns `inventory.transaction-required`. Stock, valuation and any journal commit or roll back together with the source document.
- **Idempotent per source document.** Posting a `(SourceDocType, SourceDocId)` a second time returns the first result with `AlreadyPosted = true`. The database also guards this with `UX_InventoryMovement_Source`.
- **Journals.** The posting service writes stock movements only. It writes a journal only when `PostJournal = true`, and then only for lines that carry a `CounterRole`. Each such line books Dr/Cr `Inventory` (or `InventoryInTransit` for a transit warehouse) against its counter role, with source module = the caller and purpose `inventory`. Procurement, sales and similar modules usually build their own journals from the returned costs.
- **Units.** Quantities are converted to the base unit with the catalog's unit factors. Serial-tracked items need whole base quantities and exactly one serial per unit.
- **Cost.** Inbound movements need `UnitCost`, except returns that cost from `CostSourceMovementId`. A missing cost returns `inventory.cost-required`. Outbound movements are valued by the item's method. An explicit `UnitCost` or a `CostSourceMovementId` on an outbound line is used as given; returns to a supplier at the receipt cost are the example. `TotalCost` (inbound only) receives exactly that value; transfers use it so carried cost leaves no residue.
- **Returns by issue layer.** An inbound line whose `CostSourceMovementId` is an outbound movement is a return of that issue *line*: all outbound movements of the line (one per batch or serial) are found, and the return is split across them in proportion to what each can still take back (whole units when all quantities are whole, largest remainder; otherwise 6 dp with the last part taking the remainder). Each part comes back into its own batch at its own cost; serial returns use the movement that issued that serial. `GetReturnableIssuesAsync` (and `GET returnable-issues`) shows issued / returned / returnable per movement. Sales returns, credit notes and POS refunds keep passing the first movement — the engine does the rest.
- **Bins.** See *Bin locations* below; `BinId` is the put-away or pick bin.
- **Batches.** Inbound batch-tracked lines need `Batch`; the batch is found or created by number. Outbound lines without a batch are allocated FEFO, skipping expired batches. Issuing from an expired batch returns `inventory.batch-expired`.
- **Reservations.** `ConsumeReservation` consumes the open quantity of that reservation, up to the issued quantity, in the same statement that reduces stock.
- **Reversal.** `ReverseAsync` posts opposite movements at the original cost. It restores or reduces FIFO layers, puts serials back and undoes bin quantities. It reverses each journal that the original posting wrote. If the received stock has already been consumed, the call returns `inventory.reversal-stock-consumed`.
- **Cost adjustment.** `AdjustCostAsync` handles landed cost and invoice price variance. The part of the amount that matches stock still on hand is capitalised (WAC: in proportion to quantity on hand; FIFO: in proportion to what remains of the receipt's layer). The rest is returned as `ToVariance` for the caller to expense. For FIFO the layer's remaining quantity at that moment is recorded (`CostLayerAdjustment`), so a later reversal is **exact**: only `amount × remaining now / remaining then` leaves the valuation and the layer; the consumed share (already in cost of sales at the adjusted cost) is put back with a journal Dr Inventory / Cr PurchasePriceVariance (purpose `inventory-adjustment-reversal`), because the caller's own journal reversal takes the whole amount out of Inventory.

## Costing engine

| Rule | Behaviour |
|---|---|
| Valuation area | Per legal entity (default) or per warehouse (`InventoryPolicySettings.ValuationArea = "Warehouse"`). |
| Weighted average | A receipt recalculates the average. An issue is valued at the average. The issue that empties the item takes the exact remaining value, so no residue is left. Money is held to 4 dp and unit cost to 6 dp. |
| Negative stock | Allowed only when the company policy `AllowNegativeStock` is on. The issue is valued at the last average. The receipt that covers the shortfall trues it up: `covered × (receiptCost − issuedAverage)` goes to `CostTrueUp` (5130) in a separate journal with purpose `inventory-true-up`. |
| FIFO | Every receipt opens a layer; issues consume the oldest layers first and record each consumption. FIFO items never go negative (`inventory.fifo-negative-not-allowed`). |
| Concurrency | Rows are locked in a fixed order: product, variant, warehouse, batch. Each balance changes in a single `UPDATE … WHERE OnHand - @q >= 0` statement, so concurrent issues cannot oversell. Valuation rows are locked with `UPDLOCK, HOLDLOCK`. |
| Immutability | The trigger `inventory.TR_InventoryMovement_Immutable` refuses UPDATE and DELETE on movements. Corrections are made by reversal only. |
| Back-dated postings | Costs are computed in posting sequence. **Recost** replays a weighted-average item's movements in its valuation area in transaction-date order and corrects the issues whose cost changes (see below). |

### Recost (back-dated postings)

`POST recost` (`inventory.recost`, sensitive, idempotent) runs a recost for one item from a date; `preview: true` returns the corrections without posting. With the inventory policy `AutoRecostBackdatedPostings` on (off by default), a posting dated before the item's latest movement in its valuation area queues a `RecostRun` (trigger `BackdatedPosting`); the `RecostRequested` event is handled after commit through the outbox (inbox-deduplicated). Results: `GET recost-runs`, `GET recost-runs/{id}`.

- Replay: receipts, returns at original cost, transfers, production movements, true-ups and earlier cost adjustments keep their recorded values; **sales issues** and **adjustment losses** are recalculated at the replayed average (the issue that empties the item takes the remaining value). Reversed movements and movements with a cost source keep their cost. Issues before `fromDate` keep their cost.
- Each correction is a value-only `CostAdjustment` movement (`SourceDocType = InventoryRecost`, related to the issue, dated the run date) and one journal per run (purpose `inventory-recost`): Dr CostOfGoodsSold / InventoryAdjustmentLoss, Cr Inventory when the issue was under-costed, the reverse when over-costed. The journal is posted before anything changes in Inventory; a failing automatic run is marked `Failed` with the reason (not retried).
- FIFO items are not recosted (`inventory.recost-fifo-not-supported`): their issues consume layers in sequence.

### Bin locations

Opt-in per warehouse: a warehouse with at least one active bin (`POST warehouses/{id}/bins`) is bin-managed. Bins split quantities only — the ledger, balances and valuation stay per warehouse.

- **Put-away:** an inbound movement goes to the line's `BinId`, else the warehouse's default receiving bin, else it stays outside any bin until moved with a bin transfer.
- **Pick:** an outbound movement takes from the line's `BinId` (`inventory.bin-insufficient` when short), else from bins in code order and then from stock outside any bin. With negative stock allowed the shortfall goes below zero outside the bins.
- **Bin transfer:** `POST bin-transfers` moves quantity between bins (or from/to "no bin") under the item's balance lock; no ledger movement.
- Every bin change is logged in `BinMovement` (with the inventory movement), which is how reversals undo them. `GET warehouses/{id}/bin-stock` lists stock per bin plus the not-yet-put-away remainder. Bins are not counted separately (counts are per warehouse).

## Documents and their journals

| Document | Flow | Journal (purpose `inventory`, module `inventory`) |
|---|---|---|
| Opening stock (`OS-`) | Draft → InReview → Approved → Posted (or Cancelled) | Dr Inventory / Cr OpeningBalanceEquity (3300) |
| Adjustment (`ADJ-`) | Draft → Approved → Posted. Reasons: Damage, Loss, Found, CountVariance, Expiry, Other | Gain: Dr Inventory / Cr InventoryAdjustmentGain (4910). Loss: Dr InventoryAdjustmentLoss (5210) / Cr Inventory |
| Transfer (`TRF-`) | Draft → Approved → InTransit → PartiallyReceived → Completed. **Direct** transfers (`direct: true`): Approved → Completed in one posting | Issue: Dr InventoryInTransit (1320) / Cr Inventory. Receipt: Dr Inventory / Cr InventoryInTransit. Close with a discrepancy: Dr InventoryAdjustmentLoss / Cr InventoryInTransit. Direct: both legs in one command (the 1320 lines net to zero) |
| Count (`CNT-`) | Open → Counting (snapshot) → VarianceCalculated → Approved → Posted | Posting creates and posts a `CountVariance` adjustment. Variance = counted − (snapshot + movements since the snapshot). Uncounted lines are ignored. Serial-tracked products are counted **by serial**: one snapshot line per serial in stock; the count entry lists the serials found (missing → loss of that serial, unknown → found stock); serial variances are grouped per product into adjustment lines that carry the serial numbers. |

Stock leaves the source warehouse when the transfer is issued and sits in the legal entity's Transit warehouse until it is received. Each receipt carries cost in whole cents and the receipt that empties a line carries the exact remainder of the issued value, so nothing is left in 1320. Transfers between legal entities are refused (`inventory.cross-legal-entity-transfer`).

## Endpoints (`/api/v1/inventory`)

Every endpoint needs authentication, an active company and the module. An endpoint marked † also needs an `Idempotency-Key` header.

| Area | Endpoints | Permission |
|---|---|---|
| Inquiry | `GET stock`, `GET movements`, `GET valuation`, `GET batches`, `GET serials`, `GET returnable-issues?sourceDocType&sourceDocId&sourceLineId`, `GET recost-runs`, `GET recost-runs/{id}`, `GET warehouses/{id}/bins`, `GET warehouses/{id}/bin-stock` | `inventory.read` |
| Bins | `POST warehouses/{id}/bins`, `PUT bins/{id}`, `POST bin-transfers` † | `inventory.bin.manage` |
| Recost | `POST recost` † (`productId`, `warehouseId`, `fromDate`, `preview`) | `inventory.recost` (sensitive) |
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

- Transfers pass through InventoryInTransit (1320) unless created as direct. Legs within one area move cost without revaluing the item.
- The posting sequence is kept per item within its valuation area. Recost corrects weighted-average issues afterwards; it does not cascade into downstream documents (a recosted production issue, transfer or return keeps its recorded value — only sales issues and adjustment losses are corrected). FIFO items are not recosted.
- Automatic recost is opt-in (`AutoRecostBackdatedPostings`) and runs asynchronously through the outbox; corrections are dated on the run date.
- Bins are not part of counts, reservations or FEFO; a bin-managed warehouse may hold stock outside any bin (not yet put away).
- Document lines are stored as JSON columns. Document numbers are assigned when the document is created.
- Reservations are held on the aggregate (non-batch) balance row.
- Reversing a weighted-average cost adjustment still removes the whole applied amount from the item's value (the average absorbs it).
- Stock inquiry (`GET /api/v1/inventory/stock`) reports `Available = OnHand − Reserved`, `Incoming` (open purchase-order quantity from Procurement.Contracts + quantity in transit on inbound transfers), `Outgoing` (open sales-order demand from Sales.Contracts) and `Projected = Available + Incoming − Outgoing`. Procurement and Sales are optional: without them their part is 0. Rows that only have incoming/outgoing quantities are listed too.
- Partial transfer receipts carry whole cents (minor units of 2-decimal currencies); for a 0-decimal functional currency a sub-unit residue can remain until the line's last receipt.
