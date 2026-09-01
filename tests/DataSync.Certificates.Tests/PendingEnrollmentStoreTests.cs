namespace DataSync.Certificates.Tests;

public sealed class PendingEnrollmentStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ds-pending-" + Guid.NewGuid());

    public PendingEnrollmentStoreTests() => Directory.CreateDirectory(_root);

    public void Dispose() => GitTempDirectory.DeleteRecursively(_root);

    private static PendingEnrollment Sample(string requestId) => new(
        requestId, KeyName: "datasync-cert-1", SubjectCommonName: "datasync.example.com",
        DnsNames: ["datasync.example.com"], CaConfig: "CASERVER\\CA Name",
        SubmittedAtUtc: DateTimeOffset.UtcNow);

    [Fact]
    public void SaveThenFind_RoundTrips()
    {
        PendingEnrollmentStore.Save(_root, Sample("req-1"));

        var found = PendingEnrollmentStore.Find(_root, "req-1");

        Assert.NotNull(found);
        Assert.Equal("datasync-cert-1", found!.KeyName);
    }

    [Fact]
    public void Find_UnknownRequestId_ReturnsNull() =>
        Assert.Null(PendingEnrollmentStore.Find(_root, "never-submitted"));

    [Fact]
    public void Remove_DropsOnlyTheNamedRequest()
    {
        PendingEnrollmentStore.Save(_root, Sample("req-1"));
        PendingEnrollmentStore.Save(_root, Sample("req-2"));

        PendingEnrollmentStore.Remove(_root, "req-1");

        Assert.Null(PendingEnrollmentStore.Find(_root, "req-1"));
        Assert.NotNull(PendingEnrollmentStore.Find(_root, "req-2"));
    }

    [Fact]
    public void Save_AddsAGitignoreEntry_SoTheFileNeverShowsAsUntracked()
    {
        PendingEnrollmentStore.Save(_root, Sample("req-1"));

        var gitignore = File.ReadAllLines(Path.Combine(_root, ".gitignore"));
        Assert.Contains("pending-certificate-enrollments.json", gitignore);
    }

    [Fact]
    public void Save_Twice_DoesNotDuplicateTheGitignoreEntry()
    {
        PendingEnrollmentStore.Save(_root, Sample("req-1"));
        PendingEnrollmentStore.Save(_root, Sample("req-2"));

        var gitignore = File.ReadAllLines(Path.Combine(_root, ".gitignore"));
        Assert.Single(gitignore, l => l == "pending-certificate-enrollments.json");
    }

    [Fact]
    public void Save_AppendsToAnExistingGitignore_WithoutDisturbingItsOtherLines()
    {
        File.WriteAllText(Path.Combine(_root, ".gitignore"), "datasync.config.yaml.bak");

        PendingEnrollmentStore.Save(_root, Sample("req-1"));

        var gitignore = File.ReadAllLines(Path.Combine(_root, ".gitignore"));
        Assert.Contains("datasync.config.yaml.bak", gitignore);
        Assert.Contains("pending-certificate-enrollments.json", gitignore);
    }
}
