using System.Text.Json.Serialization;
using ClrKernel.Core.Secrets;
using DbDataSync.Api.Configuration;
using DbDataSync.Api.Hubs;
using DbDataSync.Api.Services;
using DbDataSync.Api.State;
using DbDataSync.State.Remote;
using DbDataSync.Core.Config;
using DbDataSync.Core.Git;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.MsSql;
using DbDataSync.Scripting;
using DbDataSync.Drivers.Postgres;
using DbDataSync.State;

DbDataSync.Api.DbDataSyncHost.Build(args).Run();

// Exposes the implicit top-level-statements Program class for WebApplicationFactory<Program> in tests.
public partial class Program;
