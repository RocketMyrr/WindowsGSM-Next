using WindowsGSM.Agent.Hosting;
using WindowsGSM.Contracts;
using WindowsGSM.Engine.Servers;

namespace WindowsGSM.Agent.Api;

/// <summary>Server templates: save a server's setup, start new servers from it, or apply it to another server.</summary>
public static class TemplateEndpoints
{
    private static object Summary(ServerTemplates.Template t) => new
    {
        id = t.Id, name = t.Name, game = t.Game, description = t.Description, createdAt = t.CreatedAt, createdBy = t.CreatedBy, fromServer = t.FromServer,
        settings = t.Settings.Count, files = t.Files.Select(f => f.Path).ToList(),
    };

    public static void Map(RouteGroupBuilder api, RouteGroupBuilder server)
    {
        var machine = api.MapGroup("/machines/{machine}/templates").RequireMachine();

        machine.MapGet("", (HttpContext http, AgentContext ctx, ServerTemplates templates) =>
            Results.Json(templates.List().Select(Summary)));

        machine.MapDelete("/{template}", (HttpContext http, AgentContext ctx, ServerTemplates templates, string template) =>
        {
            if (!ctx.CurrentUser(http)!.IsAdmin) { return ApiResults.Forbidden("Only admins and owners can delete templates."); }
            var t = templates.Get(template);
            if (t == null || !templates.Delete(template)) { return ApiResults.NotFound("No such template."); }
            ctx.Record(http, "template", null, true, $"deleted \"{t.Name}\"");
            return Results.NoContent();
        });

        server.MapPost("/template", (HttpContext http, AgentContext ctx, ServerTemplates templates, TemplateRequest body) =>
        {
            var s = Scopes.Server(http);
            string name = body.Name?.Trim() ?? "";
            if (name.Length is 0 or > 60) { return ApiResults.BadRequest("Give the template a name (up to 60 characters)."); }
            if ((body.Description?.Length ?? 0) > 300) { return ApiResults.BadRequest("Keep the description under 300 characters."); }
            var t = templates.Create(s.Id, name, body.Description, body.IncludeFiles, Scopes.User(http).Username);
            ctx.Record(http, "template", s.Id, true, $"saved \"{t.Name}\" ({t.Settings.Count} settings, {t.Files.Count} files)");
            return Results.Json(Summary(t));
        }).Needs(Capability.EditConfig);

        server.MapPost("/apply-template", async (HttpContext http, AgentContext ctx, ServerTemplates templates, ApplyTemplateRequest body) =>
        {
            var s = ctx.Engine.Servers.Get(Scopes.Server(http).Id)!;
            if (s.State != ServerState.Stopped) { return ApiResults.Conflict("Stop the server first."); }
            var t = templates.Get(body.Template ?? "");
            if (t == null) { return ApiResults.NotFound("No such template."); }
            try
            {
                string done = await templates.ApplyAsync(t, s.Id);
                ctx.Record(http, "template", s.Id, true, $"applied \"{t.Name}\": {done}");
                return Results.Json(new { done });
            }
            catch (InvalidOperationException ex) { return ApiResults.BadRequest(ex.Message); }
        }).Needs(Capability.EditConfig);
    }
}

public sealed record TemplateRequest(string? Name, string? Description, bool IncludeFiles = true);
public sealed record ApplyTemplateRequest(string? Template);
