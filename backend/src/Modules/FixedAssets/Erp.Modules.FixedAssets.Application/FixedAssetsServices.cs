using Erp.Application.Abstractions;
using Erp.Modules.Accounting.Contracts;
using Erp.Modules.FixedAssets.Domain;
using Erp.Modules.Organization.Contracts;
using Erp.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Erp.Modules.FixedAssets.Application;

public interface IFixedAssetsDbContext
{
    DbSet<AssetCategory> Categories { get; }

    DbSet<Asset> Assets { get; }

    DbSet<DepreciationRun> DepreciationRuns { get; }

    DbSet<DepreciationBook> Books { get; }

    DbSet<AssetSettings> Settings { get; }

    EntityEntry<TEntity> Entry<TEntity>(TEntity entity)
        where TEntity : class;

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

public static class FixedAssetsAppErrors
{
    public static readonly Error AssetNotFound = Error.NotFound("assets.asset-not-found", "The asset was not found.");
    public static readonly Error CategoryNotFound = Error.NotFound("assets.category-not-found", "The asset category was not found or is inactive.");
    public static readonly Error CategoryCodeTaken = Error.Conflict("assets.category-code-taken", "An asset category with this code already exists.");
    public static readonly Error RunNotFound = Error.NotFound("assets.run-not-found", "The depreciation run was not found.");
    public static readonly Error LegalEntityNotFound = Error.NotFound("assets.legal-entity-not-found", "The legal entity was not found in this company.");
    public static readonly Error AccountInvalid = Error.Validation("assets.account-invalid",
        "The account must be an active posting account that is not a control account.");
    public static readonly Error ClearingAccountRequired = Error.Validation("assets.clearing-account-required", "A manual acquisition needs the account to credit.");
    public static readonly Error SourceRequired = Error.Validation("assets.source-required", "A purchase-invoice acquisition needs the source document id.");
    public static readonly Error ProceedsAccountRequired = Error.Validation("assets.proceeds-account-required", "Disposal proceeds need the account to debit.");
    public static readonly Error NothingToDepreciate = Error.Conflict("assets.nothing-to-depreciate", "No depreciation is due for this legal entity and period.");
    public static readonly Error NotLatestRun = Error.Conflict("assets.not-latest-run", "Only the latest posted run of the legal entity can be reversed.");
    public static readonly Error CrossLegalEntity = Error.Conflict("assets.cross-legal-entity",
        "Assets cannot move between legal entities by editing; use the legal-entity transfer.");
    public static readonly Error BookNotFound = Error.NotFound("assets.book-not-found", "The depreciation book was not found or is inactive.");
    public static readonly Error BookCodeTaken = Error.Conflict("assets.book-code-taken", "A depreciation book with this code already exists.");
    public static readonly Error SourceTypeUnsupported = Error.Validation("assets.source-type-unsupported", "A purchase-invoice acquisition must reference a PurchaseInvoice.");
    public static readonly Error SourceInvoiceNotFound = Error.NotFound("assets.source-invoice-not-found", "The purchase invoice was not found in this company.");
    public static readonly Error SourceInvoiceNotPosted = Error.Conflict("assets.source-invoice-not-posted", "The purchase invoice is not posted.");
    public static readonly Error SourceInvoiceOtherLegalEntity = Error.Validation("assets.source-invoice-other-legal-entity",
        "The purchase invoice belongs to another legal entity.");
    public static readonly Error SourceLineNotFound = Error.NotFound("assets.source-line-not-found", "The purchase invoice line was not found.");
    public static readonly Error SourceAmountMismatch = Error.Conflict("assets.source-amount-mismatch",
        "The capitalised cost exceeds the purchase invoice amount (including assets already capitalised from it).");
    public static readonly Error PartialQuantityOrPercent = Error.Validation("assets.partial-quantity-or-percent", "Give either a quantity or a percentage to dispose.");
    public static readonly Error PartiallyDisposed = Error.Conflict("assets.partially-disposed",
        "An asset of this run was partially disposed after the run; the run can no longer be reversed.");
    public static readonly Error SameLegalEntity = Error.Validation("assets.same-legal-entity", "The target legal entity is the asset's own legal entity.");
    public static readonly Error TransferCurrencyMismatch = Error.Validation("assets.transfer-currency-mismatch",
        "Both legal entities must have the same functional currency.");
    public static readonly Error TransferWithReserve = Error.Conflict("assets.transfer-with-reserve",
        "Assets with a revaluation reserve cannot be transferred between legal entities.");
}

public sealed class FixedAssetsOptions
{
    public const string SectionName = "FixedAssets";

    /// <summary>How far (in the functional currency) the capitalised cost may exceed the purchase invoice line before activation is refused.</summary>
    public decimal InvoiceMatchTolerance { get; set; } = 1m;

    /// <summary>How often the worker checks whether automatic depreciation is due.</summary>
    public int ScheduleIntervalMinutes { get; set; } = 60;
}

/// <summary>Resolves the legal entity (must belong to the active company) and validates user-chosen accounts.</summary>
public sealed class AssetScope(ITenantContext tenant, IOrganizationQueries organization, IAccountQueries accounts)
{
    public async Task<Result<LegalEntitySummary>> LegalEntityAsync(Guid legalEntityId, CancellationToken ct)
    {
        var le = await organization.GetLegalEntityAsync(legalEntityId, ct);
        if (le is null || (tenant.CompanyId is { } company && company != le.CompanyId))
        {
            return FixedAssetsAppErrors.LegalEntityNotFound;
        }

        return le;
    }

    /// <summary>User-chosen accounts (clearing, proceeds, revaluation reserve) must be active, posting and not a control account.</summary>
    public async Task<Result> ValidateAccountAsync(Guid accountId, CancellationToken ct)
    {
        var account = await accounts.GetAccountAsync(accountId, ct);
        return account is { IsActive: true, IsPostingAccount: true, IsControl: false } ? Result.Success() : FixedAssetsAppErrors.AccountInvalid;
    }
}

/// <summary>Builds functional-currency journal lines (rate 1), skipping zero amounts.</summary>
internal sealed class AssetJournal
{
    private readonly List<JournalLineRequest> _lines = [];

    public IReadOnlyList<JournalLineRequest> Lines => _lines;

    public AssetJournal Debit(string? role, Guid? accountId, decimal amount, string? postingGroup = null, string? description = null) =>
        Add(role, accountId, amount, 0, postingGroup, description);

    public AssetJournal Credit(string? role, Guid? accountId, decimal amount, string? postingGroup = null, string? description = null) =>
        Add(role, accountId, 0, amount, postingGroup, description);

    private AssetJournal Add(string? role, Guid? accountId, decimal debit, decimal credit, string? postingGroup, string? description)
    {
        if (debit == 0 && credit == 0)
        {
            return this;
        }

        _lines.Add(new JournalLineRequest(accountId is null ? role : null, accountId, debit, credit,
            postingGroup is null ? null : new PostingContext(ProductPostingGroup: postingGroup), Description: description));
        return this;
    }
}
