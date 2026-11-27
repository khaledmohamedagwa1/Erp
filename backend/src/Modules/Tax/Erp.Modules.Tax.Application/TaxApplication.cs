using Erp.Application.Abstractions;
using Erp.Application.Messaging;
using Erp.Modules.Tax.Contracts;
using Erp.Modules.Tax.Domain;
using Erp.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Tax.Application;

public interface ITaxDbContext
{
    DbSet<Currency> Currencies { get; }

    DbSet<ExchangeRate> ExchangeRates { get; }

    DbSet<TaxCode> TaxCodes { get; }

    DbSet<TaxGroup> TaxGroups { get; }

    DbSet<TaxCategoryMapping> TaxCategoryMappings { get; }

    DbSet<EInvoicingSettings> EInvoicingSettings { get; }

    DbSet<EInvoiceSubmission> EInvoiceSubmissions { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

public static class TaxErrors
{
    public static readonly Error CurrencyExists = Error.Conflict("tax.currency-exists", "The currency already exists.");
    public static readonly Error CurrencyNotFound = Error.NotFound("tax.currency-not-found", "The currency is not defined.");
    public static readonly Error TaxCodeExists = Error.Conflict("tax.code-exists", "A tax code with this code already exists.");
    public static readonly Error TaxCodeNotFound = Error.NotFound("tax.code-not-found", "The tax code was not found.");
    public static readonly Error TaxGroupExists = Error.Conflict("tax.group-exists", "A tax group with this code already exists.");
    public static readonly Error TaxGroupNotFound = Error.NotFound("tax.group-not-found", "The tax group was not found.");
    public static readonly Error CategoryNotMapped = Error.NotFound("tax.category-not-mapped", "No tax group is mapped to this tax category.");

    public static Error RateMissing(string code, DateOnly date) =>
        Error.Validation("tax.rate-missing", $"Tax code {code} has no rate effective on {date:yyyy-MM-dd}.");
}

// ---------- DTOs ----------
public sealed record CurrencyDto(Guid Id, string Code, string NameEn, string NameAr, int MinorUnits, decimal CashRoundingIncrement);

public sealed record ExchangeRateDto(Guid Id, string FromCurrency, string ToCurrency, string Type, DateOnly EffectiveDate, decimal Rate);

public sealed record TaxRateDto(decimal Rate, DateOnly ValidFrom, DateOnly? ValidTo);

public sealed record TaxCodeDto(Guid Id, string Code, string NameEn, string NameAr, string Kind, bool Recoverable, string? ReportingBox, string? Jurisdiction, IReadOnlyList<TaxRateDto> Rates);

public sealed record TaxGroupComponentDto(Guid TaxCodeId, int Sequence, bool CompoundsOnPrevious);

public sealed record TaxGroupDto(Guid Id, string Code, string NameEn, string NameAr, IReadOnlyList<TaxGroupComponentDto> Components);

public sealed record TaxCategoryMappingDto(Guid Id, string CategoryCode, string Direction, Guid TaxGroupId);

internal static class TaxMapping
{
    public static CurrencyDto ToDto(Currency c) => new(c.Id, c.Code, c.NameEn, c.NameAr, c.MinorUnits, c.CashRoundingIncrement);

    public static ExchangeRateDto ToDto(ExchangeRate r) => new(r.Id, r.FromCurrency, r.ToCurrency, r.Type.ToString(), r.EffectiveDate, r.Rate);

    public static TaxCodeDto ToDto(TaxCode t) => new(t.Id, t.Code, t.NameEn, t.NameAr, t.Kind.ToString(), t.Recoverable, t.ReportingBox, t.Jurisdiction,
        t.Rates.OrderBy(r => r.ValidFrom).Select(r => new TaxRateDto(r.Rate, r.ValidFrom, r.ValidTo)).ToList());

    public static TaxGroupDto ToDto(TaxGroup g) => new(g.Id, g.Code, g.NameEn, g.NameAr,
        g.Components.Select(c => new TaxGroupComponentDto(c.TaxCodeId, c.Sequence, c.CompoundsOnPrevious)).ToList());

    public static TaxCategoryMappingDto ToDto(TaxCategoryMapping m) => new(m.Id, m.CategoryCode, m.Direction.ToString(), m.TaxGroupId);

    public static TaxResult ToContract(TaxComputation c) => new(c.Net, c.TotalTax, c.Gross, c.TotalWithholding,
        c.Lines.Select(l => new TaxLineResult(l.TaxCodeId, l.Code, (TaxKindCode)(int)l.Kind, l.RatePercent, l.Base, l.Amount, l.Recoverable)).ToList());
}

// ---------- Currencies ----------
public sealed record CreateCurrencyCommand(string Code, string NameEn, string NameAr, int MinorUnits, decimal? CashRoundingIncrement) : ICommand<CurrencyDto>;

public sealed class CreateCurrencyValidator : AbstractValidator<CreateCurrencyCommand>
{
    public CreateCurrencyValidator()
    {
        RuleFor(x => x.Code).NotEmpty().Length(3);
        RuleFor(x => x.NameEn).NotEmpty().MaximumLength(200);
        RuleFor(x => x.NameAr).NotEmpty().MaximumLength(200);
        RuleFor(x => x.MinorUnits).InclusiveBetween(0, 4);
    }
}

public sealed class CreateCurrencyHandler(ITaxDbContext db) : ICommandHandler<CreateCurrencyCommand, CurrencyDto>
{
    public async Task<Result<CurrencyDto>> Handle(CreateCurrencyCommand command, CancellationToken cancellationToken)
    {
        var code = command.Code.Trim().ToUpperInvariant();
        if (await db.Currencies.AnyAsync(c => c.Code == code, cancellationToken))
        {
            return TaxErrors.CurrencyExists;
        }

        var currency = Currency.Create(code, command.NameEn, command.NameAr, command.MinorUnits, command.CashRoundingIncrement);
        db.Currencies.Add(currency);
        await db.SaveChangesAsync(cancellationToken);
        return TaxMapping.ToDto(currency);
    }
}

public sealed record ListCurrenciesQuery : IQuery<IReadOnlyList<CurrencyDto>>;

public sealed class ListCurrenciesHandler(ITaxDbContext db) : IQueryHandler<ListCurrenciesQuery, IReadOnlyList<CurrencyDto>>
{
    public async Task<Result<IReadOnlyList<CurrencyDto>>> Handle(ListCurrenciesQuery query, CancellationToken cancellationToken) =>
        await db.Currencies.AsNoTracking().OrderBy(c => c.Code)
            .Select(c => new CurrencyDto(c.Id, c.Code, c.NameEn, c.NameAr, c.MinorUnits, c.CashRoundingIncrement))
            .ToListAsync(cancellationToken);
}

// ---------- Exchange rates ----------
public sealed record RecordExchangeRateCommand(string FromCurrency, string ToCurrency, string? Type, DateOnly EffectiveDate, decimal Rate) : ICommand<ExchangeRateDto>;

public sealed class RecordExchangeRateValidator : AbstractValidator<RecordExchangeRateCommand>
{
    public RecordExchangeRateValidator()
    {
        RuleFor(x => x.FromCurrency).NotEmpty().Length(3);
        RuleFor(x => x.ToCurrency).NotEmpty().Length(3);
        RuleFor(x => x.Rate).GreaterThan(0);
        RuleFor(x => x.Type).Must(t => t is null || Enum.TryParse<RateType>(t, true, out _)).WithMessage("Type must be Spot, Average or Closing.");
    }
}

/// <summary>Records a rate; recording again for the same pair, type and date replaces the rate (audited).</summary>
public sealed class RecordExchangeRateHandler(ITaxDbContext db) : ICommandHandler<RecordExchangeRateCommand, ExchangeRateDto>
{
    public async Task<Result<ExchangeRateDto>> Handle(RecordExchangeRateCommand command, CancellationToken cancellationToken)
    {
        var from = command.FromCurrency.Trim().ToUpperInvariant();
        var to = command.ToCurrency.Trim().ToUpperInvariant();
        var known = await db.Currencies.Where(c => c.Code == from || c.Code == to).Select(c => c.Code).ToListAsync(cancellationToken);
        if (!known.Contains(from) || !known.Contains(to))
        {
            return TaxErrors.CurrencyNotFound;
        }

        var type = command.Type is null ? RateType.Spot : Enum.Parse<RateType>(command.Type, true);
        var existing = await db.ExchangeRates.FirstOrDefaultAsync(
            r => r.FromCurrency == from && r.ToCurrency == to && r.Type == type && r.EffectiveDate == command.EffectiveDate, cancellationToken);
        if (existing is null)
        {
            existing = ExchangeRate.Create(from, to, type, command.EffectiveDate, command.Rate);
            db.ExchangeRates.Add(existing);
        }
        else
        {
            existing.ChangeRate(command.Rate);
        }

        await db.SaveChangesAsync(cancellationToken);
        return TaxMapping.ToDto(existing);
    }
}

public sealed record ListExchangeRatesQuery(string? FromCurrency, string? ToCurrency, DateOnly? DateFrom, DateOnly? DateTo) : IQuery<IReadOnlyList<ExchangeRateDto>>;

public sealed class ListExchangeRatesHandler(ITaxDbContext db) : IQueryHandler<ListExchangeRatesQuery, IReadOnlyList<ExchangeRateDto>>
{
    public async Task<Result<IReadOnlyList<ExchangeRateDto>>> Handle(ListExchangeRatesQuery query, CancellationToken cancellationToken)
    {
        var source = db.ExchangeRates.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.FromCurrency))
        {
            var from = query.FromCurrency.Trim().ToUpperInvariant();
            source = source.Where(r => r.FromCurrency == from);
        }

        if (!string.IsNullOrWhiteSpace(query.ToCurrency))
        {
            var to = query.ToCurrency.Trim().ToUpperInvariant();
            source = source.Where(r => r.ToCurrency == to);
        }

        if (query.DateFrom is { } dateFrom)
        {
            source = source.Where(r => r.EffectiveDate >= dateFrom);
        }

        if (query.DateTo is { } dateTo)
        {
            source = source.Where(r => r.EffectiveDate <= dateTo);
        }

        var rates = await source.OrderByDescending(r => r.EffectiveDate).ThenBy(r => r.FromCurrency).Take(500).ToListAsync(cancellationToken);
        return rates.Select(TaxMapping.ToDto).ToList();
    }
}

public sealed record LookupExchangeRateQuery(string FromCurrency, string ToCurrency, DateOnly Date, string? Type) : IQuery<decimal>;

public sealed class LookupExchangeRateHandler(IExchangeRateProvider rates) : IQueryHandler<LookupExchangeRateQuery, decimal>
{
    public async Task<Result<decimal>> Handle(LookupExchangeRateQuery query, CancellationToken cancellationToken)
    {
        var kind = query.Type is not null && Enum.TryParse<ExchangeRateKind>(query.Type, true, out var parsed) ? parsed : ExchangeRateKind.Spot;
        var rate = await rates.GetRateAsync(query.FromCurrency, query.ToCurrency, query.Date, kind, cancellationToken);
        return rate is null
            ? Error.NotFound("tax.exchange-rate-not-found", $"No {kind} rate for {query.FromCurrency}/{query.ToCurrency} on or before {query.Date:yyyy-MM-dd}.")
            : rate.Value;
    }
}

// ---------- Tax codes ----------
public sealed record CreateTaxCodeCommand(string Code, string NameEn, string NameAr, string Kind, bool Recoverable, string? ReportingBox, string? Jurisdiction,
    decimal Rate, DateOnly ValidFrom) : ICommand<TaxCodeDto>;

public sealed class CreateTaxCodeValidator : AbstractValidator<CreateTaxCodeCommand>
{
    public CreateTaxCodeValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(30);
        RuleFor(x => x.NameEn).NotEmpty().MaximumLength(200);
        RuleFor(x => x.NameAr).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Kind).Must(k => Enum.TryParse<TaxKind>(k, true, out _)).WithMessage("Kind must be Output, Input or Withholding.");
        RuleFor(x => x.Rate).InclusiveBetween(0, 100);
        RuleFor(x => x.ReportingBox).MaximumLength(50);
        RuleFor(x => x.Jurisdiction).MaximumLength(50);
    }
}

public sealed class CreateTaxCodeHandler(ITaxDbContext db, ITenantContext tenant) : ICommandHandler<CreateTaxCodeCommand, TaxCodeDto>
{
    public async Task<Result<TaxCodeDto>> Handle(CreateTaxCodeCommand command, CancellationToken cancellationToken)
    {
        var code = command.Code.Trim().ToUpperInvariant();
        if (await db.TaxCodes.AnyAsync(t => t.Code == code, cancellationToken))
        {
            return TaxErrors.TaxCodeExists;
        }

        var taxCode = TaxCode.Create(tenant.CompanyId!.Value, code, command.NameEn, command.NameAr, Enum.Parse<TaxKind>(command.Kind, true),
            command.Recoverable, command.ReportingBox, command.Jurisdiction, command.Rate, command.ValidFrom);
        db.TaxCodes.Add(taxCode);
        await db.SaveChangesAsync(cancellationToken);
        return TaxMapping.ToDto(taxCode);
    }
}

public sealed record AddTaxRateCommand(Guid TaxCodeId, decimal Rate, DateOnly ValidFrom, DateOnly? ValidTo) : ICommand<TaxCodeDto>;

public sealed class AddTaxRateValidator : AbstractValidator<AddTaxRateCommand>
{
    public AddTaxRateValidator() => RuleFor(x => x.Rate).InclusiveBetween(0, 100);
}

public sealed class AddTaxRateHandler(ITaxDbContext db) : ICommandHandler<AddTaxRateCommand, TaxCodeDto>
{
    public async Task<Result<TaxCodeDto>> Handle(AddTaxRateCommand command, CancellationToken cancellationToken)
    {
        var taxCode = await db.TaxCodes.Include(t => t.Rates).FirstOrDefaultAsync(t => t.Id == command.TaxCodeId, cancellationToken);
        if (taxCode is null)
        {
            return TaxErrors.TaxCodeNotFound;
        }

        taxCode.AddRate(command.Rate, command.ValidFrom, command.ValidTo);
        await db.SaveChangesAsync(cancellationToken);
        return TaxMapping.ToDto(taxCode);
    }
}

public sealed record ListTaxCodesQuery : IQuery<IReadOnlyList<TaxCodeDto>>;

public sealed class ListTaxCodesHandler(ITaxDbContext db) : IQueryHandler<ListTaxCodesQuery, IReadOnlyList<TaxCodeDto>>
{
    public async Task<Result<IReadOnlyList<TaxCodeDto>>> Handle(ListTaxCodesQuery query, CancellationToken cancellationToken)
    {
        var codes = await db.TaxCodes.AsNoTracking().Include(t => t.Rates).OrderBy(t => t.Code).ToListAsync(cancellationToken);
        return codes.Select(TaxMapping.ToDto).ToList();
    }
}

// ---------- Tax groups ----------
public sealed record TaxGroupComponentInput(Guid TaxCodeId, int Sequence, bool CompoundsOnPrevious);

public sealed record CreateTaxGroupCommand(string Code, string NameEn, string NameAr, IReadOnlyList<TaxGroupComponentInput> Components) : ICommand<TaxGroupDto>;

public sealed class CreateTaxGroupValidator : AbstractValidator<CreateTaxGroupCommand>
{
    public CreateTaxGroupValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(30);
        RuleFor(x => x.NameEn).NotEmpty().MaximumLength(200);
        RuleFor(x => x.NameAr).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Components).NotEmpty();
    }
}

public sealed class CreateTaxGroupHandler(ITaxDbContext db, ITenantContext tenant) : ICommandHandler<CreateTaxGroupCommand, TaxGroupDto>
{
    public async Task<Result<TaxGroupDto>> Handle(CreateTaxGroupCommand command, CancellationToken cancellationToken)
    {
        var code = command.Code.Trim().ToUpperInvariant();
        if (await db.TaxGroups.AnyAsync(g => g.Code == code, cancellationToken))
        {
            return TaxErrors.TaxGroupExists;
        }

        // Company query filter: only tax codes of the active company count.
        var ids = command.Components.Select(c => c.TaxCodeId).Distinct().ToList();
        if (await db.TaxCodes.CountAsync(t => ids.Contains(t.Id), cancellationToken) != ids.Count)
        {
            return TaxErrors.TaxCodeNotFound;
        }

        var group = TaxGroup.Create(tenant.CompanyId!.Value, code, command.NameEn, command.NameAr,
            command.Components.Select(c => (c.TaxCodeId, c.Sequence, c.CompoundsOnPrevious)));
        db.TaxGroups.Add(group);
        await db.SaveChangesAsync(cancellationToken);
        return TaxMapping.ToDto(group);
    }
}

public sealed record ListTaxGroupsQuery : IQuery<IReadOnlyList<TaxGroupDto>>;

public sealed class ListTaxGroupsHandler(ITaxDbContext db) : IQueryHandler<ListTaxGroupsQuery, IReadOnlyList<TaxGroupDto>>
{
    public async Task<Result<IReadOnlyList<TaxGroupDto>>> Handle(ListTaxGroupsQuery query, CancellationToken cancellationToken)
    {
        var groups = await db.TaxGroups.AsNoTracking().Include(g => g.Components).OrderBy(g => g.Code).ToListAsync(cancellationToken);
        return groups.Select(TaxMapping.ToDto).ToList();
    }
}

// ---------- Category mappings ----------
public sealed record SetCategoryMappingCommand(string CategoryCode, string Direction, Guid TaxGroupId) : ICommand<TaxCategoryMappingDto>;

public sealed class SetCategoryMappingValidator : AbstractValidator<SetCategoryMappingCommand>
{
    public SetCategoryMappingValidator()
    {
        RuleFor(x => x.CategoryCode).NotEmpty().MaximumLength(30);
        RuleFor(x => x.Direction).Must(d => Enum.TryParse<TaxDirectionKind>(d, true, out _)).WithMessage("Direction must be Sales or Purchase.");
        RuleFor(x => x.TaxGroupId).NotEmpty();
    }
}

public sealed class SetCategoryMappingHandler(ITaxDbContext db, ITenantContext tenant) : ICommandHandler<SetCategoryMappingCommand, TaxCategoryMappingDto>
{
    public async Task<Result<TaxCategoryMappingDto>> Handle(SetCategoryMappingCommand command, CancellationToken cancellationToken)
    {
        if (!await db.TaxGroups.AnyAsync(g => g.Id == command.TaxGroupId, cancellationToken))
        {
            return TaxErrors.TaxGroupNotFound;
        }

        var category = command.CategoryCode.Trim().ToUpperInvariant();
        var direction = Enum.Parse<TaxDirectionKind>(command.Direction, true);
        var mapping = await db.TaxCategoryMappings.FirstOrDefaultAsync(m => m.CategoryCode == category && m.Direction == direction, cancellationToken);
        if (mapping is null)
        {
            mapping = TaxCategoryMapping.Create(tenant.CompanyId!.Value, category, direction, command.TaxGroupId);
            db.TaxCategoryMappings.Add(mapping);
        }
        else
        {
            mapping.ChangeGroup(command.TaxGroupId);
        }

        await db.SaveChangesAsync(cancellationToken);
        return TaxMapping.ToDto(mapping);
    }
}

public sealed record ListCategoryMappingsQuery : IQuery<IReadOnlyList<TaxCategoryMappingDto>>;

public sealed class ListCategoryMappingsHandler(ITaxDbContext db) : IQueryHandler<ListCategoryMappingsQuery, IReadOnlyList<TaxCategoryMappingDto>>
{
    public async Task<Result<IReadOnlyList<TaxCategoryMappingDto>>> Handle(ListCategoryMappingsQuery query, CancellationToken cancellationToken)
    {
        var mappings = await db.TaxCategoryMappings.AsNoTracking().OrderBy(m => m.CategoryCode).ThenBy(m => m.Direction).ToListAsync(cancellationToken);
        return mappings.Select(TaxMapping.ToDto).ToList();
    }
}

// ---------- Calculation preview ----------
public sealed record CalculateTaxQuery(Guid? TaxGroupId, string? CategoryCode, string? Direction, decimal Amount, bool TaxInclusive, string Currency, DateOnly? Date)
    : IQuery<TaxResult>;

public sealed class CalculateTaxValidator : AbstractValidator<CalculateTaxQuery>
{
    public CalculateTaxValidator()
    {
        RuleFor(x => x.Currency).NotEmpty().Length(3);
        RuleFor(x => x).Must(x => x.TaxGroupId is not null || (!string.IsNullOrWhiteSpace(x.CategoryCode) && Enum.TryParse<TaxDirection>(x.Direction, true, out _)))
            .WithName("taxGroupId").WithMessage("Provide taxGroupId, or categoryCode with direction Sales or Purchase.");
    }
}

public sealed class CalculateTaxHandler(ITaxCalculator calculator, IClock clock) : IQueryHandler<CalculateTaxQuery, TaxResult>
{
    public Task<Result<TaxResult>> Handle(CalculateTaxQuery query, CancellationToken cancellationToken)
    {
        var date = query.Date ?? DateOnly.FromDateTime(clock.UtcNow);
        return query.TaxGroupId is { } groupId
            ? calculator.CalculateAsync(groupId, query.Amount, query.TaxInclusive, query.Currency, date, cancellationToken)
            : calculator.CalculateForCategoryAsync(query.CategoryCode!, Enum.Parse<TaxDirection>(query.Direction!, true), query.Amount, query.TaxInclusive, query.Currency, date, cancellationToken);
    }
}

// ---------- Contract implementations ----------
public sealed class TaxCalculator(ITaxDbContext db, ICurrencyQueries currencies) : ITaxCalculator
{
    public async Task<Result<TaxResult>> CalculateAsync(Guid taxGroupId, decimal amount, bool taxInclusive, string currency, DateOnly date, CancellationToken cancellationToken = default)
    {
        var info = await currencies.GetAsync(currency, cancellationToken);
        if (info is null)
        {
            return TaxErrors.CurrencyNotFound;
        }

        var group = await db.TaxGroups.AsNoTracking().Include(g => g.Components).FirstOrDefaultAsync(g => g.Id == taxGroupId, cancellationToken);
        if (group is null)
        {
            return TaxErrors.TaxGroupNotFound;
        }

        var codeIds = group.Components.Select(c => c.TaxCodeId).ToList();
        var codes = await db.TaxCodes.AsNoTracking().Include(t => t.Rates).Where(t => codeIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, cancellationToken);
        var inputs = new List<TaxComponentInput>();
        foreach (var component in group.Components)
        {
            if (!codes.TryGetValue(component.TaxCodeId, out var code))
            {
                return TaxErrors.TaxCodeNotFound;
            }

            var rate = code.RateOn(date);
            if (rate is null)
            {
                return TaxErrors.RateMissing(code.Code, date);
            }

            inputs.Add(new TaxComponentInput(code.Id, code.Code, code.Kind, rate.Value, component.CompoundsOnPrevious, code.Recoverable));
        }

        return TaxMapping.ToContract(TaxEngine.Compute(amount, taxInclusive, inputs, info.MinorUnits));
    }

    public async Task<Result<TaxResult>> CalculateForCategoryAsync(string taxCategoryCode, TaxDirection direction, decimal amount, bool taxInclusive, string currency, DateOnly date, CancellationToken cancellationToken = default)
    {
        var groupId = await ResolveTaxGroupAsync(taxCategoryCode, direction, cancellationToken);
        return groupId is null
            ? TaxErrors.CategoryNotMapped
            : await CalculateAsync(groupId.Value, amount, taxInclusive, currency, date, cancellationToken);
    }

    public async Task<Guid?> ResolveTaxGroupAsync(string taxCategoryCode, TaxDirection direction, CancellationToken cancellationToken = default)
    {
        var category = taxCategoryCode.Trim().ToUpperInvariant();
        var kind = (TaxDirectionKind)(int)direction;
        return await db.TaxCategoryMappings.AsNoTracking()
            .Where(m => m.CategoryCode == category && m.Direction == kind)
            .Select(m => (Guid?)m.TaxGroupId)
            .FirstOrDefaultAsync(cancellationToken);
    }
}

public sealed class ExchangeRateProvider(ITaxDbContext db) : IExchangeRateProvider
{
    public async Task<decimal?> GetRateAsync(string from, string to, DateOnly date, ExchangeRateKind kind = ExchangeRateKind.Spot, CancellationToken cancellationToken = default)
    {
        var f = from.Trim().ToUpperInvariant();
        var t = to.Trim().ToUpperInvariant();
        if (f == t)
        {
            return 1m;
        }

        var type = (RateType)(int)kind;
        var direct = await Latest(f, t, type, date, cancellationToken);
        if (direct is not null)
        {
            return direct;
        }

        var inverse = await Latest(t, f, type, date, cancellationToken);
        return inverse is null ? null : Math.Round(1m / inverse.Value, 8, MidpointRounding.AwayFromZero);
    }

    private Task<decimal?> Latest(string from, string to, RateType type, DateOnly date, CancellationToken cancellationToken) =>
        db.ExchangeRates.AsNoTracking()
            .Where(r => r.FromCurrency == from && r.ToCurrency == to && r.Type == type && r.EffectiveDate <= date)
            .OrderByDescending(r => r.EffectiveDate)
            .Select(r => (decimal?)r.Rate)
            .FirstOrDefaultAsync(cancellationToken);
}

public sealed class CurrencyQueries(ITaxDbContext db) : ICurrencyQueries
{
    public async Task<CurrencyInfo?> GetAsync(string code, CancellationToken cancellationToken = default)
    {
        var c = code.Trim().ToUpperInvariant();
        return await db.Currencies.AsNoTracking().Where(x => x.Code == c)
            .Select(x => new CurrencyInfo(x.Code, x.NameEn, x.NameAr, x.MinorUnits, x.CashRoundingIncrement))
            .FirstOrDefaultAsync(cancellationToken);
    }
}
