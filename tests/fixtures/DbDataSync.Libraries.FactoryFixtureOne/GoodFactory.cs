using System.Data.Common;

namespace DbDataSync.Libraries.FactoryFixtureOne;

/// <summary>The one shape <c>FactoryTypeReflector.Discover</c> is meant to find: a public,
/// non-abstract <see cref="DbProviderFactory"/> subclass with a public static <c>Instance</c>
/// field — every bundled provider's own convention.</summary>
public sealed class GoodFactory : DbProviderFactory
{
    public static readonly GoodFactory Instance = new();
}

/// <summary>Not public — must be skipped even though it otherwise qualifies.</summary>
internal sealed class InternalFactory : DbProviderFactory
{
    public static readonly InternalFactory Instance = new();
}

/// <summary>Abstract — must be skipped even though it otherwise qualifies.</summary>
public abstract class AbstractFactory : DbProviderFactory;

/// <summary>A public type that has nothing to do with <see cref="DbProviderFactory"/> — proves the
/// scan does not just match "any public type in the assembly".</summary>
public sealed class NotAFactory;
