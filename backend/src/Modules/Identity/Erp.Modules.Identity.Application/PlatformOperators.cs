using Erp.Application.Abstractions;
using Erp.Application.Messaging;
using Erp.Modules.Identity.Domain;
using Erp.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Erp.Modules.Identity.Application;

public static class PlatformErrors
{
    public static readonly Error BootstrapClosed = Error.Conflict("platform.bootstrap-closed",
        "A platform operator already exists; the bootstrap key can no longer create operators.");

    public static readonly Error InvalidCredentials = Error.Unauthorized("platform.invalid-credentials", "The email, password or code is incorrect.");
    public static readonly Error OperatorNotFound = Error.NotFound("platform.operator-not-found", "The platform operator was not found.");
    public static readonly Error EmailTaken = Error.Conflict("platform.email-taken", "An operator with this email already exists.");
    public static readonly Error CannotDeactivateSelf = Error.Conflict("platform.cannot-deactivate-self", "You cannot deactivate your own operator account.");
}

public interface IPlatformTokenService
{
    AccessToken CreateOperatorToken(PlatformOperator op);
}

public sealed record PlatformOperatorDto(Guid Id, string Email, string DisplayName, bool IsActive, DateTime? LastLoginAtUtc);

/// <summary>Returned once at creation: the operator must add the TOTP secret to an authenticator app before signing in.</summary>
public sealed record PlatformOperatorCreatedDto(Guid Id, string Email, string MfaSecret, string OtpAuthUri);

public sealed record OperatorTokenDto(string AccessToken, DateTime ExpiresAtUtc);

internal static class OperatorFactory
{
    public static (PlatformOperator Operator, PlatformOperatorCreatedDto Dto) Create(
        string email, string displayName, string password, bool bootstrap, IPasswordHasher hasher, ISecretProtector secrets, AuthOptions options)
    {
        var secret = Totp.NewSecret();
        var encoded = Base32.Encode(secret);
        var op = PlatformOperator.Create(email, displayName, hasher.Hash(password), secrets.Protect(encoded), bootstrap);
        return (op, new PlatformOperatorCreatedDto(op.Id, op.Email, encoded, Totp.ProvisioningUri(options.MfaIssuer + " Platform", op.Email, secret)));
    }
}

public abstract class CreateOperatorValidator<T> : AbstractValidator<T>
    where T : ICreateOperator
{
    public CreateOperatorValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Password).StrongPassword();
    }
}

public interface ICreateOperator
{
    string Email { get; }

    string DisplayName { get; }

    string Password { get; }
}

/// <summary>Creates the first operator with the static bootstrap key. Refused once any operator exists.</summary>
public sealed record BootstrapOperatorCommand(string Email, string DisplayName, string Password, string? IpAddress)
    : ICommand<PlatformOperatorCreatedDto>, ICreateOperator;

public sealed class BootstrapOperatorValidator : CreateOperatorValidator<BootstrapOperatorCommand>;

public sealed class BootstrapOperatorHandler(IIdentityDbContext db, IPasswordHasher hasher, ISecretProtector secrets, IClock clock, IOptions<AuthOptions> options)
    : ICommandHandler<BootstrapOperatorCommand, PlatformOperatorCreatedDto>
{
    public async Task<Result<PlatformOperatorCreatedDto>> Handle(BootstrapOperatorCommand command, CancellationToken cancellationToken)
    {
        if (await db.PlatformOperators.AnyAsync(cancellationToken))
        {
            return PlatformErrors.BootstrapClosed;
        }

        var (op, dto) = OperatorFactory.Create(command.Email, command.DisplayName, command.Password, true, hasher, secrets, options.Value);
        db.PlatformOperators.Add(op);
        db.PlatformAudit.Add(new PlatformAuditEntry(clock.UtcNow, op.Id, "operator.bootstrapped", op.Email, command.IpAddress));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Lost a race with a concurrent bootstrap: the unique filtered index on IsBootstrap rejected the second one.
            return PlatformErrors.BootstrapClosed;
        }

        return dto;
    }
}

public sealed record CreateOperatorCommand(string Email, string DisplayName, string Password) : ICommand<PlatformOperatorCreatedDto>, ICreateOperator;

public sealed class CreateOperatorValidatorImpl : CreateOperatorValidator<CreateOperatorCommand>;

public sealed class CreateOperatorHandler(IIdentityDbContext db, IPasswordHasher hasher, ISecretProtector secrets, IOptions<AuthOptions> options)
    : ICommandHandler<CreateOperatorCommand, PlatformOperatorCreatedDto>
{
    public async Task<Result<PlatformOperatorCreatedDto>> Handle(CreateOperatorCommand command, CancellationToken cancellationToken)
    {
        var normalized = User.Normalize(command.Email);
        if (await db.PlatformOperators.AnyAsync(o => o.NormalizedEmail == normalized, cancellationToken))
        {
            return PlatformErrors.EmailTaken;
        }

        var (op, dto) = OperatorFactory.Create(command.Email, command.DisplayName, command.Password, false, hasher, secrets, options.Value);
        db.PlatformOperators.Add(op);
        await db.SaveChangesAsync(cancellationToken);
        return dto;
    }
}

public sealed record ListOperatorsQuery : IQuery<IReadOnlyList<PlatformOperatorDto>>;

public sealed class ListOperatorsHandler(IIdentityDbContext db) : IQueryHandler<ListOperatorsQuery, IReadOnlyList<PlatformOperatorDto>>
{
    public async Task<Result<IReadOnlyList<PlatformOperatorDto>>> Handle(ListOperatorsQuery query, CancellationToken cancellationToken) =>
        await db.PlatformOperators.AsNoTracking().OrderBy(o => o.Email)
            .Select(o => new PlatformOperatorDto(o.Id, o.Email, o.DisplayName, o.IsActive, o.LastLoginAtUtc))
            .ToListAsync(cancellationToken);
}

public sealed record DeactivateOperatorCommand(Guid OperatorId) : ICommand<PlatformOperatorDto>;

public sealed class DeactivateOperatorHandler(IIdentityDbContext db, ICurrentUser currentUser) : ICommandHandler<DeactivateOperatorCommand, PlatformOperatorDto>
{
    public async Task<Result<PlatformOperatorDto>> Handle(DeactivateOperatorCommand command, CancellationToken cancellationToken)
    {
        if (command.OperatorId == currentUser.UserId)
        {
            return PlatformErrors.CannotDeactivateSelf;
        }

        var op = await db.PlatformOperators.FirstOrDefaultAsync(o => o.Id == command.OperatorId, cancellationToken);
        if (op is null)
        {
            return PlatformErrors.OperatorNotFound;
        }

        op.Deactivate();
        await db.SaveChangesAsync(cancellationToken);
        return new PlatformOperatorDto(op.Id, op.Email, op.DisplayName, op.IsActive, op.LastLoginAtUtc);
    }
}

/// <summary>Password + TOTP (always required) → short-lived token for the platform audience. No refresh tokens.</summary>
public sealed record OperatorLoginCommand(string Email, string Password, string Code, string? IpAddress) : ICommand<OperatorTokenDto>, IManualTransaction;

public sealed class OperatorLoginValidator : AbstractValidator<OperatorLoginCommand>
{
    public OperatorLoginValidator()
    {
        RuleFor(x => x.Email).NotEmpty().MaximumLength(256);
        RuleFor(x => x.Password).NotEmpty().MaximumLength(256);
        RuleFor(x => x.Code).NotEmpty().MaximumLength(10);
    }
}

public sealed class OperatorLoginHandler(IIdentityDbContext db, IPasswordHasher hasher, ISecretProtector secrets, IPlatformTokenService tokens, IClock clock)
    : ICommandHandler<OperatorLoginCommand, OperatorTokenDto>
{
    private static string? _dummyHash;

    public async Task<Result<OperatorTokenDto>> Handle(OperatorLoginCommand command, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var normalized = User.Normalize(command.Email);
        var op = await db.PlatformOperators.FirstOrDefaultAsync(o => o.NormalizedEmail == normalized, cancellationToken);
        if (op is not { IsActive: true })
        {
            hasher.Verify(_dummyHash ??= hasher.Hash(Guid.NewGuid().ToString()), command.Password);
            return await Fail(null, "unknown-or-inactive");
        }

        if (op.IsLockedOut(now))
        {
            return IdentityErrors.LockedOut;
        }

        var passwordOk = hasher.Verify(op.PasswordHash, command.Password);
        var codeOk = passwordOk && Totp.Verify(Base32.Decode(secrets.Unprotect(op.MfaSecretProtected)), command.Code, now, out var step) && op.AcceptTimeStep(step);
        if (!codeOk)
        {
            op.RegisterFailedLogin(now);
            return await Fail(op.Id, passwordOk ? "invalid-code" : "invalid-password");
        }

        op.RegisterSuccessfulLogin(now);
        db.PlatformAudit.Add(new PlatformAuditEntry(now, op.Id, "operator.signed-in", null, command.IpAddress));
        await db.SaveChangesAsync(cancellationToken);
        var token = tokens.CreateOperatorToken(op);
        return new OperatorTokenDto(token.Token, token.ExpiresAtUtc);

        async Task<Result<OperatorTokenDto>> Fail(Guid? operatorId, string reason)
        {
            db.PlatformAudit.Add(new PlatformAuditEntry(now, operatorId, "operator.sign-in-failed", $"{normalized}: {reason}", command.IpAddress));
            await db.SaveChangesAsync(cancellationToken);
            return PlatformErrors.InvalidCredentials;
        }
    }
}
