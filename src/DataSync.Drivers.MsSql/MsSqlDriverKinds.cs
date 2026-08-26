namespace DataSync.Drivers.MsSql;

public static class MsSqlDriverKinds
{
    public const string ChangeTracking = "MsSqlChangeTracking";
    public const string Watermark = "Watermark";
    public const string StagingTable = "MsSqlStagingTable";
    public const string Merge = "MsSqlMerge";
}
