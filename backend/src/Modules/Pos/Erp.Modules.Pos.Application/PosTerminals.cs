using Erp.Application.Abstractions;
using Erp.Application.Messaging;
using Erp.Modules.Catalog.Contracts;
using Erp.Modules.Finance.Contracts;
using Erp.Modules.Organization.Contracts;
using Erp.Modules.Parties.Contracts;
using Erp.Modules.Pos.Domain;
using Erp.Modules.Tax.Contracts;
using Erp.SharedKernel.Domain;
using Erp.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Pos.Application;

// ============================ Terminals ============================

public sealed record TerminalInput(
    string Name,
    Guid WarehouseId,
    Guid PriceListId,
    Guid CashAccountId,
    Guid? CardAccountId = null,
    Guid? WalletAccountId = null,
    decimal MaxDiscountPercent = 0,
    string? StalePricePolicy = null,
    Guid? DefaultCustomerId = null);

public sealed record TerminalSetting(Guid LegalEntityId, Guid? BranchId, string Currency, Guid DefaultCustomerId);

/// <summary>Validates where a terminal sells from: physical warehouse → legal entity; price list and accounts in its functional currency.</summary>
public sealed class TerminalValidator(
    ITenantContext tenant,
    IOrganizationQueries organization,
    IPriceListSnapshotQueries priceLists,
    ICashBankAccountQueries accounts,
    IPartyQueries parties,
    IClock clock)
{
    public async Task<Result<TerminalSetting>> ValidateAsync(TerminalInput input, CancellationToken ct)
    {
        var warehouse = (await organization.GetWarehousesAsync([input.WarehouseId], ct)).FirstOrDefault();
        if (warehouse is null || !warehouse.IsActive || warehouse.CompanyId != tenant.CompanyId || warehouse.Type is "Transit")
        {
            return PosAppErrors.WarehouseInvalid;
        }

        var le = await organization.GetLegalEntityAsync(warehouse.LegalEntityId, ct);
        if (le is null)
        {
            return PosAppErrors.WarehouseInvalid;
        }

        var priceList = await priceLists.GetSnapshotAsync(input.PriceListId, DateOnly.FromDateTime(clock.UtcNow), ct);
        if (priceList is null)
        {
            return PosAppErrors.PriceListInvalid;
        }

        if (!string.Equals(priceList.Currency, le.FunctionalCurrency, StringComparison.OrdinalIgnoreCase))
        {
            return PosAppErrors.CurrencyUnsupported;
        }

        var cash = await accounts.GetAsync(input.CashAccountId, ct);
        if (cash is not { IsActive: true, Kind: "Cash" } || cash.LegalEntityId != le.Id || !string.Equals(cash.Currency, le.FunctionalCurrency, StringComparison.OrdinalIgnoreCase))
        {
            return PosAppErrors.CashAccountInvalid;
        }

        foreach (var id in new[] { input.CardAccountId, input.WalletAccountId })
        {
            if (id is null)
            {
                continue;
            }

            var account = await accounts.GetAsync(id.Value, ct);
            if (account is not { IsActive: true } || account.LegalEntityId != le.Id || !string.Equals(account.Currency, le.FunctionalCurrency, StringComparison.OrdinalIgnoreCase))
            {
                return PosAppErrors.PaymentAccountInvalid;
            }
        }

        // The walk-in customer is resolved (and created if needed) here, once, not by concurrent first receipts.
        Guid customerId;
        if (input.DefaultCustomerId is { } requested)
        {
            if (await parties.GetCustomerAsync(requested, ct) is not { IsActive: true })
            {
                return PosAppErrors.CustomerNotFound;
            }

            customerId = requested;
        }
        else
        {
            customerId = await parties.GetWalkInCustomerIdAsync(tenant.CompanyId ?? Guid.Empty, ct);
        }

        return new TerminalSetting(le.Id, warehouse.BranchId, le.FunctionalCurrency.ToUpperInvariant(), customerId);
    }
}

public sealed record ListTerminalsQuery(bool IncludeInactive = false) : IQuery<IReadOnlyList<TerminalDto>>;

public sealed class ListTerminalsHandler(IPosDbContext db) : IQueryHandler<ListTerminalsQuery, IReadOnlyList<TerminalDto>>
{
    public async Task<Result<IReadOnlyList<TerminalDto>>> Handle(ListTerminalsQuery q, CancellationToken ct)
    {
        var terminals = await db.Terminals.Where(t => q.IncludeInactive || t.IsActive).OrderBy(t => t.Code).ToListAsync(ct);
        return terminals.Select(t => PosMapping.ToDto(db, t)).ToList();
    }
}

public sealed record GetTerminalQuery(Guid Id) : IQuery<TerminalDto>;

public sealed class GetTerminalHandler(IPosDbContext db) : IQueryHandler<GetTerminalQuery, TerminalDto>
{
    public async Task<Result<TerminalDto>> Handle(GetTerminalQuery q, CancellationToken ct)
    {
        var terminal = await db.Terminals.FirstOrDefaultAsync(t => t.Id == q.Id, ct);
        return terminal is null ? PosAppErrors.TerminalNotFound : PosMapping.ToDto(db, terminal);
    }
}

public sealed record CreateTerminalCommand(
    string Code,
    string Name,
    Guid WarehouseId,
    Guid PriceListId,
    Guid CashAccountId,
    Guid? CardAccountId = null,
    Guid? WalletAccountId = null,
    decimal MaxDiscountPercent = 0,
    string? StalePricePolicy = null,
    Guid? DefaultCustomerId = null) : ICommand<TerminalDto>;

public sealed class CreateTerminalHandler(IPosDbContext db, ITenantContext tenant, TerminalValidator validator) : ICommandHandler<CreateTerminalCommand, TerminalDto>
{
    public async Task<Result<TerminalDto>> Handle(CreateTerminalCommand c, CancellationToken ct)
    {
        var input = new TerminalInput(c.Name, c.WarehouseId, c.PriceListId, c.CashAccountId, c.CardAccountId, c.WalletAccountId, c.MaxDiscountPercent, c.StalePricePolicy, c.DefaultCustomerId);
        var setting = await validator.ValidateAsync(input, ct);
        if (setting.IsFailure)
        {
            return setting.Error;
        }

        var code = (c.Code ?? string.Empty).Trim().ToUpperInvariant();
        if (await db.Terminals.AnyAsync(t => t.Code == code, ct))
        {
            return PosAppErrors.TerminalCodeTaken;
        }

        PosTerminal terminal;
        try
        {
            terminal = new PosTerminal(tenant.CompanyId ?? Guid.Empty, setting.Value.LegalEntityId, setting.Value.BranchId, c.WarehouseId, code, c.Name, c.PriceListId,
                setting.Value.Currency, c.CashAccountId, c.CardAccountId, c.WalletAccountId, c.MaxDiscountPercent, c.StalePricePolicy ?? StalePricePolicies.Reject,
                setting.Value.DefaultCustomerId);
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }

        db.Terminals.Add(terminal);
        await db.SaveChangesAsync(ct);
        return PosMapping.ToDto(db, terminal);
    }
}

public sealed record UpdateTerminalCommand(Guid Id, TerminalInput Input, byte[] ExpectedVersion) : ICommand<TerminalDto>;

public sealed class UpdateTerminalHandler(IPosDbContext db, TerminalValidator validator) : ICommandHandler<UpdateTerminalCommand, TerminalDto>
{
    public async Task<Result<TerminalDto>> Handle(UpdateTerminalCommand c, CancellationToken ct)
    {
        var terminal = await db.Terminals.FirstOrDefaultAsync(t => t.Id == c.Id, ct);
        if (terminal is null)
        {
            return PosAppErrors.TerminalNotFound;
        }

        if (!PosMapping.VersionMatches(db, terminal, c.ExpectedVersion))
        {
            return new Error("concurrency-conflict", "The terminal was changed by someone else. Reload and try again.", ErrorType.PreconditionFailed);
        }

        var setting = await validator.ValidateAsync(c.Input, ct);
        if (setting.IsFailure)
        {
            return setting.Error;
        }

        var relocating = setting.Value.LegalEntityId != terminal.LegalEntityId || c.Input.WarehouseId != terminal.WarehouseId || c.Input.PriceListId != terminal.PriceListId;
        if (relocating && await db.Shifts.AnyAsync(s => s.TerminalId == terminal.Id && s.Status != ShiftStatus.Closed, ct))
        {
            return PosAppErrors.TerminalHasOpenShift;
        }

        try
        {
            terminal.Update(setting.Value.LegalEntityId, setting.Value.BranchId, c.Input.WarehouseId, c.Input.Name, c.Input.PriceListId, setting.Value.Currency,
                c.Input.CashAccountId, c.Input.CardAccountId, c.Input.WalletAccountId, c.Input.MaxDiscountPercent, c.Input.StalePricePolicy ?? terminal.StalePricePolicy,
                setting.Value.DefaultCustomerId);
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }

        await db.SaveChangesAsync(ct);
        return PosMapping.ToDto(db, terminal);
    }
}

public sealed record SetTerminalActiveCommand(Guid Id, bool Active) : ICommand<TerminalDto>;

public sealed class SetTerminalActiveHandler(IPosDbContext db) : ICommandHandler<SetTerminalActiveCommand, TerminalDto>
{
    public async Task<Result<TerminalDto>> Handle(SetTerminalActiveCommand c, CancellationToken ct)
    {
        var terminal = await db.Terminals.FirstOrDefaultAsync(t => t.Id == c.Id, ct);
        if (terminal is null)
        {
            return PosAppErrors.TerminalNotFound;
        }

        if (!c.Active && await db.Shifts.AnyAsync(s => s.TerminalId == terminal.Id && s.Status != ShiftStatus.Closed, ct))
        {
            return PosAppErrors.TerminalHasOpenShift;
        }

        if (c.Active)
        {
            terminal.Activate();
        }
        else
        {
            terminal.Deactivate();
        }

        await db.SaveChangesAsync(ct);
        return PosMapping.ToDto(db, terminal);
    }
}

/// <summary>
/// Binds the terminal to one device: a random key is generated, returned once, and only its SHA-256 hash is stored. From then on
/// receipts and refunds at this terminal require the key in the X-Pos-Device-Key header. Registering again replaces the key.
/// </summary>
public sealed record RegisterDeviceCommand(Guid Id) : ICommand<DeviceRegistrationDto>;

public sealed class RegisterDeviceHandler(IPosDbContext db, IClock clock) : ICommandHandler<RegisterDeviceCommand, DeviceRegistrationDto>
{
    public async Task<Result<DeviceRegistrationDto>> Handle(RegisterDeviceCommand c, CancellationToken ct)
    {
        var terminal = await db.Terminals.FirstOrDefaultAsync(t => t.Id == c.Id, ct);
        if (terminal is null)
        {
            return PosAppErrors.TerminalNotFound;
        }

        var key = PosDevice.NewKey();
        terminal.RegisterDevice(PosDevice.Hash(key), clock.UtcNow);
        await db.SaveChangesAsync(ct);
        return new DeviceRegistrationDto(terminal.Id, key, PosDevice.HeaderName);
    }
}

/// <summary>Everything a terminal needs to sell offline-capable: products, prices valid on the date, barcodes and the price version.</summary>
public sealed record GetCatalogSnapshotQuery(Guid TerminalId, DateOnly? Date) : IQuery<CatalogSnapshotDto>;

public sealed class GetCatalogSnapshotHandler(
    IPosDbContext db,
    IPriceListSnapshotQueries priceLists,
    ICatalogQueries catalog,
    ICurrencyQueries currencies,
    IClock clock) : IQueryHandler<GetCatalogSnapshotQuery, CatalogSnapshotDto>
{
    public async Task<Result<CatalogSnapshotDto>> Handle(GetCatalogSnapshotQuery q, CancellationToken ct)
    {
        var terminal = await db.Terminals.AsNoTracking().FirstOrDefaultAsync(t => t.Id == q.TerminalId, ct);
        if (terminal is null)
        {
            return PosAppErrors.TerminalNotFound;
        }

        var date = q.Date ?? DateOnly.FromDateTime(clock.UtcNow);
        var snapshot = await priceLists.GetSnapshotAsync(terminal.PriceListId, date, ct);
        if (snapshot is null)
        {
            return PosAppErrors.PriceListInvalid;
        }

        var products = await catalog.GetProductsAsync(snapshot.Items.Select(i => i.ProductId).Distinct().ToList(), ct);
        var active = products.Where(p => p.IsActive).ToList();
        var activeIds = active.Select(p => p.Id).ToHashSet();
        var currency = await currencies.GetAsync(terminal.Currency, ct);
        return new CatalogSnapshotDto(terminal.Id, terminal.PriceListId, snapshot.Currency, snapshot.TaxInclusive, snapshot.Version,
            currency?.CashRoundingIncrement ?? 0, currency?.MinorUnits ?? 2, terminal.MaxDiscountPercent, date, clock.UtcNow,
            active.Select(p => new SnapshotProductDto(p.Id, p.Sku, p.NameEn, p.NameAr, p.Type, p.IsStockable, p.BaseUomId, p.Units, p.Tracking, p.TaxCategory)).ToList(),
            snapshot.Items.Where(i => activeIds.Contains(i.ProductId)).ToList(), snapshot.Barcodes);
    }
}
