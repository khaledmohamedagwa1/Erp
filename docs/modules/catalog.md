# Catalog module (products & pricing)

Schema `catalog` · migration order 40 · module code `catalog` (core) · all endpoints require `Authorization` and `X-Company-Id`.

## Model

| Aggregate | Notes |
|---|---|
| UnitOfMeasure | Code unique per company; class Count / Mass / Volume / Length / Time. `seed-standard` adds PCS, BOX, CTN, SET, KG, G, L, ML, M, CM, HR. |
| ProductCategory | Hierarchy (cycles rejected), default costing method (WeightedAverage / FIFO), posting group (default `GOODS`). Audited. |
| Brand | Code unique per company. |
| Product | SKU unique per company (also across variant SKUs); type Stockable, Consumable, Service, Bundle, RawMaterial, SemiFinished, FinishedGood, Manufactured; base unit (factor 1) plus extra units with factor-to-base; barcodes unique per company, optionally tied to a unit or variant; tracking None / Batch / Serial + expiry (stocked types only); tax category code (resolved by the Tax module); costing-method and posting-group overrides; active flag. Audited. |
| PriceList | Code unique per company, currency, tax-inclusive flag, validity. Items: product, optional variant, unit, minimum quantity (volume tier), price `decimal(19,4)`, validity. Overlapping validity for the same product/variant/unit/minimum quantity is rejected. Audited. |

Quantity conversion always goes through the base unit and rounds half away from zero to 6 decimals (`UomConversion`).
The base unit cannot change once another module reports the product as used (`IProductUsageProbe`; Inventory will provide it) or while other units exist.

## Endpoints (`/api/v1/catalog`)

| Method & path | Permission | Notes |
|---|---|---|
| `GET /uoms` · `POST /uoms` · `POST /uoms/seed-standard` | `catalog.read` · `catalog.setup.manage` | |
| `GET /categories` · `POST /categories` · `PUT /categories/{id}` | `catalog.read` · `catalog.setup.manage` | `catalog.category-cycle` on invalid moves. |
| `GET /brands` · `POST /brands` | `catalog.read` · `catalog.setup.manage` | |
| `GET /products?page&pageSize&search&categoryId&type&active` | `catalog.read` | Search matches SKU, EN/AR name, exact barcode or variant SKU. Paged. |
| `GET /products/{id}` | `catalog.read` | Units (with codes), barcodes, variants. |
| `POST /products` | `catalog.product.manage` | Body may include `units[]` and `barcodes[]`. Errors: `catalog.sku-taken`, `catalog.barcode-taken`, `catalog.uom-not-found`, `catalog.category-not-found`. |
| `PUT /products/{id}` | `catalog.product.manage` | Names, category, brand, base unit (guarded), tracking, tax category, overrides. |
| `POST /products/{id}/units` · `/barcodes` · `/variants` | `catalog.product.manage` | |
| `POST /products/{id}/deactivate` · `/activate` | `catalog.product.manage` | |
| `GET /barcodes/{code}` | `catalog.read` | Returns product, unit (the barcode's unit or the base unit), factor, variant. |
| `GET /price-lists` · `GET /price-lists/{id}?productId` | `catalog.read` | |
| `POST /price-lists` | `catalog.price.manage` (sensitive) | |
| `POST /price-lists/{id}/items` | `catalog.price.manage` | `catalog.price-overlap` on overlap. |
| `PUT /price-lists/{id}/products/{productId}` | `catalog.price.manage` | Replaces all prices of one product; validated as a set. |
| `GET /price-quote?priceListId&productId&variantId&uomId&quantity&date` | `catalog.read` | Variant price first, then same unit, then highest applicable volume tier (compared in base units). A price in another unit is converted. |

## Contracts for other modules

* `ICatalogQueries.GetProductsAsync(ids)` → `ProductInfo` with units, tracking and the effective costing method / posting group.
* `IPriceCalculator.GetPriceAsync(...)` → `PriceQuote` or null.
* `IProductUsageProbe` — implemented by Inventory to lock base units of used products.
