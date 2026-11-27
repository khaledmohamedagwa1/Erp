using Erp.Application;
using Erp.Application.Abstractions;
using Erp.Application.Messaging;
using Erp.Infrastructure;
using Erp.Modules.Pos.Application;
using Erp.Modules.Pos.Contracts;
using Erp.Web.Http;
using Erp.Web.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Modules.Pos.Infrastructure;

public sealed record OpenShiftBody(Guid TerminalId, decimal OpeningFloat, DateOnly? BusinessDate);

public sealed record CashMovementBody(string Kind, decimal Amount, string Reason);

public sealed record CloseShiftBody(IReadOnlyList<CountInput>? Counts);

public sealed record SubmitReceiptBody(
    Guid TerminalId,
    Guid ClientReceiptId,
    string? PriceVersion,
    decimal? ExpectedGross,
    Guid? CustomerId,
    IReadOnlyList<PosLineInput> Lines,
    IReadOnlyList<PosPaymentInput> Payments);

public sealed record RefundBody(Guid ClientReceiptId, IReadOnlyList<RefundLineInput> Lines, IReadOnlyList<PosPaymentInput>? Payments, Guid? TerminalId);

public sealed record OfflineRangeBody(int Size);

public sealed record OfflineSyncBody(IReadOnlyList<OfflineReceiptInput> Receipts);

public sealed record OfflineSyncResultDto(int Accepted, int Replayed, int Rejected, IReadOnlyList<OfflineReceiptResultDto> Results);

public sealed class PosModule : IErpModule
{
    public const string Code = "pos";

    public ModuleManifest Manifest { get; } = new(
        Code,
        "Point of sale",
        "نقاط البيع",
        IsCore: false,
        DependsOn: ["sales", "inventory", "accounting", "finance"],
        Permissions:
        [
            new(PosPermissions.Read, Code, "View POS terminals, shifts and receipts", "عرض نقاط البيع والورديات والإيصالات"),
            new(PosPermissions.TerminalManage, Code, "Set up POS terminals and register devices", "إعداد نقاط البيع وتسجيل الأجهزة", IsSensitive: true),
            new(PosPermissions.ShiftManage, Code, "Open, count and close cashier shifts; X/Z reports", "فتح وجرد وإغلاق ورديات الكاشير وتقارير X/Z"),
            new(PosPermissions.Sell, Code, "Ring up POS sales", "تسجيل مبيعات نقاط البيع"),
            new(PosPermissions.Refund, Code, "Refund POS receipts", "رد مبالغ إيصالات نقاط البيع", IsSensitive: true),
            new(PosPermissions.Discount, Code, "Give POS discounts (up to the terminal maximum)", "منح خصومات نقاط البيع (حتى الحد الأقصى للنقطة)", IsSensitive: true),
        ],
        Menu:
        [
            new("pos.sell", "Point of sale", "نقطة البيع", "/pos", "pos", 10, PosPermissions.Sell),
            new("pos.shifts", "Cashier shifts", "ورديات الكاشير", "/pos/shifts", "pos", 20, PosPermissions.Read),
            new("pos.terminals", "POS terminals", "نقاط البيع", "/pos/terminals", "pos", 30, PosPermissions.TerminalManage),
        ]);

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddErpDbContext<PosDbContext>(PosDbContext.SchemaName, order: 120);
        services.AddScoped<IPosDbContext>(sp => sp.GetRequiredService<PosDbContext>());
        services.AddScoped<IPosLocks, PosLocks>();
        services.AddScoped<PosAccounts>();
        services.AddScoped<PosPricing>();
        services.AddScoped<TerminalValidator>();
        services.AddScoped<ShiftTotals>();
        services.AddScoped<ReceiptPosting>();
        services.AddHandlersFromAssembly(typeof(IPosDbContext).Assembly);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var g = endpoints.MapGroup("/api/v1/pos").WithTags("POS").RequireAuthorization().RequireCompany().RequireModule(Code);

        // ---- Terminals ----
        g.MapGet("/terminals", async (bool? includeInactive, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListTerminalsQuery(includeInactive ?? false), ct)).ToHttpResult())
            .RequirePermission(PosPermissions.Read);
        g.MapGet("/terminals/{id:guid}", async (Guid id, HttpContext http, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new GetTerminalQuery(id), ct)).ToHttpResult(t => WithETag(http, t.Version, t)))
            .RequirePermission(PosPermissions.Read);
        g.MapPost("/terminals", async (CreateTerminalCommand b, IDispatcher d, CancellationToken ct) =>
                (await d.Send(b, ct)).ToHttpResult(t => Results.Created($"/api/v1/pos/terminals/{t.Id}", t)))
            .RequirePermission(PosPermissions.TerminalManage);
        g.MapPut("/terminals/{id:guid}", async (Guid id, TerminalInput b, HttpContext http, IDispatcher d, CancellationToken ct) =>
            {
                var ifMatch = ConcurrencyHeaders.ReadIfMatch(http.Request);
                return ifMatch.IsFailure
                    ? ifMatch.Error.ToProblem()
                    : (await d.Send(new UpdateTerminalCommand(id, b, ifMatch.Value), ct)).ToHttpResult(t => WithETag(http, t.Version, t));
            })
            .RequirePermission(PosPermissions.TerminalManage);
        g.MapPost("/terminals/{id:guid}/deactivate", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new SetTerminalActiveCommand(id, false), ct)).ToHttpResult())
            .RequirePermission(PosPermissions.TerminalManage);
        g.MapPost("/terminals/{id:guid}/activate", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new SetTerminalActiveCommand(id, true), ct)).ToHttpResult())
            .RequirePermission(PosPermissions.TerminalManage);
        g.MapPost("/terminals/{id:guid}/register-device", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new RegisterDeviceCommand(id), ct)).ToHttpResult())
            .RequirePermission(PosPermissions.TerminalManage).RequireIdempotency();
        g.MapGet("/terminals/{id:guid}/catalog-snapshot", async (Guid id, DateOnly? date, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new GetCatalogSnapshotQuery(id, date), ct)).ToHttpResult())
            .RequirePermission(PosPermissions.Sell);

        // ---- Shifts ----
        g.MapGet("/shifts", async (Guid? terminalId, string? status, DateOnly? from, DateOnly? to, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListShiftsQuery(terminalId, status, from, to), ct)).ToHttpResult())
            .RequirePermission(PosPermissions.Read);
        g.MapGet("/shifts/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new GetShiftQuery(id), ct)).ToHttpResult())
            .RequirePermission(PosPermissions.Read);
        g.MapPost("/shifts", async (OpenShiftBody b, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new OpenShiftCommand(b.TerminalId, b.OpeningFloat, b.BusinessDate), ct)).ToHttpResult(s => Results.Created($"/api/v1/pos/shifts/{s.Id}", s)))
            .RequirePermission(PosPermissions.ShiftManage).RequireIdempotency();
        g.MapPost("/shifts/{id:guid}/cash-movements", async (Guid id, CashMovementBody b, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new AddCashMovementCommand(id, b.Kind, b.Amount, b.Reason), ct)).ToHttpResult())
            .RequirePermission(PosPermissions.ShiftManage).RequireIdempotency();
        g.MapPost("/shifts/{id:guid}/start-close", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new StartCloseShiftCommand(id), ct)).ToHttpResult())
            .RequirePermission(PosPermissions.ShiftManage).RequireIdempotency();
        g.MapPost("/shifts/{id:guid}/close", async (Guid id, CloseShiftBody b, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CloseShiftCommand(id, b.Counts ?? []), ct)).ToHttpResult())
            .RequirePermission(PosPermissions.ShiftManage).RequireIdempotency();
        g.MapGet("/shifts/{id:guid}/x-report", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ShiftReportQuery(id, Final: false), ct)).ToHttpResult())
            .RequirePermission(PosPermissions.ShiftManage);
        g.MapGet("/shifts/{id:guid}/z-report", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ShiftReportQuery(id, Final: true), ct)).ToHttpResult())
            .RequirePermission(PosPermissions.Read);
        g.MapGet("/shifts/{id:guid}/receipts", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ListShiftReceiptsQuery(id), ct)).ToHttpResult())
            .RequirePermission(PosPermissions.Read);

        // ---- Receipts ----
        g.MapPost("/receipts", async (SubmitReceiptBody b, [FromHeader(Name = PosDevice.HeaderName)] string? deviceKey, IDispatcher d, CancellationToken ct) =>
            {
                var result = await d.Send(new SubmitReceiptCommand(b.TerminalId, b.ClientReceiptId, b.PriceVersion, b.ExpectedGross, b.CustomerId, b.Lines ?? [],
                    b.Payments ?? [], deviceKey), ct);
                if (result.IsFailure)
                {
                    return result.Error.ToProblem();
                }

                var outcome = result.Value;
                if (outcome.Repriced is not null)
                {
                    var error = PosAppErrors.PriceVersionStale;
                    return Results.Problem(title: ErrorLocalizer.Title(error.Code, error.Message), detail: error.Message, statusCode: StatusCodes.Status409Conflict,
                        type: $"https://errors.erp.local/{error.Code}", extensions: new Dictionary<string, object?> { ["code"] = error.Code, ["repriced"] = outcome.Repriced });
                }

                return outcome.Receipt!.Replayed ? Results.Ok(outcome.Receipt) : Results.Created($"/api/v1/pos/receipts/{outcome.Receipt.Id}", outcome.Receipt);
            })
            .RequirePermission(PosPermissions.Sell).RequireIdempotency();
        g.MapGet("/receipts/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new GetReceiptQuery(id), ct)).ToHttpResult())
            .RequirePermission(PosPermissions.Read);
        g.MapPost("/receipts/{id:guid}/refund", async (Guid id, RefundBody b, [FromHeader(Name = PosDevice.HeaderName)] string? deviceKey, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new RefundReceiptCommand(id, b.ClientReceiptId, b.Lines ?? [], b.Payments, b.TerminalId, deviceKey), ct))
                .ToHttpResult(r => r.Replayed ? Results.Ok(r) : Results.Created($"/api/v1/pos/receipts/{r.Id}", r)))
            .RequirePermission(PosPermissions.Refund).RequireIdempotency();

        // ---- Offline mode ----
        g.MapPost("/terminals/{id:guid}/offline-range", async (Guid id, OfflineRangeBody b, [FromHeader(Name = PosDevice.HeaderName)] string? deviceKey, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new AllocateOfflineRangeCommand(id, b.Size, deviceKey), ct)).ToHttpResult())
            .RequirePermission(PosPermissions.Sell).RequireIdempotency();

        // Each receipt is posted in its own transaction (idempotent per client receipt id), so one rejected receipt never blocks the others.
        g.MapPost("/terminals/{id:guid}/offline-sync", async (Guid id, OfflineSyncBody b, [FromHeader(Name = PosDevice.HeaderName)] string? deviceKey, IDispatcher d,
                IEnumerable<IChangeTrackerReset> resets, CancellationToken ct) =>
            {
                var results = new List<OfflineReceiptResultDto>();
                foreach (var receipt in (b.Receipts ?? []).OrderBy(r => r.CreatedAtUtc))
                {
                    var result = await d.Send(new SubmitOfflineReceiptCommand(id, receipt, deviceKey), ct);
                    foreach (var reset in resets)
                    {
                        reset.ClearTrackedChanges(); // a rolled-back receipt must not leak into the next one
                    }

                    if (result.IsFailure && result.Error.Code is "pos.terminal-not-found" or "pos.device-key-invalid")
                    {
                        return result.Error.ToProblem();
                    }

                    results.Add(result.IsSuccess
                        ? result.Value
                        : new OfflineReceiptResultDto(receipt.ClientReceiptId, receipt.OfflineNumber, "Rejected", null,
                            [new OfflineConflictDto(result.Error.Code, result.Error.Message)]));
                }

                return Results.Ok(new OfflineSyncResultDto(results.Count(r => r.Status == "Accepted"), results.Count(r => r.Status == "Replayed"),
                    results.Count(r => r.Status == "Rejected"), results));
            })
            .RequirePermission(PosPermissions.Sell);
    }

    private static IResult WithETag<T>(HttpContext http, string version, T body)
    {
        http.Response.Headers.ETag = $"\"{version}\"";
        return Results.Ok(body);
    }
}
