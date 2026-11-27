using Erp.Application.Abstractions;

namespace Erp.Infrastructure.Tenancy;

/// <summary>Scoped holder of the request's branch/warehouse limits. Starts unrestricted; may only be narrowed once.</summary>
public sealed class DataScope : IDataScope
{
    private List<Guid> _branchIds = [];
    private List<Guid> _warehouseIds = [];
    private bool _set;

    public bool RestrictsBranches { get; private set; }

    public bool RestrictsWarehouses { get; private set; }

    public IReadOnlyList<Guid> BranchIds => _branchIds;

    public IReadOnlyList<Guid> WarehouseIds => _warehouseIds;

    public bool AllowsBranch(Guid? branchId) => !RestrictsBranches || branchId is null || _branchIds.Contains(branchId.Value);

    public bool AllowsWarehouse(Guid? warehouseId) => !RestrictsWarehouses || warehouseId is null || _warehouseIds.Contains(warehouseId.Value);

    public void Restrict(CompanyAccess access)
    {
        if (_set)
        {
            throw new InvalidOperationException("The data scope of a request can only be set once.");
        }

        _set = true;
        if (!access.AllBranches)
        {
            _branchIds = access.BranchIds.Distinct().ToList();
            RestrictsBranches = true;
        }

        if (!access.AllWarehouses)
        {
            _warehouseIds = access.WarehouseIds.Distinct().ToList();
            RestrictsWarehouses = true;
        }
    }
}
