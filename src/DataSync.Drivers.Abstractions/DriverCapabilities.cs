using DataSync.Core.Config;

namespace DataSync.Drivers.Abstractions;

/// <summary>What one registered driver can actually do, derived entirely from the declarative
/// properties its readers/staging providers/writers expose — never from string-matching Kind values.
/// This is what lets a UI build its Kind pickers (and show which choices support segmentation or
/// reconciliation) against whatever drivers happen to be registered, instead of hardcoding a list
/// that silently goes stale the moment a second driver exists.</summary>
public sealed record DriverCapabilities(
    ConnectionDriverType DriverType,
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

public sealed record ReaderCapability(string Kind, bool SupportsSegmentation, bool DetectsDeletes);

public sealed record StagingCapability(string Kind);

public sealed record WriterCapability(string Kind, bool SupportsReconciliation);
