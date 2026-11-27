# Missing Requirements & Open Questions

Part 25 requires that critical accounting, tax and inventory policy decisions are **not** made silently. Each item below has a **recommended default**. Items marked **🔴 Blocking** must be confirmed before the phase that implements them starts; the recommendation is used as the working assumption until then and is configurable wherever possible.

## 1. Missing business requirements (not covered by the specification)

| ID | Missing requirement | Why it matters | Recommendation |
|---|---|---|---|
| MR-1 | **E-invoicing / fiscal compliance** (e.g. Egypt ETA e-invoice & e-receipt, KSA ZATCA Fatoorah) | Legally mandatory in likely target markets; affects invoice numbering, immutability, QR codes, digital signatures, POS receipts | Define a `Compliance` integration port per jurisdiction; invoice snapshot + submission status on Sales/POS documents. Target jurisdictions must be named. 🔴 before Phase 6 |
| MR-2 | **Withholding tax** (common in Egypt/GCC on supplier payments and customer receipts) | Changes payment/receipt posting | Model as tax component with `TaxKind = Withholding` applied at settlement time |
| MR-3 | **Data migration / import** from legacy systems (opening balances, master data, open invoices) | Every ERP go-live needs it | Phase 3+: CSV/Excel import framework with validation and dry-run; opening-balance documents (stock, AR, AP, GL) |
| MR-4 | **Intercompany transactions** between legal entities in one tenant | Spec allows multiple legal entities but is silent on transactions between them | v1: block cross-legal-entity transfers unless an intercompany policy is configured; design due-to/due-from accounts; full intercompany later |
| MR-5 | **Consolidation** of multiple legal entities | Group reporting | Out of v1; consolidated *reporting* (sum, same currency) only |
| MR-6 | **Rounding rules** (line vs document tax rounding, cash rounding for POS) | Affects every total; jurisdiction-specific | Per-legal-entity tax rounding policy (default: per line, half away from zero); per-currency cash rounding increment |
| MR-7 | **Discount stacking order** (price list → customer → volume → promotion → manual) | Pricing determinism | Ordered, explicit pricing pipeline; manual discount requires permission & limit |
| MR-8 | **Credit limit behavior** (warn vs block; at SO or invoice; include open orders?) | Sales flow | Policy: `CreditCheckPoint = SalesOrderConfirm|InvoicePost`, `Action = Warn|Block|RequireApproval`; exposure = AR balance + uninvoiced confirmed orders |
| MR-9 | **Printing hardware** (thermal 80 mm receipts, A4 invoices, barcode labels) | POS & warehouse | Browser printing + ESC/POS via local print agent (future); templates per document type |
| MR-10 | **External integrations** (webhooks, public API for e-commerce/BI) | Common need | Versioned public API + outbound webhooks fed by outbox (Phase 10) |
| MR-11 | **Data retention & residency** | Legal (tax archives often 5–10 yrs) & SaaS customer contracts | Retention policy per tenant; region-pinned SaaS databases |
| MR-12 | **Distribution-specific features** (routes, van sales, delivery planning, proof of delivery) | "Distribution" business model listed but no features specified | Treated as future specialized module; v1 covers distribution through reservations, partial deliveries and multi-warehouse |
| MR-13 | **Service business features** (timesheets, service contracts, recurring invoices) | "Service-based businesses where applicable" | v1: service products, non-stock invoices, expenses; recurring invoices Phase 10 |
| MR-14 | **Licensing enforcement on-prem** (user counts, module entitlements, offline license file) | Commercial model | Signed offline license file verified at startup; integration port in Configuration |
| MR-15 | **Bin/location management inside warehouses** | Larger warehouses | Out of v1; warehouse schema keeps an optional `LocationId` column for future |
| MR-16 | **Customer/supplier portal** | Not requested | Out of scope |
| MR-17 | **Budgeting** | Common finance need | Out of v1; cost/profit centers enable later |

## 2. Open policy questions

### Inventory

| ID | Question | Recommended default | Status |
|---|---|---|---|
| OQ-INV-1 | Valuation area: is cost averaged per **legal entity**, per **branch** or per **warehouse**? | **Legal entity** (configurable to warehouse). Transfers inside a valuation area then carry cost without revaluation. | 🔴 before Phase 4 |
| OQ-INV-2 | Costing method selectable per company, per product category, or per product? | Per **product category** with company default; change of method only at period start with a revaluation document | 🔴 before Phase 4 |
| OQ-INV-3 | Back-dated inventory postings: allowed? Do they trigger cost recalculation of later issues? | Allowed within open periods; v1 costs in **posting sequence** (not document-date order) and flags back-dated postings; a recost utility is a later capability. Period lock prevents back-dating into closed periods. | 🔴 before Phase 4 |
| OQ-INV-4 | Negative stock under WAC/FIFO: what cost is used, and how is it trued up? | Negative stock **off** by default. When on: issue at last known WAC; on next receipt post a cost true-up movement + GL adjustment. FIFO + negative stock not allowed. | 🔴 before Phase 4 |
| OQ-INV-5 | Available quantity formula — subtract outgoing (unreserved confirmed orders) too? | `Available = OnHand − Reserved`; `Projected = OnHand − Reserved + Incoming − Outgoing` shown separately | Confirm |
| OQ-INV-6 | Stock count freeze: block movements or snapshot? | **Snapshot** (movements continue; variance = counted − (snapshot + movements after snapshot)) with optional freeze per warehouse | Confirm |
| OQ-INV-7 | FEFO enforced or suggested? | Suggested by default; enforceable by policy | Confirm |
| OQ-INV-8 | Expired stock: block sale? | Block sale of expired batches; configurable "near expiry" warning days | Confirm |

### Sales

| ID | Question | Recommended default | Status |
|---|---|---|---|
| OQ-SAL-1 | When is stock issued and COGS recognized — at **delivery note** or at **invoice**? | Policy per company: (a) *Invoice-driven* (no delivery note; invoice issues stock) for retail/POS; (b) *Delivery-driven*: delivery issues stock and posts `Dr COGS / Cr Inventory`; invoice posts revenue. Optional "Goods delivered not invoiced" deferral is a later capability. | 🔴 before Phase 6 |
| OQ-SAL-2 | Revenue recognition timing (IFRS 15 control transfer) | Revenue at invoice posting; companies invoicing after delivery should invoice promptly; deferred revenue out of v1 | Confirm |
| OQ-SAL-3 | Sales return cost | Original issue cost of the referenced invoice/delivery; un-referenced returns at current WAC (requires permission) | Confirm |
| OQ-SAL-4 | Can a posted invoice be edited? | Never. Cancellation = full reversal (credit note); corrections = credit/debit note | Fixed by spec |
| OQ-SAL-5 | Commission basis (invoiced, collected, margin) and timing | Configurable plans; default: % of net invoiced amount, payable on collection; accrued via journal | Confirm before commissions |
| OQ-SAL-6 | Customer advances: separate liability account? | Yes — `CustomerAdvances` liability; allocation moves to AR | Confirm |

### Procurement

| ID | Question | Recommended default | Status |
|---|---|---|---|
| OQ-PUR-1 | Purchase price variance between PO/GRN and invoice under WAC | If stock still on hand: adjust inventory value; for the portion already consumed: post to `PurchasePriceVariance` (or COGS) per policy | 🔴 before Phase 5 |
| OQ-PUR-2 | Landed cost allocation basis | Per landed-cost document: by value (default), quantity, weight or volume | Confirm |
| OQ-PUR-3 | Invoice-before-receipt accounting | `Dr Goods-Invoiced-Not-Received (asset) / Cr AP`; cleared at GRN | Confirm |
| OQ-PUR-4 | Three-way match tolerance | Qty 0 %, price 2 % default; outside tolerance → approval | Confirm |
| OQ-PUR-5 | Non-recoverable input tax | Capitalized into inventory cost / expense when tax code is non-recoverable | Confirm |

### Accounting / tax / currency

| ID | Question | Recommended default | Status |
|---|---|---|---|
| OQ-ACC-1 | Chart of accounts templates per jurisdiction? | Ship 1 generic IFRS-style template (AR/EN); jurisdiction templates added as data | Confirm |
| OQ-ACC-2 | Journal numbering — gapless per legal entity + fiscal year? | Yes, gapless, assigned at **posting** (drafts use temporary numbers) | Confirm |
| OQ-ACC-3 | Period close types | Open → Soft-closed (only users with `accounting.period.post-soft-closed`) → Closed; reopen requires permission + audit | Confirm |
| OQ-ACC-4 | Year-end closing | Closing entry moving P&L to retained earnings, generated by a year-end close document | Confirm |
| OQ-ACC-5 | Unrealized FX revaluation | Month-end revaluation of open foreign-currency AR/AP/bank with auto-reversal on 1st of next period | Confirm |
| OQ-ACC-6 | Exchange rate source | Manual entry + import port (central bank feed adapter optional) | Confirm |
| OQ-TAX-1 | Target jurisdictions for tax & e-invoicing | Must be named (see MR-1) | 🔴 before Phase 6 |
| OQ-TAX-2 | Compound taxes (tax on tax), e.g. table tax + VAT in Egypt | Supported via ordered tax components with `BaseIncludesPriorComponents` | Confirm |

### Manufacturing

| ID | Question | Recommended default | Status |
|---|---|---|---|
| OQ-MFG-1 | Standard vs actual costing for finished goods | v1: **actual costing** (materials actual + labor/machine at routing rates + overhead rate); standard costing with variances later | 🔴 before Phase 9 |
| OQ-MFG-2 | Backflush vs manual material issue | Both; per BOM line `IssueMethod = Manual|Backflush` | Confirm |
| OQ-MFG-3 | Overhead absorption basis | Rate per machine hour or labor hour per work center | Confirm |
| OQ-MFG-4 | Scrap accounting | Normal scrap absorbed in FG cost; abnormal scrap expensed to `ProductionScrap` | Confirm |
| OQ-MFG-5 | Subcontracting | Out of v1 | Confirm |

### Platform / NFR / localization

| ID | Question | Recommended default | Status |
|---|---|---|---|
| OQ-NFR-1 | Expected tenant count, users per tenant, transaction volumes | Targets in NFR-1..4 | Confirm |
| OQ-NFR-2 | SaaS hosting region(s) and cloud provider | Provider-agnostic containers; region per data residency | Confirm before Phase 11 |
| OQ-LOC-1 | Additional languages beyond AR/EN | Architecture supports N languages; only AR/EN shipped | Confirm |
| OQ-LOC-2 | Hijri calendar display | Optional display format; storage always Gregorian UTC | Confirm |
| OQ-LOC-3 | Arabic-Indic digits | User preference; default Western digits for data entry | Confirm |
| OQ-SEC-1 | Required external identity providers (Azure AD/Entra, Google, LDAP/AD on-prem) | OIDC generic; LDAP via on-prem adapter later | Confirm |
| OQ-LIC-1 | PDF library licensing (QuestPDF community license has a revenue threshold) | Abstract behind `IPdfRenderer`; decide commercial license vs alternative before Phase 6 | Confirm |
