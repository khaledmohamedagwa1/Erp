# Manufacturing module

Schema `manufacturing`, migration order 130, module code `manufacturing`. The module is optional: every endpoint uses `.RequireCompany()` and `.RequireModule("manufacturing")`. The module catalog lists `inventory` and `procurement` as its dependencies. The `manufacturing` business preset turns it on.

The module covers:

- **Setup:** work centers, routings, and bills of materials with versions.
- **Production orders:** Draft through Closed, including WIP accounting.
- **Basic MRP:** infinite capacity, low-level codes, netting, safety stock, lot sizing, lead-time offset, pegging and a shortage list.

All stock moves go through `IInventoryPostingService` and `IReservationService`. All journals go through `IJournalPostingService`. The module never writes another module's tables.

> **Not implemented: full manufacturing cost accounting.** Labour, machine and overhead are *applied* to WIP at the work-center rates (Dr WIP / Cr Applied Labour, Applied Machine, Applied Overhead).
>
> The following are **not** implemented:
>
> - Clearing the applied accounts against actual payroll and overhead spending (absorption/spending variance analysis).
> - Standard costing with price, usage or efficiency variance splits.
> - Activity-based costing.
> - Revaluation of finished goods after close.
>
> The Applied* accounts therefore build up credit balances. Finance must clear them manually against the actual expense accounts. Order-level residual WIP goes to a single ProductionVariance account, or to AbnormalScrap.

## Contracts (`Erp.Modules.Manufacturing.Contracts`)

```csharp
public static class ManufacturingPermissions
{
    Read = "manufacturing.read"; SetupManage = "manufacturing.setup.manage"; OrderManage = "manufacturing.order.manage";
    OrderPost = "manufacturing.order.post" /* sensitive */; PlanningRun = "manufacturing.planning.run";
}

public interface IManufacturingQueries
{
    // Open production supply (remaining output) and open component demand (remaining requirement not yet reserved), per product.
    Task<IReadOnlyList<ProductionSupplyDemand>> GetOpenSupplyDemandAsync(IReadOnlyCollection<Guid> productIds, CancellationToken ct = default);
}

// MRP extension points: register more implementations in DI and the next MRP run picks them up.
public sealed record MrpDemand(Guid ProductId, decimal Quantity, DateOnly DueDate, string SourceType, string SourceReference);
public sealed record MrpSupply(Guid ProductId, decimal Quantity, DateOnly DueDate, string SourceType, string SourceReference);
public interface IMrpDemandSource { string Name { get; } Task<IReadOnlyList<MrpDemand>> GetDemandAsync(Guid companyId, DateOnly horizonEnd, CancellationToken ct = default); }
public interface IMrpSupplySource { string Name { get; } Task<IReadOnlyList<MrpSupply>> GetSupplyAsync(Guid companyId, DateOnly horizonEnd, CancellationToken ct = default); }
```

Quantities in `MrpDemand` and `MrpSupply` are in the product's **base UOM**.

### Pluggable sources (to wire after merge)

| Source | Registered implementation | Status |
|---|---|---|
| Production orders (component demand and FG supply) | `ProductionOrderMrpSource` (`production-orders`) | Live |
| Sales orders (independent demand) | `SalesOrderMrpSource` (`sales-orders`) | Confirmed, undelivered lines from `ISalesQueries.GetAllOpenDemandAsync`, minus the part already reserved in stock (from `IReservationService`), within the horizon |
| Purchase orders (scheduled receipts) | `PurchaseOrderMrpSource` (`purchase-orders`) | Open approved/sent/partially received PO lines from `IProcurementQueries.GetAllOpenSupplyAsync`, dated at the expected date (today if none), within the horizon |

When Sales and Procurement are merged, each of them should do two things:

- Implement `IMrpDemandSource` / `IMrpSupplySource` from its open order lines.
- Replace the placeholder registration in `ManufacturingModule.Register` with its own implementation, or register the implementation from its own module and delete the placeholder.

Until then, MRP plans only against stock on hand, production orders and safety stock. The integration tests inject demand through a test `IMrpDemandSource`.

## Domain

| Aggregate | Notes |
|---|---|
| `WorkCenter` | Labour, machine and overhead rates per hour, stored as decimal(19,6). The overhead basis is `LaborHours` or `MachineHours`. `Cost(labourH, machineH)` returns (labour, machine, overhead). |
| `Routing` | A product plus operations (sequence, work center, setup hours, run hours per unit), stored as a JSON column. |
| `BillOfMaterials` / `BomVersion` | A version has a number, an effective-from/to range, output quantity, expected yield %, and lines (component, quantity per output, UOM, scrap %, issue method `Manual`/`Backflush`, alternative components). Statuses: Draft → Active → Superseded / Obsolete. |
| `ProductionOrder` | Number `MO-…`, component and output warehouses, snapshots of components and operations, a transaction history, and a WIP balance. |
| `ItemPlanningParameters` | Safety stock, lead time in days, lot rule (`LotForLot`, `FixedMinimum`, `Multiple`) with lot size, and procurement type (`Make` / `Buy`). |
| `MrpRun` | Number `MRP-…`, run date, horizon, suggestions with pegging, and shortages. |

### BOM rules

- **Explosion:** required quantity per unit = `quantityPerOutput / outputQuantity × (1 + scrap%) ÷ (yield% / 100)`, rounded to 6 dp. Requirements are kept in the line's UOM. Production orders also snapshot the UOM factor to the base UOM.
- **Activation checks:**
  - **Cycles:** activation builds the graph of all *usable* versions (Active or Superseded) across all BOMs, adds the candidate, and refuses a multi-level cycle (`manufacturing.bom-cycle`). A component equal to its own output is refused when the version is created.
  - **Dates:** activation refuses a date range that overlaps another usable version (`manufacturing.bom-version-overlap`). The one exception is an earlier *open-ended* version: it is closed at `from − 1` and becomes Superseded.
- **Effective version:** a usable version whose range contains the date. If several match, the one with the latest start wins.

## Production order lifecycle and accounting

`Draft → Planned → Released → InProgress → PartiallyCompleted → Completed → Closed`. An order can move to `Cancelled` from Draft, Planned or Released, provided no stock has moved.

| Step | Effect | Journal (via inventory `PostJournal`, counter role `WorkInProgress`, or a direct journal) |
|---|---|---|
| **Plan** | Snapshots the effective BOM version (or the one given) and the active routing (or the one given): component requirements with UOM factors, and planned operation hours. | — |
| **Release** | Reserves each component through `IReservationService` (demand type `ProductionOrder`, demand line = BOM line id). The reservation is capped at the stock available now; the rest is left to MRP. | — |
| **Issue** (`/issues`) | Inventory movement `ProductionIssue` out of the component warehouse. Consumes the reservation. | Dr WIP / Cr Inventory, at inventory cost |
| **Return** (`/returns`) | Inventory movement back in, at the line's average issued cost. A return cannot exceed what was issued. | Dr Inventory / Cr WIP |
| **Report operation** | Work-center hours × rates. | Dr WIP / Cr AppliedLabor, AppliedMachine, AppliedOverhead |
| **Receive FG** | First backflushes the `Backflush` components owed for the output so far (`min(required, perUnit × (completed + scrapped)) − issued`). Then receives the FG at **unit cost = WIP balance ÷ max(remaining quantity, received quantity)**. | Backflush: Dr WIP / Cr Inventory. Receipt: Dr Inventory / Cr WIP |
| **Scrap** | Normal scrap only: records the quantity, and its cost stays in WIP. Later receipts absorb it, because the remaining WIP is spread over the remaining good output. | — |
| **Close** | Backflushes anything still owed for reported output, releases every open reservation, then posts the residual WIP. | Residual > 0: Dr ProductionVariance (or AbnormalScrap when `abnormalScrap: true`) / Cr WIP. Residual < 0: the reverse |

Invariants:

- Every transaction stores its signed WIP amount, so `Σ transactions.wipAmount = WipBalance`. This equals the order's movements on the WIP control account.
- The WIP account (template 1350) allows only `manufacturing` postings.
- After close, `WipBalance = 0`.

Worked example (S-3 / J-9 in the integration tests):

- **BOM:** 2 X + 3 Y + 1 Z per FG. The order is for 100.
- **Materials:** X 200 @5 + Y 300 @2 + Z 100 @4 = 2,000.
- **Operation:** 10 labour h @30 + 8 machine h @50 + overhead 8 machine h @25 = 900.
- **Receipts:** receiving 60 gives 1,740, and receiving 40 gives 1,160. FG is valued at 2,900, and WIP ends at 0.

Every material, backflush and receipt event posts under its own source document id (`SequentialGuid`), because inventory needs a unique `(SourceDocType, SourceDocId)` pair. The doc types are `manufacturing.material-issue`, `…material-return`, `…backflush`, `…operation`, `…finished-goods-receipt` and `…close`.

## MRP

The `RunMrpCommand` takes an optional run date (default today) and an optional horizon in days (default 56). It runs these steps:

1. Collects demand and supply from every registered source. Demand beyond the horizon is ignored.
2. Builds the BOM graph from usable versions, and computes low-level codes (the deepest use of each product).
3. Processes products in low-level-code order:
   - Starting from available stock (on hand − reserved, all warehouses of the company), it merges scheduled receipts and requirements by date, with receipts first on the same day.
   - Whenever projected stock drops below safety stock, it plans `LotSize(shortfall)`, due on the requirement date. The order-by date is `due − lead time`.
   - An order whose order-by date is before the run date is flagged late and listed as a shortage.
4. A *Make* item explodes its planned order into dependent demand for its components, dated at the order-by date (clamped to the run date) and pegged as `PlannedProduction`.
   - Procurement type defaults to *Make* when the product has a BOM, and to *Buy* otherwise.
   - A Make item without a usable BOM becomes a shortage.
5. Stores the result as an `MrpRun`, with suggestions (Produce or Purchase, quantity, due date, order-by date, late flag, pegging) and shortages.

**Converting** (`/mrp-runs/{id}/convert`) turns open *Produce* suggestions into Draft production orders, with source reference `MRP <number>`. Purchase suggestions stay suggestions until procurement is wired (`manufacturing.suggestion-not-convertible`).

Open production-order component demand counts only the part not already reserved. Reserved stock is already excluded from available stock, so counting it again would double it.

## Endpoints (`/api/v1/manufacturing`)

| Method & path | Permission |
|---|---|
| `GET /work-centers`, `GET /routings`, `GET /boms`, `GET /boms/{id}`, `GET /planning-parameters`, `GET /production-orders`, `GET /production-orders/{id}`, `GET /mrp-runs`, `GET /mrp-runs/{id}` | `manufacturing.read` |
| `POST /work-centers`, `PUT /work-centers/{id}`, `POST /routings`, `PUT /routings/{id}`, `POST /boms`, `POST /boms/{id}/versions`, `PUT /boms/{id}/versions/{versionId}`, `POST /boms/{id}/versions/{versionId}/activate`, `POST …/obsolete`, `PUT /planning-parameters` | `manufacturing.setup.manage` |
| `POST /production-orders`, `POST /production-orders/{id}/plan`, `/release`, `/cancel` | `manufacturing.order.manage` |
| `POST /production-orders/{id}/issues`, `/returns`, `/operations`, `/receipts`, `/scrap`, `/close` | `manufacturing.order.post`, **`.RequireIdempotency()`** (an `Idempotency-Key` header is required) |
| `POST /mrp-runs`, `POST /mrp-runs/{id}/convert` | `manufacturing.planning.run` |

## Decisions and limitations

- **Cost accounting:** full manufacturing cost accounting is **not implemented** (see the note at the top). Only applied-rate absorption and a single residual variance at close exist.
- **Receipt costing:** the order's own average cost, `WIP ÷ remaining quantity`. Inventory computes the receipt total from the unit cost, so the last receipt can leave a rounding residual of a few hundredths. Close posts that residual to ProductionVariance.
- **Backflush:** proportional to *reported* output (completed + scrapped). A short close does **not** consume the backflush components of units that were never produced. Their reservations are released.
- **Scrap:** only normal scrap is modelled as a quantity; its cost is absorbed by the good output. Abnormal loss is written off at close with `abnormalScrap: true`, which sends the whole residual to AbnormalScrap.
- **Variants:** not supported. Production lines post with `VariantId = null`.
- **Batches and serials:** batches and serials on component issues and FG receipts are passed through to inventory.
- **Release reservations:** release reserves only the stock available at that moment; it does not fail when stock is short.
- **Capacity:** MRP assumes infinite capacity. There is no capacity planning or operation scheduling; work-center capacity hours are informational.
- **Alternatives:** alternative components on BOM lines are stored, but MRP and issues do not substitute them automatically.
- **Sources:** sales-order demand and purchase-order supply are connected (see above). Planned purchase suggestions still cannot be converted into purchase orders automatically.
- **Concurrency:** posting actions run inside the idempotency transaction, and orders carry a row version. No range locks are taken on rows that may not exist.
