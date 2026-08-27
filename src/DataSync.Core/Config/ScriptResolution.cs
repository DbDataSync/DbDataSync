namespace DataSync.Core.Config;

/// <summary>
/// Which script — if any — is bound to a slot for a given connection, replication and table mapping.
/// <para>
/// **Table mapping, then replication, then connection.** The same shape as
/// <see cref="EndpointResolution"/>, and for the same reason: resolving in one place means nothing
/// downstream has to know inheritance exists.
/// </para>
/// <para>
/// One thing endpoints never needed: a way to say "explicitly none". If a connection binds a transform
/// and one mapping must not use it, the mapping has to be able to say so — and "absent" cannot mean
/// both *inherit* and *none*. A dictionary distinguishes them: an absent key inherits, a key present
/// with a null value overrides with nothing.
/// </para>
/// </summary>
public static class ScriptResolution
{
    /// <param name="connection">
    /// The connection the slot belongs to — the *source* connection for a source-side slot. A mapping
    /// can read from one connection and write to another, so "the connection" is only meaningful once
    /// the caller has said which side it is asking about.
    /// </param>
    public static ScriptBinding? Resolve(
        string slot,
        ConnectionConfig? connection,
        ReplicationTaskConfig? task,
        TableMappingConfig? mapping) =>
        HierarchicalBinding.Resolve(slot, mapping?.Scripts, task?.Scripts, connection?.Scripts);

    /// <summary>Where a resolved binding came from, for a UI that wants to show INHERITED.</summary>
    public static BindingLevel LevelOf(
        string slot,
        ConnectionConfig? connection,
        ReplicationTaskConfig? task,
        TableMappingConfig? mapping) =>
        HierarchicalBinding.LevelOf(slot, mapping?.Scripts, task?.Scripts, connection?.Scripts);
}
