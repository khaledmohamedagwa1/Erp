using Erp.Application.Abstractions;
using Erp.Application.Reporting;
using Erp.SharedKernel.Results;

namespace Erp.Modules.Reporting.Application;

public sealed record ReportRun(ReportDefinition Definition, ReportOutput Output, ReportQuery Query);

/// <summary>
/// Finds a report, checks permission and module activation (server-side, per request), applies default filters,
/// resolves the product/category filter and enforces the export row cap.
/// </summary>
public sealed class ReportService(
    IEnumerable<IReportArea> areas,
    ITenantContext tenant,
    ICurrentUser user,
    IPermissionService permissions,
    IModuleActivationService modules,
    ReportLookups lookups,
    IClock clock,
    ReportingOptions options)
{
    public static readonly IReadOnlyList<string> Formats = ["json", "csv", "xlsx", "html"];

    private const int MaxPageSize = 1000;

    public int ExportCap => Math.Max(1, options.MaxExportRows);

    public IEnumerable<ReportDefinition> AllDefinitions => areas.SelectMany(a => a.Definitions);

    /// <summary>Reports the current user may run in the active company: permission granted, module enabled and installed.</summary>
    public async Task<Result<IReadOnlyList<ReportCatalogueEntry>>> CatalogueAsync(CancellationToken ct)
    {
        if (tenant.CompanyId is not { } companyId)
        {
            return ReportingErrors.CompanyRequired;
        }

        var granted = user.UserId is { } userId ? await permissions.GetPermissionsAsync(userId, ct) : new HashSet<string>();
        var enabled = await modules.GetEnabledModulesAsync(companyId, ct);
        return areas.Where(a => a.IsAvailable)
            .SelectMany(a => a.Definitions)
            .Where(d => granted.Contains(d.Permission) && enabled.Contains(d.ModuleCode))
            .Select(d => new ReportCatalogueEntry(d.Area, d.Key, d.TitleEn, d.TitleAr, d.DescriptionEn, d.Source, d.Filters,
                d.IsLink ? ["json"] : Formats, d.Href ?? $"/api/v1/reports/{d.Area}/{d.Key}"))
            .ToList();
    }

    /// <summary>Finds the report and checks that the user may run it in the active company.</summary>
    public async Task<Result<ReportDefinition>> AuthorizeAsync(string area, string key, CancellationToken ct)
    {
        var reportArea = areas.FirstOrDefault(a => string.Equals(a.Area, area, StringComparison.OrdinalIgnoreCase));
        var definition = reportArea?.Definitions.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));
        if (reportArea is null || definition is null || !reportArea.IsAvailable)
        {
            return ReportingErrors.UnknownReport;
        }

        if (tenant.CompanyId is not { } companyId)
        {
            return ReportingErrors.CompanyRequired;
        }

        if (user.UserId is not { } userId || !await permissions.HasPermissionAsync(userId, definition.Permission, ct))
        {
            return ReportingErrors.Forbidden(definition.Permission);
        }

        return await modules.IsEnabledAsync(companyId, definition.ModuleCode, ct) ? definition : ReportingErrors.ModuleDisabled;
    }

    public async Task<Result<ReportRun>> RunAsync(string area, string key, ReportRequest request, bool forExport, bool arabic, CancellationToken ct)
    {
        var authorized = await AuthorizeAsync(area, key, ct);
        if (authorized.IsFailure)
        {
            return authorized.Error;
        }

        var definition = authorized.Value;
        var query = await BuildQueryAsync(request, forExport, arabic, ct);
        if (query.IsFailure)
        {
            return query.Error;
        }

        var reportArea = areas.First(a => string.Equals(a.Area, definition.Area, StringComparison.Ordinal));
        Result<ReportOutput> output;
        if (query.Value.ProductIds is { Count: 0 })
        {
            // The product/category filter matched nothing: run on a product that cannot exist so every report shape stays the same.
            output = await reportArea.RunAsync(definition.Key, query.Value with { ProductIds = [Guid.Empty] }, ct);
        }
        else
        {
            output = await reportArea.RunAsync(definition.Key, query.Value, ct);
        }

        if (output.IsFailure)
        {
            return output.Error;
        }

        if (forExport && !output.Value.IsPaged && output.Value.Table.Rows.Count > ExportCap)
        {
            return ReportingErrors.ExportTooLarge(output.Value.Table.Rows.Count, ExportCap);
        }

        return new ReportRun(definition, output.Value, query.Value);
    }

    public async Task<RenderContext> RenderContextAsync(bool arabic, Guid? legalEntityId, CancellationToken ct)
    {
        var letterhead = await lookups.LetterheadAsync(legalEntityId, ct);
        return new RenderContext(arabic ? "ar" : "en", letterhead?.CompanyNameEn ?? string.Empty, letterhead?.CompanyNameAr ?? string.Empty,
            letterhead?.LegalEntityName, letterhead?.TaxRegistrationNo, clock.UtcNow);
    }

    /// <summary>JSON shape: the requested page of rows (unless the builder already paged in the database).</summary>
    public static ReportResultDto ToDto(ReportRun run)
    {
        var table = run.Output.Table;
        var rows = run.Output.IsPaged
            ? table.Rows
            : table.Rows.Skip((run.Query.Page - 1) * run.Query.PageSize).Take(run.Query.PageSize).ToList();
        var total = run.Output.IsPaged ? run.Output.TotalCount : table.Rows.Count;
        return new ReportResultDto(run.Definition.Area, run.Definition.Key, table.TitleEn, table.TitleAr,
            table.Columns.Select(c => new ReportColumnDto(c.Key, c.LabelEn, c.LabelAr, c.Type.ToString().ToLowerInvariant())).ToList(),
            rows.Select(r => ToObject(table.Columns, r)).ToList(),
            table.Totals is { } totals ? ToObject(table.Columns, totals) : null,
            table.Parameters ?? [], run.Query.Page, run.Query.PageSize, total);
    }

    private static IDictionary<string, object?> ToObject(IReadOnlyList<ReportColumn> columns, object?[] values)
    {
        var row = new Dictionary<string, object?>(columns.Count, StringComparer.Ordinal);
        for (var i = 0; i < columns.Count; i++)
        {
            row[columns[i].Key] = i < values.Length ? values[i] : null;
        }

        return row;
    }

    private async Task<Result<ReportQuery>> BuildQueryAsync(ReportRequest r, bool forExport, bool arabic, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(clock.UtcNow);
        var to = r.To ?? today;
        var from = r.From ?? new DateOnly(to.Year, to.Month, 1);
        if (from > to)
        {
            return ReportingErrors.InvalidRange;
        }

        if (to.DayNumber - from.DayNumber > 3 * 366)
        {
            return ReportingErrors.RangeTooLong;
        }

        IReadOnlyCollection<Guid>? products = null;
        if (r.CategoryId is { } categoryId)
        {
            var inCategory = await lookups.ProductsInCategoryAsync(categoryId, ct);
            products = r.ProductId is { } productId ? inCategory.Where(p => p == productId).ToList() : inCategory;
        }
        else if (r.ProductId is { } productId)
        {
            products = [productId];
        }

        var page = Math.Max(1, r.Page);
        var pageSize = Math.Clamp(r.PageSize, 1, MaxPageSize);
        return new ReportQuery(from, to, today, r.BranchId, r.WarehouseId, r.CustomerId, r.SupplierId, products, r.GroupBy?.Trim(), r.Days,
            string.IsNullOrWhiteSpace(r.BatchNumber) ? null : r.BatchNumber.Trim(), page, pageSize, forExport, ExportCap, arabic);
    }
}
