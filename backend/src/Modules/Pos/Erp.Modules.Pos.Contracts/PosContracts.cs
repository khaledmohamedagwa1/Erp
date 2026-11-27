namespace Erp.Modules.Pos.Contracts;

public static class PosPermissions
{
    public const string Read = "pos.read";
    public const string TerminalManage = "pos.terminal.manage";
    public const string ShiftManage = "pos.shift.manage";
    public const string Sell = "pos.sell";

    /// <summary>Refunds pay money out of the drawer: sensitive.</summary>
    public const string Refund = "pos.refund";

    /// <summary>Required for any line discount above 0 % (never above the terminal's maximum).</summary>
    public const string Discount = "pos.discount";
}

/// <summary>Source document types used by POS for inventory and journal references.</summary>
public static class PosDocumentTypes
{
    public const string Receipt = "pos.receipt";
    public const string Refund = "pos.refund";
    public const string Shift = "pos.shift";
}
