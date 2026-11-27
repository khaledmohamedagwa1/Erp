using Erp.Application.Abstractions;
using Erp.Application.Reporting;
using Erp.Modules.Reporting.Application;
using Erp.SharedKernel.Results;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Erp.Modules.Reporting.Infrastructure;

/// <summary>Renders report tables with the same writers as the report endpoint.</summary>
public sealed class ReportFileRenderer(ReportService reports, IDocumentRenderer html, IPdfRenderer pdf) : IReportFileRenderer
{
    public async Task<Result<(byte[] Content, string ContentType, string Extension)>> RenderAsync(ReportRun run, string format, bool arabic, CancellationToken ct)
    {
        var table = run.Output.Table;
        switch (format)
        {
            case "csv":
            {
                using var buffer = new MemoryStream();
                await CsvReportWriter.WriteAsync(table, buffer, arabic, ct);
                return (buffer.ToArray(), CsvReportWriter.ContentType, "csv");
            }

            case "xlsx":
            {
                var context = await reports.RenderContextAsync(arabic, null, ct);
                using var buffer = new MemoryStream();
                XlsxReportWriter.Write(table, context, buffer);
                return (buffer.ToArray(), XlsxReportWriter.ContentType, "xlsx");
            }

            case "html":
            {
                var context = await reports.RenderContextAsync(arabic, null, ct);
                return (System.Text.Encoding.UTF8.GetBytes(html.RenderReport(table, context)), html.ContentType, "html");
            }

            case "pdf":
            {
                if (!pdf.IsAvailable)
                {
                    return Error.Validation("reporting.pdf-not-configured", "PDF output is not configured (Pdf:GotenbergUrl).");
                }

                var context = await reports.RenderContextAsync(arabic, null, ct);
                return (await pdf.RenderAsync(html.RenderReport(table, context), ct), "application/pdf", "pdf");
            }

            default:
                return ReportingErrors.InvalidFormat;
        }
    }
}

/// <summary>Runs due report schedules for every active tenant (worker/all role).</summary>
public sealed class ReportScheduleBackgroundService(
    IServiceScopeFactory scopes,
    ITenantDirectory tenants,
    ReportingOptions options,
    ILogger<ReportScheduleBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, options.ScheduleIntervalMinutes));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var tenant in await tenants.ListActiveAsync(stoppingToken))
                {
                    await RunTenantAsync(tenant.Id, tenant.Code, stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Report schedule run failed");
            }

            await Task.Delay(interval, stoppingToken);
        }
    }

    private async Task RunTenantAsync(Guid tenantId, string tenantCode, CancellationToken ct)
    {
        IReadOnlyList<(Guid ScheduleId, Guid CompanyId)> due;
        await using (var scope = scopes.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
            due = await ReportScheduleExecutor.DueAsync(scope.ServiceProvider.GetRequiredService<IReportingDbContext>(), tenantId,
                scope.ServiceProvider.GetRequiredService<IClock>().UtcNow, ct);
        }

        foreach (var (scheduleId, companyId) in due)
        {
            await using var scope = scopes.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<ITenantContext>();
            context.SetTenant(tenantId);
            context.SetCompany(companyId);
            var result = await scope.ServiceProvider.GetRequiredService<ReportScheduleExecutor>().RunAsync(scheduleId, ReportScheduleExecutor.ScheduledTrigger, ct);
            if (result.IsSuccess)
            {
                logger.LogInformation("Report schedule {ScheduleId} of tenant {TenantCode} ran: {Status}", scheduleId, tenantCode, result.Value.Status);
            }
            else
            {
                logger.LogDebug("Report schedule {ScheduleId} of tenant {TenantCode} skipped: {Code}", scheduleId, tenantCode, result.Error.Code);
            }
        }
    }
}
