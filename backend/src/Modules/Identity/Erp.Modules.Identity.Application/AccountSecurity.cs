using System.Security.Cryptography;
using Erp.Application.Abstractions;
using Erp.Application.Messaging;
using Erp.Modules.Identity.Domain;
using Erp.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Erp.Modules.Identity.Application;

/// <summary>Encrypts secrets at rest (MFA secrets) with a key the database never sees (ASP.NET Data Protection).</summary>
public interface ISecretProtector
{
    string Protect(string plaintext);

    string Unprotect(string protectedText);
}

public static class ChallengePurpose
{
    public const string Mfa = "mfa";
    public const string PasswordChange = "password-change";
}

/// <summary>Short-lived, tamper-proof tokens that carry a pending sign-in step (MFA challenge, forced password change).</summary>
public interface IChallengeTokens
{
    string Create(string purpose, Guid tenantId, Guid userId);

    bool TryRead(string purpose, string? token, out Guid tenantId, out Guid userId);
}

public interface ISessionIssuer
{
    /// <summary>Completes a sign-in: records it on the user, starts a session with a refresh token, issues an access token.</summary>
    Task<SignInResult> IssueAsync(User user, Guid tenantId, string? userAgent, string? ipAddress, CancellationToken cancellationToken);
}

public sealed class SessionIssuer(IIdentityDbContext db, ITokenService tokens, IClock clock, IOptions<AuthOptions> options) : ISessionIssuer
{
    public async Task<SignInResult> IssueAsync(User user, Guid tenantId, string? userAgent, string? ipAddress, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var now = clock.UtcNow;
        user.RegisterSuccessfulLogin(now);
        var session = UserSession.Start(user.Id, now, TimeSpan.FromDays(settings.SessionAbsoluteDays), userAgent, ipAddress);
        var rawRefresh = RefreshTokens.Create(tenantId);
        var refresh = session.IssueRefreshToken(RefreshTokens.Hash(rawRefresh), now, TimeSpan.FromDays(settings.RefreshTokenDays));
        db.Sessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);
        var access = tokens.CreateAccessToken(user, tenantId, session.Id);
        return new SignInResult(access.Token, access.ExpiresAtUtc, rawRefresh, refresh.ExpiresAtUtc, user.Id, tenantId, user.DisplayName);
    }
}

/// <summary>Revokes a user's active sessions (password change, reset, deactivation).</summary>
public sealed class SessionRevoker(IIdentityDbContext db, IIdentityCache cache, IClock clock)
{
    public async Task RevokeAllAsync(Guid userId, string reason, Guid? exceptSessionId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var sessions = await db.Sessions.Where(s => s.UserId == userId && s.RevokedAtUtc == null && s.AbsoluteExpiresAtUtc > now && s.Id != exceptSessionId)
            .ToListAsync(cancellationToken);
        foreach (var session in sessions)
        {
            session.Revoke(reason, now);
            cache.InvalidateSession(session.Id);
        }

        cache.InvalidateUser(userId);
    }
}

public static class PasswordRules
{
    public const int MinimumLength = 10;

    public static IRuleBuilderOptions<T, string> StrongPassword<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty().MinimumLength(MinimumLength).MaximumLength(256).WithMessage($"Password must be {MinimumLength}–256 characters.");
}

// ---------- MFA: second step of sign-in ----------
public sealed record VerifyMfaCommand(string MfaToken, string? Code, string? RecoveryCode, string? UserAgent, string? IpAddress)
    : ICommand<SignInResult>, IManualTransaction;

public sealed class VerifyMfaValidator : AbstractValidator<VerifyMfaCommand>
{
    public VerifyMfaValidator()
    {
        RuleFor(x => x.MfaToken).NotEmpty();
        RuleFor(x => x).Must(x => !string.IsNullOrWhiteSpace(x.Code) || !string.IsNullOrWhiteSpace(x.RecoveryCode))
            .WithName("code").WithMessage("Provide a verification code or a recovery code.");
    }
}

public sealed class VerifyMfaHandler(
    IIdentityDbContext db,
    ITenantDirectory tenants,
    ITenantContext tenantContext,
    IChallengeTokens challenges,
    ISecretProtector secrets,
    ISessionIssuer sessions,
    IClock clock) : ICommandHandler<VerifyMfaCommand, SignInResult>
{
    public async Task<Result<SignInResult>> Handle(VerifyMfaCommand command, CancellationToken cancellationToken)
    {
        if (!challenges.TryRead(ChallengePurpose.Mfa, command.MfaToken, out var tenantId, out var userId) ||
            await tenants.FindByIdAsync(tenantId, cancellationToken) is not { IsActive: true })
        {
            return IdentityErrors.MfaChallengeInvalid;
        }

        tenantContext.SetTenant(tenantId);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        var now = clock.UtcNow;
        if (user is not { IsActive: true, MfaEnabled: true })
        {
            return IdentityErrors.MfaChallengeInvalid;
        }

        if (user.IsLockedOut(now))
        {
            return IdentityErrors.LockedOut;
        }

        var accepted = !string.IsNullOrWhiteSpace(command.Code)
            ? MfaCodes.VerifyTotp(user, secrets, command.Code, now)
            : user.UseRecoveryCode(RecoveryCodes.Hash(command.RecoveryCode!));

        if (!accepted)
        {
            user.RegisterFailedLogin(now);
            await db.SaveChangesAsync(cancellationToken);
            return user.IsLockedOut(now) ? IdentityErrors.LockedOut : IdentityErrors.MfaInvalidCode;
        }

        return await sessions.IssueAsync(user, tenantId, command.UserAgent, command.IpAddress, cancellationToken);
    }
}

internal static class MfaCodes
{
    public static bool VerifyTotp(User user, ISecretProtector secrets, string code, DateTime now)
    {
        var secret = Base32.Decode(secrets.Unprotect(user.MfaSecretProtected!));
        return Totp.Verify(secret, code, now, out var step) && user.AcceptTimeStep(step);
    }
}

// ---------- MFA: enrollment ----------
public sealed record MfaEnrollmentDto(string Secret, string OtpAuthUri);

public sealed record BeginMfaEnrollmentCommand : ICommand<MfaEnrollmentDto>;

public sealed class BeginMfaEnrollmentHandler(IIdentityDbContext db, ICurrentUser currentUser, ISecretProtector secrets, IOptions<AuthOptions> options)
    : ICommandHandler<BeginMfaEnrollmentCommand, MfaEnrollmentDto>
{
    public async Task<Result<MfaEnrollmentDto>> Handle(BeginMfaEnrollmentCommand command, CancellationToken cancellationToken)
    {
        var user = await db.Users.FirstAsync(u => u.Id == currentUser.UserId, cancellationToken);
        if (user.MfaEnabled)
        {
            return IdentityErrors.MfaAlreadyEnabled;
        }

        var secret = Totp.NewSecret();
        var encoded = Base32.Encode(secret);
        user.BeginMfaEnrollment(secrets.Protect(encoded));
        await db.SaveChangesAsync(cancellationToken);
        return new MfaEnrollmentDto(encoded, Totp.ProvisioningUri(options.Value.MfaIssuer, user.Email, secret));
    }
}

public sealed record RecoveryCodesDto(IReadOnlyList<string> RecoveryCodes);

public sealed record ConfirmMfaCommand(string Code) : ICommand<RecoveryCodesDto>;

public sealed class ConfirmMfaValidator : AbstractValidator<ConfirmMfaCommand>
{
    public ConfirmMfaValidator() => RuleFor(x => x.Code).NotEmpty();
}

public sealed class ConfirmMfaHandler(IIdentityDbContext db, ICurrentUser currentUser, ISecretProtector secrets, IClock clock)
    : ICommandHandler<ConfirmMfaCommand, RecoveryCodesDto>
{
    public async Task<Result<RecoveryCodesDto>> Handle(ConfirmMfaCommand command, CancellationToken cancellationToken)
    {
        var user = await db.Users.FirstAsync(u => u.Id == currentUser.UserId, cancellationToken);
        if (user.PendingMfaSecretProtected is null)
        {
            return IdentityErrors.MfaNotEnrolling;
        }

        var secret = Base32.Decode(secrets.Unprotect(user.PendingMfaSecretProtected));
        if (!Totp.Verify(secret, command.Code, clock.UtcNow, out var step))
        {
            return IdentityErrors.MfaInvalidCode;
        }

        var codes = RecoveryCodes.Generate();
        user.ConfirmMfa(codes.Select(RecoveryCodes.Hash));
        user.AcceptTimeStep(step);
        await db.SaveChangesAsync(cancellationToken);
        return new RecoveryCodesDto(codes);
    }
}

public sealed record DisableMfaCommand(string Password, string Code) : ICommand<Unit>;

public sealed class DisableMfaValidator : AbstractValidator<DisableMfaCommand>
{
    public DisableMfaValidator()
    {
        RuleFor(x => x.Password).NotEmpty();
        RuleFor(x => x.Code).NotEmpty();
    }
}

public sealed class DisableMfaHandler(
    IIdentityDbContext db,
    ICurrentUser currentUser,
    IPasswordHasher hasher,
    ISecretProtector secrets,
    IClock clock) : ICommandHandler<DisableMfaCommand, Unit>
{
    public async Task<Result<Unit>> Handle(DisableMfaCommand command, CancellationToken cancellationToken)
    {
        var user = await db.Users.FirstAsync(u => u.Id == currentUser.UserId, cancellationToken);
        if (!user.MfaEnabled)
        {
            return IdentityErrors.MfaNotEnabled;
        }

        if (!hasher.Verify(user.PasswordHash, command.Password))
        {
            return IdentityErrors.InvalidCurrentPassword;
        }

        if (!MfaCodes.VerifyTotp(user, secrets, command.Code, clock.UtcNow) && !user.UseRecoveryCode(RecoveryCodes.Hash(command.Code)))
        {
            return IdentityErrors.MfaInvalidCode;
        }

        user.DisableMfa();
        await db.SaveChangesAsync(cancellationToken);
        return Unit.Value;
    }
}

// ---------- Passwords ----------

/// <summary>Signed-in user changes their own password. Every other session of the user is ended.</summary>
public sealed record ChangePasswordCommand(string CurrentPassword, string NewPassword) : ICommand<Unit>;

public sealed class ChangePasswordValidator : AbstractValidator<ChangePasswordCommand>
{
    public ChangePasswordValidator()
    {
        RuleFor(x => x.CurrentPassword).NotEmpty();
        RuleFor(x => x.NewPassword).StrongPassword();
        RuleFor(x => x.NewPassword).NotEqual(x => x.CurrentPassword).WithMessage("The new password must differ from the current one.");
    }
}

public sealed class ChangePasswordHandler(IIdentityDbContext db, ICurrentUser currentUser, IPasswordHasher hasher, SessionRevoker revoker)
    : ICommandHandler<ChangePasswordCommand, Unit>
{
    public async Task<Result<Unit>> Handle(ChangePasswordCommand command, CancellationToken cancellationToken)
    {
        var user = await db.Users.FirstAsync(u => u.Id == currentUser.UserId, cancellationToken);
        if (!hasher.Verify(user.PasswordHash, command.CurrentPassword))
        {
            return IdentityErrors.InvalidCurrentPassword;
        }

        user.ChangePasswordHash(hasher.Hash(command.NewPassword));
        await revoker.RevokeAllAsync(user.Id, "password-changed", currentUser.SessionId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return Unit.Value;
    }
}

/// <summary>Completes the forced change after an administrator reset (token from the login response). The user then signs in normally.</summary>
public sealed record CompletePasswordChangeCommand(string PasswordChangeToken, string NewPassword) : ICommand<Unit>, IManualTransaction;

public sealed class CompletePasswordChangeValidator : AbstractValidator<CompletePasswordChangeCommand>
{
    public CompletePasswordChangeValidator()
    {
        RuleFor(x => x.PasswordChangeToken).NotEmpty();
        RuleFor(x => x.NewPassword).StrongPassword();
    }
}

public sealed class CompletePasswordChangeHandler(
    IIdentityDbContext db,
    ITenantDirectory tenants,
    ITenantContext tenantContext,
    IChallengeTokens challenges,
    IPasswordHasher hasher,
    IUnitOfWork unitOfWork,
    SessionRevoker revoker) : ICommandHandler<CompletePasswordChangeCommand, Unit>
{
    public async Task<Result<Unit>> Handle(CompletePasswordChangeCommand command, CancellationToken cancellationToken)
    {
        if (!challenges.TryRead(ChallengePurpose.PasswordChange, command.PasswordChangeToken, out var tenantId, out var userId) ||
            await tenants.FindByIdAsync(tenantId, cancellationToken) is not { IsActive: true })
        {
            return IdentityErrors.PasswordChangeTokenInvalid;
        }

        tenantContext.SetTenant(tenantId);
        await unitOfWork.BeginTransactionAsync(cancellationToken);
        try
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
            if (user is not { IsActive: true, MustChangePassword: true })
            {
                await unitOfWork.RollbackAsync(cancellationToken);
                return IdentityErrors.PasswordChangeTokenInvalid;
            }

            if (hasher.Verify(user.PasswordHash, command.NewPassword))
            {
                await unitOfWork.RollbackAsync(cancellationToken);
                return Error.Validation("validation-failed", "The new password must differ from the temporary one.")
                    with { Details = new Dictionary<string, string[]> { ["newPassword"] = ["The new password must differ from the temporary one."] } };
            }

            user.ChangePasswordHash(hasher.Hash(command.NewPassword));
            await revoker.RevokeAllAsync(user.Id, "password-changed", null, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await unitOfWork.CommitAsync(cancellationToken);
            return Unit.Value;
        }
        catch
        {
            await unitOfWork.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
}

public sealed record PasswordResetDto(string? TemporaryPassword);

/// <summary>
/// Administrator sets a temporary password. All the user's sessions end, the account is unlocked and the user must pick
/// a new password at the next sign-in. A generated password is returned once so the administrator can hand it over.
/// </summary>
public sealed record ResetUserPasswordCommand(Guid UserId, string? TemporaryPassword) : ICommand<PasswordResetDto>;

public sealed class ResetUserPasswordValidator : AbstractValidator<ResetUserPasswordCommand>
{
    public ResetUserPasswordValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        When(x => x.TemporaryPassword is not null, () => RuleFor(x => x.TemporaryPassword!).StrongPassword());
    }
}

public sealed class ResetUserPasswordHandler(IIdentityDbContext db, IPasswordHasher hasher, SessionRevoker revoker)
    : ICommandHandler<ResetUserPasswordCommand, PasswordResetDto>
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";

    public async Task<Result<PasswordResetDto>> Handle(ResetUserPasswordCommand command, CancellationToken cancellationToken)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == command.UserId, cancellationToken);
        if (user is null)
        {
            return IdentityErrors.UserNotFound;
        }

        var generated = command.TemporaryPassword is null ? new string(RandomNumberGenerator.GetItems<char>(Alphabet, 16)) : null;
        user.SetTemporaryPassword(hasher.Hash(command.TemporaryPassword ?? generated!));
        await revoker.RevokeAllAsync(user.Id, "password-reset", null, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return new PasswordResetDto(generated);
    }
}

// ---------- Security notifications (sent from the outbox after commit, at most once per event) ----------
public sealed class MfaStatusChangedNotifier(IUnitOfWork unitOfWork, IInbox inbox, IOutboxMessageContext message, IIdentityDbContext db, INotificationSender notifications)
    : InboxIntegrationEventHandler<MfaStatusChanged>(unitOfWork, inbox, message)
{
    protected override async Task HandleOnceAsync(MfaStatusChanged integrationEvent, CancellationToken cancellationToken)
    {
        var email = await db.Users.Where(u => u.Id == integrationEvent.UserId).Select(u => u.Email).FirstOrDefaultAsync(cancellationToken);
        if (email is not null)
        {
            var state = integrationEvent.Enabled ? "turned on" : "turned off";
            await notifications.SendEmailAsync(new EmailMessage(email, $"Two-step verification {state}",
                $"Two-step verification was {state} for your account. If this was not you, contact your administrator."), cancellationToken);
        }
    }
}

public sealed class PasswordResetNotifier(IUnitOfWork unitOfWork, IInbox inbox, IOutboxMessageContext message, IIdentityDbContext db, INotificationSender notifications)
    : InboxIntegrationEventHandler<PasswordResetByAdministrator>(unitOfWork, inbox, message)
{
    protected override async Task HandleOnceAsync(PasswordResetByAdministrator integrationEvent, CancellationToken cancellationToken)
    {
        var email = await db.Users.Where(u => u.Id == integrationEvent.UserId).Select(u => u.Email).FirstOrDefaultAsync(cancellationToken);
        if (email is not null)
        {
            await notifications.SendEmailAsync(new EmailMessage(email, "Your password was reset",
                "An administrator reset your password. You will be asked to choose a new one when you next sign in."), cancellationToken);
        }
    }
}

// ---------- Activation ----------
public sealed record DeactivateUserCommand(Guid UserId) : ICommand<UserDto>;

public sealed class DeactivateUserHandler(IIdentityDbContext db, ICurrentUser currentUser, SessionRevoker revoker)
    : ICommandHandler<DeactivateUserCommand, UserDto>
{
    public async Task<Result<UserDto>> Handle(DeactivateUserCommand command, CancellationToken cancellationToken)
    {
        if (command.UserId == currentUser.UserId)
        {
            return IdentityErrors.CannotDeactivateSelf;
        }

        var user = await db.Users.Include(u => u.Roles).Include(u => u.CompanyAccess).FirstOrDefaultAsync(u => u.Id == command.UserId, cancellationToken);
        if (user is null)
        {
            return IdentityErrors.UserNotFound;
        }

        user.Deactivate();
        await revoker.RevokeAllAsync(user.Id, "user-deactivated", null, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return IdentityMapping.ToDto(user);
    }
}

public sealed record ReactivateUserCommand(Guid UserId) : ICommand<UserDto>;

public sealed class ReactivateUserHandler(IIdentityDbContext db, IIdentityCache cache) : ICommandHandler<ReactivateUserCommand, UserDto>
{
    public async Task<Result<UserDto>> Handle(ReactivateUserCommand command, CancellationToken cancellationToken)
    {
        var user = await db.Users.Include(u => u.Roles).Include(u => u.CompanyAccess).FirstOrDefaultAsync(u => u.Id == command.UserId, cancellationToken);
        if (user is null)
        {
            return IdentityErrors.UserNotFound;
        }

        user.Activate();
        await db.SaveChangesAsync(cancellationToken);
        cache.InvalidateUser(user.Id);
        return IdentityMapping.ToDto(user);
    }
}
