namespace Erp.Modules.Documents.Contracts;

public static class DocumentsPermissions
{
    public const string AttachmentRead = "documents.attachments.read";
    public const string AttachmentWrite = "documents.attachments.write";
}

/// <summary>An in-app notification for one or more users.</summary>
/// <param name="Category">Short machine-readable kind, e.g. <c>workflow.approval-requested</c>.</param>
/// <param name="Link">Relative client route, e.g. <c>/approvals</c>. Never an absolute URL.</param>
/// <param name="CompanyId">The company the notification is about, if any (the client may switch company when it is opened).</param>
public sealed record InAppNotification(string Category, string Title, string? Body = null, string? Link = null, Guid? CompanyId = null);

/// <summary>
/// Writes in-app notifications (the bell in the UI). Runs inside the caller's transaction and only writes rows in the
/// current tenant, so it is safe to call from outbox handlers. Users that do not exist simply never read theirs.
/// </summary>
public interface INotificationPublisher
{
    Task PublishAsync(IReadOnlyCollection<Guid> userIds, InAppNotification notification, CancellationToken cancellationToken = default);
}

/// <summary>Default used when the Documents module is not installed (register with TryAdd).</summary>
public sealed class NoInAppNotifications : INotificationPublisher
{
    public Task PublishAsync(IReadOnlyCollection<Guid> userIds, InAppNotification notification, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
