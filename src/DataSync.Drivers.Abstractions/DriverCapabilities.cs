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
    IReadOnlyList<string> SupportedProvisioningActions,
    /// <summary>
    /// What this driver's connections take beyond the fields every connection has. A driver declares
    /// its free-form properties bag here as one vararg <c>Property</c> parameter, rather than the SPA
    /// assuming every connection has one.
    /// <para>
    /// Host, port, database, auth mode, user and credential are deliberately **not** here. They are
    /// the shape of <c>ConnectionConfig</c> itself, validated at save, and one of them is a secret
    /// that must never travel through a generic string bag — see phase 31. This is for what a driver
    /// needs *in addition*.
    /// </para>
    /// </summary>
    IReadOnlyList<ParameterDescriptor> ConnectionParameters);

/// <param name="Parameters">
/// The settings this Kind reads out of its options bag. Declared so the SPA can offer them instead of
/// an operator having to know the key by heart and type it into a free-form table — which is what
/// choosing a Kind used to mean.
/// </param>
public sealed record ReaderCapability(
    string Kind, bool SupportsSegmentation, bool DetectsDeletes, IReadOnlyList<ParameterDescriptor> Parameters);

public sealed record StagingCapability(string Kind, IReadOnlyList<ParameterDescriptor> Parameters);

public sealed record WriterCapability(
    string Kind, bool SupportsReconciliation, IReadOnlyList<ParameterDescriptor> Parameters);
