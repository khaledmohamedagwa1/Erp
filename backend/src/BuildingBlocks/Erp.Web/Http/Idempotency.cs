using System.Security.Cryptography;
using System.Text;
using Erp.Application.Abstractions;
using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Reliability;
using Erp.SharedKernel.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Web.Http;

/// <summary>Endpoint metadata: the endpoint requires an <c>Idempotency-Key</c> header (used by OpenAPI docs).</summary>
public sealed class IdempotencyMetadata;

/// <summary>Endpoint metadata: the endpoint requires the <c>X-Company-Id</c> header (used by OpenAPI docs).</summary>
public sealed class RequiresCompanyMetadata;

/// <summary>
/// Makes a state-changing endpoint safe to retry. The idempotency record is read with an update lock and written in the
/// <b>same database transaction</b> as the endpoint's effect, so either both commit or neither does:
/// a retry with the same key and body replays the stored response (header <c>Idempotency-Replayed: true</c>);
/// the same key with a different body is rejected; a concurrent duplicate waits for the first and then replays it.
/// Only 2xx responses are stored — a failed attempt can be retried with the same key.
/// </summary>
public static class IdempotencyExtensions
{
    public const string HeaderName = "Idempotency-Key";
    public const string ReplayedHeader = "Idempotency-Replayed";
    public const int MaxKeyLength = 100;

    public static TBuilder RequireIdempotency<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new IdempotencyMetadata());
        builder.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var ct = http.RequestAborted;
            var key = http.Request.Headers[HeaderName].ToString().Trim();
            if (key.Length is 0 or > MaxKeyLength)
            {
                return Error.Validation("idempotency.key-required", $"The {HeaderName} header (1–{MaxKeyLength} characters) is required.").ToProblem();
            }

            var services = http.RequestServices;
            if (!services.GetRequiredService<ITenantContext>().HasTenant)
            {
                return Error.Unauthorized("unauthorized", "Authentication is required.").ToProblem();
            }

            var session = services.GetRequiredService<ErpDbSession>();
            var store = services.GetRequiredService<IIdempotencyStore>();
            var endpoint = $"{http.Request.Method} {http.Request.Path}";
            var hash = await HashRequestAsync(http.Request, ct);

            await session.BeginTransactionAsync(ct);
            try
            {
                var existing = await store.ClaimAsync(key, endpoint, hash, ct);
                if (existing is not null)
                {
                    await session.RollbackAsync(ct);
                    return existing.RequestHash == hash
                        ? new ReplayResult(existing)
                        : Error.Validation("idempotency.key-reused", "This Idempotency-Key was already used with a different request.").ToProblem();
                }

                var result = await next(context);
                var captured = await CaptureAsync(http, result);
                if (captured.StatusCode is >= 200 and < 300)
                {
                    await store.SaveAsync(key, endpoint, new StoredResponse(hash, captured.StatusCode,
                        Encoding.UTF8.GetString(captured.Body), captured.ContentType), ct);
                    await session.CommitAsync(ct);
                }
                else
                {
                    await session.RollbackAsync(ct);
                }

                return new BufferedResult(captured.Body);
            }
            catch (SqlException ex) when (ex.Number is 2627 or 2601)
            {
                await session.RollbackAsync(CancellationToken.None);
                return Error.Conflict("idempotency.in-progress", "A request with this Idempotency-Key is being processed. Retry shortly.").ToProblem();
            }
            catch
            {
                await session.RollbackAsync(CancellationToken.None);
                throw;
            }
        });
        return builder;
    }

    /// <summary>Lets the idempotency filter re-read the body after model binding. Must run before endpoint execution.</summary>
    public static IApplicationBuilder UseIdempotentRequestBuffering(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (context.Request.Headers.ContainsKey(HeaderName))
            {
                context.Request.EnableBuffering();
            }

            await next(context);
        });

    private static async Task<string> HashRequestAsync(HttpRequest request, CancellationToken ct)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(Encoding.UTF8.GetBytes($"{request.Method} {request.Path}{request.QueryString}\n"));
        if (request.Body.CanSeek)
        {
            request.Body.Position = 0;
            var buffer = new byte[8192];
            int read;
            while ((read = await request.Body.ReadAsync(buffer, ct)) > 0)
            {
                sha.AppendData(buffer, 0, read);
            }

            request.Body.Position = 0;
        }

        return Convert.ToHexString(sha.GetHashAndReset());
    }

    private sealed record Captured(int StatusCode, string? ContentType, byte[] Body);

    /// <summary>Executes the endpoint result into memory so it can be stored before anything reaches the client.</summary>
    private static async Task<Captured> CaptureAsync(HttpContext http, object? result)
    {
        var inner = result as IResult ?? Results.Json(result);
        var original = http.Response.Body;
        await using var buffer = new MemoryStream();
        http.Response.Body = buffer;
        try
        {
            await inner.ExecuteAsync(http);
        }
        finally
        {
            http.Response.Body = original;
        }

        return new Captured(http.Response.StatusCode, http.Response.ContentType, buffer.ToArray());
    }

    /// <summary>Writes an already-captured body; status and headers were set while capturing.</summary>
    private sealed class BufferedResult(byte[] body) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext) =>
            body.Length == 0 ? Task.CompletedTask : httpContext.Response.Body.WriteAsync(body).AsTask();
    }

    private sealed class ReplayResult(StoredResponse stored) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = stored.StatusCode;
            httpContext.Response.Headers[ReplayedHeader] = "true";
            if (stored.ContentType is not null)
            {
                httpContext.Response.ContentType = stored.ContentType;
            }

            if (!string.IsNullOrEmpty(stored.Body))
            {
                await httpContext.Response.WriteAsync(stored.Body);
            }
        }
    }
}
