using Serilog;
using Serilog.Events;

/// <summary>One structured log event per HTTP request, enriched with tenant, user and correlation id.</summary>
internal static class RequestLogging
{
    public static WebApplication UseErpRequestLogging(this WebApplication app)
    {
        app.UseSerilogRequestLogging(options =>
        {
            options.MessageTemplate = "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0} ms";

            // Probes are frequent and uninteresting unless they fail.
            options.GetLevel = (http, _, exception) =>
                exception is not null || http.Response.StatusCode >= 500 ? LogEventLevel.Error
                : http.Request.Path.StartsWithSegments("/health") ? LogEventLevel.Verbose
                : LogEventLevel.Information;

            options.EnrichDiagnosticContext = (diagnostics, http) =>
            {
                diagnostics.Set("TenantId", http.User.FindFirst("tid")?.Value);
                diagnostics.Set("UserId", http.User.FindFirst("sub")?.Value);
                diagnostics.Set("CorrelationId", http.Response.Headers["X-Correlation-Id"].ToString());
                diagnostics.Set("ClientIp", http.Connection.RemoteIpAddress?.ToString());
            };
        });
        return app;
    }
}
