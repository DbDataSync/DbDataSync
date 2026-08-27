using System.Text.Json.Serialization;
using ClrKernel.Core.Secrets;
using DataSync.Api.Configuration;
using DataSync.Api.Hubs;
using DataSync.Api.Services;
using DataSync.Core.Config;
using DataSync.Core.Git;
using DataSync.Drivers.Abstractions;
using DataSync.Drivers.MsSql;
using DataSync.Drivers.Postgres;
using DataSync.State;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
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
builder.Services.AddSingleton(new GitAuthor("DataSync API", "datasync@localhost"));

builder.Services.AddSingleton(new SecretStore(true));
builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<ApiOptions>();
    return new ConfigRepository(
        Path.Combine(options.RepoRoot, "config"),
        new GitCommitService(options.RepoRoot),
        sp.GetRequiredService<SecretStore>());
});

builder.Services.AddSingleton(sp => new StateDatabase(sp.GetRequiredService<ApiOptions>().StateDbPath));
builder.Services.AddSingleton(sp => new TaskRunStore(sp.GetRequiredService<StateDatabase>()));
builder.Services.AddSingleton(sp => new ChangeWatermarkStore(sp.GetRequiredService<StateDatabase>()));
builder.Services.AddSingleton(sp => new RunLockStore(sp.GetRequiredService<StateDatabase>()));
builder.Services.AddSingleton(sp => new WorkQueueStore(sp.GetRequiredService<StateDatabase>()));
builder.Services.AddSingleton(sp => new LogWriter(sp.GetRequiredService<StateDatabase>()));

builder.Services.AddSingleton(_ =>
{
    var registry = new DriverRegistry();
    registry.Register(new MsSqlDriver());
    registry.Register(new PostgresDriver());
    return registry;
});

builder.Services.AddSingleton<DriverConnectionFactory>();
builder.Services.AddSingleton<MetadataService>();
builder.Services.AddSingleton<ProcessSupervisor>();
builder.Services.AddSingleton<BackfillService>();
builder.Services.AddHostedService<SchedulerService>();
builder.Services.AddHostedService<RunMonitorService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();
app.MapHub<RunHub>("/hubs/run");

app.Run();

// Exposes the implicit top-level-statements Program class for WebApplicationFactory<Program> in tests.
public partial class Program;
