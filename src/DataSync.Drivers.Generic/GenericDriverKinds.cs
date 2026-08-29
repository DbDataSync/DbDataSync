namespace DataSync.Drivers.Generic;

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
}
