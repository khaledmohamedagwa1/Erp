using System.Data;
using System.Data.Common;
using Erp.Application.Abstractions;
using Erp.Infrastructure.Persistence;
using Erp.Modules.Inventory.Application;
using Erp.Modules.Inventory.Domain;

namespace Erp.Modules.Inventory.Infrastructure;

/// <summary>
/// Set-based stock updates on the shared connection and transaction (docs §19.4). The item's aggregate balance row
/// (BatchKey = empty GUID) is created if needed and update-locked first; batch rows only after it. All modifications of an
/// item/warehouse therefore serialise on that row, which keeps the "available ≥ 0" check exact under concurrency.
/// </summary>
public sealed class InventoryStore(ErpDbSession session, ITenantContext tenant) : IInventoryStore
{
    private const string EnsureAggregate = """
        DECLARE @lock int;
        -- Row locks only (no key-range locks, which deadlock unrelated postings): create the row once; a concurrent creator
        -- makes the insert wait on the unique key and then fail as a duplicate, which is ignored. The UPDATE that follows
        -- takes the exclusive row lock that serialises all changes to this item/warehouse until the transaction ends.
        IF NOT EXISTS (SELECT 1 FROM inventory.InventoryBalance
                       WHERE TenantId = @t AND ProductId = @p AND VariantKey = @v AND WarehouseId = @w AND BatchKey = @empty)
        BEGIN TRY
            INSERT INTO inventory.InventoryBalance (Id, TenantId, CompanyId, ProductId, VariantKey, WarehouseId, BatchKey, OnHand, Reserved, UpdatedAtUtc)
            VALUES (NEWID(), @t, @c, @p, @v, @w, @empty, 0, 0, SYSUTCDATETIME());
        END TRY
        BEGIN CATCH
            IF ERROR_NUMBER() NOT IN (2601, 2627) THROW;
        END CATCH
        -- Serialisation point: update-lock the (now existing) aggregate row first, held until the transaction ends.
        SELECT @lock = 1 FROM inventory.InventoryBalance WITH (UPDLOCK, ROWLOCK)
         WHERE TenantId = @t AND ProductId = @p AND VariantKey = @v AND WarehouseId = @w AND BatchKey = @empty;
        """;

    private const string EnsureBatch = """
        IF @b <> @empty AND NOT EXISTS (SELECT 1 FROM inventory.InventoryBalance
                       WHERE TenantId = @t AND ProductId = @p AND VariantKey = @v AND WarehouseId = @w AND BatchKey = @b)
        BEGIN TRY
            INSERT INTO inventory.InventoryBalance (Id, TenantId, CompanyId, ProductId, VariantKey, WarehouseId, BatchKey, OnHand, Reserved, UpdatedAtUtc)
            VALUES (NEWID(), @t, @c, @p, @v, @w, @b, 0, 0, SYSUTCDATETIME());
        END TRY
        BEGIN CATCH
            IF ERROR_NUMBER() NOT IN (2601, 2627) THROW;
        END CATCH
        IF @b <> @empty
            SELECT @lock = 1 FROM inventory.InventoryBalance WITH (UPDLOCK, ROWLOCK)
             WHERE TenantId = @t AND ProductId = @p AND VariantKey = @v AND WarehouseId = @w AND BatchKey = @b;
        """;

    private const string ItemTotal = """
        (SELECT SUM(x.OnHand) FROM inventory.InventoryBalance x
         WHERE x.TenantId = @t AND x.ProductId = @p AND x.VariantKey = @v AND x.WarehouseId = @w)
        """;

    public async Task ReceiveAsync(Guid companyId, StockKey key, decimal baseQuantity, CancellationToken cancellationToken)
    {
        await using var command = Command($"""
            {EnsureAggregate}
            {EnsureBatch}
            UPDATE inventory.InventoryBalance SET OnHand = OnHand + @q, UpdatedAtUtc = SYSUTCDATETIME()
             WHERE TenantId = @t AND ProductId = @p AND VariantKey = @v AND WarehouseId = @w AND BatchKey = @b;
            """, companyId, key.ProductId, key.VariantKey, key.WarehouseId, key.BatchKey);
        Add(command, "@q", DbType.Decimal, baseQuantity);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<StockApplyStatus> IssueAsync(Guid companyId, StockKey key, decimal baseQuantity, decimal consumeReserved, bool allowNegative, CancellationToken cancellationToken)
    {
        await using var command = Command($"""
            {EnsureAggregate}
            UPDATE inventory.InventoryBalance SET Reserved = Reserved - @consume, UpdatedAtUtc = SYSUTCDATETIME()
             WHERE TenantId = @t AND ProductId = @p AND VariantKey = @v AND WarehouseId = @w AND BatchKey = @empty
               AND (@neg = 1 OR {ItemTotal} - (Reserved - @consume) - @q >= 0);
            IF @@ROWCOUNT = 0
            BEGIN
                SELECT 1;
                RETURN;
            END
            -- The aggregate row is already exclusively locked by the UPDATE above, so this read is stable.
            IF @neg = 0 AND @b <> @empty AND NOT EXISTS (SELECT 1 FROM inventory.InventoryBalance WITH (UPDLOCK, ROWLOCK)
                       WHERE TenantId = @t AND ProductId = @p AND VariantKey = @v AND WarehouseId = @w AND BatchKey = @b)
            BEGIN
                SELECT 1;
                RETURN;
            END
            {EnsureBatch}
            UPDATE inventory.InventoryBalance SET OnHand = OnHand - @q, UpdatedAtUtc = SYSUTCDATETIME()
             WHERE TenantId = @t AND ProductId = @p AND VariantKey = @v AND WarehouseId = @w AND BatchKey = @b
               AND (@neg = 1 OR OnHand - @q >= 0);
            SELECT CASE WHEN @@ROWCOUNT = 0 THEN 1 ELSE 0 END;
            """, companyId, key.ProductId, key.VariantKey, key.WarehouseId, key.BatchKey);
        Add(command, "@q", DbType.Decimal, baseQuantity);
        Add(command, "@consume", DbType.Decimal, consumeReserved);
        Add(command, "@neg", DbType.Boolean, allowNegative);
        var status = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        return status == 0 ? StockApplyStatus.Applied : StockApplyStatus.Insufficient;
    }

    public async Task<StockApplyStatus> ReserveAsync(Guid companyId, Guid productId, Guid variantKey, Guid warehouseId, decimal baseQuantity, CancellationToken cancellationToken)
    {
        await using var command = Command($"""
            {EnsureAggregate}
            UPDATE inventory.InventoryBalance SET Reserved = Reserved + @q, UpdatedAtUtc = SYSUTCDATETIME()
             WHERE TenantId = @t AND ProductId = @p AND VariantKey = @v AND WarehouseId = @w AND BatchKey = @empty
               AND {ItemTotal} - Reserved - @q >= 0;
            SELECT CASE WHEN @@ROWCOUNT = 0 THEN 1 ELSE 0 END;
            """, companyId, productId, variantKey, warehouseId, Guid.Empty);
        Add(command, "@q", DbType.Decimal, baseQuantity);
        var status = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        return status == 0 ? StockApplyStatus.Applied : StockApplyStatus.Insufficient;
    }

    public async Task ReleaseAsync(Guid companyId, Guid productId, Guid variantKey, Guid warehouseId, decimal baseQuantity, CancellationToken cancellationToken)
    {
        await using var command = Command($"""
            {EnsureAggregate}
            UPDATE inventory.InventoryBalance SET Reserved = CASE WHEN Reserved - @q < 0 THEN 0 ELSE Reserved - @q END, UpdatedAtUtc = SYSUTCDATETIME()
             WHERE TenantId = @t AND ProductId = @p AND VariantKey = @v AND WarehouseId = @w AND BatchKey = @empty;
            """, companyId, productId, variantKey, warehouseId, Guid.Empty);
        Add(command, "@q", DbType.Decimal, baseQuantity);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task LockValuationAsync(Guid companyId, Guid valuationAreaId, Guid productId, Guid variantKey, string costingMethod, CancellationToken cancellationToken)
    {
        var transaction = session.Transaction ?? throw new InvalidOperationException("Inventory valuation requires a transaction.");
        await using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            IF NOT EXISTS (SELECT 1 FROM inventory.ItemValuation
                           WHERE TenantId = @t AND ValuationAreaId = @a AND ProductId = @p AND VariantKey = @v)
            BEGIN TRY
                INSERT INTO inventory.ItemValuation (Id, TenantId, CompanyId, ValuationAreaId, ProductId, VariantKey, CostingMethod,
                                                     Quantity, Value, AverageCost, LastCost, LastSequence, CreatedAtUtc)
                VALUES (NEWID(), @t, @c, @a, @p, @v, @m, 0, 0, 0, 0, 0, SYSUTCDATETIME());
            END TRY
            BEGIN CATCH
                IF ERROR_NUMBER() NOT IN (2601, 2627) THROW;
            END CATCH
            -- Hold an update lock on the valuation row until the transaction ends: costing is serialised per item.
            SELECT 1 FROM inventory.ItemValuation WITH (UPDLOCK, ROWLOCK)
             WHERE TenantId = @t AND ValuationAreaId = @a AND ProductId = @p AND VariantKey = @v;
            """;
        Add(command, "@t", DbType.Guid, TenantId);
        Add(command, "@c", DbType.Guid, companyId);
        Add(command, "@a", DbType.Guid, valuationAreaId);
        Add(command, "@p", DbType.Guid, productId);
        Add(command, "@v", DbType.Guid, variantKey);
        Add(command, "@m", DbType.String, costingMethod);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private Guid TenantId => tenant.TenantId ?? throw new InvalidOperationException("Tenant context required.");

    private DbCommand Command(string sql, Guid companyId, Guid productId, Guid variantKey, Guid warehouseId, Guid batchKey)
    {
        var transaction = session.Transaction ?? throw new InvalidOperationException("Stock updates require a transaction.");
        var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        Add(command, "@t", DbType.Guid, TenantId);
        Add(command, "@c", DbType.Guid, companyId);
        Add(command, "@p", DbType.Guid, productId);
        Add(command, "@v", DbType.Guid, variantKey);
        Add(command, "@w", DbType.Guid, warehouseId);
        Add(command, "@b", DbType.Guid, batchKey);
        Add(command, "@empty", DbType.Guid, Guid.Empty);
        return command;
    }

    private static void Add(DbCommand command, string name, DbType type, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value;
        if (type == DbType.Decimal)
        {
            parameter.Precision = 18;
            parameter.Scale = 6;
        }

        command.Parameters.Add(parameter);
    }
}
