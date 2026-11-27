using Erp.Application.Messaging;
using Erp.Modules.Accounting.Contracts;
using Erp.Modules.Accounting.Domain;
using Erp.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Accounting.Application;

/// <summary>Resolves account codes and runs the regular accounting commands for the Import module.</summary>
public sealed class AccountingImportService(IAccountingDbContext db, IDispatcher dispatcher) : IAccountingImportService
{
    public static readonly Error Unbalanced = Error.Validation("accounting.opening-import-unbalanced", "Imported opening balances must balance (total debit = total credit).");

    public async Task<Result<Guid>> CreateAccountAsync(AccountImportRequest request, CancellationToken cancellationToken = default)
    {
        var parentCode = request.ParentCode.Trim().ToUpperInvariant();
        var parent = await db.Accounts.AsNoTracking().Where(a => a.Code == parentCode).Select(a => new { a.Id, a.Type }).FirstOrDefaultAsync(cancellationToken);
        if (parent is null)
        {
            return AccountingErrors.AccountNotFound with { Message = $"Parent account '{request.ParentCode}' was not found." };
        }

        var type = string.IsNullOrWhiteSpace(request.Type) ? parent.Type.ToString() : request.Type.Trim();
        var created = await dispatcher.Send(new CreateAccountCommand(request.Code, request.NameEn, request.NameAr, type, parent.Id, request.IsPostingAccount,
            null, request.AllowManualPosting, string.IsNullOrWhiteSpace(request.CashFlowCategory) ? null : request.CashFlowCategory.Trim()), cancellationToken);
        return created.IsFailure ? created.Error : created.Value.Id;
    }

    public async Task<Result<Guid>> PostOpeningBalancesAsync(OpeningBalanceImportRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Lines.Sum(l => l.Debit) != request.Lines.Sum(l => l.Credit))
        {
            return Unbalanced;
        }

        var codes = request.Lines.Select(l => l.AccountCode.Trim().ToUpperInvariant()).Distinct().ToList();
        var accounts = await db.Accounts.AsNoTracking().Where(a => codes.Contains(a.Code)).ToDictionaryAsync(a => a.Code.ToUpperInvariant(), cancellationToken);

        // Line-level problems are reported per line ("lines[i]", zero-based) so the importer can point at the exact row.
        var details = new Dictionary<string, string[]>();
        for (var i = 0; i < request.Lines.Count; i++)
        {
            var line = request.Lines[i];
            var code = line.AccountCode.Trim().ToUpperInvariant();
            string? problem = !accounts.TryGetValue(code, out var account) ? $"Account '{line.AccountCode}' was not found."
                : account.IsControl ? $"{OpeningBalance.ControlAccountNotAllowed.Message} ({account.Code})"
                : !account.IsPostingAccount ? $"{AccountingErrors.NotPostingAccount.Message} ({account.Code})"
                : account.Status != AccountStatus.Active ? $"{AccountingErrors.AccountInactive.Message} ({account.Code})"
                : line.Debit < 0 || line.Credit < 0 || (line.Debit > 0) == (line.Credit > 0) ? AccountingErrors.LineOneSided.Message
                : null;
            if (problem is not null)
            {
                details[$"lines[{i}]"] = [problem];
            }
        }

        if (details.Count > 0)
        {
            return Error.Validation("accounting.opening-import-invalid-lines", "Some opening balance lines are invalid.") with { Details = details };
        }

        var created = await dispatcher.Send(new CreateOpeningBalanceCommand(request.LegalEntityId, request.OpeningDate, request.Description,
            request.Lines.Select(l => new OpeningBalanceLineRequest(accounts[l.AccountCode.Trim().ToUpperInvariant()].Id, l.Debit, l.Credit, l.Description)).ToList()),
            cancellationToken);
        if (created.IsFailure)
        {
            return created.Error;
        }

        var posted = await dispatcher.Send(new PostOpeningBalanceCommand(created.Value.Id), cancellationToken);
        return posted.IsFailure ? posted.Error : posted.Value.JournalId!.Value;
    }
}
