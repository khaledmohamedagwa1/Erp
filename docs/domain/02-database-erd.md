# 6. Database ERD

The ERD is split by schema (bounded context). Cross-schema relationships are **logical references by ID** — no foreign keys are created across module schemas (so a module can be moved to its own database later) except to `organization` tenancy tables, which every module may reference (ADR-005). Integrity across schemas is protected by application rules and reconciliation jobs.

Column types in diagrams are abbreviated: `money` = `decimal(19,4)`, `qty` = `decimal(18,6)`, `cost` = `decimal(19,6)`, `rate` = `decimal(18,8)`, `id` = `uniqueidentifier`, `rv` = `rowversion`, `dt` = `datetime2(3)` UTC.

## 6.1 Global database rules

| Rule | Implementation |
|---|---|
| Primary keys | `uniqueidentifier` generated in the application with a **SQL-Server-ordered sequential GUID** generator (EF Core `SequentialGuidValueGenerator` semantics). Note: plain UUIDv7 is *not* sequential under SQL Server's GUID sort order, so it is not used for clustered keys (ADR-008). |
| Tenant column | `TenantId id NOT NULL` on every tenant-scoped table, first column of every business index and unique constraint |
| Company column | `CompanyId` on company-scoped tables |
| Unique constraints | Tenant/company-scoped: e.g. `UX_catalog_Product_TenantId_CompanyId_Sku` |
| Concurrency | `RowVersion rowversion` on every aggregate root table |
| Money/qty | Explicit precision everywhere (EF convention + analyzer test fails on any unconfigured decimal) |
| Time | `datetime2(3)` UTC (`…Utc` suffix); business dates (`DocumentDate`, `PostingDate`) as `date` |
| Deletes | `ON DELETE NO ACTION` everywhere; soft delete (`IsDeleted`) only for master data; posted documents and ledgers never deleted |
| Naming | Tables singular PascalCase; FKs `FK_<schema>_<Table>_<RefTable>_<Column>`; indexes `IX_`/`UX_` |
| Ledger protection | `inventory.InventoryMovement`, `accounting.JournalLine` (posted), `audit.AuditLog`: application DB user has no UPDATE/DELETE grant; INSTEAD OF UPDATE/DELETE triggers as second line |
| Row-level security | SQL Server RLS security policy on `TenantId` using `SESSION_CONTEXT('TenantId')` (defense in depth, ADR-004) |

## 6.2 Organization, identity, configuration

```mermaid
erDiagram
    Tenant ||--o{ Company : owns
    Company ||--|{ LegalEntity : has
    LegalEntity ||--|{ Branch : has
    Branch ||--o{ Warehouse : has
    Company ||--o{ Department : has
    Company ||--o{ Employee : employs
    Employee |o--o| Salesperson : "may be"
    Company ||--o{ ModuleActivation : enables
    Company ||--|| CompanyPolicySet : configures
    Tenant ||--o{ User : has
    User ||--o{ UserCompanyAccess : "access to"
    UserCompanyAccess }o--|| Company : grants
    User ||--o{ UserRole : has
    Role ||--o{ RolePermission : has
    User ||--o{ RefreshTokenFamily : has
    Company ||--o{ NumberSequence : numbers

    Tenant {
        id Id PK
        string Code UK
        string Name
        string IsolationMode "Shared|Dedicated"
        string Status
        rv RowVersion
    }
    Company {
        id Id PK
        id TenantId FK
        string Code
        string NameAr
        string NameEn
        string BusinessPreset
    }
    LegalEntity {
        id Id PK
        id TenantId
        id CompanyId FK
        string Name
        string TaxRegistrationNo
        char3 FunctionalCurrency
        id FiscalCalendarId
    }
    Branch {
        id Id PK
        id TenantId
        id LegalEntityId FK
        string Code
        id DefaultWarehouseId
    }
    Warehouse {
        id Id PK
        id TenantId
        id BranchId FK
        string Code
        string Type "Physical|Transit|Production|Virtual"
    }
    ModuleActivation {
        id CompanyId PK
        string ModuleCode PK
        bool IsEnabled
        dt EnabledAtUtc
    }
    CompanyPolicySet {
        id CompanyId PK
        int Version
        json Policies
        rv RowVersion
    }
    User {
        id Id PK
        id TenantId
        string UserName
        string Email
        bool MfaEnabled
    }
    UserCompanyAccess {
        id UserId PK
        id CompanyId PK
        json BranchIds
        json WarehouseIds
    }
    NumberSequence {
        id Id PK
        id TenantId
        id CompanyId
        id LegalEntityId
        id BranchId
        string DocumentType
        int FiscalYear
        string Pattern
        bigint NextValue
        rv RowVersion
    }
```

## 6.3 Catalog, parties, tax

```mermaid
erDiagram
    Product ||--o{ ProductVariant : has
    Product ||--|{ ProductUnit : "sold/stocked in"
    ProductUnit }o--|| UnitOfMeasure : uses
    Product ||--o{ ProductBarcode : has
    Product }o--|| ProductCategory : in
    Product }o--o| Brand : of
    ProductCategory |o--o{ ProductCategory : parent
    PriceList ||--o{ PriceListItem : contains
    PriceListItem }o--|| Product : prices
    Customer }o--o| CustomerGroup : in
    Customer ||--o{ CustomerAddress : has
    Customer ||--o{ CustomerContact : has
    Customer }o--o| PriceList : uses
    Customer }o--|| PaymentTerms : uses
    Supplier }o--|| PaymentTerms : uses
    TaxGroup ||--|{ TaxComponent : has
    TaxComponent }o--|| TaxCode : uses
    TaxCode ||--|{ TaxRate : "rates over time"
    Currency ||--o{ ExchangeRate : quoted

    Product {
        id Id PK
        id TenantId
        id CompanyId
        string Sku "UX per company"
        string NameAr
        string NameEn
        string ProductType
        id BaseUomId
        string TrackingMode "None|Batch|Serial"
        bool ExpiryTracked
        id CategoryId FK
        string TaxCategory
        string CostingMethod "null = category default"
        rv RowVersion
    }
    ProductVariant {
        id Id PK
        id ProductId FK
        string Sku
        json AttributeValues
    }
    ProductUnit {
        id ProductId PK
        id UomId PK
        qty FactorToBase
        bool IsDefaultSales
        bool IsDefaultPurchase
    }
    ProductBarcode {
        id Id PK
        id TenantId
        id CompanyId
        string Barcode "UX per company"
        id ProductId
        id VariantId
        id UomId
    }
    PriceListItem {
        id Id PK
        id PriceListId FK
        id ProductId
        id VariantId
        id UomId
        qty MinQty
        money Price
        date ValidFrom
        date ValidTo
    }
    Customer {
        id Id PK
        id TenantId
        id CompanyId
        string Code
        string Kind
        money CreditLimit
        string CreditAction
        char3 Currency
        string PostingGroup
    }
    TaxCode {
        id Id PK
        id TenantId
        string Code
        string Kind "Output|Input|Withholding"
        bool Recoverable
        string Jurisdiction
    }
    TaxRate {
        id Id PK
        id TaxCodeId FK
        decimal Rate
        date ValidFrom
        date ValidTo
    }
    ExchangeRate {
        id Id PK
        id TenantId
        char3 FromCurrency
        char3 ToCurrency
        string RateType
        date EffectiveDate
        rate Rate
    }
```

## 6.4 Sales & POS

```mermaid
erDiagram
    SalesQuotation ||--|{ SalesQuotationLine : has
    SalesOrder ||--|{ SalesOrderLine : has
    SalesOrder ||--o{ DeliveryNote : "fulfilled by"
    DeliveryNote ||--|{ DeliveryLine : has
    SalesInvoice ||--|{ SalesInvoiceLine : has
    SalesInvoice ||--o{ SalesInvoiceTax : has
    SalesInvoice ||--o{ SalesReturn : "returned by"
    SalesReturn ||--|{ SalesReturnLine : has
    PosTerminal ||--o{ CashierShift : runs
    CashierShift ||--o{ PosReceipt : contains
    PosReceipt ||--|{ PosReceiptLine : has
    PosReceipt ||--|{ PosPayment : "paid by"
    CashierShift ||--o{ CashMovement : has

    SalesInvoice {
        id Id PK
        id TenantId
        id CompanyId
        id LegalEntityId
        id BranchId
        string Number "UX, assigned at posting"
        date DocumentDate
        date PostingDate
        id CustomerId
        char3 Currency
        rate ExchangeRate
        money NetTotal
        money TaxTotal
        money GrandTotal
        string Status
        id SalesOrderId
        id ReversalOfId
        id JournalEntryId
        rv RowVersion
    }
    SalesInvoiceLine {
        id Id PK
        id SalesInvoiceId FK
        int LineNo
        id ProductId
        id VariantId
        id UomId
        qty Quantity
        qty BaseQuantity
        money UnitPrice
        money DiscountAmount
        money NetAmount
        id TaxGroupId
        money TaxAmount
        cost UnitCost "from inventory posting"
        id WarehouseId
        id SalesOrderLineId
        id DeliveryLineId
    }
    PosReceipt {
        id Id PK
        id TenantId
        id TerminalId
        id ShiftId
        id ClientReceiptId "UX with TerminalId"
        string Number
        int PriceVersion
        money GrandTotal
        string Status
    }
    PosPayment {
        id Id PK
        id ReceiptId FK
        string Method
        money Amount
        string Reference
    }
    CashierShift {
        id Id PK
        id TerminalId
        id CashierUserId
        money OpeningFloat
        money ExpectedCash
        money CountedCash
        money Variance
        string Status
    }
```

## 6.5 Procurement

```mermaid
erDiagram
    PurchaseRequest ||--|{ PurchaseRequestLine : has
    PurchaseOrder ||--|{ PurchaseOrderLine : has
    PurchaseOrder ||--o{ GoodsReceipt : "received by"
    GoodsReceipt ||--|{ GoodsReceiptLine : has
    PurchaseInvoice ||--|{ PurchaseInvoiceLine : has
    PurchaseInvoiceLine }o--o| GoodsReceiptLine : matches
    PurchaseInvoiceLine }o--o| PurchaseOrderLine : matches
    PurchaseReturn ||--|{ PurchaseReturnLine : has
    LandedCostDocument ||--|{ LandedCostCharge : has
    LandedCostDocument ||--|{ LandedCostAllocation : allocates
    LandedCostAllocation }o--|| GoodsReceiptLine : "allocated to"

    PurchaseOrderLine {
        id Id PK
        id PurchaseOrderId FK
        id ProductId
        id UomId
        qty OrderedQty
        qty ReceivedQty
        qty InvoicedQty
        money UnitPrice
        id TaxGroupId
    }
    GoodsReceiptLine {
        id Id PK
        id GoodsReceiptId FK
        id PurchaseOrderLineId
        id ProductId
        id WarehouseId
        qty AcceptedQty
        qty RejectedQty
        string BatchNo
        date ExpiryDate
        cost UnitCost
        qty InvoicedQty
    }
    PurchaseInvoiceLine {
        id Id PK
        id PurchaseInvoiceId FK
        id GoodsReceiptLineId
        id PurchaseOrderLineId
        id ExpenseAccountRole
        qty Quantity
        money UnitPrice
        money TaxAmount
        money PriceVarianceBase
    }
```

## 6.6 Inventory

```mermaid
erDiagram
    InventoryMovement }o--|| InventoryBalance : "aggregated into"
    InventoryMovement }o--o| Batch : for
    InventoryMovement }o--o| SerialNumber : for
    CostLayer ||--o{ CostLayerConsumption : "consumed by"
    CostLayerConsumption }o--|| InventoryMovement : "issue movement"
    CostLayer }o--|| InventoryMovement : "receipt movement"
    ItemValuation ||--o{ InventoryMovement : values
    StockTransfer ||--|{ StockTransferLine : has
    StockAdjustment ||--|{ StockAdjustmentLine : has
    StockCount ||--|{ StockCountLine : has
    StockReservation }o--|| InventoryBalance : reserves

    InventoryMovement {
        id Id PK
        id TenantId
        id CompanyId
        id LegalEntityId
        id ValuationAreaId
        bigint PostingSequence "per valuation area"
        id ProductId
        id VariantId
        id WarehouseId
        id BatchId
        id SerialId
        string MovementType
        smallint Direction "+1 or -1"
        qty Quantity
        id UomId
        qty BaseQuantity
        cost UnitCost
        money TotalCost
        date TransactionDate
        string SourceModule
        string SourceDocType
        id SourceDocId
        id SourceLineId
        string ReferenceNo
        id ReversalOfId
        id CreatedBy
        dt CreatedAtUtc
    }
    InventoryBalance {
        id TenantId PK
        id ProductId PK
        id VariantId PK
        id WarehouseId PK
        id BatchId PK
        qty OnHand
        qty Reserved
        rv RowVersion
    }
    ItemValuation {
        id TenantId PK
        id ValuationAreaId PK
        id ProductId PK
        id VariantId PK
        qty TotalQty
        money TotalValue
        cost AverageCost
        rv RowVersion
    }
    CostLayer {
        id Id PK
        id ValuationAreaId
        id ProductId
        id ReceiptMovementId
        dt ReceivedAt
        qty OriginalQty
        qty RemainingQty
        cost UnitCost
    }
    Batch {
        id Id PK
        id ProductId
        string BatchNo
        date ManufactureDate
        date ExpiryDate
        id SupplierId
    }
    SerialNumber {
        id Id PK
        id ProductId
        string SerialNo
        id CurrentWarehouseId
        string Status
        id LastMovementId
    }
```

Unique index enforcing single posting per source line:
`UX_inventory_InventoryMovement_Source (TenantId, SourceDocType, SourceDocId, SourceLineId, MovementType, Direction, BatchId, SerialId)` — reversal movements carry their own source (the reversal document).

## 6.7 Manufacturing

```mermaid
erDiagram
    BillOfMaterials ||--|{ BomVersion : versions
    BomVersion ||--|{ BomLine : components
    BomLine ||--o{ BomAlternative : alternatives
    Routing ||--|{ RoutingOperation : ops
    RoutingOperation }o--|| WorkCenter : at
    ProductionOrder ||--|{ ProductionComponent : requires
    ProductionOrder ||--o{ ProductionOperation : plans
    ProductionOrder ||--o{ OperationReport : "actuals"
    ProductionOrder ||--o{ MaterialIssue : consumes
    ProductionOrder ||--o{ ProductionOutput : produces
    MrpRun ||--o{ PlannedSupply : suggests

    BomVersion {
        id Id PK
        id BomId FK
        int VersionNo
        date EffectiveFrom
        date EffectiveTo
        qty OutputQty
        decimal ExpectedYieldPct
        string Status
    }
    BomLine {
        id Id PK
        id BomVersionId FK
        id ComponentProductId
        qty QtyPerOutput
        id UomId
        decimal ScrapPct
        string IssueMethod "Manual|Backflush"
    }
    ProductionOrder {
        id Id PK
        id TenantId
        id CompanyId
        string Number
        id ProductId
        id BomVersionId
        id RoutingId
        qty PlannedQty
        qty CompletedQty
        qty ScrappedQty
        id WipWarehouseId
        money WipBalance
        string Status
        rv RowVersion
    }
    WorkCenter {
        id Id PK
        string Code
        cost MachineRatePerHour
        cost LaborRatePerHour
        cost OverheadRatePerHour
    }
```

## 6.8 Accounting & finance

```mermaid
erDiagram
    Account |o--o{ Account : parent
    FiscalYear ||--|{ FiscalPeriod : has
    JournalEntry ||--|{ JournalLine : has
    JournalLine }o--|| Account : posts
    JournalLine }o--o| CostCenter : "cost center"
    JournalLine }o--o| ProfitCenter : "profit center"
    JournalEntry }o--|| FiscalPeriod : in
    JournalEntry |o--o| JournalEntry : reverses
    PostingRuleSet ||--|{ AccountDetermination : maps
    AccountDetermination }o--|| Account : "resolves to"
    AccountBalance }o--|| Account : summarizes
    OpenItem ||--o{ Allocation : "settled by"
    Receipt ||--o{ Allocation : allocates
    Payment ||--o{ Allocation : allocates
    BankAccount ||--o{ BankReconciliation : reconciles

    Account {
        id Id PK
        id TenantId
        id LegalEntityId "null = shared COA in company"
        string Code
        string NameAr
        string NameEn
        string AccountType
        string NormalBalance
        id ParentId
        bool IsPostingAccount
        bool IsControl
        string ControlSubledger
        bool AllowManualPosting
        string Status
    }
    JournalEntry {
        id Id PK
        id TenantId
        id LegalEntityId
        string Number "gapless per LE and FY"
        date JournalDate
        id FiscalPeriodId
        string SourceModule
        string SourceDocType
        id SourceDocId
        string Purpose
        char3 Currency
        rate ExchangeRate
        string Status
        id ReversalOfId
        id PostedBy
        dt PostedAtUtc
        rv RowVersion
    }
    JournalLine {
        id Id PK
        id JournalEntryId FK
        int LineNo
        id AccountId
        money Debit
        money Credit
        char3 Currency
        money TxAmount
        rate ExchangeRate
        money BaseDebit
        money BaseCredit
        id CostCenterId
        id ProfitCenterId
        string PartyType
        id PartyId
        string Description
    }
    AccountDetermination {
        id Id PK
        id PostingRuleSetId FK
        string PostingRole
        string ProductPostingGroup
        string PartyPostingGroup
        id TaxCodeId
        id WarehouseId
        int Priority
        id AccountId
    }
    AccountBalance {
        id LegalEntityId PK
        id AccountId PK
        id FiscalPeriodId PK
        id CostCenterId PK
        char3 Currency PK
        money BaseDebit
        money BaseCredit
        money TxBalance
    }
    OpenItem {
        id Id PK
        id TenantId
        id LegalEntityId
        string PartyType "Customer|Supplier"
        id PartyId
        string SourceDocType
        id SourceDocId
        date DueDate
        char3 Currency
        money OriginalTx
        money OpenTx
        money OriginalBase
        money OpenBase
        rv RowVersion
    }
    Allocation {
        id Id PK
        id OpenItemId FK
        string SettlementDocType
        id SettlementDocId
        money AmountTx
        money AmountBase
        money RealizedFxBase
        date AllocationDate
    }
```

Database check constraints on `accounting.JournalLine`: `CK_JournalLine_OneSided CHECK ((Debit = 0 AND Credit > 0) OR (Credit = 0 AND Debit > 0))`. Balance (Σ debit = Σ credit) is validated in the domain and re-verified in the posting transaction with a set-based query before commit.

## 6.9 Platform tables (every module schema)

| Table | Purpose |
|---|---|
| `<schema>.OutboxMessage` | Id, OccurredAtUtc, Type, Payload (json), TenantId, CorrelationId, ProcessedAtUtc, Attempts, Error |
| `<schema>.InboxMessage` | MessageId + Consumer (PK) — idempotent consumption |
| `core.IdempotencyRecord` | (TenantId, Key, Endpoint) PK, request hash, response status/body, expires |
| `audit.AuditLog` | Id, TenantId, UserId, Action, EntityType, EntityId, BeforeJson, AfterJson, CorrelationId, AtUtc, PrevHash, Hash |
| `core.DocumentStatusHistory` | document ref, from, to, user, reason, at |
| `core.Attachment` | owner doc ref, storage key, file name, content type, size, hash |
