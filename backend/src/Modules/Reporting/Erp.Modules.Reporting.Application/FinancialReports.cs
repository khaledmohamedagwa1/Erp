using Erp.Modules.Accounting.Contracts;
using Erp.Modules.Reporting.Contracts;
using Erp.SharedKernel.Results;

namespace Erp.Modules.Reporting.Application;

/// <summary>
/// Financial statements live in Accounting; the catalogue links to them instead of duplicating them. Requests to
/// <c>/api/v1/reports/financial/{key}</c> are redirected to the Accounting endpoint with the same query string.
/// </summary>
public sealed class FinancialReports : IReportArea
{
    private const string Source = "Accounting general ledger (Accounting module statements)";

    public string Area => ReportAreas.Financial;

    public bool IsAvailable => true;

    public IReadOnlyList<ReportDefinition> Definitions { get; } =
    [
        Link("trial-balance", "Trial balance", "ميزان المراجعة", "Opening, movement and closing balance per account.", ["legalEntityId", "from", "to"]),
        Link("income-statement", "Income statement", "قائمة الدخل", "Revenue, expenses and profit for a period.", ["legalEntityId", "from", "to"]),
        Link("balance-sheet", "Balance sheet", "الميزانية العمومية", "Assets, liabilities and equity at a date.", ["legalEntityId", "asOf"]),
        Link("cash-flow", "Cash flow statement", "قائمة التدفقات النقدية", "Cash movements by activity for a period.", ["legalEntityId", "from", "to"]),
    ];

    public Task<Result<ReportOutput>> RunAsync(string key, ReportQuery query, CancellationToken cancellationToken) =>
        Task.FromResult<Result<ReportOutput>>(ReportingErrors.UnknownReport);

    private static ReportDefinition Link(string key, string en, string ar, string description, IReadOnlyList<string> filters) =>
        new(ReportAreas.Financial, key, en, ar, description, Source, AccountingPermissions.ReportRead, "accounting", filters, $"/api/v1/accounting/reports/{key}");
}
