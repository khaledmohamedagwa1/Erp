using Erp.Application.Messaging;
using Erp.Modules.Accounting.Contracts;
using Erp.Modules.Accounting.Domain;
using Erp.SharedKernel.Domain;
using Erp.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Accounting.Application;

public sealed record OpeningBalanceLineDto(int LineNo, Guid AccountId, string AccountCode, decimal Debit, decimal Credit, string? Description);

public sealed record OpeningBalanceDto(Guid Id, Guid LegalEntityId, DateOnly OpeningDate, string Description, string Status, decimal TotalDebit,
    decimal TotalCredit, decimal EquityDifference, Guid? JournalId, IReadOnlyList<OpeningBalanceLineDto> Lines);

public sealed record OpeningBalanceLineRequest(Guid AccountId, decimal Debit, decimal Credit, string? Description);

internal static class OpeningBalanceMapping
{
    public static async Task<OpeningBalanceDto> ToDtoAsync(IAccountingDbContext db, OpeningBalance doc, CancellationToken ct)
    {
        var ids = doc.Lines.Select(l => l.AccountId).Distinct().ToList();
        var codes = await db.Accounts.AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Code, ct);
        return new OpeningBalanceDto(doc.Id, doc.LegalEntityId, doc.OpeningDate, doc.Description, doc.Status.ToString(), doc.TotalDebit, doc.TotalCredit,
            doc.EquityDifference, doc.JournalId,
            doc.Lines.Select(l => new OpeningBalanceLineDto(l.LineNo, l.AccountId, codes.GetValueOrDefault(l.AccountId, "?"), l.Debit, l.Credit, l.Description)).ToList());
    }
}

public sealed record CreateOpeningBalanceCommand(Guid LegalEntityId, DateOnly OpeningDate, string? Description, IReadOnlyList<OpeningBalanceLineRequest> Lines)
    : ICommand<OpeningBalanceDto>;

public sealed class CreateOpeningBalanceValidator : AbstractValidator<CreateOpeningBalanceCommand>
{
    public CreateOpeningBalanceValidator()
    {
        RuleFor(x => x.LegalEntityId).NotEmpty();
        RuleFor(x => x.Description).MaximumLength(500);
        RuleFor(x => x.Lines).NotEmpty();
        RuleForEach(x => x.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.AccountId).NotEmpty();
            line.RuleFor(l => l).Must(l => l.Debit >= 0 && l.Credit >= 0 && (l.Debit > 0) != (l.Credit > 0))
                .WithMessage("Each line needs a positive debit or a positive credit, not both.");
        });
    }
}

public sealed class CreateOpeningBalanceHandler(IAccountingDbContext db, CompanyLegalEntities legalEntities)
    : ICommandHandler<CreateOpeningBalanceCommand, OpeningBalanceDto>
{
    public async Task<Result<OpeningBalanceDto>> Handle(CreateOpeningBalanceCommand command, CancellationToken cancellationToken)
    {
        var le = await legalEntities.GetAsync(command.LegalEntityId, cancellationToken);
        if (le.IsFailure)
        {
            return le.Error;
        }

        var ids = command.Lines.Select(l => l.AccountId).Distinct().ToList();
        var accounts = await db.Accounts.Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, cancellationToken);
        if (accounts.Count != ids.Count)
        {
            return AccountingErrors.AccountNotFound;
        }

        var doc = OpeningBalance.Create(le.Value.CompanyId, command.LegalEntityId, command.OpeningDate, command.Description);
        try
        {
            foreach (var line in command.Lines)
            {
                doc.AddLine(accounts[line.AccountId], line.Debit, line.Credit, line.Description);
            }
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }

        db.OpeningBalances.Add(doc);
        await db.SaveChangesAsync(cancellationToken);
        return await OpeningBalanceMapping.ToDtoAsync(db, doc, cancellationToken);
    }
}

/// <summary>
/// Posts the opening balances as one journal (source "accounting"/OpeningBalance, purpose "opening") dated the opening date;
/// the line difference goes to the OpeningBalanceEquity role. Only one posted document per legal entity.
/// </summary>
public sealed record PostOpeningBalanceCommand(Guid OpeningBalanceId) : ICommand<OpeningBalanceDto>;

public sealed class PostOpeningBalanceHandler(IAccountingDbContext db, JournalPoster poster, CompanyLegalEntities legalEntities)
    : ICommandHandler<PostOpeningBalanceCommand, OpeningBalanceDto>
{
    public const string Purpose = "opening";

    public async Task<Result<OpeningBalanceDto>> Handle(PostOpeningBalanceCommand command, CancellationToken cancellationToken)
    {
        var doc = await db.OpeningBalances.Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == command.OpeningBalanceId, cancellationToken);
        if (doc is null)
        {
            return Error.NotFound("accounting.opening-balance-not-found", "The opening balance document was not found.");
        }

        if (doc.Status != OpeningBalanceStatus.Draft)
        {
            return AccountingErrors.NotDraft;
        }

        var le = await legalEntities.GetAsync(doc.LegalEntityId, cancellationToken);
        if (le.IsFailure)
        {
            return le.Error;
        }

        if (await db.OpeningBalances.AnyAsync(o => o.LegalEntityId == doc.LegalEntityId && o.Status == OpeningBalanceStatus.Posted, cancellationToken))
        {
            return OpeningBalance.AlreadyOpened;
        }

        JournalEntry journal;
        try
        {
            journal = JournalEntry.CreateDraft(le.Value.CompanyId, doc.LegalEntityId, doc.OpeningDate, SourceModules.Accounting,
                AccountingDocumentTypes.OpeningBalance, doc.Id, Purpose, doc.Description, le.Value.FunctionalCurrency, 1m);
            foreach (var line in doc.Lines)
            {
                journal.AddLine(line.AccountId, line.Debit, line.Credit, null, null, null, line.Description);
            }

            var difference = doc.EquityDifference;
            if (difference != 0)
            {
                var rules = await db.PostingRules.Where(r => r.Role == PostingRoles.OpeningBalanceEquity).ToListAsync(cancellationToken);
                var equity = PostingRuleResolver.Resolve(rules, PostingRoles.OpeningBalanceEquity, doc.LegalEntityId, null, null, null, null);
                if (equity is null)
                {
                    return PostingErrors.RuleMissing(PostingRoles.OpeningBalanceEquity, PostingContext.Empty);
                }

                journal.AddLine(equity.AccountId, difference < 0 ? -difference : 0m, difference > 0 ? difference : 0m, null, null, null, "Opening balance equity");
            }
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }

        db.Journals.Add(journal);
        var posted = await poster.PostDraftAsync(journal, manual: false, cancellationToken);
        if (posted.IsFailure)
        {
            JournalPoster.Detach(db, journal);
            return posted.Error;
        }

        doc.MarkPosted(journal.Id);
        await db.SaveChangesAsync(cancellationToken);
        return await OpeningBalanceMapping.ToDtoAsync(db, doc, cancellationToken);
    }
}

public sealed record ReverseOpeningBalanceCommand(Guid OpeningBalanceId, DateOnly ReversalDate) : ICommand<OpeningBalanceDto>;

public sealed class ReverseOpeningBalanceHandler(IAccountingDbContext db, JournalPoster poster, CompanyLegalEntities legalEntities)
    : ICommandHandler<ReverseOpeningBalanceCommand, OpeningBalanceDto>
{
    public async Task<Result<OpeningBalanceDto>> Handle(ReverseOpeningBalanceCommand command, CancellationToken cancellationToken)
    {
        var doc = await db.OpeningBalances.Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == command.OpeningBalanceId, cancellationToken);
        if (doc is null)
        {
            return Error.NotFound("accounting.opening-balance-not-found", "The opening balance document was not found.");
        }

        if (doc.Status != OpeningBalanceStatus.Posted || doc.JournalId is null)
        {
            return AccountingErrors.NotPosted;
        }

        if ((await legalEntities.GetAsync(doc.LegalEntityId, cancellationToken)).IsFailure)
        {
            return PostingErrors.LegalEntityNotFound;
        }

        var journal = await db.Journals.Include(j => j.Lines).FirstAsync(j => j.Id == doc.JournalId, cancellationToken);
        var reversal = await poster.ReverseAsync(journal, command.ReversalDate, cancellationToken);
        if (reversal.IsFailure)
        {
            return reversal.Error;
        }

        doc.MarkReversed();
        await db.SaveChangesAsync(cancellationToken);
        return await OpeningBalanceMapping.ToDtoAsync(db, doc, cancellationToken);
    }
}

public sealed record ListOpeningBalancesQuery(Guid? LegalEntityId) : IQuery<IReadOnlyList<OpeningBalanceDto>>;

public sealed class ListOpeningBalancesHandler(IAccountingDbContext db) : IQueryHandler<ListOpeningBalancesQuery, IReadOnlyList<OpeningBalanceDto>>
{
    public async Task<Result<IReadOnlyList<OpeningBalanceDto>>> Handle(ListOpeningBalancesQuery query, CancellationToken cancellationToken)
    {
        var source = db.OpeningBalances.AsNoTracking().Include(o => o.Lines).AsQueryable();
        if (query.LegalEntityId is { } le)
        {
            source = source.Where(o => o.LegalEntityId == le);
        }

        var docs = await source.OrderBy(o => o.OpeningDate).ToListAsync(cancellationToken);
        var result = new List<OpeningBalanceDto>();
        foreach (var doc in docs)
        {
            result.Add(await OpeningBalanceMapping.ToDtoAsync(db, doc, cancellationToken));
        }

        return result;
    }
}
