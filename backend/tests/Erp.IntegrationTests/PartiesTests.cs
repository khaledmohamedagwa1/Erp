using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Erp.Application.Abstractions;
using Erp.Modules.Parties.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Erp.IntegrationTests;

public class PartiesTests(ErpApiFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    [Fact]
    public async Task Customer_lifecycle_with_terms_contacts_addresses_and_credit()
    {
        var t = await fixture.NewTenantAsync();

        var terms = await t.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/parties/payment-terms", t.CompanyId, new
        {
            code = "N30-60", nameEn = "50% 30 days, 50% 60 days", nameAr = "50% بعد 30 و50% بعد 60",
            installments = new[] { new { percent = 50m, days = 30, basis = "InvoiceDate" }, new { percent = 50m, days = 60, basis = "InvoiceDate" } },
        });
        terms.StatusCode.ShouldBe(HttpStatusCode.Created);
        var termsId = (await Json(terms)).GetProperty("id").GetGuid();

        var created = await t.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/parties/customers", t.CompanyId, new
        {
            code = "cus-00318", kind = "Business", nameEn = "Delta Retail Group", nameAr = "مجموعة دلتا للتجزئة",
            taxRegistrationNo = "123-456-789", currency = "EGP", paymentTermsId = termsId,
        });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var customer = await Json(created);
        var id = customer.GetProperty("id").GetGuid();
        customer.GetProperty("code").GetString().ShouldBe("CUS-00318");

        var duplicate = await t.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/parties/customers", t.CompanyId, new
        {
            code = "CUS-00318", kind = "Business", nameEn = "Other", nameAr = "آخر", currency = "EGP",
        });
        duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await duplicate.ErrorCodeAsync()).ShouldBe("parties.customer-code-taken");

        var contact = await t.Client.SendAsCompanyAsync(HttpMethod.Post, $"/api/v1/parties/customers/{id}/contacts", t.CompanyId,
            new { name = "Omar Fathy", phone = "+20 100 111 2222", email = "omar@delta.example", role = "Buyer" });
        contact.StatusCode.ShouldBe(HttpStatusCode.Created);
        var contactId = (await Json(contact)).GetProperty("id").GetGuid();

        (await t.Client.SendAsCompanyAsync(HttpMethod.Post, $"/api/v1/parties/customers/{id}/addresses", t.CompanyId,
            new { type = "Billing", line1 = "12 Nile Corniche", city = "Cairo", country = "EG", isDefault = false })).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await t.Client.SendAsCompanyAsync(HttpMethod.Post, $"/api/v1/parties/customers/{id}/addresses", t.CompanyId,
            new { type = "Billing", line1 = "5 Tahrir Square", city = "Cairo", country = "EG", isDefault = true })).StatusCode.ShouldBe(HttpStatusCode.Created);

        var get = await t.Client.GetAsCompanyAsync($"/api/v1/parties/customers/{id}", t.CompanyId);
        var detail = await Json(get);
        detail.GetProperty("contacts").GetArrayLength().ShouldBe(1);
        var addresses = detail.GetProperty("addresses").EnumerateArray().ToList();
        addresses.Count.ShouldBe(2);
        addresses.Single(a => a.GetProperty("isDefault").GetBoolean()).GetProperty("line1").GetString().ShouldBe("5 Tahrir Square");
        var etag = get.Headers.ETag!.Tag;

        // Credit profile: version-checked and permissioned.
        (await t.Client.SendAsCompanyAsync(HttpMethod.Put, $"/api/v1/parties/customers/{id}/credit-profile", t.CompanyId,
            new { creditLimit = 500000m, creditAction = "RequireApproval", onHold = false })).StatusCode.ShouldBe(HttpStatusCode.PreconditionRequired);
        var credit = await t.Client.SendAsCompanyAsync(HttpMethod.Put, $"/api/v1/parties/customers/{id}/credit-profile", t.CompanyId,
            new { creditLimit = 500000m, creditAction = "RequireApproval", onHold = false }, etag);
        credit.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Json(credit)).GetProperty("credit").GetProperty("creditLimit").GetDecimal().ShouldBe(500000m);
        (await t.Client.SendAsCompanyAsync(HttpMethod.Put, $"/api/v1/parties/customers/{id}/credit-profile", t.CompanyId,
            new { creditLimit = 1m, creditAction = "Block", onHold = false }, etag)).StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);

        // Update general details with the latest version.
        var update = await t.Client.SendAsCompanyAsync(HttpMethod.Put, $"/api/v1/parties/customers/{id}", t.CompanyId, new
        {
            kind = "Business", nameEn = "Delta Retail Group S.A.E.", nameAr = "مجموعة دلتا للتجزئة ش.م.م", currency = "EGP", paymentTermsId = termsId,
        }, credit.Headers.ETag!.Tag);
        update.StatusCode.ShouldBe(HttpStatusCode.OK);

        (await t.Client.SendAsCompanyAsync(HttpMethod.Delete, $"/api/v1/parties/customers/{id}/contacts/{contactId}", t.CompanyId)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await t.Client.SendAsCompanyAsync(HttpMethod.Delete, $"/api/v1/parties/customers/{id}/contacts/{contactId}", t.CompanyId)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var search = await Json(await t.Client.GetAsCompanyAsync("/api/v1/parties/customers?search=Delta", t.CompanyId));
        search.GetProperty("totalCount").GetInt32().ShouldBe(1);

        (await t.Client.SendAsCompanyAsync(HttpMethod.Post, $"/api/v1/parties/customers/{id}/deactivate", t.CompanyId)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await Json(await t.Client.GetAsCompanyAsync("/api/v1/parties/customers", t.CompanyId))).GetProperty("totalCount").GetInt32().ShouldBe(0);
        (await Json(await t.Client.GetAsCompanyAsync("/api/v1/parties/customers?includeInactive=true", t.CompanyId))).GetProperty("totalCount").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task Invalid_payment_terms_and_unknown_references_are_rejected()
    {
        var t = await fixture.NewTenantAsync();
        var bad = await t.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/parties/payment-terms", t.CompanyId, new
        {
            code = "BAD", nameEn = "Bad", nameAr = "سيء", installments = new[] { new { percent = 60m, days = 30, basis = "InvoiceDate" } },
        });
        bad.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        var unknownTerms = await t.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/parties/suppliers", t.CompanyId, new
        {
            code = "SUP-1", nameEn = "Sinai Olive Groves", nameAr = "بساتين سيناء", currency = "EGP", paymentTermsId = Guid.NewGuid(),
        });
        (await unknownTerms.ErrorCodeAsync()).ShouldBe("parties.payment-terms-not-found");
    }

    [Fact]
    public async Task Suppliers_and_customers_are_isolated_per_company()
    {
        var t = await fixture.NewTenantAsync();
        var second = await t.Client.PostAsJsonAsync("/api/v1/organization/companies", new
        {
            code = "SECOND", nameEn = "Second Co", nameAr = "الشركة الثانية", baseCurrency = "SAR", businessPreset = "wholesale",
        }, Ct);
        var secondId = (await Json(second)).GetProperty("id").GetGuid();

        var supplier = await t.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/parties/suppliers", t.CompanyId, new
        {
            code = "SUP-1", nameEn = "Sinai Olive Groves", nameAr = "بساتين سيناء", currency = "EGP",
        });
        supplier.StatusCode.ShouldBe(HttpStatusCode.Created);
        var supplierId = (await Json(supplier)).GetProperty("id").GetGuid();

        // Same code is fine in another company; the first company's supplier is invisible there.
        (await t.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/parties/suppliers", secondId, new
        {
            code = "SUP-1", nameEn = "Jeddah Supplies", nameAr = "توريدات جدة", currency = "SAR",
        })).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await t.Client.GetAsCompanyAsync($"/api/v1/parties/suppliers/{supplierId}", secondId)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Json(await t.Client.GetAsCompanyAsync("/api/v1/parties/suppliers", secondId))).GetProperty("items").EnumerateArray()
            .Single().GetProperty("currency").GetString().ShouldBe("SAR");

        // Another tenant cannot see them at all.
        var other = await fixture.NewTenantAsync();
        (await other.Client.GetAsCompanyAsync($"/api/v1/parties/suppliers/{supplierId}", other.CompanyId)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Changing_a_credit_limit_needs_its_own_permission()
    {
        var t = await fixture.NewTenantAsync();
        var customer = await Json(await t.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/parties/customers", t.CompanyId, new
        {
            code = "C1", kind = "Individual", nameEn = "Ahmed", nameAr = "أحمد", currency = "EGP",
        }));
        var id = customer.GetProperty("id").GetGuid();

        var role = await t.Client.PostAsJsonAsync("/api/v1/identity/roles", new
        {
            name = "Sales clerk", permissions = new[] { PartiesPermissions.CustomerRead, PartiesPermissions.CustomerManage },
        }, Ct);
        var roleId = (await Json(role)).GetProperty("id").GetGuid();
        (await t.Client.PostAsJsonAsync("/api/v1/identity/users", new
        {
            email = "clerk@test.example", displayName = "Clerk", password = "Clerk-Passw0rd!", roleIds = new[] { roleId }, companyIds = new[] { t.CompanyId },
        }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);

        using var clerk = fixture.CreateClient();
        await ErpApiFixture.SignInAsync(clerk, t.TenantCode, "clerk@test.example", "Clerk-Passw0rd!");
        var get = await clerk.GetAsCompanyAsync($"/api/v1/parties/customers/{id}", t.CompanyId);
        get.StatusCode.ShouldBe(HttpStatusCode.OK);
        var denied = await clerk.SendAsCompanyAsync(HttpMethod.Put, $"/api/v1/parties/customers/{id}/credit-profile", t.CompanyId,
            new { creditLimit = 1_000_000m, creditAction = "Warn", onHold = false }, get.Headers.ETag!.Tag);
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Walk_in_customer_is_created_once_and_due_schedule_sums_to_the_amount()
    {
        var t = await fixture.NewTenantAsync();
        var first = await Json(await t.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/parties/customers/walk-in", t.CompanyId));
        var again = await Json(await t.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/parties/customers/walk-in", t.CompanyId));
        first.GetProperty("id").GetGuid().ShouldBe(again.GetProperty("id").GetGuid());
        first.GetProperty("isWalkIn").GetBoolean().ShouldBeTrue();
        first.GetProperty("currency").GetString().ShouldBe("EGP");

        var terms = await Json(await t.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/parties/payment-terms", t.CompanyId, new
        {
            code = "THIRDS", nameEn = "Thirds", nameAr = "أثلاث",
            installments = new[]
            {
                new { percent = 33.333333m, days = 30, basis = "InvoiceDate" },
                new { percent = 33.333333m, days = 60, basis = "InvoiceDate" },
                new { percent = 33.333334m, days = 0, basis = "EndOfMonth" },
            },
        }));

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenant.SetTenant(t.TenantId);
        tenant.SetCompany(t.CompanyId);
        var queries = scope.ServiceProvider.GetRequiredService<IPartyQueries>();
        var schedule = await queries.GetDueScheduleAsync(terms.GetProperty("id").GetGuid(), new DateOnly(2026, 11, 27), 1000m, 2, Ct);
        schedule.Sum(s => s.Amount).ShouldBe(1000m);
        schedule.Select(s => s.DueDate).ShouldBe([new DateOnly(2026, 11, 30), new DateOnly(2026, 12, 27), new DateOnly(2027, 1, 26)]);
        (await queries.GetWalkInCustomerIdAsync(t.CompanyId, Ct)).ShouldBe(first.GetProperty("id").GetGuid());
        (await queries.GetCustomerAsync(first.GetProperty("id").GetGuid(), Ct))!.IsWalkIn.ShouldBeTrue();
    }
}
