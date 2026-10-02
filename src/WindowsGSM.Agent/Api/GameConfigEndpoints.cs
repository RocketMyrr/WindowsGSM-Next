using WindowsGSM.Contracts;
using WindowsGSM.Engine.GameConfig;
using WindowsGSM.Engine.Services;

namespace WindowsGSM.Agent.Api;

/// <summary>The game's own config files (server.cfg, server.properties, Game.ini…) as settings.</summary>
public static class GameConfigEndpoints
{
    public static void Map(RouteGroupBuilder server)
    {
        server.MapGet("/gameconfig", (HttpContext http, AgentContext ctx) => Handle(() =>
            Results.Json(ctx.Engine.GameConfigs.Discover(Scopes.Server(http).Id)
                .Select(f => new ConfigFileDto(f.Path, f.Name, f.Format.ToString(), f.Size, f.Modified, f.Known, f.Label)))))
            .Needs(Capability.EditConfig);

        server.MapGet("/gameconfig/file", (HttpContext http, AgentContext ctx, string path) => Handle(() =>
            Results.Json(ToDto(ctx.Engine.GameConfigs.Read(Scopes.Server(http).Id, path)))))
            .Needs(Capability.EditConfig);

        server.MapPatch("/gameconfig/file", (HttpContext http, AgentContext ctx, WindowsGSM.Agent.Hosting.ConfigHistory history, GameConfigUpdateRequest body) =>
        {
            var s = Scopes.Server(http);
            var changes = (body.Changes ?? Array.Empty<ConfigChangeDto>()).Select(c => new ConfigChange(c.Id, c.Value)).ToList();
            var result = Handle(() =>
            {
                if (changes.Count > 0)
                {
                    string full = ctx.Engine.Files.Resolve(s.Id, body.Path);
                    history.Keep(s.Id, ctx.Engine.Files.Relative(s.Id, full), full, Scopes.User(http).Username, "Game config");
                }
                var saved = ctx.Engine.GameConfigs.Write(s.Id, body.Path, changes, body.ExpectedModified);
                return Results.Json(ToDto(saved));
            });
            // Which settings changed, never their values (passwords live in these files too).
            bool ok = result is IStatusCodeHttpResult r && (r.StatusCode ?? 200) < 400;
            ctx.Record(http, "game-config", s.Id, ok, $"{body.Path}: {changes.Count} setting(s)");
            return result;
        }).Needs(Capability.EditConfig);
    }

    private static GameConfigDto ToDto(ParsedConfig p) =>
        new(p.Path, p.Format.ToString(), p.Modified, p.Entries.Select(e => new ConfigEntryDto(e.Id, e.Section, e.Key, e.Value, e.Type, e.Comment, e.ReadOnly)).ToList(), p.Note);

    private static IResult Handle(Func<IResult> action)
    {
        try { return action(); }
        catch (ConfigException ex) { return ApiResults.BadRequest(ex.Message); }
        catch (FileOperationException ex) { return ApiResults.FromFileProblem(ex); }
        catch (UnauthorizedAccessException) { return ApiResults.Forbidden("Windows denied access to that file."); }
        catch (IOException ex) { return ApiResults.Conflict(ex.Message); }
    }
}
