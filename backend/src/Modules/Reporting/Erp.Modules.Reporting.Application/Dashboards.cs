using System.Globalization;
using Erp.Application.Abstractions;
using Erp.Application.Messaging;
using Erp.Modules.Identity.Contracts;
using Erp.Modules.Reporting.Domain;
using Erp.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Reporting.Application;

/// <summary>
/// Turns stored report parameters (query-string names → text) into a <see cref="ReportRequest"/>. Dates may be absolute (yyyy-MM-dd) or relative to
/// today: today, yesterday, month-start, year-start, -Nd, -Nm.
/// </summary>
public static class ReportParameterParser
{
    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        "from", "to", "branchId", "warehouseId", "customerId", "supplierId", "productId", "categoryId", "groupBy", "days", "batchNumber",
    };

    public static Result Validate(IReadOnlyDictionary<string, string>? parameters)
    {
        var request = ToRequest(parameters, DateOnly.FromDateTime(DateTime.UtcNow), 1);
        return request.IsSuccess ? Result.Success() : request.Error;
    }

    public static Result<ReportRequest> ToRequest(IReadOnlyDictionary<string, string>? parameters, DateOnly today, int pageSize)
    {
        var p = new Dictionary<string, string>(parameters ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        if (p.Keys.FirstOrDefault(k => !Known.Contains(k)) is { } unknown)
        {
            return ReportingErrors.InvalidParameter(unknown, "unknown parameter.");
        }

        DateOnly? from = null, to = null;
        Guid? branch = null, warehouse = null, customer = null, supplier = null, product = null, category = null;
        int? days = null;
        foreach (var (name, raw) in p)
        {
            var value = raw?.Trim() ?? string.Empty;
            if (value.Length == 0)
            {
                continue;
            }

            switch (name.ToLowerInvariant())
            {
                case "from":
                case "to":
                    if (ParseDate(value, today) is not { } date)
                    {
                        return ReportingErrors.InvalidParameter(name, "use yyyy-MM-dd, today, yesterday, month-start, year-start, -Nd or -Nm.");
                    }

                    if (name.Equals("from", StringComparison.OrdinalIgnoreCase))
                    {
                        from = date;
                    }
                    else
                    {
                        to = date;
                    }

                    break;
                case "days":
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) || d is < 0 or > 3650)
                    {
                        return ReportingErrors.InvalidParameter(name, "must be a number of days.");
                    }

                    days = d;
                    break;
                case "groupby":
                case "batchnumber":
                    break;
                default:
                    if (!Guid.TryParse(value, out var id))
                    {
                        return ReportingErrors.InvalidParameter(name, "must be an id.");
                    }

                    switch (name.ToLowerInvariant())
                    {
                        case "branchid": branch = id; break;
                        case "warehouseid": warehouse = id; break;
                        case "customerid": customer = id; break;
                        case "supplierid": supplier = id; break;
                        case "productid": product = id; break;
                        default: category = id; break;
                    }

                    break;
            }
        }

        return new ReportRequest(from, to, branch, warehouse, customer, supplier, product, category, p.GetValueOrDefault("groupBy"), days,
            p.GetValueOrDefault("batchNumber"), 1, pageSize);
    }

    public static DateOnly? ParseDate(string value, DateOnly today)
    {
        switch (value.ToLowerInvariant())
        {
            case "today": return today;
            case "yesterday": return today.AddDays(-1);
            case "month-start": return new DateOnly(today.Year, today.Month, 1);
            case "year-start": return new DateOnly(today.Year, 1, 1);
        }

        if (value.Length > 2 && value[0] == '-' && int.TryParse(value[1..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n is >= 0 and <= 3650)
        {
            return char.ToLowerInvariant(value[^1]) switch
            {
                'd' => today.AddDays(-n),
                'm' => today.AddMonths(-n),
                _ => null,
            };
        }

        return DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
    }
}

// ======================= Dashboards =======================
public sealed record WidgetInput(Guid? WidgetId, string Title, string ReportKey, string Visualization, IReadOnlyDictionary<string, string>? Parameters,
    string? LabelColumn, string? ValueColumn, int? RowLimit, int X, int Y, int Width, int Height);

public sealed record WidgetDto(Guid WidgetId, string Title, string ReportKey, string Visualization, IReadOnlyDictionary<string, string> Parameters,
    string? LabelColumn, string? ValueColumn, int RowLimit, int X, int Y, int Width, int Height);

public sealed record DashboardDto(Guid Id, string Name, string? Description, Guid OwnerUserId, bool IsOwner, IReadOnlyList<Guid> SharedWithRoleIds,
    IReadOnlyList<WidgetDto> Widgets);

public sealed record WidgetDataDto(Guid WidgetId, string Title, string Visualization, string? LabelColumn, string? ValueColumn, object? Value, ReportResultDto Result);

public sealed record SaveDashboardCommand(Guid? Id, string Name, string? Description, IReadOnlyList<Guid>? SharedWithRoleIds, IReadOnlyList<WidgetInput> Widgets)
    : ICommand<DashboardDto>;

public sealed record DeleteDashboardCommand(Guid Id) : ICommand<Unit>;

public sealed record ListDashboardsQuery : IQuery<IReadOnlyList<DashboardDto>>;

public sealed record GetDashboardQuery(Guid Id) : IQuery<DashboardDto>;

public sealed record WidgetDataQuery(Guid DashboardId, Guid WidgetId, string? Lang) : IQuery<WidgetDataDto>;

internal static class DashboardMapping
{
    public static DashboardDto ToDto(Dashboard d, Guid userId) => new(d.Id, d.Name, d.Description, d.OwnerUserId, d.OwnerUserId == userId, d.SharedWithRoleIds,
        d.Widgets.Select(ToDto).ToList());

    public static WidgetDto ToDto(DashboardWidget w) => new(w.WidgetId, w.Title, w.ReportKey, w.Visualization.ToString(), w.Parameters, w.LabelColumn, w.ValueColumn,
        w.RowLimit, w.X, w.Y, w.Width, w.Height);
}

public sealed class SaveDashboardValidator : AbstractValidator<SaveDashboardCommand>
{
    public SaveDashboardValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Description).MaximumLength(500);
        RuleFor(x => x.Widgets).NotNull();
        RuleForEach(x => x.Widgets).ChildRules(w =>
        {
            w.RuleFor(x => x.Title).NotEmpty().MaximumLength(100);
            w.RuleFor(x => x.ReportKey).NotEmpty().MaximumLength(100);
            w.RuleFor(x => x.Visualization).Must(v => Enum.TryParse<WidgetVisualization>(v, true, out _))
                .WithMessage("Visualization must be Table, Kpi, Bar, Line or Pie.");
        });
    }
}

/// <summary>Creates (Id null) or replaces a dashboard. Widgets must reference runnable reports with valid parameters; only the owner can change it.</summary>
public sealed class SaveDashboardHandler(IReportingDbContext db, ICurrentUser user, ITenantContext tenant, ReportService reports)
    : ICommandHandler<SaveDashboardCommand, DashboardDto>
{
    public async Task<Result<DashboardDto>> Handle(SaveDashboardCommand command, CancellationToken cancellationToken)
    {
        if (tenant.CompanyId is not { } companyId || user.UserId is not { } userId)
        {
            return ReportingErrors.CompanyRequired;
        }

        var widgets = new List<DashboardWidget>();
        foreach (var input in command.Widgets)
        {
            if (reports.FindRunnable(input.ReportKey.Trim()) is null)
            {
                return ReportingErrors.UnknownReport with { Message = $"Widget '{input.Title}': the report '{input.ReportKey}' does not exist or cannot be used here." };
            }

            var parameters = new Dictionary<string, string>(input.Parameters ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
            var valid = ReportParameterParser.Validate(parameters);
            if (valid.IsFailure)
            {
                return valid.Error;
            }

            widgets.Add(new DashboardWidget
            {
                WidgetId = input.WidgetId ?? Guid.Empty, Title = input.Title, ReportKey = input.ReportKey, Visualization = Enum.Parse<WidgetVisualization>(input.Visualization, true),
                Parameters = parameters, LabelColumn = input.LabelColumn?.Trim(), ValueColumn = input.ValueColumn?.Trim(), RowLimit = input.RowLimit ?? 20,
                X = input.X, Y = input.Y, Width = input.Width, Height = input.Height,
            });
        }

        Dashboard dashboard;
        if (command.Id is { } id)
        {
            var existing = await db.Dashboards.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
            if (existing is null)
            {
                return ReportingErrors.DashboardNotFound;
            }

            if (existing.OwnerUserId != userId)
            {
                return ReportingErrors.NotOwner;
            }

            existing.Update(command.Name, command.Description, command.SharedWithRoleIds ?? [], widgets);
            dashboard = existing;
        }
        else
        {
            dashboard = Dashboard.Create(companyId, userId, command.Name, command.Description, command.SharedWithRoleIds ?? [], widgets);
            db.Dashboards.Add(dashboard);
        }

        await db.SaveChangesAsync(cancellationToken);
        return DashboardMapping.ToDto(dashboard, userId);
    }
}

public sealed class DeleteDashboardHandler(IReportingDbContext db, ICurrentUser user) : ICommandHandler<DeleteDashboardCommand, Unit>
{
    public async Task<Result<Unit>> Handle(DeleteDashboardCommand command, CancellationToken cancellationToken)
    {
        var dashboard = await db.Dashboards.FirstOrDefaultAsync(d => d.Id == command.Id, cancellationToken);
        if (dashboard is null)
        {
            return ReportingErrors.DashboardNotFound;
        }

        if (dashboard.OwnerUserId != user.UserId)
        {
            return ReportingErrors.NotOwner;
        }

        db.Dashboards.Remove(dashboard);
        await db.SaveChangesAsync(cancellationToken);
        return Unit.Value;
    }
}

/// <summary>Dashboards the user owns or that are shared with one of the user's roles (the company filter adds the company).</summary>
public sealed class DashboardAccess(IReportingDbContext db, ICurrentUser user, IUserDirectory users)
{
    public async Task<(Guid UserId, IReadOnlyCollection<Guid> Roles)> CurrentAsync(CancellationToken ct)
    {
        var userId = user.UserId ?? Guid.Empty;
        return (userId, userId == Guid.Empty ? [] : await users.GetRoleIdsAsync(userId, ct));
    }

    public async Task<Result<Dashboard>> GetVisibleAsync(Guid id, CancellationToken ct)
    {
        var (userId, roles) = await CurrentAsync(ct);
        var dashboard = await db.Dashboards.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
        return dashboard is not null && dashboard.IsVisibleTo(userId, roles) ? dashboard : ReportingErrors.DashboardNotFound;
    }
}

public sealed class ListDashboardsHandler(IReportingDbContext db, DashboardAccess access) : IQueryHandler<ListDashboardsQuery, IReadOnlyList<DashboardDto>>
{
    public async Task<Result<IReadOnlyList<DashboardDto>>> Handle(ListDashboardsQuery query, CancellationToken cancellationToken)
    {
        var (userId, roles) = await access.CurrentAsync(cancellationToken);
        var all = await db.Dashboards.AsNoTracking().OrderBy(d => d.Name).ToListAsync(cancellationToken);
        return all.Where(d => d.IsVisibleTo(userId, roles)).Select(d => DashboardMapping.ToDto(d, userId)).ToList();
    }
}

public sealed class GetDashboardHandler(DashboardAccess access) : IQueryHandler<GetDashboardQuery, DashboardDto>
{
    public async Task<Result<DashboardDto>> Handle(GetDashboardQuery query, CancellationToken cancellationToken)
    {
        var dashboard = await access.GetVisibleAsync(query.Id, cancellationToken);
        if (dashboard.IsFailure)
        {
            return dashboard.Error;
        }

        var (userId, _) = await access.CurrentAsync(cancellationToken);
        return DashboardMapping.ToDto(dashboard.Value, userId);
    }
}

/// <summary>
/// Runs a widget's report for the viewer, with the viewer's permissions: a shared dashboard never reveals a report the viewer may not run.
/// </summary>
public sealed class WidgetDataHandler(DashboardAccess access, ReportService reports, IClock clock) : IQueryHandler<WidgetDataQuery, WidgetDataDto>
{
    public async Task<Result<WidgetDataDto>> Handle(WidgetDataQuery query, CancellationToken cancellationToken)
    {
        var dashboard = await access.GetVisibleAsync(query.DashboardId, cancellationToken);
        if (dashboard.IsFailure)
        {
            return dashboard.Error;
        }

        var widget = dashboard.Value.Widgets.FirstOrDefault(w => w.WidgetId == query.WidgetId);
        if (widget is null)
        {
            return ReportingErrors.WidgetNotFound;
        }

        var request = ReportParameterParser.ToRequest(widget.Parameters, DateOnly.FromDateTime(clock.UtcNow), widget.RowLimit);
        if (request.IsFailure)
        {
            return request.Error;
        }

        var parts = widget.ReportKey.Split('/', 2);
        if (parts.Length != 2)
        {
            return ReportingErrors.UnknownReport;
        }

        var run = await reports.RunAsync(parts[0], parts[1], request.Value, forExport: false,
            arabic: string.Equals(query.Lang, "ar", StringComparison.OrdinalIgnoreCase), cancellationToken);
        if (run.IsFailure)
        {
            return run.Error;
        }

        var result = ReportService.ToDto(run.Value);
        object? value = null;
        if (widget.ValueColumn is { } column)
        {
            value = result.Totals is { } totals && totals.TryGetValue(column, out var total) && total is not null ? total
                : result.Rows.FirstOrDefault() is { } first && first.TryGetValue(column, out var cell) ? cell : null;
        }

        return new WidgetDataDto(widget.WidgetId, widget.Title, widget.Visualization.ToString(), widget.LabelColumn, widget.ValueColumn, value, result);
    }
}
