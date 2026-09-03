namespace DbDataSync.State.Tests;

public sealed class LogWriterTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("dbdatasync-state-tests-").FullName;
    private readonly StateDatabase _database;

    public LogWriterTests()
    {
        _database = new StateDatabase(Path.Combine(_tempDir, "state.db"));
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    [Fact]
    public void Log_ThenExplicitFlush_MakesLinesReadable()
    {
        using var writer = new LogWriter(_database);
        var runId = Guid.NewGuid();

        writer.Log(runId, LogSeverity.Info, "starting run");
        writer.Log(runId, LogSeverity.Error, "row 42 failed");
        writer.Flush();

        var logs = writer.GetLogs(runId);
        Assert.Equal(2, logs.Count);
        Assert.Equal("starting run", logs[0].Message);
        Assert.Equal(LogSeverity.Info, logs[0].Level);
        Assert.Equal("row 42 failed", logs[1].Message);
        Assert.Equal(LogSeverity.Error, logs[1].Level);
    }

    [Fact]
    public void Log_BelowThreshold_IsNotVisibleUntilFlushed()
    {
        using var writer = new LogWriter(_database);
        var runId = Guid.NewGuid();
        writer.Log(runId, LogSeverity.Info, "buffered line");

        using var connection = _database.OpenConnection();
        using var cmd = _database.Command(connection, "SELECT COUNT(*) FROM Logs;");
        Assert.Equal(0L, Convert.ToInt64(cmd.ExecuteScalar()));
    }

    [Fact]
    public void Log_AtThreshold_AutoFlushes()
    {
        using var writer = new LogWriter(_database);
        var runId = Guid.NewGuid();

        for (var i = 0; i < 50; i++)
            writer.Log(runId, LogSeverity.Info, $"line {i}");

        using var connection = _database.OpenConnection();
        using var cmd = _database.Command(connection, "SELECT COUNT(*) FROM Logs;");
        Assert.Equal(50L, Convert.ToInt64(cmd.ExecuteScalar()));
    }

    [Fact]
    public void Dispose_FlushesRemainingBufferedLines()
    {
        var runId = Guid.NewGuid();
        var writer = new LogWriter(_database);
        writer.Log(runId, LogSeverity.Info, "last line before dispose");
        writer.Dispose();

        using var connection = _database.OpenConnection();
        using var cmd = _database.Command(connection, "SELECT Message FROM Logs WHERE RunId = $runId;");
        cmd.Bind(_database, "runId", runId.ToString());
        Assert.Equal("last line before dispose", Convert.ToString(cmd.ExecuteScalar()));
    }

    [Fact]
    public async Task ParallelLogWriters_NoLostWritesUnderContention()
    {
        const int writerCount = 8;
        const int linesPerWriter = 200;
        var runId = Guid.NewGuid();

        var writers = Enumerable.Range(0, writerCount).Select(_ => new LogWriter(_database)).ToList();
        try
        {
            var tasks = writers.Select((writer, idx) => Task.Run(() =>
            {
                for (var i = 0; i < linesPerWriter; i++)
                    writer.Log(runId, LogSeverity.Info, $"writer-{idx}-line-{i}");
            }));

            await Task.WhenAll(tasks);
        }
        finally
        {
            foreach (var writer in writers)
                writer.Dispose();
        }

        using var connection = _database.OpenConnection();
        using var cmd = _database.Command(connection, "SELECT COUNT(*) FROM Logs WHERE RunId = $runId;");
        cmd.Bind(_database, "runId", runId.ToString());
        Assert.Equal((long)(writerCount * linesPerWriter), Convert.ToInt64(cmd.ExecuteScalar()));
    }
}
