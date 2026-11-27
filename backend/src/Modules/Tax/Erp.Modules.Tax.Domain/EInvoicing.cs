using Erp.SharedKernel.Domain;
using Erp.SharedKernel.Results;

namespace Erp.Modules.Tax.Domain;

/// <summary>
/// Per-company e-invoicing configuration and the document chain head (counter and hash of the last document handed to the provider,
/// ZATCA-style). The chain is advanced with optimistic concurrency (rowversion): concurrent submissions retry through the outbox.
/// </summary>
[Audited]
public sealed class EInvoicingSettings : AggregateRoot, ICompanyScoped
{
    private EInvoicingSettings()
    {
    }

    public Guid CompanyId { get; private set; }

    public bool Enabled { get; private set; }

    /// <summary>Name of the configured provider adapter (e.g. "mock").</summary>
    public string Provider { get; private set; } = string.Empty;

    /// <summary>Free text passed to the adapter, e.g. "sandbox" or "production".</summary>
    public string Environment { get; private set; } = string.Empty;

    public long LastCounter { get; private set; }

    public string? LastHash { get; private set; }

    public static EInvoicingSettings Create(Guid companyId, bool enabled, string provider, string environment) => new()
    {
        Id = SequentialGuid.New(),
        CompanyId = companyId,
        Enabled = enabled,
        Provider = Guard.MaxLength(Guard.NotEmpty(provider, "tax.einvoice-provider-required", "Provider"), 50, "tax.einvoice-provider-too-long", "Provider").ToLowerInvariant(),
        Environment = Guard.MaxLength(environment?.Trim() ?? string.Empty, 50, "tax.einvoice-environment-too-long", "Environment"),
    };

    public void Update(bool enabled, string provider, string environment)
    {
        Enabled = enabled;
        Provider = Guard.MaxLength(Guard.NotEmpty(provider, "tax.einvoice-provider-required", "Provider"), 50, "tax.einvoice-provider-too-long", "Provider").ToLowerInvariant();
        Environment = Guard.MaxLength(environment?.Trim() ?? string.Empty, 50, "tax.einvoice-environment-too-long", "Environment");
    }

    /// <summary>Takes the next counter value and links the document hash into the chain. Returns (counter, previous hash).</summary>
    public (long Counter, string? PreviousHash) Advance(Func<long, string?, string> hashFor)
    {
        var counter = LastCounter + 1;
        var previous = LastHash;
        LastHash = hashFor(counter, previous);
        LastCounter = counter;
        return (counter, previous);
    }
}

public enum EInvoiceStatus
{
    /// <summary>Queued; not yet accepted or rejected by the provider (includes transient failures being retried).</summary>
    Pending = 1,

    /// <summary>Handed to the provider, awaiting its asynchronous verdict.</summary>
    Submitted = 2,

    Accepted = 3,

    Rejected = 4,
}

/// <summary>
/// One posted sales document sent to the e-invoicing provider. The provider-neutral document is built once (payload, counter, hash and
/// previous hash) and resubmitted unchanged on transient failures; a rejected submission retried by a user is rebuilt (new counter).
/// </summary>
public sealed class EInvoiceSubmission : AggregateRoot, ICompanyScoped
{
    private EInvoiceSubmission()
    {
    }

    public Guid CompanyId { get; private set; }

    public Guid LegalEntityId { get; private set; }

    public string SourceModule { get; private set; } = string.Empty;

    public string SourceDocType { get; private set; } = string.Empty;

    public Guid SourceDocId { get; private set; }

    public string DocumentNumber { get; private set; } = string.Empty;

    public EInvoiceStatus Status { get; private set; }

    public Guid Uuid { get; private set; }

    public long? Counter { get; private set; }

    public string? Hash { get; private set; }

    public string? PreviousHash { get; private set; }

    public string? Payload { get; private set; }

    public string? Provider { get; private set; }

    public string? ProviderReference { get; private set; }

    public string? QrCode { get; private set; }

    public string? Errors { get; private set; }

    public int Attempts { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public DateTime? LastAttemptAtUtc { get; private set; }

    public DateTime? CompletedAtUtc { get; private set; }

    public bool IsBuilt => Payload is not null;

    public static EInvoiceSubmission Create(Guid companyId, Guid legalEntityId, string sourceModule, string sourceDocType, Guid sourceDocId, string documentNumber,
        DateTime nowUtc)
    {
        var submission = new EInvoiceSubmission
        {
            Id = SequentialGuid.New(),
            CompanyId = companyId,
            LegalEntityId = legalEntityId,
            SourceModule = sourceModule,
            SourceDocType = sourceDocType,
            SourceDocId = sourceDocId,
            DocumentNumber = documentNumber,
            Status = EInvoiceStatus.Pending,
            Uuid = Guid.NewGuid(),
            CreatedAtUtc = nowUtc,
        };
        submission.Raise(new EInvoiceSubmissionQueued(submission.Id, companyId));
        return submission;
    }

    public void SetBuilt(string payload, long counter, string hash, string? previousHash, string provider)
    {
        EnsurePending();
        Payload = payload;
        Counter = counter;
        Hash = hash;
        PreviousHash = previousHash;
        Provider = provider;
    }

    public void RecordAttempt(DateTime nowUtc)
    {
        EnsurePending();
        Attempts++;
        LastAttemptAtUtc = nowUtc;
    }

    /// <summary>A transient failure (provider unreachable): stays Pending; the outbox retries.</summary>
    public void RecordFailure(string error) => Errors = Truncate(error);

    public void MarkSubmitted(string? providerReference)
    {
        EnsurePending();
        Status = EInvoiceStatus.Submitted;
        ProviderReference = providerReference;
        Errors = null;
    }

    public void Accept(string? providerReference, string? qrCode, DateTime nowUtc)
    {
        if (Status is EInvoiceStatus.Accepted or EInvoiceStatus.Rejected)
        {
            throw new DomainException(Error.Conflict("tax.einvoice-completed", "The submission is already completed."));
        }

        Status = EInvoiceStatus.Accepted;
        ProviderReference = providerReference ?? ProviderReference;
        QrCode = qrCode;
        Errors = null;
        CompletedAtUtc = nowUtc;
    }

    public void Reject(IEnumerable<string> errors, DateTime nowUtc)
    {
        if (Status is EInvoiceStatus.Accepted or EInvoiceStatus.Rejected)
        {
            throw new DomainException(Error.Conflict("tax.einvoice-completed", "The submission is already completed."));
        }

        Status = EInvoiceStatus.Rejected;
        Errors = Truncate(string.Join("\n", errors));
        CompletedAtUtc = nowUtc;
    }

    /// <summary>
    /// Queues the submission again. A rejected document is rebuilt (corrected master data, new counter/hash); a pending one keeps its
    /// payload (after the outbox gave up on transient failures).
    /// </summary>
    public void Retry()
    {
        if (Status is EInvoiceStatus.Accepted or EInvoiceStatus.Submitted)
        {
            throw new DomainException(Error.Conflict("tax.einvoice-not-retryable", "Only pending or rejected submissions can be retried."));
        }

        if (Status == EInvoiceStatus.Rejected)
        {
            Payload = null;
            Counter = null;
            Hash = null;
            PreviousHash = null;
            Uuid = Guid.NewGuid();
            CompletedAtUtc = null;
        }

        Status = EInvoiceStatus.Pending;
        Raise(new EInvoiceSubmissionQueued(Id, CompanyId));
    }

    private void EnsurePending()
    {
        if (Status != EInvoiceStatus.Pending)
        {
            throw new DomainException(Error.Conflict("tax.einvoice-not-pending", "The submission is not pending."));
        }
    }

    private static string? Truncate(string? value) => value is { Length: > 4000 } ? value[..4000] : value;
}

/// <summary>Outbox event: a submission must be built and sent to the provider.</summary>
public sealed record EInvoiceSubmissionQueued(Guid SubmissionId, Guid CompanyId) : IDomainEvent;
