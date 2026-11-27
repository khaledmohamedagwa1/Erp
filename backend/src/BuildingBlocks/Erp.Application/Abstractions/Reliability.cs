using Erp.SharedKernel.Domain;

namespace Erp.Application.Abstractions;

/// <summary>Ambient information about the outbox message currently being delivered (set by the outbox processor).</summary>
public interface IOutboxMessageContext
{
    Guid? MessageId { get; }

    void Set(Guid messageId);
}

public sealed class OutboxMessageContext : IOutboxMessageContext
{
    public Guid? MessageId { get; private set; }

    public void Set(Guid messageId) => MessageId = messageId;
}

/// <summary>
/// Consumer-side deduplication. <see cref="TryBeginAsync"/> records "this consumer handled this message" inside the current
/// transaction; it returns false when the message was already handled, so a redelivered event is skipped.
/// </summary>
public interface IInbox
{
    Task<bool> TryBeginAsync(Guid messageId, string consumer, CancellationToken cancellationToken = default);
}

/// <summary>
/// Base for outbox consumers that must run at most once: opens a transaction, claims the message in the inbox,
/// runs <see cref="HandleOnceAsync"/>, commits. A redelivery finds the inbox row and does nothing.
/// </summary>
public abstract class InboxIntegrationEventHandler<TEvent>(IUnitOfWork unitOfWork, IInbox inbox, IOutboxMessageContext message)
    : IIntegrationEventHandler<TEvent>
    where TEvent : IDomainEvent
{
    /// <summary>Stable consumer name; changing it makes old messages look unprocessed.</summary>
    protected virtual string ConsumerName => GetType().FullName!;

    public async Task HandleAsync(TEvent integrationEvent, CancellationToken cancellationToken)
    {
        var messageId = message.MessageId
            ?? throw new InvalidOperationException("Inbox handlers can only run while delivering an outbox message.");

        await unitOfWork.BeginTransactionAsync(cancellationToken);
        try
        {
            if (!await inbox.TryBeginAsync(messageId, ConsumerName, cancellationToken))
            {
                await unitOfWork.RollbackAsync(cancellationToken);
                return;
            }

            await HandleOnceAsync(integrationEvent, cancellationToken);
            await unitOfWork.CommitAsync(cancellationToken);
        }
        catch
        {
            await unitOfWork.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    protected abstract Task HandleOnceAsync(TEvent integrationEvent, CancellationToken cancellationToken);
}

public sealed record EmailMessage(string To, string Subject, string Body);

/// <summary>Outbound notifications (email-shaped). Implementations: logging (default) and SMTP.</summary>
public interface INotificationSender
{
    Task SendEmailAsync(EmailMessage message, CancellationToken cancellationToken = default);
}

/// <summary>Decides whether an exception is a transient database failure (e.g. a deadlock) worth retrying.</summary>
public interface ITransientFailureDetector
{
    bool IsTransient(Exception exception);
}

public sealed class NeverTransientFailureDetector : ITransientFailureDetector
{
    public bool IsTransient(Exception exception) => false;
}

/// <summary>Discards tracked (unsaved or failed) entity state so a command can be re-run from a clean slate.</summary>
public interface IChangeTrackerReset
{
    void ClearTrackedChanges();
}
