using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Erp.Application.Abstractions;
using Erp.Modules.Accounting.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Erp.IntegrationTests;

public class FixedAssetsTests(ErpApiFixture fixture)
{
    private const string Api = "/api/v1/assets";

    private sealed record Books(ErpApiFixture.TenantSession T, Guid LegalEntityId, Dictionary<string, Guid> Accounts, Guid CategoryId);

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.ShouldBeTrue($"{(int)response.StatusCode}: {text}");
        return string.IsNullOrEmpty(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static Task<HttpResponseMessage> Send(Books b, HttpMethod method, string url, object? body = null, bool idempotent = false) =>
        b.T.Client.SendAsCompanyAsync(method, url, b.T.CompanyId, body, idempotencyKey: idempotent ? Guid.NewGuid().ToString() : null);

    private static Task<HttpResponseMessage> Get(Books b, string url) => b.T.Client.GetAsCompanyAsync(url, b.T.CompanyId);

    private async Task<Books> NewBooksAsync()
    {
        var t = await fixture.NewTenantAsync(preset: "services");
        (await t.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/accounting/coa-templates/generic", t.CompanyId)).EnsureSuccessStatusCode();
        var legal = await Json(await t.Client.GetAsCompanyAsync("/api/v1/organization/legal-entities", t.CompanyId));
        var le = legal.EnumerateArray().First().GetProperty("id").GetGuid();
        (await t.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/accounting/fiscal-years", t.CompanyId, new { legalEntityId = le, startDate = "2026-01-01" }))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        var accounts = (await Json(await t.Client.GetAsCompanyAsync("/api/v1/accounting/accounts", t.CompanyId)))
            .EnumerateArray().ToDictionary(a => a.GetProperty("code").GetString()!, a => a.GetProperty("id").GetGuid());

        // Optional module: off until switched on.
        (await t.Client.GetAsCompanyAsync($"{Api}/categories", t.CompanyId)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await t.Client.SendAsCompanyAsync(HttpMethod.Put, "/api/v1/configuration/modules/fixed-assets", t.CompanyId, new { enabled = true })).StatusCode.ShouldBe(HttpStatusCode.OK);

        var category = await Json(await t.Client.SendAsCompanyAsync(HttpMethod.Post, $"{Api}/categories", t.CompanyId, new
        {
            code = "veh", nameEn = "Vehicles", nameAr = "مركبات", defaultMethod = "StraightLine", usefulLifeMonths = 120, salvagePercent = 0m,
        }));
        category.GetProperty("code").GetString().ShouldBe("VEH");
        return new Books(t, le, accounts, category.GetProperty("id").GetGuid());
    }

    private static async Task<JsonElement> NewActiveAssetAsync(Books b, decimal cost, string inService = "2026-01-10", int? life = null, decimal? salvage = null)
    {
        var draft = await Json(await Send(b, HttpMethod.Post, $"{Api}/assets", new
        {
            nameEn = "Truck", nameAr = "شاحنة", categoryId = b.CategoryId, legalEntityId = b.LegalEntityId, location = "HQ",
            acquisitionDate = inService, inServiceDate = inService, acquisitionCost = cost, usefulLifeMonths = life, salvageValue = salvage,
        }));
        draft.GetProperty("status").GetString().ShouldBe("Draft");
        draft.GetProperty("number").ValueKind.ShouldBe(JsonValueKind.Null);
        var id = draft.GetProperty("id").GetGuid();
        var active = await Json(await Send(b, HttpMethod.Post, $"{Api}/assets/{id}/activate", new { mode = "ManualJournal", clearingAccountId = b.Accounts["1120"] }, idempotent: true));
        active.GetProperty("status").GetString().ShouldBe("Active");
        active.GetProperty("number").GetString()!.ShouldStartWith("FA");
        return active;
    }

    private static async Task<Dictionary<string, (decimal Debit, decimal Credit)>> JournalAsync(Books b, Guid journalId)
    {
        var journal = await Json(await Get(b, $"/api/v1/accounting/journals/{journalId}"));
        journal.GetProperty("status").GetString().ShouldBe("Posted");
        return journal.GetProperty("lines").EnumerateArray()
            .GroupBy(l => l.GetProperty("accountCode").GetString()!)
            .ToDictionary(g => g.Key, g => (g.Sum(l => l.GetProperty("baseDebit").GetDecimal()), g.Sum(l => l.GetProperty("baseCredit").GetDecimal())));
    }

    private async Task<decimal> RoleBalanceAsync(Books b, string role)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenant.SetTenant(b.T.TenantId);
        tenant.SetCompany(b.T.CompanyId);
        return await scope.ServiceProvider.GetRequiredService<ILedgerQueries>().GetRoleBalanceAsync(b.LegalEntityId, role, new DateOnly(2026, 12, 31));
    }

    private static Task<HttpResponseMessage> PostRun(Books b, string period) =>
        Send(b, HttpMethod.Post, $"{Api}/depreciation-runs", new { legalEntityId = b.LegalEntityId, period }, idempotent: true);

    [Fact]
    public async Task J12_activation_capitalises_and_monthly_run_posts_1000()
    {
        var b = await NewBooksAsync();
        var asset = await NewActiveAssetAsync(b, 120_000m);
        var id = asset.GetProperty("id").GetGuid();

        var acquisition = await JournalAsync(b, asset.GetProperty("acquisitionJournalId").GetGuid());
        acquisition["1510"].ShouldBe((120_000m, 0m));
        acquisition["1120"].ShouldBe((0m, 120_000m));

        var schedule = await Json(await Get(b, $"{Api}/assets/{id}/schedule"));
        schedule.GetArrayLength().ShouldBe(120);
        schedule.EnumerateArray().ShouldAllBe(l => l.GetProperty("amount").GetDecimal() == 1000m);
        schedule[0].GetProperty("period").GetString().ShouldBe("2026-01-01");
        schedule[119].GetProperty("netBookValue").GetDecimal().ShouldBe(0m);

        var preview = await Json(await Send(b, HttpMethod.Post, $"{Api}/depreciation-runs/preview", new { legalEntityId = b.LegalEntityId, period = "2026-01-31" }));
        preview.GetProperty("total").GetDecimal().ShouldBe(1000m);

        var created = await PostRun(b, "2026-01-31");
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var run = await Json(created);
        run.GetProperty("total").GetDecimal().ShouldBe(1000m);
        run.GetProperty("period").GetString().ShouldBe("2026-01-01");
        var journal = await JournalAsync(b, run.GetProperty("journalId").GetGuid());
        journal["5510"].ShouldBe((1000m, 0m));
        journal["1590"].ShouldBe((0m, 1000m));

        var after = await Json(await Get(b, $"{Api}/assets/{id}"));
        after.GetProperty("accumulatedDepreciation").GetDecimal().ShouldBe(1000m);
        after.GetProperty("netBookValue").GetDecimal().ShouldBe(119_000m);
        var history = await Json(await Get(b, $"{Api}/assets/{id}/history"));
        history.EnumerateArray().Select(h => h.GetProperty("kind").GetString()).ShouldBe(["Acquired", "Depreciated"]);

        // Running the same period again returns the posted run and posts nothing more.
        var rerun = await PostRun(b, "2026-01-15");
        rerun.StatusCode.ShouldBe(HttpStatusCode.OK);
        var again = await Json(rerun);
        again.GetProperty("id").GetGuid().ShouldBe(run.GetProperty("id").GetGuid());
        again.GetProperty("alreadyPosted").GetBoolean().ShouldBeTrue();
        (await RoleBalanceAsync(b, PostingRoles.DepreciationExpense)).ShouldBe(1000m);
        (await Json(await Get(b, $"{Api}/assets/{id}"))).GetProperty("accumulatedDepreciation").GetDecimal().ShouldBe(1000m);
    }

    [Fact]
    public async Task Catch_up_run_reverse_and_rerun()
    {
        var b = await NewBooksAsync();
        var id = (await NewActiveAssetAsync(b, 120_000m)).GetProperty("id").GetGuid();
        var jan = await Json(await PostRun(b, "2026-01-31"));
        var mar = await Json(await PostRun(b, "2026-03-31")); // February and March together
        mar.GetProperty("total").GetDecimal().ShouldBe(2000m);

        // Only the latest run can be reversed.
        (await (await Send(b, HttpMethod.Post, $"{Api}/depreciation-runs/{jan.GetProperty("id").GetGuid()}/reverse", new { }, idempotent: true)).ErrorCodeAsync())
            .ShouldBe("assets.not-latest-run");

        var reversed = await Json(await Send(b, HttpMethod.Post, $"{Api}/depreciation-runs/{mar.GetProperty("id").GetGuid()}/reverse", new { }, idempotent: true));
        reversed.GetProperty("status").GetString().ShouldBe("Reversed");
        (await Json(await Get(b, $"/api/v1/accounting/journals/{mar.GetProperty("journalId").GetGuid()}"))).GetProperty("status").GetString().ShouldBe("Reversed");
        (await Json(await Get(b, $"{Api}/assets/{id}"))).GetProperty("accumulatedDepreciation").GetDecimal().ShouldBe(1000m);
        (await RoleBalanceAsync(b, PostingRoles.DepreciationExpense)).ShouldBe(1000m);

        // The period is free again.
        var repost = await Json(await PostRun(b, "2026-03-31"));
        repost.GetProperty("id").GetGuid().ShouldNotBe(mar.GetProperty("id").GetGuid());
        repost.GetProperty("total").GetDecimal().ShouldBe(2000m);
        var runs = await Json(await Get(b, $"{Api}/depreciation-runs?legalEntityId={b.LegalEntityId}"));
        runs.GetArrayLength().ShouldBe(3);
        (await RoleBalanceAsync(b, PostingRoles.AccumulatedDepreciation)).ShouldBe(-3000m);
    }

    [Fact]
    public async Task Run_in_a_closed_period_cannot_be_reversed()
    {
        var b = await NewBooksAsync();
        await NewActiveAssetAsync(b, 12_000m, life: 12);
        var run = await Json(await PostRun(b, "2026-01-31"));
        var years = await Json(await Get(b, $"/api/v1/accounting/fiscal-years?legalEntityId={b.LegalEntityId}"));
        var janPeriod = years.EnumerateArray().First().GetProperty("periods")[0].GetProperty("id").GetGuid();
        (await Send(b, HttpMethod.Put, $"/api/v1/accounting/periods/{janPeriod}/status", new { status = "Closed" })).StatusCode.ShouldBe(HttpStatusCode.OK);

        var refused = await Send(b, HttpMethod.Post, $"{Api}/depreciation-runs/{run.GetProperty("id").GetGuid()}/reverse", new { }, idempotent: true);
        refused.IsSuccessStatusCode.ShouldBeFalse();
        (await refused.ErrorCodeAsync()).ShouldBe("accounting.period-closed");
        (await Json(await Get(b, $"{Api}/depreciation-runs"))).EnumerateArray().Single().GetProperty("status").GetString().ShouldBe("Posted");
    }

    [Fact]
    public async Task Disposal_with_gain_and_with_loss_posts_to_the_ledger()
    {
        var b = await NewBooksAsync();
        var gainAsset = await NewActiveAssetAsync(b, 12_000m, "2026-01-05", life: 12);
        var lossAsset = await NewActiveAssetAsync(b, 12_000m, "2026-01-05", life: 12);
        var gainId = gainAsset.GetProperty("id").GetGuid();
        var lossId = lossAsset.GetProperty("id").GetGuid();

        // Depreciation must be up to date before disposal.
        (await (await Send(b, HttpMethod.Post, $"{Api}/assets/{gainId}/dispose", new { date = "2026-04-10", proceeds = 10_000m, proceedsAccountId = b.Accounts["1120"] }, idempotent: true))
            .ErrorCodeAsync()).ShouldBe("assets.depreciation-not-up-to-date");
        (await Json(await PostRun(b, "2026-03-31"))).GetProperty("total").GetDecimal().ShouldBe(6000m);

        // NBV 9,000; proceeds 10,000 → gain 1,000.
        var sold = await Json(await Send(b, HttpMethod.Post, $"{Api}/assets/{gainId}/dispose", new { date = "2026-04-10", proceeds = 10_000m, proceedsAccountId = b.Accounts["1120"] }, idempotent: true));
        sold.GetProperty("status").GetString().ShouldBe("Disposed");
        sold.GetProperty("disposalGainLoss").GetDecimal().ShouldBe(1000m);
        var gain = await JournalAsync(b, sold.GetProperty("disposalJournalId").GetGuid());
        gain["1590"].ShouldBe((3000m, 0m));
        gain["1120"].ShouldBe((10_000m, 0m));
        gain["1510"].ShouldBe((0m, 12_000m));
        gain["4940"].ShouldBe((0m, 1000m));

        // Proceeds 5,000 → loss 4,000.
        var scrapped = await Json(await Send(b, HttpMethod.Post, $"{Api}/assets/{lossId}/dispose", new { date = "2026-04-10", proceeds = 5_000m, proceedsAccountId = b.Accounts["1120"] }, idempotent: true));
        scrapped.GetProperty("disposalGainLoss").GetDecimal().ShouldBe(-4000m);
        var loss = await JournalAsync(b, scrapped.GetProperty("disposalJournalId").GetGuid());
        loss["4940"].ShouldBe((4000m, 0m));
        loss["1510"].ShouldBe((0m, 12_000m));

        (await RoleBalanceAsync(b, PostingRoles.AssetDisposalGainLoss)).ShouldBe(3000m);
        (await RoleBalanceAsync(b, PostingRoles.FixedAssetCost)).ShouldBe(0m);
        (await RoleBalanceAsync(b, PostingRoles.AccumulatedDepreciation)).ShouldBe(0m);

        // Disposed assets drop out of later runs and cannot be disposed twice.
        (await (await PostRun(b, "2026-04-30")).ErrorCodeAsync()).ShouldBe("assets.nothing-to-depreciate");
        (await (await Send(b, HttpMethod.Post, $"{Api}/assets/{lossId}/dispose", new { date = "2026-04-10", proceeds = 0m }, idempotent: true)).ErrorCodeAsync())
            .ShouldBe("assets.disposed");
    }

    [Fact]
    public async Task Register_ties_to_the_ledger_after_revaluation_and_transfer()
    {
        var b = await NewBooksAsync();
        var a = (await NewActiveAssetAsync(b, 120_000m)).GetProperty("id").GetGuid();
        var c = (await NewActiveAssetAsync(b, 24_000m, "2026-02-20", life: 24)).GetProperty("id").GetGuid();
        await Json(await PostRun(b, "2026-03-31"));

        // Revalue A from 117,000 to 125,000 against an equity account; the remaining 117 months are rescheduled.
        var revalued = await Json(await Send(b, HttpMethod.Post, $"{Api}/assets/{a}/revalue",
            new { date = "2026-03-31", newNetBookValue = 125_000m, reserveAccountId = b.Accounts["3200"] }, idempotent: true));
        revalued.GetProperty("cost").GetDecimal().ShouldBe(128_000m);
        revalued.GetProperty("netBookValue").GetDecimal().ShouldBe(125_000m);
        var schedule = await Json(await Get(b, $"{Api}/assets/{a}/schedule"));
        schedule.EnumerateArray().Count(l => !l.GetProperty("posted").GetBoolean()).ShouldBe(117);
        schedule.EnumerateArray().Where(l => !l.GetProperty("posted").GetBoolean()).Sum(l => l.GetProperty("amount").GetDecimal()).ShouldBe(125_000m);

        // A user-chosen account must be a plain posting account.
        (await (await Send(b, HttpMethod.Post, $"{Api}/assets/{a}/revalue",
            new { date = "2026-03-31", newNetBookValue = 120_000m, reserveAccountId = b.Accounts["1210"] }, idempotent: true)).ErrorCodeAsync()).ShouldBe("assets.account-invalid");

        var moved = await Json(await Send(b, HttpMethod.Post, $"{Api}/assets/{c}/transfer", new { date = "2026-04-01", location = "Branch 2", note = "Relocated" }));
        moved.GetProperty("location").GetString().ShouldBe("Branch 2");
        await Json(await PostRun(b, "2026-04-30"));

        var register = await Json(await Get(b, $"{Api}/reports/register?legalEntityId={b.LegalEntityId}&asOf=2026-12-31"));
        register.GetProperty("assets").GetArrayLength().ShouldBe(2);
        var cost = register.GetProperty("totalCost").GetDecimal();
        var accumulated = register.GetProperty("totalAccumulatedDepreciation").GetDecimal();
        cost.ShouldBe(152_000m);
        cost.ShouldBe(await RoleBalanceAsync(b, PostingRoles.FixedAssetCost));
        accumulated.ShouldBe(-await RoleBalanceAsync(b, PostingRoles.AccumulatedDepreciation));
        register.GetProperty("totalNetBookValue").GetDecimal().ShouldBe(cost - accumulated);
        register.GetProperty("categories").EnumerateArray().Single().GetProperty("categoryCode").GetString().ShouldBe("VEH");

        // As of the end of February: before the revaluation, and the catch-up run (dated 31 March) is not yet in the ledger.
        var february = await Json(await Get(b, $"{Api}/reports/register?legalEntityId={b.LegalEntityId}&asOf=2026-02-28"));
        february.GetProperty("totalCost").GetDecimal().ShouldBe(144_000m);
        february.GetProperty("totalAccumulatedDepreciation").GetDecimal().ShouldBe(0m);
        var march = await Json(await Get(b, $"{Api}/reports/register?legalEntityId={b.LegalEntityId}&asOf=2026-03-31"));
        march.GetProperty("totalCost").GetDecimal().ShouldBe(152_000m);
        march.GetProperty("totalAccumulatedDepreciation").GetDecimal().ShouldBe(4000m);

        var history = await Json(await Get(b, $"{Api}/assets/{c}/history"));
        history.EnumerateArray().Select(h => h.GetProperty("kind").GetString()).ShouldContain("Transferred");
    }

    [Fact]
    public async Task Drafts_can_be_edited_and_deleted_but_active_assets_cannot()
    {
        var b = await NewBooksAsync();
        var draft = await Json(await Send(b, HttpMethod.Post, $"{Api}/assets", new
        {
            nameEn = "Laptop", nameAr = "حاسب", categoryId = b.CategoryId, legalEntityId = b.LegalEntityId,
            acquisitionDate = "2026-02-01", inServiceDate = "2026-02-01", acquisitionCost = 3_600m, usefulLifeMonths = 36,
        }));
        var id = draft.GetProperty("id").GetGuid();
        var updated = await Json(await Send(b, HttpMethod.Put, $"{Api}/assets/{id}", new
        {
            nameEn = "Laptop Pro", nameAr = "حاسب", categoryId = b.CategoryId, legalEntityId = b.LegalEntityId,
            acquisitionDate = "2026-02-01", inServiceDate = "2026-02-01", acquisitionCost = 4_800m, usefulLifeMonths = 48, salvageValue = 0m,
        }));
        updated.GetProperty("cost").GetDecimal().ShouldBe(4_800m);
        (await (await Send(b, HttpMethod.Post, $"{Api}/assets/{id}/activate", new { mode = "ManualJournal" }, idempotent: true)).ErrorCodeAsync())
            .ShouldBe("assets.clearing-account-required");

        // A purchase-invoice acquisition must point at a real posted invoice (see Purchase_invoice_link_is_validated...).
        (await (await Send(b, HttpMethod.Post, $"{Api}/assets/{id}/activate",
            new { mode = "PurchaseInvoice", sourceDocType = "PurchaseInvoice", sourceDocId = Guid.NewGuid() }, idempotent: true)).ErrorCodeAsync())
            .ShouldBe("assets.source-invoice-not-found");
        (await (await Send(b, HttpMethod.Post, $"{Api}/assets/{id}/activate",
            new { mode = "PurchaseInvoice", sourceDocType = "GoodsReceipt", sourceDocId = Guid.NewGuid() }, idempotent: true)).ErrorCodeAsync())
            .ShouldBe("assets.source-type-unsupported");
        await Json(await Send(b, HttpMethod.Post, $"{Api}/assets/{id}/activate", new { mode = "ManualJournal", clearingAccountId = b.Accounts["1120"] }, idempotent: true));

        (await (await Send(b, HttpMethod.Delete, $"{Api}/assets/{id}")).ErrorCodeAsync()).ShouldBe("assets.not-draft");

        var other = await Json(await Send(b, HttpMethod.Post, $"{Api}/assets", new
        {
            nameEn = "Desk", nameAr = "مكتب", categoryId = b.CategoryId, legalEntityId = b.LegalEntityId,
            acquisitionDate = "2026-02-01", inServiceDate = "2026-02-01", acquisitionCost = 500m,
        }));
        (await Send(b, HttpMethod.Delete, $"{Api}/assets/{other.GetProperty("id").GetGuid()}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var page = await Json(await Get(b, $"{Api}/assets?status=Active"));
        page.GetProperty("totalCount").GetInt32().ShouldBe(1);
        (await RoleBalanceAsync(b, PostingRoles.FixedAssetCost)).ShouldBe(4_800m);
        (await (await Send(b, HttpMethod.Post, $"{Api}/categories", new
        {
            code = "VEH", nameEn = "Dup", nameAr = "Dup", defaultMethod = "StraightLine", usefulLifeMonths = 12, salvagePercent = 0m,
        })).ErrorCodeAsync()).ShouldBe("assets.category-code-taken");
    }

    [Fact]
    public async Task Purchase_invoice_link_is_validated_against_the_posted_invoice()
    {
        var b = await NewBooksAsync();
        (await Send(b, HttpMethod.Put, "/api/v1/configuration/modules/procurement", new { enabled = true })).IsSuccessStatusCode.ShouldBeTrue();
        var supplier = (await Json(await Send(b, HttpMethod.Post, "/api/v1/parties/suppliers", new { code = "S1", nameEn = "Fleet Motors", nameAr = "فليت", currency = "EGP" })))
            .GetProperty("id").GetGuid();
        var draftInvoice = await Json(await Send(b, HttpMethod.Post, "/api/v1/procurement/invoices", new
        {
            legalEntityId = b.LegalEntityId, supplierId = supplier, supplierInvoiceNumber = "FM-1", invoiceDate = "2026-01-10",
            lines = new[] { new { kind = "Expense", accountId = b.Accounts["1510"], description = "Two vans", quantity = 2m, unitPrice = 50_000m } },
        }));
        var invoiceId = draftInvoice.GetProperty("id").GetGuid();
        var lineId = draftInvoice.GetProperty("lines")[0].GetProperty("lineId").GetGuid();

        async Task<Guid> DraftAsync(decimal cost) => (await Json(await Send(b, HttpMethod.Post, $"{Api}/assets", new
        {
            nameEn = "Van", nameAr = "فان", categoryId = b.CategoryId, legalEntityId = b.LegalEntityId,
            acquisitionDate = "2026-01-10", inServiceDate = "2026-01-10", acquisitionCost = cost,
        }))).GetProperty("id").GetGuid();
        Task<HttpResponseMessage> Activate(Guid asset) => Send(b, HttpMethod.Post, $"{Api}/assets/{asset}/activate",
            new { mode = "PurchaseInvoice", sourceDocType = "PurchaseInvoice", sourceDocId = invoiceId, sourceLineId = lineId }, idempotent: true);

        var first = await DraftAsync(50_000m);
        (await (await Activate(first)).ErrorCodeAsync()).ShouldBe("assets.source-invoice-not-posted");

        await Json(await Send(b, HttpMethod.Post, $"/api/v1/procurement/invoices/{invoiceId}/submit"));
        await Json(await Send(b, HttpMethod.Post, $"/api/v1/procurement/invoices/{invoiceId}/post", idempotent: true));

        var linked = await Json(await Activate(first));
        linked.GetProperty("acquisitionMode").GetString().ShouldBe("PurchaseInvoice");
        linked.GetProperty("acquisitionJournalId").ValueKind.ShouldBe(JsonValueKind.Null);
        linked.GetProperty("sourceLineId").GetGuid().ShouldBe(lineId);

        // The second van fits the rest of the line; a third asset would exceed the invoice.
        var second = await DraftAsync(50_000m);
        await Json(await Activate(second));
        var third = await DraftAsync(1_000m);
        (await (await Activate(third)).ErrorCodeAsync()).ShouldBe("assets.source-amount-mismatch");

        var badLine = await Send(b, HttpMethod.Post, $"{Api}/assets/{third}/activate",
            new { mode = "PurchaseInvoice", sourceDocType = "PurchaseInvoice", sourceDocId = invoiceId, sourceLineId = Guid.NewGuid() }, idempotent: true);
        (await badLine.ErrorCodeAsync()).ShouldBe("assets.source-line-not-found");

        // The invoice already debited the asset account: no second acquisition journal.
        (await RoleBalanceAsync(b, PostingRoles.FixedAssetCost)).ShouldBe(100_000m);
    }

    [Fact]
    public async Task IAS16_revaluation_uses_the_reserve_and_disposal_moves_it_to_retained_earnings()
    {
        var b = await NewBooksAsync();
        var id = (await NewActiveAssetAsync(b, 12_000m, "2026-01-05", life: 12)).GetProperty("id").GetGuid();
        await Json(await PostRun(b, "2026-03-31")); // NBV 9,000

        // Up to 11,000: +2,000 to the reserve.
        var up = await Json(await Send(b, HttpMethod.Post, $"{Api}/assets/{id}/revalue", new { date = "2026-03-31", newNetBookValue = 11_000m }, idempotent: true));
        up.GetProperty("revaluationReserve").GetDecimal().ShouldBe(2_000m);
        (await RoleBalanceAsync(b, PostingRoles.AssetRevaluationReserve)).ShouldBe(-2_000m);

        // Down to 8,000: 3,000 = 2,000 from the reserve + 1,000 expense.
        var down = await Json(await Send(b, HttpMethod.Post, $"{Api}/assets/{id}/revalue", new { date = "2026-03-31", newNetBookValue = 8_000m }, idempotent: true));
        down.GetProperty("revaluationReserve").GetDecimal().ShouldBe(0m);
        down.GetProperty("revaluationLossInProfit").GetDecimal().ShouldBe(1_000m);
        (await RoleBalanceAsync(b, PostingRoles.AssetRevaluationReserve)).ShouldBe(0m);
        (await RoleBalanceAsync(b, PostingRoles.ImpairmentLoss)).ShouldBe(1_000m);

        // Up to 10,000: the first 1,000 reverses the earlier loss, 1,000 goes to the reserve.
        var again = await Json(await Send(b, HttpMethod.Post, $"{Api}/assets/{id}/revalue", new { date = "2026-03-31", newNetBookValue = 10_000m }, idempotent: true));
        again.GetProperty("revaluationReserve").GetDecimal().ShouldBe(1_000m);
        again.GetProperty("revaluationLossInProfit").GetDecimal().ShouldBe(0m);
        (await RoleBalanceAsync(b, PostingRoles.ImpairmentLoss)).ShouldBe(0m);
        (await RoleBalanceAsync(b, PostingRoles.AssetRevaluationReserve)).ShouldBe(-1_000m);

        // Disposal transfers the remaining reserve to retained earnings, outside profit or loss.
        var sold = await Json(await Send(b, HttpMethod.Post, $"{Api}/assets/{id}/dispose",
            new { date = "2026-04-02", proceeds = 10_000m, proceedsAccountId = b.Accounts["1120"] }, idempotent: true));
        sold.GetProperty("disposalGainLoss").GetDecimal().ShouldBe(0m);
        (await RoleBalanceAsync(b, PostingRoles.AssetRevaluationReserve)).ShouldBe(0m);
        (await RoleBalanceAsync(b, PostingRoles.RetainedEarnings)).ShouldBe(-1_000m);
        var history = await Json(await Get(b, $"{Api}/assets/{id}/history"));
        history.EnumerateArray().Last().GetProperty("reserveDelta").GetDecimal().ShouldBe(-1_000m);
    }

    [Fact]
    public async Task Impairment_writes_down_and_reschedules_and_a_later_revaluation_reverses_it_first()
    {
        var b = await NewBooksAsync();
        var id = (await NewActiveAssetAsync(b, 12_000m, "2026-01-05", life: 12)).GetProperty("id").GetGuid();
        await Json(await PostRun(b, "2026-03-31")); // NBV 9,000, 9 months left

        (await (await Send(b, HttpMethod.Post, $"{Api}/assets/{id}/impair", new { date = "2026-03-31", recoverableAmount = 9_500m }, idempotent: true)).ErrorCodeAsync())
            .ShouldBe("assets.impairment-invalid");
        var impaired = await Json(await Send(b, HttpMethod.Post, $"{Api}/assets/{id}/impair",
            new { date = "2026-03-31", recoverableAmount = 4_500m, reason = "Damaged" }, idempotent: true));
        impaired.GetProperty("netBookValue").GetDecimal().ShouldBe(4_500m);
        (await RoleBalanceAsync(b, PostingRoles.ImpairmentLoss)).ShouldBe(4_500m);
        var schedule = await Json(await Get(b, $"{Api}/assets/{id}/schedule"));
        schedule.EnumerateArray().Where(l => !l.GetProperty("posted").GetBoolean()).Select(l => l.GetProperty("amount").GetDecimal()).ShouldAllBe(a => a == 500m);

        var back = await Json(await Send(b, HttpMethod.Post, $"{Api}/assets/{id}/revalue", new { date = "2026-03-31", newNetBookValue = 6_000m }, idempotent: true));
        back.GetProperty("revaluationReserve").GetDecimal().ShouldBe(0m);
        (await RoleBalanceAsync(b, PostingRoles.ImpairmentLoss)).ShouldBe(3_000m);

        var register = await Json(await Get(b, $"{Api}/reports/register?legalEntityId={b.LegalEntityId}&asOf=2026-12-31"));
        register.GetProperty("totalNetBookValue").GetDecimal().ShouldBe(6_000m);
        register.GetProperty("totalCost").GetDecimal().ShouldBe(await RoleBalanceAsync(b, PostingRoles.FixedAssetCost));
    }

    [Fact]
    public async Task Partial_disposal_by_quantity_and_percent_shrinks_the_asset_proportionally()
    {
        var b = await NewBooksAsync();
        var draft = await Json(await Send(b, HttpMethod.Post, $"{Api}/assets", new
        {
            nameEn = "Chairs", nameAr = "كراسي", categoryId = b.CategoryId, legalEntityId = b.LegalEntityId,
            acquisitionDate = "2026-01-05", inServiceDate = "2026-01-05", acquisitionCost = 12_000m, usefulLifeMonths = 12, quantity = 10m,
        }));
        var id = draft.GetProperty("id").GetGuid();
        await Json(await Send(b, HttpMethod.Post, $"{Api}/assets/{id}/activate", new { mode = "ManualJournal", clearingAccountId = b.Accounts["1120"] }, idempotent: true));
        await Json(await PostRun(b, "2026-03-31")); // cost 12,000, accumulated 3,000

        (await (await Send(b, HttpMethod.Post, $"{Api}/assets/{id}/dispose-partial",
            new { date = "2026-04-10", quantity = 2m, percent = 20m, proceeds = 0m }, idempotent: true)).ErrorCodeAsync()).ShouldBe("assets.partial-quantity-or-percent");

        // 4 of 10 chairs for 4,000: cost 4,800, accumulated 1,200 → NBV 3,600, gain 400.
        var part = await Json(await Send(b, HttpMethod.Post, $"{Api}/assets/{id}/dispose-partial",
            new { date = "2026-04-10", quantity = 4m, proceeds = 4_000m, proceedsAccountId = b.Accounts["1120"] }, idempotent: true));
        part.GetProperty("status").GetString().ShouldBe("Active");
        part.GetProperty("quantity").GetDecimal().ShouldBe(6m);
        part.GetProperty("cost").GetDecimal().ShouldBe(7_200m);
        part.GetProperty("accumulatedDepreciation").GetDecimal().ShouldBe(1_800m);
        (await RoleBalanceAsync(b, PostingRoles.AssetDisposalGainLoss)).ShouldBe(-400m);

        // The remaining 5,400 is spread over the remaining 9 months (600 a month).
        var schedule = await Json(await Get(b, $"{Api}/assets/{id}/schedule"));
        schedule.EnumerateArray().Where(l => !l.GetProperty("posted").GetBoolean()).Select(l => l.GetProperty("amount").GetDecimal()).ShouldAllBe(a => a == 600m);

        // The run that was partly disposed can no longer be reversed.
        var runs = await Json(await Get(b, $"{Api}/depreciation-runs"));
        (await (await Send(b, HttpMethod.Post, $"{Api}/depreciation-runs/{runs[0].GetProperty("id").GetGuid()}/reverse", new { }, idempotent: true)).ErrorCodeAsync())
            .ShouldBe("assets.partially-disposed");

        await Json(await PostRun(b, "2026-04-30"));
        var half = await Json(await Send(b, HttpMethod.Post, $"{Api}/assets/{id}/dispose-partial",
            new { date = "2026-05-03", percent = 50m, proceeds = 0m }, idempotent: true));
        half.GetProperty("quantity").GetDecimal().ShouldBe(3m);
        half.GetProperty("cost").GetDecimal().ShouldBe(3_600m);

        var register = await Json(await Get(b, $"{Api}/reports/register?legalEntityId={b.LegalEntityId}&asOf=2026-12-31"));
        register.GetProperty("totalCost").GetDecimal().ShouldBe(await RoleBalanceAsync(b, PostingRoles.FixedAssetCost));
        register.GetProperty("totalAccumulatedDepreciation").GetDecimal().ShouldBe(-await RoleBalanceAsync(b, PostingRoles.AccumulatedDepreciation));
        var april = await Json(await Get(b, $"{Api}/reports/register?legalEntityId={b.LegalEntityId}&asOf=2026-04-09"));
        april.GetProperty("totalCost").GetDecimal().ShouldBe(12_000m);
        april.GetProperty("totalAccumulatedDepreciation").GetDecimal().ShouldBe(3_000m);
    }

    [Fact]
    public async Task A_tax_book_depreciates_on_its_own_terms_and_reports_the_difference()
    {
        var b = await NewBooksAsync();
        var id = (await NewActiveAssetAsync(b, 12_000m, "2026-01-05", life: 12)).GetProperty("id").GetGuid();
        var book = await Json(await Send(b, HttpMethod.Post, $"{Api}/books", new { code = "tax", nameEn = "Tax book", nameAr = "الدفتر الضريبي" }));
        book.GetProperty("code").GetString().ShouldBe("TAX");
        var bookId = book.GetProperty("id").GetGuid();
        (await (await Send(b, HttpMethod.Post, $"{Api}/books", new { code = "TAX", nameEn = "Dup", nameAr = "Dup" })).ErrorCodeAsync()).ShouldBe("assets.book-code-taken");

        // Tax: 6 months straight line (2,000 a month); the posting book stays at 12 months (1,000 a month).
        var books = await Json(await Send(b, HttpMethod.Put, $"{Api}/assets/{id}/books/{bookId}", new { method = "StraightLine", usefulLifeMonths = 6 }));
        var tax = books.EnumerateArray().Single();
        tax.GetProperty("schedule").GetArrayLength().ShouldBe(6);
        tax.GetProperty("schedule")[0].GetProperty("amount").GetDecimal().ShouldBe(2_000m);
        (await (await Send(b, HttpMethod.Put, $"{Api}/assets/{id}/books/{bookId}", new { method = "UnitsOfProduction", usefulLifeMonths = 6 })).ErrorCodeAsync())
            .ShouldBe("validation-failed");

        await Json(await PostRun(b, "2026-03-31"));
        var register = await Json(await Get(b, $"{Api}/reports/book-register?legalEntityId={b.LegalEntityId}&bookId={bookId}&asOf=2026-03-31"));
        var line = register.GetProperty("assets").EnumerateArray().Single();
        line.GetProperty("bookAccumulated").GetDecimal().ShouldBe(6_000m);
        line.GetProperty("bookNetBookValue").GetDecimal().ShouldBe(6_000m);
        line.GetProperty("postingNetBookValue").GetDecimal().ShouldBe(9_000m);
        line.GetProperty("difference").GetDecimal().ShouldBe(-3_000m);

        // Only the posting book posts: the ledger shows 3,000 of depreciation.
        (await RoleBalanceAsync(b, PostingRoles.DepreciationExpense)).ShouldBe(3_000m);

        // A partial disposal shrinks the tax book too: half of cost 12,000 and of 6,000 accumulated before April.
        await Json(await Send(b, HttpMethod.Post, $"{Api}/assets/{id}/dispose-partial", new { date = "2026-04-01", percent = 50m, proceeds = 0m }, idempotent: true));
        var after = await Json(await Get(b, $"{Api}/reports/book-register?legalEntityId={b.LegalEntityId}&bookId={bookId}&asOf=2026-04-01"));
        var afterLine = after.GetProperty("assets").EnumerateArray().Single();
        afterLine.GetProperty("bookCost").GetDecimal().ShouldBe(6_000m);
        afterLine.GetProperty("bookAccumulated").GetDecimal().ShouldBe(4_000m); // 3,000 before April + 1,000 for April on the remaining half
        var end = await Json(await Get(b, $"{Api}/reports/book-register?legalEntityId={b.LegalEntityId}&bookId={bookId}&asOf=2026-12-31"));
        end.GetProperty("assets").EnumerateArray().Single().GetProperty("bookNetBookValue").GetDecimal().ShouldBe(0m);
    }

    [Fact]
    public async Task Transfer_to_another_legal_entity_disposes_at_book_value_and_carries_the_asset_over()
    {
        var b = await NewBooksAsync();
        var currency = (await Json(await Get(b, "/api/v1/organization/legal-entities"))).EnumerateArray().First().GetProperty("functionalCurrency").GetString();
        var second = (await Json(await Send(b, HttpMethod.Post, "/api/v1/organization/legal-entities", new { name = "Second LE", functionalCurrency = currency })))
            .GetProperty("id").GetGuid();
        (await Send(b, HttpMethod.Post, "/api/v1/accounting/fiscal-years", new { legalEntityId = second, startDate = "2026-01-01" })).StatusCode.ShouldBe(HttpStatusCode.Created);

        var source = (await NewActiveAssetAsync(b, 12_000m, "2026-01-05", life: 12)).GetProperty("id").GetGuid();
        await Json(await PostRun(b, "2026-03-31")); // NBV 9,000, 9 months left

        var moved = await Json(await Send(b, HttpMethod.Post, $"{Api}/assets/{source}/transfer-legal-entity",
            new { date = "2026-04-01", targetLegalEntityId = second, clearingAccountId = b.Accounts["1130"] }, idempotent: true));
        moved.GetProperty("source").GetProperty("status").GetString().ShouldBe("Disposed");
        moved.GetProperty("source").GetProperty("disposalGainLoss").GetDecimal().ShouldBe(0m);
        var target = moved.GetProperty("target");
        target.GetProperty("legalEntityId").GetGuid().ShouldBe(second);
        target.GetProperty("cost").GetDecimal().ShouldBe(12_000m);
        target.GetProperty("accumulatedDepreciation").GetDecimal().ShouldBe(3_000m);
        target.GetProperty("acquisitionMode").GetString().ShouldBe("Transfer");
        target.GetProperty("number").GetString()!.ShouldStartWith("FA");
        var targetId = target.GetProperty("id").GetGuid();
        var schedule = await Json(await Get(b, $"{Api}/assets/{targetId}/schedule"));
        schedule.GetArrayLength().ShouldBe(9);
        schedule[0].GetProperty("period").GetString().ShouldBe("2026-04-01");
        schedule.EnumerateArray().ShouldAllBe(l => l.GetProperty("amount").GetDecimal() == 1_000m);

        // Each legal entity's ledger balances through the clearing account; the sender has no asset left.
        (await RoleBalanceAsync(b, PostingRoles.FixedAssetCost)).ShouldBe(0m);
        var targetBooks = b with { LegalEntityId = second };
        (await RoleBalanceAsync(targetBooks, PostingRoles.FixedAssetCost)).ShouldBe(12_000m);
        (await RoleBalanceAsync(targetBooks, PostingRoles.AccumulatedDepreciation)).ShouldBe(-3_000m);

        var registerTarget = await Json(await Get(b, $"{Api}/reports/register?legalEntityId={second}&asOf=2026-12-31"));
        registerTarget.GetProperty("totalAccumulatedDepreciation").GetDecimal().ShouldBe(3_000m);
        (await Json(await Get(b, $"{Api}/reports/register?legalEntityId={b.LegalEntityId}&asOf=2026-12-31"))).GetProperty("assets").GetArrayLength().ShouldBe(0);

        var april = await Json(await Send(b, HttpMethod.Post, $"{Api}/depreciation-runs", new { legalEntityId = second, period = "2026-04-30" }, idempotent: true));
        april.GetProperty("total").GetDecimal().ShouldBe(1_000m);
        (await (await Send(b, HttpMethod.Post, $"{Api}/assets/{source}/transfer-legal-entity",
            new { date = "2026-04-01", targetLegalEntityId = second, clearingAccountId = b.Accounts["1130"] }, idempotent: true)).ErrorCodeAsync()).ShouldBe("assets.disposed");
    }

    [Fact]
    public async Task Scheduled_depreciation_posts_last_month_once_per_legal_entity()
    {
        var b = await NewBooksAsync();
        await NewActiveAssetAsync(b, 12_000m, "2026-01-05", life: 12);
        var off = await Json(await Send(b, HttpMethod.Post, $"{Api}/depreciation-runs/scheduled/run", new { today = "2026-02-03" }));
        off.GetProperty("skipped").GetString()!.ShouldContain("off");

        var settings = await Json(await Send(b, HttpMethod.Put, $"{Api}/settings", new { autoDepreciationEnabled = true, runOnDay = 3 }));
        settings.GetProperty("runOnDay").GetInt32().ShouldBe(3);
        (await (await Send(b, HttpMethod.Put, $"{Api}/settings", new { autoDepreciationEnabled = true, runOnDay = 31 })).ErrorCodeAsync()).ShouldBe("assets.run-day-invalid");

        var early = await Json(await Send(b, HttpMethod.Post, $"{Api}/depreciation-runs/scheduled/run", new { today = "2026-02-02" }));
        early.GetProperty("skipped").GetString()!.ShouldContain("day 3");

        // The worker path: no signed-in user, only tenant and company context.
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
            tenant.SetTenant(b.T.TenantId);
            var db = scope.ServiceProvider.GetRequiredService<Erp.Modules.FixedAssets.Application.IFixedAssetsDbContext>();
            (await Erp.Modules.FixedAssets.Application.ScheduledDepreciationRunner.CompaniesWithAutoDepreciationAsync(db, b.T.TenantId, CancellationToken.None))
                .ShouldBe([b.T.CompanyId]);
            tenant.SetCompany(b.T.CompanyId);
            var result = await scope.ServiceProvider.GetRequiredService<Erp.Modules.FixedAssets.Application.ScheduledDepreciationRunner>()
                .RunAsync(new DateOnly(2026, 2, 3), CancellationToken.None);
            result.Period.ShouldBe(new DateOnly(2026, 1, 1));
            result.LegalEntities.Single().Outcome.ShouldBe("Posted");
            result.LegalEntities.Single().Total.ShouldBe(1_000m);
        }

        var again = await Json(await Send(b, HttpMethod.Post, $"{Api}/depreciation-runs/scheduled/run", new { today = "2026-02-20" }));
        again.GetProperty("legalEntities")[0].GetProperty("outcome").GetString().ShouldBe("AlreadyPosted");
        (await RoleBalanceAsync(b, PostingRoles.DepreciationExpense)).ShouldBe(1_000m);
        (await Json(await Get(b, $"{Api}/settings"))).GetProperty("lastRunSummary").GetString()!.ShouldContain("AlreadyPosted");
    }

    [Fact]
    public async Task Tenants_are_isolated_and_the_module_switch_is_enforced()
    {
        var b = await NewBooksAsync();
        var id = (await NewActiveAssetAsync(b, 6_000m, life: 12)).GetProperty("id").GetGuid();

        var other = await NewBooksAsync();
        (await Get(other, $"{Api}/assets/{id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Json(await Get(other, $"{Api}/assets"))).GetProperty("totalCount").GetInt32().ShouldBe(0);
        (await Json(await Get(other, $"{Api}/categories"))).GetArrayLength().ShouldBe(1);
        (await (await Send(other, HttpMethod.Post, $"{Api}/depreciation-runs", new { legalEntityId = b.LegalEntityId, period = "2026-01-31" }, idempotent: true)).ErrorCodeAsync())
            .ShouldBe("assets.legal-entity-not-found");

        (await Send(b, HttpMethod.Put, "/api/v1/configuration/modules/fixed-assets", new { enabled = false })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Get(b, $"{Api}/assets/{id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
