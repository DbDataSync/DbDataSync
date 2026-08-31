using System.Text.Json.Serialization;
using ClrKernel.Core.Secrets;
using DataSync.Api.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication;
using DataSync.Api.Auth;
using DataSync.Api.Hubs;
using DataSync.Api.Services;
using DataSync.Api.State;
using DataSync.State.Remote;
using DataSync.Core.Config;
using DataSync.Core.Git;
using DataSync.Drivers.Abstractions;
using DataSync.Drivers.MsSql;
using DataSync.Scripting;
using DataSync.Drivers.Postgres;
using DataSync.State;


namespace DataSync.Api;

/// <summary>
/// The application, assembled in one place.
/// <para>
/// Extracted from top-level statements when the CLI arrived: the tool's <c>serve</c> command and the
/// API's own entry point have to build the *same* graph, and two copies of a composition root drift
/// the first time somebody registers a service in one of them. <c>WebApplicationFactory&lt;Program&gt;</c>
/// still works because <c>Program</c> still exists and still calls this.
/// </para>
/// </summary>
public static class DataSyncHost
{
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Applied only when this process really is a service, so `datasync serve` in a terminal is
        // unaffected — UseWindowsService changes lifetime and logging, and doing that to an
        // interactive run would make Ctrl+C and console output behave unlike every other command.
        if (Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService())
            builder.Host.UseWindowsService(options => options.ServiceName = "DataSync");

        builder.Services.AddControllers()
            // Named explicitly, because controller discovery starts from the *entry* assembly and the
            // entry assembly is not always this one: under the CLI it is DataSync.Cli, and without
            // this the host starts perfectly and serves no API at all — "No action descriptors found"
            // in the log and a 404 on every route.
            .AddApplicationPart(typeof(DataSyncHost).Assembly)
            .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.AddOpenApi();
        builder.Services.AddSignalR();

        // ApiOptions is resolved from DI (not a plain variable computed here) so that config overrides
        // layered on after this point — e.g. WebApplicationFactory's ConfigureWebHost in tests — are picked
        // up correctly. Reading configuration into an eager local here silently ignores those overrides,
        // since they're applied to the builder as part of Build(), which hasn't run yet at this point.
        builder.Services.AddSingleton(sp => ApiOptions.FromConfiguration(sp.GetRequiredService<IConfiguration>()));

        // No real auth yet (architecture/detailed-design.md §8, open question) — every config write from the
        // API is attributed to this fixed system identity until per-user auth exists.
        // Per request, from whoever is signed in — which is what makes the config history in the
        // Version Control tab able to answer "who", and is the reason this feature is worth more than
        // access control. Falls back to a system identity where genuinely nobody is signed in.
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton(sp => AuthOptions.FromConfiguration(sp.GetRequiredService<IConfiguration>()));

        // A singleton that reads IHttpContextAccessor per call, rather than a scoped GitAuthor.
        // Registering the author itself as scoped looked tidier and made every singleton that writes
        // config — ProvisioningService among them — fail to construct: a singleton cannot hold a
        // per-request value, and the accessor exists precisely so it does not have to.
        builder.Services.AddSingleton<CurrentUser>();
        builder.Services.AddSingleton(sp => PasskeyOptions.FromConfiguration(sp.GetRequiredService<IConfiguration>()));
        builder.Services.AddSingleton<PasskeyService>();

        builder.Services.AddSingleton(new SecretStore(true));
        builder.Services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<ApiOptions>();
            return new ConfigRepository(
                Path.Combine(options.RepoRoot, "config"),
                new GitCommitService(options.RepoRoot),
                sp.GetRequiredService<SecretStore>());
        });

        builder.Services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<ApiOptions>();
            // SQLite keeps its own constructor and its own setting, so a deployment that has never
            // heard of phase 63 reaches exactly the code it always did.
            if (options.StateEngine == StateEngine.Sqlite)
                return new StateDatabase(options.StateDbPath);

            return new StateDatabase(
                options.StateEngine,
                options.StateConnectionString
                    ?? throw new InvalidOperationException(
                        $"DataSync:StateEngine is '{options.StateEngine}', which needs " +
                        "DataSync:StateConnectionString. Only SQLite is configured by path."));
        });
        builder.Services.AddSingleton(sp => new TaskRunStore(sp.GetRequiredService<StateDatabase>()));
        builder.Services.AddSingleton(sp => new RunMetricsStore(sp.GetRequiredService<StateDatabase>()));
        builder.Services.AddSingleton(sp => new VerificationResultStore(sp.GetRequiredService<StateDatabase>()));
        builder.Services.AddSingleton(sp => new UserStore(sp.GetRequiredService<StateDatabase>()));
        builder.Services.AddSingleton(sp => new SessionStore(sp.GetRequiredService<StateDatabase>()));
        builder.Services.AddSingleton(sp => new InviteStore(sp.GetRequiredService<StateDatabase>()));
        builder.Services.AddSingleton(sp => new ChangeWatermarkStore(sp.GetRequiredService<StateDatabase>()));
        builder.Services.AddSingleton(sp => new RunLockStore(sp.GetRequiredService<StateDatabase>()));
        builder.Services.AddSingleton(sp => new WorkQueueStore(sp.GetRequiredService<StateDatabase>()));
        builder.Services.AddSingleton(sp => new LogWriter(sp.GetRequiredService<StateDatabase>()));

        // This process owns the state file — LocalRunnerState is how it, and only it, writes to it. The
        // runners it spawns reach these same operations over loopback (RunnerStateController).
        builder.Services.AddSingleton<LocalRunnerState>();
        builder.Services.AddSingleton<RunnerToken>();
        builder.Services.AddSingleton<JournalRecovery>();

        // The state endpoint is a server of its own, listening on loopback only, so a remote connection is
        // refused by the OS and no application code has to be correct for that to hold — including when
        // someone puts the main API behind a reverse proxy or binds it to 0.0.0.0, which is how this would
        // realistically go wrong. RunnerStateGuard is the second defence, for when the binding is widened.
        // Started before the scheduler, because the scheduler spawns the children that need it.
        builder.Services.AddSingleton<StateHost>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<StateHost>());

        builder.Services.AddSingleton(sp =>
            ScriptCacheDirectory.BesideStateDatabase(sp.GetRequiredService<ApiOptions>().StateDbPath));
        builder.Services.AddSingleton<ScriptCompiler>();
        builder.Services.AddSingleton<ScriptHost>();

        builder.Services.AddSingleton(sp =>
        {
            // The scripted reader is composed here rather than inside a driver, because it needs the script
            // host and a driver must not depend on Roslyn. From every caller's side it is a reader the driver
            // has — including the capability endpoint the SPA's reader picker is built from.
            var registry = new DriverRegistry();
            var scriptHost = sp.GetRequiredService<ScriptHost>();
            registry.RegisterWithScripting(new MsSqlDriver(), scriptHost);
            registry.RegisterWithScripting(new PostgresDriver(), scriptHost);
            return registry;
        });
        builder.Services.AddSingleton<ScriptedMetadata>();
        builder.Services.AddSingleton<DriverConnectionFactory>();
        builder.Services.AddSingleton<MetadataService>();
        builder.Services.AddSingleton<ProvisioningService>();
        builder.Services.AddSingleton<ScriptUsageScanner>();
        builder.Services.AddSingleton<ParameterCheck>();
        builder.Services.AddSingleton<PreviewService>();
        builder.Services.AddSingleton<ScriptTestService>();
        builder.Services.AddSingleton<ProcessSupervisor>();
        builder.Services.AddSingleton<SegmentingStrategyRunner>();
        builder.Services.AddSingleton<CustomSegmentExpansion>();
        builder.Services.AddSingleton<BackfillService>();
        builder.Services.AddSingleton<SegmentingPreviewService>();
        builder.Services.AddSingleton<ResyncService>();
        builder.Services.AddHostedService<SchedulerService>();
        builder.Services.AddHostedService<RunMonitorService>();
        builder.Services.AddHostedService<RunPruningService>();
        builder.Services.AddHostedService<BootstrapInvite>();

        // One scheme for every request — controllers and the hub alike — so there is one answer to
        // "who is this". Negotiate is registered alongside it and used by exactly one endpoint, which
        // trades a Windows identity for a session.
        var authentication = builder.Services
            .AddAuthentication(SessionAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(
                SessionAuthenticationHandler.SchemeName, _ => { });

        if (OperatingSystem.IsWindows())
            authentication.AddNegotiate();

        builder.Services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Admin, policy => policy.RequireRole(nameof(UserRole.Admin)))
            // An admin is a viewer too. Stating it here rather than putting both roles on every read
            // endpoint keeps the attribute on a controller meaning what it says.
            .AddPolicy(Policies.Viewer, policy => policy.RequireRole(nameof(UserRole.Admin), nameof(UserRole.Viewer)))
            // Anything unmarked is closed. An endpoint added later is admin-only by omission rather
            // than open by omission, which is the whole reason to state a fallback at all.
            .SetFallbackPolicy(new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .RequireRole(nameof(UserRole.Admin))
                .Build());

        var app = builder.Build();

        // At startup, so a deployment whose passkeys cannot possibly work says so now rather than
        // failing inside a browser API with a message that names nothing. A warning and not a refusal:
        // Windows authentication may be the only method this deployment intends to use.
        if (app.Services.GetRequiredService<PasskeyOptions>().Problem() is { } passkeyProblem)
            app.Logger.LogWarning("Passkeys are misconfigured and will not work: {Problem}", passkeyProblem);

        if (app.Environment.IsDevelopment())
            app.MapOpenApi();

        // Only when an HTTPS URL is actually bound. Unconditionally, this redirects to a port nothing is
        // listening on — which is every containerised and every plain-HTTP deployment, and produces a
        // browser that cannot reach a server that is running perfectly well.
        if (app.Urls.Any(u => u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            || (Environment.GetEnvironmentVariable("ASPNETCORE_URLS")?.Contains("https://", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            app.UseHttpsRedirection();
        }

        app.UseAuthentication();
        app.UseAuthorization();

        // The SPA, served by the same process. In development Vite serves it on its own port and proxies
        // /api and /hubs here; a published build puts it in wwwroot, which is what makes the tool and the
        // container one artifact rather than two things to run.
        app.UseDefaultFiles();
        app.UseStaticFiles();

        app.MapControllers();
        app.MapHub<RunHub>("/hubs/run");

        // Deep links are the SPA's routes, not this server's — /replications/x/mappings/y has to return the
        // app. The exclusion is the load-bearing part: without it an unmatched /api route returns index.html
        // with a 200, and the client parses HTML as JSON and reports something incomprehensible instead of a
        // 404. Asserted in ApiFallbackTests rather than trusted to the routing table.
        app.MapFallback(context =>
        {
            var path = context.Request.Path.Value ?? "";
            if (path.StartsWith("/api", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/hubs", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
            }

            var index = Path.Combine(app.Environment.WebRootPath ?? "", "index.html");
            if (!File.Exists(index))
            {
                // A published build always has one. Running the API straight out of the repo does not, and
                // saying so beats a 404 that looks like a routing bug.
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return context.Response.WriteAsync(
                    "No web assets are published with this build. Run the SPA's dev server, or publish the " +
                    "API (which builds it into wwwroot).");
            }

            context.Response.ContentType = "text/html";
            return context.Response.SendFileAsync(index);
        });

        return app;
    }
}
