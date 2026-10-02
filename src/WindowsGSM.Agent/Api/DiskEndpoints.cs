using WindowsGSM.Agent.Hosting;

namespace WindowsGSM.Agent.Api;

/// <summary>Disk space on a machine: usage per server and safe clean-up (admins).</summary>
public static class DiskEndpoints
{
    public sealed record CleanupRequest(IReadOnlyList<string> Keys);

    public static void Map(RouteGroupBuilder api)
    {
        var machine = api.MapGroup("/machines/{machine}/disk").RequireMachine();

        machine.MapGet("", async (HttpContext http, AgentContext ctx, DiskSpace disk, bool? fresh) =>
            ctx.CurrentUser(http)!.IsAdmin ? Results.Json(await disk.ReportAsync(fresh ?? false)) : ApiResults.Forbidden());

        machine.MapPost("/cleanup", async (HttpContext http, AgentContext ctx, DiskSpace disk, CleanupRequest body) =>
        {
            if (!ctx.CurrentUser(http)!.IsAdmin) { return ApiResults.Forbidden("Only admins and owners can clean up."); }
            if (body.Keys == null || body.Keys.Count == 0) { return ApiResults.BadRequest("Choose what to clean up."); }
            var (freed, files, failed) = await disk.CleanAsync(body.Keys);
            ctx.Record(http, "cleanup", null, failed.Count == 0, $"{string.Join(", ", body.Keys)}: {files} item(s), {freed / 1048576.0:0.#} MB{(failed.Count > 0 ? $", {failed.Count} couldn't go" : "")}");
            return Results.Json(new { freed, files, failed });
        });
    }
}
