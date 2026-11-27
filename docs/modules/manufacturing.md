# Manufacturing module

Schema `manufacturing`, migration order 130, module code `manufacturing`. The module is optional: every endpoint uses `.RequireCompany()` and `.RequireModule("manufacturing")`. The module catalog lists `inventory` and `procurement` as its dependencies. The `manufacturing` business preset turns it on.

The module covers:

- **Setup:** work centers (rates, daily capacity, working days), routings, and bills of materials with versions, alternatives and variant-specific lines.
- **Production orders:** Draft through Closed, including WIP accounting, at **actual or standard cost**, rework orders and component substitution.
- **Cost accounting:** standard cost versions with roll-up, activation with stock revaluation, variance split at close, and period-end absorption clearing of applied labour, machine and overhead.
- **MRP:** low-level codes, netting, safety stock, lot sizing, lead-time offset, pegging and a shortage list; conversion of production suggestions into production orders and of purchase suggestions into draft purchase orders.
- **Capacity:** work-center load against capacity per day, with a finite schedule that flags late orders and proposes finish dates.

All stock moves go through `IInventoryPostingService`, `IInventoryRevaluationService` and `IReservationService`. All journals go through `IJournalPostingService`. Purchase orders are created through `IPurchaseOrderDraftService` and variants are read through `ICatalogVariantService`. The module never writes another module's tables.

> ⚖️ **Review with an accountant before production use:** the standard-cost conventions below (what is a price versus a usage variance, where revaluation differences go, absorption clearing against expense accounts) are recommended defaults, not jurisdiction-specific rules.

## Contracts (`Erp.Modules.Manufacturing.Contracts`)

```csharp
public static class ManufacturingPermissions
{
    Read = "manufacturing.read"; SetupManage = "manufacturing.setup.manage"; OrderManage = "manufacturing.order.manage";
    OrderPost = "manufacturing.order.post" /* sensitive */; PlanningRun = "manufacturing.planning.run";
    CostingManage = "manufacturing.costing.manage" /* sensitive: activates standards (revalues stock) and posts absorption */;
}

public interface IManufacturingQueries
{
    // Open production supply (remaining output) and open component demand (remaining requirement not yet reserved), per product.
    Task<IReadOnlyList<ProductionSupplyDemand>> GetOpenSupplyDemandAsync(IReadOnlyCollection<Guid> productIds, CancellationToken ct = default);
}

// MRP extension points: register more implementations in DI and the next MRP run picks them up.
public interface IMrpDemandSource { string Name { get; } Task<IReadOnlyList<MrpDemand>> GetDemandAsync(Guid companyId, DateOnly horizonEnd, CancellationToken ct = default); }
public interface IMrpSupplySource { string Name { get; } Task<IReadOnlyList<MrpSupply>> GetSupplyAsync(Guid companyId, DateOnly horizonEnd, CancellationToken ct = default); }
```

Quantities in `MrpDemand` and `MrpSupply` are in the product's **base UOM**.

### Contracts this module uses from other modules (added in wave 6)

| Contract | Owner | Use |
|---|---|---|
| `IInventoryRevaluationService.RevalueAsync` | Inventory | Revalues everything on hand of a product in a legal entity to a new unit cost (value-only `CostAdjustment` movement per valuation area; FIFO layers still open take the new cost; journal Dr Inventory / Cr gain role or Dr loss role / Cr Inventory). Idempotent per (source doc, product). |
| `IPurchaseOrderDraftService.CreateDraftAsync` | Procurement | Sends Procurement's regular create-purchase-order command (Draft, same validation, numbering and tax). |
| `ICatalogVariantService` | Catalog | Reads variants with their attribute values; `GenerateVariantsAsync` creates the missing combinations (also exposed as `POST /api/v1/catalog/products/{id}/variants/generate`). |

### MRP sources

| Source | Registered implementation | Status |
|---|---|---|
| Production orders (component demand and FG supply) | `ProductionOrderMrpSource` (`production-orders`) | Live |
| Sales orders (independent demand) | `SalesOrderMrpSource` (`sales-orders`) | Confirmed, undelivered lines minus the part already reserved in stock |
| Purchase orders (scheduled receipts) | `PurchaseOrderMrpSource` (`purchase-orders`) | Open approved/sent/partially received PO lines at their expected date (today if none) |

## Domain

| Aggregate | Notes |
|---|---|
| `WorkCenter` | Labour, machine and overhead rates per hour (decimal(19,6)), overhead basis `LaborHours`/`MachineHours`, capacity hours per day and **working days** (bit mask, default all seven days). |
| `Routing` | A product plus operations (sequence, work center, setup hours, run hours per unit), JSON column. |
| `BillOfMaterials` / `BomVersion` | Version number, effective range, output quantity, expected yield %, lines (component, quantity per output, UOM, scrap %, issue method, **alternatives in priority order**, optional **component variant**, optional **variant condition** `appliesTo` such as `{"Size":"L"}`). Draft → Active → Superseded / Obsolete. |
| `ProductionOrder` | Number `MO-…`, **type** `Standard`/`Rework`, optional output **variant**, warehouses, component and operation snapshots, transactions, substitutions, WIP balance, **costing method** with the standards it was planned with, and the variance split at close. |
| `ItemPlanningParameters` | Safety stock, lead time, lot rule and size, procurement type, **costing method override** and **preferred supplier**. |
| `ManufacturingSettings` | Company default costing method (`Actual` until set). |
| `CostVersion` | Code, name, effective date, Draft → Approved → Active → Superseded; standard cost items per product (material, labour, machine, overhead per base unit; source `Manual`, `RollUp` or `CurrentCost`). |
| `CostPool` | Kind (`Labor`, `Machine`, `Overhead`), work centers, actual expense accounts (each account in at most one pool). |
| `AbsorptionRun` | Legal entity, period, applied/actual/variance per pool, journal. One per legal entity and period. |
| `MrpRun` | Run date, horizon, suggestions (with production order or purchase order once converted), shortages. |

### BOM rules

- **Explosion:** required quantity per unit = `quantityPerOutput / outputQuantity × (1 + scrap%) ÷ (yield% / 100)`, rounded to 6 dp, in the line's UOM. Lines with a variant condition are included only when the order's variant has all the listed attribute values (case-insensitive); orders without a variant include only unconditional lines.
- **Activation checks:** multi-level cycles (`manufacturing.bom-cycle`) and overlapping date ranges (`manufacturing.bom-version-overlap`), except that an earlier open-ended version is closed at `from − 1` and superseded.
- **Effective version:** a usable version whose range contains the date; the latest start wins.

## Production order lifecycle and accounting

`Draft → Planned → Released → InProgress → PartiallyCompleted → Completed → Closed`, or `Cancelled` from Draft, Planned or Released when no stock has moved.

| Step | Effect | Journal |
|---|---|---|
| **Plan** | Snapshots the effective BOM version (filtered by the order's variant) and the active routing. If the product is standard-costed, also snapshots the active cost version's standards for the product and its components (`manufacturing.no-standard-cost` when missing). | — |
| **Release** | **Substitutes** short components with their alternatives (see below), then reserves each component line, capped at available stock. | — |
| **Issue / Return** | Inventory `ProductionIssue` / `ProductionReturn` (with the component variant). | Dr WIP / Cr Inventory, or the reverse |
| **Report operation** | Hours × rates; the transaction records the work center and the labour/machine/overhead split (used by absorption clearing). | Dr WIP / Cr AppliedLabor, AppliedMachine, AppliedOverhead |
| **Receive FG** | Backflush, then receipt (with the output variant). Unit cost: **actual** = WIP ÷ max(remaining, received); **standard** = the order's standard unit cost. | Dr Inventory / Cr WIP |
| **Scrap** | Normal scrap quantity; cost stays in WIP. | — |
| **Close** | Backflush owed components, release reservations, post the residual WIP. **Actual:** residual to ProductionVariance (or AbnormalScrap). **Standard:** residual split into variances (below), unless `abnormalScrap: true`. | see below |

Invariants: `Σ transactions.wipAmount = WipBalance`; WIP is 0 after close; the WIP account accepts only `manufacturing` postings.

## Standard costing

**Method:** each product uses its item override (`PUT /planning-parameters` with `costingMethod`), else the company default (`PUT /settings`). Rework orders always use actual cost.

**Cost versions:** `POST /cost-versions` (Draft) → `PUT /cost-versions/{id}/items` (manual standards, typically for purchased materials) → `POST /cost-versions/{id}/roll-up` → `POST …/approve` (or `…/reopen`) → `POST …/activate`.

**Roll-up** (bottom-up, recursive, cycle-safe), per base unit:

- **Manufactured product** (effective BOM on the version's date): costing lot = the item's lot size, else the BOM output quantity.
  - material = Σ exploded component base quantity for the lot × component standard ÷ lot;
  - labour / machine / overhead = Σ routing hours for the lot (setup + run × lot) × the work center's labour / machine / overhead rate ÷ lot. An operation is assumed to occupy labour and machine for its planned hours.
- **Purchased component:** the manual standard if entered, else the current average (else last) inventory cost in `costWarehouseId` (`CurrentCost`, with a note when no cost is found).
- Manual items are kept unless `overwriteManual: true`. Variant-conditional BOM lines are not included: standards are per product, not per variant.

**Activation** (`manufacturing.costing.manage`, step-up and `Idempotency-Key` required): supersedes the active version and **revalues the stock on hand** of every standard-costed product in the given legal entity to its new standard: Dr Inventory / Cr InventoryAdjustmentGain for an increase, Dr InventoryAdjustmentLoss / Cr Inventory for a decrease. The version cannot be activated before its effective date. Orders already planned keep the standards they were planned with.

**Variance split at close** (positive = unfavourable, debit):

| Variance | Formula | Role |
|---|---|---|
| Material price | Σ over components with a standard: actual issued cost − actual base quantity × component standard | `PurchasePriceVariance` |
| Material usage | Σ (actual base quantity − standard base quantity for the good output) × component standard (components without a standard: their actual average issue cost) | `MaterialUsageVariance` |
| Conversion | actual applied labour + machine + overhead − standard conversion × good output | `ConversionCostVariance` |
| Other | residual − the three above (product material standard differing from its components' standards, rounding) | `ProductionVariance` |

Credit WIP with the residual; the four lines sum to it exactly. Price variance arises only for components whose stock is not itself kept at standard (e.g. purchased items left on actual costing), because standard-costed stock is revalued to standard on activation.

Worked example (integration test): standards X 4.8, Y 2, Z 4.5, FG 20.1 material + 3.3 labour + 5.5 machine + 2.75 overhead = 31.65. 100 planned, 5 scrapped, 95 good. Actual: X 200 @5, Y 300 @2, Z 100 @4.5, conversion 1,110 = 3,160. Received 95 × 31.65 = 3,006.75; residual 153.25 = price 40 + usage 100.5 + conversion 12.75 + other 0.

## Absorption clearing

`POST /cost-pools` defines pools: kind, work centers and actual expense accounts (active, non-control expense posting accounts, each in one pool only). For a legal entity and period, `GET /absorption-runs/preview` and `POST /absorption-runs` compute per pool:

- **applied** = labour / machine / overhead recorded on operation reports of the pool's work centers dated in the period;
- **actual** = net debits on the pool's expense accounts in the period (excluding earlier clearing journals);
- **variance** = actual − applied (positive = under-absorbed).

Posting (`manufacturing.costing.manage`, `Idempotency-Key`) journals, dated at the period end: Dr the applied account (applied), Cr each expense account (its actual), Dr/Cr `AbsorptionVariance` (the difference). The applied and expense accounts are cleared for the period. One run per legal entity and period; overlapping periods are refused (`manufacturing.absorption-already-posted`).

## Component substitution

BOM line alternatives are substituted **1 : 1 in base units**:

- **Automatically at release:** for each component whose remaining requirement exceeds the available stock in the component warehouse, the shortfall moves to alternatives with available stock, in priority order.
- **Manually:** `POST /production-orders/{id}/substitute` (`lineId`, `alternativeProductId`, `baseQuantity` ≤ remaining). On a released order the original's reservation is reduced and the alternative reserved as far as stock allows.

The substitute gets its own component line (`substituteForLineId`) carrying the moved share of the standard quantity per unit, so standard usage and variances remain correct; each substitution is recorded on the order (`substitutions`, `automatic` flag).

## Rework orders

`POST /rework-orders`: product (and variant), quantity, component warehouse (e.g. a quarantine store), optional output warehouse, routing, source order and extra components. The order is created **Planned** with the defective product itself as the main component (1 : 1) plus the extras, and the routing's operations. It is then released, issued, reported and received like any order, always at actual cost.

## MRP

`RunMrpCommand` (run date, horizon, default 56 days) collects demand and supply from the registered sources, builds the BOM graph and low-level codes, nets each product in low-level order against available stock (on hand − reserved), plans lot-sized orders whenever projected stock drops below safety stock (due on the requirement date, ordered lead-time days earlier, flagged late when before the run date), and explodes planned production into dependent demand. MRP itself plans at **infinite capacity**; use the capacity load to check it.

- **`POST /mrp-runs/{id}/convert`:** open *Produce* suggestions → Draft production orders (source reference `MRP <number>`).
- **`POST /mrp-runs/{id}/convert-purchases`:** open *Purchase* suggestions → **Draft purchase orders**, one per supplier: the item's preferred supplier, else `defaultSupplierId` (`manufacturing.supplier-required` when neither). Lines are in the base unit at the last (else average) cost in the warehouse, expected on the suggestion's due date. The suggestion records the purchase order id.

## Capacity

`GET /capacity-load?from&to&mrpRunId` (default 28 days from today):

- **Load:** remaining hours of each operation of open orders (planned − max(reported labour, machine hours)), plus — with `mrpRunId` — routing hours of the run's open Produce suggestions, spread evenly over the work center's working days between start and due date, compared with capacity per day (`overloaded`).
- **Finite schedule:** orders in due-date order take the earliest free capacity from their start date; `proposedFinish` is when their last hours fit and `late` is set when that is after the due date (`unschedulable` when a work center has no capacity).

The load is a check and a proposal: dates on orders are not changed automatically.

## Endpoints (`/api/v1/manufacturing`)

| Method & path | Permission |
|---|---|
| `GET /work-centers`, `/routings`, `/boms`, `/boms/{id}`, `/planning-parameters`, `/production-orders`, `/production-orders/{id}`, `/mrp-runs`, `/mrp-runs/{id}`, `/capacity-load`, `/settings`, `/cost-versions`, `/cost-versions/{id}`, `/cost-pools`, `/absorption-runs`, `/absorption-runs/preview` | `manufacturing.read` |
| `POST/PUT /work-centers`, `/routings`, `POST /boms`, BOM versions (`POST`, `PUT`, `…/activate`, `…/obsolete`), `PUT /planning-parameters` | `manufacturing.setup.manage` |
| `POST /production-orders`, `…/plan`, `…/release`, `…/cancel`, `…/substitute`, `POST /rework-orders` | `manufacturing.order.manage` |
| `POST /production-orders/{id}/issues`, `/returns`, `/operations`, `/receipts`, `/scrap`, `/close` | `manufacturing.order.post` + `Idempotency-Key` |
| `POST /mrp-runs`, `…/convert`, `…/convert-purchases` | `manufacturing.planning.run` |
| `PUT /settings`, `POST /cost-versions`, `PUT …/items`, `POST …/roll-up`, `…/approve`, `…/reopen`, `POST/PUT /cost-pools` | `manufacturing.costing.manage` |
| `POST /cost-versions/{id}/activate` | `manufacturing.costing.manage` + step-up + `Idempotency-Key` |
| `POST /absorption-runs` | `manufacturing.costing.manage` + `Idempotency-Key` |

## Decisions and limitations

- **Standards are per product, not per variant;** variant-conditional BOM lines are excluded from the roll-up. Standard costs per variant would need a variant key on cost items.
- **Revaluation** on activation covers one legal entity per call and posts to InventoryAdjustmentGain/Loss (no separate revaluation-variance role). Stock received *after* activation by other modules (purchases) still enters at its actual cost under the inventory costing method; the difference to standard shows as material price variance when issued to production. Inventory itself remains on weighted average/FIFO — "standard" applies to production receipts and revaluation, not to purchase receipts.
- **Price variance** is posted to `PurchasePriceVariance` (shared with procurement), not a separate material-price role.
- **Absorption** uses operation reports recorded from this version on (earlier transactions lack the work-center cost split). Expense accounts are cleared in full for the period; spending accrued in another period is not apportioned.
- **Substitution** is 1 : 1 in base units; there is no conversion ratio between a component and its alternative, and MRP does not substitute (it plans the original component).
- **Capacity** is a report with proposals: it does not reschedule orders, ignores operation sequencing within an order (operations are loaded independently from the order start) and treats setup as part of the remaining hours.
- **Rework** is actual-cost only and does not link the defective units to a quality-inspection record (no QC module).
- **Receipt costing (actual):** `WIP ÷ remaining quantity`; rounding residuals go to ProductionVariance at close.
- **Backflush:** proportional to reported output (completed + scrapped).
- **Release reservations** never fail on shortage; the unreserved rest is left to MRP.
- **Concurrency:** posting actions run inside the idempotency transaction, and orders carry a row version. No range locks on rows that may not exist (ADR-029).
