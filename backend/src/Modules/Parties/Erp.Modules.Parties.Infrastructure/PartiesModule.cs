using Erp.Application;
using Erp.Application.Messaging;
using Erp.Application.Paging;
using Erp.Infrastructure;
using Erp.Infrastructure.Outbox;
using Erp.Modules.Parties.Application;
using Erp.Modules.Parties.Contracts;
using Erp.SharedKernel.Results;
using Erp.Web.Http;
using Erp.Web.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Modules.Parties.Infrastructure;

public sealed record PaymentTermsRequest(string? Code, string NameEn, string NameAr, IReadOnlyList<InstallmentInput> Installments);

public sealed record CreateCustomerRequest(string Code, string Kind, string NameEn, string NameAr, string? TaxRegistrationNo, string Currency,
    Guid? CustomerGroupId, Guid? PriceListId, Guid? PaymentTermsId, string? PostingGroup);

public sealed record UpdateCustomerRequest(string Kind, string NameEn, string NameAr, string? TaxRegistrationNo, string Currency,
    Guid? CustomerGroupId, Guid? PriceListId, Guid? PaymentTermsId, string? PostingGroup);

public sealed record CreditProfileRequest(decimal CreditLimit, string CreditAction, bool OnHold);

public sealed record CreateSupplierRequest(string Code, string NameEn, string NameAr, string? TaxRegistrationNo, string Currency, Guid? PaymentTermsId, string? PostingGroup);

public sealed record UpdateSupplierRequest(string NameEn, string NameAr, string? TaxRegistrationNo, string Currency, Guid? PaymentTermsId, string? PostingGroup);

public sealed record ContactRequest(string Name, string? Phone, string? Email, string? Role);

public sealed record AddressRequest(string Type, string Line1, string? Line2, string City, string? Region, string? PostalCode, string Country, bool IsDefault);

public sealed class PartiesModule : IErpModule
{
    public ModuleManifest Manifest { get; } = new(
        "parties",
        "Customers & suppliers",
        "العملاء والموردون",
        IsCore: true,
        DependsOn: [],
        Permissions:
        [
            new(PartiesPermissions.SetupRead, "parties", "View payment terms and customer groups", "عرض شروط الدفع ومجموعات العملاء"),
            new(PartiesPermissions.SetupManage, "parties", "Change payment terms and customer groups", "تعديل شروط الدفع ومجموعات العملاء"),
            new(PartiesPermissions.CustomerRead, "parties", "View customers", "عرض العملاء"),
            new(PartiesPermissions.CustomerManage, "parties", "Create and change customers", "إنشاء العملاء وتعديلهم"),
            new(PartiesPermissions.CustomerCredit, "parties", "Change customer credit limits and holds", "تعديل حدود ائتمان العملاء وإيقافهم", IsSensitive: true),
            new(PartiesPermissions.SupplierRead, "parties", "View suppliers", "عرض الموردين"),
            new(PartiesPermissions.SupplierManage, "parties", "Create and change suppliers", "إنشاء الموردين وتعديلهم"),
        ],
        Menu:
        [
            new("parties.customers", "Customers", "العملاء", "/customers", "master-data", 20, PartiesPermissions.CustomerRead),
            new("parties.suppliers", "Suppliers", "الموردون", "/suppliers", "master-data", 21, PartiesPermissions.SupplierRead),
            new("parties.payment-terms", "Payment terms", "شروط الدفع", "/settings/payment-terms", "master-data", 22, PartiesPermissions.SetupRead),
        ]);

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddErpDbContext<PartiesDbContext>(PartiesDbContext.SchemaName, order: 50);
        services.AddScoped<IPartiesDbContext>(sp => sp.GetRequiredService<PartiesDbContext>());
        services.AddScoped<IPartyQueries, PartyQueries>();
        services.AddScoped<IPartyReportQueries, PartyReportQueries>();
        services.AddHandlersFromAssembly(typeof(IPartiesDbContext).Assembly);
        services.AddSingleton(new EventAssembly(typeof(Domain.Customer).Assembly));
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/parties").WithTags("Parties").RequireAuthorization().RequireCompany();

        // ---- Payment terms & customer groups ----
        group.MapGet("/payment-terms", async (IDispatcher d, CancellationToken ct) => (await d.Send(new ListPaymentTermsQuery(), ct)).ToHttpResult())
            .RequirePermission(PartiesPermissions.SetupRead);
        group.MapGet("/payment-terms/{id:guid}", async (Guid id, HttpContext http, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new GetPaymentTermsQuery(id), ct)).ToHttpResult(t => WithETag(http, t.Version, t)))
            .RequirePermission(PartiesPermissions.SetupRead);
        group.MapPost("/payment-terms", async (PaymentTermsRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CreatePaymentTermsCommand(body.Code ?? string.Empty, body.NameEn, body.NameAr, body.Installments ?? []), ct))
                .ToHttpResult(t => Results.Created($"/api/v1/parties/payment-terms/{t.Id}", t)))
            .RequirePermission(PartiesPermissions.SetupManage);
        group.MapPut("/payment-terms/{id:guid}", async (Guid id, PaymentTermsRequest body, HttpContext http, IDispatcher d, CancellationToken ct) =>
                await WithIfMatch(http, async version => (await d.Send(new UpdatePaymentTermsCommand(id, body.NameEn, body.NameAr, body.Installments ?? [], version), ct))
                    .ToHttpResult(t => WithETag(http, t.Version, t))))
            .RequirePermission(PartiesPermissions.SetupManage);
        group.MapGet("/customer-groups", async (IDispatcher d, CancellationToken ct) => (await d.Send(new ListCustomerGroupsQuery(), ct)).ToHttpResult())
            .RequirePermission(PartiesPermissions.SetupRead);
        group.MapPost("/customer-groups", async (CreateCustomerGroupCommand body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(body, ct)).ToHttpResult(g => Results.Created($"/api/v1/parties/customer-groups/{g.Id}", g)))
            .RequirePermission(PartiesPermissions.SetupManage);

        // ---- Customers ----
        group.MapGet("/customers", async (int? page, int? pageSize, string? search, bool? includeInactive, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListCustomersQuery(new PageRequest(page ?? 1, pageSize ?? 50), search, includeInactive ?? false), ct)).ToHttpResult())
            .RequirePermission(PartiesPermissions.CustomerRead);
        group.MapGet("/customers/{id:guid}", async (Guid id, HttpContext http, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new GetCustomerQuery(id), ct)).ToHttpResult(c => WithETag(http, c.Version, c)))
            .RequirePermission(PartiesPermissions.CustomerRead);
        group.MapPost("/customers", async (CreateCustomerRequest b, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CreateCustomerCommand(b.Code, new CustomerInput(b.Kind, b.NameEn, b.NameAr, b.TaxRegistrationNo, b.Currency,
                    b.CustomerGroupId, b.PriceListId, b.PaymentTermsId, b.PostingGroup)), ct))
                .ToHttpResult(c => Results.Created($"/api/v1/parties/customers/{c.Id}", c)))
            .RequirePermission(PartiesPermissions.CustomerManage);
        group.MapPost("/customers/walk-in", async (IDispatcher d, CancellationToken ct) => (await d.Send(new EnsureWalkInCustomerCommand(), ct)).ToHttpResult())
            .RequirePermission(PartiesPermissions.CustomerRead);
        group.MapPut("/customers/{id:guid}", async (Guid id, UpdateCustomerRequest b, HttpContext http, IDispatcher d, CancellationToken ct) =>
                await WithIfMatch(http, async version => (await d.Send(new UpdateCustomerCommand(id, new CustomerInput(b.Kind, b.NameEn, b.NameAr,
                        b.TaxRegistrationNo, b.Currency, b.CustomerGroupId, b.PriceListId, b.PaymentTermsId, b.PostingGroup), version), ct))
                    .ToHttpResult(c => WithETag(http, c.Version, c))))
            .RequirePermission(PartiesPermissions.CustomerManage);
        group.MapPut("/customers/{id:guid}/credit-profile", async (Guid id, CreditProfileRequest b, HttpContext http, IDispatcher d, CancellationToken ct) =>
                await WithIfMatch(http, async version => (await d.Send(new SetCreditProfileCommand(id, b.CreditLimit, b.CreditAction, b.OnHold, version), ct))
                    .ToHttpResult(c => WithETag(http, c.Version, c))))
            .RequirePermission(PartiesPermissions.CustomerCredit);
        group.MapPost("/customers/{id:guid}/deactivate", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new DeactivatePartyCommand(PartyKind.Customer, id), ct)).ToHttpResult(_ => Results.NoContent()))
            .RequirePermission(PartiesPermissions.CustomerManage);
        MapContactsAndAddresses(group, "customers", PartyKind.Customer, PartiesPermissions.CustomerManage);

        // ---- Suppliers ----
        group.MapGet("/suppliers", async (int? page, int? pageSize, string? search, bool? includeInactive, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListSuppliersQuery(new PageRequest(page ?? 1, pageSize ?? 50), search, includeInactive ?? false), ct)).ToHttpResult())
            .RequirePermission(PartiesPermissions.SupplierRead);
        group.MapGet("/suppliers/{id:guid}", async (Guid id, HttpContext http, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new GetSupplierQuery(id), ct)).ToHttpResult(s => WithETag(http, s.Version, s)))
            .RequirePermission(PartiesPermissions.SupplierRead);
        group.MapPost("/suppliers", async (CreateSupplierRequest b, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CreateSupplierCommand(b.Code, new SupplierInput(b.NameEn, b.NameAr, b.TaxRegistrationNo, b.Currency, b.PaymentTermsId, b.PostingGroup)), ct))
                .ToHttpResult(s => Results.Created($"/api/v1/parties/suppliers/{s.Id}", s)))
            .RequirePermission(PartiesPermissions.SupplierManage);
        group.MapPut("/suppliers/{id:guid}", async (Guid id, UpdateSupplierRequest b, HttpContext http, IDispatcher d, CancellationToken ct) =>
                await WithIfMatch(http, async version => (await d.Send(new UpdateSupplierCommand(id,
                        new SupplierInput(b.NameEn, b.NameAr, b.TaxRegistrationNo, b.Currency, b.PaymentTermsId, b.PostingGroup), version), ct))
                    .ToHttpResult(s => WithETag(http, s.Version, s))))
            .RequirePermission(PartiesPermissions.SupplierManage);
        group.MapPost("/suppliers/{id:guid}/deactivate", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new DeactivatePartyCommand(PartyKind.Supplier, id), ct)).ToHttpResult(_ => Results.NoContent()))
            .RequirePermission(PartiesPermissions.SupplierManage);
        MapContactsAndAddresses(group, "suppliers", PartyKind.Supplier, PartiesPermissions.SupplierManage);
    }

    private static void MapContactsAndAddresses(RouteGroupBuilder group, string resource, PartyKind kind, string permission)
    {
        group.MapPost($"/{resource}/{{id:guid}}/contacts", async (Guid id, ContactRequest b, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new AddContactCommand(kind, id, b.Name, b.Phone, b.Email, b.Role), ct))
                .ToHttpResult(c => Results.Created($"/api/v1/parties/{resource}/{id}/contacts/{c.Id}", c)))
            .RequirePermission(permission);
        group.MapDelete($"/{resource}/{{id:guid}}/contacts/{{contactId:guid}}", async (Guid id, Guid contactId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new RemoveContactCommand(kind, id, contactId), ct)).ToHttpResult(_ => Results.NoContent()))
            .RequirePermission(permission);
        group.MapPost($"/{resource}/{{id:guid}}/addresses", async (Guid id, AddressRequest b, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new AddAddressCommand(kind, id, b.Type, b.Line1, b.Line2, b.City, b.Region, b.PostalCode, b.Country, b.IsDefault), ct))
                .ToHttpResult(a => Results.Created($"/api/v1/parties/{resource}/{id}/addresses/{a.Id}", a)))
            .RequirePermission(permission);
        group.MapDelete($"/{resource}/{{id:guid}}/addresses/{{addressId:guid}}", async (Guid id, Guid addressId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new RemoveAddressCommand(kind, id, addressId), ct)).ToHttpResult(_ => Results.NoContent()))
            .RequirePermission(permission);
    }

    private static IResult WithETag<T>(HttpContext http, string version, T body)
    {
        http.Response.Headers.ETag = $"\"{version}\"";
        return Results.Ok(body);
    }

    private static async Task<IResult> WithIfMatch(HttpContext http, Func<byte[], Task<IResult>> action)
    {
        var ifMatch = ConcurrencyHeaders.ReadIfMatch(http.Request);
        return ifMatch.IsFailure ? ifMatch.Error.ToProblem() : await action(ifMatch.Value);
    }
}
