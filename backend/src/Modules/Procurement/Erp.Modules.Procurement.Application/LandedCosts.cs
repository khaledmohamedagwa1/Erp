using Erp.Application.Abstractions;
using Erp.Application.Messaging;
using Erp.Application.Paging;
using Erp.Modules.Accounting.Contracts;
using Erp.Modules.Finance.Contracts;
using Erp.Modules.Inventory.Contracts;
using Erp.Modules.Parties.Contracts;
using Erp.Modules.Procurement.Contracts;
using Erp.Modules.Procurement.Domain;
using Erp.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Procurement.Application;

public sealed record LandedCostChargeDto(Guid LineId, string Description, decimal Amount, Guid? TaxGroupId);

public sealed record LandedCostAllocationDto(Guid GoodsReceiptId, Guid GoodsReceiptLineId, Guid ProductId, Guid WarehouseId, decimal Quantity, decimal Value,
    decimal AllocatedBase, decimal AppliedToInventory, decimal ToVariance);

public sealed record LandedCostDto(Guid Id, Guid LegalEntityId, Guid? SupplierId, string? SupplierReference, string? Number, DateOnly Date, string Currency,
    decimal ExchangeRate, string Basis, string Status, decimal ChargesTotal, decimal GrossTotal, Guid? JournalId, IReadOnlyList<Guid> GoodsReceiptIds,
    IReadOnlyList<LandedCostChargeDto> Charges, IReadOnlyList<LandedCostAllocationDto> Allocations);

internal static class LandedCostMapping
{
    public static LandedCostDto ToDto(LandedCost c) => new(c.Id, c.LegalEntityId, c.SupplierId, c.SupplierReference, c.Number, c.Date, c.Currency, c.ExchangeRate,
        c.Basis.ToString(), c.Status.ToString(), c.ChargesTotal, c.GrossTotal, c.JournalId, c.GoodsReceiptIds,
        c.Charges.Select(x => new LandedCostChargeDto(x.LineId, x.Description, x.Amount, x.TaxGroupId)).ToList(),
        c.Allocations.Select(a => new LandedCostAllocationDto(a.GoodsReceiptId, a.GoodsReceiptLineId, a.ProductId, a.WarehouseId, a.Quantity, a.Value,
            a.AllocatedBase, a.AppliedToInventory, a.ToVariance)).ToList());
}

public sealed record LandedCostChargeInput(string Description, decimal Amount, Guid? TaxGroupId);

public sealed record CreateLandedCostCommand(Guid LegalEntityId, Guid? SupplierId, string? SupplierReference, DateOnly Date, string? Currency, decimal? ExchangeRate,
    string Basis, IReadOnlyList<LandedCostChargeInput> Charges, IReadOnlyList<Guid> GoodsReceiptIds) : ICommand<LandedCostDto>;

public sealed class CreateLandedCostValidator : AbstractValidator<CreateLandedCostCommand>
{
    public CreateLandedCostValidator()
    {
        RuleFor(x => x.LegalEntityId).NotEmpty();
        RuleFor(x => x.Basis).Must(b => Enum.TryParse<AllocationBasis>(b, true, out var basis) && Enum.IsDefined(basis)).WithMessage("Basis must be Value or Quantity.");
        RuleFor(x => x.Charges).NotEmpty();
        RuleFor(x => x.GoodsReceiptIds).NotEmpty();
        RuleForEach(x => x.Charges).ChildRules(c =>
        {
            c.RuleFor(x => x.Description).NotEmpty().MaximumLength(200);
            c.RuleFor(x => x.Amount).GreaterThan(0);
        });
    }
}

public sealed class CreateLandedCostHandler(IProcurementDbContext db, ProcurementScope scope, IPartyQueries parties, ITenantContext tenant)
    : ICommandHandler<CreateLandedCostCommand, LandedCostDto>
{
    public async Task<Result<LandedCostDto>> Handle(CreateLandedCostCommand command, CancellationToken cancellationToken)
    {
        var le = await scope.LegalEntityAsync(command.LegalEntityId, cancellationToken);
        if (le.IsFailure)
        {
            return le.Error;
        }

        var currency = le.Value.FunctionalCurrency;
        if (command.SupplierId is { } supplierId)
        {
            var supplier = await parties.GetSupplierAsync(supplierId, cancellationToken);
            if (supplier is not { IsActive: true })
            {
                return ProcurementAppErrors.SupplierNotFound;
            }

            currency = string.IsNullOrWhiteSpace(command.Currency) ? supplier.Currency : command.Currency.Trim().ToUpperInvariant();
        }
        else if (!string.IsNullOrWhiteSpace(command.Currency))
        {
            currency = command.Currency.Trim().ToUpperInvariant();
        }

        var rate = await scope.RateAsync(currency, le.Value.FunctionalCurrency, command.Date, command.ExchangeRate, cancellationToken);
        if (rate.IsFailure)
        {
            return rate.Error;
        }

        var ids = command.GoodsReceiptIds.Distinct().ToList();
        var receipts = await db.Receipts.AsNoTracking().Where(r => ids.Contains(r.Id)).ToListAsync(cancellationToken);
        if (receipts.Count != ids.Count)
        {
            return ProcurementAppErrors.NotFound;
        }

        if (receipts.Any(r => r.Status != GoodsReceiptStatus.Posted))
        {
            return ProcurementAppErrors.ReceiptNotPosted;
        }

        if (receipts.Any(r => r.LegalEntityId != le.Value.Id))
        {
            return ProcurementAppErrors.SupplierMismatch;
        }

        var landedCost = new LandedCost(tenant.CompanyId!.Value, le.Value.Id, command.SupplierId, command.SupplierReference, command.Date, currency, rate.Value,
            Enum.Parse<AllocationBasis>(command.Basis, true), command.Charges.Select(c => new LandedCostCharge(Guid.Empty, c.Description.Trim(), c.Amount, command.SupplierId is null ? null : c.TaxGroupId)),
            ids);
        db.LandedCosts.Add(landedCost);
        await db.SaveChangesAsync(cancellationToken);
        return LandedCostMapping.ToDto(landedCost);
    }
}

/// <summary>Allocates the charges over the receipt lines and returns the allocation without posting.</summary>
public sealed record PreviewLandedCostQuery(Guid Id) : IQuery<IReadOnlyList<LandedCostAllocationDto>>;

public sealed class PreviewLandedCostHandler(IProcurementDbContext db) : IQueryHandler<PreviewLandedCostQuery, IReadOnlyList<LandedCostAllocationDto>>
{
    public async Task<Result<IReadOnlyList<LandedCostAllocationDto>>> Handle(PreviewLandedCostQuery query, CancellationToken cancellationToken)
    {
        var landedCost = await db.LandedCosts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == query.Id, cancellationToken);
        if (landedCost is null)
        {
            return ProcurementAppErrors.NotFound;
        }

        var receipts = await db.Receipts.AsNoTracking().Where(r => landedCost.GoodsReceiptIds.Contains(r.Id)).ToListAsync(cancellationToken);
        var allocations = LandedCostAllocations.Allocate(receipts, Money.Round(landedCost.ChargesTotal * landedCost.ExchangeRate), landedCost.Basis);
        return allocations.IsFailure
            ? allocations.Error
            : allocations.Value.Select(a => new LandedCostAllocationDto(a.GoodsReceiptId, a.GoodsReceiptLineId, a.ProductId, a.WarehouseId, a.Quantity, a.Value,
                a.AllocatedBase, 0, 0)).ToList();
    }
}

internal static class LandedCostAllocations
{
    public static Result<IReadOnlyList<LandedCostAllocation>> Allocate(IReadOnlyList<GoodsReceipt> receipts, decimal totalBase, AllocationBasis basis)
    {
        var lines = receipts.SelectMany(r => r.Lines.Where(l => l.MovementId is not null && l.AcceptedQuantity > 0).Select(l => (Receipt: r, Line: l))).ToList();
        var weights = lines.Select(x => (x.Line.BaseQuantity, x.Line.BaseValue)).ToList();
        if (lines.Count == 0 || weights.Sum(w => basis == AllocationBasis.Value ? w.BaseValue : w.BaseQuantity) <= 0)
        {
            return Error.Validation("procurement.nothing-to-allocate", "The receipts have no posted lines with value or quantity to allocate to.");
        }

        var shares = Allocator.LandedCost(totalBase, weights, basis);
        return lines.Select((x, i) => new LandedCostAllocation(x.Receipt.Id, x.Line.LineId, x.Line.MovementId!.Value, x.Line.ProductId, x.Receipt.WarehouseId,
            x.Line.BaseQuantity, x.Line.BaseValue, shares[i])).ToList();
    }
}

/// <summary>
/// Posts landed cost: charges (functional) allocated over the receipt lines; the on-hand share is capitalised into inventory
/// (cost adjustment), the share of consumed stock goes to purchase price variance; Cr Supplier payable (with an open item) or
/// Landed cost clearing when no supplier is given. Recoverable tax on supplier charges is debited to Input tax.
/// </summary>
public sealed record PostLandedCostCommand(Guid Id) : ICommand<LandedCostDto>;

public sealed class PostLandedCostHandler(
    IProcurementDbContext db,
    ProcurementScope scope,
    ProductLookup products,
    PurchaseTax taxes,
    IPartyQueries parties,
    IInventoryPostingService inventory,
    IJournalPostingService journals,
    ISubledgerService subledger,
    IDocumentNumberGenerator numbers) : ICommandHandler<PostLandedCostCommand, LandedCostDto>
{
    public async Task<Result<LandedCostDto>> Handle(PostLandedCostCommand command, CancellationToken cancellationToken)
    {
        var landedCost = await db.LandedCosts.FirstOrDefaultAsync(c => c.Id == command.Id, cancellationToken);
        if (landedCost is null)
        {
            return ProcurementAppErrors.NotFound;
        }

        if (landedCost.Status == LandedCostStatus.Posted)
        {
            return LandedCostMapping.ToDto(landedCost);
        }

        if (landedCost.Status != LandedCostStatus.Draft)
        {
            return ProcurementErrors.InvalidState("landed cost", landedCost.Status);
        }

        var le = await scope.LegalEntityAsync(landedCost.LegalEntityId, cancellationToken);
        if (le.IsFailure)
        {
            return le.Error;
        }

        SupplierInfo? supplier = null;
        if (landedCost.SupplierId is { } supplierId)
        {
            supplier = await parties.GetSupplierAsync(supplierId, cancellationToken);
            if (supplier is null)
            {
                return ProcurementAppErrors.SupplierNotFound;
            }
        }

        // Transaction-currency buckets: cost per charge (net + non-recoverable tax), recoverable tax per code.
        var buckets = new List<(Guid? TaxCodeId, decimal Amount)>();
        foreach (var charge in landedCost.Charges)
        {
            var tax = await taxes.CalculateAsync(charge.TaxGroupId, charge.Amount, landedCost.Currency, landedCost.Date, cancellationToken);
            if (tax.IsFailure)
            {
                return tax.Error;
            }

            buckets.Add((null, charge.Amount + tax.Value.NonRecoverable));
            buckets.AddRange(tax.Value.Lines.Where(t => t.Recoverable && t.Amount != 0).GroupBy(t => t.TaxCodeId).Select(g => ((Guid?)g.Key, g.Sum(t => t.Amount))));
        }

        var grossTotal = buckets.Sum(b => b.Amount);
        var baseGross = Money.Round(grossTotal * landedCost.ExchangeRate);
        var parts = BaseAmounts.FitToTotal(buckets.Select(b => b.Amount).ToList(), landedCost.ExchangeRate, baseGross);
        var costBase = parts.Where((_, i) => buckets[i].TaxCodeId is null).Sum();

        var receipts = await db.Receipts.AsNoTracking().Where(r => landedCost.GoodsReceiptIds.Contains(r.Id)).ToListAsync(cancellationToken);
        var allocations = LandedCostAllocations.Allocate(receipts, costBase, landedCost.Basis);
        if (allocations.IsFailure)
        {
            return allocations.Error;
        }

        var catalog = await products.LoadAsync(allocations.Value.Select(a => a.ProductId), cancellationToken);
        if (catalog.IsFailure)
        {
            return catalog.Error;
        }

        var journal = new JournalBuilder();
        var posted = new List<LandedCostAllocation>();
        foreach (var allocation in allocations.Value)
        {
            var product = catalog.Value[allocation.ProductId];
            decimal applied = 0, variance = allocation.AllocatedBase;
            if (allocation.AllocatedBase != 0)
            {
                var adjusted = await inventory.AdjustCostAsync(new CostAdjustmentRequest(allocation.ReceiptMovementId, allocation.AllocatedBase, landedCost.Date,
                    SourceModules.Procurement, ProcurementDocumentTypes.LandedCost, landedCost.Id, allocation.GoodsReceiptLineId), cancellationToken);
                if (adjusted.IsFailure)
                {
                    return adjusted.Error;
                }

                (applied, variance) = (adjusted.Value.AppliedToInventory, adjusted.Value.ToVariance);
            }

            journal.Add(PostingRoles.Inventory, applied, ProcurementPostingContext.Stock(product, allocation.WarehouseId), product.Sku);
            journal.Add(PostingRoles.PurchasePriceVariance, variance, ProcurementPostingContext.Product(product), product.Sku);
            posted.Add(allocation with { AppliedToInventory = applied, ToVariance = variance });
        }

        for (var i = 0; i < buckets.Count; i++)
        {
            if (buckets[i].TaxCodeId is { } taxCodeId)
            {
                journal.Add(PostingRoles.InputTax, parts[i], ProcurementPostingContext.Tax(taxCodeId));
            }
        }

        var number = await numbers.NextAsync(new NumberScope(landedCost.CompanyId, "LandedCost", "LC", landedCost.LegalEntityId, FiscalYear: landedCost.Date.Year),
            cancellationToken);
        if (supplier is not null)
        {
            journal.Add(PostingRoles.SupplierPayable, -baseGross, ProcurementPostingContext.Party(supplier.PostingGroup), landedCost.SupplierReference ?? number,
                nameof(PartyKind.Supplier), supplier.Id);
        }
        else
        {
            journal.Add(PostingRoles.LandedCostClearing, -baseGross, description: number);
        }

        var journalResult = await journals.PostAsync(journal.Build(landedCost.LegalEntityId, landedCost.Date, ProcurementDocumentTypes.LandedCost, landedCost.Id,
            "landed-cost", $"Landed cost {number}", le.Value.FunctionalCurrency), cancellationToken);
        if (journalResult.IsFailure)
        {
            return journalResult.Error;
        }

        if (supplier is not null)
        {
            var items = await subledger.CreateOpenItemsAsync(new OpenItemRequest(PartyKind.Supplier, supplier.Id, landedCost.LegalEntityId, SourceModules.Procurement,
                ProcurementDocumentTypes.LandedCost, landedCost.Id, number, landedCost.Date, landedCost.Currency, landedCost.ExchangeRate, grossTotal,
                supplier.PaymentTermsId), cancellationToken);
            if (items.IsFailure)
            {
                return items.Error;
            }
        }

        landedCost.MarkPosted(number, posted, grossTotal, journalResult.Value.JournalId);
        await db.SaveChangesAsync(cancellationToken);
        return LandedCostMapping.ToDto(landedCost);
    }
}

public sealed record CancelLandedCostCommand(Guid Id) : ICommand<LandedCostDto>;

public sealed class CancelLandedCostHandler(IProcurementDbContext db) : ICommandHandler<CancelLandedCostCommand, LandedCostDto>
{
    public async Task<Result<LandedCostDto>> Handle(CancelLandedCostCommand command, CancellationToken cancellationToken)
    {
        var landedCost = await db.LandedCosts.FirstOrDefaultAsync(c => c.Id == command.Id, cancellationToken);
        if (landedCost is null)
        {
            return ProcurementAppErrors.NotFound;
        }

        landedCost.Cancel();
        await db.SaveChangesAsync(cancellationToken);
        return LandedCostMapping.ToDto(landedCost);
    }
}

public sealed record GetLandedCostQuery(Guid Id) : IQuery<LandedCostDto>;

public sealed class GetLandedCostHandler(IProcurementDbContext db) : IQueryHandler<GetLandedCostQuery, LandedCostDto>
{
    public async Task<Result<LandedCostDto>> Handle(GetLandedCostQuery query, CancellationToken cancellationToken)
    {
        var landedCost = await db.LandedCosts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == query.Id, cancellationToken);
        return landedCost is null ? ProcurementAppErrors.NotFound : LandedCostMapping.ToDto(landedCost);
    }
}

public sealed record ListLandedCostsQuery(PageRequest Page) : IQuery<PagedResult<DocumentSummaryDto>>;

public sealed class ListLandedCostsHandler(IProcurementDbContext db) : IQueryHandler<ListLandedCostsQuery, PagedResult<DocumentSummaryDto>>
{
    public async Task<Result<PagedResult<DocumentSummaryDto>>> Handle(ListLandedCostsQuery query, CancellationToken cancellationToken)
    {
        var source = db.LandedCosts.AsNoTracking();
        var total = await source.CountAsync(cancellationToken);
        var items = await source.OrderByDescending(c => c.Date).Skip(query.Page.Skip).Take(query.Page.SafePageSize)
            .Select(c => new DocumentSummaryDto(c.Id, c.Number, c.Date, c.SupplierId, c.Status.ToString(), c.GrossTotal, c.Currency)).ToListAsync(cancellationToken);
        return new PagedResult<DocumentSummaryDto>(items, query.Page.SafePage, query.Page.SafePageSize, total);
    }
}
