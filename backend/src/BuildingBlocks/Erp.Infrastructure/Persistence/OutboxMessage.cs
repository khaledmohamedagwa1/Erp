namespace Erp.Infrastructure.Persistence;

/// <summary>Domain event persisted in the same transaction as the change that raised it (transactional outbox).</summary>
public sealed class OutboxMessage
{
    public Guid Id { get; set; }

    public Guid? TenantId { get; set; }

    public DateTime OccurredAtUtc { get; set; }

    public string Type { get; set; } = string.Empty;

    public string Payload { get; set; } = string.Empty;

    public string? CorrelationId { get; set; }

    public DateTime? ProcessedAtUtc { get; set; }

    public int Attempts { get; set; }

    public string? Error { get; set; }
}
