using Erp.SharedKernel.Domain;

namespace Erp.Modules.Workflow.Contracts;

public static class WorkflowPermissions
{
    public const string DefinitionRead = "workflow.definition.read";
    public const string DefinitionManage = "workflow.definition.manage";
    public const string Approve = "workflow.approve";
}

/// <summary>Document types that can go through approval. Modules use these exact strings.</summary>
public static class ApprovalDocumentTypes
{
    public const string PurchaseRequest = "procurement.purchase-request";
    public const string PurchaseOrder = "procurement.purchase-order";
    public const string PurchaseInvoice = "procurement.purchase-invoice";
    public const string SalesOrder = "sales.sales-order";
    public const string SalesInvoice = "sales.sales-invoice";
    public const string SalesReturn = "sales.sales-return";
    public const string StockAdjustment = "inventory.stock-adjustment";
    public const string StockTransfer = "inventory.stock-transfer";
    public const string Expense = "finance.expense";
    public const string ProductionOrder = "manufacturing.production-order";
}

/// <summary>
/// Facts about a document submitted for approval. Rules are evaluated against these (amount, branch, discount, …).
/// The workflow engine snapshots them so later edits to the document do not change an in-flight decision.
/// </summary>
public sealed record ApprovalRequest(
    string DocumentType,
    Guid DocumentId,
    string DocumentNumber,
    Guid CompanyId,
    decimal Amount,
    string Currency,
    Guid RequestedBy,
    Guid? BranchId = null,
    IReadOnlyDictionary<string, decimal>? NumericFacts = null,
    IReadOnlyDictionary<string, string>? TextFacts = null);

/// <param name="Required">False when no approval rule matched: the caller moves the document straight to Approved.</param>
/// <param name="InstanceId">The approval instance when <paramref name="Required"/> is true.</param>
public sealed record ApprovalDecision(bool Required, Guid? InstanceId);

/// <summary>
/// Generic approval engine (Part 16). Approval never posts anything: when an instance completes, the engine publishes
/// <see cref="ApprovalCompleted"/> and the owning module moves its document to Approved or back to Draft.
/// Runs inside the caller's transaction.
/// </summary>
public interface IApprovalService
{
    Task<ApprovalDecision> SubmitAsync(ApprovalRequest request, CancellationToken cancellationToken = default);

    /// <summary>Withdraws an open approval instance (e.g. the document was edited or cancelled). No-op if none.</summary>
    Task WithdrawAsync(string documentType, Guid documentId, CancellationToken cancellationToken = default);
}

/// <summary>Published by the workflow engine (outbox) when an approval instance is approved or rejected.</summary>
public sealed record ApprovalCompleted(string DocumentType, Guid DocumentId, bool Approved, Guid DecidedBy, string? Reason) : IDomainEvent;

/// <summary>
/// Default used until the Workflow module is installed: nothing requires approval. Modules register it with TryAdd;
/// the Workflow module registers the real engine, which then takes precedence.
/// </summary>
public sealed class NoApprovalRequired : IApprovalService
{
    public Task<ApprovalDecision> SubmitAsync(ApprovalRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ApprovalDecision(false, null));

    public Task WithdrawAsync(string documentType, Guid documentId, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
