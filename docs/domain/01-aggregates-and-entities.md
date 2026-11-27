# 5. Domain Entities and Aggregate Roots

Conventions: **bold** = aggregate root; *italic* = entity inside the aggregate; `code` = value object. Aggregates reference other aggregates **by ID only**. One transaction normally modifies one aggregate per module; the posting coordinator is the documented exception (see [07-transaction-consistency.md](../architecture/07-transaction-consistency.md)).

## 5.1 Shared kernel value objects

| Value object | Fields | Rules |
|---|---|---|
| `Money` | Amount `decimal(19,4)`, CurrencyCode `char(3)` | Arithmetic only between same currency; rounding via `IRoundingPolicy` per currency minor units |
| `Quantity` | Value `decimal(18,6)`, UomId | Conversions via `UomConverter` only |
| `BaseQuantity` | Value `decimal(18,6)` in product base UOM | Ledger always stores both |
| `UnitCost` | `decimal(19,6)` in base currency per base UOM | |
| `ExchangeRate` | From, To, Rate `decimal(18,8)`, EffectiveDate | Rate = units of base per 1 unit of foreign |
| `DocumentReference` | ModuleCode, DocumentType, DocumentId, LineId?, DocumentNumber | Used as source reference on ledgers; unique per posting |
| `Period` | FiscalYearId, PeriodNo | |
| `Address`, `ContactInfo`, `LocalizedText` (Ar, En) | | |
| `TenantId`, `CompanyId`, `LegalEntityId`, `BranchId`, `WarehouseId` | strongly typed IDs (Guid) | |

Every tenant-scoped entity implements `ITenantScoped { TenantId }`, most implement `ICompanyScoped { CompanyId }`. All aggregates have `RowVersion` (rowversion) and audit fields (`CreatedAtUtc`, `CreatedBy`, `ModifiedAtUtc`, `ModifiedBy`).

## 5.2 Organization & platform

| Aggregate root | Contained entities / VOs | Key invariants |
|---|---|---|
| **Tenant** | `TenantSettings`, `DataIsolationMode` (Shared/Dedicated), `Status` | Code unique globally |
| **Company** | *ModuleActivation*s, `BaseBusinessPreset` | Belongs to 1 tenant; ≥1 legal entity |
| **LegalEntity** | `TaxRegistration`s, FunctionalCurrency, FiscalCalendarId, `Address` | Functional currency immutable once journals exist |
| **Branch** | `Address`, DefaultWarehouseId | Belongs to 1 legal entity |
| **Department** | ParentId | Hierarchy, no cycles |
| **Warehouse** | Type (Physical/Transit/Virtual/Production), BranchId | Transit warehouse auto-created per legal entity |
| **Employee** | `ContactInfo`, BranchId, DepartmentId | |
| **Salesperson** | EmployeeId?, CommissionPlanId | |
| **CompanyPolicySet** | typed policy sections (InventoryPolicy, SalesPolicy, ProcurementPolicy, AccountingPolicy, PosPolicy…) | Versioned; every change audited |
| **NumberSequence** | Scope (Company, LegalEntity?, Branch?, DocType, FiscalYear?), Prefix, Pattern, NextValue | Allocation under `UPDLOCK`; gapless for posted numbers |
| **User** (Identity) | *UserCompanyAccess* (CompanyId, BranchIds, WarehouseIds), *UserRole*s | Belongs to exactly one tenant (platform operators separate) |
| **Role** | *RolePermission*s | Permission codes from module manifests |
| **RefreshTokenFamily** | *RefreshToken*s (hash, expires, replacedBy, revokedAt) | Reuse of a rotated token revokes the whole family |

## 5.3 Catalog & pricing

| Aggregate root | Contained | Key invariants |
|---|---|---|
| **Product** | *ProductVariant*s (attribute value combination, own SKU/barcodes), *ProductUnit*s (UomId, conversion factor to base, isSales/isPurchase default), *ProductBarcode*s, *ProductImage*s, `TrackingMode` (None/Batch/Serial) + ExpiryTracked, ProductType, CategoryId, BrandId, TaxCategory, CostingMethod override, ReorderPolicy | SKU unique per company; barcode unique per company; base UOM immutable after first movement; type change restricted after movements |
| **ProductCategory** | ParentId, default accounts posting group, default costing method | Hierarchy |
| **ProductAttribute** | *AttributeValue*s | |
| **Brand**, **Manufacturer** | | |
| **UnitOfMeasure** | UomClass (Count, Mass, Volume, Length), IsBase-of-class, ratio to class base | Standard conversions inside a class (kg↔g) |
| **PriceList** | *PriceListItem*s (ProductId, VariantId?, UomId, MinQty, Price, ValidFrom/To), Currency, TaxInclusive, Priority | No overlapping validity for same product/uom/minQty |
| **DiscountRule** | Scope (customer, group, product, category), type (%/amount/volume tier/promo), period, stacking order | |

**UOM conversion rule.** Product-specific conversions (1 carton = 12 boxes, 1 box = 24 pieces) are stored as a factor *to base* per product unit (carton = 288, box = 24, piece = 1). Conversion: `baseQty = qty × factor`; reverse conversion rounds to the product's UOM precision with `MidpointRounding.AwayFromZero`. Conversion between two non-base units always goes through base, which makes it deterministic and associative.

## 5.4 Parties (schema `crm`)

| Aggregate root | Contained | Key invariants |
|---|---|---|
| **Customer** | Kind (Individual/Business), *CustomerAddress*es, *CustomerContact*s, `CreditProfile` (limit, check action, on-hold), PaymentTermsId, PriceListId, CustomerGroupId, CurrencyCode, TaxRegistrationNo, ReceivableAccountGroup | Code unique per company; walk-in customer is a flagged system customer |
| **CustomerGroup** | defaults (price list, terms, accounts) | |
| **Supplier** | *SupplierContact*s, *SupplierAddress*es, PaymentTermsId, Currency, PayableAccountGroup | |
| **PaymentTerms** | *Installment*s (% , days, basis) , early payment discount | Σ% = 100 |
| **SupplierPriceHistory** | (read model fed by `PurchaseInvoicePosted`) | |

## 5.5 Tax & currency

| Aggregate root | Contained | Key invariants |
|---|---|---|
| **TaxCode** | *TaxRate*s (rate, ValidFrom/To), Kind (Output/Input/Withholding), Recoverable, ReportingBox, Jurisdiction | Non-overlapping validity |
| **TaxGroup** | ordered *TaxComponent*s (TaxCodeId, sequence, compounds-on-previous) | |
| **TaxExemption** | party/product, certificate, validity | |
| **Currency** | ISO code, minor units, cash rounding increment | |
| **ExchangeRateTable** | *ExchangeRate*s (date, rate, type: spot/average/closing) | One rate per pair/type/date |

## 5.6 Sales

| Aggregate root | Contained | Key invariants |
|---|---|---|
| **SalesQuotation** | *QuotationLine*s, ValidUntil, Status | Sent quotes are read-only; accepted → convertible once |
| **SalesOrder** | *SalesOrderLine*s (ordered, reserved, delivered, invoiced qty), Status | delivered ≤ ordered (+ over-delivery tolerance policy); cannot close with open reservations |
| **DeliveryNote** | *DeliveryLine*s (SO line ref, qty, batch/serial allocations), Status | Posting issues stock (delivery-driven policy) |
| **SalesInvoice** | *SalesInvoiceLine*s, *InvoiceTaxLine*s, `TotalsSnapshot`, PaymentTermsId, DueSchedule, Status, ReversalOf? | Totals computed server-side only; line tax via `ITaxCalculator`; posted → immutable |
| **SalesReturn** (credit note) | *ReturnLine*s (original invoice line ref, qty, restock?), Reason | returned ≤ invoiced − previously returned |
| **CommissionPlan** / **CommissionEntry** | tiers, basis | |

## 5.7 POS

| Aggregate root | Contained | Key invariants |
|---|---|---|
| **PosTerminal** | WarehouseId, BranchId, PriceListId, receipt template, device key | |
| **CashierShift** | OpeningFloat, *CashMovement*s (pay-in/pay-out), ExpectedByMethod, CountedByMethod, Variance, Status (Open/Closing/Closed) | One open shift per terminal; receipts only in open shift |
| **PosReceipt** | *PosReceiptLine*s, *PosPayment*s (method, amount, reference), ClientReceiptId (idempotency), PriceVersion, Status | Σ payments ≥ total (change computed); unique (TerminalId, ClientReceiptId) |

## 5.8 Procurement

| Aggregate root | Contained | Key invariants |
|---|---|---|
| **PurchaseRequest** | *RequestLine*s | |
| **PurchaseQuotation** (RFQ response) | *QuotationLine*s, SupplierId | Comparison is a query across quotations of an RFQ |
| **PurchaseOrder** | *PurchaseOrderLine*s (ordered, received, invoiced qty), Status | received ≤ ordered + tolerance |
| **GoodsReceipt** | *ReceiptLine*s (PO line ref, qty, accepted/rejected qty, batch/serial, expiry), InspectionResult, Status | Posting increases stock + GRNI accrual |
| **PurchaseInvoice** | *InvoiceLine*s (GRN line ref / PO line ref / expense account), *TaxLine*s, matching status | Three-way match within tolerance or approval |
| **PurchaseReturn** (debit note) | *ReturnLine*s | |
| **LandedCostDocument** | *Charge*s, *Allocation*s to GRN lines, AllocationBasis | Σ allocations = Σ charges |

## 5.9 Inventory

| Aggregate root | Contained | Key invariants |
|---|---|---|
| **InventoryMovement** (ledger row, append-only) | ProductId, VariantId?, WarehouseId, BatchId?, SerialId?, MovementType, Direction, Quantity+UomId, BaseQuantity, UnitCost, TotalCost, ValuationAreaId, TransactionDateUtc, PostingSequence, `DocumentReference`, ReversalOfId? | Never updated/deleted; unique (SourceDocument, SourceLine, MovementType, Sequence) |
| **InventoryBalance** | (ProductId, VariantId, WarehouseId, BatchId) → OnHand, Reserved, RowVersion | OnHand ≥ 0 unless negative-stock policy; Reserved ≤ OnHand when reservation policy strict |
| **ItemValuation** | (ValuationAreaId, ProductId, VariantId) → TotalQty, TotalValue, AverageCost | Updated in same transaction as movement |
| **CostLayer** (FIFO) | ReceiptMovementId, OriginalQty, RemainingQty, UnitCost; *LayerConsumption*s | RemainingQty ≥ 0 |
| **StockReservation** | Demand reference (SO line / production component), qty, warehouse, batch?, expires | |
| **StockTransfer** | *TransferLine*s, FromWarehouse, ToWarehouse, Status | Receive ≤ issued; cost carried from issue |
| **StockAdjustment** | *AdjustmentLine*s, ReasonCode (→ posting role), Status | |
| **StockCount** | *CountLine*s (snapshot qty, counted qty, variance), Scope, Status | Adjustment generated once |
| **OpeningStockDocument** | *Line*s with qty + unit cost | Only before first operational movement for product/warehouse, or with permission |
| **Batch** | ProductId, BatchNo, MfgDate, ExpiryDate, SupplierId | BatchNo unique per product |
| **SerialNumber** | ProductId, SerialNo, CurrentWarehouseId, Status (InStock/Reserved/Issued/Returned/Scrapped), LastMovementId | SerialNo unique per product; serial quantity always 1 |

## 5.10 Manufacturing

| Aggregate root | Contained | Key invariants |
|---|---|---|
| **BillOfMaterials** | *BomVersion*s (EffectiveFrom/To, Status, OutputQty, ExpectedYield%) → *BomLine*s (component, qty per output, UomId, scrap%, issue method, *AlternativeComponent*s) | One active version per effective date; no cycles (multi-level check at activation) |
| **WorkCenter** | capacity, cost rates (machine/hour, labor/hour, overhead/hour) | |
| **Routing** | *RoutingOperation*s (sequence, work center, setup time, run time per unit) | |
| **ProductionOrder** | snapshot of BOM version + routing at release (*RequiredComponent*s, *PlannedOperation*s), planned/completed/scrapped qty, *MaterialIssue* refs, *OperationReport*s, *OutputReceipt* refs, WIP balance, Status | Cannot change BOM after release; close requires WIP = 0 after variance |
| **MrpRun** | parameters, *PlannedSupply* suggestions, *ShortageLine*s | Suggestions are proposals; converting creates PO/production orders |

## 5.11 Accounting

| Aggregate root | Contained | Key invariants |
|---|---|---|
| **Account** | Code, `LocalizedText` name (Ar/En), AccountType (Asset/Liability/Equity/Revenue/Expense), AccountGroup, ParentId, IsControl, ControlSubledger (AR/AP/Inventory/GRNI…), AllowManualPosting, NormalBalance, Currency restriction, Status | Only leaf accounts accept postings; type must match parent; cannot delete when used |
| **FiscalYear** | *FiscalPeriod*s (No, Start, End, Status Open/SoftClosed/Closed) | Contiguous, non-overlapping |
| **JournalEntry** | *JournalLine*s (AccountId, Debit, Credit, TxCurrency, TxAmount, Rate, BaseDebit, BaseCredit, CostCenterId, ProfitCenterId, PartyRef, Description), Number, Date, PeriodId, LegalEntityId, SourceModule, `DocumentReference`, Status (Draft/Posted/Reversed), ReversalOfId, PostedBy/At | ΣBaseDebit = ΣBaseCredit (and per currency Σ tx amounts balance or an FX rounding line is added); ≥2 lines; each line one-sided and > 0; open period; posted immutable; unique (LegalEntity, SourceDocumentReference, JournalPurpose) |
| **PostingRuleSet** | *AccountDetermination*s (PostingRole, context keys: product posting group, party posting group, tax code, warehouse, …, priority → AccountId) | Every required role resolvable before a module can be enabled ("accounting readiness check") |
| **CostCenter**, **ProfitCenter** | hierarchy | |
| **AccountBalance** (derived) | (LegalEntity, Account, Period, CostCenter?, Currency) → Debit, Credit | Maintained in the posting transaction; rebuildable from lines |

## 5.12 Finance

| Aggregate root | Contained | Key invariants |
|---|---|---|
| **OpenItem** (customer/supplier) | Party, source doc ref, original amount (tx & base), open amount, due date, currency | open = original − Σ allocations; never negative |
| **Receipt** / **Payment** | method, cash/bank account, amount, currency, *Allocation*s, unallocated amount, withholding lines | Σ allocations ≤ amount; posted immutable |
| **Allocation** | open item ↔ settlement doc, amount, realized FX difference | |
| **CashAccount**, **BankAccount** | GL account, currency | |
| **BankReconciliation** | statement lines, matched ledger lines, status | |
| **Expense** | *ExpenseLine*s (expense account, tax, cost center), paid by | |
| **InternalTransfer** | from/to cash/bank | |

## 5.13 Fixed assets, workflow, audit

| Aggregate root | Contained | Key invariants |
|---|---|---|
| **Asset** | category, acquisition cost, in-service date, useful life, method (straight-line, declining balance, units), salvage value, location, custodian, *AssetEvent*s (acquire, transfer, revalue, dispose) | Depreciation run idempotent per asset/period |
| **ApprovalDefinition** | document type, *ApprovalRule*s (condition expression on document facts: amount, branch, discount%…), *ApprovalStep*s (approver role/user, quorum, escalation timeout) | |
| **ApprovalInstance** | document ref, snapshot of facts, *ApprovalAction*s (approve/reject/delegate, user, reason, at), status | Document cannot be approved twice; delegation audited |
| **AuditLog** | entity, id, action, before/after JSON, user, tenant, correlationId, hash chain | Insert-only (DB permissions + trigger) |
| **DocumentStatusHistory** | document ref, from, to, user, at, reason | |
