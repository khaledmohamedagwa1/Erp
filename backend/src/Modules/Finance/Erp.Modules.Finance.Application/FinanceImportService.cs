using Erp.Application.Abstractions;
using Erp.Modules.Accounting.Contracts;
using Erp.Modules.Finance.Contracts;
using Erp.Modules.Finance.Domain;
using Erp.Modules.Tax.Contracts;
using Erp.SharedKernel.Domain;
using Erp.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Finance.Application;

/// <summary>
/// Opening receivables/payables: each imported document becomes one open item (single installment, explicit due date)
/// and one journal — Dr receivable / Cr opening balance equity for customers, Dr opening balance equity / Cr payable for
/// suppliers (reversed for credits). The journal is in functional currency at the document's rate, like every Finance posting.
/// </summary>
public sealed class FinanceImportService(
    IFinanceDbContext db,
    IUnitOfWork unitOfWork,
    FinanceScope scope,
    SubledgerService subledger,
    ICurrencyQueries currencies,
    IJournalPostingService journals) : IFinanceImportService
{
    public static readonly Error OpeningItemExists = Error.Conflict("finance.opening-item-exists", "This party already has an opening item with this document number.");
    public static readonly Error DueBeforeDocument = Error.Validation("finance.due-before-document", "The due date cannot be before the document date.");

    public async Task<Result<Guid>> CreateOpeningItemAsync(OpeningItemImportRequest request, CancellationToken cancellationToken = default)
    {
        if (!unitOfWork.HasActiveTransaction)
        {
            return FinanceAppErrors.TransactionRequired;
        }

        if (request.Amount == 0 || request.ExchangeRate is <= 0)
        {
            return FinanceErrors.InvalidAmount;
        }

        if (request.DueDate < request.DocumentDate)
        {
            return DueBeforeDocument;
        }

        var number = request.DocumentNumber?.Trim() ?? string.Empty;
        if (number.Length is 0 or > 50)
        {
            return Error.Validation("finance.document-number-invalid", "The document number is required (at most 50 characters).");
        }

        var docType = request.PartyType == PartyKind.Customer ? FinanceOpeningDocTypes.OpeningReceivable : FinanceOpeningDocTypes.OpeningPayable;
        if (await db.OpenItems.AnyAsync(i => i.SourceDocType == docType && i.PartyId == request.PartyId && i.DocumentNumber == number, cancellationToken))
        {
            return OpeningItemExists with { Message = $"{OpeningItemExists.Message} ({number})" };
        }

        var le = await scope.LegalEntityAsync(request.LegalEntityId, cancellationToken);
        if (le.IsFailure)
        {
            return le.Error;
        }

        var postingGroup = await subledger.PostingGroupAsync(request.PartyType, request.PartyId, cancellationToken);
        if (postingGroup is null)
        {
            return FinanceAppErrors.PartyNotFound;
        }

        var functional = le.Value.FunctionalCurrency;
        var currency = string.IsNullOrWhiteSpace(request.Currency) ? functional : request.Currency.Trim().ToUpperInvariant();
        var decimals = (await currencies.GetAsync(currency, cancellationToken))?.MinorUnits ?? 2;
        var rate = await scope.RateAsync(currency, functional, request.DocumentDate, request.ExchangeRate, cancellationToken);
        if (rate.IsFailure)
        {
            return rate.Error;
        }

        var sign = request.Amount > 0 ? 1 : -1;
        var amount = FinanceMath.Round(Math.Abs(request.Amount), decimals);
        var baseAmount = FinanceMath.ToBase(amount, rate.Value);
        var documentId = SequentialGuid.New();
        var item = OpenItem.Create(le.Value.CompanyId, le.Value.Id, request.PartyType, request.PartyId, postingGroup, SourceModules.Finance, docType, documentId,
            number, request.DocumentDate, 1, request.DueDate, currency, rate.Value, sign, amount, baseAmount);
        db.OpenItems.Add(item);

        var context = new PostingContext(PartyPostingGroup: postingGroup);
        var lines = new JournalLines();
        var isCustomer = request.PartyType == PartyKind.Customer;
        var controlRole = isCustomer ? PostingRoles.CustomerReceivable : PostingRoles.SupplierPayable;

        // Customer invoice / supplier credit increase the debit side of the control account.
        var debitControl = isCustomer == (sign > 0);
        if (debitControl)
        {
            lines.Debit(controlRole, null, baseAmount, context, request.PartyType, request.PartyId)
                .Credit(PostingRoles.OpeningBalanceEquity, null, baseAmount);
        }
        else
        {
            lines.Debit(PostingRoles.OpeningBalanceEquity, null, baseAmount)
                .Credit(controlRole, null, baseAmount, context, request.PartyType, request.PartyId);
        }

        var description = string.IsNullOrWhiteSpace(request.Description) ? $"Opening {(isCustomer ? "receivable" : "payable")} {number}" : request.Description.Trim();
        var posted = await journals.PostAsync(new JournalRequest(le.Value.Id, request.DocumentDate, SourceModules.Finance, docType, documentId, "opening",
            description, functional, 1m, lines.Lines), cancellationToken);
        if (posted.IsFailure)
        {
            db.Entry(item).State = EntityState.Detached;
            return posted.Error;
        }

        await db.SaveChangesAsync(cancellationToken);
        return item.Id;
    }
}
