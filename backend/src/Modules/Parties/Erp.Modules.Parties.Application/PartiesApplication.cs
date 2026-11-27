using Erp.Application.Abstractions;
using Erp.Application.Messaging;
using Erp.Application.Paging;
using Erp.Modules.Organization.Contracts;
using Erp.Modules.Parties.Contracts;
using Erp.Modules.Parties.Domain;
using Erp.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Erp.Modules.Parties.Application;

public interface IPartiesDbContext
{
    DbSet<PaymentTerms> PaymentTerms { get; }

    DbSet<CustomerGroup> CustomerGroups { get; }

    DbSet<Customer> Customers { get; }

    DbSet<Supplier> Suppliers { get; }

    EntityEntry<TEntity> Entry<TEntity>(TEntity entity)
        where TEntity : class;

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

public static class PartiesErrors
{
    public static readonly Error PaymentTermsNotFound = Error.NotFound("parties.payment-terms-not-found", "The payment terms were not found.");
    public static readonly Error PaymentTermsCodeTaken = Error.Conflict("parties.payment-terms-code-taken", "Payment terms with this code already exist.");
    public static readonly Error CustomerGroupNotFound = Error.NotFound("parties.customer-group-not-found", "The customer group was not found.");
    public static readonly Error CustomerGroupCodeTaken = Error.Conflict("parties.customer-group-code-taken", "A customer group with this code already exists.");
    public static readonly Error CustomerNotFound = Error.NotFound("parties.customer-not-found", "The customer was not found.");
    public static readonly Error CustomerCodeTaken = Error.Conflict("parties.customer-code-taken", "A customer with this code already exists.");
    public static readonly Error SupplierNotFound = Error.NotFound("parties.supplier-not-found", "The supplier was not found.");
    public static readonly Error SupplierCodeTaken = Error.Conflict("parties.supplier-code-taken", "A supplier with this code already exists.");
    public static readonly Error ContactNotFound = Error.NotFound("parties.contact-not-found", "The contact was not found.");
    public static readonly Error AddressNotFound = Error.NotFound("parties.address-not-found", "The address was not found.");
    public static readonly Error VersionMismatch = new("concurrency-conflict", "The record was changed by someone else. Reload and try again.", ErrorType.PreconditionFailed);
}

// ---------------- DTOs ----------------
public sealed record InstallmentDto(decimal Percent, int Days, string Basis);

public sealed record PaymentTermsDto(Guid Id, string Code, string NameEn, string NameAr, bool IsActive, IReadOnlyList<InstallmentDto> Installments, string Version);

public sealed record CustomerGroupDto(Guid Id, string Code, string NameEn, string NameAr, Guid? DefaultPriceListId, Guid? DefaultPaymentTermsId, string PostingGroup);

public sealed record ContactDto(Guid Id, string Name, string? Phone, string? Email, string? Role);

public sealed record AddressDto(Guid Id, string Type, string Line1, string? Line2, string City, string? Region, string? PostalCode, string Country, bool IsDefault);

public sealed record CreditProfileDto(decimal CreditLimit, string CreditAction, bool OnHold);

public sealed record CustomerListItemDto(Guid Id, string Code, string NameEn, string NameAr, string Kind, string Currency, bool IsActive, bool IsWalkIn);

public sealed record CustomerDto(
    Guid Id, string Code, string Kind, string NameEn, string NameAr, string? TaxRegistrationNo, string Currency,
    Guid? CustomerGroupId, Guid? PriceListId, Guid? PaymentTermsId, string PostingGroup, CreditProfileDto Credit,
    bool IsActive, bool IsWalkIn, IReadOnlyList<ContactDto> Contacts, IReadOnlyList<AddressDto> Addresses, string Version);

public sealed record SupplierListItemDto(Guid Id, string Code, string NameEn, string NameAr, string Currency, bool IsActive);

public sealed record SupplierDto(
    Guid Id, string Code, string NameEn, string NameAr, string? TaxRegistrationNo, string Currency, Guid? PaymentTermsId,
    string PostingGroup, bool IsActive, IReadOnlyList<ContactDto> Contacts, IReadOnlyList<AddressDto> Addresses, string Version);

public enum PartyKind
{
    Customer,
    Supplier,
}

internal static class PartiesMapping
{
    public static string Version(IPartiesDbContext db, object entity) =>
        Convert.ToBase64String((byte[]?)db.Entry(entity).Property("RowVersion").CurrentValue ?? []);

    public static bool VersionMatches(IPartiesDbContext db, object entity, byte[] expected)
    {
        var current = (byte[]?)db.Entry(entity).Property("RowVersion").CurrentValue ?? [];
        if (!current.AsSpan().SequenceEqual(expected))
        {
            return false;
        }

        // Let the database enforce it too, against a concurrent update after this read.
        db.Entry(entity).Property("RowVersion").OriginalValue = expected;
        return true;
    }

    public static PaymentTermsDto ToDto(IPartiesDbContext db, PaymentTerms t) => new(
        t.Id, t.Code, t.NameEn, t.NameAr, t.IsActive,
        t.Installments.Select(i => new InstallmentDto(i.Percent, i.Days, i.Basis.ToString())).ToList(), Version(db, t));

    public static CustomerGroupDto ToDto(CustomerGroup g) =>
        new(g.Id, g.Code, g.NameEn, g.NameAr, g.DefaultPriceListId, g.DefaultPaymentTermsId, g.PostingGroup);

    public static ContactDto ToDto(PartyContact c) => new(c.Uid, c.Name, c.Phone, c.Email, c.Role);

    public static AddressDto ToDto(PartyAddress a) =>
        new(a.Uid, a.Type.ToString(), a.Line1, a.Line2, a.City, a.Region, a.PostalCode, a.Country, a.IsDefault);

    public static CustomerDto ToDto(IPartiesDbContext db, Customer c) => new(
        c.Id, c.Code, c.Kind.ToString(), c.NameEn, c.NameAr, c.TaxRegistrationNo, c.Currency, c.CustomerGroupId, c.PriceListId,
        c.PaymentTermsId, c.PostingGroup, new CreditProfileDto(c.CreditLimit, c.CreditAction.ToString(), c.OnHold),
        c.IsActive, c.IsWalkIn, c.Contacts.Select(ToDto).ToList(), c.Addresses.Select(ToDto).ToList(), Version(db, c));

    public static SupplierDto ToDto(IPartiesDbContext db, Supplier s) => new(
        s.Id, s.Code, s.NameEn, s.NameAr, s.TaxRegistrationNo, s.Currency, s.PaymentTermsId, s.PostingGroup, s.IsActive,
        s.Contacts.Select(ToDto).ToList(), s.Addresses.Select(ToDto).ToList(), Version(db, s));
}

internal static class PartiesValidators
{
    public static IRuleBuilderOptions<T, string> PartyCode<T>(this IRuleBuilder<T, string> rule) => rule.NotEmpty().MaximumLength(30);

    public static IRuleBuilderOptions<T, string> PartyName<T>(this IRuleBuilder<T, string> rule) => rule.NotEmpty().MaximumLength(200);

    public static IRuleBuilderOptions<T, string> CurrencyCode<T>(this IRuleBuilder<T, string> rule) => rule.NotEmpty().Length(3);
}

// ---------------- Payment terms ----------------
public sealed record InstallmentInput(decimal Percent, int Days, string Basis);

public sealed record CreatePaymentTermsCommand(string Code, string NameEn, string NameAr, IReadOnlyList<InstallmentInput> Installments) : ICommand<PaymentTermsDto>;

public sealed record UpdatePaymentTermsCommand(Guid Id, string NameEn, string NameAr, IReadOnlyList<InstallmentInput> Installments, byte[] ExpectedVersion)
    : ICommand<PaymentTermsDto>;

public sealed class InstallmentInputValidator : AbstractValidator<InstallmentInput>
{
    public InstallmentInputValidator()
    {
        RuleFor(x => x.Percent).GreaterThan(0).LessThanOrEqualTo(100).PrecisionScale(9, 6, true);
        RuleFor(x => x.Days).InclusiveBetween(0, 3650);
        RuleFor(x => x.Basis).Must(b => Enum.TryParse<DueDateBasis>(b, true, out _)).WithMessage("Basis must be InvoiceDate or EndOfMonth.");
    }
}

public sealed class CreatePaymentTermsValidator : AbstractValidator<CreatePaymentTermsCommand>
{
    public CreatePaymentTermsValidator()
    {
        RuleFor(x => x.Code).PartyCode();
        RuleFor(x => x.NameEn).PartyName();
        RuleFor(x => x.NameAr).PartyName();
        RuleFor(x => x.Installments).NotEmpty();
        RuleForEach(x => x.Installments).SetValidator(new InstallmentInputValidator());
        RuleFor(x => x.Installments).Must(i => i is null || i.Sum(x => x.Percent) == 100m).WithMessage("Installment percentages must add up to 100.");
    }
}

public sealed class UpdatePaymentTermsValidator : AbstractValidator<UpdatePaymentTermsCommand>
{
    public UpdatePaymentTermsValidator()
    {
        RuleFor(x => x.NameEn).PartyName();
        RuleFor(x => x.NameAr).PartyName();
        RuleFor(x => x.Installments).NotEmpty();
        RuleForEach(x => x.Installments).SetValidator(new InstallmentInputValidator());
        RuleFor(x => x.Installments).Must(i => i is null || i.Sum(x => x.Percent) == 100m).WithMessage("Installment percentages must add up to 100.");
    }
}

internal static class InstallmentFactory
{
    public static IEnumerable<PaymentInstallment> From(IEnumerable<InstallmentInput> inputs) =>
        inputs.Select(i => new PaymentInstallment(i.Percent, i.Days, Enum.Parse<DueDateBasis>(i.Basis, true)));
}

public sealed class CreatePaymentTermsHandler(IPartiesDbContext db, ITenantContext tenant) : ICommandHandler<CreatePaymentTermsCommand, PaymentTermsDto>
{
    public async Task<Result<PaymentTermsDto>> Handle(CreatePaymentTermsCommand command, CancellationToken cancellationToken)
    {
        var code = command.Code.Trim().ToUpperInvariant();
        if (await db.PaymentTerms.AnyAsync(t => t.Code == code, cancellationToken))
        {
            return PartiesErrors.PaymentTermsCodeTaken;
        }

        var terms = Domain.PaymentTerms.Create(tenant.CompanyId!.Value, code, command.NameEn, command.NameAr, InstallmentFactory.From(command.Installments));
        db.PaymentTerms.Add(terms);
        await db.SaveChangesAsync(cancellationToken);
        return PartiesMapping.ToDto(db, terms);
    }
}

public sealed class UpdatePaymentTermsHandler(IPartiesDbContext db) : ICommandHandler<UpdatePaymentTermsCommand, PaymentTermsDto>
{
    public async Task<Result<PaymentTermsDto>> Handle(UpdatePaymentTermsCommand command, CancellationToken cancellationToken)
    {
        var terms = await db.PaymentTerms.FirstOrDefaultAsync(t => t.Id == command.Id, cancellationToken);
        if (terms is null)
        {
            return PartiesErrors.PaymentTermsNotFound;
        }

        if (!PartiesMapping.VersionMatches(db, terms, command.ExpectedVersion))
        {
            return PartiesErrors.VersionMismatch;
        }

        terms.Update(command.NameEn, command.NameAr, InstallmentFactory.From(command.Installments));
        await db.SaveChangesAsync(cancellationToken);
        return PartiesMapping.ToDto(db, terms);
    }
}

public sealed record ListPaymentTermsQuery : IQuery<IReadOnlyList<PaymentTermsDto>>;

public sealed class ListPaymentTermsHandler(IPartiesDbContext db) : IQueryHandler<ListPaymentTermsQuery, IReadOnlyList<PaymentTermsDto>>
{
    public async Task<Result<IReadOnlyList<PaymentTermsDto>>> Handle(ListPaymentTermsQuery query, CancellationToken cancellationToken)
    {
        var list = await db.PaymentTerms.OrderBy(t => t.Code).ToListAsync(cancellationToken);
        return list.Select(t => PartiesMapping.ToDto(db, t)).ToList();
    }
}

public sealed record GetPaymentTermsQuery(Guid Id) : IQuery<PaymentTermsDto>;

public sealed class GetPaymentTermsHandler(IPartiesDbContext db) : IQueryHandler<GetPaymentTermsQuery, PaymentTermsDto>
{
    public async Task<Result<PaymentTermsDto>> Handle(GetPaymentTermsQuery query, CancellationToken cancellationToken)
    {
        var terms = await db.PaymentTerms.FirstOrDefaultAsync(t => t.Id == query.Id, cancellationToken);
        return terms is null ? PartiesErrors.PaymentTermsNotFound : PartiesMapping.ToDto(db, terms);
    }
}

// ---------------- Customer groups ----------------
public sealed record CreateCustomerGroupCommand(string Code, string NameEn, string NameAr, Guid? DefaultPriceListId, Guid? DefaultPaymentTermsId, string? PostingGroup)
    : ICommand<CustomerGroupDto>;

public sealed class CreateCustomerGroupValidator : AbstractValidator<CreateCustomerGroupCommand>
{
    public CreateCustomerGroupValidator()
    {
        RuleFor(x => x.Code).PartyCode();
        RuleFor(x => x.NameEn).PartyName();
        RuleFor(x => x.NameAr).PartyName();
        RuleFor(x => x.PostingGroup).MaximumLength(30);
    }
}

public sealed class CreateCustomerGroupHandler(IPartiesDbContext db, ITenantContext tenant) : ICommandHandler<CreateCustomerGroupCommand, CustomerGroupDto>
{
    public async Task<Result<CustomerGroupDto>> Handle(CreateCustomerGroupCommand command, CancellationToken cancellationToken)
    {
        var code = command.Code.Trim().ToUpperInvariant();
        if (await db.CustomerGroups.AnyAsync(g => g.Code == code, cancellationToken))
        {
            return PartiesErrors.CustomerGroupCodeTaken;
        }

        if (command.DefaultPaymentTermsId is { } termsId && !await db.PaymentTerms.AnyAsync(t => t.Id == termsId, cancellationToken))
        {
            return PartiesErrors.PaymentTermsNotFound;
        }

        var group = CustomerGroup.Create(tenant.CompanyId!.Value, code, command.NameEn, command.NameAr, command.DefaultPriceListId, command.DefaultPaymentTermsId, command.PostingGroup);
        db.CustomerGroups.Add(group);
        await db.SaveChangesAsync(cancellationToken);
        return PartiesMapping.ToDto(group);
    }
}

public sealed record ListCustomerGroupsQuery : IQuery<IReadOnlyList<CustomerGroupDto>>;

public sealed class ListCustomerGroupsHandler(IPartiesDbContext db) : IQueryHandler<ListCustomerGroupsQuery, IReadOnlyList<CustomerGroupDto>>
{
    public async Task<Result<IReadOnlyList<CustomerGroupDto>>> Handle(ListCustomerGroupsQuery query, CancellationToken cancellationToken) =>
        (await db.CustomerGroups.AsNoTracking().OrderBy(g => g.Code).ToListAsync(cancellationToken)).Select(PartiesMapping.ToDto).ToList();
}

// ---------------- Customers ----------------
public sealed record CustomerInput(
    string Kind, string NameEn, string NameAr, string? TaxRegistrationNo, string Currency, Guid? CustomerGroupId, Guid? PriceListId,
    Guid? PaymentTermsId, string? PostingGroup);

public sealed record CreateCustomerCommand(string Code, CustomerInput Details) : ICommand<CustomerDto>;

public sealed record UpdateCustomerCommand(Guid Id, CustomerInput Details, byte[] ExpectedVersion) : ICommand<CustomerDto>;

public sealed class CustomerInputValidator : AbstractValidator<CustomerInput>
{
    public CustomerInputValidator()
    {
        RuleFor(x => x.Kind).Must(k => Enum.TryParse<CustomerKind>(k, true, out _)).WithMessage("Kind must be Individual or Business.");
        RuleFor(x => x.NameEn).PartyName();
        RuleFor(x => x.NameAr).PartyName();
        RuleFor(x => x.TaxRegistrationNo).MaximumLength(50);
        RuleFor(x => x.Currency).CurrencyCode();
        RuleFor(x => x.PostingGroup).MaximumLength(30);
    }
}

public sealed class CreateCustomerValidator : AbstractValidator<CreateCustomerCommand>
{
    public CreateCustomerValidator()
    {
        RuleFor(x => x.Code).PartyCode();
        RuleFor(x => x.Details).NotNull().SetValidator(new CustomerInputValidator());
    }
}

public sealed class UpdateCustomerValidator : AbstractValidator<UpdateCustomerCommand>
{
    public UpdateCustomerValidator() => RuleFor(x => x.Details).NotNull().SetValidator(new CustomerInputValidator());
}

internal static class ReferenceChecks
{
    public static async Task<Error?> CheckCustomerReferencesAsync(IPartiesDbContext db, CustomerInput d, CancellationToken ct)
    {
        if (d.PaymentTermsId is { } termsId && !await db.PaymentTerms.AnyAsync(t => t.Id == termsId, ct))
        {
            return PartiesErrors.PaymentTermsNotFound;
        }

        if (d.CustomerGroupId is { } groupId && !await db.CustomerGroups.AnyAsync(g => g.Id == groupId, ct))
        {
            return PartiesErrors.CustomerGroupNotFound;
        }

        return null;
    }

    public static async Task<Error?> CheckPaymentTermsAsync(IPartiesDbContext db, Guid? paymentTermsId, CancellationToken ct) =>
        paymentTermsId is { } id && !await db.PaymentTerms.AnyAsync(t => t.Id == id, ct) ? PartiesErrors.PaymentTermsNotFound : null;
}

public sealed class CreateCustomerHandler(IPartiesDbContext db, ITenantContext tenant) : ICommandHandler<CreateCustomerCommand, CustomerDto>
{
    public async Task<Result<CustomerDto>> Handle(CreateCustomerCommand command, CancellationToken cancellationToken)
    {
        var code = command.Code.Trim().ToUpperInvariant();
        if (await db.Customers.AnyAsync(c => c.Code == code, cancellationToken))
        {
            return PartiesErrors.CustomerCodeTaken;
        }

        if (await ReferenceChecks.CheckCustomerReferencesAsync(db, command.Details, cancellationToken) is { } error)
        {
            return error;
        }

        var d = command.Details;
        var customer = Customer.Create(tenant.CompanyId!.Value, code, Enum.Parse<CustomerKind>(d.Kind, true), d.NameEn, d.NameAr,
            d.TaxRegistrationNo, d.Currency, d.CustomerGroupId, d.PriceListId, d.PaymentTermsId, d.PostingGroup);
        db.Customers.Add(customer);
        await db.SaveChangesAsync(cancellationToken);
        return PartiesMapping.ToDto(db, customer);
    }
}

public sealed class UpdateCustomerHandler(IPartiesDbContext db) : ICommandHandler<UpdateCustomerCommand, CustomerDto>
{
    public async Task<Result<CustomerDto>> Handle(UpdateCustomerCommand command, CancellationToken cancellationToken)
    {
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == command.Id, cancellationToken);
        if (customer is null)
        {
            return PartiesErrors.CustomerNotFound;
        }

        if (!PartiesMapping.VersionMatches(db, customer, command.ExpectedVersion))
        {
            return PartiesErrors.VersionMismatch;
        }

        if (await ReferenceChecks.CheckCustomerReferencesAsync(db, command.Details, cancellationToken) is { } error)
        {
            return error;
        }

        var d = command.Details;
        customer.Update(Enum.Parse<CustomerKind>(d.Kind, true), d.NameEn, d.NameAr, d.TaxRegistrationNo, d.Currency,
            d.CustomerGroupId, d.PriceListId, d.PaymentTermsId, d.PostingGroup);
        await db.SaveChangesAsync(cancellationToken);
        return PartiesMapping.ToDto(db, customer);
    }
}

public sealed record SetCreditProfileCommand(Guid CustomerId, decimal CreditLimit, string CreditAction, bool OnHold, byte[] ExpectedVersion) : ICommand<CustomerDto>;

public sealed class SetCreditProfileValidator : AbstractValidator<SetCreditProfileCommand>
{
    public SetCreditProfileValidator()
    {
        RuleFor(x => x.CreditLimit).GreaterThanOrEqualTo(0).PrecisionScale(19, 4, true);
        RuleFor(x => x.CreditAction).Must(a => Enum.TryParse<CreditAction>(a, true, out _)).WithMessage("Credit action must be Warn, RequireApproval or Block.");
    }
}

public sealed class SetCreditProfileHandler(IPartiesDbContext db) : ICommandHandler<SetCreditProfileCommand, CustomerDto>
{
    public async Task<Result<CustomerDto>> Handle(SetCreditProfileCommand command, CancellationToken cancellationToken)
    {
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == command.CustomerId, cancellationToken);
        if (customer is null)
        {
            return PartiesErrors.CustomerNotFound;
        }

        if (!PartiesMapping.VersionMatches(db, customer, command.ExpectedVersion))
        {
            return PartiesErrors.VersionMismatch;
        }

        customer.SetCreditProfile(command.CreditLimit, Enum.Parse<CreditAction>(command.CreditAction, true), command.OnHold);
        await db.SaveChangesAsync(cancellationToken);
        return PartiesMapping.ToDto(db, customer);
    }
}

public sealed record ListCustomersQuery(PageRequest Page, string? Search, bool IncludeInactive) : IQuery<PagedResult<CustomerListItemDto>>;

public sealed class ListCustomersHandler(IPartiesDbContext db) : IQueryHandler<ListCustomersQuery, PagedResult<CustomerListItemDto>>
{
    public async Task<Result<PagedResult<CustomerListItemDto>>> Handle(ListCustomersQuery query, CancellationToken cancellationToken)
    {
        var source = db.Customers.AsNoTracking();
        if (!query.IncludeInactive)
        {
            source = source.Where(c => c.IsActive);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            source = source.Where(c => c.Code.Contains(term) || c.NameEn.Contains(term) || c.NameAr.Contains(term) || (c.TaxRegistrationNo != null && c.TaxRegistrationNo.Contains(term)));
        }

        var total = await source.CountAsync(cancellationToken);
        var items = await source.OrderBy(c => c.Code).Skip(query.Page.Skip).Take(query.Page.SafePageSize)
            .Select(c => new CustomerListItemDto(c.Id, c.Code, c.NameEn, c.NameAr, c.Kind.ToString(), c.Currency, c.IsActive, c.IsWalkIn))
            .ToListAsync(cancellationToken);
        return new PagedResult<CustomerListItemDto>(items, query.Page.SafePage, query.Page.SafePageSize, total);
    }
}

public sealed record GetCustomerQuery(Guid Id) : IQuery<CustomerDto>;

public sealed class GetCustomerHandler(IPartiesDbContext db) : IQueryHandler<GetCustomerQuery, CustomerDto>
{
    public async Task<Result<CustomerDto>> Handle(GetCustomerQuery query, CancellationToken cancellationToken)
    {
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == query.Id, cancellationToken);
        return customer is null ? PartiesErrors.CustomerNotFound : PartiesMapping.ToDto(db, customer);
    }
}

public sealed record EnsureWalkInCustomerCommand : ICommand<CustomerDto>;

public sealed class EnsureWalkInCustomerHandler(IPartiesDbContext db, ITenantContext tenant, IPartyQueries parties)
    : ICommandHandler<EnsureWalkInCustomerCommand, CustomerDto>
{
    public async Task<Result<CustomerDto>> Handle(EnsureWalkInCustomerCommand command, CancellationToken cancellationToken)
    {
        var id = await parties.GetWalkInCustomerIdAsync(tenant.CompanyId!.Value, cancellationToken);
        var customer = await db.Customers.FirstAsync(c => c.Id == id, cancellationToken);
        return PartiesMapping.ToDto(db, customer);
    }
}

// ---------------- Suppliers ----------------
public sealed record SupplierInput(string NameEn, string NameAr, string? TaxRegistrationNo, string Currency, Guid? PaymentTermsId, string? PostingGroup);

public sealed record CreateSupplierCommand(string Code, SupplierInput Details) : ICommand<SupplierDto>;

public sealed record UpdateSupplierCommand(Guid Id, SupplierInput Details, byte[] ExpectedVersion) : ICommand<SupplierDto>;

public sealed class SupplierInputValidator : AbstractValidator<SupplierInput>
{
    public SupplierInputValidator()
    {
        RuleFor(x => x.NameEn).PartyName();
        RuleFor(x => x.NameAr).PartyName();
        RuleFor(x => x.TaxRegistrationNo).MaximumLength(50);
        RuleFor(x => x.Currency).CurrencyCode();
        RuleFor(x => x.PostingGroup).MaximumLength(30);
    }
}

public sealed class CreateSupplierValidator : AbstractValidator<CreateSupplierCommand>
{
    public CreateSupplierValidator()
    {
        RuleFor(x => x.Code).PartyCode();
        RuleFor(x => x.Details).NotNull().SetValidator(new SupplierInputValidator());
    }
}

public sealed class UpdateSupplierValidator : AbstractValidator<UpdateSupplierCommand>
{
    public UpdateSupplierValidator() => RuleFor(x => x.Details).NotNull().SetValidator(new SupplierInputValidator());
}

public sealed class CreateSupplierHandler(IPartiesDbContext db, ITenantContext tenant) : ICommandHandler<CreateSupplierCommand, SupplierDto>
{
    public async Task<Result<SupplierDto>> Handle(CreateSupplierCommand command, CancellationToken cancellationToken)
    {
        var code = command.Code.Trim().ToUpperInvariant();
        if (await db.Suppliers.AnyAsync(s => s.Code == code, cancellationToken))
        {
            return PartiesErrors.SupplierCodeTaken;
        }

        var d = command.Details;
        if (await ReferenceChecks.CheckPaymentTermsAsync(db, d.PaymentTermsId, cancellationToken) is { } error)
        {
            return error;
        }

        var supplier = Supplier.Create(tenant.CompanyId!.Value, code, d.NameEn, d.NameAr, d.TaxRegistrationNo, d.Currency, d.PaymentTermsId, d.PostingGroup);
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync(cancellationToken);
        return PartiesMapping.ToDto(db, supplier);
    }
}

public sealed class UpdateSupplierHandler(IPartiesDbContext db) : ICommandHandler<UpdateSupplierCommand, SupplierDto>
{
    public async Task<Result<SupplierDto>> Handle(UpdateSupplierCommand command, CancellationToken cancellationToken)
    {
        var supplier = await db.Suppliers.FirstOrDefaultAsync(s => s.Id == command.Id, cancellationToken);
        if (supplier is null)
        {
            return PartiesErrors.SupplierNotFound;
        }

        if (!PartiesMapping.VersionMatches(db, supplier, command.ExpectedVersion))
        {
            return PartiesErrors.VersionMismatch;
        }

        var d = command.Details;
        if (await ReferenceChecks.CheckPaymentTermsAsync(db, d.PaymentTermsId, cancellationToken) is { } error)
        {
            return error;
        }

        supplier.Update(d.NameEn, d.NameAr, d.TaxRegistrationNo, d.Currency, d.PaymentTermsId, d.PostingGroup);
        await db.SaveChangesAsync(cancellationToken);
        return PartiesMapping.ToDto(db, supplier);
    }
}

public sealed record ListSuppliersQuery(PageRequest Page, string? Search, bool IncludeInactive) : IQuery<PagedResult<SupplierListItemDto>>;

public sealed class ListSuppliersHandler(IPartiesDbContext db) : IQueryHandler<ListSuppliersQuery, PagedResult<SupplierListItemDto>>
{
    public async Task<Result<PagedResult<SupplierListItemDto>>> Handle(ListSuppliersQuery query, CancellationToken cancellationToken)
    {
        var source = db.Suppliers.AsNoTracking();
        if (!query.IncludeInactive)
        {
            source = source.Where(s => s.IsActive);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            source = source.Where(s => s.Code.Contains(term) || s.NameEn.Contains(term) || s.NameAr.Contains(term) || (s.TaxRegistrationNo != null && s.TaxRegistrationNo.Contains(term)));
        }

        var total = await source.CountAsync(cancellationToken);
        var items = await source.OrderBy(s => s.Code).Skip(query.Page.Skip).Take(query.Page.SafePageSize)
            .Select(s => new SupplierListItemDto(s.Id, s.Code, s.NameEn, s.NameAr, s.Currency, s.IsActive))
            .ToListAsync(cancellationToken);
        return new PagedResult<SupplierListItemDto>(items, query.Page.SafePage, query.Page.SafePageSize, total);
    }
}

public sealed record GetSupplierQuery(Guid Id) : IQuery<SupplierDto>;

public sealed class GetSupplierHandler(IPartiesDbContext db) : IQueryHandler<GetSupplierQuery, SupplierDto>
{
    public async Task<Result<SupplierDto>> Handle(GetSupplierQuery query, CancellationToken cancellationToken)
    {
        var supplier = await db.Suppliers.FirstOrDefaultAsync(s => s.Id == query.Id, cancellationToken);
        return supplier is null ? PartiesErrors.SupplierNotFound : PartiesMapping.ToDto(db, supplier);
    }
}

// ---------------- Contacts, addresses, activation (customers and suppliers) ----------------
public sealed record AddContactCommand(PartyKind Kind, Guid PartyId, string Name, string? Phone, string? Email, string? Role) : ICommand<ContactDto>;

public sealed class AddContactValidator : AbstractValidator<AddContactCommand>
{
    public AddContactValidator()
    {
        RuleFor(x => x.Name).PartyName();
        RuleFor(x => x.Phone).MaximumLength(50);
        RuleFor(x => x.Email).MaximumLength(256).EmailAddress().When(x => !string.IsNullOrWhiteSpace(x.Email));
        RuleFor(x => x.Role).MaximumLength(100);
    }
}

public sealed record RemoveContactCommand(PartyKind Kind, Guid PartyId, Guid ContactId) : ICommand<Unit>;

public sealed record AddAddressCommand(
    PartyKind Kind, Guid PartyId, string Type, string Line1, string? Line2, string City, string? Region, string? PostalCode, string Country, bool IsDefault)
    : ICommand<AddressDto>;

public sealed class AddAddressValidator : AbstractValidator<AddAddressCommand>
{
    public AddAddressValidator()
    {
        RuleFor(x => x.Type).Must(t => Enum.TryParse<AddressType>(t, true, out _)).WithMessage("Type must be Billing or Shipping.");
        RuleFor(x => x.Line1).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Line2).MaximumLength(200);
        RuleFor(x => x.City).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Region).MaximumLength(100);
        RuleFor(x => x.PostalCode).MaximumLength(20);
        RuleFor(x => x.Country).NotEmpty().Length(2);
    }
}

public sealed record RemoveAddressCommand(PartyKind Kind, Guid PartyId, Guid AddressId) : ICommand<Unit>;

public sealed record DeactivatePartyCommand(PartyKind Kind, Guid PartyId) : ICommand<Unit>;

internal static class PartyLoader
{
    public static async Task<Party?> LoadAsync(IPartiesDbContext db, PartyKind kind, Guid id, CancellationToken ct) => kind == PartyKind.Customer
        ? await db.Customers.FirstOrDefaultAsync(c => c.Id == id, ct)
        : await db.Suppliers.FirstOrDefaultAsync(s => s.Id == id, ct);

    public static Error NotFound(PartyKind kind) => kind == PartyKind.Customer ? PartiesErrors.CustomerNotFound : PartiesErrors.SupplierNotFound;
}

public sealed class AddContactHandler(IPartiesDbContext db) : ICommandHandler<AddContactCommand, ContactDto>
{
    public async Task<Result<ContactDto>> Handle(AddContactCommand command, CancellationToken cancellationToken)
    {
        var party = await PartyLoader.LoadAsync(db, command.Kind, command.PartyId, cancellationToken);
        if (party is null)
        {
            return PartyLoader.NotFound(command.Kind);
        }

        var contact = party.AddContact(command.Name, command.Phone, command.Email, command.Role);
        await db.SaveChangesAsync(cancellationToken);
        return PartiesMapping.ToDto(contact);
    }
}

public sealed class RemoveContactHandler(IPartiesDbContext db) : ICommandHandler<RemoveContactCommand, Unit>
{
    public async Task<Result<Unit>> Handle(RemoveContactCommand command, CancellationToken cancellationToken)
    {
        var party = await PartyLoader.LoadAsync(db, command.Kind, command.PartyId, cancellationToken);
        if (party is null)
        {
            return PartyLoader.NotFound(command.Kind);
        }

        if (!party.RemoveContact(command.ContactId))
        {
            return PartiesErrors.ContactNotFound;
        }

        await db.SaveChangesAsync(cancellationToken);
        return Unit.Value;
    }
}

public sealed class AddAddressHandler(IPartiesDbContext db) : ICommandHandler<AddAddressCommand, AddressDto>
{
    public async Task<Result<AddressDto>> Handle(AddAddressCommand command, CancellationToken cancellationToken)
    {
        var party = await PartyLoader.LoadAsync(db, command.Kind, command.PartyId, cancellationToken);
        if (party is null)
        {
            return PartyLoader.NotFound(command.Kind);
        }

        var address = party.AddAddress(
            new PartyAddress(Enum.Parse<AddressType>(command.Type, true), command.Line1, command.Line2, command.City, command.Region, command.PostalCode, command.Country),
            command.IsDefault);
        await db.SaveChangesAsync(cancellationToken);
        return PartiesMapping.ToDto(address);
    }
}

public sealed class RemoveAddressHandler(IPartiesDbContext db) : ICommandHandler<RemoveAddressCommand, Unit>
{
    public async Task<Result<Unit>> Handle(RemoveAddressCommand command, CancellationToken cancellationToken)
    {
        var party = await PartyLoader.LoadAsync(db, command.Kind, command.PartyId, cancellationToken);
        if (party is null)
        {
            return PartyLoader.NotFound(command.Kind);
        }

        if (!party.RemoveAddress(command.AddressId))
        {
            return PartiesErrors.AddressNotFound;
        }

        await db.SaveChangesAsync(cancellationToken);
        return Unit.Value;
    }
}

public sealed class DeactivatePartyHandler(IPartiesDbContext db) : ICommandHandler<DeactivatePartyCommand, Unit>
{
    public async Task<Result<Unit>> Handle(DeactivatePartyCommand command, CancellationToken cancellationToken)
    {
        var party = await PartyLoader.LoadAsync(db, command.Kind, command.PartyId, cancellationToken);
        if (party is null)
        {
            return PartyLoader.NotFound(command.Kind);
        }

        party.Deactivate();
        await db.SaveChangesAsync(cancellationToken);
        return Unit.Value;
    }
}

// ---------------- Contract implementation ----------------
public sealed class PartyQueries(IPartiesDbContext db, ITenantContext tenant, IOrganizationQueries organization) : IPartyQueries
{
    public async Task<CustomerInfo?> GetCustomerAsync(Guid customerId, CancellationToken cancellationToken = default) =>
        await db.Customers.AsNoTracking().Where(c => c.Id == customerId)
            .Select(c => new CustomerInfo(c.Id, c.Code, c.NameEn, c.NameAr, c.Currency, c.PriceListId, c.PaymentTermsId, c.PostingGroup,
                c.CreditLimit, c.CreditAction.ToString(), c.OnHold, c.IsActive, c.IsWalkIn))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<SupplierInfo?> GetSupplierAsync(Guid supplierId, CancellationToken cancellationToken = default) =>
        await db.Suppliers.AsNoTracking().Where(s => s.Id == supplierId)
            .Select(s => new SupplierInfo(s.Id, s.Code, s.NameEn, s.NameAr, s.Currency, s.PaymentTermsId, s.PostingGroup, s.IsActive))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<DueInstallment>> GetDueScheduleAsync(
        Guid? paymentTermsId, DateOnly invoiceDate, decimal amount, int decimals = 2, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<PaymentInstallment> installments = [];
        if (paymentTermsId is { } id)
        {
            var terms = await db.PaymentTerms.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
                ?? throw new InvalidOperationException($"Payment terms {id} not found in the active company.");
            installments = terms.Installments.ToList();
        }

        return Domain.PaymentTerms.BuildSchedule(installments, invoiceDate, amount, decimals)
            .Select(s => new DueInstallment(s.DueDate, s.Amount)).ToList();
    }

    public async Task<Guid> GetWalkInCustomerIdAsync(Guid companyId, CancellationToken cancellationToken = default)
    {
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("Tenant context required.");

        // Explicit tenant + company predicate: the caller may not have selected this company as its active one.
        var existing = await db.Customers.IgnoreQueryFilters().AsNoTracking()
            .Where(c => EF.Property<Guid>(c, "TenantId") == tenantId && c.CompanyId == companyId && c.IsWalkIn)
            .Select(c => (Guid?)c.Id).FirstOrDefaultAsync(cancellationToken);
        if (existing is { } id)
        {
            return id;
        }

        var company = (await organization.GetCompaniesAsync([companyId], cancellationToken)).FirstOrDefault()
            ?? throw new InvalidOperationException($"Company {companyId} not found.");
        var walkIn = Customer.CreateWalkIn(companyId, company.BaseCurrency);
        db.Customers.Add(walkIn);
        await db.SaveChangesAsync(cancellationToken);
        return walkIn.Id;
    }
}
