namespace DataSync.Drivers.MsSql;

public static class MsSqlDriverKinds
{
    public const string ChangeTracking = "MsSqlChangeTracking";
    public const string Watermark = "Watermark";
    public const string BatchReload = "MsSqlBatchReload";
    public const string StagingTable = "MsSqlStagingTable";
    public const string Merge = "MsSqlMerge";
    public const string MergeReconcile = "MsSqlMergeReconcile";
    public const string DeleteInsert = "MsSqlDeleteInsert";
}
