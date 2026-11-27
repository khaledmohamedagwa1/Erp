using System.Text;
using ClosedXML.Excel;
using Erp.Application.Reporting;
using Erp.Infrastructure.Rendering;
using Erp.Modules.Reporting.Application;
using Erp.Modules.Reporting.Domain;
using Erp.Modules.Reporting.Infrastructure;
using Erp.Modules.Sales.Contracts;
using Erp.SharedKernel.Documents;
using Erp.SharedKernel.Domain;
using Shouldly;
using Xunit;

namespace Erp.UnitTests;

public sealed class ReportingTests
{
    private static readonly DateOnly AsOf = new(2026, 6, 30);

    private static ReportTable SampleTable() => new(
        "sales/by-customer",
        "Sales by customer",
        "المبيعات حسب العميل",
        [
            new ReportColumn("customer", "Customer", "العميل", ReportColumnType.Text),
            new ReportColumn("date", "Date", "التاريخ", ReportColumnType.Date),
            new ReportColumn("quantity", "Quantity", "الكمية", ReportColumnType.Quantity),
            new ReportColumn("net", "Net", "الصافي", ReportColumnType.Money),
        ],
        [
            ["شركة النور, فرع \"القاهرة\"", new DateOnly(2026, 6, 1), 2.5m, 1234.5m],
            ["Plain Customer", new DateOnly(2026, 6, 2), 1m, 10m],
        ],
        [null, null, 3.5m, 1244.5m],
        [new ReportParameter("From", "من", "2026-06-01")]);

    private static RenderContext Context(string language) => new(language, "Acme Trading", "أكمي للتجارة", "Acme Egypt", "123-456-789", new DateTime(2026, 6, 30, 10, 0, 0, DateTimeKind.Utc));

    // ---------------- Aggregations ----------------
    [Fact]
    public void Stock_is_aged_from_the_newest_receipts_backwards()
    {
        (DateOnly, decimal)[] inbound =
        [
            (AsOf.AddDays(-10), 5m),   // 0-30
            (AsOf.AddDays(-45), 10m),  // 31-60
            (AsOf.AddDays(-100), 20m), // 91-180
        ];

        var buckets = ReportMath.AgeStock(12m, inbound, AsOf);

        buckets.ShouldBe([5m, 7m, 0m, 0m, 0m]);
    }

    [Fact]
    public void Stock_not_explained_by_receipts_goes_to_the_oldest_bucket()
    {
        var buckets = ReportMath.AgeStock(30m, [(AsOf.AddDays(-200), 4m), (AsOf, 6m)], AsOf);

        // Receipts are consumed in the order given (newest first is the caller's contract).
        buckets.Sum().ShouldBe(30m);
        buckets[^1].ShouldBe(24m);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(30, 0)]
    [InlineData(31, 1)]
    [InlineData(60, 1)]
    [InlineData(61, 2)]
    [InlineData(90, 2)]
    [InlineData(91, 3)]
    [InlineData(180, 3)]
    [InlineData(181, 4)]
    [InlineData(5000, 4)]
    public void Aging_bucket_boundaries_are_inclusive(int days, int bucket) => ReportMath.BucketOf(days).ShouldBe(bucket);

    [Fact]
    public void No_stock_means_empty_buckets() => ReportMath.AgeStock(0m, [(AsOf, 10m)], AsOf).ShouldAllBe(b => b == 0m);

    [Fact]
    public void Margin_and_periods()
    {
        ReportMath.Margin(200m, 50m).ShouldBe(25m);
        ReportMath.Margin(0m, 50m).ShouldBe(0m);
        ReportMath.Percent(1m, 3m).ShouldBe(33.33m);
        ReportMath.Period(new DateOnly(2026, 3, 9), "day").ShouldBe("2026-03-09");
        ReportMath.Period(new DateOnly(2026, 3, 9), "month").ShouldBe("2026-03");
        ReportMath.Period(new DateOnly(2026, 3, 9), "year").ShouldBe("2026");
    }

    [Fact]
    public void Sales_summary_nets_returns_and_computes_gross_profit_per_period()
    {
        var customer = Guid.NewGuid();
        var product = Guid.NewGuid();
        var invoice1 = Guid.NewGuid();
        var invoice2 = Guid.NewGuid();
        var creditNote = Guid.NewGuid();
        SalesFact[] facts =
        [
            new(SalesDocumentTypes.Invoice, invoice1, "INV-1", new DateOnly(2026, 5, 3), null, Guid.NewGuid(), customer, product, "A", "A", false, 10m, 1000m, 140m, 600m),
            new(SalesDocumentTypes.Invoice, invoice1, "INV-1", new DateOnly(2026, 5, 3), null, Guid.NewGuid(), customer, product, "B", "B", true, 1m, 100m, 14m, 0m),
            new(SalesDocumentTypes.Invoice, invoice2, "INV-2", new DateOnly(2026, 6, 1), null, Guid.NewGuid(), customer, product, "A", "A", false, 5m, 500m, 70m, 300m),
            new(SalesDocumentTypes.Return, creditNote, "CN-1", new DateOnly(2026, 6, 2), null, Guid.NewGuid(), customer, product, "A", "A", false, -2m, -200m, -28m, -120m),
        ];
        var query = new ReportQuery(new DateOnly(2026, 5, 1), new DateOnly(2026, 6, 30), AsOf, null, null, null, null, null, "month", null, null, 1, 100, false, 100, false);

        var table = SalesReports.Summary(facts, "month", query);

        table.Rows.Count.ShouldBe(2);
        var may = table.Rows[0];
        may[0].ShouldBe("2026-05");
        may[1].ShouldBe(1m); // one invoice
        may[5].ShouldBe(1100m);
        may[7].ShouldBe(600m);
        may[8].ShouldBe(500m);
        var june = table.Rows[1];
        june[2].ShouldBe(1m); // one credit note
        june[3].ShouldBe(500m);
        june[4].ShouldBe(-200m);
        june[5].ShouldBe(300m);
        june[7].ShouldBe(180m);
        june[8].ShouldBe(120m);
        june[9].ShouldBe(40m);
        table.Totals![5].ShouldBe(1400m);
        table.Totals[8].ShouldBe(620m);
        table.Totals[9].ShouldBe(ReportMath.Margin(1400m, 620m));
    }

    // ---------------- CSV ----------------
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("line1\nline2", "\"line1\nline2\"")]
    [InlineData("عميل نقدي", "عميل نقدي")]
    [InlineData("شركة، النور, مصر", "\"شركة، النور, مصر\"")]
    public void Csv_fields_are_escaped(string value, string expected) => CsvReportWriter.Escape(value).ShouldBe(expected);

    [Fact]
    public async Task Csv_export_has_bom_arabic_headers_and_invariant_numbers()
    {
        using var stream = new MemoryStream();
        await CsvReportWriter.WriteAsync(SampleTable(), stream, arabic: true, TestContext.Current.CancellationToken);
        var bytes = stream.ToArray();

        bytes.Take(3).ShouldBe(new byte[] { 0xEF, 0xBB, 0xBF });
        var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines[0].ShouldBe("العميل,التاريخ,الكمية,الصافي");
        lines[1].ShouldBe("\"شركة النور, فرع \"\"القاهرة\"\"\",2026-06-01,2.5,1234.50");
        lines[2].ShouldBe("Plain Customer,2026-06-02,1,10.00");
        lines[3].ShouldBe("الإجمالي,,3.5,1244.50");
    }

    // ---------------- Excel ----------------
    [Fact]
    public void Xlsx_export_opens_and_has_typed_cells()
    {
        using var stream = new MemoryStream();
        XlsxReportWriter.Write(SampleTable(), Context("en"), stream);
        stream.Position = 0;

        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheets.Single();
        sheet.Name.ShouldBe("Sales by customer");
        sheet.RightToLeft.ShouldBeFalse();
        sheet.Cell(1, 1).GetString().ShouldBe("Sales by customer");
        sheet.Cell(2, 1).GetString().ShouldBe("Acme Trading");
        sheet.Cell(3, 1).GetString().ShouldBe("From");
        // Row 4 is blank; header on row 5, data from row 6.
        sheet.Cell(5, 1).GetString().ShouldBe("Customer");
        sheet.Cell(5, 4).GetString().ShouldBe("Net");
        sheet.Cell(6, 1).GetString().ShouldBe("شركة النور, فرع \"القاهرة\"");
        sheet.Cell(6, 2).GetDateTime().ShouldBe(new DateTime(2026, 6, 1));
        sheet.Cell(6, 4).GetValue<decimal>().ShouldBe(1234.5m);
        sheet.Cell(8, 1).GetString().ShouldBe("Total");
        sheet.Cell(8, 4).GetValue<decimal>().ShouldBe(1244.5m);
    }

    [Fact]
    public void Arabic_xlsx_is_right_to_left_with_arabic_headers()
    {
        using var stream = new MemoryStream();
        XlsxReportWriter.Write(SampleTable(), Context("ar"), stream);
        stream.Position = 0;

        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheets.Single();
        sheet.RightToLeft.ShouldBeTrue();
        sheet.Cell(1, 1).GetString().ShouldBe("المبيعات حسب العميل");
        sheet.Cell(5, 1).GetString().ShouldBe("العميل");
    }

    [Fact]
    public void Sheet_names_are_made_valid() => XlsxReportWriter.SheetName("a/b:c?d*e[f]g that is far longer than thirty-one characters").Length.ShouldBe(31);

    // ---------------- HTML ----------------
    [Fact]
    public void Arabic_html_report_is_rtl_and_escaped()
    {
        var html = new HtmlDocumentRenderer().RenderReport(SampleTable(), Context("ar"));

        html.ShouldContain("dir=\"rtl\"");
        html.ShouldContain("lang=\"ar\"");
        html.ShouldContain("المبيعات حسب العميل");
        html.ShouldContain("أكمي للتجارة");
        html.ShouldContain("&quot;القاهرة&quot;");
        html.ShouldContain("1,234.50");
        html.ShouldContain("page-break-inside");
    }

    [Fact]
    public void English_html_report_is_ltr() => new HtmlDocumentRenderer().RenderReport(SampleTable(), Context("en")).ShouldContain("dir=\"ltr\"");

    [Fact]
    public void Printed_document_shows_party_lines_taxes_and_totals()
    {
        var uom = Guid.NewGuid();
        var document = new PrintableDocument("sales-invoice", "Tax invoice", "فاتورة ضريبية", "INV-2026-0001", new DateOnly(2026, 6, 30), "EGP", "Posted",
            Guid.NewGuid(), Guid.NewGuid(), new PrintParty("Customer", Guid.NewGuid()),
            [new PrintField("Customer reference", "مرجع العميل", "PO-77")],
            [new PrintLine("SKU-1", "Widget <b>", 2m, Guid.NewGuid(), uom, 50m, 0m, 100m, 14m, 114m)],
            [new PrintTax("VAT", 14m, 100m, 14m)],
            [new PrintTotal("Total", "الإجمالي", 114m, Emphasis: true)]);

        var html = new HtmlDocumentRenderer().RenderDocument(document, new PrintPartyDetails("C-001", "عميل تجريبي", "999-888"),
            new Dictionary<Guid, string> { [uom] = "PCS" }, Context("ar"));

        html.ShouldContain("dir=\"rtl\"");
        html.ShouldContain("فاتورة ضريبية");
        html.ShouldContain("INV-2026-0001");
        html.ShouldContain("عميل تجريبي");
        html.ShouldContain("Widget &lt;b&gt;");
        html.ShouldContain("PCS");
        html.ShouldContain("114.00");
        html.ShouldContain("123-456-789");
    }

    // ---------------- Saved views ----------------
    [Fact]
    public void Saved_view_requires_a_json_object_of_filters()
    {
        var view = SavedReportView.Create(Guid.NewGuid(), Guid.NewGuid(), " Monthly ", "Sales/Summary", "{\"groupBy\":\"month\"}", DateTime.UtcNow);
        view.Name.ShouldBe("Monthly");
        view.ReportKey.ShouldBe("sales/summary");

        Should.Throw<DomainException>(() => SavedReportView.Create(Guid.NewGuid(), Guid.NewGuid(), "x", "sales/summary", "[1,2]", DateTime.UtcNow))
            .Error.Code.ShouldBe("reporting.view-filters-invalid");
        Should.Throw<DomainException>(() => SavedReportView.Create(Guid.NewGuid(), Guid.NewGuid(), "x", "sales/summary", "{not json", DateTime.UtcNow))
            .Error.Code.ShouldBe("reporting.view-filters-invalid");
        Should.Throw<DomainException>(() => SavedReportView.Create(Guid.NewGuid(), Guid.NewGuid(), "", "sales/summary", null, DateTime.UtcNow))
            .Error.Code.ShouldBe("reporting.view-name-required");
    }

    [Fact]
    public void Document_file_names_are_sanitised() => DocumentPrinting.SafeFileName("INV/2026 0001").ShouldBe("INV-2026-0001");
}
