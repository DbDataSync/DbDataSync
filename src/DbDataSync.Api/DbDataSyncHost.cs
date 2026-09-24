using System.Text.Json.Serialization;
using ClrKernel.Core.Secrets;
using DbDataSync.Api.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication;
using DbDataSync.Api.Auth;
using DbDataSync.Api.Hubs;
using DbDataSync.Certificates;
using DbDataSync.Api.Services;
using DbDataSync.Api.State;
using DbDataSync.State.Remote;
using DbDataSync.Core.Config;
using DbDataSync.Core.Git;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.DuckDb;
using DbDataSync.Drivers.Generic;
using DbDataSync.Drivers.MsSql;
using DbDataSync.Drivers.MySql;
using DbDataSync.Drivers.Oracle;
using DbDataSync.Scripting;
using DbDataSync.Drivers.Descriptor;
using DbDataSync.Drivers.Postgres;
using DbDataSync.Libraries;
using DbDataSync.State;
using Microsoft.Extensions.Configuration.EnvironmentVariables;

namespace DbDataSync.Api;

/// <summary>
/// The application, assembled in one place.
/// <para>
/// Extracted from top-level statements when the CLI arrived: the tool's <c>serve</c> command and the
/// API's own entry point have to build the *same* graph, and two copies of a composition root drift
/// the first time somebody registers a service in one of them. <c>WebApplicationFactory&lt;Program&gt;</c>
/// still works because <c>Program</c> still exists and still calls this.
/// </para>
/// </summary>
public static class DbDataSyncHost
{
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            // Only the web root, not the content root: moving the content root would also change where appsettings.json is
            // read from, which is a much larger change than "find the console".
            WebRootPath = WebRootLocator.Resolve(Directory.GetCurrentDirectory(), AppContext.BaseDirectory),
        });
        InsertConfigFile(builder);

        // Applied only when this process really is a service, so `dbdatasync serve` in a terminal is
        // unaffected — UseWindowsService changes lifetime and logging, and doing that to an
        // interactive run would make Ctrl+C and console output behave unlike every other command.
        if (Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService())
            builder.Host.UseWindowsService(options => options.ServiceName = "DbDataSync");
        // Phase 111's Linux analog — UseSystemd() switches the lifetime to one that sends
        // sd_notify(READY=1) once the host has actually started (which is what lets the unit be
        // Type=notify: systemctl start blocks until the host is really serving, not until the process
        // merely exists) and routes logging to the journal format. Same "only when it's real" guard.
        else if (Microsoft.Extensions.Hosting.Systemd.SystemdHelpers.IsSystemdService())
            builder.Host.UseSystemd();

        builder.Services.AddControllers()
            // Named explicitly, because controller discovery starts from the *entry* assembly and the
            // entry assembly is not always this one: under the CLI it is DbDataSync.Cli, and without
            // this the host starts perfectly and serves no API at all — "No action descriptors found"
            // in the log and a 404 on every route.
            .AddApplicationPart(typeof(DbDataSyncHost).Assembly)
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
        builder.Services.AddSingleton(sp =>
            PasskeyOptions.FromConfiguration(sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<ApiOptions>()));
        builder.Services.AddSingleton<PasskeyService>();
        builder.Services.AddSingleton(sp => CertificateOptions.FromConfiguration(sp.GetRequiredService<IConfiguration>()));

        builder.Services.AddSingleton(new SecretStore("DbDataSync", true));

        // One GitCommitService per process, not one per consumer — its own doc comment explains why:
        // the in-process write lock that makes concurrent commits safe only holds if every writer to
        // this repo root shares the same instance. Phase 81 added a second writer (AdminConfigService,
        // for dbdatasync.config.yaml) alongside ConfigRepository, which is what made this worth pulling
        // out of ConfigRepository's own registration rather than each constructing its own.
        builder.Services.AddSingleton(sp => new GitCommitService(sp.GetRequiredService<ApiOptions>().RepoRoot));
        builder.Services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<ApiOptions>();
            return new ConfigRepository(
                Path.Combine(options.RepoRoot, "config"),
                sp.GetRequiredService<GitCommitService>(),
                sp.GetRequiredService<SecretStore>());
        });
        builder.Services.AddSingleton<RestartRequiredState>();
        builder.Services.AddSingleton<AdminConfigService>();
        builder.Services.AddSingleton<AdminCertificateService>();
        builder.Services.AddSingleton<LibrariesService>();
        builder.Services.AddSingleton<FilesService>();
        // A short timeout: this is a read-only search-index query an operator is waiting on in a
        // browser tab, not a background job — a slow or unreachable public index should degrade the
        // Libraries screen to manual entry in a few seconds, not hang the request.
        builder.Services.AddHttpClient(LibrarySearchService.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(5);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("DbDataSync-LibrarySearch/1.0");
        });
        builder.Services.AddSingleton<LibrarySearchService>();

        builder.Services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<ApiOptions>();
            // The same LibraryRegistry singleton the driver layer resolves through (registered above,
            // before this) — phase 109g put MsSql/PostgresStateDialect on it too, so a StateEngine of
            // MsSql or Postgres with that library not installed fails right here, at startup, naming
            // the fix, rather than starting an API that can never open its own state store.
            return StateDatabase.FromOptions(
                options.StateEngine, options.StateDbPath, options.StateConnectionString,
                sp.GetRequiredService<SecretStore>(), sp.GetRequiredService<LibraryRegistry>());
        });
        builder.Services.AddSingleton(sp => new TaskRunStore(sp.GetRequiredService<StateDatabase>()));
        builder.Services.AddSingleton(sp => new RunMetricsStore(sp.GetRequiredService<StateDatabase>()));
        builder.Services.AddSingleton(sp => new BulkLoadBatchStore(sp.GetRequiredService<StateDatabase>()));
        builder.Services.AddSingleton(sp => new VerificationResultStore(sp.GetRequiredService<StateDatabase>()));
        builder.Services.AddSingleton(sp => new UserStore(sp.GetRequiredService<StateDatabase>()));
        builder.Services.AddSingleton(sp => new SessionStore(sp.GetRequiredService<StateDatabase>()));
        builder.Services.AddSingleton(sp => new InviteStore(sp.GetRequiredService<StateDatabase>()));
        builder.Services.AddSingleton(sp => new ChangeWatermarkStore(sp.GetRequiredService<StateDatabase>()));
        builder.Services.AddSingleton(sp => new ChangeCheckStore(sp.GetRequiredService<StateDatabase>()));
        builder.Services.AddSingleton(sp => new NotificationStore(sp.GetRequiredService<StateDatabase>()));
        builder.Services.AddSingleton(sp => new RunLockStore(sp.GetRequiredService<StateDatabase>()));
        builder.Services.AddSingleton(sp => new WorkQueueStore(sp.GetRequiredService<StateDatabase>()));
        builder.Services.AddSingleton(sp => new LogWriter(sp.GetRequiredService<StateDatabase>()));

        // This process owns the state file — LocalRunnerState is how it, and only it, writes to it. The
        // runners it spawns reach these same operations over loopback (RunnerStateController).
        builder.Services.AddSingleton<LocalRunnerState>();
        builder.Services.AddSingleton<RunnerToken>();
        builder.Services.AddSingleton<JournalRecovery>();

        // And it owns config, on the same terms — LocalRunnerConfig is the one path by which a runner's
        // provisioning report becomes a real commit here (phase 94). Attributed to SystemAuthor: there
        // is no request and no operator behind this write, and CurrentUser.Author's own doc comment
        // reserves that identity for exactly this case.
        builder.Services.AddSingleton<IRunnerConfig>(sp =>
            new LocalRunnerConfig(sp.GetRequiredService<ConfigRepository>(), CurrentUser.SystemAuthor));

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

        // Loaded before DriverRegistry: a descriptor or a compiled driver registered from 109d/109e
        // on resolves its library through this, so the library closure has to be loadable first.
        // An absent or empty libraries/ directory is a silent no-op — most deployments have none.
        builder.Services.AddSingleton(sp =>
            new LibraryRegistry(sp.GetRequiredService<ApiOptions>().RepoRoot).LoadAll());

        builder.Services.AddSingleton(sp =>
        {
            // The scripted reader is composed here rather than inside a driver, because it needs the script
            // host and a driver must not depend on Roslyn. From every caller's side it is a reader the driver
            // has — including the capability endpoint the SPA's reader picker is built from.
            var registry = new DriverRegistry();
            var scriptHost = sp.GetRequiredService<ScriptHost>();
            var msSqlDriver = new MsSqlDriver();
            var postgresDriver = new PostgresDriver();
            var mySqlDriver = new MySqlDriver();
            var oracleDriver = new OracleDriver();
            var duckDbDriver = new DuckDbDriver();
            registry.RegisterWithScripting(msSqlDriver, scriptHost);
            registry.RegisterWithScripting(postgresDriver, scriptHost);
            registry.RegisterWithScripting(mySqlDriver, scriptHost);
            registry.RegisterWithScripting(oracleDriver, scriptHost);
            // DuckDb takes the same call and gets no ScriptedQuery reader out of it: it names a dialect
            // but supplies no ITableCatalog, and a scripted query builder is handed the source table's
            // columns by contract. Registered through the same helper anyway, so there is one
            // registration shape rather than a special case to keep in step.
            registry.RegisterWithScripting(duckDbDriver, scriptHost);

            // A descriptor-defined driver's library (resolved above) is already loadable; this is
            // what actually stands one up and puts it beside the built-ins.
            var libraryRegistry = sp.GetRequiredService<LibraryRegistry>();
            var repoRoot = sp.GetRequiredService<ApiOptions>().RepoRoot;
            var driverErrorLogger = (string message, Exception ex) =>
                sp.GetRequiredService<ILogger<DriverRegistry>>().LogError(ex, "{Message}", message);
            DriverLoader.LoadDescriptorDrivers(repoRoot, libraryRegistry, registry, driverErrorLogger);
            // Compiled plugins (109e) load after descriptors — neither ordering matters for
            // correctness (they don't reference each other), but this matches both manifest kinds
            // being enumerated in the same pass conceptually.
            DriverLoader.LoadCompiledDrivers(repoRoot, registry, driverErrorLogger);

            return registry;
        });
        builder.Services.AddSingleton<ScriptedMetadata>();
        builder.Services.AddSingleton<DriverConnectionFactory>();
        // Registered by interface as well, not instead — see IConnectionFactory's doc comment.
        // ProvisioningService depends on the interface so a test can hand it a counting fake; every
        // other caller still resolves the concrete type.
        builder.Services.AddSingleton<IConnectionFactory>(sp => sp.GetRequiredService<DriverConnectionFactory>());
        builder.Services.AddSingleton<MetadataService>();
        // Writing a mapping's cached column metadata goes through MetadataService above rather than
        // a catalog of its own, so what gets cached is the same answer the editor's picker showed —
        // see phase 90. Registered by interface as well, not instead: MetadataController still
        // resolves the concrete service for the questions the cache does not ask.
        builder.Services.AddSingleton<IColumnCatalog>(sp => sp.GetRequiredService<MetadataService>());
        // One reader behind both writers of that cache — Refresh, and the capture a bulk create does
        // as it creates (phase 95).
        builder.Services.AddSingleton<MappingColumnReader>();
        builder.Services.AddSingleton<MappingMetadataService>();
        builder.Services.AddSingleton<ProvisioningService>();
        builder.Services.AddSingleton<ScriptUsageScanner>();
        builder.Services.AddSingleton<ParameterCheck>();
        builder.Services.AddSingleton<PreviewService>();
        builder.Services.AddSingleton<ScriptTestService>();
        builder.Services.AddSingleton<ProcessSupervisor>();

        // Phase 159: updating this installation from the console. The drain state is read by the scheduler
        // and the request middleware; the facts describe how this process was installed and started.
        builder.Services.AddSingleton<UpdateDrainState>();
        builder.Services.AddSingleton(_ => UpdateHostFacts.Current());
        builder.Services.AddSingleton<IUpdateWorkProbe, SupervisorWorkProbe>();
        builder.Services.AddSingleton<IUpdateRestart, HostUpdateRestart>();
        builder.Services.AddSingleton<UpdateService>();
        builder.Services.AddHostedService<UpdateConfirmationService>();
        builder.Services.AddSingleton<LibraryValidationLauncher>();
        builder.Services.AddSingleton<SegmentingStrategyRunner>();
        builder.Services.AddSingleton<CustomSegmentExpansion>();
        builder.Services.AddSingleton<BulkLoadService>();
        // LocalRunnerState (DbDataSync.State) needs BulkLoadService's segment-expansion/enqueue core for
        // a runner-triggered initial load (phase 134) but cannot reference DbDataSync.Api directly
        // without a project-reference cycle — see IInitialLoadEnqueuer's own doc. Same pattern as
        // IConnectionFactory/DriverConnectionFactory above: registered by interface as well, not instead.
        builder.Services.AddSingleton<IInitialLoadEnqueuer>(sp => sp.GetRequiredService<BulkLoadService>());
        // Deferred, not resolved directly by LocalRunnerState's constructor — StateHost (an
        // IHostedService built eagerly at startup) depends on LocalRunnerState, and eagerly resolving
        // IInitialLoadEnqueuer here would force BulkLoadService -> ProcessSupervisor -> StateHost to
        // build too, closing a DI cycle that hangs the host before it logs a single line (this shipped
        // once without the Lazy wrapper and did exactly that in CI). The Lazy<T> itself is cheap to
        // construct — it just closes over `sp` — and only resolves the real service, well after
        // startup, the first time LocalRunnerState.RequestInitialLoad actually runs.
        builder.Services.AddSingleton(sp => new Lazy<IInitialLoadEnqueuer>(sp.GetRequiredService<IInitialLoadEnqueuer>));
        builder.Services.AddSingleton<ReconcileService>();
        builder.Services.AddSingleton<SegmentingPreviewService>();
        builder.Services.AddSingleton<ResyncService>();

        // The scheduler's polling gate, and the one piece of it that does I/O behind an interface so
        // the decision logic can be tested without a SQL Server — see phase 75.
        builder.Services.AddSingleton<IChangeCounterSource, DriverChangeCounterSource>();
        builder.Services.AddSingleton<ChangeSourceResolver>();
        builder.Services.AddSingleton<ChangePollingGate>();

        // Lag reads the same groups the gate writes, through the same resolution, so a mapping is
        // never grouped one way for the skip decision and another for the figure reported about it —
        // see phase 85.
        builder.Services.AddSingleton<ReaderLagService>();

        // Dating a run's stored watermarks reads the same polling history through the same
        // resolution, for the same reason — see phase 88.
        builder.Services.AddSingleton<RunWatermarkTimeService>();
        builder.Services.AddHostedService<SchedulerService>();
        builder.Services.AddHostedService<RunMonitorService>();
        builder.Services.AddHostedService<RunPruningService>();
        builder.Services.AddHostedService<BootstrapInvite>();

        // Windows-only, registered only there — the same gating this file already uses a few lines
        // down for Negotiate authentication, not a new idiom. Certificate management is Windows-only
        // end to end (phase 82), so a Linux host never constructs this hosted service at all.
        if (OperatingSystem.IsWindows())
            builder.Services.AddHostedService<CertificateExpiryService>();

        // Phase 130, tier 2: registered only when Kestrel:Certificates:Default:Path actually names this
        // phase's own well-known managed-certificate path — not gated by OS, unlike the Windows-only
        // service just above; tier 2's whole point is a certificate story that works on Linux. Read
        // directly off builder.Configuration, the same way InsertConfigFile resolves RepoRoot above:
        // this decision has to be made before the DI container exists, so ApiOptions (which comes from
        // DI) isn't resolvable yet.
        var certificateRepoRoot = builder.Configuration["DbDataSync:App:RepoRoot"]
            ?? Path.Combine(Directory.GetCurrentDirectory(), "dbdatasync-repo");
        if (string.Equals(
                builder.Configuration["Kestrel:Certificates:Default:Path"],
                ManagedSelfSignedCertificate.PfxPath(certificateRepoRoot),
                StringComparison.OrdinalIgnoreCase))
        {
            builder.Services.AddHostedService<SelfSignedCertificateService>();
        }

        // One scheme for every request — controllers and the hub alike — so there is one answer to
        // "who is this". Negotiate is registered alongside it and used by exactly one endpoint, which
        // trades a Windows identity for a session.
        var authentication = builder.Services
            .AddAuthentication(SessionAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(
                SessionAuthenticationHandler.SchemeName, _ => { });

        // Also gated on WindowsEnabled (phase 164: mode enabled *and* a group actually configured) —
        // found necessary by a real failure, not a hypothetical: with no group configured,
        // AddNegotiate() was still being registered on every Windows host (this check used to be purely
        // OperatingSystem.IsWindows()-gated), and merely having Negotiate registered as an available
        // scheme is enough for ASP.NET Core's real, Windows-native SSPI implementation to require
        // IConnectionItemsFeature on every request through the authentication middleware — a real
        // Kestrel connection feature TestServer (what WebApplicationFactory-based tests run against)
        // does not implement, throwing NotSupportedException even for a route nothing ever challenges
        // for Negotiate. Read directly off builder.Configuration, not DI, for the same reason
        // certificateRepoRoot above does: this runs before builder.Build(). A deployment with no
        // Windows group configured gets nothing to lose here either way — Negotiate existing but never
        // being the effective scheme was already true whenever nothing could ever satisfy it.
        if (OperatingSystem.IsWindows() && AuthOptions.FromConfiguration(builder.Configuration).WindowsEnabled)
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

        // A fresh process has, by construction, already picked up whatever the marker was recording.
        app.Services.GetRequiredService<RestartRequiredState>().Clear();

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

        // The SPA, served by the same process. In development Vite serves it on its own port and proxies
        // /api and /hubs here; a published build puts it in wwwroot, which is what makes the tool and the
        // container one artifact rather than two things to run.
        //
        // **Before authentication, and that is the point.** The fallback policy below closes everything that is not
        // explicitly opened, and it applies to a request no endpoint claims — which is what a static file is. Placed
        // after UseAuthorization, these answered 401 to `/` itself, so a default deployment (authentication on) could
        // not serve its own sign-in screen. What is in wwwroot is the app's code and the docs the build shipped, the
        // same text that is public on GitHub; the data is behind /api, which stays closed.
        app.UseDefaultFiles();
        app.UseStaticFiles();

        // While an update drains, anything that would change something answers 409. Before authentication: it
        // costs nothing and does not depend on who is asking.
        app.UseMiddleware<UpdateDrainMiddleware>();

        app.UseAuthentication();
        app.UseAuthorization();

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
        })
        // The app's own routes (/replications/x, /invite, /docs/...) are the SPA, which has to load before anyone has
        // signed in — it is what draws the sign-in screen. Without this the fallback policy answers 401 with no body.
        // Unmatched /api and /hubs paths still get the 404 above rather than the page, whoever asks.
        .AllowAnonymous();

        return app;
    }

    /// <summary>
    /// Wires <c>dbdatasync.config.yaml</c> in as an <see cref="IConfigurationSource"/> at the same
    /// precedence slot <c>appsettings.json</c> occupies — ahead of environment variables and the
    /// command line, both of which must still be able to override a file value for a one-off run.
    /// <para>
    /// **Inserted, not appended.** <c>builder.Configuration.Sources</c> already holds
    /// appsettings/appsettings.&lt;env&gt;/environment-variables/command-line, in that order, by the
    /// time <see cref="WebApplication.CreateBuilder(string[])"/> returns — later sources win ties. A
    /// plain <c>builder.Configuration.AddXyz(...)</c> call here would append this source *last*,
    /// making it win over an env var or CLI flag, which is backwards: the whole point of the file is
    /// to hold a default that a one-off <c>--DbDataSync:App:Url</c> or <c>DbDataSync__App__Url</c> can still
    /// override. Finding the first <see cref="EnvironmentVariablesConfigurationSource"/> and inserting
    /// there reproduces appsettings.json's own slot instead.
    /// </para>
    /// <para>
    /// The repo root is read straight off <c>builder.Configuration</c> rather than through
    /// <see cref="Configuration.ApiOptions"/> — <c>ApiOptions</c> isn't resolvable yet (it comes from
    /// DI, built later in this method), and by the time <c>CreateBuilder(args)</c> has returned, the
    /// command-line and environment sources it already added are enough to answer "which repo root" on
    /// their own. The default mirrors <c>ApiOptions.FromConfiguration</c>'s exactly, so this looks in
    /// the same place that class will end up saying <c>RepoRoot</c> is.
    /// </para>
    /// </summary>
    private static void InsertConfigFile(WebApplicationBuilder builder)
    {
        var repoRoot = builder.Configuration["DbDataSync:App:RepoRoot"]
            ?? Path.Combine(Directory.GetCurrentDirectory(), "dbdatasync-repo");

        if (!File.Exists(DbDataSyncConfigFile.PathIn(repoRoot)))
            return;

        var source = new DbDataSyncConfigFileSource { InitialData = DbDataSyncConfigFile.Read(repoRoot) };

        var sources = builder.Configuration.Sources;
        var envIndex = -1;
        for (var i = 0; i < sources.Count; i++)
        {
            if (sources[i] is EnvironmentVariablesConfigurationSource)
            {
                envIndex = i;
                break;
            }
        }

        if (envIndex < 0)
            sources.Add(source); // No environment source was registered (unusual host setup) — append.
        else
            sources.Insert(envIndex, source);
    }
}
