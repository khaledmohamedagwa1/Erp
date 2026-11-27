using Erp.Web.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace Erp.Web;

/// <summary>OpenAPI document enrichment: bearer authentication and the platform's custom request headers.</summary>
public static class ErpOpenApi
{
    public const string BearerScheme = "Bearer";

    public static IServiceCollection AddErpOpenApi(this IServiceCollection services) =>
        services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                document.Components.SecuritySchemes[BearerScheme] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    BearerFormat = "JWT",
                    Description = "Access token from POST /api/v1/auth/login (or /auth/mfa/verify).",
                };
                return Task.CompletedTask;
            });

            options.AddOperationTransformer((operation, context, _) =>
            {
                var metadata = context.Description.ActionDescriptor.EndpointMetadata;
                var anonymous = metadata.OfType<IAllowAnonymous>().Any();
                if (!anonymous && metadata.OfType<IAuthorizeData>().Any())
                {
                    operation.Security ??= [];
                    operation.Security.Add(new OpenApiSecurityRequirement
                    {
                        [new OpenApiSecuritySchemeReference(BearerScheme, context.Document)] = [],
                    });
                }

                if (metadata.OfType<RequiresCompanyMetadata>().Any())
                {
                    AddHeader(operation, "X-Company-Id", "Active company. Must be a company the user has access to.", "uuid");
                }

                if (metadata.OfType<IdempotencyMetadata>().Any())
                {
                    AddHeader(operation, IdempotencyExtensions.HeaderName,
                        "Client-generated key (e.g. a UUID). Retrying with the same key and body replays the first response.", null);
                }

                return Task.CompletedTask;
            });
        });

    private static void AddHeader(OpenApiOperation operation, string name, string description, string? format)
    {
        operation.Parameters ??= [];
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = name,
            In = ParameterLocation.Header,
            Required = true,
            Description = description,
            Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = format },
        });
    }
}
