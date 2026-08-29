using DataSync.Drivers.Abstractions;

namespace DataSync.Drivers.Generic;

/// <param name="ShadowTableExists">Whether the shadow table is already there.</param>
/// <param name="TriggerExists">Whether the trigger that fills it is already there.</param>
/// <param name="KeyColumns">The source's primary key, which the shadow table is keyed by.</param>
public sealed record TriggerAuditState(
    bool ShadowTableExists, bool TriggerExists, IReadOnlyList<string> KeyColumns);

/// <summary>
/// The engine-independent parts of enabling trigger-audit capture: what to check, what to warn about,
/// and how to assemble the steps an engine supplies.
/// <para>
/// The DDL itself is **not** here and is not shared. <c>CREATE TRIGGER</c> diverges more than almost
/// anything else in SQL — PL/pgSQL functions, T-SQL's statement-level <c>inserted</c> and
/// <c>deleted</c> pseudo-tables, Oracle's <c>:NEW</c>/<c>:OLD</c> — and there is no portable trigger
/// DDL to pretend about. What is shared is the shape of the decision, so two engines cannot answer
/// "is this already set up" differently.
/// </para>
/// </summary>
public static class TriggerAuditPlan
{
    /// <summary>
    /// The costs an operator should read before turning this on, rather than discover afterwards.
    /// <para>
    /// Both are inherent to the mechanism and neither can be engineered away, which is exactly why
    /// they are stated at the point of choice. A trigger is more invasive than an index and less
    /// invasive than <c>ALTER DATABASE SET CHANGE_TRACKING</c>, which this tool already offers — the
    /// answer is to be honest about the trade, not to refuse it.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> Costs { get; } =
    [
        "A trigger runs inside every transaction that writes to this table, for as long as it exists. " +
        "That cost is paid by the source application, not by DataSync.",
        "TRUNCATE TABLE fires no row triggers on any engine, so a truncated source produces no " +
        "deletes and the target keeps every row. Nothing inside a trigger can detect that.",
    ];

    public static ProvisioningPlan Build(
        string qualifiedTable, TriggerAuditState state, IReadOnlyList<ProvisioningStep> steps)
    {
        if (state.KeyColumns.Count == 0)
            return new ProvisioningPlan(
                ProvisioningActions.EnableSourceChangeCapture,
                ProvisioningState.Unsupported,
                [],
                [$"'{qualifiedTable}' has no primary key. A shadow table is keyed by it — without one " +
                 "there is nothing to collapse changes by and nothing to identify a deleted row with."]);

        var outstanding = steps.Count == 0;
        return new ProvisioningPlan(
            ProvisioningActions.EnableSourceChangeCapture,
            outstanding ? ProvisioningState.Satisfied : ProvisioningState.Missing,
            steps,
            // Only where there is something to apply. Repeating the cost of a trigger at a table that
            // already has one is telling somebody about a decision they made months ago.
            outstanding ? [] : Costs);
    }
}
