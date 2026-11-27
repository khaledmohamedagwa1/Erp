using Erp.SharedKernel.Documents;

namespace Erp.Modules.Finance.Contracts;

/// <summary>Printable finance documents for the Reporting module.</summary>
public interface IFinanceDocumentQueries
{
    /// <summary>Printable posted receipt or payment voucher with its allocations; null when it does not exist or is not posted.</summary>
    Task<PrintableDocument?> GetSettlementPrintAsync(Guid settlementId, CancellationToken cancellationToken = default);

    /// <summary>Printable withholding tax certificate (active or cancelled); null when it does not exist.</summary>
    Task<PrintableDocument?> GetWithholdingCertificatePrintAsync(Guid certificateId, CancellationToken cancellationToken = default);
}
