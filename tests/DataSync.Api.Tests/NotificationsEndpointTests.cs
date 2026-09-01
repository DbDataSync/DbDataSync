using System.Net;
using System.Net.Http.Json;
using DataSync.Api.Configuration;
using DataSync.Api.Services;
using DataSync.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataSync.Api.Tests;

/// <summary>
/// The notification endpoint over HTTP — see phase 77. What the store's own tests cannot cover: the
/// cursor being keyed to the signed-in caller, and what happens where there isn't one.
/// </summary>
public sealed class NotificationsEndpointTests
{
    private static Guid FailARun(IServiceProvider services, string taskName, string error)
    {
        var runId = services.GetRequiredService<WorkQueueStore>().Enqueue(taskName, RunKind.Primary, "orders");
        var runs = services.GetRequiredService<TaskRunStore>();
        runs.BeginRun(runId, pid: null);
        runs.CompleteRun(runId, RunStatus.Failed, 0, 0, error);
        return runId;
    }

    /// <summary>
    /// Authentication **on**: two signed-in people, one feed, two cursors. This is the claim the whole
    /// read-state table exists for, and it cannot be made against the anonymous factory.
    /// </summary>
    public sealed class WithAuthentication(AuthenticatedApiFactory factory) : IClassFixture<AuthenticatedApiFactory>
    {
        [Fact]
        public async Task OneUserMarkingSeen_DoesNotClearAnothersBadge()
        {
            var task = $"crm-{Guid.NewGuid():N}";
            FailARun(factory.Services, task, "first");
            FailARun(factory.Services, task, "second");

            var alice = await factory.SignedInAsAsync(UserRole.Admin);
            var bob = await factory.SignedInAsAsync(UserRole.Viewer);

            var aliceFeed = await alice.GetFromJsonAsync<NotificationFeed>("/api/notifications");
            Assert.NotNull(aliceFeed);
            Assert.True(aliceFeed!.Personalized);
            Assert.True(aliceFeed.UnreadCount >= 2);
            Assert.Null(aliceFeed.LastSeenNotificationId);

            var highest = aliceFeed.Notifications[^1].Id;
            var seen = await alice.PostAsJsonAsync("/api/notifications/seen", new { lastSeenNotificationId = highest });
            seen.EnsureSuccessStatusCode();

            var aliceAfter = await alice.GetFromJsonAsync<NotificationFeed>("/api/notifications");
            Assert.Equal(0, aliceAfter!.UnreadCount);
            Assert.Equal(highest, aliceAfter.LastSeenNotificationId);

            // Bob acknowledged nothing. A cursor shared between them would have silenced his badge
            // the moment Alice looked at hers.
            var bobFeed = await bob.GetFromJsonAsync<NotificationFeed>("/api/notifications");
            Assert.True(bobFeed!.UnreadCount >= 2);
            Assert.Null(bobFeed.LastSeenNotificationId);
        }

        [Fact]
        public async Task AViewer_CanReadTheFeedAndMarkItSeen()
        {
            FailARun(factory.Services, $"crm-{Guid.NewGuid():N}", "boom");

            var viewer = await factory.SignedInAsAsync(UserRole.Viewer);

            var feed = await viewer.GetFromJsonAsync<NotificationFeed>("/api/notifications");
            Assert.NotEmpty(feed!.Notifications);

            // Marking your own notifications read is not an administrative act, and a feed a viewer
            // can see but never dismiss is a badge they learn to ignore.
            var response = await viewer.PostAsJsonAsync(
                "/api/notifications/seen", new { lastSeenNotificationId = feed.Notifications[^1].Id });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    /// <summary>
    /// Authentication **off** — <c>DataSync:Auth:Disabled=true</c>, which is what
    /// <see cref="TestApiFactory"/> configures. There is no <c>CurrentUser.Id</c> here, and the
    /// defined answer is "everything is unread, forever", said out loud.
    /// </summary>
    public sealed class WithoutAuthentication(TestApiFactory factory) : IClassFixture<TestApiFactory>
    {
        [Fact]
        public async Task TheFeedIsServed_AndSaysItIsNotPersonalized()
        {
            FailARun(factory.Services, $"crm-{Guid.NewGuid():N}", "boom");

            var feed = await factory.CreateClient().GetFromJsonAsync<NotificationFeed>("/api/notifications");

            Assert.NotNull(feed);
            Assert.False(feed!.Personalized);
            Assert.Null(feed.LastSeenNotificationId);
            Assert.True(feed.UnreadCount > 0);
            // The notification itself is not dropped. Nobody being signed in is a reason not to
            // personalise read state, not a reason to stop telling anyone anything.
            Assert.NotEmpty(feed.Notifications);
        }

        [Fact]
        public async Task MarkSeen_Succeeds_AndReportsThatNothingWasStored()
        {
            FailARun(factory.Services, $"crm-{Guid.NewGuid():N}", "boom");
            var client = factory.CreateClient();

            var before = await client.GetFromJsonAsync<NotificationFeed>("/api/notifications");
            var response = await client.PostAsJsonAsync(
                "/api/notifications/seen", new { lastSeenNotificationId = before!.Notifications[^1].Id });

            // Not a 500 and not a 401 — a null user id is a configuration this deployment chose, and
            // the honest answer is a 200 that says the cursor was not stored.
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<NotificationFeed>();
            Assert.False(body!.Personalized);

            // And it did not quietly clear: the count is unchanged, which is the whole point of
            // saying so rather than returning a success the client would read as "dismissed".
            var after = await client.GetFromJsonAsync<NotificationFeed>("/api/notifications");
            Assert.Equal(before.UnreadCount, after!.UnreadCount);
        }

        [Fact]
        public async Task SinceId_ReturnsOnlyWhatIsNewer_AndOmittingItReturnsEverything()
        {
            FailARun(factory.Services, $"crm-{Guid.NewGuid():N}", "first");
            var client = factory.CreateClient();

            var all = await client.GetFromJsonAsync<NotificationFeed>("/api/notifications");
            Assert.NotEmpty(all!.Notifications);
            var highest = all.Notifications[^1].Id;

            var newer = await client.GetFromJsonAsync<NotificationFeed>($"/api/notifications?sinceId={highest}");
            Assert.Empty(newer!.Notifications);

            FailARun(factory.Services, $"crm-{Guid.NewGuid():N}", "second");

            var next = await client.GetFromJsonAsync<NotificationFeed>($"/api/notifications?sinceId={highest}");
            Assert.Equal("second", Assert.Single(next!.Notifications).Message.Split(": ")[^1]);
        }
    }

    /// <summary>
    /// That the feed is swept by the service that already sweeps run history, on the same tick — the
    /// claim that justifies this table having no background service of its own, and the one that
    /// would quietly stop being true if the third delete were dropped from
    /// <see cref="RunPruningService.PruneAsync"/>.
    /// </summary>
    public sealed class Pruning(TestApiFactory factory) : IClassFixture<TestApiFactory>
    {
        [Fact]
        public async Task TheExistingSweep_AlsoAgesOutNotifications()
        {
            var database = factory.Services.GetRequiredService<StateDatabase>();
            var notifications = factory.Services.GetRequiredService<NotificationStore>();

            var stale = $"stale-{Guid.NewGuid():N}";
            FailARun(factory.Services, stale, "ancient");
            Backdate(database, stale, TimeSpan.FromDays(365));
            var fresh = $"fresh-{Guid.NewGuid():N}";
            FailARun(factory.Services, fresh, "recent");

            var service = new RunPruningService(
                factory.Services.GetRequiredService<TaskRunStore>(),
                factory.Services.GetRequiredService<ChangeCheckStore>(),
                notifications,
                factory.Services.GetRequiredService<ApiOptions>(),
                NullLogger<RunPruningService>.Instance);

            await service.PruneAsync(
                maxAge: TimeSpan.FromDays(90), maxPerMapping: null, checkMaxAge: TimeSpan.FromDays(7));

            var remaining = notifications.List().Select(n => n.TaskName).ToList();
            Assert.DoesNotContain(stale, remaining);
            Assert.Contains(fresh, remaining);
        }

        private static void Backdate(StateDatabase database, string taskName, TimeSpan age)
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection,
                "UPDATE Notifications SET CreatedAtUtc = $created WHERE TaskName = $taskName;");
            cmd.Bind(database, "created", (DateTimeOffset.UtcNow - age).ToString("O"));
            cmd.Bind(database, "taskName", taskName);
            cmd.ExecuteNonQuery();
        }
    }
}
