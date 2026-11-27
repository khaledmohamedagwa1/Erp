using Erp.Application;
using Erp.Application.Messaging;
using Erp.Application.Paging;
using Erp.Infrastructure;
using Erp.Infrastructure.Outbox;
using Erp.Modules.Finance.Application;
using Erp.Modules.Finance.Contracts;
using Erp.Modules.Finance.Domain;
using Erp.Web.Http;
using Erp.Web.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Modules.Finance.Infrastructure;

public sealed record CashBankAccountRequest(Guid LegalEntityId, string Code, string Name, string Currency, Guid? GlAccountId, string? BankName, string? AccountNumber, string? Iban);

public sealed record SettlementRequest(Guid LegalEntityId, Guid PartyId, DateOnly Date, string Method, Guid CashBankAccountId, decimal Amount, decimal? ExchangeRate,
    Guid? WithholdingTaxGroupId, decimal? WithholdingBase, IReadOnlyList<AllocationInput>? Allocations, string? Reference, string? Description);

public sealed record AdvanceAllocationRequest(DateOnly Date, IReadOnlyList<AllocationInput> Allocations);

public sealed record MatchRequest(Guid? MovementId);

public sealed record RevaluationRequest(Guid LegalEntityId, DateOnly AsOf);

public sealed class FinanceModule : IErpModule
{
    public ModuleManifest Manifest { get; } = new(
        "finance",
        "Receivables, payables, cash & bank",
        "الذمم والنقدية والبنوك",
        IsCore: true,
        DependsOn: ["accounting"],
        Permissions:
        [
            new(FinancePermissions.Read, "finance", "View open items, receipts, payments, cash and reports", "عرض الأرصدة المفتوحة والمقبوضات والمدفوعات والتقارير"),
            new(FinancePermissions.AccountManage, "finance", "Create cash and bank accounts", "إنشاء حسابات النقدية والبنوك"),
            new(FinancePermissions.ReceiptManage, "finance", "Create customer receipts", "إنشاء سندات القبض"),
            new(FinancePermissions.ReceiptPost, "finance", "Post customer receipts", "ترحيل سندات القبض", IsSensitive: true),
            new(FinancePermissions.PaymentManage, "finance", "Create supplier payments", "إنشاء سندات الصرف"),
            new(FinancePermissions.PaymentPost, "finance", "Post supplier payments", "ترحيل سندات الصرف", IsSensitive: true),
            new(FinancePermissions.AllocationManage, "finance", "Allocate advances to open items", "تخصيص الدفعات المقدمة"),
            new(FinancePermissions.TransferPost, "finance", "Create and post internal transfers", "إنشاء وترحيل التحويلات الداخلية", IsSensitive: true),
            new(FinancePermissions.ExpenseManage, "finance", "Create expenses", "إنشاء المصروفات"),
            new(FinancePermissions.ExpensePost, "finance", "Post expenses", "ترحيل المصروفات", IsSensitive: true),
            new(FinancePermissions.ReconciliationManage, "finance", "Reconcile bank statements", "تسوية كشوف البنوك"),
            new(FinancePermissions.RevaluationRun, "finance", "Run foreign-currency revaluation", "تشغيل إعادة تقييم العملات", IsSensitive: true),
        ],
        Menu:
        [
            new("finance.receipts", "Receipts", "المقبوضات", "/finance/receipts", "finance", 10, FinancePermissions.Read),
            new("finance.payments", "Payments", "المدفوعات", "/finance/payments", "finance", 11, FinancePermissions.Read),
            new("finance.open-items", "Open items & aging", "الأرصدة المفتوحة والأعمار", "/finance/open-items", "finance", 12, FinancePermissions.Read),
            new("finance.cash-bank", "Cash & bank", "النقدية والبنوك", "/finance/cash-bank", "finance", 13, FinancePermissions.Read),
            new("finance.expenses", "Expenses", "المصروفات", "/finance/expenses", "finance", 14, FinancePermissions.Read),
        ]);

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddErpDbContext<FinanceDbContext>(FinanceDbContext.SchemaName, order: 90);
        services.AddScoped<IFinanceDbContext>(sp => sp.GetRequiredService<FinanceDbContext>());
        services.AddScoped<FinanceScope>();
        services.AddScoped<AllocationService>();
        services.AddScoped<SubledgerService>();
        services.AddScoped<ISubledgerService>(sp => sp.GetRequiredService<SubledgerService>());
        services.AddScoped<ICreditExposureQueries, CreditExposureQueries>();
        services.AddScoped<IOpenItemQueries, OpenItemQueries>();
        services.AddScoped<ICashBankAccountQueries, CashBankAccountQueries>();
        services.AddHandlersFromAssembly(typeof(IFinanceDbContext).Assembly);
        services.AddSingleton(new EventAssembly(typeof(OpenItem).Assembly));
        services.AddSingleton(new EventAssembly(typeof(InvoiceSettled).Assembly));
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var g = endpoints.MapGroup("/api/v1/finance").WithTags("Finance").RequireAuthorization().RequireCompany();

        // ---- Cash & bank accounts ----
        foreach (var (path, kind) in new[] { ("cash-accounts", "Cash"), ("bank-accounts", "Bank") })
        {
            g.MapGet($"/{path}", async (IDispatcher d, CancellationToken ct) => (await d.Send(new ListCashBankAccountsQuery(kind), ct)).ToHttpResult())
                .RequirePermission(FinancePermissions.Read);
            g.MapPost($"/{path}", async (CashBankAccountRequest b, IDispatcher d, CancellationToken ct) =>
                    (await d.Send(new CreateCashBankAccountCommand(kind, b.LegalEntityId, b.Code, b.Name, b.Currency, b.GlAccountId, b.BankName, b.AccountNumber, b.Iban), ct))
                    .ToHttpResult(a => Results.Created($"/api/v1/finance/{path}/{a.Id}", a)))
                .RequirePermission(FinancePermissions.AccountManage);
        }

        g.MapGet("/cash-bank-accounts/{id:guid}/movements", async (Guid id, bool? unreconciled, int? page, int? pageSize, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListMovementsQuery(id, unreconciled ?? false, new PageRequest(page ?? 1, pageSize ?? 50)), ct)).ToHttpResult())
            .RequirePermission(FinancePermissions.Read);

        // ---- Receipts & payments ----
        MapSettlements(g, "receipts", SettlementDirection.Receipt, FinancePermissions.ReceiptManage, FinancePermissions.ReceiptPost);
        MapSettlements(g, "payments", SettlementDirection.Payment, FinancePermissions.PaymentManage, FinancePermissions.PaymentPost);

        // ---- Open items & reports ----
        g.MapGet("/open-items", async (string? partyType, Guid? partyId, Guid? legalEntityId, bool? openOnly, int? page, int? pageSize, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListOpenItemsQuery(partyType, partyId, legalEntityId, openOnly ?? true, new PageRequest(page ?? 1, pageSize ?? 50)), ct)).ToHttpResult())
            .RequirePermission(FinancePermissions.Read);
        g.MapGet("/reports/ar-aging", async (Guid legalEntityId, DateOnly asOf, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new AgingQuery(PartyKind.Customer, legalEntityId, asOf), ct)).ToHttpResult())
            .RequirePermission(FinancePermissions.Read);
        g.MapGet("/reports/ap-aging", async (Guid legalEntityId, DateOnly asOf, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new AgingQuery(PartyKind.Supplier, legalEntityId, asOf), ct)).ToHttpResult())
            .RequirePermission(FinancePermissions.Read);
        g.MapGet("/reports/customer-statement", async (Guid customerId, Guid legalEntityId, DateOnly from, DateOnly to, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new StatementQuery(PartyKind.Customer, customerId, legalEntityId, from, to), ct)).ToHttpResult())
            .RequirePermission(FinancePermissions.Read);
        g.MapGet("/reports/supplier-statement", async (Guid supplierId, Guid legalEntityId, DateOnly from, DateOnly to, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new StatementQuery(PartyKind.Supplier, supplierId, legalEntityId, from, to), ct)).ToHttpResult())
            .RequirePermission(FinancePermissions.Read);

        // ---- Internal transfers ----
        g.MapGet("/internal-transfers", async (int? page, int? pageSize, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListTransfersQuery(new PageRequest(page ?? 1, pageSize ?? 50)), ct)).ToHttpResult())
            .RequirePermission(FinancePermissions.Read);
        g.MapPost("/internal-transfers", async (CreateTransferCommand b, IDispatcher d, CancellationToken ct) =>
                (await d.Send(b, ct)).ToHttpResult(t => Results.Created($"/api/v1/finance/internal-transfers/{t.Id}", t)))
            .RequirePermission(FinancePermissions.TransferPost);
        g.MapPost("/internal-transfers/{id:guid}/post", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new PostTransferCommand(id), ct)).ToHttpResult())
            .RequirePermission(FinancePermissions.TransferPost).RequireIdempotency();

        // ---- Expenses ----
        g.MapGet("/expenses", async (int? page, int? pageSize, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListExpensesQuery(new PageRequest(page ?? 1, pageSize ?? 50)), ct)).ToHttpResult())
            .RequirePermission(FinancePermissions.Read);
        g.MapGet("/expenses/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new GetExpenseQuery(id), ct)).ToHttpResult())
            .RequirePermission(FinancePermissions.Read);
        g.MapPost("/expenses", async (CreateExpenseCommand b, IDispatcher d, CancellationToken ct) =>
                (await d.Send(b, ct)).ToHttpResult(e => Results.Created($"/api/v1/finance/expenses/{e.Id}", e)))
            .RequirePermission(FinancePermissions.ExpenseManage);
        g.MapPost("/expenses/{id:guid}/post", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new PostExpenseCommand(id), ct)).ToHttpResult())
            .RequirePermission(FinancePermissions.ExpensePost).RequireIdempotency();

        // ---- Bank reconciliation ----
        g.MapPost("/bank-reconciliations", async (CreateReconciliationCommand b, IDispatcher d, CancellationToken ct) =>
                (await d.Send(b, ct)).ToHttpResult(r => Results.Created($"/api/v1/finance/bank-reconciliations/{r.Id}", r)))
            .RequirePermission(FinancePermissions.ReconciliationManage);
        g.MapGet("/bank-reconciliations/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new GetReconciliationQuery(id), ct)).ToHttpResult())
            .RequirePermission(FinancePermissions.Read);
        g.MapPut("/bank-reconciliations/{id:guid}/lines/{lineId:guid}", async (Guid id, Guid lineId, MatchRequest b, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new MatchStatementLineCommand(id, lineId, b.MovementId), ct)).ToHttpResult())
            .RequirePermission(FinancePermissions.ReconciliationManage);
        g.MapPost("/bank-reconciliations/{id:guid}/complete", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CompleteReconciliationCommand(id), ct)).ToHttpResult())
            .RequirePermission(FinancePermissions.ReconciliationManage);

        // ---- FX revaluation ----
        g.MapGet("/fx-revaluations", async (Guid? legalEntityId, IDispatcher d, CancellationToken ct) => (await d.Send(new ListRevaluationsQuery(legalEntityId), ct)).ToHttpResult())
            .RequirePermission(FinancePermissions.Read);
        g.MapPost("/fx-revaluations", async (RevaluationRequest b, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new RunFxRevaluationCommand(b.LegalEntityId, b.AsOf), ct)).ToHttpResult(r => Results.Created($"/api/v1/finance/fx-revaluations/{r.Id}", r)))
            .RequirePermission(FinancePermissions.RevaluationRun).RequireIdempotency();
    }

    private static void MapSettlements(RouteGroupBuilder g, string path, SettlementDirection direction, string manage, string post)
    {
        g.MapGet($"/{path}", async (Guid? partyId, string? status, int? page, int? pageSize, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListSettlementsQuery(direction, partyId, status, new PageRequest(page ?? 1, pageSize ?? 50)), ct)).ToHttpResult())
            .RequirePermission(FinancePermissions.Read);
        g.MapGet($"/{path}/{{id:guid}}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new GetSettlementQuery(direction, id), ct)).ToHttpResult())
            .RequirePermission(FinancePermissions.Read);
        g.MapPost($"/{path}", async (SettlementRequest b, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CreateSettlementCommand(direction, b.LegalEntityId, b.PartyId, b.Date, b.Method, b.CashBankAccountId, b.Amount, b.ExchangeRate,
                    b.WithholdingTaxGroupId, b.WithholdingBase, b.Allocations, b.Reference, b.Description), ct))
                .ToHttpResult(s => Results.Created($"/api/v1/finance/{path}/{s.Id}", s)))
            .RequirePermission(manage);
        g.MapPost($"/{path}/{{id:guid}}/post", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new PostSettlementCommand(direction, id), ct)).ToHttpResult())
            .RequirePermission(post).RequireIdempotency();
        g.MapPost($"/{path}/{{id:guid}}/allocations", async (Guid id, AdvanceAllocationRequest b, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new AllocateAdvanceCommand(direction, id, b.Date, b.Allocations), ct)).ToHttpResult())
            .RequirePermission(FinancePermissions.AllocationManage).RequireIdempotency();
    }
}
