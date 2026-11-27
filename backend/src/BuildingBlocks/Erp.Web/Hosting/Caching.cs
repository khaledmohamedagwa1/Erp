using System.Collections.Concurrent;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Erp.Web.Hosting;

public enum ErpCacheProvider
{
    /// <summary>Per-instance memory. Correct for a single API instance.</summary>
    Memory,

    /// <summary>Shared Redis: session revocation, permission invalidation and rate limits apply across all instances.</summary>
    Redis,
}

/// <summary>Section <c>ErpCache</c>.</summary>
public sealed class ErpCacheOptions
{
    public const string SectionName = "ErpCache";

    public ErpCacheProvider Provider { get; set; } = ErpCacheProvider.Memory;

    /// <summary>StackExchange.Redis configuration string, e.g. <c>redis:6379,password=...,ssl=false</c> (secret file ErpCache__RedisConnection).</summary>
    public string? RedisConnection { get; set; }

    /// <summary>Key prefix so several installations can share one Redis.</summary>
    public string InstanceName { get; set; } = "erp:";
}

/// <summary>A counter shared by every API instance, used by the fixed-window rate limiter.</summary>
public interface IFixedWindowCounter
{
    /// <summary>Increments the counter for <paramref name="key"/> in the current window and returns the new value.</summary>
    Task<long> IncrementAsync(string key, TimeSpan window, CancellationToken cancellationToken);
}

/// <summary>In-process counter (single instance, tests).</summary>
public sealed class MemoryFixedWindowCounter(TimeProvider? time = null) : IFixedWindowCounter
{
    private readonly ConcurrentDictionary<string, (long Window, long Count)> _counts = new(StringComparer.Ordinal);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public Task<long> IncrementAsync(string key, TimeSpan window, CancellationToken cancellationToken)
    {
        var current = _time.GetUtcNow().ToUnixTimeMilliseconds() / (long)window.TotalMilliseconds;
        var value = _counts.AddOrUpdate(key, _ => (current, 1), (_, existing) => existing.Window == current ? (current, existing.Count + 1) : (current, 1));

        // Drop stale windows now and then so the dictionary does not grow without bound.
        if (_counts.Count > 10_000)
        {
            foreach (var stale in _counts.Where(p => p.Value.Window != current).Select(p => p.Key).ToList())
            {
                _counts.TryRemove(stale, out _);
            }
        }

        return Task.FromResult(value.Count);
    }
}

/// <summary>Redis counter: INCR on a per-window key that expires with the window (atomic via a Lua script).</summary>
public sealed class RedisFixedWindowCounter(IConnectionMultiplexer redis, string prefix) : IFixedWindowCounter
{
    private const string Script = "local c = redis.call('INCR', KEYS[1]) if c == 1 then redis.call('PEXPIRE', KEYS[1], ARGV[1]) end return c";

    public async Task<long> IncrementAsync(string key, TimeSpan window, CancellationToken cancellationToken)
    {
        var bucket = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / (long)window.TotalMilliseconds;
        var result = await redis.GetDatabase().ScriptEvaluateAsync(Script,
            [new RedisKey($"{prefix}rl:{key}:{bucket}")], [(long)window.TotalMilliseconds]);
        return (long)result;
    }
}

/// <summary>Fixed-window limiter whose counts live in an <see cref="IFixedWindowCounter"/> (Redis when several instances run).</summary>
public sealed class DistributedFixedWindowRateLimiter(IFixedWindowCounter counter, string key, int permitLimit, TimeSpan window) : RateLimiter
{
    private static readonly RateLimitLease Granted = new Lease(true);
    private static readonly RateLimitLease Rejected = new Lease(false);

    public override TimeSpan? IdleDuration => null;

    public override RateLimiterStatistics? GetStatistics() => null;

    // The counter needs a round trip, so the synchronous attempt always declines; the rate-limiting middleware then
    // calls AcquireAsync, which decides.
    protected override RateLimitLease AttemptAcquireCore(int permitCount) => Rejected;

    protected override async ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
    {
        var count = await counter.IncrementAsync(key, window, cancellationToken);
        return count <= permitLimit ? Granted : Rejected;
    }

    private sealed class Lease(bool acquired) : RateLimitLease
    {
        public override bool IsAcquired { get; } = acquired;

        public override IEnumerable<string> MetadataNames => [];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            metadata = null;
            return false;
        }
    }
}

public static class CachingExtensions
{
    /// <summary>
    /// Registers <see cref="Microsoft.Extensions.Caching.Distributed.IDistributedCache"/> (memory or Redis) and the "auth"
    /// rate-limit policy. With Redis, revoked sessions and permission changes take effect on every instance immediately
    /// and rate limits are counted across instances.
    /// </summary>
    public static IServiceCollection AddErpCaching(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(ErpCacheOptions.SectionName).Get<ErpCacheOptions>() ?? new ErpCacheOptions();
        services.AddOptions<ErpCacheOptions>().Bind(configuration.GetSection(ErpCacheOptions.SectionName));

        if (options.Provider == ErpCacheProvider.Redis)
        {
            if (string.IsNullOrWhiteSpace(options.RedisConnection))
            {
                throw new InvalidOperationException("ErpCache:RedisConnection is required when ErpCache:Provider is Redis.");
            }

            var multiplexer = new Lazy<IConnectionMultiplexer>(() => ConnectionMultiplexer.Connect(options.RedisConnection));
            services.AddSingleton(_ => multiplexer.Value);
            services.AddStackExchangeRedisCache(redis =>
            {
                redis.InstanceName = options.InstanceName;
                redis.ConnectionMultiplexerFactory = () => Task.FromResult(multiplexer.Value);
            });
            services.AddSingleton<IFixedWindowCounter>(sp => new RedisFixedWindowCounter(sp.GetRequiredService<IConnectionMultiplexer>(), options.InstanceName));
        }
        else
        {
            services.AddDistributedMemoryCache();
            services.AddSingleton<IFixedWindowCounter>(new MemoryFixedWindowCounter());
        }

        var authPerMinute = configuration.GetValue("RateLimiting:AuthPerMinute", 20);
        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.AddPolicy("auth", http =>
            {
                var counter = http.RequestServices.GetRequiredService<IFixedWindowCounter>();
                var key = "auth:" + (http.Connection.RemoteIpAddress?.ToString() ?? "unknown");
                return RateLimitPartition.Get(key, k => new DistributedFixedWindowRateLimiter(counter, k, authPerMinute, TimeSpan.FromMinutes(1)));
            });
        });
        return services;
    }
}
