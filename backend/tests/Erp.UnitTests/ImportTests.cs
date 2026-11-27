using System.Text;
using ClosedXML.Excel;
using Erp.Application.Abstractions;
using Erp.Modules.Import.Application;
using Erp.Modules.Import.Domain;
using Erp.Modules.Import.Infrastructure;
using Erp.SharedKernel.Domain;
using Erp.SharedKernel.Results;
using Shouldly;
using Xunit;

namespace Erp.UnitTests;

public class ImportFileTests
{
    private static ImportTable Csv(string text) => CsvImportReader.Read(Encoding.UTF8.GetBytes(text)).Value;

    [Fact]
    public void Csv_handles_quotes_embedded_delimiters_line_breaks_and_a_bom()
    {
        var table = Csv("﻿Code,Name\r\nA1,\"Smith, \"\"Jr\"\"\"\r\n\r\nA2,\"two\nlines\"\nA3,  spaced  \n");
        table.Headers.ShouldBe(["Code", "Name"]);
        table.Rows.Count.ShouldBe(3);
        table.Rows[0].Cells.ShouldBe(["A1", "Smith, \"Jr\""]);
        table.Rows[0].RowNumber.ShouldBe(2);
        table.Rows[1].Cells[1].ShouldBe("two\nlines");
        table.Rows[1].RowNumber.ShouldBe(4); // the blank line 3 is skipped but still counted
        table.Rows[2].RowNumber.ShouldBe(6);
        table.Rows[2].Cells[1].ShouldBe("spaced");
    }

    [Fact]
    public void Csv_detects_semicolon_and_tab_delimiters()
    {
        Csv("a;b;c\n1;2,5;3\n").Rows[0].Cells.ShouldBe(["1", "2,5", "3"]);
        Csv("a\tb\n1\t2\n").Rows[0].Cells.ShouldBe(["1", "2"]);
    }

    [Fact]
    public void Csv_rejects_non_utf8_and_empty_files()
    {
        CsvImportReader.Read([0xC3, 0x28, 0x2C]).Error.Code.ShouldBe("import.encoding");
        CsvImportReader.Read(Encoding.UTF8.GetBytes("  \n")).Error.Code.ShouldBe(ImportErrors.FileEmpty.Code);
    }

    [Theory]
    [InlineData("1,234.50", 1234.50)]
    [InlineData("1,234", 1234)]
    [InlineData("12,5", 12.5)]
    [InlineData("(150)", -150)]
    [InlineData("١٢٣٫٥", 123.5)]
    [InlineData("-7", -7)]
    public void Numbers_accept_common_spreadsheet_formats(string text, double expected)
    {
        ImportValues.TryDecimal(text, out var value).ShouldBeTrue();
        value.ShouldBe((decimal)expected);
    }

    [Theory]
    [InlineData("2026-03-01")]
    [InlineData("01/03/2026")]
    [InlineData("2026-03-01T00:00:00")]
    [InlineData("٢٠٢٦-٠٣-٠١")]
    public void Dates_accept_iso_day_first_and_arabic_digits(string text)
    {
        ImportValues.TryDate(text, out var date).ShouldBeTrue();
        date.ShouldBe(new DateOnly(2026, 3, 1));
    }

    [Theory]
    [InlineData("yes", true)]
    [InlineData("نعم", true)]
    [InlineData("0", false)]
    [InlineData("لا", false)]
    public void Booleans_accept_english_and_arabic(string text, bool expected)
    {
        ImportValues.TryBool(text, out var value).ShouldBeTrue();
        value.ShouldBe(expected);
    }

    [Fact]
    public void Header_mapping_accepts_arabic_aliases_and_reports_missing_and_duplicate_columns()
    {
        var definition = new GlOpeningBalancesImport(null!).Info;
        var (map, errors) = ImportColumnMap.Build(definition.Columns, ["رمز الحساب", "DEBIT", "credit *", "Unknown"]);
        errors.ShouldBeEmpty();
        map.UnknownHeaders.ShouldBe(["Unknown"]);
        map.Get(new ImportSourceRow(2, ["1110", " 5 ", "", "x"]), "account").ShouldBe("1110");
        map.Get(new ImportSourceRow(2, ["1110", " 5 ", "", "x"]), "debit").ShouldBe("5");
        map.Get(new ImportSourceRow(2, ["1110", " 5 ", "", "x"]), "credit").ShouldBeNull();

        var (_, bad) = ImportColumnMap.Build(definition.Columns, ["Debit", "Debit"]);
        bad.Select(e => e.Code).ShouldBe(["import.duplicate-column", "import.missing-column"], ignoreOrder: true);
    }

    [Fact]
    public void Template_round_trips_through_the_xlsx_reader_with_every_header_recognised()
    {
        foreach (var definition in AllDefinitions())
        {
            var bytes = new XlsxImportTemplateWriter().Write(definition.Info);
            var table = ImportFileReader.ReadXlsx(bytes);
            table.IsSuccess.ShouldBeTrue();
            var (_, errors) = ImportColumnMap.Build(definition.Info.Columns, table.Value.Headers);
            errors.ShouldBeEmpty(definition.Info.Name);
            table.Value.Rows.Count.ShouldBe(1); // the example row
        }
    }

    [Fact]
    public void Xlsx_reader_turns_numbers_and_dates_into_invariant_text()
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Data");
        sheet.Cell(1, 1).Value = "Code";
        sheet.Cell(1, 2).Value = "Date";
        sheet.Cell(1, 3).Value = "Amount";
        sheet.Cell(2, 1).Value = 1115;
        sheet.Cell(2, 2).Value = new DateTime(2026, 2, 3);
        sheet.Cell(2, 3).Value = 1234.5;
        sheet.Cell(4, 1).Value = "X"; // row 3 left blank
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);

        var table = ImportFileReader.ReadXlsx(stream.ToArray()).Value;
        table.Rows[0].Cells.ShouldBe(["1115", "2026-02-03", "1234.5"]);
        table.Rows[1].RowNumber.ShouldBe(4);
        ImportFileReader.ReadXlsx(Encoding.UTF8.GetBytes("not a workbook")).Error.Code.ShouldBe("import.unreadable-file");
    }

    internal static IReadOnlyList<IImportDefinition> AllDefinitions() =>
    [
        new ChartOfAccountsImport(null!), new ProductsImport(null!), new CustomersImport(null!, null!), new SuppliersImport(null!, null!),
        new OpeningStockImport(null!, null!, null!, null!), new OpenReceivablesImport(null!, null!), new OpenPayablesImport(null!, null!),
        new GlOpeningBalancesImport(null!),
    ];
}

public class ImportDefinitionTests
{
    private static readonly ImportContext Context = new(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 1, 1));

    private static ImportTable Csv(string text) => CsvImportReader.Read(Encoding.UTF8.GetBytes(text)).Value;

    [Fact]
    public void Every_import_type_has_a_definition_with_unique_keys()
    {
        var definitions = ImportFileTests.AllDefinitions();
        definitions.Select(d => d.Info.Type).Order().ShouldBe(Enum.GetValues<ImportType>());
        foreach (var definition in definitions)
        {
            definition.Info.Columns.Select(c => c.Key).ShouldBeUnique();
            definition.Info.Columns.Select(c => ImportColumnMap.NormalizeHeader(c.Header)).ShouldBeUnique();
        }
    }

    [Fact]
    public async Task Gl_opening_rejects_one_sided_violations_per_row_and_unbalanced_files_as_a_whole()
    {
        var definition = new GlOpeningBalancesImport(null!);
        var lines = await definition.PrepareAsync(Csv("Account code,Debit,Credit\n1110,10,5\n1120,,\n3100,,10\n"), Context, CancellationToken.None);
        lines.Errors.Select(e => (e.Row, e.Code)).ShouldBe([(2, "import.one-sided"), (3, "import.one-sided")]);

        var unbalanced = await definition.PrepareAsync(Csv("Account code,Debit,Credit\n1110,10,\n3100,,9.99\n"), Context, CancellationToken.None);
        unbalanced.Errors.Single().ShouldBe(new ImportRowError(0, null, "import.unbalanced", "Total debit (10.00) must equal total credit (9.99); difference 0.01."));
        unbalanced.Units.ShouldBeEmpty();

        var balanced = await definition.PrepareAsync(Csv("Account code,Debit,Credit\n1110,10,\n3100,,10\n"), Context, CancellationToken.None);
        balanced.Errors.ShouldBeEmpty();
        balanced.Units.Single().Rows.ShouldBe([2, 3]);
        balanced.Units.Single().DiagnoseRow.ShouldBeNull(); // a single line can never balance: line errors come back indexed instead
    }

    [Fact]
    public async Task Missing_required_columns_stop_before_any_row_is_read()
    {
        var prepared = await new ChartOfAccountsImport(null!).PrepareAsync(Csv("Code,Name (English)\n1115,Petty\n"), Context, CancellationToken.None);
        prepared.Errors.Select(e => e.Column).ShouldBe(["Name (Arabic)", "Parent code"], ignoreOrder: true);
        prepared.Errors.ShouldAllBe(e => e.Row == 1);
        prepared.Units.ShouldBeEmpty();
    }

    [Fact]
    public async Task Open_items_validate_amounts_rates_and_duplicates_in_the_file()
    {
        var prepared = await new OpenReceivablesImport(null!, new FakeParties()).PrepareAsync(Csv(
            "Customer code,Document number,Document date,Due date,Open amount,Exchange rate\n" +
            "C1,A,2026-01-05,,100,\n" +
            "C1,B,05/13/2026,,100,\n" +   // month 13: not a date
            "C1,C,2026-01-05,,0,\n" +
            "C1,D,2026-01-05,,10,-1\n" +
            "C1,a,2026-01-06,,10,\n" +   // same document as row 2 (case-insensitive)
            "C9,E,2026-01-06,,10,\n"), Context, CancellationToken.None);
        prepared.Errors.Select(e => (e.Row, e.Code)).ShouldBe(
            [(3, "import.invalid-date"), (4, "import.zero-amount"), (5, "import.invalid-rate"), (6, "import.duplicate-document"), (7, "import.party-not-found")],
            ignoreOrder: true);
        prepared.Units.Select(u => u.Rows.Single()).ShouldBe([2]);
    }

    private sealed class FakeParties : Erp.Modules.Parties.Contracts.IPartyImportService
    {
        public Task<Result<Guid>> CreateCustomerAsync(Erp.Modules.Parties.Contracts.CustomerImportRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<Guid>> CreateSupplierAsync(Erp.Modules.Parties.Contracts.SupplierImportRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, Guid>> FindCustomerIdsByCodeAsync(IReadOnlyCollection<string> codes, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, Guid>>(new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase) { ["C1"] = Guid.NewGuid() });

        public Task<IReadOnlyDictionary<string, Guid>> FindSupplierIdsByCodeAsync(IReadOnlyCollection<string> codes, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}

public class ImportRunnerTests
{
    private sealed class RecordingSavepoints : IImportSavepoints
    {
        public List<string> Log { get; } = [];

        public Task CreateAsync(string name, CancellationToken cancellationToken)
        {
            Log.Add("save " + name);
            return Task.CompletedTask;
        }

        public Task RollbackAsync(string name, CancellationToken cancellationToken)
        {
            Log.Add("rollback " + name);
            return Task.CompletedTask;
        }
    }

    private sealed class CountingReset : IChangeTrackerReset
    {
        public int Count { get; private set; }

        public void ClearTrackedChanges() => Count++;
    }

    private static ImportUnit Unit(Result result, params int[] rows) => new(rows, _ => Task.FromResult(result), null);

    [Fact]
    public async Task Failed_units_roll_back_to_their_savepoint_and_successful_ones_are_kept()
    {
        var savepoints = new RecordingSavepoints();
        var reset = new CountingReset();
        var runner = new ImportRunner(savepoints, [reset]);
        var (succeeded, errors) = await runner.RunAsync(
            [Unit(Result.Success(), 2), Unit(Error.Conflict("x.taken", "Taken."), 3), new ImportUnit([4], _ => throw new DomainException(Error.Validation("x.rule", "Rule.")), null)],
            CancellationToken.None);
        succeeded.ShouldBe(1);
        errors.ShouldBe([new ImportRowError(3, null, "x.taken", "Taken."), new ImportRowError(4, null, "x.rule", "Rule.")]);
        savepoints.Log.ShouldBe(["save import_1", "save import_2", "rollback import_2", "save import_3", "rollback import_3"]);
        reset.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Indexed_line_errors_point_at_rows_and_otherwise_each_row_is_diagnosed_alone()
    {
        var runner = new ImportRunner(new RecordingSavepoints(), []);
        var indexed = Error.Validation("doc.lines", "Bad lines.") with { Details = new Dictionary<string, string[]> { ["lines[1]"] = ["Account missing."], ["lines[2].quantity"] = ["Too big."] } };
        var (_, lineErrors) = await runner.RunAsync([Unit(indexed, 10, 11, 12)], CancellationToken.None);
        lineErrors.Select(e => (e.Row, e.Message)).ShouldBe([(11, "Account missing."), (12, "Too big.")]);

        var diagnosed = new ImportUnit([20, 21, 22], _ => Task.FromResult<Result>(Error.Validation("doc.bad", "Bad document.")),
            (row, _) => Task.FromResult(row == 21 ? (Result)Error.Validation("line.bad", "Row 21 is wrong.") : Result.Success()));
        var (_, rowErrors) = await runner.RunAsync([diagnosed], CancellationToken.None);
        rowErrors.ShouldBe([new ImportRowError(21, null, "line.bad", "Row 21 is wrong.")]);

        var documentLevel = new ImportUnit([30, 31], _ => Task.FromResult<Result>(Error.Conflict("period.closed", "Closed.")), (_, _) => Task.FromResult(Result.Success()));
        var (_, docErrors) = await runner.RunAsync([documentLevel], CancellationToken.None);
        docErrors.ShouldBe([new ImportRowError(30, null, "period.closed", "Closed.")]);
    }

    [Fact]
    public void Job_status_follows_validation_and_commit_rules()
    {
        var job = ImportJob.Create(Guid.NewGuid(), ImportType.Customers, "c.csv", ImportFileFormat.Csv, null, null, null, DateTime.UtcNow);
        Should.Throw<DomainException>(() => job.BeginCommit(DateTime.UtcNow)).Error.ShouldBe(ImportErrors.NotValidated);
        job.RecordValidation(2, [new ImportRowError(2, null, "x", "y")], DateTime.UtcNow);
        job.Status.ShouldBe(ImportStatus.Failed);
        job.RecordValidation(0, [], DateTime.UtcNow);
        job.Status.ShouldBe(ImportStatus.Failed); // an empty file never validates
        job.RecordValidation(2, [], DateTime.UtcNow);
        job.Status.ShouldBe(ImportStatus.Validated);
        job.MarkCommitted(2, Guid.NewGuid(), DateTime.UtcNow);
        Should.Throw<DomainException>(() => job.MarkCommitted(2, null, DateTime.UtcNow)).Error.ShouldBe(ImportErrors.AlreadyCommitted);
        Should.Throw<DomainException>(() => job.RecordValidation(2, [], DateTime.UtcNow)).Error.ShouldBe(ImportErrors.AlreadyCommitted);
        Should.Throw<DomainException>(() => ImportJobFile.Create(job, [])).Error.ShouldBe(ImportErrors.FileEmpty);
    }
}
