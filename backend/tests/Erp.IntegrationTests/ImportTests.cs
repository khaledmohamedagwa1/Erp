using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using Shouldly;
using Xunit;

namespace Erp.IntegrationTests;

public class ImportTests(ErpApiFixture fixture)
{
    private sealed record Books(ErpApiFixture.TenantSession T, Guid LegalEntityId);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Books> NewBooksAsync()
    {
        var t = await fixture.NewTenantAsync();
        (await t.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/accounting/coa-templates/generic", t.CompanyId)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var le = (await (await t.Client.GetAsCompanyAsync("/api/v1/organization/legal-entities", t.CompanyId)).Content.ReadFromJsonAsync<JsonElement>(Ct))
            .EnumerateArray().First().GetProperty("id").GetGuid();
        (await t.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/accounting/fiscal-years", t.CompanyId, new { legalEntityId = le, startDate = "2026-01-01" }))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        return new Books(t, le);
    }

    private static async Task<HttpResponseMessage> UploadAsync(Books b, string type, string fileName, byte[] content, bool legalEntity = false, string? openingDate = null)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(fileName.EndsWith(".xlsx", StringComparison.Ordinal)
            ? "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" : "text/csv");
        form.Add(file, "file", fileName);
        form.Add(new StringContent(type), "type");
        if (legalEntity)
        {
            form.Add(new StringContent(b.LegalEntityId.ToString()), "legalEntityId");
        }

        if (openingDate is not null)
        {
            form.Add(new StringContent(openingDate), "openingDate");
        }

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/import/jobs") { Content = form }.WithCompany(b.T.CompanyId);
        return await b.T.Client.SendAsync(request, Ct);
    }

    private static async Task<Guid> UploadCsvAsync(Books b, string type, string csv, bool legalEntity = false, string? openingDate = null)
    {
        var response = await UploadAsync(b, type, $"{type}.csv", Encoding.UTF8.GetBytes(csv), legalEntity, openingDate);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> ValidateAsync(Books b, Guid job)
    {
        var response = await b.T.Client.SendAsCompanyAsync(HttpMethod.Post, $"/api/v1/import/jobs/{job}/validate", b.T.CompanyId);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static Task<HttpResponseMessage> CommitAsync(Books b, Guid job, string? key = null) =>
        b.T.Client.SendAsCompanyAsync(HttpMethod.Post, $"/api/v1/import/jobs/{job}/commit", b.T.CompanyId, idempotencyKey: key ?? Guid.NewGuid().ToString());

    private static async Task<JsonElement> CommitOkAsync(Books b, Guid job)
    {
        var response = await CommitAsync(b, job);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("status").GetString().ShouldBe("Committed");
        return body;
    }

    private static async Task<JsonElement> ValidatedAsync(Books b, Guid job)
    {
        var result = await ValidateAsync(b, job);
        result.GetProperty("status").GetString().ShouldBe("Validated", result.GetProperty("errors").ToString());
        return result;
    }

    private static IEnumerable<(int Row, string Code)> Errors(JsonElement job) =>
        job.GetProperty("errors").EnumerateArray().Select(e => (e.GetProperty("row").GetInt32(), e.GetProperty("code").GetString()!));

    private static async Task<JsonElement> GetAsync(Books b, string url)
    {
        var response = await b.T.Client.GetAsCompanyAsync(url, b.T.CompanyId);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static async Task<decimal> ClosingAsync(Books b, string accountCode)
    {
        var tb = await GetAsync(b, $"/api/v1/accounting/reports/trial-balance?legalEntityId={b.LegalEntityId}&from=2026-01-01&to=2026-12-31");
        tb.GetProperty("isBalanced").GetBoolean().ShouldBeTrue();
        return tb.GetProperty("lines").EnumerateArray().Where(l => l.GetProperty("code").GetString() == accountCode)
            .Sum(l => l.GetProperty("closingBalance").GetDecimal());
    }

    [Fact]
    public async Task Customers_dry_run_reports_row_errors_without_writing_then_commit_creates_them_once()
    {
        var b = await NewBooksAsync();

        // Arabic header for the Arabic name, a duplicate code (row 3), a missing English name (row 4) and a bad number (row 5).
        var bad = await UploadCsvAsync(b, "customers",
            "Code,Name (English),الاسم بالعربية,Credit limit\n" +
            "C-100,Delta Retail,دلتا,5000\n" +
            "C-100,Delta Again,دلتا ٢,\n" +
            "C-101,,بدون اسم,\n" +
            "C-102,Gamma,جاما,abc\n");
        var failed = await ValidateAsync(b, bad);
        failed.GetProperty("status").GetString().ShouldBe("Failed");
        failed.GetProperty("totalRows").GetInt32().ShouldBe(4);
        Errors(failed).Select(e => e.Row).Distinct().Order().ShouldBe([3, 4, 5]);
        Errors(failed).ShouldContain((3, "parties.customer-code-taken"));
        Errors(failed).ShouldContain((4, "import.value-required"));
        Errors(failed).ShouldContain((5, "import.invalid-number"));

        // The dry run wrote nothing.
        (await GetAsync(b, "/api/v1/parties/customers?search=C-100")).GetProperty("totalCount").GetInt32().ShouldBe(0);
        (await CommitAsync(b, bad)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var good = await UploadCsvAsync(b, "customers",
            "code;name_en;name_ar;kind;credit limit;credit action\n" +
            "C-100;Delta Retail;دلتا;Business;5000;Block\n" +
            "C-101;\"Omega, Ltd\";أوميجا;Individual;;\n");
        await ValidatedAsync(b, good);
        (await GetAsync(b, "/api/v1/parties/customers?search=C-10")).GetProperty("totalCount").GetInt32().ShouldBe(0);

        var key = Guid.NewGuid().ToString();
        var first = await CommitAsync(b, good, key);
        first.StatusCode.ShouldBe(HttpStatusCode.OK, await first.Content.ReadAsStringAsync(Ct));
        (await first.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("committedRows").GetInt32().ShouldBe(2);

        var customers = await GetAsync(b, "/api/v1/parties/customers?search=C-10");
        customers.GetProperty("totalCount").GetInt32().ShouldBe(2);
        var delta = customers.GetProperty("items").EnumerateArray().Single(c => c.GetProperty("code").GetString() == "C-100");
        delta.GetProperty("currency").GetString().ShouldBe("EGP"); // company base currency by default
        var detail = await GetAsync(b, $"/api/v1/parties/customers/{delta.GetProperty("id").GetGuid()}");
        detail.GetProperty("credit").GetProperty("creditLimit").GetDecimal().ShouldBe(5000m);
        detail.GetProperty("credit").GetProperty("creditAction").GetString().ShouldBe("Block");
        customers.GetProperty("items").EnumerateArray().Single(c => c.GetProperty("code").GetString() == "C-101").GetProperty("nameEn").GetString().ShouldBe("Omega, Ltd");

        // Same key replays; a new key is refused; re-validation is refused.
        (await CommitAsync(b, good, key)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var again = await CommitAsync(b, good);
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await again.ErrorCodeAsync()).ShouldBe("import.already-committed");
        (await b.T.Client.SendAsCompanyAsync(HttpMethod.Post, $"/api/v1/import/jobs/{good}/validate", b.T.CompanyId)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await GetAsync(b, "/api/v1/parties/customers?search=C-10")).GetProperty("totalCount").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task Chart_of_accounts_from_xlsx_then_gl_opening_balances_post_one_balanced_journal()
    {
        var b = await NewBooksAsync();
        byte[] chart;
        using (var workbook = new XLWorkbook())
        {
            var sheet = workbook.Worksheets.Add("Data");
            string[] headers = ["رمز الحساب", "Name (English)", "Name (Arabic)", "Parent code", "Posting"];
            for (var i = 0; i < headers.Length; i++)
            {
                sheet.Cell(1, i + 1).Value = headers[i];
            }

            object[][] rows = [["1115", "Petty cash – branch", "عهدة نقدية – فرع", "11", "yes"], ["1116", "Petty cash – HQ", "عهدة نقدية – الإدارة", "11", "yes"]];
            for (var r = 0; r < rows.Length; r++)
            {
                for (var c = 0; c < rows[r].Length; c++)
                {
                    sheet.Cell(r + 2, c + 1).Value = rows[r][c].ToString();
                }
            }

            sheet.Cell(2, 1).Value = 1115; // a numeric cell must still read as the code "1115"
            using var stream = new MemoryStream();
            workbook.SaveAs(stream);
            chart = stream.ToArray();
        }

        var upload = await UploadAsync(b, "ChartOfAccounts", "chart.xlsx", chart);
        upload.StatusCode.ShouldBe(HttpStatusCode.Created, await upload.Content.ReadAsStringAsync(Ct));
        var chartJob = (await upload.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        await ValidatedAsync(b, chartJob);
        await CommitOkAsync(b, chartJob);
        var accounts = await GetAsync(b, "/api/v1/accounting/accounts");
        accounts.EnumerateArray().Select(a => a.GetProperty("code").GetString()).ShouldContain("1116");

        // Unbalanced file: one file-level error, nothing executed.
        var unbalanced = await UploadCsvAsync(b, "gl-opening-balances", "Account code,Debit,Credit\n1115,100,\n3100,,90\n", legalEntity: true, openingDate: "2026-01-01");
        var u = await ValidateAsync(b, unbalanced);
        u.GetProperty("status").GetString().ShouldBe("Failed");
        Errors(u).ShouldBe([(0, "import.unbalanced")]);

        // A control account (receivables) and an unknown account are pinned to their rows.
        var control = await UploadCsvAsync(b, "GlOpeningBalances", "Account code,Debit,Credit\n1115,100,\n1210,50,\n9999,10,\n3100,,160\n", legalEntity: true, openingDate: "2026-01-01");
        var c1 = await ValidateAsync(b, control);
        Errors(c1).Select(e => e.Row).Order().ShouldBe([3, 4]);

        var good = await UploadCsvAsync(b, "GlOpeningBalances",
            "Account code,Debit,Credit,Description\n1115,\"2,500.00\",,Branch float\n1116,1500,,\n3100,,4000,Share capital\n", legalEntity: true, openingDate: "2026-01-01");
        await ValidatedAsync(b, good);
        (await ClosingAsync(b, "1115")).ShouldBe(0m); // dry run posted nothing
        await CommitOkAsync(b, good);
        (await ClosingAsync(b, "1115")).ShouldBe(2500m);
        (await ClosingAsync(b, "1116")).ShouldBe(1500m);
        (await ClosingAsync(b, "3100")).ShouldBe(-4000m);
    }

    [Fact]
    public async Task Open_receivables_and_payables_keep_due_dates_for_aging_and_mirror_the_control_accounts()
    {
        var b = await NewBooksAsync();
        var customers = await UploadCsvAsync(b, "customers", "Code,Name (English),Name (Arabic)\nC1,Delta,دلتا\nC2,Omega,أوميجا\n");
        await ValidatedAsync(b, customers);
        await CommitOkAsync(b, customers);
        var suppliers = await UploadCsvAsync(b, "suppliers", "Code,Name (English),Name (Arabic)\nS1,Nile,النيل\n");
        await ValidatedAsync(b, suppliers);
        await CommitOkAsync(b, suppliers);

        // Unknown customer and a duplicate document are reported per row.
        var bad = await UploadCsvAsync(b, "open-receivables",
            "Customer code,Document number,Document date,Due date,Open amount\nC9,X-1,2026-01-10,,100\nC1,INV-1,2026-01-10,,100\nC1,INV-1,2026-01-11,,100\n", legalEntity: true);
        var failed = await ValidateAsync(b, bad);
        Errors(failed).ShouldContain((2, "import.party-not-found"));
        Errors(failed).ShouldContain((4, "import.duplicate-document"));

        var ar = await UploadCsvAsync(b, "OpenReceivables",
            "رمز العميل,رقم المستند,تاريخ المستند,تاريخ الاستحقاق,الرصيد المفتوح,البيان\n" +
            "C1,INV-1,2026-01-10,2026-02-09,1000,Old invoice\n" +
            "C1,INV-2,2026-05-20,2026-06-20,500,\n" +
            "C2,CN-7,2026-06-30,,-200,Open credit note\n", legalEntity: true);
        await ValidatedAsync(b, ar);
        (await ClosingAsync(b, "1210")).ShouldBe(0m);
        (await CommitOkAsync(b, ar)).GetProperty("committedRows").GetInt32().ShouldBe(3);

        var aging = await GetAsync(b, $"/api/v1/finance/reports/ar-aging?legalEntityId={b.LegalEntityId}&asOf=2026-06-30");
        aging.GetProperty("totals").GetProperty("total").GetDecimal().ShouldBe(1300m);
        aging.GetProperty("totals").GetProperty("over90").GetDecimal().ShouldBe(1000m);   // due 9 Feb
        aging.GetProperty("totals").GetProperty("days1To30").GetDecimal().ShouldBe(500m);  // due 20 Jun
        (await ClosingAsync(b, "1210")).ShouldBe(1300m);

        var ap = await UploadCsvAsync(b, "OpenPayables", "Supplier code,Bill number,Bill date,Due date,Amount\nS1,B-77,2026-03-01,2026-07-15,750\n", legalEntity: true);
        await ValidatedAsync(b, ap);
        await CommitOkAsync(b, ap);
        var apAging = await GetAsync(b, $"/api/v1/finance/reports/ap-aging?legalEntityId={b.LegalEntityId}&asOf=2026-06-30");
        apAging.GetProperty("totals").GetProperty("current").GetDecimal().ShouldBe(750m);
        (await ClosingAsync(b, "2110")).ShouldBe(-750m);
        (await ClosingAsync(b, "3300")).ShouldBe(-1300m + 750m);

        // Importing the same documents again is refused by Finance (per party and document number).
        var dup = await UploadCsvAsync(b, "OpenPayables", "Supplier code,Document number,Document date,Amount\nS1,B-77,2026-03-01,750\n", legalEntity: true);
        Errors(await ValidateAsync(b, dup)).ShouldBe([(2, "finance.opening-item-exists")]);
    }

    [Fact]
    public async Task Products_with_prices_then_opening_stock_per_warehouse()
    {
        var b = await NewBooksAsync();
        Task<HttpResponseMessage> Post(string url, object? body = null) => b.T.Client.SendAsCompanyAsync(HttpMethod.Post, url, b.T.CompanyId, body);
        (await Post("/api/v1/catalog/uoms/seed-standard")).EnsureSuccessStatusCode();
        (await Post("/api/v1/catalog/categories", new { code = "GOODS", nameEn = "Goods", nameAr = "بضائع", defaultCostingMethod = "WeightedAverage", postingGroup = "GOODS" }))
            .EnsureSuccessStatusCode();
        (await Post("/api/v1/catalog/price-lists", new { code = "RETAIL", name = "Retail", currency = "EGP", taxInclusive = false })).EnsureSuccessStatusCode();

        var products = await UploadCsvAsync(b, "products",
            "SKU,Name (English),Name (Arabic),Category code,Base unit,Barcode,Price list code,Price\n" +
            "SHELF-01,Shelf,رف,GOODS,PCS,6221234567890,RETAIL,250\n" +
            "BOLT-02,Bolt,مسمار,GOODS,PCS,,,\n" +
            "BAD-03,Bad,سيء,NOPE,PCS,,,\n");
        var failed = await ValidateAsync(b, products);
        Errors(failed).ShouldBe([(4, "catalog.category-not-found")]);

        var fixedFile = await UploadCsvAsync(b, "products",
            "SKU,Name (English),Name (Arabic),Category code,Base unit,Barcode,Price list code,Price\n" +
            "SHELF-01,Shelf,رف,GOODS,PCS,6221234567890,RETAIL,250\n" +
            "BOLT-02,Bolt,مسمار,GOODS,PCS,,,\n");
        await ValidatedAsync(b, fixedFile);
        await CommitOkAsync(b, fixedFile);
        var list = await GetAsync(b, "/api/v1/catalog/products?search=SHELF");
        var shelf = list.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid();
        var prices = await GetAsync(b, "/api/v1/catalog/price-lists");
        var retail = prices.EnumerateArray().Single(p => p.GetProperty("code").GetString() == "RETAIL");
        retail.GetProperty("itemCount").GetInt32().ShouldBe(1);

        var main = (await GetAsync(b, "/api/v1/organization/warehouses")).GetProperty("items").EnumerateArray()
            .First(w => w.GetProperty("code").GetString() == "MAIN").GetProperty("id").GetGuid();

        var badStock = await UploadCsvAsync(b, "opening-stock", "Warehouse code,SKU,Quantity,Unit cost\nMAIN,SHELF-01,10,100\nMAIN,NOPE,1,1\nZZZ,BOLT-02,1,1\nMAIN,BOLT-02,0,1\n",
            openingDate: "2026-01-01");
        var stockErrors = Errors(await ValidateAsync(b, badStock)).ToList();
        stockErrors.ShouldContain((3, "import.product-not-found"));
        stockErrors.ShouldContain((4, "import.warehouse-not-found"));
        stockErrors.ShouldContain((5, "import.quantity-not-positive"));
        stockErrors.Select(e => e.Row).ShouldNotContain(2);

        var stock = await UploadCsvAsync(b, "opening-stock", "Warehouse code,SKU,Unit,Quantity,Unit cost\nMAIN,SHELF-01,PCS,10,100\nMAIN,BOLT-02,,200,0.5\n",
            openingDate: "2026-01-01");
        await ValidatedAsync(b, stock);
        await CommitOkAsync(b, stock);
        var onHand = await GetAsync(b, $"/api/v1/inventory/stock?productId={shelf}&warehouseId={main}");
        onHand.GetProperty("items").EnumerateArray().Sum(r => r.GetProperty("onHand").GetDecimal()).ShouldBe(10m);
        (await ClosingAsync(b, "1310")).ShouldBe(1100m);
        (await ClosingAsync(b, "3300")).ShouldBe(-1100m);
    }

    [Fact]
    public async Task Commit_is_all_or_nothing_when_data_changed_after_validation_and_concurrent_commits_apply_once()
    {
        var b = await NewBooksAsync();
        var job = await UploadCsvAsync(b, "suppliers", "Code,Name (English),Name (Arabic)\nS1,Nile,النيل\nS2,Delta,دلتا\n");
        await ValidatedAsync(b, job);

        // Someone creates S2 by hand after validation: the commit fails, names the row and writes nothing.
        (await b.T.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/parties/suppliers", b.T.CompanyId, new { code = "S2", nameEn = "Manual", nameAr = "يدوي", currency = "EGP" }))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        var failed = await CommitAsync(b, job);
        failed.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var problem = await failed.Content.ReadFromJsonAsync<JsonElement>(Ct);
        problem.GetProperty("code").GetString().ShouldBe("import.commit-failed");
        problem.GetProperty("errors").TryGetProperty("row 3", out _).ShouldBeTrue(problem.ToString());
        (await GetAsync(b, "/api/v1/parties/suppliers?search=S1")).GetProperty("totalCount").GetInt32().ShouldBe(0);
        (await GetAsync(b, $"/api/v1/import/jobs/{job}")).GetProperty("status").GetString().ShouldBe("Validated");

        // Two simultaneous commits of one job (different idempotency keys): exactly one applies.
        var second = await UploadCsvAsync(b, "suppliers", "Code,Name (English),Name (Arabic)\nS3,Nile,النيل\nS4,Gamma,جاما\n");
        await ValidatedAsync(b, second);
        var results = await Task.WhenAll(CommitAsync(b, second), CommitAsync(b, second));
        results.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        results.Count(r => r.StatusCode == HttpStatusCode.Conflict).ShouldBe(1);
        (await GetAsync(b, "/api/v1/parties/suppliers?search=S")).GetProperty("totalCount").GetInt32().ShouldBe(3);
    }

    [Fact]
    public async Task Jobs_are_tenant_isolated_and_templates_describe_every_type()
    {
        var a = await NewBooksAsync();
        var job = await UploadCsvAsync(a, "suppliers", "Code,Name (English),Name (Arabic)\nS1,Nile,النيل\n");
        (await GetAsync(a, "/api/v1/import/jobs")).GetProperty("totalCount").GetInt32().ShouldBe(1);

        var other = await fixture.NewTenantAsync();
        (await other.Client.GetAsCompanyAsync($"/api/v1/import/jobs/{job}", other.CompanyId)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var otherList = await (await other.Client.GetAsCompanyAsync("/api/v1/import/jobs", other.CompanyId)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        otherList.GetProperty("totalCount").GetInt32().ShouldBe(0);
        (await other.Client.SendAsCompanyAsync(HttpMethod.Post, $"/api/v1/import/jobs/{job}/validate", other.CompanyId)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        await using (var connection = await fixture.OpenAsTenantAsync(other.TenantId))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM import.ImportJobFile";
            ((int)(await command.ExecuteScalarAsync(Ct))!).ShouldBe(0); // row-level security hides the other tenant's file
        }

        var types = await GetAsync(a, "/api/v1/import/types");
        types.GetArrayLength().ShouldBe(8);
        var template = await a.T.Client.GetAsCompanyAsync("/api/v1/import/types/opening-stock/template", a.T.CompanyId);
        template.StatusCode.ShouldBe(HttpStatusCode.OK);
        template.Content.Headers.ContentType!.MediaType.ShouldBe("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        using var workbook = new XLWorkbook(new MemoryStream(await template.Content.ReadAsByteArrayAsync(Ct)));
        workbook.Worksheet("Data").Cell(1, 1).GetString().ShouldBe("Warehouse code *");

        // Uploads needing a legal entity or opening date are refused without them; unknown types/formats too.
        (await (await UploadAsync(a, "GlOpeningBalances", "gl.csv", Encoding.UTF8.GetBytes("Account code,Debit\n1110,1\n"))).ErrorCodeAsync())
            .ShouldBe("import.legal-entity-required");
        (await (await UploadAsync(a, "nope", "x.csv", Encoding.UTF8.GetBytes("a\n1\n"))).ErrorCodeAsync()).ShouldBe("import.unknown-type");
        (await (await UploadAsync(a, "suppliers", "x.pdf", Encoding.UTF8.GetBytes("a\n1\n"))).ErrorCodeAsync()).ShouldBe("import.unsupported-format");
    }
}
