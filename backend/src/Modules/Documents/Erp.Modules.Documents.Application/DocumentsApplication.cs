using Erp.Application.Abstractions;
using Erp.Application.Messaging;
using Erp.Application.Paging;
using Erp.Modules.Documents.Contracts;
using Erp.Modules.Documents.Domain;
using Erp.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Documents.Application;

public interface IDocumentsDbContext
{
    DbSet<Attachment> Attachments { get; }

    DbSet<Notification> Notifications { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

public static class DocumentsAppErrors
{
    public static readonly Error AttachmentNotFound = Error.NotFound("documents.attachment-not-found", "Attachment not found.");

    public static readonly Error NotificationNotFound = Error.NotFound("documents.notification-not-found", "Notification not found.");

    public static readonly Error CompanyRequired = Error.Validation("company.required", "This endpoint requires the X-Company-Id header.");

    public static readonly Error FileMissing = Error.Failure("documents.file-missing", "The stored file could not be found.");
}

// ---------- Attachments ----------

/// <summary>Bytes already validated and written to the file store by <see cref="AttachmentIntake"/>.</summary>
public sealed record StoredBlob(string StorageKey, string Sha256, long SizeBytes, string ContentType, string FileName);

public sealed record AttachmentDto(
    Guid Id, string OwnerType, Guid OwnerId, string FileName, string ContentType, long SizeBytes, string Sha256, string? Description,
    Guid UploadedBy, DateTime UploadedAtUtc);

public sealed record AttachmentFile(AttachmentDto Attachment, string StorageKey);

public sealed record CreateAttachmentCommand(string OwnerType, Guid OwnerId, string? Description, StoredBlob Blob) : ICommand<AttachmentDto>;

public sealed record ListAttachmentsQuery(string OwnerType, Guid OwnerId) : IQuery<IReadOnlyList<AttachmentDto>>;

public sealed record GetAttachmentFileQuery(Guid Id) : IQuery<AttachmentFile>;

public sealed record DeleteAttachmentCommand(Guid Id) : ICommand<Unit>;

/// <summary>
/// Validates an upload (size, allowlisted extension, content signature), hashes it and writes it to the file store under a
/// content-addressed key. Runs before the command — no database transaction is open while bytes travel to S3, and the
/// write is idempotent, so a failed or retried command at worst leaves an unreferenced blob.
/// </summary>
public sealed class AttachmentIntake(IFileStorage storage, ITenantContext tenant, AttachmentOptions options)
{
    public async Task<Result<StoredBlob>> PrepareAsync(Stream source, string? fileName, long? declaredLength, CancellationToken cancellationToken)
    {
        if (declaredLength > options.MaxUploadBytes)
        {
            return FileTypes.TooLarge(options.MaxUploadBytes);
        }

        var name = FileTypes.SanitizeFileName(fileName);
        if (name.Length == 0)
        {
            return Error.Validation("documents.file-name-required", "File name is required.");
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > options.MaxUploadBytes)
            {
                return FileTypes.TooLarge(options.MaxUploadBytes);
            }

            buffer.Write(chunk, 0, read);
        }

        var content = buffer.ToArray();
        var type = FileTypes.Detect(name, content, options.AllowedExtensions);
        if (type.IsFailure)
        {
            return type.Error;
        }

        var sha = StorageKeys.Sha256Hex(content);
        var key = StorageKeys.For(tenant.TenantId ?? throw new InvalidOperationException("Tenant is required."), sha);
        if (!await storage.ExistsAsync(key, cancellationToken))
        {
            using var stream = new MemoryStream(content, writable: false);
            await storage.SaveAsync(key, stream, type.Value.ContentType, cancellationToken);
        }

        return new StoredBlob(key, sha, content.LongLength, type.Value.ContentType, name);
    }
}

internal static class AttachmentMapping
{
    public static AttachmentDto ToDto(Attachment a) =>
        new(a.Id, a.OwnerType, a.OwnerId, a.FileName, a.ContentType, a.SizeBytes, a.Sha256, a.Description, a.UploadedBy, a.UploadedAtUtc);
}

public sealed class CreateAttachmentHandler(IDocumentsDbContext db, ITenantContext tenant, ICurrentUser user, IClock clock)
    : ICommandHandler<CreateAttachmentCommand, AttachmentDto>
{
    public async Task<Result<AttachmentDto>> Handle(CreateAttachmentCommand command, CancellationToken cancellationToken)
    {
        if (tenant.CompanyId is not { } companyId || user.UserId is not { } userId)
        {
            return DocumentsAppErrors.CompanyRequired;
        }

        var blob = command.Blob;
        var attachment = Attachment.Create(companyId, command.OwnerType, command.OwnerId, blob.FileName, blob.ContentType, blob.SizeBytes, blob.Sha256,
            blob.StorageKey, command.Description, userId, clock.UtcNow);
        db.Attachments.Add(attachment);
        await db.SaveChangesAsync(cancellationToken);
        return AttachmentMapping.ToDto(attachment);
    }
}

public sealed class ListAttachmentsHandler(IDocumentsDbContext db) : IQueryHandler<ListAttachmentsQuery, IReadOnlyList<AttachmentDto>>
{
    public async Task<Result<IReadOnlyList<AttachmentDto>>> Handle(ListAttachmentsQuery query, CancellationToken cancellationToken)
    {
        var ownerType = (query.OwnerType ?? string.Empty).Trim().ToLowerInvariant();
        if (!DocumentsErrors.OwnerTypePattern().IsMatch(ownerType))
        {
            return DocumentsErrors.InvalidOwnerType;
        }

        var items = await db.Attachments.AsNoTracking()
            .Where(a => a.OwnerType == ownerType && a.OwnerId == query.OwnerId && !a.IsDeleted)
            .OrderBy(a => a.UploadedAtUtc)
            .ToListAsync(cancellationToken);
        return items.Select(AttachmentMapping.ToDto).ToList();
    }
}

public sealed class GetAttachmentFileHandler(IDocumentsDbContext db) : IQueryHandler<GetAttachmentFileQuery, AttachmentFile>
{
    public async Task<Result<AttachmentFile>> Handle(GetAttachmentFileQuery query, CancellationToken cancellationToken)
    {
        var attachment = await db.Attachments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == query.Id && !a.IsDeleted, cancellationToken);
        return attachment is null ? DocumentsAppErrors.AttachmentNotFound : new AttachmentFile(AttachmentMapping.ToDto(attachment), attachment.StorageKey);
    }
}

/// <summary>Soft delete: the row stays (audited) and the blob is kept — it may be shared by other attachments with the same content.</summary>
public sealed class DeleteAttachmentHandler(IDocumentsDbContext db, ICurrentUser user, IClock clock) : ICommandHandler<DeleteAttachmentCommand, Unit>
{
    public async Task<Result<Unit>> Handle(DeleteAttachmentCommand command, CancellationToken cancellationToken)
    {
        var attachment = await db.Attachments.FirstOrDefaultAsync(a => a.Id == command.Id && !a.IsDeleted, cancellationToken);
        if (attachment is null)
        {
            return DocumentsAppErrors.AttachmentNotFound;
        }

        attachment.Delete(user.UserId ?? Guid.Empty, clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        return Unit.Value;
    }
}

// ---------- Notifications ----------

public sealed record NotificationDto(Guid Id, string Category, string Title, string? Body, string? Link, Guid? CompanyId, DateTime CreatedAtUtc, DateTime? ReadAtUtc);

public sealed record ListNotificationsQuery(bool UnreadOnly, PageRequest Page) : IQuery<PagedResult<NotificationDto>>;

public sealed record UnreadNotificationCountQuery : IQuery<int>;

public sealed record MarkNotificationReadCommand(Guid Id) : ICommand<Unit>;

public sealed record MarkAllNotificationsReadCommand : ICommand<int>;

/// <summary>Every notification query is restricted to the signed-in user (the tenant filter adds the tenant).</summary>
public sealed class ListNotificationsHandler(IDocumentsDbContext db, ICurrentUser user) : IQueryHandler<ListNotificationsQuery, PagedResult<NotificationDto>>
{
    public async Task<Result<PagedResult<NotificationDto>>> Handle(ListNotificationsQuery query, CancellationToken cancellationToken)
    {
        var userId = user.UserId ?? Guid.Empty;
        var source = db.Notifications.AsNoTracking().Where(n => n.UserId == userId);
        if (query.UnreadOnly)
        {
            source = source.Where(n => n.ReadAtUtc == null);
        }

        var total = await source.CountAsync(cancellationToken);
        var items = await source.OrderByDescending(n => n.CreatedAtUtc).ThenByDescending(n => n.Id)
            .Skip(query.Page.Skip).Take(query.Page.SafePageSize)
            .Select(n => new NotificationDto(n.Id, n.Category, n.Title, n.Body, n.Link, n.CompanyId, n.CreatedAtUtc, n.ReadAtUtc))
            .ToListAsync(cancellationToken);
        return new PagedResult<NotificationDto>(items, query.Page.SafePage, query.Page.SafePageSize, total);
    }
}

public sealed class UnreadNotificationCountHandler(IDocumentsDbContext db, ICurrentUser user) : IQueryHandler<UnreadNotificationCountQuery, int>
{
    public async Task<Result<int>> Handle(UnreadNotificationCountQuery query, CancellationToken cancellationToken)
    {
        var userId = user.UserId ?? Guid.Empty;
        return await db.Notifications.CountAsync(n => n.UserId == userId && n.ReadAtUtc == null, cancellationToken);
    }
}

public sealed class MarkNotificationReadHandler(IDocumentsDbContext db, ICurrentUser user, IClock clock) : ICommandHandler<MarkNotificationReadCommand, Unit>
{
    public async Task<Result<Unit>> Handle(MarkNotificationReadCommand command, CancellationToken cancellationToken)
    {
        var userId = user.UserId ?? Guid.Empty;
        var notification = await db.Notifications.FirstOrDefaultAsync(n => n.Id == command.Id && n.UserId == userId, cancellationToken);
        if (notification is null)
        {
            return DocumentsAppErrors.NotificationNotFound;
        }

        notification.MarkRead(clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        return Unit.Value;
    }
}

public sealed class MarkAllNotificationsReadHandler(IDocumentsDbContext db, ICurrentUser user, IClock clock) : ICommandHandler<MarkAllNotificationsReadCommand, int>
{
    public async Task<Result<int>> Handle(MarkAllNotificationsReadCommand command, CancellationToken cancellationToken)
    {
        var userId = user.UserId ?? Guid.Empty;
        var unread = await db.Notifications.Where(n => n.UserId == userId && n.ReadAtUtc == null).Take(1000).ToListAsync(cancellationToken);
        var now = clock.UtcNow;
        foreach (var notification in unread)
        {
            notification.MarkRead(now);
        }

        await db.SaveChangesAsync(cancellationToken);
        return unread.Count;
    }
}

/// <summary>Writes notification rows in the caller's transaction (shared connection, ADR-006).</summary>
public sealed class NotificationPublisher(IDocumentsDbContext db, ITenantContext tenant, IClock clock) : INotificationPublisher
{
    public async Task PublishAsync(IReadOnlyCollection<Guid> userIds, InAppNotification notification, CancellationToken cancellationToken = default)
    {
        if (userIds.Count == 0)
        {
            return;
        }

        _ = tenant.TenantId ?? throw new InvalidOperationException("Notifications can only be published inside a tenant.");
        var now = clock.UtcNow;
        foreach (var userId in userIds.Distinct())
        {
            db.Notifications.Add(Notification.Create(userId, notification.CompanyId, notification.Category, notification.Title, notification.Body,
                notification.Link, now));
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
