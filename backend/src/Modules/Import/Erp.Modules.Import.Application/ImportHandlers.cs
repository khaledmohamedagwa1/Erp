using Erp.Application.Abstractions;
using Erp.Application.Messaging;
using Erp.Application.Paging;
using Erp.Modules.Import.Domain;
using Erp.Modules.Organization.Contracts;
using Erp.SharedKernel.Domain;
using Erp.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Import.Application;

public interface IImportDbContext
{
    DbSet<ImportJob> Jobs { get; }

    DbSet<ImportJobFile> Files { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

// ======================= DTOs =======================
public sealed record ImportRowErrorDto(int Row, string? Column, string Code, string Message);

public sealed record ImportJobDto(Guid Id, string Type, string FileName, string Format, string Status, Guid? LegalEntityId, DateOnly? OpeningDate, Guid? UploadedBy,
    DateTime UploadedAtUtc, int TotalRows, int ErrorRows, DateTime? ValidatedAtUtc, DateTime? CommittedAtUtc, Guid? CommittedBy, int CommittedRows,
    bool ErrorsTruncated, IReadOnlyList<ImportRowErrorDto> Errors);

public sealed record ImportColumnDto(string Key, string Header, string HeaderAr, bool Required, string Description, string Example, IReadOnlyList<string> Aliases);

public sealed record ImportTypeDto(string Type, string Name, string NameAr, string TargetPermission, bool RequiresLegalEntity, bool RequiresOpeningDate,
    IReadOnlyList<ImportColumnDto> Columns);

public sealed record ImportTemplateDto(string FileName, byte[] Content);

public static class ImportAppErrors
{
    public static readonly Error JobNotFound = Error.NotFound("import.job-not-found", "The import job was not found.");
    public static readonly Error UnknownType = Error.Validation("import.unknown-type", "Unknown import type.");
    public static readonly Error LegalEntityRequired = Error.Validation("import.legal-entity-required", "This import type needs a legal entity.");
    public static readonly Error OpeningDateRequired = Error.Validation("import.opening-date-required", "This import type needs an opening date.");
    public static readonly Error LegalEntityNotFound = Error.NotFound("import.legal-entity-not-found", "The legal entity was not found in this company.");
}

internal static class ImportMapping
{
    public static ImportJobDto ToDto(ImportJob j, bool includeErrors = true) => new(j.Id, j.Type.ToString(), j.FileName, j.Format.ToString(), j.Status.ToString(),
        j.LegalEntityId, j.OpeningDate, j.UploadedBy, j.UploadedAtUtc, j.TotalRows, j.ErrorRows, j.ValidatedAtUtc, j.CommittedAtUtc, j.CommittedBy, j.CommittedRows,
        j.ErrorsTruncated, includeErrors ? j.Errors.Select(e => new ImportRowErrorDto(e.Row, e.Column, e.Code, e.Message)).ToList() : []);

    public static ImportTypeDto ToDto(ImportDefinitionInfo d) => new(d.Type.ToString(), d.Name, d.NameAr, d.TargetPermission, d.RequiresLegalEntity, d.RequiresOpeningDate,
        d.Columns.Select(c => new ImportColumnDto(c.Key, c.Header, c.HeaderAr, c.Required, c.Description, c.Example, c.Aliases)).ToList());

    public static Result<ImportType> ParseType(string? value) =>
        Enum.TryParse<ImportType>(value?.Replace("-", string.Empty, StringComparison.Ordinal), true, out var type) && Enum.IsDefined(type)
            ? type
            : ImportAppErrors.UnknownType with { Message = $"Unknown import type '{value}'. Use one of: {string.Join(", ", Enum.GetNames<ImportType>())}." };
}

/// <summary>All import types, by <see cref="ImportType"/>.</summary>
public sealed class ImportDefinitionRegistry(IEnumerable<IImportDefinition> definitions)
{
    private readonly Dictionary<ImportType, IImportDefinition> _byType = definitions.ToDictionary(d => d.Info.Type);

    public IReadOnlyList<IImportDefinition> All => _byType.Values.OrderBy(d => d.Info.Type).ToList();

    public IImportDefinition Get(ImportType type) => _byType[type];
}

/// <summary>Importing writes another module's data, so the user also needs that module's own permission.</summary>
public sealed class ImportAccess(ICurrentUser user, IPermissionService permissions)
{
    public async Task<Result> EnsureTargetAsync(ImportDefinitionInfo info, CancellationToken ct)
    {
        if (user.UserId is not { } userId)
        {
            return Error.Unauthorized("unauthorized", "Authentication is required.");
        }

        return await permissions.HasPermissionAsync(userId, info.TargetPermission, ct)
            ? Result.Success()
            : Error.Forbidden("import.target-permission-missing", $"Importing {info.Name.ToLowerInvariant()} also requires the permission '{info.TargetPermission}'.");
    }
}

// ======================= Upload =======================
public sealed record UploadImportCommand(string Type, string FileName, byte[] Content, Guid? LegalEntityId, DateOnly? OpeningDate) : ICommand<ImportJobDto>;

public sealed class UploadImportValidator : AbstractValidator<UploadImportCommand>
{
    public UploadImportValidator()
    {
        RuleFor(x => x.Type).NotEmpty();
        RuleFor(x => x.FileName).NotEmpty().MaximumLength(260);
        RuleFor(x => x.Content).NotEmpty().WithMessage("The file is empty.")
            .Must(c => c.Length <= ImportJobFile.MaxBytes).WithMessage($"The file is larger than {ImportJobFile.MaxBytes / (1024 * 1024)} MB.");
    }
}

public sealed class UploadImportHandler(IImportDbContext db, ImportDefinitionRegistry registry, ImportAccess access, IImportFileReader reader, IOrganizationQueries organization,
    ITenantContext tenant, ICurrentUser user, IClock clock) : ICommandHandler<UploadImportCommand, ImportJobDto>
{
    public async Task<Result<ImportJobDto>> Handle(UploadImportCommand command, CancellationToken cancellationToken)
    {
        var type = ImportMapping.ParseType(command.Type);
        if (type.IsFailure)
        {
            return type.Error;
        }

        var format = ImportLimits.FormatOf(command.FileName);
        if (format.IsFailure)
        {
            return format.Error;
        }

        var definition = registry.Get(type.Value).Info;
        var allowed = await access.EnsureTargetAsync(definition, cancellationToken);
        if (allowed.IsFailure)
        {
            return allowed.Error;
        }

        if (definition.RequiresLegalEntity)
        {
            if (command.LegalEntityId is not { } legalEntityId)
            {
                return ImportAppErrors.LegalEntityRequired;
            }

            var le = await organization.GetLegalEntityAsync(legalEntityId, cancellationToken);
            if (le is null || le.CompanyId != tenant.CompanyId)
            {
                return ImportAppErrors.LegalEntityNotFound;
            }
        }

        if (definition.RequiresOpeningDate && command.OpeningDate is null)
        {
            return ImportAppErrors.OpeningDateRequired;
        }

        // Reject unreadable files at upload; row-level problems are reported by validation.
        var table = reader.Read(command.Content, format.Value);
        if (table.IsFailure)
        {
            return table.Error;
        }

        var job = ImportJob.Create(tenant.CompanyId!.Value, type.Value, Path.GetFileName(command.FileName), format.Value,
            definition.RequiresLegalEntity ? command.LegalEntityId : null, definition.RequiresOpeningDate ? command.OpeningDate : null, user.UserId, clock.UtcNow);
        db.Jobs.Add(job);
        db.Files.Add(ImportJobFile.Create(job, command.Content));
        await db.SaveChangesAsync(cancellationToken);
        return ImportMapping.ToDto(job);
    }
}

// ======================= Validate (dry run) =======================

/// <summary>
/// Dry run: executes the import for real inside a transaction — through the target modules' own commands, so every business rule
/// applies — and always rolls it back. Only the job's outcome (row counts and per-row errors) is then saved.
/// </summary>
public sealed record ValidateImportCommand(Guid JobId) : ICommand<ImportJobDto>, IManualTransaction;

public sealed class ValidateImportHandler(IImportDbContext db, ImportDefinitionRegistry registry, ImportAccess access, IImportFileReader reader, ImportRunner runner,
    IUnitOfWork unitOfWork, IEnumerable<IChangeTrackerReset> resets, IClock clock) : ICommandHandler<ValidateImportCommand, ImportJobDto>
{
    public async Task<Result<ImportJobDto>> Handle(ValidateImportCommand command, CancellationToken cancellationToken)
    {
        var job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == command.JobId, cancellationToken);
        if (job is null)
        {
            return ImportAppErrors.JobNotFound;
        }

        if (job.Status == ImportStatus.Committed)
        {
            return ImportErrors.AlreadyCommitted;
        }

        var definition = registry.Get(job.Type);
        var allowed = await access.EnsureTargetAsync(definition.Info, cancellationToken);
        if (allowed.IsFailure)
        {
            return allowed.Error;
        }

        var content = await db.Files.AsNoTracking().Where(f => f.Id == job.Id).Select(f => f.Content).FirstAsync(cancellationToken);
        var table = reader.Read(content, job.Format);
        int total;
        var errors = new List<ImportRowError>();
        if (table.IsFailure)
        {
            total = 0;
            errors.Add(new ImportRowError(0, null, table.Error.Code, table.Error.Message));
        }
        else
        {
            await unitOfWork.BeginTransactionAsync(cancellationToken);
            try
            {
                var prepared = await definition.PrepareAsync(table.Value, ImportContext.From(job), cancellationToken);
                var run = await runner.RunAsync(prepared.Units, cancellationToken);
                total = prepared.TotalRows;
                errors.AddRange(prepared.Errors);
                errors.AddRange(run.Errors);
            }
            finally
            {
                await unitOfWork.RollbackAsync(CancellationToken.None);
                foreach (var reset in resets)
                {
                    reset.ClearTrackedChanges();
                }
            }
        }

        var tracked = await db.Jobs.FirstAsync(j => j.Id == command.JobId, cancellationToken);
        tracked.RecordValidation(total, errors, clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        return ImportMapping.ToDto(tracked);
    }
}

// ======================= Commit =======================

/// <summary>
/// Commits a validated job in one transaction (all rows or none), in batches of <see cref="ImportRunner.BatchSize"/> units.
/// If anything fails now (data changed since validation), nothing is written and the errors are returned.
/// </summary>
public sealed record CommitImportCommand(Guid JobId) : ICommand<ImportJobDto>;

public sealed class CommitImportHandler(IImportDbContext db, ImportDefinitionRegistry registry, ImportAccess access, IImportFileReader reader, ImportRunner runner,
    IEnumerable<IChangeTrackerReset> resets, ICurrentUser user, IClock clock) : ICommandHandler<CommitImportCommand, ImportJobDto>
{
    public static readonly Error CommitFailed = Error.Validation("import.commit-failed",
        "The import could not be committed and nothing was saved. Fix the rows below (or validate again) and retry.");

    public async Task<Result<ImportJobDto>> Handle(CommitImportCommand command, CancellationToken cancellationToken)
    {
        var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == command.JobId, cancellationToken);
        if (job is null)
        {
            return ImportAppErrors.JobNotFound;
        }

        var definition = registry.Get(job.Type);
        var allowed = await access.EnsureTargetAsync(definition.Info, cancellationToken);
        if (allowed.IsFailure)
        {
            return allowed.Error;
        }

        try
        {
            // Writing the job row first takes its lock for the rest of the transaction: a concurrent commit waits, then sees Committed.
            job.BeginCommit(clock.UtcNow);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }
        catch (DbUpdateConcurrencyException)
        {
            return ImportErrors.AlreadyCommitted;
        }

        var content = await db.Files.AsNoTracking().Where(f => f.Id == job.Id).Select(f => f.Content).FirstAsync(cancellationToken);
        var table = reader.Read(content, job.Format);
        if (table.IsFailure)
        {
            return table.Error;
        }

        var prepared = await definition.PrepareAsync(table.Value, ImportContext.From(job), cancellationToken);
        if (prepared.Errors.Count > 0)
        {
            return Failed(prepared.Errors);
        }

        var run = await runner.RunAsync(prepared.Units, cancellationToken);
        if (run.Errors.Count > 0)
        {
            return Failed(run.Errors);
        }

        foreach (var reset in resets)
        {
            reset.ClearTrackedChanges();
        }

        var tracked = await db.Jobs.FirstAsync(j => j.Id == command.JobId, cancellationToken);
        tracked.MarkCommitted(run.SucceededRows, user.UserId, clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        return ImportMapping.ToDto(tracked);
    }

    private static Error Failed(IEnumerable<ImportRowError> errors) => CommitFailed with
    {
        Details = errors.GroupBy(e => e.Row == 0 ? "file" : $"row {e.Row}")
            .Take(ImportJob.MaxStoredErrors)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Column is null ? e.Message : $"{e.Column}: {e.Message}").ToArray()),
    };
}

// ======================= Queries =======================
public sealed record ListImportJobsQuery(PageRequest Page, string? Type, string? Status) : IQuery<PagedResult<ImportJobDto>>;

public sealed class ListImportJobsHandler(IImportDbContext db) : IQueryHandler<ListImportJobsQuery, PagedResult<ImportJobDto>>
{
    public async Task<Result<PagedResult<ImportJobDto>>> Handle(ListImportJobsQuery query, CancellationToken cancellationToken)
    {
        var source = db.Jobs.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Type))
        {
            var type = ImportMapping.ParseType(query.Type);
            if (type.IsFailure)
            {
                return type.Error;
            }

            source = source.Where(j => j.Type == type.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            if (!Enum.TryParse<ImportStatus>(query.Status, true, out var status))
            {
                return Error.Validation("import.unknown-status", "Unknown status.");
            }

            source = source.Where(j => j.Status == status);
        }

        var total = await source.CountAsync(cancellationToken);
        var jobs = await source.OrderByDescending(j => j.UploadedAtUtc).Skip(query.Page.Skip).Take(query.Page.SafePageSize).ToListAsync(cancellationToken);
        return new PagedResult<ImportJobDto>(jobs.Select(j => ImportMapping.ToDto(j, includeErrors: false)).ToList(), query.Page.SafePage, query.Page.SafePageSize, total);
    }
}

public sealed record GetImportJobQuery(Guid JobId) : IQuery<ImportJobDto>;

public sealed class GetImportJobHandler(IImportDbContext db) : IQueryHandler<GetImportJobQuery, ImportJobDto>
{
    public async Task<Result<ImportJobDto>> Handle(GetImportJobQuery query, CancellationToken cancellationToken)
    {
        var job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == query.JobId, cancellationToken);
        return job is null ? ImportAppErrors.JobNotFound : ImportMapping.ToDto(job);
    }
}

public sealed record ListImportTypesQuery : IQuery<IReadOnlyList<ImportTypeDto>>;

public sealed class ListImportTypesHandler(ImportDefinitionRegistry registry) : IQueryHandler<ListImportTypesQuery, IReadOnlyList<ImportTypeDto>>
{
    public Task<Result<IReadOnlyList<ImportTypeDto>>> Handle(ListImportTypesQuery query, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success<IReadOnlyList<ImportTypeDto>>(registry.All.Select(d => ImportMapping.ToDto(d.Info)).ToList()));
}

public sealed record GetImportTemplateQuery(string Type) : IQuery<ImportTemplateDto>;

public sealed class GetImportTemplateHandler(ImportDefinitionRegistry registry, IImportTemplateWriter writer) : IQueryHandler<GetImportTemplateQuery, ImportTemplateDto>
{
    public Task<Result<ImportTemplateDto>> Handle(GetImportTemplateQuery query, CancellationToken cancellationToken)
    {
        var type = ImportMapping.ParseType(query.Type);
        if (type.IsFailure)
        {
            return Task.FromResult(Result.Failure<ImportTemplateDto>(type.Error));
        }

        var info = registry.Get(type.Value).Info;
        return Task.FromResult(Result.Success(new ImportTemplateDto($"import-{type.Value.ToString().ToLowerInvariant()}.xlsx", writer.Write(info))));
    }
}
