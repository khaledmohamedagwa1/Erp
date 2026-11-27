using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Erp.Application.Abstractions;
using Erp.Application.Messaging;
using Erp.Application.Paging;
using Erp.Modules.Accounting.Contracts;
using Erp.Modules.Organization.Contracts;
using Erp.Modules.Parties.Contracts;
using Erp.Modules.Sales.Contracts;
using Erp.Modules.Tax.Contracts;
using Erp.Modules.Tax.Domain;
using Erp.SharedKernel.Documents;
using Erp.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Modules.Tax.Application;

public sealed record EInvoicingSettingsDto(bool Enabled, string Provider, string Environment, long LastCounter, string? LastHash, IReadOnlyList<string> AvailableProviders);

public sealed record EInvoiceSubmissionDto(Guid Id, Guid LegalEntityId, string SourceModule, string SourceDocType, Guid SourceDocId, string DocumentNumber,
    string Status, Guid Uuid, long? Counter, string? Hash, string? PreviousHash, string? Provider, string? ProviderReference, string? QrCode, string? Errors,
    int Attempts, DateTime CreatedAtUtc, DateTime? LastAttemptAtUtc, DateTime? CompletedAtUtc);

internal static class EInvoiceMapping
{
    public static EInvoiceSubmissionDto ToDto(EInvoiceSubmission s) => new(s.Id, s.LegalEntityId, s.SourceModule, s.SourceDocType, s.SourceDocId, s.DocumentNumber,
        s.Status.ToString(), s.Uuid, s.Counter, s.Hash, s.PreviousHash, s.Provider, s.ProviderReference, s.QrCode, s.Errors, s.Attempts, s.CreatedAtUtc,
        s.LastAttemptAtUtc, s.CompletedAtUtc);
}

public static class EInvoicingErrors
{
    public static readonly Error SubmissionNotFound = Error.NotFound("tax.einvoice-submission-not-found", "The e-invoice submission was not found.");

    public static Error UnknownProvider(string name) => Error.Validation("tax.einvoice-unknown-provider", $"No e-invoicing adapter named '{name}' is installed.");
}

// ---------- Settings ----------
public sealed record GetEInvoicingSettingsQuery : IQuery<EInvoicingSettingsDto>;

public sealed class GetEInvoicingSettingsHandler(ITaxDbContext db, IEnumerable<IEInvoicingProvider> providers) : IQueryHandler<GetEInvoicingSettingsQuery, EInvoicingSettingsDto>
{
    public async Task<Result<EInvoicingSettingsDto>> Handle(GetEInvoicingSettingsQuery query, CancellationToken cancellationToken)
    {
        var settings = await db.EInvoicingSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        var names = providers.Select(p => p.Name).Order(StringComparer.Ordinal).ToList();
        return settings is null
            ? new EInvoicingSettingsDto(false, MockProviderName.Value, "sandbox", 0, null, names)
            : new EInvoicingSettingsDto(settings.Enabled, settings.Provider, settings.Environment, settings.LastCounter, settings.LastHash, names);
    }
}

/// <summary>Name of the bundled mock adapter.</summary>
public static class MockProviderName
{
    public const string Value = "mock";
}

public sealed record UpdateEInvoicingSettingsCommand(bool Enabled, string? Provider, string? Environment) : ICommand<EInvoicingSettingsDto>;

public sealed class UpdateEInvoicingSettingsValidator : AbstractValidator<UpdateEInvoicingSettingsCommand>
{
    public UpdateEInvoicingSettingsValidator()
    {
        RuleFor(x => x.Provider).MaximumLength(50);
        RuleFor(x => x.Environment).MaximumLength(50);
    }
}

/// <summary>Turns e-invoicing on or off for the active company and selects the adapter. Enabled companies queue every posted sales invoice and credit note.</summary>
public sealed class UpdateEInvoicingSettingsHandler(ITaxDbContext db, ITenantContext tenant, IEnumerable<IEInvoicingProvider> providers)
    : ICommandHandler<UpdateEInvoicingSettingsCommand, EInvoicingSettingsDto>
{
    public async Task<Result<EInvoicingSettingsDto>> Handle(UpdateEInvoicingSettingsCommand command, CancellationToken cancellationToken)
    {
        var provider = (command.Provider ?? MockProviderName.Value).Trim().ToLowerInvariant();
        var installed = providers.Select(p => p.Name).ToList();
        if (!installed.Contains(provider, StringComparer.Ordinal))
        {
            return EInvoicingErrors.UnknownProvider(provider);
        }

        var environment = command.Environment ?? "sandbox";
        var settings = await db.EInvoicingSettings.FirstOrDefaultAsync(cancellationToken);
        if (settings is null)
        {
            settings = EInvoicingSettings.Create(tenant.CompanyId!.Value, command.Enabled, provider, environment);
            db.EInvoicingSettings.Add(settings);
        }
        else
        {
            settings.Update(command.Enabled, provider, environment);
        }

        await db.SaveChangesAsync(cancellationToken);
        return new EInvoicingSettingsDto(settings.Enabled, settings.Provider, settings.Environment, settings.LastCounter, settings.LastHash,
            installed.Order(StringComparer.Ordinal).ToList());
    }
}

// ---------- Submissions ----------
public sealed record ListEInvoiceSubmissionsQuery(string? Status, Guid? SourceDocId, PageRequest Page) : IQuery<PagedResult<EInvoiceSubmissionDto>>;

public sealed class ListEInvoiceSubmissionsHandler(ITaxDbContext db) : IQueryHandler<ListEInvoiceSubmissionsQuery, PagedResult<EInvoiceSubmissionDto>>
{
    public async Task<Result<PagedResult<EInvoiceSubmissionDto>>> Handle(ListEInvoiceSubmissionsQuery query, CancellationToken cancellationToken)
    {
        var source = db.EInvoiceSubmissions.AsNoTracking();
        if (query.Status is not null && Enum.TryParse<EInvoiceStatus>(query.Status, true, out var status))
        {
            source = source.Where(s => s.Status == status);
        }

        if (query.SourceDocId is { } docId)
        {
            source = source.Where(s => s.SourceDocId == docId);
        }

        var total = await source.CountAsync(cancellationToken);
        var items = await source.OrderByDescending(s => s.CreatedAtUtc).Skip(query.Page.Skip).Take(query.Page.SafePageSize).ToListAsync(cancellationToken);
        return new PagedResult<EInvoiceSubmissionDto>(items.Select(EInvoiceMapping.ToDto).ToList(), query.Page.SafePage, query.Page.SafePageSize, total);
    }
}

public sealed record GetEInvoiceSubmissionQuery(Guid Id) : IQuery<EInvoiceSubmissionDto>;

public sealed class GetEInvoiceSubmissionHandler(ITaxDbContext db) : IQueryHandler<GetEInvoiceSubmissionQuery, EInvoiceSubmissionDto>
{
    public async Task<Result<EInvoiceSubmissionDto>> Handle(GetEInvoiceSubmissionQuery query, CancellationToken cancellationToken)
    {
        var s = await db.EInvoiceSubmissions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == query.Id, cancellationToken);
        return s is null ? EInvoicingErrors.SubmissionNotFound : EInvoiceMapping.ToDto(s);
    }
}

public sealed record RetryEInvoiceSubmissionCommand(Guid Id) : ICommand<EInvoiceSubmissionDto>;

/// <summary>Queues a pending (outbox gave up) or rejected (master data corrected) submission again.</summary>
public sealed class RetryEInvoiceSubmissionHandler(ITaxDbContext db) : ICommandHandler<RetryEInvoiceSubmissionCommand, EInvoiceSubmissionDto>
{
    public async Task<Result<EInvoiceSubmissionDto>> Handle(RetryEInvoiceSubmissionCommand command, CancellationToken cancellationToken)
    {
        var submission = await db.EInvoiceSubmissions.FirstOrDefaultAsync(s => s.Id == command.Id, cancellationToken);
        if (submission is null)
        {
            return EInvoicingErrors.SubmissionNotFound;
        }

        submission.Retry();
        await db.SaveChangesAsync(cancellationToken);
        return EInvoiceMapping.ToDto(submission);
    }
}

// ---------- Event handlers (outbox) ----------

/// <summary>
/// Queues an e-invoice submission when a sales invoice or credit note journal is posted and the company has e-invoicing enabled.
/// Idempotent: one submission per source document (also enforced by a unique index).
/// </summary>
public sealed class QueueEInvoiceOnJournalPosted(ITaxDbContext db, ITenantContext tenant, IClock clock) : IIntegrationEventHandler<JournalPosted>
{
    private static readonly Dictionary<string, string> Purposes = new(StringComparer.Ordinal)
    {
        [SalesDocumentTypes.Invoice] = "invoice",
        [SalesDocumentTypes.Return] = "return",
    };

    public async Task HandleAsync(JournalPosted e, CancellationToken ct)
    {
        if (e.SourceModule != SourceModules.Sales || !Purposes.TryGetValue(e.SourceDocType, out var purpose) || e.Purpose != purpose)
        {
            return;
        }

        if (tenant.CompanyId is null)
        {
            tenant.SetCompany(e.CompanyId);
        }

        var settings = await db.EInvoicingSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        if (settings is not { Enabled: true } || await db.EInvoiceSubmissions.AnyAsync(s => s.SourceDocType == e.SourceDocType && s.SourceDocId == e.SourceDocId, ct))
        {
            return;
        }

        db.EInvoiceSubmissions.Add(EInvoiceSubmission.Create(e.CompanyId, e.LegalEntityId, e.SourceModule, e.SourceDocType, e.SourceDocId, e.Number, clock.UtcNow));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // A concurrent delivery of the same event queued it first (unique index); otherwise let the outbox retry.
            if (!await db.EInvoiceSubmissions.AsNoTracking().AnyAsync(s => s.SourceDocType == e.SourceDocType && s.SourceDocId == e.SourceDocId, ct))
            {
                throw;
            }
        }
    }
}

/// <summary>
/// Builds the provider-neutral document (once), links it into the company's chain and submits it. Transient provider failures are
/// recorded and rethrown so the outbox retries with the same payload; rejections are final until a user retries.
/// </summary>
public sealed class SubmitEInvoiceOnQueued(
    ITaxDbContext db,
    ITenantContext tenant,
    IClock clock,
    EInvoiceBuilder builder,
    IEnumerable<IEInvoicingProvider> providers) : IIntegrationEventHandler<EInvoiceSubmissionQueued>
{
    public async Task HandleAsync(EInvoiceSubmissionQueued e, CancellationToken ct)
    {
        if (tenant.CompanyId is null)
        {
            tenant.SetCompany(e.CompanyId);
        }

        var submission = await db.EInvoiceSubmissions.FirstOrDefaultAsync(s => s.Id == e.SubmissionId, ct);
        if (submission is not { Status: EInvoiceStatus.Pending })
        {
            return;
        }

        var settings = await db.EInvoicingSettings.FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("E-invoicing settings are missing for the company.");
        var provider = providers.FirstOrDefault(p => p.Name == (submission.Provider ?? settings.Provider))
            ?? throw new InvalidOperationException($"No e-invoicing adapter named '{settings.Provider}' is installed.");

        if (!submission.IsBuilt)
        {
            var document = await builder.BuildAsync(submission, settings.Environment, ct);
            if (document is null)
            {
                submission.Reject(["The source document was not found or is not posted."], clock.UtcNow);
                await db.SaveChangesAsync(ct);
                return;
            }

            string? payload = null;
            var (counter, previous) = settings.Advance((next, prev) =>
            {
                var (json, hash) = EInvoiceBuilder.Seal(document with { Counter = next, PreviousHash = prev });
                payload = json;
                return hash;
            });
            submission.SetBuilt(payload!, counter, settings.LastHash!, previous, provider.Name);

            // Chain head and payload commit together; a concurrent submission fails on the settings rowversion and is retried.
            await db.SaveChangesAsync(ct);
        }

        var sealedDocument = JsonSerializer.Deserialize<EInvoiceDocument>(submission.Payload!, EInvoiceBuilder.Json)!;
        submission.RecordAttempt(clock.UtcNow);
        EInvoiceSubmissionResult result;
        try
        {
            result = await provider.SubmitAsync(sealedDocument, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            submission.RecordFailure(ex.Message);
            await db.SaveChangesAsync(ct);
            throw;
        }

        switch (result.Outcome)
        {
            case EInvoiceOutcome.Accepted:
                submission.Accept(result.ProviderReference, result.QrCode, clock.UtcNow);
                break;
            case EInvoiceOutcome.Rejected:
                submission.Reject(result.Errors, clock.UtcNow);
                break;
            default:
                submission.MarkSubmitted(result.ProviderReference);
                break;
        }

        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Maps posted sales documents (through Sales.Contracts) to <see cref="EInvoiceDocument"/> and seals them (canonical JSON + SHA-256).</summary>
public sealed class EInvoiceBuilder(IServiceProvider services)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<EInvoiceDocument?> BuildAsync(EInvoiceSubmission submission, string environment, CancellationToken ct)
    {
        var sales = services.GetService<ISalesReportQueries>();
        if (sales is null)
        {
            return null;
        }

        var creditNote = submission.SourceDocType == SalesDocumentTypes.Return;
        PrintableDocument? print = creditNote
            ? await sales.GetReturnPrintAsync(submission.SourceDocId, ct)
            : await sales.GetInvoicePrintAsync(submission.SourceDocId, ct);
        if (print is null)
        {
            return null;
        }

        var letterhead = services.GetService<IOrganizationReportQueries>() is { } organization
            ? await organization.GetLetterheadAsync(submission.LegalEntityId, ct)
            : null;
        var customer = print.Party is { } party && services.GetService<IPartyQueries>() is { } parties
            ? await parties.GetCustomerAsync(party.Id, ct)
            : null;

        var lines = print.Lines.Select((l, i) => new EInvoiceLine(i + 1, l.Code, l.Description, l.Quantity, l.UnitPrice ?? 0m, l.DiscountPercent ?? 0m,
            l.Net ?? 0m, l.Tax ?? 0m, l.Total ?? (l.Net ?? 0m) + (l.Tax ?? 0m))).ToList();
        var taxes = print.Taxes.Select(t => new EInvoiceTaxBreakdown(t.Code, t.Rate, t.Base, t.Amount)).ToList();
        var seller = new EInvoiceParty(letterhead?.LegalEntityName ?? letterhead?.CompanyNameEn ?? string.Empty, letterhead?.TaxRegistrationNo, letterhead?.CompanyCode);
        var buyer = new EInvoiceParty(customer?.NameEn ?? string.Empty, customer?.TaxRegistrationNo, customer?.Code);
        return new EInvoiceDocument(submission.Uuid, creditNote ? EInvoiceDocumentTypes.CreditNote : EInvoiceDocumentTypes.Invoice, print.Number, print.Date,
            print.Currency, seller, buyer, lines, taxes, lines.Sum(l => l.NetAmount), lines.Sum(l => l.TaxAmount), lines.Sum(l => l.GrossAmount), 0, null,
            environment);
    }

    /// <summary>Canonical JSON of the document with its hash: SHA-256 (Base64) over the JSON of the document without the hash.</summary>
    public static (string Json, string Hash) Seal(EInvoiceDocument document)
    {
        var unsealed = JsonSerializer.Serialize(document with { Hash = null }, Json);
        var hash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(unsealed)));
        return (JsonSerializer.Serialize(document with { Hash = hash }, Json), hash);
    }
}

/// <summary>Tax master data lookups for reports.</summary>
public sealed class TaxCodeQueries(ITaxDbContext db) : ITaxCodeQueries
{
    public async Task<IReadOnlyList<TaxCodeInfo>> GetTaxCodesAsync(DateOnly asOf, CancellationToken cancellationToken = default)
    {
        var codes = await db.TaxCodes.AsNoTracking().Include(c => c.Rates).ToListAsync(cancellationToken);
        return codes.Select(c => new TaxCodeInfo(c.Id, c.Code, c.NameEn, c.NameAr, (TaxKindCode)(int)c.Kind,
                c.RateOn(asOf) ?? c.Rates.OrderByDescending(r => r.ValidFrom).Select(r => (decimal?)r.Rate).FirstOrDefault(), c.ReportingBox))
            .OrderBy(c => c.Code, StringComparer.Ordinal).ToList();
    }

    public async Task<IReadOnlyList<TaxGroupInfo>> GetTaxGroupsAsync(CancellationToken cancellationToken = default) =>
        await db.TaxGroups.AsNoTracking().OrderBy(g => g.Code).Select(g => new TaxGroupInfo(g.Id, g.Code, g.NameEn, g.NameAr)).ToListAsync(cancellationToken);
}
