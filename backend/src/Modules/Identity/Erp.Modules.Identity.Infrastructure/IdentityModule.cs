using Erp.Application;
using Erp.Application.Abstractions;
using Erp.Application.Messaging;
using Erp.Application.Paging;
using Erp.Infrastructure;
using Erp.Infrastructure.Outbox;
using Erp.Modules.Identity.Application;
using Erp.Modules.Identity.Contracts;
using Erp.Modules.Identity.Domain;
using Erp.SharedKernel.Results;
using Erp.Web.Hosting;
using Erp.Web.Http;
using Erp.Web.Modules;
using Erp.Web.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Erp.Modules.Identity.Infrastructure;

public sealed record LoginRequest(string? TenantCode, string Email, string Password);

public sealed record SignInResponse(string AccessToken, DateTime AccessTokenExpiresAtUtc, Guid UserId, Guid TenantId, string DisplayName);

public sealed record CreateUserRequest(string Email, string DisplayName, string Password, IReadOnlyCollection<Guid>? RoleIds, IReadOnlyCollection<Guid>? CompanyIds);

public sealed record SetRolesRequest(IReadOnlyCollection<Guid> RoleIds);

public sealed record CompanyAccessRequest(bool Granted, IReadOnlyCollection<Guid>? BranchIds, IReadOnlyCollection<Guid>? WarehouseIds);

public sealed record RoleRequest(string Name, IReadOnlyCollection<string>? Permissions);

public sealed record PermissionsRequest(IReadOnlyCollection<string> Permissions);

public sealed record MfaChallengeResponse(bool MfaRequired, string MfaToken, int ExpiresInSeconds);

public sealed record MfaVerifyRequest(string MfaToken, string? Code, string? RecoveryCode);

public sealed record MfaCodeRequest(string Code);

public sealed record StepUpRequest(string? Code, string? RecoveryCode);

public sealed record OperatorRequest(string Email, string DisplayName, string Password);

public sealed record OperatorLoginRequest(string Email, string Password, string Code);

public sealed record IdentityProviderRequest(string Key, string DisplayName, string Authority, string ClientId, string? ClientSecretName,
    string? Scopes, string? EmailClaim, bool? RequireVerifiedEmail, bool? IsEnabled);

public sealed record MfaDisableRequest(string Password, string Code);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record CompletePasswordChangeRequest(string PasswordChangeToken, string NewPassword);

public sealed record ResetPasswordRequest(string? TemporaryPassword);

public sealed class IdentityModule : IErpModule
{
    public const string RefreshCookie = "erp_refresh";
    public const string RefreshCookiePath = "/api/v1/auth";

    /// <summary>Header required on cookie-authenticated auth endpoints; a cross-site form cannot set it (CSRF defence).</summary>
    public const string CsrfHeader = "X-ERP-Request";

    public ModuleManifest Manifest { get; } = new(
        "identity",
        "Users & security",
        "المستخدمون والأمان",
        IsCore: true,
        DependsOn: [],
        Permissions:
        [
            new(IdentityPermissions.UserRead, "identity", "View users", "عرض المستخدمين"),
            new(IdentityPermissions.UserManage, "identity", "Create users and change their access", "إنشاء المستخدمين وتعديل صلاحياتهم", IsSensitive: true),
            new(IdentityPermissions.RoleRead, "identity", "View roles", "عرض الأدوار"),
            new(IdentityPermissions.RoleManage, "identity", "Create roles and change permissions", "إنشاء الأدوار وتعديل الصلاحيات", IsSensitive: true),
            new(IdentityPermissions.IdentityProviderManage, "identity", "Configure external sign-in providers", "إعداد مزودي تسجيل الدخول الخارجيين", IsSensitive: true),
            new(IdentityPermissions.AllDataScopes, "identity", "Access every branch and warehouse regardless of data scopes", "الوصول لكل الفروع والمستودعات بغض النظر عن نطاق البيانات", IsSensitive: true),
        ],
        Menu:
        [
            new("identity.users", "Users", "المستخدمون", "/settings/users", "administration", 20, IdentityPermissions.UserRead),
            new("identity.roles", "Roles & permissions", "الأدوار والصلاحيات", "/settings/roles", "administration", 21, IdentityPermissions.RoleRead),
        ]);

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddErpDbContext<IdentityDbContext>(IdentityDbContext.SchemaName, order: 20);
        services.AddScoped<IIdentityDbContext>(sp => sp.GetRequiredService<IdentityDbContext>());
        services.AddOptions<AuthOptions>().Bind(configuration.GetSection(AuthOptions.SectionName))
            .Validate(o => o.AccessTokenMinutes is > 0 and <= 60, "Auth:AccessTokenMinutes must be 1–60.")
            .Validate(o => o.PlatformTokenMinutes is > 0 and <= 480, "Auth:PlatformTokenMinutes must be 1–480.")
            .Validate(o => o.PlatformAudience != o.Audience, "Auth:PlatformAudience must differ from Auth:Audience.")
            .ValidateOnStart();
        services.AddSingleton<IPasswordHasher, AspNetPasswordHasher>();
        services.AddSingleton<SigningKeyProvider>();
        services.AddSingleton<ITokenService, JwtTokenService>();
        services.AddScoped<IdentityCache>();
        services.AddScoped<IIdentityCache>(sp => sp.GetRequiredService<IdentityCache>());
        services.AddScoped<IUserAccessService, UserAccessService>();
        services.AddScoped<IPermissionService, PermissionService>();
        services.AddScoped<IIdentityProvisioning, IdentityProvisioning>();
        services.AddScoped<IUserDirectory, UserDirectory>();
        services.AddScoped<ISessionIssuer, SessionIssuer>();
        services.AddScoped<SessionRevoker>();
        services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
        services.AddSingleton<IChallengeTokens, DataProtectionChallengeTokens>();
        services.AddSingleton<IExternalSignInStateProtector, DataProtectionExternalStateProtector>();
        services.AddSingleton<IOidcClient, OidcClient>();
        services.AddHttpClient(OidcClient.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(15));
        services.AddSingleton<IPlatformTokenService, PlatformTokenService>();
        services.AddScoped<IPlatformOperatorDirectory, PlatformOperatorDirectory>();
        services.AddHandlersFromAssembly(typeof(IIdentityDbContext).Assembly);
        services.AddSingleton(new EventAssembly(typeof(Domain.User).Assembly));

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer().AddJwtBearer(PlatformAuth.Scheme);
        services.AddSingleton<IConfigureOptions<JwtBearerOptions>, ConfigureJwtBearer>();
        services.AddAuthorization();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var auth = endpoints.MapGroup("/api/v1/auth").WithTags("Authentication");

        auth.MapPost("/login", async (LoginRequest body, HttpContext http, IDispatcher d, IOptions<AuthOptions> options, CancellationToken ct) =>
        {
            var result = await d.Send(new LoginCommand(body.TenantCode, body.Email, body.Password,
                http.Request.Headers.UserAgent.ToString(), http.Connection.RemoteIpAddress?.ToString()), ct);
            if (result.IsFailure)
            {
                return result.Error.ToProblem();
            }

            var outcome = result.Value;
            http.Response.Headers.CacheControl = "no-store";
            if (outcome.PasswordChangeRequired)
            {
                // 403 with a short-lived token for POST /auth/password/complete-change.
                return Results.Problem(
                    title: ErrorLocalizer.Title(IdentityErrors.PasswordChangeRequired.Code, IdentityErrors.PasswordChangeRequired.Message),
                    detail: IdentityErrors.PasswordChangeRequired.Message,
                    statusCode: StatusCodes.Status403Forbidden,
                    type: $"https://errors.erp.local/{IdentityErrors.PasswordChangeRequired.Code}",
                    extensions: new Dictionary<string, object?>
                    {
                        ["code"] = IdentityErrors.PasswordChangeRequired.Code,
                        ["passwordChangeToken"] = outcome.PasswordChangeToken,
                    });
            }

            if (outcome.MfaRequired)
            {
                return Results.Ok(new MfaChallengeResponse(true, outcome.MfaChallengeToken!,
                    (int)DataProtectionChallengeTokens.Lifetime(ChallengePurpose.Mfa).TotalSeconds));
            }

            return Respond(outcome.Session!, http, options.Value);
        }).AllowAnonymous().RequireRateLimiting("auth");

        auth.MapPost("/mfa/verify", async (MfaVerifyRequest body, HttpContext http, IDispatcher d, IOptions<AuthOptions> options, CancellationToken ct) =>
        {
            var result = await d.Send(new VerifyMfaCommand(body.MfaToken, body.Code, body.RecoveryCode,
                http.Request.Headers.UserAgent.ToString(), http.Connection.RemoteIpAddress?.ToString()), ct);
            return Respond(result, http, options.Value);
        }).AllowAnonymous().RequireRateLimiting("auth");

        auth.MapPost("/password/complete-change", async (CompletePasswordChangeRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CompletePasswordChangeCommand(body.PasswordChangeToken, body.NewPassword), ct)).ToHttpResult(_ => Results.NoContent()))
            .AllowAnonymous().RequireRateLimiting("auth");

        auth.MapPost("/password", async (ChangePasswordRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ChangePasswordCommand(body.CurrentPassword, body.NewPassword), ct)).ToHttpResult(_ => Results.NoContent()))
            .RequireAuthorization();

        auth.MapPost("/mfa/enroll", async (IDispatcher d, CancellationToken ct) =>
                (await d.Send(new BeginMfaEnrollmentCommand(), ct)).ToHttpResult())
            .RequireAuthorization();
        auth.MapPost("/mfa/confirm", async (MfaCodeRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ConfirmMfaCommand(body.Code), ct)).ToHttpResult())
            .RequireAuthorization();
        auth.MapPost("/mfa/disable", async (MfaDisableRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new DisableMfaCommand(body.Password, body.Code), ct)).ToHttpResult(_ => Results.NoContent()))
            .RequireAuthorization();

        auth.MapPost("/step-up", async (StepUpRequest body, HttpContext http, IDispatcher d, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return (await d.Send(new StepUpCommand(body.Code, body.RecoveryCode), ct)).ToHttpResult();
        }).RequireAuthorization().RequireRateLimiting("auth");

        auth.MapPost("/refresh", async (HttpContext http, IDispatcher d, IOptions<AuthOptions> options, CancellationToken ct) =>
        {
            if (!http.Request.Headers.ContainsKey(CsrfHeader))
            {
                return Error.Forbidden("forbidden", $"The {CsrfHeader} header is required.").ToProblem();
            }

            var result = await d.Send(new RefreshSessionCommand(http.Request.Cookies[RefreshCookie]), ct);
            if (result.IsFailure)
            {
                http.Response.Cookies.Delete(RefreshCookie, new CookieOptions { Path = RefreshCookiePath });
            }

            return Respond(result, http, options.Value);
        }).AllowAnonymous().RequireRateLimiting("auth");

        auth.MapPost("/logout", async (HttpContext http, IDispatcher d, CancellationToken ct) =>
        {
            var result = await d.Send(new LogoutCommand(), ct);
            http.Response.Cookies.Delete(RefreshCookie, new CookieOptions { Path = RefreshCookiePath });
            return result.ToHttpResult(_ => Results.NoContent());
        }).RequireAuthorization();

        MapExternalSignIn(auth);
        MapPlatformOperators(endpoints);

        endpoints.MapGet("/api/v1/me", async (IDispatcher d, CancellationToken ct) => (await d.Send(new GetMeQuery(), ct)).ToHttpResult())
            .WithTags("Me").RequireAuthorization();

        var identity = endpoints.MapGroup("/api/v1/identity").WithTags("Identity").RequireAuthorization();
        identity.MapGet("/users", async (int? page, int? pageSize, string? search, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListUsersQuery(new PageRequest(page ?? 1, pageSize ?? 50), search), ct)).ToHttpResult())
            .RequirePermission(IdentityPermissions.UserRead);
        identity.MapPost("/users", async (CreateUserRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CreateUserCommand(body.Email, body.DisplayName, body.Password, body.RoleIds ?? [], body.CompanyIds ?? []), ct))
                .ToHttpResult(u => Results.Created($"/api/v1/identity/users/{u.Id}", u)))
            .RequirePermission(IdentityPermissions.UserManage)
            .RequireStepUp();
        identity.MapPut("/users/{id:guid}/roles", async (Guid id, SetRolesRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new SetUserRolesCommand(id, body.RoleIds), ct)).ToHttpResult())
            .RequirePermission(IdentityPermissions.UserManage)
            .RequireStepUp();
        identity.MapPut("/users/{id:guid}/companies/{companyId:guid}", async (Guid id, Guid companyId, CompanyAccessRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new SetUserCompanyAccessCommand(id, companyId, body.Granted, body.BranchIds, body.WarehouseIds), ct)).ToHttpResult())
            .RequirePermission(IdentityPermissions.UserManage)
            .RequireStepUp();
        identity.MapPost("/users/{id:guid}/reset-password", async (Guid id, ResetPasswordRequest? body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ResetUserPasswordCommand(id, body?.TemporaryPassword), ct)).ToHttpResult())
            .RequirePermission(IdentityPermissions.UserManage)
            .RequireStepUp();
        identity.MapPost("/users/{id:guid}/deactivate", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new DeactivateUserCommand(id), ct)).ToHttpResult())
            .RequirePermission(IdentityPermissions.UserManage)
            .RequireStepUp();
        identity.MapPost("/users/{id:guid}/reactivate", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ReactivateUserCommand(id), ct)).ToHttpResult())
            .RequirePermission(IdentityPermissions.UserManage)
            .RequireStepUp();
        identity.MapGet("/roles", async (IDispatcher d, CancellationToken ct) => (await d.Send(new ListRolesQuery(), ct)).ToHttpResult())
            .RequirePermission(IdentityPermissions.RoleRead);
        identity.MapPost("/roles", async (RoleRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CreateRoleCommand(body.Name, body.Permissions ?? []), ct)).ToHttpResult(r => Results.Created($"/api/v1/identity/roles/{r.Id}", r)))
            .RequirePermission(IdentityPermissions.RoleManage)
            .RequireStepUp();
        identity.MapPut("/roles/{id:guid}/permissions", async (Guid id, PermissionsRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new SetRolePermissionsCommand(id, body.Permissions), ct)).ToHttpResult())
            .RequirePermission(IdentityPermissions.RoleManage)
            .RequireStepUp();
        identity.MapGet("/identity-providers", async (IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListIdentityProvidersQuery(), ct)).ToHttpResult())
            .RequirePermission(IdentityPermissions.IdentityProviderManage);
        identity.MapPost("/identity-providers", async (IdentityProviderRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(ToCommand(null, body), ct)).ToHttpResult(p => Results.Created($"/api/v1/identity/identity-providers/{p.Id}", p)))
            .RequirePermission(IdentityPermissions.IdentityProviderManage)
            .RequireStepUp();
        identity.MapPut("/identity-providers/{id:guid}", async (Guid id, IdentityProviderRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(ToCommand(id, body), ct)).ToHttpResult())
            .RequirePermission(IdentityPermissions.IdentityProviderManage)
            .RequireStepUp();
        identity.MapGet("/permissions", async (IDispatcher d, CancellationToken ct) => (await d.Send(new ListPermissionsQuery(), ct)).ToHttpResult())
            .RequirePermission(IdentityPermissions.RoleRead);
    }

    private static SaveIdentityProviderCommand ToCommand(Guid? id, IdentityProviderRequest body) =>
        new(id, body.Key, body.DisplayName, body.Authority, body.ClientId, body.ClientSecretName, body.Scopes, body.EmailClaim,
            body.RequireVerifiedEmail ?? true, body.IsEnabled ?? true);

    public const string OidcBindingCookie = "erp_oidc";
    public const string OidcCookiePath = "/api/v1/auth/oidc";

    private static void MapExternalSignIn(RouteGroupBuilder auth)
    {
        auth.MapGet("/oidc/providers", async (string? tenantCode, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListSignInProvidersQuery(tenantCode), ct)).ToHttpResult())
            .AllowAnonymous().RequireRateLimiting("auth");

        // Browser navigation: redirects to the provider. returnUrl is where the browser comes back to afterwards.
        auth.MapGet("/oidc/{providerKey}/start", async (string providerKey, string? tenantCode, string? returnUrl, HttpContext http, IDispatcher d,
            IOptions<AuthOptions> options, IOptions<HostingOptions> hosting, IConfiguration configuration, CancellationToken ct) =>
        {
            var target = string.IsNullOrWhiteSpace(returnUrl) ? "/" : returnUrl;
            if (!ReturnUrls.IsAllowed(target, hosting.Value.CorsAllowedOrigins))
            {
                return FederationErrors.InvalidReturnUrl.ToProblem();
            }

            var result = await d.Send(new StartExternalSignInCommand(tenantCode, providerKey, target, CallbackUrl(http, configuration)), ct);
            if (result.IsFailure)
            {
                return result.Error.ToProblem();
            }

            // Binds the round trip to this browser (login-CSRF defence). Lax: the provider's redirect back is a top-level GET.
            http.Response.Cookies.Append(OidcBindingCookie, result.Value.BrowserBinding, new CookieOptions
            {
                HttpOnly = true,
                Secure = options.Value.SecureCookies,
                SameSite = SameSiteMode.Lax,
                Path = OidcCookiePath,
                MaxAge = DataProtectionExternalStateProtector.Lifetime,
                IsEssential = true,
            });
            http.Response.Headers.CacheControl = "no-store";
            return Results.Redirect(result.Value.AuthorizationUrl);
        }).AllowAnonymous().RequireRateLimiting("auth");

        auth.MapGet("/oidc/callback", async (string? code, string? state, string? error, HttpContext http, IDispatcher d,
            IOptions<AuthOptions> options, IConfiguration configuration, CancellationToken ct) =>
        {
            var binding = http.Request.Cookies[OidcBindingCookie];
            http.Response.Cookies.Delete(OidcBindingCookie, new CookieOptions { Path = OidcCookiePath });
            http.Response.Headers.CacheControl = "no-store";
            var result = await d.Send(new CompleteExternalSignInCommand(code, state, binding, error, CallbackUrl(http, configuration),
                http.Request.Headers.UserAgent.ToString(), http.Connection.RemoteIpAddress?.ToString()), ct);
            if (result.IsFailure)
            {
                return result.Error.ToProblem();
            }

            var outcome = result.Value;
            if (outcome.ReturnUrl is null)
            {
                return (outcome.Error ?? FederationErrors.ExternalStateInvalid).ToProblem();
            }

            if (outcome.Error is { } failure)
            {
                return Results.Redirect(ReturnUrls.Append(outcome.ReturnUrl, "error", failure.Code));
            }

            if (outcome.MfaChallengeToken is { } mfaToken)
            {
                return Results.Redirect(ReturnUrls.Append(outcome.ReturnUrl, "mfaToken", mfaToken));
            }

            // The SPA picks the session up with POST /auth/refresh (the access token never travels in a URL).
            SetRefreshCookie(http, outcome.Session!, options.Value);
            return Results.Redirect(outcome.ReturnUrl);
        }).AllowAnonymous().RequireRateLimiting("auth");
    }

    private static string CallbackUrl(HttpContext http, IConfiguration configuration) =>
        configuration["Auth:Oidc:CallbackUrl"] is { Length: > 0 } configured
            ? configured
            : $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}/api/v1/auth/oidc/callback";

    private static void MapPlatformOperators(IEndpointRouteBuilder endpoints)
    {
        var platform = endpoints.MapGroup("/api/platform").WithTags("Platform").AllowAnonymous();

        // Static key: creates the first operator only; refused once any operator exists.
        platform.MapPost("/operators/bootstrap", async (OperatorRequest body, HttpContext http, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new BootstrapOperatorCommand(body.Email, body.DisplayName, body.Password, http.Connection.RemoteIpAddress?.ToString()), ct))
                .ToHttpResult(o => Results.Created($"/api/platform/operators/{o.Id}", o)))
            .RequirePlatformKey()
            .RequireRateLimiting("auth");

        platform.MapPost("/auth/login", async (OperatorLoginRequest body, HttpContext http, IDispatcher d, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return (await d.Send(new OperatorLoginCommand(body.Email, body.Password, body.Code, http.Connection.RemoteIpAddress?.ToString()), ct)).ToHttpResult();
        }).RequireRateLimiting("auth");

        platform.MapGet("/operators", async (IDispatcher d, CancellationToken ct) => (await d.Send(new ListOperatorsQuery(), ct)).ToHttpResult())
            .RequirePlatformOperator("operator.list");
        platform.MapPost("/operators", async (OperatorRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CreateOperatorCommand(body.Email, body.DisplayName, body.Password), ct))
                .ToHttpResult(o => Results.Created($"/api/platform/operators/{o.Id}", o)))
            .RequirePlatformOperator("operator.created");
        platform.MapPost("/operators/{id:guid}/deactivate", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new DeactivateOperatorCommand(id), ct)).ToHttpResult())
            .RequirePlatformOperator("operator.deactivated");
    }


    private static IResult Respond(Result<SignInResult> result, HttpContext http, AuthOptions options)
    {
        if (result.IsFailure)
        {
            return result.Error.ToProblem();
        }

        return Respond(result.Value, http, options);
    }

    private static IResult Respond(SignInResult value, HttpContext http, AuthOptions options)
    {
        SetRefreshCookie(http, value, options);
        http.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new SignInResponse(value.AccessToken, value.AccessTokenExpiresAtUtc, value.UserId, value.TenantId, value.DisplayName));
    }

    private static void SetRefreshCookie(HttpContext http, SignInResult value, AuthOptions options) =>
        http.Response.Cookies.Append(RefreshCookie, value.RefreshToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = options.SecureCookies,
            SameSite = SameSiteMode.Strict,
            Path = RefreshCookiePath,
            Expires = value.RefreshTokenExpiresAtUtc,
            IsEssential = true,
        });
}

/// <summary>Where the browser may be sent after an external sign-in: a local path, or an allowed (CORS) application origin.</summary>
public static class ReturnUrls
{
    public static bool IsAllowed(string url, IReadOnlyCollection<string> allowedOrigins)
    {
        if (url.StartsWith('/'))
        {
            // "//host" and "/\host" are protocol-relative to another host in browsers.
            return url.Length == 1 || (url[1] != '/' && url[1] != '\\');
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return false;
        }

        var origin = uri.GetLeftPart(UriPartial.Authority);
        return allowedOrigins.Any(o => string.Equals(o.TrimEnd('/'), origin, StringComparison.OrdinalIgnoreCase));
    }

    public static string Append(string url, string name, string value)
    {
        var separator = url.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return $"{url}{separator}{Uri.EscapeDataString(name)}={Uri.EscapeDataString(value)}";
    }
}

