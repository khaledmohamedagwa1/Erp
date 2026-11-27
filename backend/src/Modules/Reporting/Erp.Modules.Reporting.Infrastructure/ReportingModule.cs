using System.Globalization;
using Erp.Application;
using Erp.Application.Abstractions;
using Erp.Application.Messaging;
using Erp.Application.Reporting;
using Erp.Infrastructure;
using Erp.Modules.Reporting.Application;
using Erp.Modules.Reporting.Contracts;
using Erp.Web.Http;
using Erp.Web.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Modules.Reporting.Infrastructure;

/// <summary>Query-string filters of <c>GET /api/v1/reports/{area}/{report}</c>.</summary>
public sealed class ReportParameters
{
    public DateOnly? From { get; set; }

    public DateOnly? To { get; set; }

    public Guid? BranchId { get; set; }

    public Guid? WarehouseId { get; set; }

    public Guid? CustomerId { get; set; }

    public Guid? SupplierId { get; set; }

    public Guid? ProductId { get; set; }

    public Guid? CategoryId { get; set; }

    public string? GroupBy { get; set; }

    public int? Days { get; set; }

    public string? BatchNumber { get; set; }

    public int? Page { get; set; }

    public int? PageSize { get; set; }

    /// <summary>json (default), csv, xlsx or html.</summary>
    public string? Format { get; set; }

    /// <summary>en or ar; defaults to the Accept-Language header.</summary>
    public string? Lang { get; set; }

    public ReportRequest ToRequest() =>
        new(From, To, BranchId, WarehouseId, CustomerId, SupplierId, ProductId, CategoryId, GroupBy, Days, BatchNumber, Page ?? 1, PageSize ?? 100);
}

public sealed record CreateSavedViewRequest(string Name, string ReportKey, System.Text.Json.JsonElement? Filters);

public sealed class ReportingModule : IErpModule
{
    public const string Code = "reporting";

    public ModuleManifest Manifest { get; } = new(
        Code,
        "Reports",
        "التقارير",
        IsCore: true,
        DependsOn: [],
        Permissions:
        [
            new(ReportingPermissions.SalesRead, Code, "Run sales reports", "تشغيل تقارير المبيعات"),
            new(ReportingPermissions.PurchasingRead, Code, "Run purchasing reports", "تشغيل تقارير المشتريات"),
            new(ReportingPermissions.InventoryRead, Code, "Run inventory reports (including stock values)", "تشغيل تقارير المخزون (بما فيها قيم المخزون)"),
            new(ReportingPermissions.ManufacturingRead, Code, "Run manufacturing reports", "تشغيل تقارير التصنيع"),
            new(ReportingPermissions.DocumentPrint, Code, "Print posted documents (invoices, credit notes, orders, receipts, vouchers)", "طباعة المستندات المرحلة"),
            new(ReportingPermissions.ViewManage, Code, "Save and delete personal report views", "حفظ وحذف طرق العرض الشخصية للتقارير"),
        ],
        Menu:
        [
            new("reporting.sales", "Sales reports", "تقارير المبيعات", "/reports/sales", "reports", 10, ReportingPermissions.SalesRead),
            new("reporting.purchasing", "Purchasing reports", "تقارير المشتريات", "/reports/purchasing", "reports", 20, ReportingPermissions.PurchasingRead),
            new("reporting.inventory", "Inventory reports", "تقارير المخزون", "/reports/inventory", "reports", 30, ReportingPermissions.InventoryRead),
            new("reporting.manufacturing", "Manufacturing reports", "تقارير التصنيع", "/reports/manufacturing", "reports", 40, ReportingPermissions.ManufacturingRead),
        ]);

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddErpDbContext<ReportingDbContext>(ReportingDbContext.SchemaName, order: 160);
        services.AddScoped<IReportingDbContext>(sp => sp.GetRequiredService<ReportingDbContext>());
        var options = new ReportingOptions();
        configuration.GetSection(ReportingOptions.SectionName).Bind(options);
        services.AddSingleton(options);
        services.AddScoped<ReportLookups>();
        services.AddScoped<IReportArea, SalesReports>();
        services.AddScoped<IReportArea, PurchasingReports>();
        services.AddScoped<IReportArea, InventoryReports>();
        services.AddScoped<IReportArea, ManufacturingReports>();
        services.AddScoped<IReportArea, FinancialReports>();
        services.AddScoped<ReportService>();
        services.AddScoped<DocumentPrinting>();
        services.AddHandlersFromAssembly(typeof(IReportingDbContext).Assembly);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/reports").WithTags("Reports").RequireAuthorization().RequireCompany();

        group.MapGet(string.Empty, async (ReportService reports, CancellationToken ct) => (await reports.CatalogueAsync(ct)).ToHttpResult());

        group.MapGet("/views", async (string? reportKey, IDispatcher d, CancellationToken ct) => (await d.Send(new ListSavedViewsQuery(reportKey), ct)).ToHttpResult())
            .RequirePermission(ReportingPermissions.ViewManage);
        group.MapPost("/views", async (CreateSavedViewRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CreateSavedViewCommand(body.Name, body.ReportKey, body.Filters), ct)).ToHttpResult(v => Results.Created($"/api/v1/reports/views/{v.Id}", v)))
            .RequirePermission(ReportingPermissions.ViewManage);
        group.MapDelete("/views/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new DeleteSavedViewCommand(id), ct)).ToHttpResult(_ => Results.NoContent()))
            .RequirePermission(ReportingPermissions.ViewManage);

        group.MapGet("/documents/{documentType}/{id:guid}", async (string documentType, Guid id, string? lang, HttpContext http, DocumentPrinting printing, CancellationToken ct) =>
            (await printing.PrintAsync(documentType, id, IsArabic(lang, http), ct)).ToHttpResult(doc =>
            {
                http.Response.Headers.ContentDisposition = $"inline; filename=\"{doc.FileName}\"";
                return Results.Content(doc.Html, "text/html; charset=utf-8");
            }));

        group.MapGet("/{area}/{report}", RunReportAsync);
    }

    private static async Task<IResult> RunReportAsync(
        string area, string report, [AsParameters] ReportParameters p, HttpContext http, ReportService reports, IDocumentRenderer renderer, CancellationToken ct)
    {
        var format = (p.Format ?? "json").Trim().ToLowerInvariant();
        if (!ReportService.Formats.Contains(format))
        {
            return ReportingErrors.InvalidFormat.ToProblem();
        }

        var authorized = await reports.AuthorizeAsync(area, report, ct);
        if (authorized.IsFailure)
        {
            return authorized.Error.ToProblem();
        }

        if (authorized.Value.Href is { } href)
        {
            return Results.Redirect(href + http.Request.QueryString.Value);
        }

        var arabic = IsArabic(p.Lang, http);
        var run = await reports.RunAsync(area, report, p.ToRequest(), forExport: format != "json", arabic, ct);
        if (run.IsFailure)
        {
            return run.Error.ToProblem();
        }

        var table = run.Value.Output.Table;
        var fileName = $"{run.Value.Definition.Area}-{run.Value.Definition.Key}-{run.Value.Query.Today.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}";
        switch (format)
        {
            case "csv":
                http.Response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}.csv\"";
                return Results.Stream(stream => CsvReportWriter.WriteAsync(table, stream, arabic, ct), CsvReportWriter.ContentType);
            case "xlsx":
            {
                var context = await reports.RenderContextAsync(arabic, null, ct);
                using var buffer = new MemoryStream();
                XlsxReportWriter.Write(table, context, buffer);
                return Results.File(buffer.ToArray(), XlsxReportWriter.ContentType, $"{fileName}.xlsx");
            }

            case "html":
            {
                var context = await reports.RenderContextAsync(arabic, null, ct);
                http.Response.Headers.ContentDisposition = $"inline; filename=\"{fileName}.html\"";
                return Results.Content(renderer.RenderReport(table, context), renderer.ContentType);
            }

            default:
                return Results.Ok(ReportService.ToDto(run.Value));
        }
    }

    private static bool IsArabic(string? lang, HttpContext http)
    {
        var language = string.IsNullOrWhiteSpace(lang) ? http.Request.Headers.AcceptLanguage.ToString() : lang;
        return language.TrimStart().StartsWith("ar", StringComparison.OrdinalIgnoreCase);
    }
}
