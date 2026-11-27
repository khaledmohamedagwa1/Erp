using System.Data.SqlTypes;
using Erp.Infrastructure.Numbering;
using Erp.Modules.Configuration.Application;
using Erp.Modules.Configuration.Domain;
using Erp.Modules.Identity.Application;
using Erp.Modules.Identity.Domain;
using Erp.Modules.Organization.Domain;
using Erp.SharedKernel.Domain;
using Erp.SharedKernel.Results;
using Shouldly;
using Xunit;

namespace Erp.UnitTests;

public class SharedKernelTests
{
    [Fact]
    public void Sequential_guids_sort_in_creation_order_under_sql_server_rules()
    {
        var ids = Enumerable.Range(0, 1000).Select(_ => SequentialGuid.New()).ToList();
        var sqlSorted = ids.Select(g => new SqlGuid(g)).OrderBy(g => g).Select(g => g.Value).ToList();
        sqlSorted.ShouldBe(ids);
    }

    [Fact]
    public void Result_rejects_inconsistent_states()
    {
        Result.Success().IsSuccess.ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => _ = Result.Failure<int>(Error.Failure("x", "y")).Value);
        Result<int> fromValue = 5;
        fromValue.Value.ShouldBe(5);
    }
}

public class OrganizationDomainTests
{
    [Theory]
    [InlineData("a")]
    [InlineData("has space")]
    [InlineData("")]
    public void Invalid_codes_are_rejected(string code) =>
        Should.Throw<DomainException>(() => Company.Create(code, "Name", "اسم", "EGP", "trading"));

    [Fact]
    public void Codes_are_normalised_to_upper_case() =>
        Company.Create("ant-01", "Name", "اسم", "egp", "trading").Code.ShouldBe("ANT-01");

    [Fact]
    public void A_new_company_gets_a_legal_entity_head_office_and_main_and_transit_warehouses()
    {
        var s = CompanyStructure.Create("ANT", "Al-Nour", "النور", "EGP", "trading", null);
        s.LegalEntity.CompanyId.ShouldBe(s.Company.Id);
        s.LegalEntity.FunctionalCurrency.ShouldBe("EGP");
        s.HeadOffice.LegalEntityId.ShouldBe(s.LegalEntity.Id);
        s.MainWarehouse.BranchId.ShouldBe(s.HeadOffice.Id);
        s.TransitWarehouse.Type.ShouldBe(WarehouseType.Transit);
        s.TransitWarehouse.BranchId.ShouldBeNull();
        s.Company.DomainEvents.OfType<CompanyCreated>().ShouldHaveSingleItem();
    }

    [Fact]
    public void Transit_warehouses_cannot_be_created_manually()
    {
        var s = CompanyStructure.Create("ANT", "Al-Nour", "النور", "EGP", "trading", null);
        Should.Throw<DomainException>(() => Warehouse.Create(s.HeadOffice, "T2", "Transit", WarehouseType.Transit));
    }
}

public class IdentityDomainTests
{
    private static readonly DateTime Now = new(2026, 11, 27, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Account_locks_after_five_failed_sign_ins_and_unlocks_after_the_lockout()
    {
        var user = User.Create("a@b.c", "A", "hash");
        for (var i = 0; i < 4; i++)
        {
            user.RegisterFailedLogin(Now);
        }

        user.IsLockedOut(Now).ShouldBeFalse();
        user.RegisterFailedLogin(Now);
        user.IsLockedOut(Now).ShouldBeTrue();
        user.IsLockedOut(Now.Add(LoginPolicy.LockoutDuration).AddSeconds(1)).ShouldBeFalse();
    }

    [Fact]
    public void Refresh_token_rotates_once_and_reuse_revokes_the_session()
    {
        var session = UserSession.Start(Guid.NewGuid(), Now, TimeSpan.FromDays(30), null, null);
        session.IssueRefreshToken("H1", Now, TimeSpan.FromDays(14));

        session.Rotate("H1", "H2", Now.AddMinutes(15), TimeSpan.FromDays(14)).IsSuccess.ShouldBeTrue();
        session.Tokens.Count.ShouldBe(2);

        var reuse = session.Rotate("H1", "H3", Now.AddMinutes(20), TimeSpan.FromDays(14));
        reuse.Error.ShouldBe(IdentityErrors.RefreshTokenReused);
        session.IsActive(Now.AddMinutes(21)).ShouldBeFalse();
        session.Rotate("H2", "H4", Now.AddMinutes(22), TimeSpan.FromDays(14)).Error.ShouldBe(IdentityErrors.SessionRevoked);
    }

    [Fact]
    public void Expired_refresh_token_is_rejected_and_tokens_never_outlive_the_session()
    {
        var session = UserSession.Start(Guid.NewGuid(), Now, TimeSpan.FromDays(1), null, null);
        var token = session.IssueRefreshToken("H1", Now, TimeSpan.FromDays(14));
        token.ExpiresAtUtc.ShouldBe(session.AbsoluteExpiresAtUtc);
        session.Rotate("H1", "H2", Now.AddDays(2), TimeSpan.FromDays(14)).Error.ShouldBe(IdentityErrors.SessionRevoked);
    }

    [Fact]
    public void System_roles_cannot_be_edited() =>
        Should.Throw<DomainException>(() => Role.CreateAdministrator(["a"]).SetPermissions(["b"]));

    [Fact]
    public void Refresh_tokens_carry_the_tenant_and_are_hashed()
    {
        var tenant = Guid.NewGuid();
        var raw = RefreshTokens.Create(tenant);
        RefreshTokens.TryReadTenant(raw, out var parsed).ShouldBeTrue();
        parsed.ShouldBe(tenant);
        RefreshTokens.Hash(raw).Length.ShouldBe(64);
        RefreshTokens.TryReadTenant("garbage", out _).ShouldBeFalse();
    }

    [Fact]
    public void Company_access_scopes_are_replaced_not_duplicated()
    {
        var user = User.Create("a@b.c", "A", "hash");
        var company = Guid.NewGuid();
        user.GrantCompanyAccess(company);
        user.GrantCompanyAccess(company, [Guid.NewGuid()]);
        user.CompanyAccess.ShouldHaveSingleItem().BranchIds.Count.ShouldBe(1);
    }
}

public class ConfigurationDomainTests
{
    [Fact]
    public void A_module_cannot_be_enabled_before_its_dependencies() =>
        ModuleCatalog.ValidateChange("pos", true, new HashSet<string> { "sales" }).Error.Code.ShouldBe("configuration.module-dependency-missing");

    [Fact]
    public void A_module_in_use_cannot_be_disabled() =>
        ModuleCatalog.ValidateChange("inventory", false, new HashSet<string> { "inventory", "sales", "pos" }).Error.Code.ShouldBe("configuration.module-in-use");

    [Fact]
    public void Core_modules_cannot_be_switched_off() =>
        ModuleCatalog.ValidateChange("accounting", false, new HashSet<string>()).Error.Code.ShouldBe("configuration.core-module");

    [Fact]
    public void Every_preset_satisfies_module_dependencies()
    {
        foreach (var preset in Erp.Modules.Configuration.Contracts.BusinessPresets.All)
        {
            var enabled = ModuleCatalog.ModulesForPreset(preset).ToHashSet();
            foreach (var code in enabled)
            {
                ModuleCatalog.Find(code)!.DependsOn.ShouldAllBe(d => ModuleCatalog.IsOn(d, enabled), $"preset {preset}, module {code}");
            }
        }
    }

    [Fact]
    public void Negative_stock_cannot_be_combined_with_fifo() =>
        PolicyValidation.Validate(new InventoryPolicy { AllowNegativeStock = true, DefaultCostingMethod = "FIFO" }).IsFailure.ShouldBeTrue();

    [Fact]
    public void Default_policies_are_valid()
    {
        foreach (var section in PolicySections.Types.Keys)
        {
            PolicyValidation.Validate(PolicySections.Default(section)).IsSuccess.ShouldBeTrue(section);
        }
    }
}

public class NumberingTests
{
    [Theory]
    [InlineData("INV", "CAI", 2026, 124, 6, "INV-CAI-2026-000124")]
    [InlineData("JV", null, 2026, 7, 6, "JV-2026-000007")]
    [InlineData("TRF", null, null, 96, 4, "TRF-0096")]
    public void Numbers_are_formatted_deterministically(string prefix, string? branch, int? year, long value, int padding, string expected) =>
        DocumentNumberGenerator.Format(prefix, branch, year, value, padding).ShouldBe(expected);
}
