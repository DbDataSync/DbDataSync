namespace DataSync.Drivers.Generic;

/// <summary>
/// Kinds for engine-neutral implementations. Unlike a driver-specific Kind these carry no engine
/// prefix, because the name identifies a *strategy* every driver can offer rather than one engine's
/// mechanism — an operator choosing "Watermark" means the same thing whichever driver serves it.
/// </summary>
public static class GenericDriverKinds
{
    public const string Watermark = "Watermark";
    public const string BatchReload = "BatchReload";
    public const string StagingTable = "StagingTable";
    public const string DeleteInsert = "DeleteInsert";
}
