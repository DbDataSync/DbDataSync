using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataSync.Core.Config;
using LibGit2Sharp;
using Xunit;

namespace DataSync.Api.Tests;

/// <summary>
/// A config change is committed as the person who made it.
/// <para>
/// This is the part of authentication that is worth more than access control. Every write used to be
/// attributed to a fixed <c>GitAuthor("DataSync API", …)</c>, so the config history the Version Control
/// tab shows could say what changed and never who — which is most of what a history is for.
/// </para>
/// </summary>
public sealed class GitAttributionTests(AuthenticatedApiFactory factory) : IClassFixture<AuthenticatedApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public async Task AConfigChange_IsCommittedAsWhoeverMadeIt()
    {
        var (client, user) = await factory.SignedInWithUserAsync(DataSync.State.UserRole.Admin);
        var name = $"attributed-{Guid.NewGuid():N}";

        (await client.PutAsJsonAsync($"/api/replications/{name}", new ReplicationTaskConfig
        {
            Name = name,
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 3600 },
            ChangeProcessing = new ChangeProcessingConfig
            {
                Reader = new ReaderConfig { Kind = "MsSqlChangeTracking" },
                Cache = new CacheConfig { Kind = "MsSqlStagingTable" },
                Writer = new WriterConfig { Kind = "MsSqlMerge" },
            },
        }, JsonOptions)).EnsureSuccessStatusCode();

        using var repository = new Repository(factory.RepoRoot);
        var commit = repository.Commits.First();

        Assert.Equal(user.DisplayName, commit.Author.Name);
        Assert.Equal(user.Email, commit.Author.Email);
        Assert.Contains(name, commit.Message);
    }
}
