using Erp.Modules.Catalog.Contracts;
using Erp.Modules.Organization.Contracts;
using Erp.Modules.Parties.Contracts;

namespace Erp.Modules.Reporting.Application;

/// <summary>Resolves ids in report rows to codes and names through the owning modules' contracts (batched, one call per kind).</summary>
public sealed class ReportLookups(
    ICatalogReportQueries catalog,
    IPartyReportQueries parties,
    IOrganizationQueries organization,
    IOrganizationReportQueries organizationReports)
{
    public async Task<IReadOnlyDictionary<Guid, ProductReportInfo>> ProductsAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        return list.Count == 0 ? new Dictionary<Guid, ProductReportInfo>() : (await catalog.GetProductReportInfoAsync(list, ct)).ToDictionary(p => p.ProductId);
    }

    public async Task<IReadOnlyDictionary<Guid, PartyDisplay>> CustomersAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        return list.Count == 0 ? new Dictionary<Guid, PartyDisplay>() : (await parties.GetCustomersAsync(list, ct)).ToDictionary(p => p.Id);
    }

    public async Task<IReadOnlyDictionary<Guid, PartyDisplay>> SuppliersAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        return list.Count == 0 ? new Dictionary<Guid, PartyDisplay>() : (await parties.GetSuppliersAsync(list, ct)).ToDictionary(p => p.Id);
    }

    public async Task<IReadOnlyDictionary<Guid, WarehouseSummary>> WarehousesAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        return list.Count == 0 ? new Dictionary<Guid, WarehouseSummary>() : (await organization.GetWarehousesAsync(list, ct)).ToDictionary(w => w.Id);
    }

    public async Task<IReadOnlyDictionary<Guid, BranchSummary>> BranchesAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        return list.Count == 0 ? new Dictionary<Guid, BranchSummary>() : (await organizationReports.GetBranchesAsync(list, ct)).ToDictionary(b => b.Id);
    }

    public Task<IReadOnlyCollection<Guid>> ProductsInCategoryAsync(Guid categoryId, CancellationToken ct) => catalog.GetProductIdsInCategoryAsync(categoryId, ct);

    public Task<IReadOnlyDictionary<Guid, string>> UnitCodesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) => catalog.GetUnitCodesAsync(ids, ct);

    public Task<Letterhead?> LetterheadAsync(Guid? legalEntityId, CancellationToken ct) => organizationReports.GetLetterheadAsync(legalEntityId, ct);
}

internal static class LookupExtensions
{
    public static string Sku(this IReadOnlyDictionary<Guid, ProductReportInfo> products, Guid id) => products.TryGetValue(id, out var p) ? p.Sku : string.Empty;

    public static string Name(this IReadOnlyDictionary<Guid, ProductReportInfo> products, Guid id, bool arabic) =>
        products.TryGetValue(id, out var p) ? (arabic && !string.IsNullOrWhiteSpace(p.NameAr) ? p.NameAr : p.NameEn) : id.ToString();

    public static string Code(this IReadOnlyDictionary<Guid, PartyDisplay> parties, Guid id) => parties.TryGetValue(id, out var p) ? p.Code : string.Empty;

    public static string Name(this IReadOnlyDictionary<Guid, PartyDisplay> parties, Guid id, bool arabic) =>
        parties.TryGetValue(id, out var p) ? (arabic && !string.IsNullOrWhiteSpace(p.NameAr) ? p.NameAr : p.NameEn) : id.ToString();

    public static string Name(this IReadOnlyDictionary<Guid, WarehouseSummary> warehouses, Guid id) =>
        warehouses.TryGetValue(id, out var w) ? $"{w.Code} {w.Name}" : id.ToString();
}
