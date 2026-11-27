using Erp.Application;
using Erp.Application.Messaging;
using Erp.Application.Paging;
using Erp.Infrastructure;
using Erp.Infrastructure.Outbox;
using Erp.Modules.Import.Application;
using Erp.Modules.Import.Contracts;
using Erp.SharedKernel.Results;
using Erp.Web.Http;
using Erp.Web.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Modules.Import.Infrastructure;

public sealed class ImportModule : IErpModule
{
    public const string Code = "import";

    public ModuleManifest Manifest { get; } = new(
        Code,
        "Data import",
        "استيراد البيانات",
        IsCore: true,
        DependsOn: ["accounting", "catalog", "parties", "inventory", "finance"],
        Permissions:
        [
            new(ImportPermissions.Read, Code, "View import jobs, their errors and the import templates", "عرض عمليات الاستيراد وأخطائها وقوالب الاستيراد"),
            new(ImportPermissions.Write, Code, "Upload, validate and commit imports (the target module's own permission is also required)",
                "رفع ملفات الاستيراد والتحقق منها واعتمادها (تلزم أيضاً صلاحية الوحدة المستهدفة)", IsSensitive: true),
        ],
        Menu:
        [
            new("import.jobs", "Data import", "استيراد البيانات", "/settings/import", "administration", 40, ImportPermissions.Read),
        ]);

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddErpDbContext<ImportDbContext>(ImportDbContext.SchemaName, order: 170);
        services.AddScoped<IImportDbContext>(sp => sp.GetRequiredService<ImportDbContext>());
        services.AddSingleton<IImportFileReader, ImportFileReader>();
        services.AddSingleton<IImportTemplateWriter, XlsxImportTemplateWriter>();
        services.AddScoped<IImportSavepoints, SqlImportSavepoints>();
        services.AddScoped<ImportRunner>();
        services.AddScoped<ImportAccess>();
        services.AddScoped<ImportDefinitionRegistry>();
        services.AddScoped<IImportDefinition, ChartOfAccountsImport>();
        services.AddScoped<IImportDefinition, ProductsImport>();
        services.AddScoped<IImportDefinition, CustomersImport>();
        services.AddScoped<IImportDefinition, SuppliersImport>();
        services.AddScoped<IImportDefinition, OpeningStockImport>();
        services.AddScoped<IImportDefinition, OpenReceivablesImport>();
        services.AddScoped<IImportDefinition, OpenPayablesImport>();
        services.AddScoped<IImportDefinition, GlOpeningBalancesImport>();
        services.AddHandlersFromAssembly(typeof(IImportDbContext).Assembly);
        services.AddSingleton(new EventAssembly(typeof(Domain.ImportJob).Assembly));
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var g = endpoints.MapGroup("/api/v1/import").WithTags("Data import").RequireAuthorization().RequireCompany();

        g.MapGet("/types", async (IDispatcher d, CancellationToken ct) => (await d.Send(new ListImportTypesQuery(), ct)).ToHttpResult())
            .RequirePermission(ImportPermissions.Read);
        g.MapGet("/types/{type}/template", async (string type, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new GetImportTemplateQuery(type), ct)).ToHttpResult(t => Results.File(t.Content, XlsxImportTemplateWriter.ContentType, t.FileName)))
            .RequirePermission(ImportPermissions.Read);

        g.MapGet("/jobs", async (int? page, int? pageSize, string? type, string? status, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListImportJobsQuery(new PageRequest(page ?? 1, pageSize ?? 50), type, status), ct)).ToHttpResult())
            .RequirePermission(ImportPermissions.Read);
        g.MapGet("/jobs/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new GetImportJobQuery(id), ct)).ToHttpResult())
            .RequirePermission(ImportPermissions.Read);

        // multipart/form-data: file, type, legalEntityId (optional), openingDate (optional, yyyy-MM-dd).
        g.MapPost("/jobs", async (HttpRequest request, IDispatcher d, CancellationToken ct) =>
            {
                if (!request.HasFormContentType)
                {
                    return Error.Validation("import.multipart-required", "Upload the file as multipart/form-data (fields: file, type).").ToProblem();
                }

                var form = await request.ReadFormAsync(ct);
                var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
                if (file is null)
                {
                    return Error.Validation("import.file-required", "The 'file' field is required.").ToProblem();
                }

                Guid? legalEntityId = null;
                if (!string.IsNullOrWhiteSpace(form["legalEntityId"]))
                {
                    if (!Guid.TryParse(form["legalEntityId"], out var parsed))
                    {
                        return Error.Validation("import.invalid-legal-entity", "legalEntityId is not a valid id.").ToProblem();
                    }

                    legalEntityId = parsed;
                }

                DateOnly? openingDate = null;
                if (!string.IsNullOrWhiteSpace(form["openingDate"]))
                {
                    if (!DateOnly.TryParseExact(form["openingDate"].ToString(), "yyyy-MM-dd", out var date))
                    {
                        return Error.Validation("import.invalid-opening-date", "openingDate must be yyyy-MM-dd.").ToProblem();
                    }

                    openingDate = date;
                }

                using var buffer = new MemoryStream();
                await file.CopyToAsync(buffer, ct);
                var result = await d.Send(new UploadImportCommand(form["type"].ToString(), file.FileName, buffer.ToArray(), legalEntityId, openingDate), ct);
                return result.ToHttpResult(j => Results.Created($"/api/v1/import/jobs/{j.Id}", j));
            })
            .DisableAntiforgery()
            .RequirePermission(ImportPermissions.Write);

        g.MapPost("/jobs/{id:guid}/validate", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new ValidateImportCommand(id), ct)).ToHttpResult())
            .RequirePermission(ImportPermissions.Write);
        g.MapPost("/jobs/{id:guid}/commit", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new CommitImportCommand(id), ct)).ToHttpResult())
            .RequirePermission(ImportPermissions.Write).RequireIdempotency();
    }
}
