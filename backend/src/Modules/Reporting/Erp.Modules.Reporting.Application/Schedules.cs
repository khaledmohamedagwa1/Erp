using Erp.Application.Abstractions;
using Erp.Application.Messaging;
using Erp.Modules.Reporting.Domain;
using Erp.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Reporting.Application;

/// <summary>Renders a report table to a file (CSV, XLSX, HTML or PDF). Implemented in infrastructure next to the writers.</summary>
public interface IReportFileRenderer
{
    Task<Result<(byte[] Content, string ContentType, string Extension)>> RenderAsync(ReportRun run, string format, bool arabic, CancellationToken ct);
}

public sealed record ScheduleInput(string Name, string ReportKey, IReadOnlyDictionary<string, string>? Parameters, string Format, string? Language, string Cron,
    IReadOnlyList<string>? Recipients, bool IsActive);

public sealed record ScheduleDto(Guid Id, string Name, string ReportKey, IReadOnlyDictionary<string, string> Parameters, string Format, string Language, string Cron,
    IReadOnlyList<string> Recipients, bool IsActive, Guid OwnerUserId, DateTime? NextRunAtUtc, DateTime? LastRunAtUtc, string? LastStatus);

public sealed record ScheduleRunDto(Guid Id, Guid ScheduleId, string Trigger, DateTime StartedAtUtc, DateTime? FinishedAtUtc, string Status, int RowCount, string? Error,
    string? FileName, string? DownloadUrl);

public sealed record CreateScheduleCommand(ScheduleInput Schedule) : ICommand<ScheduleDto>;

public sealed record UpdateScheduleCommand(Guid Id, ScheduleInput Schedule) : ICommand<ScheduleDto>;

public sealed record DeleteScheduleCommand(Guid Id) : ICommand<Unit>;

public sealed record ListSchedulesQuery : IQuery<IReadOnlyList<ScheduleDto>>;

public sealed record ListScheduleRunsQuery(Guid ScheduleId) : IQuery<IReadOnlyList<ScheduleRunDto>>;

public sealed record ScheduleRunFile(string FileName, string ContentType, byte[] Content);

public sealed record GetScheduleRunFileQuery(Guid ScheduleId, Guid RunId) : IQuery<ScheduleRunFile>;

internal static class ScheduleMapping
{
    public static ScheduleDto ToDto(ReportSchedule s) => new(s.Id, s.Name, s.ReportKey, s.Parameters, s.Format, s.Language, s.Cron, s.Recipients, s.IsActive,
        s.OwnerUserId, s.NextRunAtUtc, s.LastRunAtUtc, s.LastStatus?.ToString());

    public static ScheduleRunDto ToDto(ReportScheduleRun r) => new(r.Id, r.ScheduleId, r.Trigger, r.StartedAtUtc, r.FinishedAtUtc, r.Status.ToString(), r.RowCount,
        r.Error, r.FileName, r.Content is null ? null : DownloadUrl(r.ScheduleId, r.Id));

    public static string DownloadUrl(Guid scheduleId, Guid runId) => $"/api/v1/reports/schedules/{scheduleId}/runs/{runId}/file";
}

public sealed class ScheduleInputValidator : AbstractValidator<ScheduleInput>
{
    public ScheduleInputValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.ReportKey).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Format).NotEmpty().Must(f => ReportSchedule.Formats.Contains(f.Trim().ToLowerInvariant())).WithMessage("Format must be csv, xlsx, html or pdf.");
        RuleFor(x => x.Cron).NotEmpty().Must(c => CronExpression.TryParse(c, out _)).WithMessage(CronExpression.Invalid.Message);
    }
}

public sealed class CreateScheduleValidator : AbstractValidator<CreateScheduleCommand>
{
    public CreateScheduleValidator() => RuleFor(x => x.Schedule).NotNull().SetValidator(new ScheduleInputValidator());
}

public sealed class UpdateScheduleValidator : AbstractValidator<UpdateScheduleCommand>
{
    public UpdateScheduleValidator() => RuleFor(x => x.Schedule).NotNull().SetValidator(new ScheduleInputValidator());
}

internal static class ScheduleRules
{
    /// <summary>The report must exist and the owner must be allowed to run it now (it will run with the owner's permissions).</summary>
    public static async Task<Result> CheckAsync(ScheduleInput input, ReportService reports, CancellationToken ct)
    {
        var parts = input.ReportKey.Trim().ToLowerInvariant().Split('/', 2);
        if (parts.Length != 2 || reports.FindRunnable(input.ReportKey.Trim()) is null)
        {
            return ReportingErrors.UnknownReport;
        }

        var authorized = await reports.AuthorizeAsync(parts[0], parts[1], ct);
        if (authorized.IsFailure)
        {
            return authorized.Error;
        }

        return ReportParameterParser.Validate(input.Parameters);
    }
}

public sealed class CreateScheduleHandler(IReportingDbContext db, ICurrentUser user, ITenantContext tenant, ReportService reports, IClock clock)
    : ICommandHandler<CreateScheduleCommand, ScheduleDto>
{
    public async Task<Result<ScheduleDto>> Handle(CreateScheduleCommand command, CancellationToken cancellationToken)
    {
        if (tenant.CompanyId is not { } companyId || user.UserId is not { } userId)
        {
            return ReportingErrors.CompanyRequired;
        }

        var input = command.Schedule;
        var check = await ScheduleRules.CheckAsync(input, reports, cancellationToken);
        if (check.IsFailure)
        {
            return check.Error;
        }

        var schedule = ReportSchedule.Create(companyId, userId, input.Name, input.ReportKey, new Dictionary<string, string>(input.Parameters ?? new Dictionary<string, string>()),
            input.Format, input.Language, input.Cron, input.Recipients ?? [], input.IsActive, clock.UtcNow);
        db.Schedules.Add(schedule);
        await db.SaveChangesAsync(cancellationToken);
        return ScheduleMapping.ToDto(schedule);
    }
}

public sealed class UpdateScheduleHandler(IReportingDbContext db, ICurrentUser user, ReportService reports, IClock clock) : ICommandHandler<UpdateScheduleCommand, ScheduleDto>
{
    public async Task<Result<ScheduleDto>> Handle(UpdateScheduleCommand command, CancellationToken cancellationToken)
    {
        var schedule = await db.Schedules.FirstOrDefaultAsync(s => s.Id == command.Id && s.OwnerUserId == user.UserId, cancellationToken);
        if (schedule is null)
        {
            return ReportingErrors.ScheduleNotFound;
        }

        var input = command.Schedule;
        var check = await ScheduleRules.CheckAsync(input, reports, cancellationToken);
        if (check.IsFailure)
        {
            return check.Error;
        }

        schedule.Update(input.Name, input.ReportKey, new Dictionary<string, string>(input.Parameters ?? new Dictionary<string, string>()), input.Format, input.Language,
            input.Cron, input.Recipients ?? [], input.IsActive, clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        return ScheduleMapping.ToDto(schedule);
    }
}

public sealed class DeleteScheduleHandler(IReportingDbContext db, ICurrentUser user) : ICommandHandler<DeleteScheduleCommand, Unit>
{
    public async Task<Result<Unit>> Handle(DeleteScheduleCommand command, CancellationToken cancellationToken)
    {
        var schedule = await db.Schedules.FirstOrDefaultAsync(s => s.Id == command.Id && s.OwnerUserId == user.UserId, cancellationToken);
        if (schedule is null)
        {
            return ReportingErrors.ScheduleNotFound;
        }

        db.ScheduleRuns.RemoveRange(await db.ScheduleRuns.Where(r => r.ScheduleId == schedule.Id).ToListAsync(cancellationToken));
        db.Schedules.Remove(schedule);
        await db.SaveChangesAsync(cancellationToken);
        return Unit.Value;
    }
}

/// <summary>Schedules are personal: users see and manage their own.</summary>
public sealed class ListSchedulesHandler(IReportingDbContext db, ICurrentUser user) : IQueryHandler<ListSchedulesQuery, IReadOnlyList<ScheduleDto>>
{
    public async Task<Result<IReadOnlyList<ScheduleDto>>> Handle(ListSchedulesQuery query, CancellationToken cancellationToken) =>
        (await db.Schedules.AsNoTracking().Where(s => s.OwnerUserId == user.UserId).OrderBy(s => s.Name).ToListAsync(cancellationToken))
        .Select(ScheduleMapping.ToDto).ToList();
}

public sealed class ListScheduleRunsHandler(IReportingDbContext db, ICurrentUser user) : IQueryHandler<ListScheduleRunsQuery, IReadOnlyList<ScheduleRunDto>>
{
    public async Task<Result<IReadOnlyList<ScheduleRunDto>>> Handle(ListScheduleRunsQuery query, CancellationToken cancellationToken)
    {
        if (!await db.Schedules.AnyAsync(s => s.Id == query.ScheduleId && s.OwnerUserId == user.UserId, cancellationToken))
        {
            return ReportingErrors.ScheduleNotFound;
        }

        // Project without the file bytes.
        var runs = await db.ScheduleRuns.AsNoTracking().Where(r => r.ScheduleId == query.ScheduleId).OrderByDescending(r => r.StartedAtUtc).Take(100)
            .Select(r => new ScheduleRunDto(r.Id, r.ScheduleId, r.Trigger, r.StartedAtUtc, r.FinishedAtUtc, r.Status.ToString(), r.RowCount, r.Error, r.FileName,
                r.FileName == null ? null : "/api/v1/reports/schedules/" + r.ScheduleId + "/runs/" + r.Id + "/file"))
            .ToListAsync(cancellationToken);
        return runs;
    }
}

/// <summary>Run output can be downloaded by the schedule owner (recipients receive the link and need the owner to share the file).</summary>
public sealed class GetScheduleRunFileHandler(IReportingDbContext db, ICurrentUser user) : IQueryHandler<GetScheduleRunFileQuery, ScheduleRunFile>
{
    public async Task<Result<ScheduleRunFile>> Handle(GetScheduleRunFileQuery query, CancellationToken cancellationToken)
    {
        if (!await db.Schedules.AnyAsync(s => s.Id == query.ScheduleId && s.OwnerUserId == user.UserId, cancellationToken))
        {
            return ReportingErrors.ScheduleNotFound;
        }

        var run = await db.ScheduleRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == query.RunId && r.ScheduleId == query.ScheduleId, cancellationToken);
        return run is { Content: not null, FileName: not null, ContentType: not null }
            ? new ScheduleRunFile(run.FileName, run.ContentType, run.Content)
            : ReportingErrors.RunNotFound;
    }
}

/// <summary>
/// Executes a schedule in the active tenant and company with the owner's permissions: runs the report, renders the file, stores the run
/// and then (after the run is saved) emails the recipients a download link. A scheduled trigger first claims the run by moving the next
/// run time on (rowversion-checked), so two workers never run the same occurrence.
/// </summary>
public sealed class ReportScheduleExecutor(
    IReportingDbContext db,
    ReportService reports,
    IReportFileRenderer renderer,
    INotificationSender email,
    IClock clock)
{
    public const string ScheduledTrigger = "Schedule";
    public const string ManualTrigger = "Manual";

    public async Task<Result<ScheduleRunDto>> RunAsync(Guid scheduleId, string trigger, CancellationToken ct)
    {
        var schedule = await db.Schedules.FirstOrDefaultAsync(s => s.Id == scheduleId, ct);
        if (schedule is null)
        {
            return ReportingErrors.ScheduleNotFound;
        }

        var started = clock.UtcNow;
        if (trigger == ScheduledTrigger)
        {
            if (!schedule.IsDue(started))
            {
                return Error.Conflict("reporting.schedule-not-due", "The schedule is not due.");
            }

            schedule.Advance(started);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                return Error.Conflict("reporting.schedule-claimed", "Another worker is running this schedule.");
            }
        }

        var run = await ExecuteAsync(schedule, trigger, started, ct);
        db.ScheduleRuns.Add(run);
        schedule.RecordRun(clock.UtcNow, run.Status);
        await db.SaveChangesAsync(ct);

        if (run.Status == ScheduleRunStatus.Succeeded)
        {
            foreach (var recipient in schedule.Recipients)
            {
                await email.SendEmailAsync(new EmailMessage(recipient, $"Scheduled report: {schedule.Name}",
                    $"The scheduled report \"{schedule.Name}\" ({schedule.ReportKey}) ran at {run.FinishedAtUtc:yyyy-MM-dd HH:mm} UTC with {run.RowCount} rows.\n\n" +
                    $"Download: {ScheduleMapping.DownloadUrl(schedule.Id, run.Id)}"), ct);
            }
        }

        return ScheduleMapping.ToDto(run);
    }

    private async Task<ReportScheduleRun> ExecuteAsync(ReportSchedule schedule, string trigger, DateTime started, CancellationToken ct)
    {
        var parts = schedule.ReportKey.Split('/', 2);
        var request = ReportParameterParser.ToRequest(schedule.Parameters, DateOnly.FromDateTime(started), 1);
        if (request.IsFailure || parts.Length != 2)
        {
            return ReportScheduleRun.Failed(schedule, trigger, started, clock.UtcNow, request.IsFailure ? request.Error.Message : "Unknown report.");
        }

        var arabic = schedule.Language == "ar";
        var result = await reports.RunAsync(parts[0], parts[1], request.Value, forExport: true, arabic, ct, asUserId: schedule.OwnerUserId);
        if (result.IsFailure)
        {
            return ReportScheduleRun.Failed(schedule, trigger, started, clock.UtcNow, $"{result.Error.Code}: {result.Error.Message}");
        }

        var file = await renderer.RenderAsync(result.Value, schedule.Format, arabic, ct);
        if (file.IsFailure)
        {
            return ReportScheduleRun.Failed(schedule, trigger, started, clock.UtcNow, $"{file.Error.Code}: {file.Error.Message}");
        }

        if (file.Value.Content.Length > ReportScheduleRun.MaxContentBytes)
        {
            return ReportScheduleRun.Failed(schedule, trigger, started, clock.UtcNow, "The output is larger than 10 MB; narrow the report parameters.");
        }

        var fileName = $"{schedule.ReportKey.Replace('/', '-')}-{started:yyyyMMddHHmm}.{file.Value.Extension}";
        return ReportScheduleRun.Succeeded(schedule, trigger, started, clock.UtcNow, result.Value.Output.Table.Rows.Count, fileName, file.Value.ContentType, file.Value.Content);
    }

    /// <summary>Due schedules of a tenant across its companies (for the worker, which has no active company).</summary>
    public static async Task<IReadOnlyList<(Guid ScheduleId, Guid CompanyId)>> DueAsync(IReportingDbContext db, Guid tenantId, DateTime nowUtc, CancellationToken ct) =>
        (await db.Schedules.IgnoreQueryFilters().AsNoTracking()
            .Where(s => EF.Property<Guid>(s, "TenantId") == tenantId && s.IsActive && s.NextRunAtUtc != null && s.NextRunAtUtc <= nowUtc)
            .Select(s => new { s.Id, s.CompanyId })
            .ToListAsync(ct))
        .Select(s => (s.Id, s.CompanyId))
        .ToList();
}
