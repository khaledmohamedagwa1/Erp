using Erp.Application;
using Erp.Application.Abstractions;
using Erp.Application.Messaging;
using Erp.Application.Paging;
using Erp.Infrastructure;
using Erp.Infrastructure.Outbox;
using Erp.Modules.Identity.Application;
using Erp.Modules.Identity.Contracts;
using Erp.SharedKernel.Results;
using Erp.Web.Http;
using Erp.Web.Modules;
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
            .ValidateOnStart();
        services.AddSingleton<IPasswordHasher, AspNetPasswordHasher>();
        services.AddSingleton<SigningKeyProvider>();
        services.AddSingleton<ITokenService, JwtTokenService>();
        services.AddScoped<IdentityCache>();
        services.AddScoped<IIdentityCache>(sp => sp.GetRequiredService<IdentityCache>());
        services.AddScoped<IUserAccessService, UserAccessService>();
        services.AddScoped<IPermissionService, PermissionService>();
        services.AddScoped<IIdentityProvisioning, IdentityProvisioning>();
        services.AddHandlersFromAssembly(typeof(IIdentityDbContext).Assembly);
        services.AddSingleton(new EventAssembly(typeof(Domain.User).Assembly));

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
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
            return Respond(result, http, options.Value);
        }).AllowAnonymous().RequireRateLimiting("auth");

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

        endpoints.MapGet("/api/v1/me", async (IDispatcher d, CancellationToken ct) => (await d.Send(new GetMeQuery(), ct)).ToHttpResult())
            .WithTags("Me").RequireAuthorization();

        var identity = endpoints.MapGroup("/api/v1/identity").WithTags("Identity").RequireAuthorization();
        identity.MapGet("/users", async (int? page, int? pageSize, string? search, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListUsersQuery(new PageRequest(page ?? 1, pageSize ?? 50), search), ct)).ToHttpResult())
            .RequirePermission(IdentityPermissions.UserRead);
        identity.MapPost("/users", async (CreateUserRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CreateUserCommand(body.Email, body.DisplayName, body.Password, body.RoleIds ?? [], body.CompanyIds ?? []), ct))
                .ToHttpResult(u => Results.Created($"/api/v1/identity/users/{u.Id}", u)))
            .RequirePermission(IdentityPermissions.UserManage);
        identity.MapPut("/users/{id:guid}/roles", async (Guid id, SetRolesRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new SetUserRolesCommand(id, body.RoleIds), ct)).ToHttpResult())
            .RequirePermission(IdentityPermissions.UserManage);
        identity.MapPut("/users/{id:guid}/companies/{companyId:guid}", async (Guid id, Guid companyId, CompanyAccessRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new SetUserCompanyAccessCommand(id, companyId, body.Granted, body.BranchIds, body.WarehouseIds), ct)).ToHttpResult())
            .RequirePermission(IdentityPermissions.UserManage);
        identity.MapGet("/roles", async (IDispatcher d, CancellationToken ct) => (await d.Send(new ListRolesQuery(), ct)).ToHttpResult())
            .RequirePermission(IdentityPermissions.RoleRead);
        identity.MapPost("/roles", async (RoleRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CreateRoleCommand(body.Name, body.Permissions ?? []), ct)).ToHttpResult(r => Results.Created($"/api/v1/identity/roles/{r.Id}", r)))
            .RequirePermission(IdentityPermissions.RoleManage);
        identity.MapPut("/roles/{id:guid}/permissions", async (Guid id, PermissionsRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new SetRolePermissionsCommand(id, body.Permissions), ct)).ToHttpResult())
            .RequirePermission(IdentityPermissions.RoleManage);
        identity.MapGet("/permissions", async (IDispatcher d, CancellationToken ct) => (await d.Send(new ListPermissionsQuery(), ct)).ToHttpResult())
            .RequirePermission(IdentityPermissions.RoleRead);
    }

    private static IResult Respond(Result<SignInResult> result, HttpContext http, AuthOptions options)
    {
        if (result.IsFailure)
        {
            return result.Error.ToProblem();
        }

        var value = result.Value;
        http.Response.Cookies.Append(RefreshCookie, value.RefreshToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = options.SecureCookies,
            SameSite = SameSiteMode.Strict,
            Path = RefreshCookiePath,
            Expires = value.RefreshTokenExpiresAtUtc,
            IsEssential = true,
        });
        http.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new SignInResponse(value.AccessToken, value.AccessTokenExpiresAtUtc, value.UserId, value.TenantId, value.DisplayName));
    }
}
