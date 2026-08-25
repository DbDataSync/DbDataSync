using Microsoft.AspNetCore.SignalR;

namespace DataSync.Api.Hubs;

/// <summary>Server-push only for v1: a client joins the group for a run id it cares about and
/// receives "logLine" / "runCompleted" events from RunMonitorService. No client->server RPC needed
/// beyond subscribing.</summary>
public sealed class RunHub : Hub
{
    public Task JoinRun(string runId) => Groups.AddToGroupAsync(Context.ConnectionId, runId);

    public Task LeaveRun(string runId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, runId);
}
