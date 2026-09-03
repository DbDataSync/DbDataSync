using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DbDataSync.Core.Config;

namespace DbDataSync.State.Remote;

/// <summary>
/// <see cref="IRunnerConfig"/> over HTTP to the process that owns the config repository — the same
/// loopback channel, token and route prefix <see cref="RemoteRunnerState"/> uses.
/// <para>
/// **Everything here is best-effort, and that is a design decision rather than a shortcut.**
/// <see cref="RemoteRunnerState"/> has two behaviours because state has two kinds of member: a
/// prerequisite it must not invent, and an outcome it must not lose. A provisioning report is neither.
/// It is not a prerequisite — the run it belongs to has already fixed its own pass in memory and
/// carries on regardless. And it is not lose-able in the way an outcome is: the pass that produced it
/// will produce it again, because the condition it fires on is "this mapping's target shape is not in
/// the cache", which is still true next time round. So there is no journal, and a delivery failure is
/// reported and stepped over rather than escalated.
/// </para>
/// <para>
/// A refusal is stepped over too, not just an unreachable owner. A 4xx here means the owner declined a
/// write the runner had no business insisting on, and failing a pass whose table was created and whose
/// rows are ready to write — over a cache entry the next pass will offer again — would turn a
/// reporting bug into a replication outage. It is said out loud instead.
/// </para>
/// </summary>
public sealed class RemoteRunnerConfig(HttpClient http, Action<string> report) : IRunnerConfig
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public void ReportProvisionedTargetColumns(
        string replicationName, string mappingName, IReadOnlyList<CachedColumn> columns)
    {
        var body = new ReportProvisionedTargetColumnsRequest(replicationName, mappingName, columns);

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post, $"{StateProtocol.Route}/report-provisioned-target-columns")
            {
                Content = JsonContent.Create(body, body.GetType(), options: Json),
            };

            using var response = http.Send(request);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            // Named precisely, because the consequence is specific and small: this pass is fine, and
            // the next auto-provisioning pass on this mapping offers the same report again. Whoever
            // reads this should not go looking for a lost run.
            report(
                $"Could not report '{mappingName}''s provisioned target columns to the config owner " +
                $"({ex.GetType().Name}: {ex.Message}). This pass is unaffected — it is using the shape it " +
                "just provisioned — and the next auto-provisioning pass reports it again.");
        }
    }
}
