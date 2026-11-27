namespace Erp.Modules.Parties.Contracts;

public sealed record CustomerInfo(
    Guid Id,
    string Code,
    string NameEn,
    string NameAr,
    string Currency,
    Guid? PriceListId,
    Guid? PaymentTermsId,
    string PostingGroup,
    decimal CreditLimit,
    string CreditAction,
    bool OnHold,
    bool IsActive,
    bool IsWalkIn,
    string? TaxRegistrationNo = null,
    Guid? SalespersonId = null);

public sealed record SupplierInfo(
    Guid Id,
    string Code,
    string NameEn,
    string NameAr,
    string Currency,
    Guid? PaymentTermsId,
    string PostingGroup,
    bool IsActive);

public sealed record DueInstallment(DateOnly DueDate, decimal Amount);

/// <summary>Read access to customers, suppliers and payment terms for other modules (sales, procurement, finance).</summary>
public interface IPartyQueries
{
    /// <summary>Customer of the active company, or null.</summary>
    Task<CustomerInfo?> GetCustomerAsync(Guid customerId, CancellationToken cancellationToken = default);

    /// <summary>Supplier of the active company, or null.</summary>
    Task<SupplierInfo?> GetSupplierAsync(Guid supplierId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Splits <paramref name="amount"/> into due installments. Each installment is rounded to <paramref name="decimals"/>
    /// (half away from zero); the last one takes the remainder so the schedule always sums to the amount.
    /// Without payment terms the whole amount is due on the invoice date.
    /// </summary>
    Task<IReadOnlyList<DueInstallment>> GetDueScheduleAsync(Guid? paymentTermsId, DateOnly invoiceDate, decimal amount, int decimals = 2, CancellationToken cancellationToken = default);

    /// <summary>
    /// The company's walk-in (cash) customer used by POS and cash sales. Created on first request (idempotent;
    /// at most one per company is guaranteed by a unique index).
    /// </summary>
    Task<Guid> GetWalkInCustomerIdAsync(Guid companyId, CancellationToken cancellationToken = default);
}

public static class PartiesPermissions
{
    public const string SetupRead = "parties.setup.read";
    public const string SetupManage = "parties.setup.manage";
    public const string CustomerRead = "parties.customer.read";
    public const string CustomerManage = "parties.customer.manage";
    public const string CustomerCredit = "parties.customer.credit";
    public const string SupplierRead = "parties.supplier.read";
    public const string SupplierManage = "parties.supplier.manage";
}

public static class CreditActions
{
    public const string Warn = "Warn";
    public const string RequireApproval = "RequireApproval";
    public const string Block = "Block";

    public static readonly IReadOnlyCollection<string> All = [Warn, RequireApproval, Block];
}
