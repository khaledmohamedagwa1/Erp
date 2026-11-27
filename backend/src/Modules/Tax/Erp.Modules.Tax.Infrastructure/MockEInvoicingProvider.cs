using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Erp.Modules.Tax.Application;
using Erp.Modules.Tax.Contracts;

namespace Erp.Modules.Tax.Infrastructure;

/// <summary>
/// Development and test adapter for <see cref="IEInvoicingProvider"/>. It validates the document the way authorities typically do and
/// returns a fake clearance reference and QR payload; nothing leaves the process. It is NOT an ETA or ZATCA integration.
/// Behaviour:
/// <list type="bullet">
/// <item>Rejects documents whose totals do not add up, have no lines, or (outside the "sandbox" environment) have no seller tax registration number.</item>
/// <item>Rejects documents whose buyer name contains <c>MOCK-REJECT</c> (to exercise the rejection path).</item>
/// <item>Throws a transient error on the first attempt for a buyer name containing <c>MOCK-TRANSIENT</c> (to exercise outbox retries).</item>
/// </list>
/// </summary>
public sealed class MockEInvoicingProvider : IEInvoicingProvider
{
    private static readonly ConcurrentDictionary<Guid, int> TransientAttempts = new();

    public string Name => MockProviderName.Value;

    public Task<EInvoiceSubmissionResult> SubmitAsync(EInvoiceDocument document, CancellationToken cancellationToken = default)
    {
        if (document.Buyer.Name.Contains("MOCK-TRANSIENT", StringComparison.Ordinal) && TransientAttempts.AddOrUpdate(document.Uuid, 1, (_, n) => n + 1) == 1)
        {
            throw new HttpRequestException("Mock e-invoicing platform temporarily unavailable.");
        }

        var errors = new List<string>();
        if (document.Lines.Count == 0)
        {
            errors.Add("document-has-no-lines");
        }

        if (document.NetTotal + document.TaxTotal != document.GrossTotal || document.Taxes.Sum(t => t.TaxAmount) != document.TaxTotal)
        {
            errors.Add("totals-do-not-add-up");
        }

        if (!string.Equals(document.Environment, "sandbox", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(document.Seller.TaxRegistrationNo))
        {
            errors.Add("seller-tax-registration-missing");
        }

        if (document.Buyer.Name.Contains("MOCK-REJECT", StringComparison.Ordinal))
        {
            errors.Add("buyer-rejected-by-mock");
        }

        if (errors.Count > 0)
        {
            return Task.FromResult(new EInvoiceSubmissionResult(EInvoiceOutcome.Rejected, null, null, errors));
        }

        var reference = $"MOCK-{document.Counter.ToString(CultureInfo.InvariantCulture)}-{document.Uuid.ToString("N")[..8].ToUpperInvariant()}";
        var qr = Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join('|',
            document.Seller.Name, document.Seller.TaxRegistrationNo ?? string.Empty, document.IssueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            document.GrossTotal.ToString("0.00", CultureInfo.InvariantCulture), document.TaxTotal.ToString("0.00", CultureInfo.InvariantCulture), document.Hash ?? string.Empty)));
        return Task.FromResult(new EInvoiceSubmissionResult(EInvoiceOutcome.Accepted, reference, qr, []));
    }
}
