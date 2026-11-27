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
        "Assets cannot move between legal entities; dispose and acquire instead.");
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
