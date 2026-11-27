using System.Security.Claims;
using System.Threading.RateLimiting;
using Erp.Application.Abstractions;
using Erp.Infrastructure.Tenancy;
using Erp.Modules.Identity.Domain;
using Erp.Modules.Identity.Infrastructure;
using Erp.SharedKernel.Domain;
using Erp.Web.Hosting;
using Erp.Web.Security;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Erp.UnitTests;

public class StepUpPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);

    private static ClaimsPrincipal Principal(bool enrolled, DateTimeOffset? mfaAt)
    {
        var claims = new List<Claim> { new(ErpClaims.MfaEnrolled, enrolled ? "true" : "false") };
        if (mfaAt is { } at)
        {
            claims.Add(new Claim(ErpClaims.MfaAt, at.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    [Fact]
    public void Recent_second_factor_is_allowed() =>
        StepUpPolicy.Evaluate(Principal(true, Now.AddMinutes(-3)), new StepUpOptions(), Now).ShouldBe(StepUpDecision.Allowed);

    [Fact]
    public void Old_second_factor_requires_step_up() =>
        StepUpPolicy.Evaluate(Principal(true, Now.AddMinutes(-11)), new StepUpOptions(), Now).ShouldBe(StepUpDecision.StepUpRequired);

    [Fact]
    public void Enrolled_user_without_mfa_in_session_requires_step_up() =>
        StepUpPolicy.Evaluate(Principal(true, null), new StepUpOptions(), Now).ShouldBe(StepUpDecision.StepUpRequired);

    [Fact]
    public void By_default_users_without_mfa_must_enroll() =>
        StepUpPolicy.Evaluate(Principal(false, null), new StepUpOptions(), Now).ShouldBe(StepUpDecision.EnrollmentRequired);

    [Fact]
    public void Policy_can_allow_users_without_mfa() =>
        StepUpPolicy.Evaluate(Principal(false, null), new StepUpOptions { AllowWithoutMfa = true }, Now).ShouldBe(StepUpDecision.Allowed);

    [Fact]
    public void Endpoint_specific_max_age_overrides_the_default() =>
        StepUpPolicy.Evaluate(Principal(true, Now.AddMinutes(-3)), new StepUpOptions(), Now, maxAgeMinutes: 2).ShouldBe(StepUpDecision.StepUpRequired);

    [Fact]
    public void Disabled_policy_allows_everything() =>
        StepUpPolicy.Evaluate(Principal(true, null), new StepUpOptions { Enabled = false }, Now).ShouldBe(StepUpDecision.Allowed);
}

public class DataScopeTests
{
    [Fact]
    public void Empty_lists_mean_unrestricted()
    {
        var scope = new DataScope();
        scope.Restrict(new CompanyAccess(Guid.NewGuid(), [], []));
        scope.RestrictsBranches.ShouldBeFalse();
        scope.RestrictsWarehouses.ShouldBeFalse();
        scope.AllowsWarehouse(Guid.NewGuid()).ShouldBeTrue();
    }

    [Fact]
    public void Restricted_scope_allows_listed_ids_and_company_wide_documents()
    {
        var allowed = Guid.NewGuid();
        var scope = new DataScope();
        scope.Restrict(new CompanyAccess(Guid.NewGuid(), [allowed], []));
        scope.RestrictsBranches.ShouldBeTrue();
        scope.AllowsBranch(allowed).ShouldBeTrue();
        scope.AllowsBranch(null).ShouldBeTrue();
        scope.AllowsBranch(Guid.NewGuid()).ShouldBeFalse();
        scope.RestrictsWarehouses.ShouldBeFalse();
    }

    [Fact]
    public void Scope_can_be_set_only_once()
    {
        var scope = new DataScope();
        scope.Restrict(new CompanyAccess(Guid.NewGuid(), [], [Guid.NewGuid()]));
        Should.Throw<InvalidOperationException>(() => scope.Restrict(new CompanyAccess(Guid.NewGuid(), [], [])));
    }
}

public class DistributedCacheTests
{
    private static IDistributedCache SharedCache() => new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));

    private static IdentityCache Instance(IDistributedCache shared, Guid tenantId)
    {
        var tenant = new TenantContext();
        tenant.SetTenant(tenantId);
        return new IdentityCache(shared, tenant);
    }

    [Fact]
    public async Task Session_revocation_on_one_instance_is_seen_by_another()
    {
        var shared = SharedCache();
        var tenantId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var instanceA = Instance(shared, tenantId);
        var instanceB = Instance(shared, tenantId);

        await instanceB.SetSessionActiveAsync(sessionId, true, TestContext.Current.CancellationToken);
        (await instanceB.GetSessionActiveAsync(sessionId, TestContext.Current.CancellationToken)).ShouldBe(true);

        await instanceA.InvalidateSessionAsync(sessionId);
        (await instanceB.GetSessionActiveAsync(sessionId, TestContext.Current.CancellationToken)).ShouldBeNull("instance B must go back to the database");
    }

    [Fact]
    public async Task Permission_change_changes_the_cache_key_on_every_instance()
    {
        var shared = SharedCache();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var instanceA = Instance(shared, tenantId);
        var instanceB = Instance(shared, tenantId);

        var before = await instanceB.UserKeyAsync(userId, "permissions", TestContext.Current.CancellationToken);
        await instanceA.InvalidateAllPermissionsAsync();
        var afterPermissions = await instanceB.UserKeyAsync(userId, "permissions", TestContext.Current.CancellationToken);
        afterPermissions.ShouldNotBe(before);

        await instanceA.InvalidateUserAsync(userId);
        (await instanceB.UserKeyAsync(userId, "permissions", TestContext.Current.CancellationToken)).ShouldNotBe(afterPermissions);
    }

    [Fact]
    public async Task Keys_are_isolated_per_tenant()
    {
        var shared = SharedCache();
        var sessionId = Guid.NewGuid();
        await Instance(shared, Guid.NewGuid()).SetSessionActiveAsync(sessionId, true, TestContext.Current.CancellationToken);
        (await Instance(shared, Guid.NewGuid()).GetSessionActiveAsync(sessionId, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task Fixed_window_limiter_shares_counts_between_limiters()
    {
        var counter = new MemoryFixedWindowCounter();
        using var first = new DistributedFixedWindowRateLimiter(counter, "auth:1.2.3.4", 2, TimeSpan.FromMinutes(1));
        using var second = new DistributedFixedWindowRateLimiter(counter, "auth:1.2.3.4", 2, TimeSpan.FromMinutes(1));
        using var other = new DistributedFixedWindowRateLimiter(counter, "auth:5.6.7.8", 2, TimeSpan.FromMinutes(1));

        (await first.AcquireAsync(1, TestContext.Current.CancellationToken)).IsAcquired.ShouldBeTrue();
        (await second.AcquireAsync(1, TestContext.Current.CancellationToken)).IsAcquired.ShouldBeTrue();
        (await first.AcquireAsync(1, TestContext.Current.CancellationToken)).IsAcquired.ShouldBeFalse("the limit is shared, as across API instances");
        (await other.AcquireAsync(1, TestContext.Current.CancellationToken)).IsAcquired.ShouldBeTrue();
    }

    [Fact]
    public async Task Memory_counter_starts_a_new_window()
    {
        var time = new ManualTime(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var counter = new MemoryFixedWindowCounter(time);
        (await counter.IncrementAsync("k", TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken)).ShouldBe(1);
        (await counter.IncrementAsync("k", TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken)).ShouldBe(2);
        time.Advance(TimeSpan.FromMinutes(1));
        (await counter.IncrementAsync("k", TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken)).ShouldBe(1);
    }

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}

public class FederationDomainTests
{
    [Fact]
    public void Provider_requires_https_authority_and_openid_scope()
    {
        Should.Throw<DomainException>(() => ExternalIdentityProvider.Create("entra", "Entra", "http://login.example", "client", null, null, null, true, true));
        var provider = ExternalIdentityProvider.Create("Entra", "Entra", "https://login.example/tenant/", "client", "entra", "email profile", null, false, true);
        provider.Key.ShouldBe("entra");
        provider.Authority.ShouldBe("https://login.example/tenant");
        provider.Scopes.ShouldBe("openid email profile");
        provider.EmailClaim.ShouldBe("email");
    }

    [Fact]
    public void Provider_key_must_be_url_safe() =>
        Should.Throw<DomainException>(() => ExternalIdentityProvider.Create("bad key", "X", "https://idp.example", "c", null, null, null, true, true));

    [Fact]
    public void Operator_totp_steps_are_single_use_and_failures_lock_out()
    {
        var op = PlatformOperator.Create("ops@example.com", "Ops", "hash", "secret", isBootstrap: true);
        op.AcceptTimeStep(100).ShouldBeTrue();
        op.AcceptTimeStep(100).ShouldBeFalse();
        var now = DateTime.UtcNow;
        for (var i = 0; i < LoginPolicy.MaxFailedAttempts; i++)
        {
            op.RegisterFailedLogin(now);
        }

        op.IsLockedOut(now).ShouldBeTrue();
    }

    [Theory]
    [InlineData("/home", true)]
    [InlineData("/", true)]
    [InlineData("//evil.example/x", false)]
    [InlineData("/\\evil.example", false)]
    [InlineData("https://app.erp.test/after", true)]
    [InlineData("https://evil.example/after", false)]
    [InlineData("javascript:alert(1)", false)]
    public void Return_urls_are_limited_to_local_paths_and_allowed_origins(string url, bool allowed) =>
        ReturnUrls.IsAllowed(url, ["https://app.erp.test"]).ShouldBe(allowed);
}
