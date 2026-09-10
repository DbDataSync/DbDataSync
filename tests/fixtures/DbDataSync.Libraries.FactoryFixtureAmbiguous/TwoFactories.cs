using System.Data.Common;

namespace DbDataSync.Libraries.FactoryFixtureAmbiguous;

/// <summary>Two qualifying subclasses in the same assembly — proves
/// <c>FactoryTypeReflector.Discover</c> reports <see cref="FactoryTypeDiscoveryStatus.Ambiguous"/>
/// rather than guessing between them.</summary>
public sealed class FirstFactory : DbProviderFactory
{
    public static readonly FirstFactory Instance = new();
}

public sealed class SecondFactory : DbProviderFactory
{
    public static readonly SecondFactory Instance = new();
}
