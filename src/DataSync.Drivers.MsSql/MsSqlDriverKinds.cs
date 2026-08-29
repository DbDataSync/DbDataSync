using DataSync.Drivers.Generic;

namespace DataSync.Drivers.MsSql;

public static class MsSqlDriverKinds
{
    public const string ChangeTracking = "MsSqlChangeTracking";

    /// <summary>Change Data Capture. Not an upgrade to <see cref="ChangeTracking"/> — a different
    /// mechanism with different semantics; see <c>MsSqlCdcReader</c>.</summary>
    public const string Cdc = "MsSqlCdc";

    /// <summary>Re-exported from <see cref="GenericDriverKinds"/>: the watermark reader is
    /// engine-neutral, so its Kind carries no prefix and is not this driver's to define.</summary>
    public const string Watermark = GenericDriverKinds.Watermark;
    public const string BatchReload = "MsSqlBatchReload";
    public const string StagingTable = "MsSqlStagingTable";
    public const string Merge = "MsSqlMerge";
    public const string MergeReconcile = "MsSqlMergeReconcile";
    public const string DeleteInsert = "MsSqlDeleteInsert";
}
