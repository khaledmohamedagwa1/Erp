using Erp.Application.Abstractions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Web.Modules;

/// <param name="RequiredModule">
/// Another module that must be enabled for the entry to show (e.g. the "Inventory reports" entry of the core Reporting module needs inventory).
/// The owning module's own activation is always checked.
/// </param>
public sealed record MenuItemDefinition(string Key, string LabelEn, string LabelAr, string Route, string Group, int Order, string? RequiredPermission,
    string? RequiredModule = null);

/// <summary>What a module contributes to the platform: identity, dependencies, permissions and navigation.</summary>
public sealed record ModuleManifest(
    string Code,
    string NameEn,
    string NameAr,
    bool IsCore,
    IReadOnlyCollection<string> DependsOn,
    IReadOnlyCollection<PermissionDefinition> Permissions,
    IReadOnlyCollection<MenuItemDefinition> Menu);

/// <summary>A bounded-context module. Hosts compose the platform from a list of these.</summary>
public interface IErpModule
{
    ModuleManifest Manifest { get; }

    void AddServices(IServiceCollection services, IConfiguration configuration);

    void MapEndpoints(IEndpointRouteBuilder endpoints);
}

public sealed class ModuleRegistry(IEnumerable<IErpModule> modules) : IPermissionCatalog
{
    private readonly IReadOnlyList<IErpModule> _modules = modules.ToList();
    private readonly Dictionary<string, PermissionDefinition> _permissions =
        modules.SelectMany(m => m.Manifest.Permissions).ToDictionary(p => p.Code, StringComparer.Ordinal);

    public IReadOnlyList<IErpModule> Modules => _modules;

    public IReadOnlyCollection<PermissionDefinition> All => _permissions.Values;

    public bool Exists(string code) => _permissions.ContainsKey(code);
}
