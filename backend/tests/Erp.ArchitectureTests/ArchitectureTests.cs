using System.Reflection;
using Erp.Infrastructure.Persistence;
using Erp.SharedKernel.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using NetArchTest.Rules;
using Shouldly;
using Xunit;

namespace Erp.ArchitectureTests;

public class ArchitectureTests
{
    private static readonly string[] Modules = ["Organization", "Identity", "Configuration", "Parties", "Tax", "Catalog", "Accounting", "Finance", "Inventory", "Workflow", "Manufacturing", "Procurement", "Sales", "FixedAssets", "Pos", "Reporting", "Documents", "Import"];

    private static Assembly Load(string name) => Assembly.Load(name);

    public static TheoryData<string> ModuleNames() => new(Modules);

    [Theory]
    [MemberData(nameof(ModuleNames))]
    public void Domain_depends_on_nothing_but_the_shared_kernel(string module)
    {
        var result = Types.InAssembly(Load($"Erp.Modules.{module}.Domain"))
            .ShouldNot().HaveDependencyOnAny(
                "Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "Erp.Application", "Erp.Infrastructure", "Erp.Web",
                $"Erp.Modules.{module}.Application", $"Erp.Modules.{module}.Infrastructure")
            .GetResult();
        result.IsSuccessful.ShouldBeTrue(string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Theory]
    [MemberData(nameof(ModuleNames))]
    public void Modules_only_use_other_modules_through_their_contracts(string module)
    {
        var forbidden = Modules.Where(m => m != module)
            .SelectMany(m => new[] { $"Erp.Modules.{m}.Domain", $"Erp.Modules.{m}.Application", $"Erp.Modules.{m}.Infrastructure" })
            .ToArray();
        foreach (var layer in new[] { "Domain", "Application", "Infrastructure", "Contracts" })
        {
            var result = Types.InAssembly(Load($"Erp.Modules.{module}.{layer}")).ShouldNot().HaveDependencyOnAny(forbidden).GetResult();
            result.IsSuccessful.ShouldBeTrue($"{module}.{layer}: {string.Join(", ", result.FailingTypeNames ?? [])}");
        }
    }

    [Theory]
    [MemberData(nameof(ModuleNames))]
    public void Contracts_expose_no_domain_or_persistence_types(string module)
    {
        var result = Types.InAssembly(Load($"Erp.Modules.{module}.Contracts"))
            .ShouldNot().HaveDependencyOnAny("Erp.Application", "Erp.Infrastructure", "Microsoft.EntityFrameworkCore", $"Erp.Modules.{module}.Domain")
            .GetResult();
        result.IsSuccessful.ShouldBeTrue(string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Application_layer_does_not_depend_on_web_or_sql_server()
    {
        var result = Types.InAssemblies(Modules.Select(m => Load($"Erp.Modules.{m}.Application")).Append(Load("Erp.Application")))
            .ShouldNot().HaveDependencyOnAny("Microsoft.AspNetCore", "Microsoft.Data.SqlClient", "Erp.Web", "Erp.Infrastructure")
            .GetResult();
        result.IsSuccessful.ShouldBeTrue(string.Join(", ", result.FailingTypeNames ?? []));
    }

    public static TheoryData<Type> ContextFactories() => new(
        typeof(PlatformDbContextFactory),
        typeof(Erp.Modules.Organization.Infrastructure.OrganizationDbContextFactory),
        typeof(Erp.Modules.Identity.Infrastructure.IdentityDbContextFactory),
        typeof(Erp.Modules.Configuration.Infrastructure.ConfigurationDbContextFactory),
        typeof(Erp.Modules.Parties.Infrastructure.PartiesDbContextFactory),
        typeof(Erp.Modules.Tax.Infrastructure.TaxDbContextFactory),
        typeof(Erp.Modules.Catalog.Infrastructure.CatalogDbContextFactory),
        typeof(Erp.Modules.Accounting.Infrastructure.AccountingDbContextFactory),
        typeof(Erp.Modules.Finance.Infrastructure.FinanceDbContextFactory),
        typeof(Erp.Modules.Inventory.Infrastructure.InventoryDbContextFactory),
        typeof(Erp.Modules.Workflow.Infrastructure.WorkflowDbContextFactory),
        typeof(Erp.Modules.Manufacturing.Infrastructure.ManufacturingDbContextFactory),
        typeof(Erp.Modules.FixedAssets.Infrastructure.FixedAssetsDbContextFactory),
        typeof(Erp.Modules.Procurement.Infrastructure.ProcurementDbContextFactory),
        typeof(Erp.Modules.Sales.Infrastructure.SalesDbContextFactory),
        typeof(Erp.Modules.Pos.Infrastructure.PosDbContextFactory),
        typeof(Erp.Modules.Reporting.Infrastructure.ReportingDbContextFactory),
        typeof(Erp.Modules.Documents.Infrastructure.DocumentsDbContextFactory),
        typeof(Erp.Modules.Import.Infrastructure.ImportDbContextFactory),
        typeof(Erp.Modules.Inventory.Infrastructure.InventoryDbContextFactory));

    /// <summary>Tenant isolation must never depend on a developer remembering a filter (Part 4).</summary>
    [Theory]
    [MemberData(nameof(ContextFactories))]
    public void Every_tenant_scoped_entity_has_a_tenant_column_and_query_filter(Type factoryType)
    {
        var factory = Activator.CreateInstance(factoryType)!;
        using var context = (DbContext)factoryType.GetMethod(nameof(IDesignTimeDbContextFactory<DbContext>.CreateDbContext))!.Invoke(factory, [Array.Empty<string>()])!;
        foreach (var entity in context.Model.GetEntityTypes().Where(e => typeof(ITenantScoped).IsAssignableFrom(e.ClrType)))
        {
            entity.FindProperty(ErpDbContext.TenantIdProperty).ShouldNotBeNull($"{entity.ClrType.Name} has no TenantId");
            entity.GetDeclaredQueryFilters().ShouldNotBeEmpty($"{entity.ClrType.Name} has no query filter");
        }
    }

    [Theory]
    [MemberData(nameof(ContextFactories))]
    public void Aggregates_have_rowversion_for_optimistic_concurrency(Type factoryType)
    {
        var factory = Activator.CreateInstance(factoryType)!;
        using var context = (DbContext)factoryType.GetMethod(nameof(IDesignTimeDbContextFactory<DbContext>.CreateDbContext))!.Invoke(factory, [Array.Empty<string>()])!;
        foreach (var entity in context.Model.GetEntityTypes().Where(e => typeof(AggregateRoot).IsAssignableFrom(e.ClrType)))
        {
            entity.FindProperty(ErpDbContext.RowVersionProperty)?.IsConcurrencyToken.ShouldBeTrue($"{entity.ClrType.Name} has no rowversion");
        }
    }

    [Fact]
    public void Api_contracts_never_expose_entities()
    {
        var result = Types.InAssemblies(Modules.Select(m => Load($"Erp.Modules.{m}.Application")))
            .That().HaveNameEndingWith("Dto")
            .ShouldNot().HaveDependencyOnAny(Modules.Select(m => $"Erp.Modules.{m}.Domain").ToArray())
            .GetResult();
        result.IsSuccessful.ShouldBeTrue(string.Join(", ", result.FailingTypeNames ?? []));
    }
}
