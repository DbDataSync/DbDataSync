using DbDataSync.Drivers.Generic;

namespace DbDataSync.Drivers.Postgres;

/// <summary>
/// The Kinds this driver defines, and the generic ones it re-exports.
/// <para>
/// There was no such class until phase 38, because until phase 38 this driver defined nothing of its
/// own — every reader, staging provider and writer it registers is <c>DbDataSync.Drivers.Generic</c>'s,
/// driven by <c>PostgresDialect</c>. <see cref="CopyStaging"/> is the first exception, and it is one
/// because <c>COPY … FROM STDIN (FORMAT BINARY)</c> is a protocol rather than a statement: there is no
/// dialect hook that could have expressed it.
/// </para>
/// </summary>
public static class PostgresDriverKinds
{
    /// <summary>
    /// Binary <c>COPY</c> staging — phase 38. Prefixed, per the naming rule: this is one engine's
    /// mechanism rather than a strategy every driver could offer.
    /// </summary>
    public const string CopyStaging = "PgCopyStaging";

    /// <summary>
    /// Logical decoding through a replication slot — phase 34. Prefixed for the same reason
    /// <see cref="CopyStaging"/> is: it is one engine's mechanism, not a strategy every driver could
    /// offer.
    /// </summary>
    public const string LogicalSlot = "PgLogicalSlot";

    /// <summary>Re-exported from <see cref="GenericDriverKinds"/>: batched multi-row <c>INSERT</c>
    /// staging is engine-neutral, and it stays registered as the fallback for an instance that will
    /// not permit <c>COPY</c> or a column <see cref="CopyStaging"/> cannot write in binary.</summary>
    public const string StagingTable = GenericDriverKinds.StagingTable;
}
