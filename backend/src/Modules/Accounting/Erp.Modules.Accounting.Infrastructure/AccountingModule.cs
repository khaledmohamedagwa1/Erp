using Erp.Application;
using Erp.Application.Messaging;
using Erp.Application.Paging;
using Erp.Infrastructure;
using Erp.Infrastructure.Outbox;
using Erp.Modules.Accounting.Application;
using Erp.Modules.Accounting.Contracts;
using Erp.Web.Http;
using Erp.Web.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Modules.Accounting.Infrastructure;

public sealed record UpdateAccountRequest(string NameEn, string NameAr, bool AllowManualPosting);

public sealed record PeriodStatusRequest(string Status);

public sealed record ReverseRequest(DateOnly ReversalDate);

public sealed record CashFlowCategoryRequest(string Category);

public sealed class AccountingModule : IErpModule
{
    public ModuleManifest Manifest { get; } = new(
        "accounting",
        "Accounting",
        "المحاسبة",
        IsCore: true,
        DependsOn: [],
        Permissions:
        [
            new(AccountingPermissions.AccountRead, "accounting", "View the chart of accounts, cost centres and posting rules", "عرض دليل الحسابات ومراكز التكلفة وقواعد الترحيل"),
            new(AccountingPermissions.AccountManage, "accounting", "Change the chart of accounts and cost centres", "تعديل دليل الحسابات ومراكز التكلفة", IsSensitive: true),
            new(AccountingPermissions.PostingRuleManage, "accounting", "Change posting rules (account determination)", "تعديل قواعد الترحيل", IsSensitive: true),
            new(AccountingPermissions.PeriodManage, "accounting", "Create fiscal years; open, soft-close, close and reopen periods", "إنشاء السنوات المالية وفتح الفترات وإغلاقها", IsSensitive: true),
            new(AccountingPermissions.PostInSoftClosedPeriod, "accounting", "Post into soft-closed periods", "الترحيل في فترات مغلقة مبدئيًا", IsSensitive: true),
            new(AccountingPermissions.JournalRead, "accounting", "View journal entries", "عرض القيود اليومية"),
            new(AccountingPermissions.JournalCreate, "accounting", "Create manual journal drafts", "إنشاء مسودات القيود اليدوية"),
            new(AccountingPermissions.JournalPost, "accounting", "Post manual journals", "ترحيل القيود اليدوية", IsSensitive: true),
            new(AccountingPermissions.JournalReverse, "accounting", "Reverse manual journals", "عكس القيود اليدوية", IsSensitive: true),
            new(AccountingPermissions.ReportRead, "accounting", "View trial balance, ledgers and financial statements", "عرض ميزان المراجعة ودفتر الأستاذ والقوائم المالية"),
            new(AccountingPermissions.OpeningBalanceManage, "accounting", "Enter, post and reverse general-ledger opening balances", "إدخال وترحيل وعكس الأرصدة الافتتاحية", IsSensitive: true),
            new(AccountingPermissions.YearEndClose, "accounting", "Run and undo the year-end close", "تنفيذ وإلغاء إقفال السنة المالية", IsSensitive: true),
        ],
        Menu:
        [
            new("accounting.accounts", "Chart of accounts", "دليل الحسابات", "/accounting/accounts", "accounting", 10, AccountingPermissions.AccountRead),
            new("accounting.journals", "Journal entries", "القيود اليومية", "/accounting/journals", "accounting", 20, AccountingPermissions.JournalRead),
            new("accounting.periods", "Fiscal periods", "الفترات المالية", "/accounting/periods", "accounting", 30, AccountingPermissions.PeriodManage),
            new("accounting.trial-balance", "Trial balance", "ميزان المراجعة", "/accounting/reports/trial-balance", "accounting", 40, AccountingPermissions.ReportRead),
            new("accounting.statements", "Financial statements", "القوائم المالية", "/accounting/reports/statements", "accounting", 50, AccountingPermissions.ReportRead),
            new("accounting.opening-balances", "Opening balances", "الأرصدة الافتتاحية", "/accounting/opening-balances", "accounting", 60, AccountingPermissions.OpeningBalanceManage),
        ]);

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddErpDbContext<AccountingDbContext>(AccountingDbContext.SchemaName, order: 70);
        services.AddScoped<IAccountingDbContext>(sp => sp.GetRequiredService<AccountingDbContext>());
        services.AddScoped<IAccountBalanceWriter, AccountBalanceWriter>();
        services.AddScoped<PeriodGuard>();
        services.AddScoped<IPeriodGuard>(sp => sp.GetRequiredService<PeriodGuard>());
        services.AddScoped<JournalPoster>();
        services.AddScoped<IJournalPostingService, JournalPostingService>();
        services.AddScoped<IAccountQueries, AccountQueries>();
        services.AddScoped<LedgerQueries>();
        services.AddScoped<ILedgerQueries>(sp => sp.GetRequiredService<LedgerQueries>());
        services.AddScoped<CompanyLegalEntities>();
        services.AddScoped<FinancialStatementService>();
        services.AddHandlersFromAssembly(typeof(IAccountingDbContext).Assembly);
        services.AddSingleton(new EventAssembly(typeof(Domain.JournalEntry).Assembly));
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var g = endpoints.MapGroup("/api/v1/accounting").WithTags("Accounting").RequireAuthorization().RequireCompany();

        // Chart of accounts
        g.MapGet("/accounts", async (IDispatcher d, CancellationToken ct) => (await d.Send(new ListAccountsQuery(), ct)).ToHttpResult())
            .RequirePermission(AccountingPermissions.AccountRead);
        g.MapPost("/accounts", async (CreateAccountCommand body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(body, ct)).ToHttpResult(a => Results.Created($"/api/v1/accounting/accounts/{a.Id}", a)))
            .RequirePermission(AccountingPermissions.AccountManage);
        g.MapPut("/accounts/{id:guid}", async (Guid id, UpdateAccountRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new UpdateAccountCommand(id, body.NameEn, body.NameAr, body.AllowManualPosting), ct)).ToHttpResult())
            .RequirePermission(AccountingPermissions.AccountManage);
        g.MapPut("/accounts/{id:guid}/cash-flow-category", async (Guid id, CashFlowCategoryRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new SetAccountCashFlowCategoryCommand(id, body.Category), ct)).ToHttpResult())
            .RequirePermission(AccountingPermissions.AccountManage);
        g.MapPost("/accounts/{id:guid}/deactivate", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new SetAccountActiveCommand(id, false), ct)).ToHttpResult())
            .RequirePermission(AccountingPermissions.AccountManage);
        g.MapPost("/accounts/{id:guid}/activate", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new SetAccountActiveCommand(id, true), ct)).ToHttpResult())
            .RequirePermission(AccountingPermissions.AccountManage);
        g.MapPost("/coa-templates/generic", async (IDispatcher d, CancellationToken ct) => (await d.Send(new SeedGenericChartCommand(), ct)).ToHttpResult())
            .RequirePermission(AccountingPermissions.AccountManage)
            .RequirePermission(AccountingPermissions.PostingRuleManage);

        // Fiscal calendar
        g.MapGet("/fiscal-years", async (Guid? legalEntityId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListFiscalYearsQuery(legalEntityId), ct)).ToHttpResult())
            .RequirePermission(AccountingPermissions.AccountRead);
        g.MapPost("/fiscal-years", async (CreateFiscalYearCommand body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(body, ct)).ToHttpResult(y => Results.Created($"/api/v1/accounting/fiscal-years/{y.Id}", y)))
            .RequirePermission(AccountingPermissions.PeriodManage);
        g.MapPut("/periods/{id:guid}/status", async (Guid id, PeriodStatusRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new SetPeriodStatusCommand(id, body.Status), ct)).ToHttpResult())
            .RequirePermission(AccountingPermissions.PeriodManage);
        g.MapPost("/fiscal-years/{id:guid}/close", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CloseFiscalYearCommand(id), ct)).ToHttpResult())
            .RequirePermission(AccountingPermissions.YearEndClose);
        g.MapPost("/fiscal-years/{id:guid}/reopen", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ReopenFiscalYearCommand(id), ct)).ToHttpResult())
            .RequirePermission(AccountingPermissions.YearEndClose)
            .RequirePermission(AccountingPermissions.PeriodManage);

        // Opening balances
        g.MapGet("/opening-balances", async (Guid? legalEntityId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListOpeningBalancesQuery(legalEntityId), ct)).ToHttpResult())
            .RequirePermission(AccountingPermissions.JournalRead);
        g.MapPost("/opening-balances", async (CreateOpeningBalanceCommand body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(body, ct)).ToHttpResult(o => Results.Created($"/api/v1/accounting/opening-balances/{o.Id}", o)))
            .RequirePermission(AccountingPermissions.OpeningBalanceManage);
        g.MapPost("/opening-balances/{id:guid}/post", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new PostOpeningBalanceCommand(id), ct)).ToHttpResult())
            .RequirePermission(AccountingPermissions.OpeningBalanceManage);
        g.MapPost("/opening-balances/{id:guid}/reverse", async (Guid id, ReverseRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ReverseOpeningBalanceCommand(id, body.ReversalDate), ct)).ToHttpResult())
            .RequirePermission(AccountingPermissions.OpeningBalanceManage);

        // Readiness
        g.MapGet("/readiness", async (Guid legalEntityId, string? roles, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new AccountingReadinessQuery(legalEntityId,
                    string.IsNullOrWhiteSpace(roles) ? null : roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)), ct)).ToHttpResult())
            .RequirePermission(AccountingPermissions.AccountRead);

        // Cost centres and posting rules
        g.MapGet("/cost-centers", async (IDispatcher d, CancellationToken ct) => (await d.Send(new ListCostCentersQuery(), ct)).ToHttpResult())
            .RequirePermission(AccountingPermissions.AccountRead);
        g.MapPost("/cost-centers", async (CreateCostCenterCommand body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(body, ct)).ToHttpResult(c => Results.Created($"/api/v1/accounting/cost-centers/{c.Id}", c)))
            .RequirePermission(AccountingPermissions.AccountManage);
        g.MapGet("/posting-rules", async (IDispatcher d, CancellationToken ct) => (await d.Send(new ListPostingRulesQuery(), ct)).ToHttpResult())
            .RequirePermission(AccountingPermissions.AccountRead);
        g.MapPut("/posting-rules", async (UpsertPostingRuleCommand body, IDispatcher d, CancellationToken ct) => (await d.Send(body, ct)).ToHttpResult())
            .RequirePermission(AccountingPermissions.PostingRuleManage);
        g.MapGet("/posting-roles", () => Results.Ok(PostingRoles.All)).RequirePermission(AccountingPermissions.AccountRead);

        // Journals
        g.MapGet("/journals", async (Guid? legalEntityId, DateOnly? from, DateOnly? to, string? status, string? sourceModule, int? page, int? pageSize,
                IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListJournalsQuery(legalEntityId, from, to, status, sourceModule, new PageRequest(page ?? 1, pageSize ?? 50)), ct)).ToHttpResult())
            .RequirePermission(AccountingPermissions.JournalRead);
        g.MapGet("/journals/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new GetJournalQuery(id), ct)).ToHttpResult())
            .RequirePermission(AccountingPermissions.JournalRead);
        g.MapPost("/journals", async (CreateManualJournalCommand body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(body, ct)).ToHttpResult(j => Results.Created($"/api/v1/accounting/journals/{j.Id}", j)))
            .RequirePermission(AccountingPermissions.JournalCreate);
        g.MapPost("/journals/{id:guid}/post", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new PostManualJournalCommand(id), ct)).ToHttpResult())
            .RequirePermission(AccountingPermissions.JournalPost);
        g.MapPost("/journals/{id:guid}/reverse", async (Guid id, ReverseRequest body, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ReverseJournalCommand(id, body.ReversalDate), ct)).ToHttpResult())
            .RequirePermission(AccountingPermissions.JournalReverse);

        // Reports
        g.MapGet("/reports/trial-balance", async (Guid legalEntityId, DateOnly from, DateOnly to, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new TrialBalanceQuery(legalEntityId, from, to), ct)).ToHttpResult(tb => Results.Ok(new
                {
                    tb.LegalEntityId, tb.From, tb.To, tb.Lines, tb.TotalDebit, tb.TotalCredit, tb.IsBalanced,
                })))
            .RequirePermission(AccountingPermissions.ReportRead);
        g.MapGet("/reports/general-ledger", async (Guid legalEntityId, Guid accountId, DateOnly from, DateOnly to, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new GeneralLedgerQuery(legalEntityId, accountId, from, to), ct)).ToHttpResult())
            .RequirePermission(AccountingPermissions.ReportRead);
        g.MapGet("/reports/income-statement", async (Guid legalEntityId, DateOnly from, DateOnly to, DateOnly? compareFrom, DateOnly? compareTo, Guid? costCenterId,
                IDispatcher d, CancellationToken ct) =>
                (await d.Send(new IncomeStatementQuery(legalEntityId, from, to, compareFrom, compareTo, costCenterId), ct)).ToHttpResult())
            .RequirePermission(AccountingPermissions.ReportRead);
        g.MapGet("/reports/balance-sheet", async (Guid legalEntityId, DateOnly asOf, DateOnly? compareAsOf, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new BalanceSheetQuery(legalEntityId, asOf, compareAsOf), ct)).ToHttpResult())
            .RequirePermission(AccountingPermissions.ReportRead);
        g.MapGet("/reports/cash-flow", async (Guid legalEntityId, DateOnly from, DateOnly to, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CashFlowQuery(legalEntityId, from, to), ct)).ToHttpResult())
            .RequirePermission(AccountingPermissions.ReportRead);

        // CSV exports (UTF-8 with BOM; same filters as the JSON reports)
        g.MapGet("/reports/trial-balance.csv", async (Guid legalEntityId, DateOnly from, DateOnly to, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new TrialBalanceQuery(legalEntityId, from, to), ct)).ToHttpResult(tb => Csv(AccountingCsv.TrialBalance(tb), $"trial-balance-{from:yyyyMMdd}-{to:yyyyMMdd}.csv")))
            .RequirePermission(AccountingPermissions.ReportRead);
        g.MapGet("/reports/income-statement.csv", async (Guid legalEntityId, DateOnly from, DateOnly to, DateOnly? compareFrom, DateOnly? compareTo, Guid? costCenterId,
                IDispatcher d, CancellationToken ct) =>
                (await d.Send(new IncomeStatementQuery(legalEntityId, from, to, compareFrom, compareTo, costCenterId), ct))
                .ToHttpResult(s => Csv(AccountingCsv.IncomeStatement(s), $"income-statement-{from:yyyyMMdd}-{to:yyyyMMdd}.csv")))
            .RequirePermission(AccountingPermissions.ReportRead);
        g.MapGet("/reports/balance-sheet.csv", async (Guid legalEntityId, DateOnly asOf, DateOnly? compareAsOf, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new BalanceSheetQuery(legalEntityId, asOf, compareAsOf), ct)).ToHttpResult(s => Csv(AccountingCsv.BalanceSheet(s), $"balance-sheet-{asOf:yyyyMMdd}.csv")))
            .RequirePermission(AccountingPermissions.ReportRead);
        g.MapGet("/reports/cash-flow.csv", async (Guid legalEntityId, DateOnly from, DateOnly to, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CashFlowQuery(legalEntityId, from, to), ct)).ToHttpResult(s => Csv(AccountingCsv.CashFlow(s), $"cash-flow-{from:yyyyMMdd}-{to:yyyyMMdd}.csv")))
            .RequirePermission(AccountingPermissions.ReportRead);
    }

    private static IResult Csv(string content, string fileName) =>
        Results.File(AccountingCsv.ToBytes(content), "text/csv; charset=utf-8", fileName);
}
