using System.Text.Json.Serialization;
using ClrKernel.Core.Secrets;
using DataSync.Api.Configuration;
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

DataSync.Api.DataSyncHost.Build(args).Run();

// Exposes the implicit top-level-statements Program class for WebApplicationFactory<Program> in tests.
public partial class Program;
