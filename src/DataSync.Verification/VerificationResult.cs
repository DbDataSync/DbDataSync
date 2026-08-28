using DataSync.Drivers.Generic;

namespace DataSync.Verification;

/// <summary>
/// One check's answer: what was compared, when each side was read, and every group that was looked at.
/// </summary>
/// <param name="SourceReadAtUtc">
/// When each side's query ran, kept separately and shown. The gap between the two reads is what tells
/// an operator whether a difference is drift or a defect — without it they are guessing, which is the
/// thing this feature exists to stop them doing.
/// </param>
/// <param name="GroupColumns">The grouping columns, in the order the rows carry them.</param>
/// <param name="MeasureColumns">Which numbers were compared.</param>
public sealed record VerificationResult(
    string CheckName,
    IReadOnlyList<string> GroupColumns,
    IReadOnlyList<string> MeasureColumns,
    double DifferenceThreshold,
    DateTimeOffset SourceReadAtUtc,
    DateTimeOffset TargetReadAtUtc,
    IReadOnlyList<VerificationRow> Rows)
{
    /// <summary>Groups where the two sides disagreed by more than the threshold, or that only one side
    /// had. The single number a list of past results is worth sorting by.</summary>
    public int DifferingGroups =>
        Rows.Count(r => r.Status != VerificationRowStatus.Match);

    /// <summary>How far apart the two reads were. A difference is only as meaningful as this is
    /// small.</summary>
    public TimeSpan ReadGap => (TargetReadAtUtc - SourceReadAtUtc).Duration();
}
