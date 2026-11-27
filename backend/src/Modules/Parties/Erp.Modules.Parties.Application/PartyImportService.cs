using Erp.Application.Messaging;
using Erp.Modules.Parties.Contracts;
using Erp.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Parties.Application;

/// <summary>Resolves codes and runs the regular party commands, so imported parties pass the same rules as manual entry.</summary>
public sealed class PartyImportService(IPartiesDbContext db, IDispatcher dispatcher) : IPartyImportService
{
    public async Task<Result<Guid>> CreateCustomerAsync(CustomerImportRequest request, CancellationToken cancellationToken = default)
    {
        Guid? group = null;
        if (!string.IsNullOrWhiteSpace(request.CustomerGroupCode))
        {
            var code = request.CustomerGroupCode.Trim().ToUpperInvariant();
            group = await db.CustomerGroups.AsNoTracking().Where(g => g.Code == code).Select(g => (Guid?)g.Id).FirstOrDefaultAsync(cancellationToken);
            if (group is null)
            {
                return PartiesErrors.CustomerGroupNotFound with { Message = $"Customer group '{request.CustomerGroupCode}' was not found." };
            }
        }

        var terms = await TermsAsync(request.PaymentTermsCode, cancellationToken);
        if (terms.IsFailure)
        {
            return terms.Error;
        }

        var created = await dispatcher.Send(new CreateCustomerCommand(request.Code, new CustomerInput(request.Kind, request.NameEn, request.NameAr,
            request.TaxRegistrationNo, request.Currency, group, null, terms.Value, request.PostingGroup)), cancellationToken);
        if (created.IsFailure)
        {
            return created.Error;
        }

        if (request.CreditLimit is not null || !string.IsNullOrWhiteSpace(request.CreditAction))
        {
            var credit = await dispatcher.Send(new SetCreditProfileCommand(created.Value.Id, request.CreditLimit ?? 0,
                string.IsNullOrWhiteSpace(request.CreditAction) ? CreditActions.Warn : request.CreditAction.Trim(), false,
                Convert.FromBase64String(created.Value.Version)), cancellationToken);
            if (credit.IsFailure)
            {
                return credit.Error;
            }
        }

        return created.Value.Id;
    }

    public async Task<Result<Guid>> CreateSupplierAsync(SupplierImportRequest request, CancellationToken cancellationToken = default)
    {
        var terms = await TermsAsync(request.PaymentTermsCode, cancellationToken);
        if (terms.IsFailure)
        {
            return terms.Error;
        }

        var created = await dispatcher.Send(new CreateSupplierCommand(request.Code, new SupplierInput(request.NameEn, request.NameAr, request.TaxRegistrationNo,
            request.Currency, terms.Value, request.PostingGroup)), cancellationToken);
        return created.IsFailure ? created.Error : created.Value.Id;
    }

    public async Task<IReadOnlyDictionary<string, Guid>> FindCustomerIdsByCodeAsync(IReadOnlyCollection<string> codes, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(codes);
        var found = await db.Customers.AsNoTracking().Where(c => normalized.Contains(c.Code)).Select(c => new { c.Code, c.Id }).ToListAsync(cancellationToken);
        return found.ToDictionary(c => c.Code.ToUpperInvariant(), c => c.Id, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyDictionary<string, Guid>> FindSupplierIdsByCodeAsync(IReadOnlyCollection<string> codes, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(codes);
        var found = await db.Suppliers.AsNoTracking().Where(c => normalized.Contains(c.Code)).Select(c => new { c.Code, c.Id }).ToListAsync(cancellationToken);
        return found.ToDictionary(c => c.Code.ToUpperInvariant(), c => c.Id, StringComparer.OrdinalIgnoreCase);
    }

    private static List<string> Normalize(IReadOnlyCollection<string> codes) =>
        codes.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim().ToUpperInvariant()).Distinct().ToList();

    private async Task<Result<Guid?>> TermsAsync(string? termsCode, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(termsCode))
        {
            return (Guid?)null;
        }

        var code = termsCode.Trim().ToUpperInvariant();
        var terms = await db.PaymentTerms.AsNoTracking().Where(t => t.Code == code).Select(t => (Guid?)t.Id).FirstOrDefaultAsync(ct);
        return terms is null
            ? PartiesErrors.PaymentTermsNotFound with { Message = $"Payment terms '{termsCode}' were not found." }
            : Result.Success<Guid?>(terms);
    }
}
