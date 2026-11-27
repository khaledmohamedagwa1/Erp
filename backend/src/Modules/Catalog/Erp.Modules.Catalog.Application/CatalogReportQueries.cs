using Erp.Modules.Catalog.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Catalog.Application;

/// <summary>Catalog reads for the Reporting module. The company query filter keeps everything inside the active company.</summary>
public sealed class CatalogReportQueries(ICatalogDbContext db) : ICatalogReportQueries
{
    public async Task<IReadOnlyList<ProductReportInfo>> GetProductReportInfoAsync(IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken = default)
    {
        if (productIds.Count == 0)
        {
            return [];
        }

        var products = await db.Products.AsNoTracking().Include(p => p.Units).Where(p => productIds.Contains(p.Id)).ToListAsync(cancellationToken);
        var categoryIds = products.Select(p => p.CategoryId).Distinct().ToList();
        var categories = await db.Categories.AsNoTracking().Where(c => categoryIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, cancellationToken);
        var uomIds = products.SelectMany(p => p.Units.Select(u => u.UomId)).Distinct().ToList();
        var codes = await db.Uoms.AsNoTracking().Where(u => uomIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Code, cancellationToken);
        return products.Select(p =>
        {
            var category = categories[p.CategoryId];
            return new ProductReportInfo(p.Id, p.Sku, p.NameEn, p.NameAr, category.Id, category.Code, category.NameEn, category.NameAr,
                p.Units.Select(u => new ProductUnitInfo(u.UomId, codes.GetValueOrDefault(u.UomId, "?"), u.FactorToBase)).ToList());
        }).ToList();
    }

    public Task<bool> CategoryExistsAsync(Guid categoryId, CancellationToken cancellationToken = default) =>
        db.Categories.AsNoTracking().AnyAsync(c => c.Id == categoryId, cancellationToken);

    public async Task<IReadOnlyCollection<Guid>> GetProductIdsInCategoryAsync(Guid categoryId, CancellationToken cancellationToken = default)
    {
        var all = await db.Categories.AsNoTracking().Select(c => new { c.Id, c.ParentId }).ToListAsync(cancellationToken);
        var wanted = new HashSet<Guid> { categoryId };
        var added = true;
        while (added)
        {
            added = false;
            foreach (var c in all.Where(c => c.ParentId is { } parent && wanted.Contains(parent) && !wanted.Contains(c.Id)))
            {
                wanted.Add(c.Id);
                added = true;
            }
        }

        return await db.Products.AsNoTracking().Where(p => wanted.Contains(p.CategoryId)).Select(p => p.Id).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Guid, string>> GetUnitCodesAsync(IReadOnlyCollection<Guid> uomIds, CancellationToken cancellationToken = default) =>
        uomIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.Uoms.AsNoTracking().Where(u => uomIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Code, cancellationToken);
}
