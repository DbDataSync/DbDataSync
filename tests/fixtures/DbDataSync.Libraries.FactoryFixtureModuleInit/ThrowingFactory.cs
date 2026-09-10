using System.Data.Common;
using System.Runtime.CompilerServices;

namespace DbDataSync.Libraries.FactoryFixtureModuleInit;

/// <summary>The one qualifying type — same shape as <c>FactoryFixtureOne.GoodFactory</c>. This
/// assembly's whole point is the module initializer below.</summary>
public sealed class ThrowingFactory : DbProviderFactory
{
    public static readonly ThrowingFactory Instance = new();
}

/// <summary>Runs the moment this assembly is *actually* loaded (a real
/// <see cref="System.Runtime.Loader.AssemblyLoadContext"/> load, never a
/// <see cref="System.Reflection.MetadataLoadContext"/> one, which only reads metadata) — proves
/// <c>FactoryTypeReflector.Discover</c> never executes anything in the assemblies it scans.</summary>
internal static class ModuleInit
{
    [ModuleInitializer]
    public static void Init() =>
        throw new InvalidOperationException(
            "This module initializer ran — FactoryTypeReflector.Discover must not load assemblies for real.");
}
