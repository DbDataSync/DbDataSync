using System.Data.Common;
using DataSync.Core.Config;
using DataSync.Core.Sql;

namespace DataSync.Drivers.Abstractions;

/// <summary>
/// A driver that can plan (and, once a human applies it, run) the DDL a replication needs but cannot
/// create for itself — enabling change capture on a source table, creating a missing target table.
/// <para>
/// Opt-in, in the same shape as <see cref="IConnectionTester"/> and <see cref="ISegmentExpandingReader"/>,
/// for the same reason: a driver reaching an arbitrary engine through ODBC has no DDL it can name, and
/// requiring the method would mean implementing something it cannot honour. Callers ask
/// <c>driver is IProvisioner</c>; capability discovery (<see cref="DriverCapabilities.SupportedProvisioningActions"/>)
/// reports the answer so the UI hides an affordance that could never work.
/// </para>
/// <para>
/// <b>Planning and applying are separate calls, and only the host applies.</b> This is the same rule
/// the scripting slots follow — a provisioner returns a description of what to do; the host does it —
/// and it is what makes the preview honest: the text shown to the operator is the text that will be
/// executed, because there is no second code path that could produce different SQL. Applying is just
/// the host running <see cref="ProvisioningStep.CommandText"/> for each step of a freshly-computed
/// plan; there is no separate "apply" method on this interface.
/// </para>
/// </summary>
public interface IProvisioner
{
    /// <summary>Which actions this driver can plan. Declared, never string-matched by callers.</summary>
    IReadOnlyList<string> SupportedActions { get; }

    /// <summary>Inspects current state and returns the statements that would change it. Runs no DDL.
    /// <paramref name="connection"/> is already open and is the side the action runs against — the
    /// source connection for <see cref="ProvisioningActions.EnableSourceChangeCapture"/>, the target
    /// connection for <see cref="ProvisioningActions.CreateTargetTable"/>.</summary>
    Task<ProvisioningPlan> PlanAsync(
        DbConnection connection, ProvisioningRequest request, CancellationToken cancellationToken);
}

/// <summary>Action names. String constants, not an enum — the same reason <c>ScriptSlots</c> gives:
/// they are persisted in URLs, and an action added later must not renumber existing ones.</summary>
public static class ProvisioningActions
{
    public const string EnableSourceChangeCapture = "enableSourceChangeCapture";
    public const string CreateTargetTable = "createTargetTable";

    /// <summary>
    /// Bring an **existing** target table's columns in line with the mapping: add what is missing,
    /// change what no longer matches. Never <c>DROP</c> — see
    /// <c>ProvisioningConfig.AlterTargetTableColumnsIfMissingOrChanged</c>.
    /// <para>
    /// Distinct from <see cref="CreateTargetTable"/> because the two are mutually exclusive: the table
    /// either exists or it does not, and the plan for one situation is never the plan for the other.
    /// They share a panel because they are the same question — "make the target fit" — asked of two
    /// different starting states.
    /// </para>
    /// </summary>
    public const string AlterTargetTable = "alterTargetTable";
}

public enum ProvisioningState
{
    /// <summary>Nothing to do — either already in the needed state, or the configured reader kind
    /// needs no cooperation from this side at all.</summary>
    Satisfied,

    /// <summary>One or more steps would change current state to the needed one.</summary>
    Missing,

    /// <summary>This driver understands the action but cannot plan it for the table as configured —
    /// an unmappable column type, a table with no primary key. Names exactly what and why; never a
    /// guess.</summary>
    Unsupported,

    /// <summary>The action is not implemented for this driver/reader-kind combination at all.</summary>
    Unknown,
}

/// <summary>Which database context one step must run in. A plan is not all in the same context: the
/// two <c>ALTER DATABASE</c> statements <see cref="ProvisioningActions.EnableSourceChangeCapture"/> may
/// emit are database-scoped settings shared by every mapping on that database, while the
/// <c>ALTER TABLE</c> alongside them must run against the source database's own context.</summary>
public enum ProvisioningStepScope
{
    Database,
    Table,
}

/// <param name="Title">Short, human-facing label for the UI — "Enable Change Tracking on database
/// [Sales]".</param>
/// <param name="CommandText">The exact statement Apply will run. This is the preview text; there is
/// no second rendering path that could produce something different.</param>
/// <param name="Rationale">Why this step exists, for the operator reading the preview — null when the
/// title already says everything worth saying.</param>
public sealed record ProvisioningStep(
    string Title, string CommandText, string? Rationale, ProvisioningStepScope Scope);

public sealed record ProvisioningPlan(
    string Action,
    ProvisioningState State,
    IReadOnlyList<ProvisioningStep> Steps,
    IReadOnlyList<string> Warnings);

/// <summary>One column a <see cref="ProvisioningActions.CreateTargetTable"/> plan should create, with
/// its type already translated to canonical form by the caller (using the *source* dialect) — so the
/// provisioner, which belongs to the *target* driver, only ever has to call its own
/// <c>RenderColumnType</c> and never needs to know what dialect produced the column it was handed.</summary>
/// <param name="Name">The target column name.</param>
/// <param name="TypeOverride">
/// The target type an operator chose, in the target's own dialect, or null to use whatever
/// <paramref name="Type"/> renders to. Set only when somebody actually edited it — an inference
/// written into config would freeze today's answer against a source column that later changes.
/// </param>
/// <param name="Renames">
/// This column's rename history, oldest first, so a planner can work out which name the target
/// currently uses. Empty for a column nobody has renamed, which is nearly all of them.
/// </param>
public sealed record ProvisioningColumn(
    string Name,
    CanonicalType Type,
    bool IsNullable,
    bool IsPrimaryKey,
    string? TypeOverride = null,
    IReadOnlyList<RenameStep>? Renames = null);

/// <param name="Table">The table this call is about: the source table for
/// <see cref="ProvisioningActions.EnableSourceChangeCapture"/>, the target table for
/// <see cref="ProvisioningActions.CreateTargetTable"/>.</param>
/// <param name="ReaderKind">The configured reader kind, for <see cref="ProvisioningActions.EnableSourceChangeCapture"/>
/// — what needs enabling depends on which reader is configured, so a reader kind needing no source
/// cooperation (<c>Watermark</c>, <c>BatchReload</c>) plans <see cref="ProvisioningState.Satisfied"/>
/// with zero steps. Null for <see cref="ProvisioningActions.CreateTargetTable"/>.</param>
/// <param name="ReaderOptions">The configured reader's options — carries
/// <c>MsSqlChangeTrackingReader.SnapshotIsolationOption</c> so the plan can include the
/// <c>ALLOW_SNAPSHOT_ISOLATION</c> step only when it is actually needed.</param>
/// <param name="Columns">Populated for <see cref="ProvisioningActions.CreateTargetTable"/>: every
/// mapped column, named by its target name, with its canonical type already resolved. Empty for
/// <see cref="ProvisioningActions.EnableSourceChangeCapture"/>.</param>
public sealed record ProvisioningRequest(
    string Action,
    TableRef Table,
    string? ReaderKind,
    IReadOnlyDictionary<string, string> ReaderOptions,
    IReadOnlyList<ProvisioningColumn> Columns,
    /// <summary>
    /// The configured writer, for the writers that need columns beyond the mapped ones — a snapshot's
    /// marker, an SCD Type 2 target's version key and validity range. Null where it does not matter.
    /// </summary>
    string? WriterKind = null);
