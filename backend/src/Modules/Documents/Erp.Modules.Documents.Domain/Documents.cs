using System.Text.RegularExpressions;
using Erp.SharedKernel.Domain;
using Erp.SharedKernel.Results;

namespace Erp.Modules.Documents.Domain;

public static partial class DocumentsErrors
{
    public static readonly Error InvalidOwnerType = Error.Validation("documents.invalid-owner-type",
        "Owner type must look like 'module.document-type' (lower-case letters, digits and hyphens).");

    public static readonly Error OwnerRequired = Error.Validation("documents.owner-required", "Owner document id is required.");

    public static readonly Error AlreadyDeleted = Error.Conflict("documents.attachment-deleted", "The attachment has already been deleted.");

    [GeneratedRegex("^[a-z][a-z0-9-]{0,39}\\.[a-z][a-z0-9-]{0,59}$", RegexOptions.CultureInvariant)]
    public static partial Regex OwnerTypePattern();
}

/// <summary>
/// A file attached to a business document (owner = document type + id), inside one company. The bytes live in the
/// file store under a content-addressed key; the row holds metadata. Deletion is soft — the history stays auditable.
/// </summary>
[Audited]
public sealed class Attachment : AggregateRoot, ICompanyScoped
{
    public const int MaxFileNameLength = 255;

    private Attachment()
    {
    }

    public Guid CompanyId { get; private set; }

    /// <summary>e.g. <c>sales.sales-invoice</c> — the same strings as approval document types.</summary>
    public string OwnerType { get; private set; } = string.Empty;

    public Guid OwnerId { get; private set; }

    public string FileName { get; private set; } = string.Empty;

    public string ContentType { get; private set; } = string.Empty;

    public long SizeBytes { get; private set; }

    /// <summary>Lower-case hex SHA-256 of the content.</summary>
    public string Sha256 { get; private set; } = string.Empty;

    /// <summary>Key in the file store (never shown to clients).</summary>
    public string StorageKey { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public Guid UploadedBy { get; private set; }

    public DateTime UploadedAtUtc { get; private set; }

    public bool IsDeleted { get; private set; }

    public Guid? DeletedBy { get; private set; }

    public DateTime? DeletedAtUtc { get; private set; }

    public static Attachment Create(
        Guid companyId, string? ownerType, Guid ownerId, string fileName, string contentType, long sizeBytes, string sha256, string storageKey,
        string? description, Guid uploadedBy, DateTime nowUtc)
    {
        var type = (ownerType ?? string.Empty).Trim().ToLowerInvariant();
        if (!DocumentsErrors.OwnerTypePattern().IsMatch(type))
        {
            throw new DomainException(DocumentsErrors.InvalidOwnerType);
        }

        if (ownerId == Guid.Empty)
        {
            throw new DomainException(DocumentsErrors.OwnerRequired);
        }

        var desc = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        return new Attachment
        {
            Id = SequentialGuid.New(),
            CompanyId = companyId,
            OwnerType = type,
            OwnerId = ownerId,
            FileName = Guard.MaxLength(Guard.NotEmpty(fileName, "documents.file-name-required", "File name"), MaxFileNameLength,
                "documents.file-name-too-long", "File name"),
            ContentType = contentType,
            SizeBytes = sizeBytes,
            Sha256 = sha256,
            StorageKey = storageKey,
            Description = desc is null ? null : Guard.MaxLength(desc, 500, "documents.description-too-long", "Description"),
            UploadedBy = uploadedBy,
            UploadedAtUtc = nowUtc,
        };
    }

    public void Delete(Guid userId, DateTime nowUtc)
    {
        if (IsDeleted)
        {
            throw new DomainException(DocumentsErrors.AlreadyDeleted);
        }

        IsDeleted = true;
        DeletedBy = userId;
        DeletedAtUtc = nowUtc;
    }
}

/// <summary>An in-app notification for one user in the tenant (not company-filtered: a user sees theirs in every company).</summary>
public sealed class Notification : AggregateRoot, ITenantScoped
{
    private Notification()
    {
    }

    public Guid UserId { get; private set; }

    public Guid? CompanyId { get; private set; }

    public string Category { get; private set; } = string.Empty;

    public string Title { get; private set; } = string.Empty;

    public string? Body { get; private set; }

    public string? Link { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public DateTime? ReadAtUtc { get; private set; }

    public bool IsRead => ReadAtUtc is not null;

    public static Notification Create(Guid userId, Guid? companyId, string? category, string? title, string? body, string? link, DateTime nowUtc)
    {
        var target = string.IsNullOrWhiteSpace(link) ? null : link.Trim();
        if (target is not null && (!target.StartsWith('/') || target.StartsWith("//", StringComparison.Ordinal) || target.Contains('\\')))
        {
            // Only relative client routes: an absolute or protocol-relative URL could be used for phishing.
            throw new DomainException(Error.Validation("documents.notification-link-invalid", "A notification link must be a relative path."));
        }

        return new Notification
        {
            Id = SequentialGuid.New(),
            UserId = userId,
            CompanyId = companyId,
            Category = Truncate(Guard.NotEmpty(category, "documents.notification-category-required", "Category").Trim(), 100),
            Title = Truncate(Guard.NotEmpty(title, "documents.notification-title-required", "Title").Trim(), 200),
            Body = string.IsNullOrWhiteSpace(body) ? null : Truncate(body.Trim(), 2000),
            Link = target is null ? null : Truncate(target, 500),
            CreatedAtUtc = nowUtc,
        };
    }

    public void MarkRead(DateTime nowUtc) => ReadAtUtc ??= nowUtc;

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
