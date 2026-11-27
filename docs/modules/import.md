# Data import module

Schema `import` (migration order 180), module code `import`, core (always on). Loads opening and master data from CSV or XLSX files with a **dry run** before anything is written. The module owns only the job record (`import.ImportJob`, per-row errors as a JSON column per ADR-030) and the uploaded bytes (`import.ImportJobFile`). Every record it creates is written **by the owning module**, through that module's import contract. The contract runs the module's regular commands, so imported data passes exactly the same validation, numbering, posting and audit as manual entry. The Import module never touches another module's tables.

## Workflow

1. **Upload** a `.csv` or `.xlsx` file (up to 10 MB and 50,000 data rows) with the import type. The job starts as `Uploaded`.
2. **Validate** (dry run). The import is executed for real inside a database transaction, then always rolled back. Afterwards the job is `Validated` (no errors) or `Failed`, with per-row errors (up to 1,000 are stored).
   - Rows run in file order, so later rows see earlier ones. For example, a parent account created on row 2 can be used on row 5, and a code repeated in the file is reported on its second row.
3. **Commit** (only when `Validated`). The same execution runs again in one transaction, in batches of 100 units (change trackers are cleared between batches).
   - If anything fails now, because data changed since validation, **nothing is written**. The response is `422 import.commit-failed` with the failing rows in `errors` (`"row 7": [...]`), and the job stays `Validated`.
   - On success the job is `Committed` and cannot be committed or validated again.

How row errors are found: each unit runs under its own SQL Server savepoint. A failing unit is rolled back to that savepoint, and every module's change tracker is cleared. The remaining rows still run, so one dry run reports every problem in the file.

How errors are attributed for multi-row units (an opening-stock document per warehouse, the GL opening journal):

- line-indexed errors (`lines[i]`) point at the exact row;
- otherwise each row is re-run alone and rolled back to find the culprit;
- a document-level problem, such as a closed period, is reported on the unit's first row.

Concurrency:

- `commit` requires an `Idempotency-Key`. The same key replays the stored response.
- A different key on an already committed job → `409 import.already-committed`.
- The commit writes the job row first (`CommitStartedAtUtc`), so two simultaneous commits serialise: exactly one applies.

## Endpoints (authenticated, `X-Company-Id`)

| Method | Route | Permission | Purpose |
|---|---|---|---|
| GET | `/api/v1/import/types` | `import.jobs.read` | Every import type with its columns: key, English and Arabic header, required, description, example, aliases. Also the target permission and whether a legal entity or opening date is needed. |
| GET | `/api/v1/import/types/{type}/template` | `import.jobs.read` | XLSX template. The *Data* sheet has the headers (required ones marked `*`) and an example row; the *Columns* sheet is help (EN/AR). |
| GET | `/api/v1/import/jobs` | `import.jobs.read` | Paged list (`type`, `status`, `page`, `pageSize`), without errors. |
| GET | `/api/v1/import/jobs/{id}` | `import.jobs.read` | Job with its row errors. |
| POST | `/api/v1/import/jobs` | `import.jobs.write` + target | `multipart/form-data`: `file`, `type`, `legalEntityId` (when required), `openingDate` (`yyyy-MM-dd`, when required). Unreadable files are refused here. |
| POST | `/api/v1/import/jobs/{id}/validate` | `import.jobs.write` + target | Dry run. Returns the job. |
| POST | `/api/v1/import/jobs/{id}/commit` | `import.jobs.write` + target | Commit. Needs `Idempotency-Key`. |

`{type}` accepts the enum name or a kebab-case form: `ChartOfAccounts`, `Products`, `Customers`, `Suppliers`, `OpeningStock`, `OpenReceivables`, `OpenPayables`, `GlOpeningBalances` (for example `gl-opening-balances`).

The **target permission** is checked on upload, validate and commit (`403 import.target-permission-missing`), because an import writes another module's data:

| Type | Target permission | Needs | Written by (contract) |
|---|---|---|---|
| ChartOfAccounts | `accounting.account.manage` | — | `IAccountingImportService.CreateAccountAsync` → `CreateAccountCommand` |
| Products | `catalog.product.manage` | — | `ICatalogImportService.CreateProductAsync` → `CreateProductCommand` (+ `AddPriceCommand`) |
| Customers | `parties.customer.manage` | — | `IPartyImportService.CreateCustomerAsync` → `CreateCustomerCommand` (+ `SetCreditProfileCommand`) |
| Suppliers | `parties.supplier.manage` | — | `IPartyImportService.CreateSupplierAsync` → `CreateSupplierCommand` |
| OpeningStock | `inventory.document.post` | opening date | `IInventoryImportService.PostOpeningStockAsync` → create, submit, approve and post an opening-stock document **per warehouse** |
| OpenReceivables | `finance.opening-balance.manage` | legal entity | `IFinanceImportService.CreateOpeningItemAsync` (one open item + one journal per document) |
| OpenPayables | `finance.opening-balance.manage` | legal entity | same |
| GlOpeningBalances | `accounting.opening-balance.manage` | legal entity, opening date | `IAccountingImportService.PostOpeningBalancesAsync` → one opening-balance document and journal |

Code lookups for references the Import module resolves itself go through contracts as well: `IOrganizationLookups.FindWarehouseByCodeAsync`, `ICatalogImportService.FindProductIdsBySkuAsync` (+ `ICatalogQueries` for units), and `IPartyImportService.Find{Customer|Supplier}IdsByCodeAsync`.

## File format

- **Headers** are matched ignoring case, spaces and punctuation, in any order. Any of these work: the English header, the column key, the Arabic header, or a listed alias. For example `Name (Arabic)`, `name_ar`, `NameAr` and `الاسم بالعربية` are the same column; a trailing `*` from the template is ignored. Unknown columns are ignored. A missing required column or a duplicated column is reported on row 1, and no rows are read.
- **CSV:** UTF-8 (BOM optional), RFC 4180 quoting, delimiter detected from the header line (comma, semicolon or tab).
- **XLSX:** the first worksheet, header in row 1. Numeric and date cells are converted to invariant text, so a numeric code `1115` stays `1115`.
- **Numbers:** `1,234.50`, `1234.5`, `12,5` (a single comma not followed by three digits is a decimal comma), `(150)` for negatives, and Arabic-Indic digits.
- **Dates:** `yyyy-MM-dd` (recommended), `dd/MM/yyyy`, `yyyy/MM/dd`, and Excel dates.
- **Booleans:** yes/no, true/false, 1/0, نعم/لا.
- **Row numbers** in errors are spreadsheet rows (header = 1). Row 0 means the whole file, for example `import.unbalanced`.

## Columns (required in bold)

- **ChartOfAccounts:** **Code**, **Name (English)**, **Name (Arabic)**, **Parent code** (a group account, for example a template group such as `11`), Type (blank = parent's type), Posting (default yes), Allow manual posting, Cash-flow category. Imported accounts are never control accounts.
- **Products:** **SKU**, **Name (English)**, **Name (Arabic)**, **Category code**, **Base unit**. Optional: Type (default Stockable), Brand code, Tracking, Expiry tracked, Tax category, Barcode, and Price list code + Price, which must be given together and add one price in the base unit.
- **Customers:** **Code**, **Name (English)**, **Name (Arabic)**. Optional: Kind (default Business), Tax registration no, Currency (default: the company base currency), Customer group code, Payment terms code, Posting group, Credit limit, Credit action.
- **Suppliers:** **Code**, **Name (English)**, **Name (Arabic)**. Optional: Tax registration no, Currency, Payment terms code, Posting group.
- **OpeningStock:** **Warehouse code**, **SKU**, **Quantity**, **Unit cost**. Optional: Unit (blank = base unit), Batch number, Manufacture date, Expiry date, Serial numbers (`;`-separated). One document per warehouse, dated the job's opening date, posted Dr inventory / Cr `OpeningBalanceEquity`.
- **OpenReceivables / OpenPayables:** **Customer code / Supplier code**, **Document number**, **Document date**, **Open amount**. Optional: Due date (blank = document date), Currency (blank = functional), Exchange rate (blank = recorded spot rate), Description.
  - A negative amount is an open credit (credit note or advance).
  - A party + document number may appear only once, both in the file and against earlier imports (`finance.opening-item-exists`).
- **GlOpeningBalances:** **Account code**, then Debit or Credit (exactly one per row), and optional Description.
  - The file must balance (`import.unbalanced`, row 0).
  - Control accounts are refused per row, so open them with the sub-ledger imports: receivables, payables and inventory. A legal entity can have only one posted GL opening document.

## Finance opening documents

`IFinanceImportService.CreateOpeningItemAsync` (permission `finance.opening-balance.manage`, sensitive) creates **one open item** with source type `OpeningReceivable` / `OpeningPayable`, a single installment and the explicit due date, so aging, statements, receipts/payments and FX revaluation treat it like any invoice. It posts **one journal** in functional currency at the document rate:

- Dr receivable (party) / Cr `OpeningBalanceEquity` for a customer invoice;
- Dr `OpeningBalanceEquity` / Cr payable for a supplier bill;
- reversed for credits.

The control accounts therefore always equal the sub-ledger.

Suggested order for a go-live:

1. chart of accounts;
2. GL opening balances (non-control accounts);
3. products;
4. customers and suppliers;
5. opening stock;
6. open receivables and payables.

Because control accounts are excluded from the GL file, a balanced GL file includes an opening-balance-equity line (`3300` in the generic chart) for the net of the sub-ledgers: receivables + inventory − payables. Each sub-ledger import then posts against the same account. When everything is in, `OpeningBalanceEquity` is zero; a residual means the sub-ledger files do not match the old trial balance.

## Limitations

- Creates records only: no update or upsert of existing records, and no import of product variants, alternative units, BOMs, fixed assets or price lists themselves.
- Categories, units, brands, price lists, customer groups, payment terms and warehouses must exist (referenced by code).
- Dry runs execute the real postings inside a rolled-back transaction. While a large opening-stock or GL import validates, it briefly holds the same locks as the real posting (number series, balances) for that company.
- The uploaded file is kept in the database with the job. There is no retention or clean-up job yet.
- Import jobs are not written to the audit log; the records they create are audited by their own modules.
