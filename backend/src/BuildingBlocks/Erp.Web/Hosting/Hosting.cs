using System.Net;
using System.Security.Cryptography.X509Certificates;
using Erp.Application.Observability;
using Erp.Infrastructure.Database;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Erp.Web.Hosting;

/// <summary>Deployment-specific hosting settings (section <c>Hosting</c>). Defaults are safe for a single instance behind nothing.</summary>
public sealed class HostingOptions
{
    public const string SectionName = "Hosting";

    /// <summary>Honour X-Forwarded-For/Proto from the reverse proxies and networks listed below (nothing is trusted otherwise).</summary>
    public bool TrustForwardedHeaders { get; set; }

    /// <summary>IP addresses of trusted reverse proxies.</summary>
    public List<string> KnownProxies { get; set; } = [];

    /// <summary>CIDR ranges of trusted reverse proxies, e.g. the Docker network <c>172.28.0.0/24</c>.</summary>
    public List<string> KnownNetworks { get; set; } = [];

    public int ForwardLimit { get; set; } = 1;

    /// <summary>Redirect HTTP to HTTPS in the app. Usually the reverse proxy does this instead.</summary>
    public bool HttpsRedirection { get; set; }

    /// <summary>Largest accepted request body (default 10 MB).</summary>
    public long MaxRequestBodyBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>Origins allowed to call the API from a browser (the Angular app). Empty = no cross-origin access.</summary>
    public List<string> CorsAllowedOrigins { get; set; } = [];

    /// <summary>Optional PFX certificate that encrypts the Data Protection key ring at rest (it is stored in the database).</summary>
    public string? DataProtectionCertificatePath { get; set; }

    public string? DataProtectionCertificatePassword { get; set; }

    /// <summary>Outbox lag after which readiness reports Degraded.</summary>
    public int OutboxLagDegradedSeconds { get; set; } = 300;

    /// <summary>Outbox lag after which readiness reports Unhealthy.</summary>
    public int OutboxLagUnhealthySeconds { get; set; } = 1800;

    /// <summary>Escape hatch for evaluation installs; production checks are skipped (logged as a warning).</summary>
    public bool SkipProductionChecks { get; set; }
}

public static class HostingExtensions
{
    public const string CorsPolicyName = "erp-web";

    /// <summary>Registers hosting concerns: CORS, forwarded headers, body limits, key-ring encryption, readiness checks.</summary>
    public static IServiceCollection AddErpHosting(this IServiceCollection services, IConfiguration configuration, string role)
    {
        var options = configuration.GetSection(HostingOptions.SectionName).Get<HostingOptions>() ?? new HostingOptions();
        services.AddOptions<HostingOptions>().Bind(configuration.GetSection(HostingOptions.SectionName));

        if (options.CorsAllowedOrigins.Count > 0)
        {
            services.AddCors(cors => cors.AddPolicy(CorsPolicyName, policy => policy
                .WithOrigins([.. options.CorsAllowedOrigins])
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials() // the refresh-token cookie
                .WithExposedHeaders("ETag", "Location", "X-Correlation-Id", "Idempotency-Replayed")
                .SetPreflightMaxAge(TimeSpan.FromMinutes(10))));
        }

        services.Configure<ForwardedHeadersOptions>(forwarded =>
        {
            forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
            forwarded.ForwardLimit = options.ForwardLimit;
            forwarded.KnownProxies.Clear();
            forwarded.KnownIPNetworks.Clear();
            foreach (var proxy in options.KnownProxies)
            {
                forwarded.KnownProxies.Add(IPAddress.Parse(proxy));
            }

            foreach (var network in options.KnownNetworks)
            {
                forwarded.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
            }
        });

        services.Configure<KestrelServerOptions>(kestrel =>
        {
            kestrel.Limits.MaxRequestBodySize = options.MaxRequestBodyBytes;
            kestrel.AddServerHeader = false;
        });

        if (!string.IsNullOrWhiteSpace(options.DataProtectionCertificatePath))
        {
            var certificate = X509CertificateLoader.LoadPkcs12FromFile(options.DataProtectionCertificatePath, options.DataProtectionCertificatePassword);
            services.AddDataProtection().ProtectKeysWithCertificate(certificate);
        }

        // Singleton so its one-minute cache survives between probes.
        services.AddSingleton<MigrationStatusHealthCheck>();
        var health = services.AddHealthChecks().Add(new HealthCheckRegistration(
            "migrations", sp => sp.GetRequiredService<MigrationStatusHealthCheck>(), failureStatus: null, tags: ["ready"]));
        if (role is "worker" or "all")
        {
            // Only processes that deliver the outbox report its lag; API-only instances are not responsible for it.
            health.AddCheck<OutboxLagHealthCheck>("outbox", tags: ["ready"]);
        }

        return services;
    }

    /// <summary>Hosting middleware. Call first, before <c>UseErpRequestPipeline</c>.</summary>
    public static WebApplication UseErpHosting(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<HostingOptions>>().Value;
        if (options.TrustForwardedHeaders)
        {
            app.UseForwardedHeaders();
        }

        app.UseMiddleware<SecurityHeadersMiddleware>();
        if (!app.Environment.IsDevelopment())
        {
            app.UseHsts(); // only emitted on HTTPS requests
        }

        if (options.HttpsRedirection)
        {
            app.UseHttpsRedirection();
        }

        if (options.CorsAllowedOrigins.Count > 0)
        {
            app.UseCors(CorsPolicyName);
        }

        return app;
    }
}

/// <summary>
/// Defensive headers for an API that serves JSON only. Swagger UI (development / explicitly enabled) gets a relaxed policy
/// because it needs its own scripts and styles.
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public const string ApiContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

    private const string SwaggerContentSecurityPolicy =
        "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; frame-ancestors 'none'";

    public Task InvokeAsync(HttpContext context)
    {
        var swagger = context.Request.Path.StartsWithSegments("/swagger");
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            headers.ContentSecurityPolicy = swagger ? SwaggerContentSecurityPolicy : ApiContentSecurityPolicy;
            return Task.CompletedTask;
        });
        return next(context);
    }
}

/// <summary>Production must not start with development-grade settings. Development and Testing are exempt.</summary>
public static class ErpProductionChecks
{
    public const int MinimumPlatformKeyLength = 32;

    public static IReadOnlyList<string> Validate(IConfiguration configuration, IHostEnvironment environment)
    {
        if (environment.IsDevelopment() || environment.IsEnvironment("Testing"))
        {
            return [];
        }

        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(configuration["Auth:SigningKeyPem"]))
        {
            problems.Add("Auth:SigningKeyPem is not set (PEM RSA private key; use a secret file such as /run/secrets/Auth__SigningKeyPem).");
        }

        if (!configuration.GetValue("Auth:SecureCookies", true))
        {
            problems.Add("Auth:SecureCookies must be true outside Development (refresh cookie over HTTPS only).");
        }

        var platformKey = configuration["Platform:ApiKey"];
        if (!string.IsNullOrEmpty(platformKey) && platformKey.Length < MinimumPlatformKeyLength)
        {
            problems.Add($"Platform:ApiKey must be at least {MinimumPlatformKeyLength} characters (or empty to disable tenant provisioning over HTTP).");
        }

        if (configuration.GetValue<bool>("Database:AutoMigrate"))
        {
            problems.Add("Database:AutoMigrate must be false outside Development; run Erp.Migrator as a deployment step (ADR-015).");
        }

        var origins = configuration.GetSection("Hosting:CorsAllowedOrigins").Get<string[]>() ?? [];
        foreach (var origin in origins)
        {
            if (origin.Contains('*', StringComparison.Ordinal) ||
                !Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.AbsolutePath != "/")
            {
                problems.Add($"Hosting:CorsAllowedOrigins entry '{origin}' must be an explicit https origin (no wildcards, no path).");
            }
        }

        return problems;
    }

    public static void ThrowIfInvalid(IConfiguration configuration, IHostEnvironment environment)
    {
        if (configuration.GetValue<bool>($"{HostingOptions.SectionName}:{nameof(HostingOptions.SkipProductionChecks)}"))
        {
            return;
        }

        var problems = Validate(configuration, environment);
        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                "Refusing to start: the configuration is not production-ready.\n - " + string.Join("\n - ", problems) +
                "\nFix the settings, or set Hosting:SkipProductionChecks=true for an evaluation install.");
        }
    }
}

/// <summary>Unhealthy while the deployed code expects migrations that are not applied (cached for a minute).</summary>
public sealed class MigrationStatusHealthCheck(IPendingMigrationsReader reader) : IHealthCheck
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (DateTimeOffset At, IReadOnlyList<string> Pending)? _cache;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> pending;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // A current schema is cached for a minute; a pending migration is re-checked on every probe.
            if (_cache is { } cached && DateTimeOffset.UtcNow - cached.At < TimeSpan.FromMinutes(1) && cached.Pending.Count == 0)
            {
                pending = cached.Pending;
            }
            else
            {
                pending = await reader.GetPendingAsync(cancellationToken);
                _cache = (DateTimeOffset.UtcNow, pending);
            }
        }
        finally
        {
            _gate.Release();
        }

        return pending.Count == 0
            ? HealthCheckResult.Healthy("Database schema is current.")
            : HealthCheckResult.Unhealthy($"{pending.Count} migration(s) pending — run Erp.Migrator.",
                data: new Dictionary<string, object> { ["pending"] = pending.Take(20).ToArray() });
    }
}

/// <summary>Degraded/Unhealthy when outbox messages wait too long (the worker is down or failing). Also refreshes the lag gauge.</summary>
public sealed class OutboxLagHealthCheck(IOutboxLagReader reader, IOptions<HostingOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var lag = await reader.GetOldestPendingAgeAsync(cancellationToken);
        ErpMetrics.SetOutboxLag(lag);
        var settings = options.Value;
        var data = new Dictionary<string, object> { ["lagSeconds"] = (long)lag.TotalSeconds };
        if (lag.TotalSeconds >= settings.OutboxLagUnhealthySeconds)
        {
            return HealthCheckResult.Unhealthy($"Oldest outbox message is {lag.TotalMinutes:0} min old.", data: data);
        }

        return lag.TotalSeconds >= settings.OutboxLagDegradedSeconds
            ? HealthCheckResult.Degraded($"Oldest outbox message is {lag.TotalSeconds:0} s old.", data: data)
            : HealthCheckResult.Healthy("Outbox is flowing.", data);
    }
}

/// <summary>
/// <c>dotnet Erp.Api.dll --healthcheck</c>: probes the running instance's readiness endpoint and exits 0 (ready) or 1.
/// Used as the container HEALTHCHECK so the runtime image needs no curl/wget.
/// </summary>
public static class ErpHealthProbe
{
    public const string Argument = "--healthcheck";

    public static async Task<int> RunAsync()
    {
        var url = Environment.GetEnvironmentVariable("ERP_HEALTHCHECK_URL") ?? DefaultUrl();
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await client.GetAsync(url);
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            await Console.Error.WriteLineAsync($"Health probe failed: {ex.Message}");
            return 1;
        }
    }

    public static string DefaultUrl()
    {
        var ports = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS");
        var port = string.IsNullOrWhiteSpace(ports) ? "8080" : ports.Split([';', ','], StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        return $"http://127.0.0.1:{port}/health/ready";
    }
}
