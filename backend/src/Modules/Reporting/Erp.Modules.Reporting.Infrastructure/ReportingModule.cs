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

    /// <summary>json (default), csv, xlsx, html or pdf (pdf needs a configured PDF engine).</summary>
    public string? Format { get; set; }

    /// <summary>en or ar; defaults to the Accept-Language header.</summary>
    public string? Lang { get; set; }

    public ReportRequest ToRequest() =>
        new(From, To, BranchId, WarehouseId, CustomerId, SupplierId, ProductId, CategoryId, GroupBy, Days, BatchNumber, Page ?? 1, PageSize ?? 100);
}

public sealed record CreateSavedViewRequest(string Name, string ReportKey, System.Text.Json.JsonElement? Filters);

public sealed record DashboardRequest(string Name, string? Description, IReadOnlyList<Guid>? SharedWithRoleIds, IReadOnlyList<WidgetInput>? Widgets);

public sealed record RebuildReadModelsRequest(DateOnly From, DateOnly To);

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
            new(ReportingPermissions.TaxRead, Code, "Run tax reports (VAT return, withholding)", "تشغيل تقارير الضرائب (إقرار القيمة المضافة والخصم من المنبع)"),
            new(ReportingPermissions.DocumentPrint, Code, "Print posted documents (invoices, credit notes, orders, receipts, vouchers)", "طباعة المستندات المرحلة"),
            new(ReportingPermissions.ViewManage, Code, "Save and delete personal report views", "حفظ وحذف طرق العرض الشخصية للتقارير"),
            new(ReportingPermissions.DashboardRead, Code, "View my dashboards and dashboards shared with my roles", "عرض لوحات المعلومات الخاصة بي والمشاركة مع أدواري"),
            new(ReportingPermissions.DashboardManage, Code, "Create dashboards and share them with roles", "إنشاء لوحات المعلومات ومشاركتها مع الأدوار"),
            new(ReportingPermissions.ScheduleManage, Code, "Schedule reports to be run and emailed", "جدولة التقارير لتشغيلها وإرسالها بالبريد"),
            new(ReportingPermissions.ReadModelRebuild, Code, "Rebuild the pre-aggregated report data", "إعادة بناء بيانات التقارير المجمعة", IsSensitive: true),
        ],
        Menu:
        [
            new("reporting.dashboards", "Dashboards", "لوحات المعلومات", "/dashboards", "overview", 1, ReportingPermissions.DashboardRead),
            new("reporting.sales", "Sales reports", "تقارير المبيعات", "/reports/sales", "reports", 10, ReportingPermissions.SalesRead, "sales"),
            new("reporting.purchasing", "Purchasing reports", "تقارير المشتريات", "/reports/purchasing", "reports", 20, ReportingPermissions.PurchasingRead, "procurement"),
            new("reporting.inventory", "Inventory reports", "تقارير المخزون", "/reports/inventory", "reports", 30, ReportingPermissions.InventoryRead, "inventory"),
            new("reporting.manufacturing", "Manufacturing reports", "تقارير التصنيع", "/reports/manufacturing", "reports", 40, ReportingPermissions.ManufacturingRead,
                "manufacturing"),
            new("reporting.tax", "Tax reports", "تقارير الضرائب", "/reports/tax", "reports", 50, ReportingPermissions.TaxRead, "accounting"),
            new("reporting.schedules", "Scheduled reports", "التقارير المجدولة", "/reports/schedules", "reports", 60, ReportingPermissions.ScheduleManage),
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
        services.AddScoped<IReportArea, TaxReports>();
        services.AddScoped<ReportService>();
        services.AddScoped<DocumentPrinting>();
        services.AddScoped<DashboardAccess>();
        services.AddScoped<ReadModelReports>();
        services.AddScoped<ReadModelProjector>();
        services.AddScoped<IReportFileRenderer, ReportFileRenderer>();
        services.AddScoped<ReportScheduleExecutor>();
        var role = configuration["Erp:Role"] ?? "all";
        if (role is "worker" or "all")
        {
            services.AddHostedService<ReportScheduleBackgroundService>();
        }
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

        // ---- Dashboards (own or shared with one of my roles; widget data runs with the viewer's permissions) ----
        group.MapGet("/dashboards", async (IDispatcher d, CancellationToken ct) => (await d.Send(new ListDashboardsQuery(), ct)).ToHttpResult())
            .RequirePermission(ReportingPermissions.DashboardRead);
        group.MapGet("/dashboards/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new GetDashboardQuery(id), ct)).ToHttpResult())
            .RequirePermission(ReportingPermissions.DashboardRead);
        group.MapPost("/dashboards", async (DashboardRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new SaveDashboardCommand(null, body.Name, body.Description, body.SharedWithRoleIds, body.Widgets ?? []), ct))
                .ToHttpResult(x => Results.Created($"/api/v1/reports/dashboards/{x.Id}", x)))
            .RequirePermission(ReportingPermissions.DashboardManage);
        group.MapPut("/dashboards/{id:guid}", async (Guid id, DashboardRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new SaveDashboardCommand(id, body.Name, body.Description, body.SharedWithRoleIds, body.Widgets ?? []), ct)).ToHttpResult())
            .RequirePermission(ReportingPermissions.DashboardManage);
        group.MapDelete("/dashboards/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new DeleteDashboardCommand(id), ct)).ToHttpResult(_ => Results.NoContent()))
            .RequirePermission(ReportingPermissions.DashboardManage);
        group.MapGet("/dashboards/{id:guid}/widgets/{widgetId:guid}/data", async (Guid id, Guid widgetId, string? lang, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new WidgetDataQuery(id, widgetId, lang), ct)).ToHttpResult())
            .RequirePermission(ReportingPermissions.DashboardRead);

        // ---- Scheduled reports (personal; run with the owner's permissions) ----
        group.MapGet("/schedules", async (IDispatcher d, CancellationToken ct) => (await d.Send(new ListSchedulesQuery(), ct)).ToHttpResult())
            .RequirePermission(ReportingPermissions.ScheduleManage);
        group.MapPost("/schedules", async (ScheduleInput body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CreateScheduleCommand(body), ct)).ToHttpResult(x => Results.Created($"/api/v1/reports/schedules/{x.Id}", x)))
            .RequirePermission(ReportingPermissions.ScheduleManage);
        group.MapPut("/schedules/{id:guid}", async (Guid id, ScheduleInput body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new UpdateScheduleCommand(id, body), ct)).ToHttpResult())
            .RequirePermission(ReportingPermissions.ScheduleManage);
        group.MapDelete("/schedules/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new DeleteScheduleCommand(id), ct)).ToHttpResult(_ => Results.NoContent()))
            .RequirePermission(ReportingPermissions.ScheduleManage);
        group.MapGet("/schedules/{id:guid}/runs", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new ListScheduleRunsQuery(id), ct)).ToHttpResult())
            .RequirePermission(ReportingPermissions.ScheduleManage);
        group.MapPost("/schedules/{id:guid}/run", async (Guid id, ICurrentUser user, IReportingDbContext db, ReportScheduleExecutor executor, CancellationToken ct) =>
            {
                // Run now: only the owner, through the same executor the worker uses.
                if (!await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(db.Schedules, s => s.Id == id && s.OwnerUserId == user.UserId, ct))
                {
                    return ReportingErrors.ScheduleNotFound.ToProblem();
                }

                return (await executor.RunAsync(id, ReportScheduleExecutor.ManualTrigger, ct)).ToHttpResult();
            })
            .RequirePermission(ReportingPermissions.ScheduleManage);
        group.MapGet("/schedules/{id:guid}/runs/{runId:guid}/file", async (Guid id, Guid runId, IDispatcher d, CancellationToken ct) =>
            {
                var file = await d.Send(new GetScheduleRunFileQuery(id, runId), ct);
                return file.IsSuccess ? Results.File(file.Value.Content, file.Value.ContentType, file.Value.FileName) : file.Error.ToProblem();
            })
            .RequirePermission(ReportingPermissions.ScheduleManage);

        // ---- Read models ----
        group.MapPost("/read-models/rebuild", async (RebuildReadModelsRequest body, ReadModelProjector projector, CancellationToken ct) =>
            {
                if (body.From > body.To || body.To.DayNumber - body.From.DayNumber > 3 * 366)
                {
                    return ReportingErrors.InvalidRange.ToProblem();
                }

                var sales = await projector.RefreshSalesAsync(body.From, body.To, ct);
                var stock = await projector.RefreshStockSnapshotAsync(ct);
                return Results.Ok(new RebuildReadModelsResult(body.From, body.To, sales, stock));
            })
            .RequirePermission(ReportingPermissions.ReadModelRebuild);

        group.MapGet("/documents/{documentType}/{id:guid}", async (
            string documentType, Guid id, string? lang, string? format, HttpContext http, DocumentPrinting printing, IPdfRenderer pdf, CancellationToken ct) =>
        {
            var output = (format ?? "html").Trim().ToLowerInvariant();
            if (output is not ("html" or "pdf"))
            {
                return ReportingErrors.InvalidFormat.ToProblem();
            }

            if (output == "pdf" && !pdf.IsAvailable)
            {
                return PdfNotConfigured();
            }

            var printed = await printing.PrintAsync(documentType, id, IsArabic(lang, http), ct);
            if (printed.IsFailure)
            {
                return printed.Error.ToProblem();
            }

            var doc = printed.Value;
            if (output == "pdf")
            {
                var fileName = Path.ChangeExtension(doc.FileName, ".pdf");
                http.Response.Headers.ContentDisposition = $"inline; filename=\"{fileName}\"";
                return Results.File(await pdf.RenderAsync(doc.Html, ct), PdfContentType);
            }

            http.Response.Headers.ContentDisposition = $"inline; filename=\"{doc.FileName}\"";
            return Results.Content(doc.Html, "text/html; charset=utf-8");
        });

        group.MapGet("/{area}/{report}", RunReportAsync);
    }

    private static async Task<IResult> RunReportAsync(
        string area, string report, [AsParameters] ReportParameters p, HttpContext http, ReportService reports, IDocumentRenderer renderer, IPdfRenderer pdf,
        CancellationToken ct)
    {
        var format = (p.Format ?? "json").Trim().ToLowerInvariant();
        if (!ReportService.Formats.Contains(format))
        {
            return ReportingErrors.InvalidFormat.ToProblem();
        }

        if (format == "pdf" && !pdf.IsAvailable)
        {
            return PdfNotConfigured();
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

            case "pdf":
            {
                var context = await reports.RenderContextAsync(arabic, null, ct);
                http.Response.Headers.ContentDisposition = $"inline; filename=\"{fileName}.pdf\"";
                return Results.File(await pdf.RenderAsync(renderer.RenderReport(table, context), ct), PdfContentType);
            }

            default:
                return Results.Ok(ReportService.ToDto(run.Value));
        }
    }

    private const string PdfContentType = "application/pdf";

    /// <summary>501 when no PDF engine is configured (Pdf:GotenbergUrl empty) — HTML output stays available.</summary>
    private static IResult PdfNotConfigured() => Results.Problem(
        title: "PDF output is not configured",
        detail: "This installation has no PDF engine. Use format=html, or configure Pdf:GotenbergUrl.",
        statusCode: StatusCodes.Status501NotImplemented,
        type: "https://errors.erp.local/reporting.pdf-not-configured",
        extensions: new Dictionary<string, object?> { ["code"] = "reporting.pdf-not-configured" });

    private static bool IsArabic(string? lang, HttpContext http)
    {
        var language = string.IsNullOrWhiteSpace(lang) ? http.Request.Headers.AcceptLanguage.ToString() : lang;
        return language.TrimStart().StartsWith("ar", StringComparison.OrdinalIgnoreCase);
    }
}
