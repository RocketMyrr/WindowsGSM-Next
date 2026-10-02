using WindowsGSM.Agent.Notifications;
using WindowsGSM.Agent.Realtime;

namespace WindowsGSM.Agent.Api;

/// <summary>The notification centre (everyone, filtered to what they may see) and notification channels (owners).</summary>
public static class NotificationEndpoints
{
    public sealed record ReadRequest(long UpTo);
    public sealed record ChannelRequest(string Name, string Type, string? Url, string? Mention, bool Enabled, List<string>? Events, List<string>? Scope);

    public static void Map(RouteGroupBuilder api)
    {
        var signedIn = api.MapGroup("/notifications").AddEndpointFilter(async (efc, next) =>
            efc.HttpContext.RequestServices.GetRequiredService<AgentContext>().CurrentUser(efc.HttpContext) == null
                ? ApiResults.Unauthorized()
                : await next(efc));

        signedIn.MapGet("", (HttpContext http, AgentContext ctx, NotificationCentre centre, int? limit) =>
        {
            var user = ctx.CurrentUser(http)!;
            return Results.Json(new
            {
                items = centre.For(user, limit ?? 100).Select(NotificationCentre.ToDto),
                unread = centre.Unread(user),
                readUpTo = centre.ReadUpTo(user),
            });
        });

        signedIn.MapPost("/read", (HttpContext http, AgentContext ctx, NotificationCentre centre, ReadRequest body) =>
        {
            var user = ctx.CurrentUser(http)!;
            centre.MarkRead(user, body.UpTo);
            return Results.Json(new { unread = centre.Unread(user) });
        });

        signedIn.MapGet("/kinds", () => Results.Json(NotificationKinds.All));

        // ── Channels: admins can see them (URLs masked — they're secrets), owners change them ──

        signedIn.MapGet("/channels", (HttpContext http, AgentContext ctx, NotificationChannels channels) =>
            ctx.CurrentUser(http)!.IsAdmin ? Results.Json(channels.All().Select(ToDto)) : ApiResults.Forbidden());

        signedIn.MapPost("/channels", (HttpContext http, AgentContext ctx, NotificationChannels channels, ChannelRequest body) =>
        {
            if (!ctx.CurrentUser(http)!.IsOwner) { return ApiResults.Forbidden("Only owners can change notification channels."); }
            if (string.IsNullOrWhiteSpace(body.Url)) { return ApiResults.BadRequest("Enter the webhook URL."); }
            string? problem = channels.Save(null, FromRequest(body), out var saved);
            ctx.Record(http, "notify-channel-create", null, problem == null, problem ?? body.Name);
            return problem == null ? Results.Json(ToDto(saved!), statusCode: 201) : ApiResults.BadRequest(problem);
        });

        signedIn.MapPut("/channels/{id}", (HttpContext http, AgentContext ctx, NotificationChannels channels, string id, ChannelRequest body) =>
        {
            if (!ctx.CurrentUser(http)!.IsOwner) { return ApiResults.Forbidden("Only owners can change notification channels."); }
            if (channels.Get(id) == null) { return ApiResults.NotFound("No such channel."); }
            string? problem = channels.Save(id, FromRequest(body), out var saved);
            ctx.Record(http, "notify-channel-update", null, problem == null, problem ?? body.Name);
            return problem == null ? Results.Json(ToDto(saved!)) : ApiResults.BadRequest(problem);
        });

        signedIn.MapDelete("/channels/{id}", (HttpContext http, AgentContext ctx, NotificationChannels channels, string id) =>
        {
            if (!ctx.CurrentUser(http)!.IsOwner) { return ApiResults.Forbidden("Only owners can change notification channels."); }
            var c = channels.Get(id);
            if (c == null || !channels.Remove(id)) { return ApiResults.NotFound("No such channel."); }
            ctx.Record(http, "notify-channel-delete", null, true, c.Name);
            return Results.NoContent();
        });

        signedIn.MapPost("/channels/{id}/test", async (HttpContext http, AgentContext ctx, NotificationChannels channels, string id) =>
        {
            if (!ctx.CurrentUser(http)!.IsOwner) { return ApiResults.Forbidden("Only owners can test notification channels."); }
            var c = channels.Get(id);
            if (c == null) { return ApiResults.NotFound("No such channel."); }
            var sample = new NotificationEntry(0, DateTimeOffset.UtcNow, "autoRestarted", "info", "Test from WindowsGSM",
                $"If you can read this, \"{c.Name}\" is set up. Sent by {ctx.CurrentUser(http)!.Username}.", ctx.MachineId, null, null, Visibility.Admin);
            string? error = await channels.DeliverAsync(c, sample, http.RequestAborted);
            return error == null ? Results.Json(new { ok = true }) : ApiResults.Error(502, "delivery_failed", $"It didn't arrive: {error}");
        });
    }

    private static NotificationChannel FromRequest(ChannelRequest b) => new()
    {
        Name = b.Name ?? string.Empty, Type = b.Type ?? "discord", Url = b.Url ?? string.Empty, Mention = b.Mention, Enabled = b.Enabled,
        Events = b.Events ?? new(), Scope = b.Scope ?? new(),
    };

    /// <summary>The URL is a secret: only enough of it to recognise which one it is.</summary>
    private static object ToDto(NotificationChannel c) => new
    {
        id = c.Id, name = c.Name, type = c.Type, url = Mask(c.Url), mention = c.Mention, enabled = c.Enabled,
        events = c.Events, scope = c.Scope, lastSentAt = c.LastSentAt, lastError = c.LastError,
    };

    private static string Mask(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) { return "…"; }
        string tail = url.Length > 4 ? url[^4..] : "";
        return $"{u.Scheme}://{u.Host}/…{tail}";
    }
}
