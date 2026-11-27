using Erp.Application.Abstractions;
using Erp.Modules.Parties.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Erp.IntegrationTests;

public class WalkInConcurrencyTests(ErpApiFixture fixture)
{
    [Fact]
    public async Task Concurrent_first_callers_all_get_the_same_walk_in_customer()
    {
        var t = await fixture.NewTenantAsync();

        async Task<Guid> Resolve()
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
            tenant.SetTenant(t.TenantId);
            tenant.SetCompany(t.CompanyId);
            return await scope.ServiceProvider.GetRequiredService<IPartyQueries>().GetWalkInCustomerIdAsync(t.CompanyId, TestContext.Current.CancellationToken);
        }

        var ids = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Resolve()));
        ids.Distinct().ShouldHaveSingleItem();
    }
}
