using DbDataSync.Api.Auth;
using DbDataSync.State;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DbDataSync.Api.Controllers;

/// <summary>
/// The notification feed and this caller's cursor into it — see phase 77.
/// <para>
/// **Shaped like the run log's endpoint, deliberately.** A client holds the highest Id it has and
/// polls for what is above it; the server keeps nothing per connection. Everything about a missed
/// poll, a repeated poll, a second tab or a reload is thereby already answered.
/// </para>
/// <para>
/// **Viewer-readable.** The feed says a replication failed or was paused, which is exactly what the
/// run history and the pause history already tell a viewer. Nothing here is a permission the reader
/// does not already have through another screen.
/// </para>
/// </summary>
[ApiController]
[Route("api/notifications")]
public sealed class NotificationsController(
    NotificationStore notifications,
    CurrentUser currentUser) : ControllerBase
{
    /// <summary>
    /// Everything newer than <paramref name="sinceId"/>, oldest first, with this caller's unread
    /// count. Omitting <paramref name="sinceId"/> is the initial load and returns the whole feed,
    /// which retention bounds.
    /// </summary>
    /// <remarks>
    /// The unread count is computed from the stored cursor rather than from <paramref name="sinceId"/>
    /// — they are different questions. <c>sinceId</c> is "what have you not sent me yet", which a
    /// polling client advances every few seconds; the cursor is "what has this person acknowledged",
    /// which only a deliberate mark-seen moves. Deriving one from the other would clear the badge for
    /// anyone who merely left the tab open.
    /// </remarks>
    [Authorize(Policies.Viewer)]
    [HttpGet]
    public ActionResult<NotificationFeed> Get([FromQuery] long? sinceId = null)
    {
        var cursor = currentUser.Id is { } userId ? notifications.GetCursor(userId) : null;

        return Ok(new NotificationFeed(
            notifications.List(sinceId),
            cursor,
            notifications.UnreadCount(cursor),
            // False where there is nobody to be. See MarkSeen.
            Personalized: currentUser.Id is not null));
    }

    /// <summary>
    /// Advances this caller's cursor, so everything at or below <see cref="MarkSeenRequest.LastSeenNotificationId"/>
    /// stops counting as unread. Monotonic — see <see cref="NotificationStore.MarkSeen"/>.
    /// </summary>
    /// <remarks>
    /// **Authentication-disabled deployments get a 200 that says nothing was stored.** With
    /// <c>DbDataSync:Auth:Network:Admin=loopback</c> there is no <c>CurrentUser.Id</c> to key a cursor to, so the
    /// feed permanently reads as unread for everybody, and the response says so through
    /// <c>Personalized: false</c> rather than through a 500, a 401, or a silent success that a client
    /// would reasonably read as "cleared". A badge that cannot be dismissed is a defensible mode; a
    /// badge that reports itself dismissed and comes straight back is not.
    /// </remarks>
    [Authorize(Policies.Viewer)]
    [HttpPost("seen")]
    public ActionResult<NotificationFeed> MarkSeen([FromBody] MarkSeenRequest request)
    {
        if (currentUser.Id is not { } userId)
            return Ok(new NotificationFeed([], null, notifications.UnreadCount(null), Personalized: false));

        notifications.MarkSeen(userId, request.LastSeenNotificationId);

        var cursor = notifications.GetCursor(userId);
        return Ok(new NotificationFeed([], cursor, notifications.UnreadCount(cursor), Personalized: true));
    }
}

/// <param name="LastSeenNotificationId">The highest Id the caller is acknowledging — normally the last
/// one it was handed. Lower than the stored cursor is not an error; it is simply already true.</param>
public sealed record MarkSeenRequest(long LastSeenNotificationId);
