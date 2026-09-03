namespace DbDataSync.Drivers.Generic;

/// <summary>
/// Kinds for engine-neutral implementations. Unlike a driver-specific Kind these carry no engine
/// prefix, because the name identifies a *strategy* every driver can offer rather than one engine's
/// mechanism — an operator choosing "Watermark" means the same thing whichever driver serves it.
/// </summary>
public static class GenericDriverKinds
{
    public const string Watermark = "Watermark";

    /// <summary>A trigger-maintained shadow table. The read side is engine-neutral; the trigger DDL
    /// that fills it is not, and is a per-engine provisioning step.</summary>
    public const string TriggerAudit = "TriggerAudit";
    public const string BatchReload = "BatchReload";
    public const string StagingTable = "StagingTable";
    public const string DeleteInsert = "DeleteInsert";

    /// <summary>Appends a complete copy per pass, marked with when it ran. Keeps history by keeping
    /// every copy.</summary>
    public const string Snapshot = "Snapshot";

    /// <summary>Slowly Changing Dimension Type 2: keeps history by versioning each key, closing the
    /// old version when its values change.</summary>
    public const string Scd2 = "Scd2";
}
