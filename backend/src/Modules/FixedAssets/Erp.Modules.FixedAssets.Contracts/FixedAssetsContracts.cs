namespace Erp.Modules.FixedAssets.Contracts;

public static class FixedAssetsPermissions
{
    public const string Read = "assets.read";
    public const string Manage = "assets.manage";
    public const string DepreciationPost = "assets.depreciation.post";
    public const string Revalue = "assets.revalue";
    public const string Dispose = "assets.dispose";
}

/// <summary>Source document types of journals posted by the Fixed Assets module.</summary>
public static class FixedAssetDocumentTypes
{
    public const string Acquisition = "AssetAcquisition";
    public const string DepreciationRun = "DepreciationRun";
    public const string Revaluation = "AssetRevaluation";
    public const string Disposal = "AssetDisposal";
    public const string Impairment = "AssetImpairment";
    public const string PartialDisposal = "AssetPartialDisposal";
    public const string Transfer = "AssetTransfer";
}
