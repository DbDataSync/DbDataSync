using ClrKernel.Core.Secrets;
using DbDataSync.Core.Config;
using DbDataSync.Core.Git;
using LibGit2Sharp;

namespace DbDataSync.Core.Tests;

/// <summary>
/// A declared parameter has to survive being written to disk and read back. The failure mode is
/// invisible until the second load: YamlDotNet constructs through a parameterless constructor and
/// property setters, so a type that serializes perfectly well can throw on the way back in — which
/// is a broken script config an operator can no longer open, not a warning.
/// </summary>
public sealed class ScriptParameterYamlRoundTripTests : IDisposable
{
    private static readonly GitAuthor Author = new("Test", "test@example.com");

    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-params-").FullName;
    private readonly ConfigRepository _config;

    public ScriptParameterYamlRoundTripTests()
    {
        Repository.Init(_root);
        _config = new ConfigRepository(
            Path.Combine(_root, "config"), new GitCommitService(_root),
            SecretStore.ForProviders([new InMemorySecretProvider()]));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void AVarargParameter_SurvivesARoundTrip()
    {
        var script = new ScriptConfig
        {
            Name = "s",
            Language = ScriptLanguage.CSharp,
            Kind = "metadataProvider",
            Parameters =
            [
                new ParameterDescriptor
                {
                    Name = "segments",
                    Type = ParameterType.Dropdown,
                    Cardinality = new ParameterCardinality(0, 5),
                    DropdownOptions = ["a", "b"],
                    Layout = new ParameterLayout("Segmentation", "keys", 2),
                },
            ],
        };

        var read = RoundTrip(script);

        var parameter = Assert.Single(read.Parameters);
        Assert.Equal("segments", parameter.Name);
        Assert.Equal(new ParameterCardinality(0, 5), parameter.Cardinality);
        Assert.Equal(new ParameterLayout("Segmentation", "keys", 2), parameter.Layout);
    }

    /// <summary>The common case: no cardinality, no layout, and both still absent afterwards rather
    /// than materialised as something the operator never wrote.</summary>
    [Fact]
    public void APlainParameter_StaysPlain()
    {
        var read = RoundTrip(new ScriptConfig
        {
            Name = "s",
            Language = ScriptLanguage.CSharp,
            Kind = "metadataProvider",
            Parameters = [new ParameterDescriptor { Name = "threshold", Required = true }],
        });

        var parameter = Assert.Single(read.Parameters);
        Assert.Null(parameter.Cardinality);
        Assert.Null(parameter.Layout);
        Assert.True(parameter.Required);
    }

    private ScriptConfig RoundTrip(ScriptConfig manifest)
    {
        _config.SaveScript(new ScriptDefinition { Manifest = manifest, Code = "// none" }, Author);
        return _config.LoadScript(manifest.Name).Manifest;
    }
}
