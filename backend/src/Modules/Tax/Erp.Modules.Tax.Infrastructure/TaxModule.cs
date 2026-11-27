using Erp.Application;
using Erp.Application.Messaging;
using Erp.Infrastructure;
using Erp.Infrastructure.Outbox;
using Erp.Modules.Tax.Application;
using Erp.Modules.Tax.Contracts;
using Erp.Web.Http;
using Erp.Web.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Modules.Tax.Infrastructure;

public sealed record AddTaxRateRequest(decimal Rate, DateOnly ValidFrom, DateOnly? ValidTo);

public sealed record EgyptTemplateRequest(DateOnly? EffectiveFrom, decimal? TableTaxRate);

public sealed record EInvoicingSettingsRequest(bool Enabled, string? Provider, string? Environment);

public sealed class TaxModule : IErpModule
{
    public ModuleManifest Manifest { get; } = new(
        "tax",
        "Taxes & currencies",
        "الضرائب والعملات",
        IsCore: true,
        DependsOn: [],
        Permissions:
        [
            new(TaxPermissions.Read, "tax", "View taxes, currencies and exchange rates", "عرض الضرائب والعملات وأسعار الصرف"),
            new(TaxPermissions.Manage, "tax", "Change taxes, currencies and exchange rates", "تعديل الضرائب والعملات وأسعار الصرف", IsSensitive: true),
            new(EInvoicingPermissions.Read, "tax", "View e-invoicing settings and submissions", "عرض إعدادات وإرساليات الفاتورة الإلكترونية"),
            new(EInvoicingPermissions.Manage, "tax", "Configure e-invoicing and retry submissions", "إعداد الفاتورة الإلكترونية وإعادة الإرسال", IsSensitive: true),
        ],
        Menu:
        [
            new("tax.codes", "Taxes", "الضرائب", "/settings/taxes", "administration", 40, TaxPermissions.Read),
            new("tax.currencies", "Currencies & rates", "العملات وأسعار الصرف", "/settings/currencies", "administration", 41, TaxPermissions.Read),
            new("tax.e-invoicing", "E-invoicing", "الفاتورة الإلكترونية", "/settings/e-invoicing", "administration", 42, EInvoicingPermissions.Read),
        ]);

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddErpDbContext<TaxDbContext>(TaxDbContext.SchemaName, order: 60);
        services.AddScoped<ITaxDbContext>(sp => sp.GetRequiredService<TaxDbContext>());
        services.AddScoped<ITaxCalculator, TaxCalculator>();
        services.AddScoped<IExchangeRateProvider, ExchangeRateProvider>();
        services.AddScoped<ICurrencyQueries, CurrencyQueries>();
        services.AddScoped<ITaxCodeQueries, TaxCodeQueries>();
        services.AddScoped<EInvoiceBuilder>();

        // E-invoicing adapters. Only the mock ships; real authority adapters (ETA, ZATCA) need credentials, certificates/HSM and onboarding.
        services.AddSingleton<IEInvoicingProvider, MockEInvoicingProvider>();
        services.AddHandlersFromAssembly(typeof(ITaxDbContext).Assembly);
        services.AddSingleton(new EventAssembly(typeof(Domain.TaxCode).Assembly));
        services.AddSingleton(new EventAssembly(typeof(Erp.Modules.Accounting.Contracts.JournalPosted).Assembly));
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/tax").WithTags("Tax").RequireAuthorization();

        // Currencies and exchange rates are tenant-wide.
        group.MapGet("/currencies", async (IDispatcher d, CancellationToken ct) => (await d.Send(new ListCurrenciesQuery(), ct)).ToHttpResult())
            .RequirePermission(TaxPermissions.Read);
        group.MapPost("/currencies", async (CreateCurrencyCommand body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(body, ct)).ToHttpResult(c => Results.Created($"/api/v1/tax/currencies/{c.Code}", c)))
            .RequirePermission(TaxPermissions.Manage);
        group.MapGet("/exchange-rates", async (string? from, string? to, DateOnly? dateFrom, DateOnly? dateTo, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListExchangeRatesQuery(from, to, dateFrom, dateTo), ct)).ToHttpResult())
            .RequirePermission(TaxPermissions.Read);
        group.MapGet("/exchange-rates/lookup", async (string from, string to, DateOnly date, string? type, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new LookupExchangeRateQuery(from, to, date, type), ct)).ToHttpResult(rate => Results.Ok(new { from, to, date, rate })))
            .RequirePermission(TaxPermissions.Read);
        group.MapPost("/exchange-rates", async (RecordExchangeRateCommand body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(body, ct)).ToHttpResult())
            .RequirePermission(TaxPermissions.Manage);

        // Tax setup is per company.
        var scoped = group.MapGroup(string.Empty).RequireCompany();
        scoped.MapGet("/tax-codes", async (IDispatcher d, CancellationToken ct) => (await d.Send(new ListTaxCodesQuery(), ct)).ToHttpResult())
            .RequirePermission(TaxPermissions.Read);
        scoped.MapPost("/tax-codes", async (CreateTaxCodeCommand body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(body, ct)).ToHttpResult(t => Results.Created($"/api/v1/tax/tax-codes/{t.Id}", t)))
            .RequirePermission(TaxPermissions.Manage);
        scoped.MapPost("/tax-codes/{id:guid}/rates", async (Guid id, AddTaxRateRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new AddTaxRateCommand(id, body.Rate, body.ValidFrom, body.ValidTo), ct)).ToHttpResult())
            .RequirePermission(TaxPermissions.Manage);
        scoped.MapGet("/tax-groups", async (IDispatcher d, CancellationToken ct) => (await d.Send(new ListTaxGroupsQuery(), ct)).ToHttpResult())
            .RequirePermission(TaxPermissions.Read);
        scoped.MapPost("/tax-groups", async (CreateTaxGroupCommand body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(body, ct)).ToHttpResult(g => Results.Created($"/api/v1/tax/tax-groups/{g.Id}", g)))
            .RequirePermission(TaxPermissions.Manage);
        scoped.MapGet("/category-mappings", async (IDispatcher d, CancellationToken ct) => (await d.Send(new ListCategoryMappingsQuery(), ct)).ToHttpResult())
            .RequirePermission(TaxPermissions.Read);
        scoped.MapPut("/category-mappings", async (SetCategoryMappingCommand body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(body, ct)).ToHttpResult())
            .RequirePermission(TaxPermissions.Manage);
        scoped.MapPost("/calculate", async (CalculateTaxQuery body, IDispatcher d, CancellationToken ct) => (await d.Send(body, ct)).ToHttpResult())
            .RequirePermission(TaxPermissions.Read);
        scoped.MapPost("/templates/egypt", async (EgyptTemplateRequest? body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ApplyEgyptTemplateCommand(body?.EffectiveFrom, body?.TableTaxRate), ct)).ToHttpResult())
            .RequirePermission(TaxPermissions.Manage);

        // E-invoicing (per company).
        scoped.MapGet("/e-invoicing/settings", async (IDispatcher d, CancellationToken ct) => (await d.Send(new GetEInvoicingSettingsQuery(), ct)).ToHttpResult())
            .RequirePermission(EInvoicingPermissions.Read);
        scoped.MapPut("/e-invoicing/settings", async (EInvoicingSettingsRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new UpdateEInvoicingSettingsCommand(body.Enabled, body.Provider, body.Environment), ct)).ToHttpResult())
            .RequirePermission(EInvoicingPermissions.Manage);
        scoped.MapGet("/e-invoicing/submissions", async (string? status, Guid? sourceDocId, int? page, int? pageSize, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListEInvoiceSubmissionsQuery(status, sourceDocId, new Erp.Application.Paging.PageRequest(page ?? 1, pageSize ?? 50)), ct)).ToHttpResult())
            .RequirePermission(EInvoicingPermissions.Read);
        scoped.MapGet("/e-invoicing/submissions/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new GetEInvoiceSubmissionQuery(id), ct)).ToHttpResult())
            .RequirePermission(EInvoicingPermissions.Read);
        scoped.MapPost("/e-invoicing/submissions/{id:guid}/retry", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new RetryEInvoiceSubmissionCommand(id), ct)).ToHttpResult())
            .RequirePermission(EInvoicingPermissions.Manage);
    }
}
