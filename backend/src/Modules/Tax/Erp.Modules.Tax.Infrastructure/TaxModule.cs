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
        ],
        Menu:
        [
            new("tax.codes", "Taxes", "الضرائب", "/settings/taxes", "administration", 40, TaxPermissions.Read),
            new("tax.currencies", "Currencies & rates", "العملات وأسعار الصرف", "/settings/currencies", "administration", 41, TaxPermissions.Read),
        ]);

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddErpDbContext<TaxDbContext>(TaxDbContext.SchemaName, order: 60);
        services.AddScoped<ITaxDbContext>(sp => sp.GetRequiredService<TaxDbContext>());
        services.AddScoped<ITaxCalculator, TaxCalculator>();
        services.AddScoped<IExchangeRateProvider, ExchangeRateProvider>();
        services.AddScoped<ICurrencyQueries, CurrencyQueries>();
        services.AddHandlersFromAssembly(typeof(ITaxDbContext).Assembly);
        services.AddSingleton(new EventAssembly(typeof(Domain.TaxCode).Assembly));
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
    }
}
