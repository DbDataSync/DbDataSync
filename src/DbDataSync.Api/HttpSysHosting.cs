using System.Linq.Expressions;
using System.Reflection;

namespace DbDataSync.Api;

/// <summary>
/// The one place that touches HTTP.sys, by reflection rather than by reference.
/// <para>
/// **Why reflection.** The reference assembly for <c>Microsoft.AspNetCore.Server.HttpSys</c> is a stub on
/// some Linux SDKs (distro-packaged ones ship a 9 KB assembly holding only resource strings), so a direct
/// <c>UseHttpSys</c> call fails to compile on a machine that can build everything else here. HTTP.sys only
/// ever runs on Windows, where the real assembly is part of the shared framework, so resolving it at
/// runtime costs nothing there and keeps every other platform's build working. If a direct reference is
/// ever acceptable everywhere this collapses to a four-line <c>UseHttpSys</c> call.
/// </para>
/// </summary>
internal static class HttpSysHosting
{
    private const string AssemblyName = "Microsoft.AspNetCore.Server.HttpSys";

    /// <summary><c>HttpSysDefaults.AuthenticationScheme</c> — the scheme HTTP.sys registers its handler under.</summary>
    public const string AuthenticationScheme = "Microsoft.AspNetCore.Server.HttpSys";

    /// <summary>
    /// Switches <paramref name="builder"/> to HTTP.sys. Anonymous requests stay allowed, because the SPA,
    /// static files and <c>/api/health</c> are open and sessions (not a Windows identity) answer "who is
    /// this" everywhere else. Negotiate/NTLM are only offered when Windows sign-in is configured, and only
    /// <em>challenged</em> by the sign-in endpoint.
    /// </summary>
    public static void Use(WebApplicationBuilder builder, bool windowsAuth)
    {
        Assembly assembly;
        try
        {
            assembly = Assembly.Load(AssemblyName);
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            throw new InvalidOperationException($"HTTP.sys is not available in this runtime: {ex.Message}", ex);
        }

        var optionsType = Required(assembly, "Microsoft.AspNetCore.Server.HttpSys.HttpSysOptions");
        var schemesType = Required(assembly, "Microsoft.AspNetCore.Server.HttpSys.AuthenticationSchemes");
        var extensions = Required(assembly, "Microsoft.AspNetCore.Hosting.WebHostBuilderHttpSysExtensions");
        var configureType = typeof(Action<>).MakeGenericType(optionsType);
        var useHttpSys = extensions.GetMethod("UseHttpSys", [typeof(IWebHostBuilder), configureType])
            ?? throw new InvalidOperationException("HTTP.sys's UseHttpSys(IWebHostBuilder, Action<HttpSysOptions>) was not found.");

        // options => { options.Authentication.AllowAnonymous = true; options.Authentication.Schemes = ...; }
        var options = Expression.Parameter(optionsType, "options");
        var authentication = Expression.Property(options, "Authentication");
        var schemes = Enum.Parse(schemesType, windowsAuth ? "Negotiate, NTLM" : "None");
        var configure = Expression.Lambda(
                configureType,
                Expression.Block(
                    Expression.Assign(Expression.Property(authentication, "AllowAnonymous"), Expression.Constant(true)),
                    Expression.Assign(Expression.Property(authentication, "Schemes"), Expression.Constant(schemes, schemesType))),
                options)
            .Compile();

        useHttpSys.Invoke(null, [builder.WebHost, configure]);
    }

    private static Type Required(Assembly assembly, string name) =>
        assembly.GetType(name)
        ?? throw new InvalidOperationException($"HTTP.sys type '{name}' was not found in {AssemblyName}.");
}
