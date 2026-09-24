using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.DuckDb;
using DbDataSync.Drivers.Generic;
using DbDataSync.Drivers.MsSql;
using DbDataSync.Scripting;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// <see cref="IPositionCapturing"/> is only ever declared where a reader can honestly report its
/// current position without reading a row — see that interface's own doc and
/// architecture/planning/done/bulk-load-pipeline-and-the-initial-load-rule.md's "correctness crux".
/// This pins the roster: a reader with a real position-capture story implements it, and one without a
/// story to tell does not.
/// <para>
/// **Nothing calls this capability yet** — the Bulk Load pipeline that will is future, unscheduled
/// work. This test exists so the declaration itself stays honest in the meantime, the same reason
/// <c>ChangeReaderFirstPassContractTests</c> pins <see cref="IReadIntentDeclaring"/>.
/// </para>
/// </summary>
public sealed class PositionCapturingContractTests
{
    [Theory]
    [InlineData(typeof(MsSqlChangeTrackingReader))]
    [InlineData(typeof(MsSqlCdcReader))]
    [InlineData(typeof(TriggerAuditReader))]
    [InlineData(typeof(WatermarkReader))]
    public void ReadersWithARealPosition_ImplementIt(Type readerType)
    {
        Assert.True(typeof(IPositionCapturing).IsAssignableFrom(readerType),
            $"{readerType.Name} has a real current-position story (a database-wide counter or an " +
            "aggregate over its own feed) and should implement IPositionCapturing.");
    }

    /// <summary>
    /// A reload reader's only "position" is whatever watermark it was handed, echoed back unchanged —
    /// there is nothing to report ahead of reading, so it has no honest answer to give. A scripted or
    /// DuckDB query is the same: the script decides what its watermark means, and this abstraction
    /// cannot ask it to do so without reading.
    /// </summary>
    [Theory]
    [InlineData(typeof(BatchReloadReader))]
    [InlineData(typeof(DuckDbQueryReader))]
    [InlineData(typeof(ScriptedQueryReader))]
    public void ReadersWithNoPositionOfTheirOwn_DoNotImplementIt(Type readerType)
    {
        Assert.False(typeof(IPositionCapturing).IsAssignableFrom(readerType),
            $"{readerType.Name} has no honest position to report ahead of reading and should not " +
            "implement IPositionCapturing.");
    }
}
