using System.Diagnostics.Metrics;
using Erp.Application.Messaging;
using Erp.SharedKernel.Results;

namespace Erp.Application.Observability;

/// <summary>
/// Business and platform metrics (meter <c>Erp</c>). Exported through OpenTelemetry when OTEL_EXPORTER_OTLP_ENDPOINT is set;
/// otherwise the instruments exist but nothing listens (near-zero cost).
/// </summary>
public static class ErpMetrics
{
    public const string MeterName = "Erp";

    private static readonly Meter Meter = new(MeterName, "1.0");

    private static readonly Counter<long> Commands = Meter.CreateCounter<long>("erp.commands", "{command}", "Commands and queries handled, by request and outcome.");

    private static readonly Counter<long> Posted = Meter.CreateCounter<long>("erp.documents.posted", "{document}", "Documents posted successfully (commands named Post*).");

    private static readonly Counter<long> FailedLogins = Meter.CreateCounter<long>("erp.auth.failed_logins", "{attempt}", "Rejected sign-in attempts (wrong credentials or locked out).");

    private static readonly Counter<long> OutboxMessages = Meter.CreateCounter<long>("erp.outbox.messages", "{message}", "Outbox messages delivered, by event type and outcome.");

    private static double _outboxLagSeconds;

    static ErpMetrics()
    {
        Meter.CreateObservableGauge("erp.outbox.lag", () => Volatile.Read(ref _outboxLagSeconds), "s",
            "Age of the oldest undelivered outbox message (refreshed by the readiness check).");
    }

    public static void RecordRequest(string requestName, Error? error)
    {
        var outcome = error is null ? "success" : "failure";
        Commands.Add(1, new("request", requestName), new("outcome", outcome), new("error_code", error?.Code ?? string.Empty));
        if (error is null && requestName.StartsWith("Post", StringComparison.Ordinal))
        {
            Posted.Add(1, new KeyValuePair<string, object?>("document", requestName));
        }

        if (error?.Code is "auth.invalid-credentials" or "auth.locked-out")
        {
            FailedLogins.Add(1, new KeyValuePair<string, object?>("reason", error.Code));
        }
    }

    public static void RecordOutboxMessage(string eventType, bool succeeded) =>
        OutboxMessages.Add(1, new("type", eventType), new("outcome", succeeded ? "success" : "failure"));

    public static void SetOutboxLag(TimeSpan lag) => Volatile.Write(ref _outboxLagSeconds, Math.Max(0, lag.TotalSeconds));
}

/// <summary>Outermost pipeline step: counts every request after the transaction has committed or rolled back.</summary>
public sealed class MetricsBehavior<TRequest, TResult> : IPipelineBehavior<TRequest, TResult>
    where TRequest : IRequest<TResult>
{
    public async Task<Result<TResult>> Handle(TRequest request, RequestHandlerDelegate<TResult> next, CancellationToken cancellationToken)
    {
        var result = await next();
        ErpMetrics.RecordRequest(typeof(TRequest).Name, result.IsFailure ? result.Error : null);
        return result;
    }
}
