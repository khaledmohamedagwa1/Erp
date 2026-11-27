using System.Text.Json;
using Erp.SharedKernel.Domain;
using Erp.SharedKernel.Results;

namespace Erp.Modules.Reporting.Domain;

/// <summary>A user's named set of filters for one report ("saved view"). Private to the user, inside one company.</summary>
public sealed class SavedReportView : AggregateRoot, ICompanyScoped
{
    public const int MaxFiltersLength = 4000;

    private SavedReportView()
    {
    }

    public Guid CompanyId { get; private set; }

    public Guid UserId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    /// <summary>"area/report", e.g. <c>sales/by-product</c>.</summary>
    public string ReportKey { get; private set; } = string.Empty;

    /// <summary>Filter values as a JSON object (query-string parameter name → value).</summary>
    public string FiltersJson { get; private set; } = "{}";

    public DateTime CreatedAtUtc { get; private set; }

    public static SavedReportView Create(Guid companyId, Guid userId, string? name, string? reportKey, string? filtersJson, DateTime nowUtc)
    {
        var json = string.IsNullOrWhiteSpace(filtersJson) ? "{}" : filtersJson.Trim();
        if (json.Length > MaxFiltersLength)
        {
            throw new DomainException(Error.Validation("reporting.view-filters-too-long", $"Filters must be at most {MaxFiltersLength} characters."));
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new DomainException(Error.Validation("reporting.view-filters-invalid", "Filters must be a JSON object."));
            }
        }
        catch (JsonException)
        {
            throw new DomainException(Error.Validation("reporting.view-filters-invalid", "Filters must be a JSON object."));
        }

        return new SavedReportView
        {
            Id = SequentialGuid.New(),
            CompanyId = companyId,
            UserId = userId,
            Name = Guard.MaxLength(Guard.NotEmpty(name, "reporting.view-name-required", "Name").Trim(), 100, "reporting.view-name-too-long", "Name"),
            ReportKey = Guard.MaxLength(Guard.NotEmpty(reportKey, "reporting.view-report-required", "Report").Trim().ToLowerInvariant(), 100,
                "reporting.view-report-too-long", "Report"),
            FiltersJson = json,
            CreatedAtUtc = nowUtc,
        };
    }
}
