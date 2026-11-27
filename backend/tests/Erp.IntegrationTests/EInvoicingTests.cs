using System.Net;
using System.Text.Json;
using Erp.Infrastructure.Outbox;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Erp.IntegrationTests;

/// <summary>E-invoicing port with the bundled mock adapter: queue on posting, chain, accept, reject, transient retry, manual retry.</summary>
public class EInvoicingTests(ErpApiFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Shop(ErpApiFixture.TenantSession T, Guid Main, Guid Pcs, Guid Repair, Guid PriceList);

    private static async Task<JsonElement> Ok(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.IsSuccessStatusCode.ShouldBeTrue($"{(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static Task<HttpResponseMessage> Send(Shop s, HttpMethod method, string url, object? body = null, bool idempotent = false, string? etag = null) =>
        s.T.Client.SendAsCompanyAsync(method, url, s.T.CompanyId, body, etag, idempotencyKey: idempotent ? Guid.NewGuid().ToString() : null);

    private static async Task<JsonElement> Get(Shop s, string url) => await Ok(await s.T.Client.GetAsCompanyAsync(url, s.T.CompanyId));

    /// <summary>Generic chart, FY2026, Egyptian taxes, a 50 EGP repair service (14 % VAT) on a price list.</summary>
    private async Task<Shop> NewShopAsync()
    {
        var t = await fixture.NewTenantAsync();
        var s = new Shop(t, Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty);
        await Ok(await Send(s, HttpMethod.Post, "/api/v1/accounting/coa-templates/generic"));
        var le = (await Get(s, "/api/v1/organization/legal-entities")).EnumerateArray().First().GetProperty("id").GetGuid();
        await Ok(await Send(s, HttpMethod.Post, "/api/v1/accounting/fiscal-years", new { legalEntityId = le, startDate = "2026-01-01" }));
        await Ok(await Send(s, HttpMethod.Post, "/api/v1/tax/templates/egypt", new { }));
        var main = (await Get(s, "/api/v1/organization/warehouses")).GetProperty("items").EnumerateArray()
            .First(w => w.GetProperty("code").GetString() == "MAIN").GetProperty("id").GetGuid();
        var uoms = (await Ok(await Send(s, HttpMethod.Post, "/api/v1/catalog/uoms/seed-standard"))).EnumerateArray()
            .ToDictionary(u => u.GetProperty("code").GetString()!, u => u.GetProperty("id").GetGuid());
        var category = (await Ok(await Send(s, HttpMethod.Post, "/api/v1/catalog/categories",
            new { code = "SRV", nameEn = "Services", nameAr = "خدمات", defaultCostingMethod = "WeightedAverage", postingGroup = "SRV" }))).GetProperty("id").GetGuid();
        var repair = (await Ok(await Send(s, HttpMethod.Post, "/api/v1/catalog/products", new
        {
            sku = "REPAIR", nameEn = "Repair", nameAr = "إصلاح", type = "Service", baseUomId = uoms["PCS"], categoryId = category, tracking = "None", expiryTracked = false,
            taxCategory = "standard",
        }))).GetProperty("id").GetGuid();
        var list = (await Ok(await Send(s, HttpMethod.Post, "/api/v1/catalog/price-lists", new { code = "RETAIL", name = "Retail", currency = "EGP", taxInclusive = false })))
            .GetProperty("id").GetGuid();
        await Ok(await Send(s, HttpMethod.Post, $"/api/v1/catalog/price-lists/{list}/items", new { productId = repair, uomId = uoms["PCS"], minQuantity = 0m, price = 50m }));
        return s with { Main = main, Pcs = uoms["PCS"], Repair = repair, PriceList = list };
    }

    private static async Task<Guid> CustomerAsync(Shop s, string code, string name) =>
        (await Ok(await Send(s, HttpMethod.Post, "/api/v1/parties/customers",
            new { code, kind = "Business", nameEn = name, nameAr = name, currency = "EGP", priceListId = s.PriceList, taxRegistrationNo = "100-200-300" }))).GetProperty("id").GetGuid();

    private static async Task<Guid> InvoiceAsync(Shop s, Guid customer, decimal quantity)
    {
        var draft = await Ok(await Send(s, HttpMethod.Post, "/api/v1/sales/invoices", new
        {
            customerId = customer, warehouseId = s.Main, date = "2026-06-02",
            lines = new[] { new { productId = s.Repair, uomId = s.Pcs, quantity } },
        }));
        var id = draft.GetProperty("id").GetGuid();
        await Ok(await Send(s, HttpMethod.Post, $"/api/v1/sales/invoices/{id}/submit", idempotent: true));
        await Ok(await Send(s, HttpMethod.Post, $"/api/v1/sales/invoices/{id}/post", idempotent: true));
        return id;
    }

    /// <summary>Drives the outbox until the document's submission reaches one of the statuses (or gives up).</summary>
    private async Task<JsonElement> SubmissionAsync(Shop s, Guid documentId, params string[] statuses)
    {
        var processor = fixture.Factory.Services.GetRequiredService<IOutboxProcessor>();
        JsonElement? last = null;
        for (var i = 0; i < 40; i++)
        {
            await processor.ProcessPendingAsync(Ct);
            var items = (await Get(s, $"/api/v1/tax/e-invoicing/submissions?sourceDocId={documentId}")).GetProperty("items");
            if (items.GetArrayLength() == 1)
            {
                last = items[0];
                if (statuses.Contains(last.Value.GetProperty("status").GetString()))
                {
                    return last.Value;
                }
            }
        }

        throw new ShouldAssertException($"Submission for {documentId} did not reach {string.Join("/", statuses)}: {last}");
    }

    [Fact]
    public async Task Posted_invoices_are_submitted_chained_accepted_rejected_and_retried()
    {
        var s = await NewShopAsync();
        var buyer = await CustomerAsync(s, "C1", "Delta Retail");

        // Disabled by default: nothing is queued.
        (await Get(s, "/api/v1/tax/e-invoicing/settings")).GetProperty("enabled").GetBoolean().ShouldBeFalse();
        var before = await InvoiceAsync(s, buyer, 1);
        await fixture.Factory.Services.GetRequiredService<IOutboxProcessor>().ProcessPendingAsync(Ct);
        (await Get(s, $"/api/v1/tax/e-invoicing/submissions?sourceDocId={before}")).GetProperty("items").GetArrayLength().ShouldBe(0);

        (await (await Send(s, HttpMethod.Put, "/api/v1/tax/e-invoicing/settings", new { enabled = true, provider = "eta" })).ErrorCodeAsync())
            .ShouldBe("tax.einvoice-unknown-provider");
        var settings = await Ok(await Send(s, HttpMethod.Put, "/api/v1/tax/e-invoicing/settings", new { enabled = true, provider = "mock", environment = "sandbox" }));
        settings.GetProperty("availableProviders").EnumerateArray().Select(p => p.GetString()).ShouldContain("mock");

        // Accepted: counter 1, no previous hash, provider reference and QR.
        var first = await InvoiceAsync(s, buyer, 2);
        var accepted = await SubmissionAsync(s, first, "Accepted", "Rejected");
        accepted.GetProperty("status").GetString().ShouldBe("Accepted");
        accepted.GetProperty("counter").GetInt64().ShouldBe(1);
        accepted.GetProperty("previousHash").ValueKind.ShouldBe(JsonValueKind.Null);
        accepted.GetProperty("providerReference").GetString()!.ShouldStartWith("MOCK-1-");
        accepted.GetProperty("qrCode").GetString().ShouldNotBeNullOrEmpty();
        accepted.GetProperty("attempts").GetInt32().ShouldBe(1);

        // Transient failure: recorded, retried by the outbox with the same payload, then accepted; chained to the first.
        var flaky = await CustomerAsync(s, "C2", "MOCK-TRANSIENT Trading");
        var second = await InvoiceAsync(s, flaky, 1);
        var retried = await SubmissionAsync(s, second, "Accepted", "Rejected");
        retried.GetProperty("status").GetString().ShouldBe("Accepted");
        retried.GetProperty("attempts").GetInt32().ShouldBe(2);
        retried.GetProperty("counter").GetInt64().ShouldBe(2);
        retried.GetProperty("previousHash").GetString().ShouldBe(accepted.GetProperty("hash").GetString());

        // Rejected: final until a user retries; a retry rebuilds the document with a new counter.
        var refused = await CustomerAsync(s, "C3", "MOCK-REJECT Imports");
        var third = await InvoiceAsync(s, refused, 1);
        var rejected = await SubmissionAsync(s, third, "Rejected", "Accepted");
        rejected.GetProperty("status").GetString().ShouldBe("Rejected");
        rejected.GetProperty("errors").GetString()!.ShouldContain("buyer-rejected-by-mock");
        rejected.GetProperty("counter").GetInt64().ShouldBe(3);
        (await (await Send(s, HttpMethod.Post, $"/api/v1/tax/e-invoicing/submissions/{accepted.GetProperty("id").GetGuid()}/retry")).ErrorCodeAsync())
            .ShouldBe("tax.einvoice-not-retryable");

        // The user corrects the customer and retries: the document is rebuilt with a new UUID and the next counter.
        var current = await s.T.Client.GetAsCompanyAsync($"/api/v1/parties/customers/{refused}", s.T.CompanyId);
        await Ok(await Send(s, HttpMethod.Put, $"/api/v1/parties/customers/{refused}", new
        {
            kind = "Business", nameEn = "Imports Co", nameAr = "Imports Co", taxRegistrationNo = "100-200-301", currency = "EGP", priceListId = s.PriceList,
        }, etag: current.Headers.ETag?.Tag));
        (await Ok(await Send(s, HttpMethod.Post, $"/api/v1/tax/e-invoicing/submissions/{rejected.GetProperty("id").GetGuid()}/retry")))
            .GetProperty("status").GetString().ShouldBe("Pending");
        var resubmitted = await SubmissionAsync(s, third, "Accepted", "Rejected");
        resubmitted.GetProperty("status").GetString().ShouldBe("Accepted");
        resubmitted.GetProperty("counter").GetInt64().ShouldBe(4);
        resubmitted.GetProperty("previousHash").GetString().ShouldBe(rejected.GetProperty("hash").GetString());
        resubmitted.GetProperty("uuid").GetGuid().ShouldNotBe(rejected.GetProperty("uuid").GetGuid());

        // One submission per document even when the posting event is delivered again.
        (await Get(s, $"/api/v1/tax/e-invoicing/submissions?sourceDocId={first}")).GetProperty("items").GetArrayLength().ShouldBe(1);
        (await Get(s, "/api/v1/tax/e-invoicing/settings")).GetProperty("lastCounter").GetInt64().ShouldBe(4);
        (await s.T.Client.GetAsCompanyAsync("/api/v1/tax/e-invoicing/submissions/" + Guid.NewGuid(), s.T.CompanyId)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
