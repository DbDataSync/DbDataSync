using DbDataSync.Api.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace DbDataSync.Api.Hubs;

/// <summary>Server-push only for v1: a client joins the group for a run id it cares about and
/// receives "logLine" / "runCompleted" events from RunMonitorService. No client->server RPC needed
/// beyond subscribing.</summary>
// The surface that gets forgotten. A hub streaming a replication's live log to anyone who can reach
// the port is the same disclosure as an open API, and it authenticates by the same session cookie —
// which SignalR does send on its handshake, unlike a header it cannot set on a WebSocket.
[Authorize(Policies.Viewer)]
public sealed class RunHub : Hub
{
    public Task JoinRun(string runId) => Groups.AddToGroupAsync(Context.ConnectionId, runId);

    public Task LeaveRun(string runId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, runId);
}
