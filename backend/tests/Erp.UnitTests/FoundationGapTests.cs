using System.Text;
using Erp.Application;
using Erp.Application.Abstractions;
using Erp.Application.Messaging;
using Erp.Modules.Identity.Application;
using Erp.Modules.Identity.Domain;
using Erp.SharedKernel.Domain;
using Erp.SharedKernel.Results;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shouldly;
using Xunit;

namespace Erp.UnitTests;

public class TotpTests
{
    // RFC 6238 Appendix B, SHA-1 secret "12345678901234567890".
    private static readonly byte[] RfcSecret = Encoding.ASCII.GetBytes("12345678901234567890");

    [Theory]
    [InlineData(59L, "94287082")]
    [InlineData(1111111109L, "07081804")]
    [InlineData(1234567890L, "89005924")]
    [InlineData(2000000000L, "69279037")]
    public void Matches_the_rfc_6238_test_vectors(long unixSeconds, string expected) =>
        Totp.Compute(RfcSecret, unixSeconds / 30, digits: 8).ShouldBe(expected);

    [Fact]
    public void Accepts_one_step_of_clock_drift_and_reports_the_matching_step()
    {
        var now = new DateTime(2026, 11, 27, 10, 0, 0, DateTimeKind.Utc);
        var previous = Totp.Compute(RfcSecret, Totp.TimeStep(now) - 1);
        Totp.Verify(RfcSecret, previous, now, out var step).ShouldBeTrue();
        step.ShouldBe(Totp.TimeStep(now) - 1);
        Totp.Verify(RfcSecret, Totp.Compute(RfcSecret, Totp.TimeStep(now) - 3), now, out _).ShouldBeFalse();
        Totp.Verify(RfcSecret, "12345", now, out _).ShouldBeFalse();
    }

    [Fact]
    public void Base32_round_trips_and_builds_an_otpauth_uri()
    {
        var secret = Totp.NewSecret();
        Base32.Decode(Base32.Encode(secret)).ShouldBe(secret);
        Base32.Encode(Encoding.ASCII.GetBytes("foobar")).ShouldBe("MZXW6YTBOI");
        Totp.ProvisioningUri("ERP", "a@b.c", secret).ShouldStartWith("otpauth://totp/ERP:a%40b.c?secret=");
    }

    [Fact]
    public void Recovery_codes_are_unique_and_hashing_ignores_formatting()
    {
        var codes = RecoveryCodes.Generate();
        codes.Count.ShouldBe(10);
        codes.Distinct().Count().ShouldBe(10);
        RecoveryCodes.Hash(codes[0]).ShouldBe(RecoveryCodes.Hash(codes[0].Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant()));
    }
}

public class UserSecurityDomainTests
{
    [Fact]
    public void Mfa_lifecycle_with_replay_protection_and_single_use_recovery_codes()
    {
        var user = User.Create("a@b.c", "A", "hash");
        Should.Throw<DomainException>(() => user.ConfirmMfa([]));

        user.BeginMfaEnrollment("protected");
        user.ConfirmMfa(["H1", "H2"]);
        user.MfaEnabled.ShouldBeTrue();
        user.MfaSecretProtected.ShouldBe("protected");
        Should.Throw<DomainException>(() => user.BeginMfaEnrollment("again"));

        user.AcceptTimeStep(100).ShouldBeTrue();
        user.AcceptTimeStep(100).ShouldBeFalse();
        user.AcceptTimeStep(99).ShouldBeFalse();

        user.UseRecoveryCode("H1").ShouldBeTrue();
        user.UseRecoveryCode("H1").ShouldBeFalse();
        user.RecoveryCodeHashes.ShouldBe(["H2"]);

        user.DisableMfa();
        user.MfaEnabled.ShouldBeFalse();
        user.RecoveryCodeHashes.ShouldBeEmpty();
    }

    [Fact]
    public void Temporary_password_unlocks_and_forces_a_change()
    {
        var now = DateTime.UtcNow;
        var user = User.Create("a@b.c", "A", "hash");
        for (var i = 0; i < LoginPolicy.MaxFailedAttempts; i++)
        {
            user.RegisterFailedLogin(now);
        }

        user.SetTemporaryPassword("temp");
        user.IsLockedOut(now).ShouldBeFalse();
        user.MustChangePassword.ShouldBeTrue();
        user.ChangePasswordHash("new");
        user.MustChangePassword.ShouldBeFalse();
    }

    [Fact]
    public void Secrets_are_marked_sensitive_for_the_audit_log()
    {
        foreach (var name in new[] { nameof(User.PasswordHash), nameof(User.MfaSecretProtected), nameof(User.PendingMfaSecretProtected), nameof(User.RecoveryCodeHashes) })
        {
            typeof(User).GetProperty(name)!.GetCustomAttributes(typeof(SensitiveAttribute), false).ShouldNotBeEmpty(name);
        }
    }
}

public class TransientRetryTests
{
    public sealed record Flaky(int FailTimes) : ICommand<int>;

    public sealed class FlakyHandler : ICommandHandler<Flaky, int>
    {
        public static int Calls;

        public Task<Result<int>> Handle(Flaky request, CancellationToken cancellationToken)
        {
            Calls++;
            if (Calls <= request.FailTimes)
            {
                throw new FakeTransientException();
            }

            return Task.FromResult<Result<int>>(Calls);
        }
    }

    public sealed class FakeTransientException : Exception;

    private sealed class Detector : ITransientFailureDetector
    {
        public bool IsTransient(Exception exception) => exception is FakeTransientException;
    }

    private sealed class Reset : IChangeTrackerReset
    {
        public int Count { get; private set; }

        public void ClearTrackedChanges() => Count++;
    }

    private sealed class UnitOfWork : IUnitOfWork
    {
        public int Begins { get; private set; }

        public int Rollbacks { get; private set; }

        public bool HasActiveTransaction { get; private set; }

        public Task BeginTransactionAsync(CancellationToken cancellationToken = default)
        {
            Begins++;
            HasActiveTransaction = true;
            return Task.CompletedTask;
        }

        public Task CommitAsync(CancellationToken cancellationToken = default)
        {
            HasActiveTransaction = false;
            return Task.CompletedTask;
        }

        public Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            Rollbacks++;
            HasActiveTransaction = false;
            return Task.CompletedTask;
        }
    }

    private static (IDispatcher Dispatcher, UnitOfWork UnitOfWork, Reset Reset) Build()
    {
        FlakyHandler.Calls = 0;
        var services = new ServiceCollection().AddLogging();
        var uow = new UnitOfWork();
        var reset = new Reset();
        services.AddSingleton<IUnitOfWork>(uow);
        services.AddSingleton<IChangeTrackerReset>(reset);
        services.AddErpApplication();
        services.Replace(ServiceDescriptor.Singleton<ITransientFailureDetector, Detector>());
        services.AddScoped<IRequestHandler<Flaky, int>, FlakyHandler>();
        return (services.BuildServiceProvider().CreateScope().ServiceProvider.GetRequiredService<IDispatcher>(), uow, reset);
    }

    [Fact]
    public async Task A_transient_failure_is_rolled_back_cleared_and_retried_in_a_new_transaction()
    {
        var (dispatcher, uow, reset) = Build();
        (await dispatcher.Send(new Flaky(2), TestContext.Current.CancellationToken)).Value.ShouldBe(3);
        uow.Begins.ShouldBe(3);
        uow.Rollbacks.ShouldBe(2);
        reset.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Gives_up_after_the_maximum_attempts()
    {
        var (dispatcher, uow, _) = Build();
        await Should.ThrowAsync<FakeTransientException>(() => dispatcher.Send(new Flaky(10), TestContext.Current.CancellationToken));
        uow.Begins.ShouldBe(TransientRetryBehavior<Flaky, int>.MaxAttempts);
    }
}
