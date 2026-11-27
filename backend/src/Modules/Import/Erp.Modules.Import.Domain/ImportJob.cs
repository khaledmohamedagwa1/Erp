using Erp.SharedKernel.Domain;
using Erp.SharedKernel.Results;

namespace Erp.Modules.Import.Domain;

/// <summary>What an import file contains. Each type has its own column layout and target module.</summary>
public enum ImportType
{
    ChartOfAccounts = 1,
    Products = 2,
    Customers = 3,
    Suppliers = 4,
    OpeningStock = 5,
    OpenReceivables = 6,
    OpenPayables = 7,
    GlOpeningBalances = 8,
}

public enum ImportFileFormat
{
    Csv = 1,
    Xlsx = 2,
}

/// <summary>Uploaded → (validate) Validated | Failed → (commit, only from Validated) Committed. Re-validation is allowed until committed.</summary>
public enum ImportStatus
{
    Uploaded = 1,
    Validated = 2,
    Failed = 3,
    Committed = 4,
}

/// <summary>A problem found in the file. <see cref="Row"/> is the spreadsheet row number (header = 1); 0 means the whole file.</summary>
public sealed record ImportRowError(int Row, string? Column, string Code, string Message);

public static class ImportErrors
{
    public static readonly Error AlreadyCommitted = Error.Conflict("import.already-committed", "This import has already been committed.");
    public static readonly Error NotValidated = Error.Conflict("import.not-validated", "Validate the import without errors before committing it.");
    public static readonly Error FileEmpty = Error.Validation("import.file-empty", "The file is empty.");
    public static readonly Error FileTooLarge = Error.Validation("import.file-too-large", "The file is too large.");
    public static readonly Error UnsupportedFormat = Error.Validation("import.unsupported-format", "Upload a .csv or .xlsx file.");
}

/// <summary>One uploaded file and its validation/commit outcome. The file content is stored separately (<see cref="ImportJobFile"/>).</summary>
public sealed class ImportJob : AggregateRoot, ICompanyScoped
{
    public const int MaxStoredErrors = 1000;

    private ImportJob()
    {
    }

    public Guid CompanyId { get; private set; }

    public ImportType Type { get; private set; }

    public string FileName { get; private set; } = string.Empty;

    public ImportFileFormat Format { get; private set; }

    public ImportStatus Status { get; private set; }

    /// <summary>Required for GL opening balances, opening receivables and payables.</summary>
    public Guid? LegalEntityId { get; private set; }

    /// <summary>Date of the opening documents (GL opening balances, opening stock).</summary>
    public DateOnly? OpeningDate { get; private set; }

    public Guid? UploadedBy { get; private set; }

    public DateTime UploadedAtUtc { get; private set; }

    public int TotalRows { get; private set; }

    public int ErrorRows { get; private set; }

    public List<ImportRowError> Errors { get; private set; } = [];

    /// <summary>True when more errors were found than <see cref="MaxStoredErrors"/>.</summary>
    public bool ErrorsTruncated { get; private set; }

    public DateTime? ValidatedAtUtc { get; private set; }

    /// <summary>Set (inside the commit transaction) when a commit starts; the row update serialises concurrent commits.</summary>
    public DateTime? CommitStartedAtUtc { get; private set; }

    public DateTime? CommittedAtUtc { get; private set; }

    public Guid? CommittedBy { get; private set; }

    public int CommittedRows { get; private set; }

    public static ImportJob Create(Guid companyId, ImportType type, string? fileName, ImportFileFormat format, Guid? legalEntityId, DateOnly? openingDate,
        Guid? uploadedBy, DateTime nowUtc)
    {
        var name = Guard.NotEmpty(fileName, "import.file-name-required", "File name").Trim();
        if (name.Length > 200)
        {
            name = name[^200..];
        }

        return new ImportJob
        {
            Id = SequentialGuid.New(),
            CompanyId = companyId,
            Type = type,
            FileName = name,
            Format = format,
            Status = ImportStatus.Uploaded,
            LegalEntityId = legalEntityId,
            OpeningDate = openingDate,
            UploadedBy = uploadedBy,
            UploadedAtUtc = nowUtc,
        };
    }

    public void RecordValidation(int totalRows, IReadOnlyCollection<ImportRowError> errors, DateTime nowUtc)
    {
        EnsureNotCommitted();
        TotalRows = totalRows;
        ErrorRows = errors.Select(e => e.Row).Distinct().Count();
        Errors = errors.OrderBy(e => e.Row).Take(MaxStoredErrors).ToList();
        ErrorsTruncated = errors.Count > MaxStoredErrors;
        ValidatedAtUtc = nowUtc;
        Status = errors.Count == 0 && totalRows > 0 ? ImportStatus.Validated : ImportStatus.Failed;
    }

    /// <summary>Guards the commit: only a validated, not yet committed job. Changing the row (rowversion) serialises concurrent commits.</summary>
    public void EnsureCommittable()
    {
        EnsureNotCommitted();
        if (Status != ImportStatus.Validated)
        {
            throw new DomainException(ImportErrors.NotValidated);
        }
    }

    public void BeginCommit(DateTime nowUtc)
    {
        EnsureCommittable();
        CommitStartedAtUtc = nowUtc;
    }

    public void MarkCommitted(int rows, Guid? userId, DateTime nowUtc)
    {
        EnsureCommittable();
        Status = ImportStatus.Committed;
        CommittedRows = rows;
        CommittedBy = userId;
        CommittedAtUtc = nowUtc;
    }

    private void EnsureNotCommitted()
    {
        if (Status == ImportStatus.Committed)
        {
            throw new DomainException(ImportErrors.AlreadyCommitted);
        }
    }
}

/// <summary>The uploaded bytes of an import job (kept apart so job lists never load file content).</summary>
public sealed class ImportJobFile : AggregateRoot, ICompanyScoped
{
    public const int MaxBytes = 10 * 1024 * 1024;

    private ImportJobFile()
    {
    }

    public Guid CompanyId { get; private set; }

    public byte[] Content { get; private set; } = [];

    public static ImportJobFile Create(ImportJob job, byte[] content)
    {
        if (content.Length == 0)
        {
            throw new DomainException(ImportErrors.FileEmpty);
        }

        if (content.Length > MaxBytes)
        {
            throw new DomainException(ImportErrors.FileTooLarge with { Message = $"The file is larger than {MaxBytes / (1024 * 1024)} MB." });
        }

        return new ImportJobFile { Id = job.Id, CompanyId = job.CompanyId, Content = content };
    }
}
