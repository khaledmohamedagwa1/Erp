using Erp.SharedKernel.Domain;
using Erp.SharedKernel.Results;

namespace Erp.Modules.Reporting.Domain;

public enum WidgetVisualization
{
    Table = 1,
    Kpi = 2,
    Bar = 3,
    Line = 4,
    Pie = 5,
}

/// <summary>
/// One tile of a dashboard: a report (area/key) run with fixed parameters, drawn as a table, a KPI or a chart.
/// Parameters use the report query-string names; dates may be relative (today, month-start, year-start, -30d, -3m).
/// </summary>
public sealed class DashboardWidget
{
    public Guid WidgetId { get; set; }

    public string Title { get; set; } = string.Empty;

    public string ReportKey { get; set; } = string.Empty;

    public WidgetVisualization Visualization { get; set; }

    public Dictionary<string, string> Parameters { get; set; } = [];

    /// <summary>Column used as the category axis / label (charts) — optional.</summary>
    public string? LabelColumn { get; set; }

    /// <summary>Column used as the value (charts, KPI) — optional; KPIs read it from the totals row.</summary>
    public string? ValueColumn { get; set; }

    /// <summary>Maximum number of rows returned to the widget.</summary>
    public int RowLimit { get; set; } = 20;

    public int X { get; set; }

    public int Y { get; set; }

    public int Width { get; set; } = 4;

    public int Height { get; set; } = 3;
}

/// <summary>A user's dashboard of report widgets, optionally shared (read-only) with roles of the same company.</summary>
[Audited]
public sealed class Dashboard : AggregateRoot, ICompanyScoped
{
    public const int MaxWidgets = 30;

    private Dashboard()
    {
    }

    private Dashboard(Guid id, Guid companyId, Guid ownerUserId)
        : base(id)
    {
        CompanyId = companyId;
        OwnerUserId = ownerUserId;
    }

    public Guid CompanyId { get; private set; }

    public Guid OwnerUserId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public List<Guid> SharedWithRoleIds { get; private set; } = [];

    public List<DashboardWidget> Widgets { get; private set; } = [];

    public static Dashboard Create(Guid companyId, Guid ownerUserId, string name, string? description, IEnumerable<Guid> sharedWithRoleIds,
        IEnumerable<DashboardWidget> widgets)
    {
        var dashboard = new Dashboard(SequentialGuid.New(), companyId, ownerUserId);
        dashboard.Update(name, description, sharedWithRoleIds, widgets);
        return dashboard;
    }

    public void Update(string name, string? description, IEnumerable<Guid> sharedWithRoleIds, IEnumerable<DashboardWidget> widgets)
    {
        var list = widgets.ToList();
        if (list.Count > MaxWidgets)
        {
            throw new DomainException(Error.Validation("reporting.dashboard-too-many-widgets", $"A dashboard can have at most {MaxWidgets} widgets."));
        }

        foreach (var widget in list)
        {
            if (widget.WidgetId == Guid.Empty)
            {
                widget.WidgetId = SequentialGuid.New();
            }

            widget.Title = Guard.MaxLength(Guard.NotEmpty(widget.Title, "reporting.widget-title-required", "Widget title").Trim(), 100, "reporting.widget-title-too-long", "Widget title");
            widget.ReportKey = Guard.NotEmpty(widget.ReportKey, "reporting.widget-report-required", "Widget report").Trim().ToLowerInvariant();
            widget.RowLimit = Math.Clamp(widget.RowLimit, 1, 500);
            if (widget.Width is < 1 or > 12 || widget.Height is < 1 or > 12 || widget.X is < 0 or > 11 || widget.Y < 0)
            {
                throw new DomainException(Error.Validation("reporting.widget-layout-invalid", "Widget position and size must fit a 12-column grid."));
            }
        }

        if (list.Select(w => w.WidgetId).Distinct().Count() != list.Count)
        {
            throw new DomainException(Error.Validation("reporting.widget-duplicate", "Widget ids must be unique within a dashboard."));
        }

        Name = Guard.MaxLength(Guard.NotEmpty(name, "reporting.dashboard-name-required", "Name").Trim(), 100, "reporting.dashboard-name-too-long", "Name");
        Description = string.IsNullOrWhiteSpace(description) ? null : Guard.MaxLength(description.Trim(), 500, "reporting.dashboard-description-too-long", "Description");
        SharedWithRoleIds = sharedWithRoleIds.Where(r => r != Guid.Empty).Distinct().ToList();
        Widgets = list;
    }

    public bool IsVisibleTo(Guid userId, IReadOnlyCollection<Guid> roleIds) => OwnerUserId == userId || SharedWithRoleIds.Any(roleIds.Contains);
}

/// <summary>A minimal five-field cron expression (minute hour day-of-month month day-of-week), evaluated in UTC.</summary>
public sealed class CronExpression
{
    private readonly bool[] _minutes;
    private readonly bool[] _hours;
    private readonly bool[] _days;
    private readonly bool[] _months;
    private readonly bool[] _weekdays;
    private readonly bool _anyDay;
    private readonly bool _anyWeekday;

    private CronExpression(string text, bool[] minutes, bool[] hours, bool[] days, bool[] months, bool[] weekdays, bool anyDay, bool anyWeekday)
    {
        Text = text;
        _minutes = minutes;
        _hours = hours;
        _days = days;
        _months = months;
        _weekdays = weekdays;
        _anyDay = anyDay;
        _anyWeekday = anyWeekday;
    }

    public string Text { get; }

    public static readonly Error Invalid = Error.Validation("reporting.cron-invalid",
        "The schedule must be a five-field cron expression (minute hour day-of-month month day-of-week), e.g. \"0 6 * * 1-5\".");

    public static bool TryParse(string? text, out CronExpression? expression)
    {
        expression = null;
        var parts = (text ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5)
        {
            return false;
        }

        if (!TryField(parts[0], 0, 59, out var minutes) || !TryField(parts[1], 0, 23, out var hours) || !TryField(parts[2], 1, 31, out var days) ||
            !TryField(parts[3], 1, 12, out var months) || !TryField(parts[4], 0, 7, out var weekdays))
        {
            return false;
        }

        weekdays[0] |= weekdays[7];
        expression = new CronExpression(string.Join(' ', parts), minutes, hours, days, months, weekdays, parts[2] == "*", parts[4] == "*");
        return true;
    }

    public static CronExpression Parse(string text) => TryParse(text, out var expression) ? expression! : throw new DomainException(Invalid);

    /// <summary>The first matching minute strictly after <paramref name="afterUtc"/>, or null when none within five years.</summary>
    public DateTime? Next(DateTime afterUtc)
    {
        var start = new DateTime(afterUtc.Year, afterUtc.Month, afterUtc.Day, afterUtc.Hour, afterUtc.Minute, 0, DateTimeKind.Utc).AddMinutes(1);
        var day = start.Date;
        for (var i = 0; i < 366 * 5; i++, day = day.AddDays(1))
        {
            if (!_months[day.Month] || !DayMatches(day))
            {
                continue;
            }

            var firstHour = day == start.Date ? start.Hour : 0;
            for (var hour = firstHour; hour < 24; hour++)
            {
                if (!_hours[hour])
                {
                    continue;
                }

                var firstMinute = day == start.Date && hour == start.Hour ? start.Minute : 0;
                for (var minute = firstMinute; minute < 60; minute++)
                {
                    if (_minutes[minute])
                    {
                        return new DateTime(day.Year, day.Month, day.Day, hour, minute, 0, DateTimeKind.Utc);
                    }
                }
            }
        }

        return null;
    }

    // Standard cron: when both day-of-month and day-of-week are restricted, either may match.
    private bool DayMatches(DateTime day)
    {
        var dom = _days[day.Day];
        var dow = _weekdays[(int)day.DayOfWeek];
        if (_anyDay)
        {
            return _anyWeekday || dow;
        }

        return _anyWeekday ? dom : dom || dow;
    }

    private static bool TryField(string field, int min, int max, out bool[] allowed)
    {
        allowed = new bool[max + 1];
        foreach (var item in field.Split(','))
        {
            var step = 1;
            var range = item;
            var slash = item.IndexOf('/');
            if (slash >= 0)
            {
                if (!int.TryParse(item[(slash + 1)..], out step) || step < 1)
                {
                    return false;
                }

                range = item[..slash];
            }

            int from, to;
            if (range == "*")
            {
                (from, to) = (min, max);
            }
            else if (range.Contains('-'))
            {
                var bounds = range.Split('-');
                if (bounds.Length != 2 || !int.TryParse(bounds[0], out from) || !int.TryParse(bounds[1], out to))
                {
                    return false;
                }
            }
            else
            {
                if (!int.TryParse(range, out from))
                {
                    return false;
                }

                to = slash >= 0 ? max : from;
            }

            if (from < min || to > max || from > to)
            {
                return false;
            }

            for (var v = from; v <= to; v += step)
            {
                allowed[v] = true;
            }
        }

        return true;
    }
}

public enum ScheduleRunStatus
{
    Succeeded = 1,
    Failed = 2,
}

/// <summary>
/// A report delivered on a cron schedule. It runs with the owner's permissions (if the owner loses access, runs fail and are recorded),
/// keeps the output of every run in the run history, and emails the recipients a link to it.
/// </summary>
[Audited]
public sealed class ReportSchedule : AggregateRoot, ICompanyScoped
{
    public static readonly IReadOnlyList<string> Formats = ["csv", "xlsx", "html", "pdf"];

    private ReportSchedule()
    {
    }

    private ReportSchedule(Guid id, Guid companyId, Guid ownerUserId)
        : base(id)
    {
        CompanyId = companyId;
        OwnerUserId = ownerUserId;
    }

    public Guid CompanyId { get; private set; }

    public Guid OwnerUserId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string ReportKey { get; private set; } = string.Empty;

    public Dictionary<string, string> Parameters { get; private set; } = [];

    public string Format { get; private set; } = "csv";

    public string Language { get; private set; } = "en";

    /// <summary>Five-field cron expression, UTC.</summary>
    public string Cron { get; private set; } = string.Empty;

    public List<string> Recipients { get; private set; } = [];

    public bool IsActive { get; private set; }

    public DateTime? NextRunAtUtc { get; private set; }

    public DateTime? LastRunAtUtc { get; private set; }

    public ScheduleRunStatus? LastStatus { get; private set; }

    public static ReportSchedule Create(Guid companyId, Guid ownerUserId, string name, string reportKey, IDictionary<string, string> parameters, string format,
        string? language, string cron, IEnumerable<string> recipients, bool isActive, DateTime nowUtc)
    {
        var schedule = new ReportSchedule(SequentialGuid.New(), companyId, ownerUserId);
        schedule.Update(name, reportKey, parameters, format, language, cron, recipients, isActive, nowUtc);
        return schedule;
    }

    public void Update(string name, string reportKey, IDictionary<string, string> parameters, string format, string? language, string cron,
        IEnumerable<string> recipients, bool isActive, DateTime nowUtc)
    {
        var expression = CronExpression.Parse(cron);
        var normalizedFormat = (format ?? string.Empty).Trim().ToLowerInvariant();
        if (!Formats.Contains(normalizedFormat))
        {
            throw new DomainException(Error.Validation("reporting.schedule-format-invalid", "Format must be csv, xlsx, html or pdf."));
        }

        var list = recipients.Select(r => r.Trim()).Where(r => r.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count > 20 || list.Any(r => r.Length > 200 || !r.Contains('@', StringComparison.Ordinal)))
        {
            throw new DomainException(Error.Validation("reporting.schedule-recipients-invalid", "Recipients must be at most 20 email addresses."));
        }

        Name = Guard.MaxLength(Guard.NotEmpty(name, "reporting.schedule-name-required", "Name").Trim(), 100, "reporting.schedule-name-too-long", "Name");
        ReportKey = Guard.NotEmpty(reportKey, "reporting.schedule-report-required", "Report").Trim().ToLowerInvariant();
        Parameters = new Dictionary<string, string>(parameters, StringComparer.OrdinalIgnoreCase);
        Format = normalizedFormat;
        Language = string.Equals(language?.Trim(), "ar", StringComparison.OrdinalIgnoreCase) ? "ar" : "en";
        Cron = expression.Text;
        Recipients = list;
        IsActive = isActive;
        NextRunAtUtc = isActive ? expression.Next(nowUtc) : null;
    }

    public bool IsDue(DateTime nowUtc) => IsActive && NextRunAtUtc is { } next && next <= nowUtc;

    /// <summary>Claims the due run: moves <see cref="NextRunAtUtc"/> on, so a second worker skips it (the rowversion check makes the claim exclusive).</summary>
    public void Advance(DateTime nowUtc) => NextRunAtUtc = IsActive ? CronExpression.Parse(Cron).Next(nowUtc) : null;

    public void RecordRun(DateTime nowUtc, ScheduleRunStatus status)
    {
        LastRunAtUtc = nowUtc;
        LastStatus = status;
    }
}

/// <summary>One execution of a scheduled report, with its output (kept for download).</summary>
public sealed class ReportScheduleRun : AggregateRoot, ICompanyScoped
{
    public const int MaxContentBytes = 10 * 1024 * 1024;

    private ReportScheduleRun()
    {
    }

    public Guid CompanyId { get; private set; }

    public Guid ScheduleId { get; private set; }

    /// <summary>Schedule or Manual.</summary>
    public string Trigger { get; private set; } = string.Empty;

    public DateTime StartedAtUtc { get; private set; }

    public DateTime? FinishedAtUtc { get; private set; }

    public ScheduleRunStatus Status { get; private set; }

    public int RowCount { get; private set; }

    public string? Error { get; private set; }

    public string? FileName { get; private set; }

    public string? ContentType { get; private set; }

    public byte[]? Content { get; private set; }

    public static ReportScheduleRun Succeeded(ReportSchedule schedule, string trigger, DateTime startedUtc, DateTime finishedUtc, int rows, string fileName,
        string contentType, byte[] content) => new()
    {
        Id = SequentialGuid.New(), CompanyId = schedule.CompanyId, ScheduleId = schedule.Id, Trigger = trigger, StartedAtUtc = startedUtc, FinishedAtUtc = finishedUtc,
        Status = ScheduleRunStatus.Succeeded, RowCount = rows, FileName = fileName, ContentType = contentType, Content = content,
    };

    public static ReportScheduleRun Failed(ReportSchedule schedule, string trigger, DateTime startedUtc, DateTime finishedUtc, string error) => new()
    {
        Id = SequentialGuid.New(), CompanyId = schedule.CompanyId, ScheduleId = schedule.Id, Trigger = trigger, StartedAtUtc = startedUtc, FinishedAtUtc = finishedUtc,
        Status = ScheduleRunStatus.Failed, Error = error.Length > 1000 ? error[..1000] : error,
    };
}

/// <summary>Read model: posted sales per day and branch, kept current by the JournalPosted handler (rebuildable).</summary>
public sealed class DailySalesSummary : AggregateRoot, ICompanyScoped
{
    private DailySalesSummary()
    {
    }

    public Guid CompanyId { get; private set; }

    public DateOnly Date { get; private set; }

    /// <summary>Guid.Empty when the documents have no branch.</summary>
    public Guid BranchKey { get; private set; }

    public int Invoices { get; private set; }

    public int CreditNotes { get; private set; }

    public decimal GrossSales { get; private set; }

    public decimal Returns { get; private set; }

    public decimal NetRevenue { get; private set; }

    public decimal Tax { get; private set; }

    public decimal Cogs { get; private set; }

    public DateTime UpdatedAtUtc { get; private set; }

    public static DailySalesSummary Create(Guid companyId, DateOnly date, Guid branchKey, int invoices, int creditNotes, decimal grossSales, decimal returns,
        decimal tax, decimal cogs, DateTime nowUtc) => new()
    {
        Id = SequentialGuid.New(), CompanyId = companyId, Date = date, BranchKey = branchKey, Invoices = invoices, CreditNotes = creditNotes, GrossSales = grossSales,
        Returns = returns, NetRevenue = grossSales + returns, Tax = tax, Cogs = cogs, UpdatedAtUtc = nowUtc,
    };
}

/// <summary>Read model: stock quantity and value per warehouse captured on a day (the latest capture of the day wins).</summary>
public sealed class StockValueSnapshot : AggregateRoot, ICompanyScoped
{
    private StockValueSnapshot()
    {
    }

    public Guid CompanyId { get; private set; }

    public DateOnly Date { get; private set; }

    public Guid WarehouseId { get; private set; }

    public decimal Quantity { get; private set; }

    public decimal Value { get; private set; }

    public DateTime UpdatedAtUtc { get; private set; }

    public static StockValueSnapshot Create(Guid companyId, DateOnly date, Guid warehouseId, decimal quantity, decimal value, DateTime nowUtc) => new()
    {
        Id = SequentialGuid.New(), CompanyId = companyId, Date = date, WarehouseId = warehouseId, Quantity = quantity, Value = value, UpdatedAtUtc = nowUtc,
    };
}
