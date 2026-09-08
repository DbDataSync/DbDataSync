using DbDataSync.Core.Config;

namespace DbDataSync.Drivers.Abstractions;

/// <summary>What one registered driver can actually do, derived entirely from the declarative
/// properties its readers/staging providers/writers expose — never from string-matching Kind values.
/// This is what lets a UI build its Kind pickers (and show which choices support segmentation or
/// reconciliation) against whatever drivers happen to be registered, instead of hardcoding a list
/// that silently goes stale the moment a second driver exists.</summary>
public sealed record DriverCapabilities(
    string DriverType,
    IReadOnlyList<ReaderCapability> Readers,
    IReadOnlyList<StagingCapability> StagingProviders,
    IReadOnlyList<WriterCapability> Writers,
    /// <summary>Whether this driver can prove a connection reaches its engine
    /// (<see cref="IConnectionTester"/>). False is not a defect — a driver reaching an arbitrary
    /// engine may have no probe it can name — so a UI hides the affordance rather than offering one
    /// that could never work.</summary>
    bool SupportsConnectionTest,
    /// <summary>Which provisioning actions (<see cref="ProvisioningActions"/>) this driver can plan
    /// (<see cref="IProvisioner"/>). Empty for a driver that implements no provisioning at all.</summary>
    IReadOnlyList<string> SupportedProvisioningActions);
// Connection parameters used to be a field here. They moved out in phase 50, when they stopped being
// a fixed list: what a connection takes now depends on what it has been given so far — Host is not a
// setting once the operator picks connection-string addressing — and an answer that depends on values
// cannot live in a response that carries none. See POST /api/drivers/{type}/connection-parameters.
//
// Which also keeps this response cacheable. The Kind pickers read it and nothing about a reader's
// options changes when somebody edits a host field.

/// <param name="Parameters">
/// The settings this Kind reads out of its options bag. Declared so the SPA can offer them instead of
/// an operator having to know the key by heart and type it into a free-form table — which is what
/// choosing a Kind used to mean.
/// </param>
/// <param name="SupportedIntents">
/// What <see cref="IReadIntentDeclaring.SupportedIntents"/> says, or empty for a reader that does not
/// implement the interface at all (a reader with no incremental mode, like batch reload) — see phase
/// 102. **Never includes <see cref="ReadIntent.InitialLoad"/>**, for the same reason the interface
/// itself never does: every reader can be asked for one regardless of what it declares here, so a UI
/// offering intents from this list adds <c>InitialLoad</c> itself rather than expecting it in this set.
/// </param>
public sealed record ReaderCapability(
    string Kind, bool SupportsSegmentation, bool DetectsDeletes, IReadOnlyList<ParameterDescriptor> Parameters,
    IReadOnlyList<ReadIntent> SupportedIntents);

public sealed record StagingCapability(string Kind, IReadOnlyList<ParameterDescriptor> Parameters);

public sealed record WriterCapability(
    string Kind, bool SupportsReconciliation, IReadOnlyList<ParameterDescriptor> Parameters);
