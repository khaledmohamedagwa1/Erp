# Decisions Log

**Date:** 2026-11-27 · **Decided by:** project owner's instruction ("take the decisions now"), using the recommended default from
[03-open-questions-and-missing-requirements.md](03-open-questions-and-missing-requirements.md) for every open item.

Every decision below is implemented as **configuration** wherever the specification allows, so a later business review can change
behaviour without code changes. Items that are legal or financial in nature should still be confirmed by a qualified accountant /
tax adviser before go-live — they are marked ⚖️.

## Architecture

| Ref | Decision |
|---|---|
| ADR-017 | **Accepted.** Books per legal entity; master data per company; inventory valuation area = legal entity (configurable to warehouse). |
| ADR-019 | **Accepted.** Accounting core (chart of accounts, periods, journals, posting rules, trial balance) is built before inventory, purchasing and sales. |

## Inventory

| Ref | Decision |
|---|---|
| OQ-INV-1 | Valuation area = **legal entity** (policy `ValuationArea`, option `Warehouse`). |
| OQ-INV-2 | Costing method set per **product category**, falling back to the company default (**weighted average**). Changing a method is only allowed when the item has no stock. |
| OQ-INV-3 | Back-dated postings allowed **within open periods**; cost is computed in **posting sequence**, not document-date order. No automatic recost. |
| OQ-INV-4 | Negative stock **off** by default. When on: issue at the last weighted-average cost; the next receipt posts a cost true-up. FIFO + negative stock is not allowed. |
| OQ-INV-5 | `Available = OnHand − Reserved`. Projected availability (incoming/outgoing) is reported separately. |
| OQ-INV-6 | Stock counts use a **snapshot** (movements continue); variance = counted − (snapshot + movements after snapshot). |
| OQ-INV-7 | FEFO is **suggested** when picking, not enforced. |
| OQ-INV-8 | Expired batches **cannot be issued** for sale. |

## Sales

| Ref | Decision |
|---|---|
| OQ-SAL-1 | Policy `StockIssueTiming`: **AtInvoice** (default; invoice issues stock and posts COGS) or **AtDelivery** (delivery note issues stock and posts COGS; invoice posts revenue only). |
| OQ-SAL-2 ⚖️ | Revenue recognised at **invoice posting**. No deferred revenue in v1. |
| OQ-SAL-3 | Sales returns restock at the **original issue cost** of the referenced invoice line. |
| OQ-SAL-4 | Posted invoices are never edited; cancellation = full reversal, corrections = credit notes. |
| OQ-SAL-5 | Commissions: % of net invoiced amount. **Deferred** to a later phase (module switch exists). |
| OQ-SAL-6 | Customer advances go to a **Customer advances liability** account until allocated. |
| MR-7 | Pricing order: price list → customer price list → line discount % (manual, permissioned). Promotions later. |
| MR-8 | Credit check at **sales-order confirmation and invoice posting**; action from policy (default **RequireApproval**, which v1 enforces as a block with a clear error until the approval engine is wired to sales). |

## Procurement

| Ref | Decision |
|---|---|
| OQ-PUR-1 ⚖️ | Supplier-invoice price difference: portion for **stock still on hand → inventory value**; portion already **consumed → purchase price variance**. |
| OQ-PUR-2 | Landed-cost allocation basis per document; default **by value**. |
| OQ-PUR-3 | Invoice-before-receipt uses **Goods invoiced not received** (asset); receipt-before-invoice uses **GRNI** (liability). Default: receipt before invoice. |
| OQ-PUR-4 | Three-way match tolerance: quantity **0 %**, price **2 %**. |
| OQ-PUR-5 ⚖️ | Non-recoverable input tax is **added to cost** (inventory or expense). |

## Accounting, tax and currency

| Ref | Decision |
|---|---|
| OQ-ACC-1 | One generic, IFRS-style chart-of-accounts template in Arabic and English, seeded per company on request; jurisdiction templates are data. |
| OQ-ACC-2 ⚖️ | Journal numbers are **gapless per legal entity and fiscal year**, assigned at **posting**. |
| OQ-ACC-3 | Period states: **Open → Soft-closed → Closed**. Soft-closed accepts postings only with `accounting.period.post-soft-closed`; reopening needs `accounting.period.manage` and is audited. |
| OQ-ACC-4 ⚖️ | Year-end close posts a closing entry moving income and expense to **retained earnings**. |
| OQ-ACC-5 ⚖️ | Month-end **unrealized FX revaluation** of open foreign-currency items, auto-reversed on the first day of the next period. |
| OQ-ACC-6 | Exchange rates entered manually (import adapter later). Rate = base-currency units per 1 foreign unit. |
| MR-1 / OQ-TAX-1 ⚖️ | The tax engine is **jurisdiction-neutral**. The first seeded template is **Egypt** (VAT 14 %, table tax as a compound component, withholding tax), matching the sample data. **Electronic invoicing submission (Egypt ETA, KSA ZATCA) is NOT implemented** — only an integration port and an invoice snapshot are provided. |
| OQ-TAX-2 | Compound taxes supported (a component may include previous components in its base). |
| MR-2 | Withholding tax is a tax kind applied at **payment/receipt**. |
| MR-6 | Tax rounding **per line**, half away from zero, to the currency's minor units. |

## Manufacturing

| Ref | Decision |
|---|---|
| OQ-MFG-1 ⚖️ | **Actual costing**: materials at actual issue cost + labour/machine/overhead at work-centre rates. Standard costing later. |
| OQ-MFG-2 | Both **manual issue and backflush**, per BOM line. |
| OQ-MFG-3 | Overhead absorbed at a rate per hour per work centre. |
| OQ-MFG-4 | Normal scrap absorbed into finished-goods cost; residual WIP at close goes to **production variance**. |
| OQ-MFG-5 | No subcontracting in v1. |

## Platform

| Ref | Decision |
|---|---|
| OQ-LOC-1..3 | Arabic and English only; Hijri dates as an optional display format; Western digits by default. |
| OQ-SEC-1 | Generic OIDC federation later; local accounts now. |
| OQ-LIC-1 | Document PDFs are produced through an `IDocumentRenderer` port; the first implementation renders HTML (printable). A commercial PDF library is a later procurement decision. |
| OQ-NFR-* | Targets in the NFR table are accepted as working targets. |
