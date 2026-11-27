# Business Requirements, Domain Glossary, Critical Workflows and NFRs (Phase 0)

## 1. Business requirements summary

The platform is a **modular ERP** serving retail, wholesale, trading, distribution, manufacturing and service businesses, deployable as **multi-tenant SaaS** or **single-customer on-premises**, built on a **mandatory double-entry accounting core**. The detailed, numbered requirement list lives in [REQUIREMENTS_TRACEABILITY.md](../../REQUIREMENTS_TRACEABILITY.md). Business-level goals:

| ID | Goal | Measure of success |
|---|---|---|
| G-1 | One codebase for every supported business model | Business model differences expressed only by module activation + policies; zero `if (businessType == …)` branches (enforced by code review + architecture tests) |
| G-2 | Financial correctness by construction | Every posted source document produces a balanced journal; trial balance always balances; GL ↔ subledger reconciliation jobs report zero unexplained difference |
| G-3 | Inventory correctness | On-hand = Σ ledger movements for every (product, warehouse, batch/serial); inventory valuation = inventory GL control account balance (reconciliation job) |
| G-4 | Strict tenant isolation | Automated cross-tenant tests in CI; DB-level row-level security as second line |
| G-5 | Deployable in cloud and on-premises | Same container images; deployment differences are configuration only |
| G-6 | Bilingual, RTL-capable professional UI | All UI strings localized; Arabic RTL and English LTR fully supported |
| G-7 | Auditability | Every financial, inventory, pricing, permission and configuration change traceable to user, time and before/after values |

## 2. Domain glossary

| Term | Definition |
|---|---|
| **Tenant** | Isolation boundary for a SaaS customer or an on-prem installation. Owns users, companies and data. Never visible across tenants. |
| **Company** | A business organization inside a tenant. Unit of module activation, policies and master-data sharing. |
| **Legal Entity** | A legally registered entity with its own tax registration and its own **set of books** (ledger). A company has ≥1 legal entity. All journals belong to exactly one legal entity. |
| **Branch** | Operational location (shop, office, plant) belonging to one legal entity. Used for numbering, security restriction and reporting. |
| **Warehouse** | Physical or logical stock location belonging to one branch. Inventory balances are tracked per warehouse (and optionally bin in future). |
| **Valuation Area** | The scope across which inventory cost is averaged/layered. Default: legal entity (see open question OQ-INV-1). |
| **Source document** | A business document (invoice, GRN, transfer…) whose posting causes ledger effects. |
| **Draft** | Editable, has no ledger effect. |
| **Approved** | Passed the approval workflow. **Has no ledger effect** — approval ≠ posting. |
| **Posted** | Ledger effects (inventory and/or accounting) recorded. Immutable; corrected only by reversal/adjustment documents. |
| **Reversal** | A new document/journal that exactly negates a posted one, referencing it. |
| **Inventory movement** | Immutable ledger row recording a quantity and cost change for a product in a warehouse. |
| **On-hand** | Physical quantity in a warehouse = Σ posted movements. |
| **Reserved** | Quantity committed to a demand (sales order, production order) but not yet issued. |
| **Available** | On-hand − Reserved (default policy; see §7.1 of spec). |
| **Incoming** | Open quantity on approved POs, inbound transfers, and released production orders (finished goods) not yet received. |
| **Outgoing** | Open quantity on confirmed sales orders and production component requirements not yet issued. |
| **In-transit** | Quantity issued from a source warehouse on a transfer and not yet received at the destination; held in a virtual transit location owned by the legal entity. |
| **Base UOM** | The product's smallest stocking unit; all ledger quantities are stored in base UOM in addition to the transaction UOM. |
| **Cost layer** | FIFO receipt layer (quantity remaining + unit cost) per valuation area/product. |
| **WAC** | Weighted average cost, recomputed at each receipt into a valuation area. |
| **COGS** | Cost of goods sold, taken from the inventory issue cost — never from the current purchase price. |
| **GRNI** | Goods Received Not Invoiced — accrued liability recognized at goods receipt, cleared by the supplier invoice. |
| **Control account** | GL account fed only by a subledger (AR, AP, Inventory, GRNI); manual posting blocked when configured. |
| **Posting account** | Leaf GL account that accepts journal lines. |
| **Posting rule / account determination** | Configurable mapping from a semantic *posting role* (e.g. `SalesRevenue`) and context (product category, customer group, tax code, warehouse…) to a GL account. |
| **Fiscal period** | Accounting period (usually a month) with status Open / Soft-closed / Closed. |
| **Allocation (settlement)** | Linking a receipt/payment/credit note to one or more open invoices. |
| **WIP** | Work in progress: value of materials, labor and overhead in unfinished production orders. |
| **BOM** | Bill of materials: versioned list of components to produce a product. |
| **Routing** | Ordered operations at work centers with setup/run times and rates. |
| **MRP** | Material Requirements Planning — netting demand against supply to propose purchase/production orders. |
| **Outbox** | Table where integration events are written in the same DB transaction as the state change, then published asynchronously. |
| **Idempotency key** | Client-supplied key making a command safe to retry (essential for POS and posting). |

## 3. Critical business workflows

| # | Workflow | Modules | Key integrity rule |
|---|---|---|---|
| W-1 | Order-to-Cash: Quotation → SO → Delivery → Invoice → Receipt → Allocation | Sales, Inventory, Accounting, Finance | Stock issued exactly once (at delivery *or* invoice per policy); AR = Σ open invoices − unallocated credits |
| W-2 | Procure-to-Pay: PR → PO → GRN → Purchase invoice → Payment | Procurement, Inventory, Accounting, Finance | Inventory recognized exactly once; GRNI cleared by invoice; price variance handled per policy |
| W-3 | POS sale: Open shift → Sell → Pay (split) → Close shift → Cash reconciliation | POS, Sales, Inventory, Finance | Idempotent submission; shift variance posted |
| W-4 | Stock transfer: Draft → Approve → Issue → In-transit → Receive | Inventory, Accounting (only if cross-legal-entity or cross-valuation-area) | Cost preserved through transit |
| W-5 | Stock count: Open → Snapshot → Count → Variance → Approve → Adjust | Inventory, Accounting | Adjustment valued at current cost; GL impact via adjustment account |
| W-6 | Make-to-Stock: Production order → Reserve → Issue materials → Report operations → Receive FG → Close/variance | Manufacturing, Inventory, Accounting | WIP debited at issue, credited at completion; residual WIP → variance at close |
| W-7 | Period close: Sub-ledger cut-off → Accruals/depreciation → Revaluation (FX) → Soft close → Close | Accounting, Finance, Fixed Assets | No posting into closed period |
| W-8 | Returns: Sales return / credit note; purchase return / debit note | Sales, Procurement, Inventory, Accounting | Return cost = original issue cost (sales) / original receipt cost (purchase) by default |
| W-9 | Tenant onboarding: provision tenant → company → legal entity → COA template → policies → users | Organization, Identity, Configuration, Accounting | Tenant data isolated from first row |

## 4. Non-functional requirements

| ID | Category | Requirement (initial targets — to be confirmed, see OQ-NFR-*) |
|---|---|---|
| NFR-1 | Performance | P95 API latency < 300 ms for CRUD/lookup endpoints at 200 concurrent users per tenant; document posting P95 < 1.5 s for 100-line invoice |
| NFR-2 | Performance | POS product lookup by barcode < 100 ms server-side; POS UI usable with scanner input bursts (≥ 20 chars in < 50 ms) |
| NFR-3 | Scalability | Shared-DB SaaS: up to ~500 small tenants per database; large tenants moved to dedicated DB without code change |
| NFR-4 | Data volume | ≥ 10 M inventory movements and ≥ 50 M journal lines per legal entity without report timeouts (via indexed queries and balance snapshots) |
| NFR-5 | Availability | SaaS: 99.9 % monthly; on-prem: customer-defined |
| NFR-6 | Recoverability | SaaS: RPO ≤ 15 min (log backups), RTO ≤ 4 h; on-prem: configurable backup scripts with documented restore |
| NFR-7 | Security | OWASP ASVS L2 baseline; TLS everywhere; secrets never in source control; MFA available |
| NFR-8 | Auditability | Audit records append-only; retained ≥ 10 years (configurable per jurisdiction) |
| NFR-9 | Localization | Arabic & English; RTL/LTR; locale-aware number/date formats; Hijri display optional (OQ-LOC-2) |
| NFR-10 | Accessibility | WCAG 2.1 AA for core screens; full keyboard operation of data-entry screens |
| NFR-11 | Maintainability | Module boundaries enforced by architecture tests; ≥ 80 % line coverage on Domain projects |
| NFR-12 | Portability | No hard dependency on a single cloud provider; runs on Linux containers and on Windows Server (on-prem) |
| NFR-13 | Observability | Structured logs with correlation id, tenant id, user id; OpenTelemetry traces/metrics; health endpoints |
| NFR-14 | Offline | On-prem operates without internet; POS offline is a *future* capability with designed-in idempotency |
| NFR-15 | Precision | Money `decimal(19,4)`, quantities `decimal(18,6)`, unit cost `decimal(19,6)`, FX rates `decimal(18,8)`; deterministic rounding policy per currency |

## 5. Assumptions

| ID | Assumption | Impact if wrong |
|---|---|---|
| A-1 | Primary market includes Arabic-speaking jurisdictions (Egypt, GCC) | Tax/e-invoicing adapters prioritized accordingly |
| A-2 | Fiscal year is configurable; default calendar year, monthly periods | Period engine supports 4-4-5 / 13 periods later |
| A-3 | One functional (base) currency per legal entity | Required by IAS 21; consolidation in different presentation currency is future |
| A-4 | Prices may be tax-inclusive or exclusive per price list | Tax engine supports both |
| A-5 | HR/payroll is out of scope (employees exist only as master data) | Payroll would be a new bounded context |
| A-6 | Intercompany transactions and group consolidation are out of scope for v1 but must not be blocked by design | Legal-entity id on every journal makes it addable |
| A-7 | SQL Server 2019+ (on-prem may use Express for very small sites; Express 10 GB limit documented) | Feature use limited to 2019+ (RLS, JSON functions) |
