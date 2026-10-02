using WindowsGSM.Installer;

namespace WindowsGSM.Agent.Api;

/// <summary>
/// The Steam account for games that can't be downloaded anonymously (owners). Signing in runs DepotDownloader once
/// so Steam Guard is answered here — the code (email or authenticator app) is typed into the panel while it waits —
/// and the login is remembered for later installs and updates. The password is stored encrypted.
/// </summary>
public static class SteamEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var steam = api.MapGroup("/machines/{machine}/steam-account").RequireMachine();

        steam.MapGet("", (HttpContext http, AgentContext ctx) =>
        {
            if (!ctx.CurrentUser(http)!.IsOwner) { return ApiResults.Forbidden("Only owners can see the Steam account."); }
            return Results.Json(Status());
        });

        // Start signing in. Poll GET /signin; when it asks for a code, POST /signin/code.
        steam.MapPost("", (HttpContext http, AgentContext ctx, SteamSignInRequest body) =>
        {
            if (!ctx.CurrentUser(http)!.IsOwner) { return ApiResults.Forbidden("Only owners can change the Steam account."); }
            string user = body.Username?.Trim() ?? "";
            string? password = string.IsNullOrEmpty(body.Password) ? SteamAccount.Get().Password : body.Password;
            if (user.Length == 0 || user.Length > 64 || user.Any(char.IsWhiteSpace)) { return ApiResults.BadRequest("Type your Steam username (the sign-in name, not the profile name)."); }
            if (string.IsNullOrEmpty(password)) { return ApiResults.BadRequest("Type your Steam password."); }
            if (!SteamSignIn.Start(user, password, Runner, out string? problem)) { return ApiResults.Conflict(problem!); }
            ctx.Record(http, "steam-account", null, true, $"sign-in started for {user}");
            return Results.Json(SteamSignIn.State());
        });

        steam.MapGet("/signin", (HttpContext http, AgentContext ctx) =>
            ctx.CurrentUser(http)!.IsOwner ? Results.Json(SteamSignIn.State()) : ApiResults.Forbidden());

        steam.MapPost("/signin/code", (HttpContext http, AgentContext ctx, SteamCodeRequest body) =>
        {
            if (!ctx.CurrentUser(http)!.IsOwner) { return ApiResults.Forbidden(); }
            string code = body.Code?.Trim() ?? "";
            if (code.Length is 0 or > 16) { return ApiResults.BadRequest("Type the Steam Guard code."); }
            return SteamSignIn.Answer(code) ? Results.Json(SteamSignIn.State()) : ApiResults.Conflict("Steam isn't waiting for a code right now.");
        });

        steam.MapPost("/signin/cancel", (HttpContext http, AgentContext ctx) =>
        {
            if (!ctx.CurrentUser(http)!.IsOwner) { return ApiResults.Forbidden(); }
            SteamSignIn.Cancel();
            return Results.Json(SteamSignIn.State());
        });

        steam.MapDelete("", (HttpContext http, AgentContext ctx) =>
        {
            if (!ctx.CurrentUser(http)!.IsOwner) { return ApiResults.Forbidden(); }
            SteamAccount.Clear();
            ctx.Record(http, "steam-account", null, true, "removed");
            return Results.Json(Status());
        });

        // The legacy app's bin\steamcmd\userData.txt keeps the password as plain text.
        steam.MapPost("/remove-legacy-password", (HttpContext http, AgentContext ctx) =>
        {
            if (!ctx.CurrentUser(http)!.IsOwner) { return ApiResults.Forbidden(); }
            SteamAccount.ImportLegacy(); // keep it (encrypted) before blanking the plain copy
            SteamAccount.RemoveLegacyPassword();
            ctx.Record(http, "steam-account", null, true, "removed the plain-text password from userData.txt");
            return Results.Json(Status());
        });
    }

    /// <summary>Tests swap this for a fake Steam.</summary>
    public static SteamSignIn.RunSignIn Runner { get; set; } = DepotDownloader.SignInAsync;

    private static object Status()
    {
        var s = SteamAccount.Status();
        return new
        {
            username = s.Username, hasPassword = s.HasPassword, signedInAt = s.SignedInAt, fromLegacyFile = s.FromLegacyFile,
            legacyPlainText = SteamAccount.LegacyHasPassword(), signIn = SteamSignIn.State(),
        };
    }
}

/// <summary>One Steam sign-in at a time, waiting on the panel for Steam Guard codes.</summary>
public static class SteamSignIn
{
    public delegate Task<string?> RunSignIn(string username, string password, Func<string, Task<string?>> onPrompt, Action<string> onLine, CancellationToken token);

    public sealed record StateDto(string State, string? Prompt, string? Message, string? Username, IReadOnlyList<string> Output);

    private static readonly object Gate = new();
    private static string _state = "idle"; // idle, running, code, done, failed
    private static string? _prompt, _message, _user;
    private static readonly List<string> _output = new();
    private static TaskCompletionSource<string?>? _answer;
    private static CancellationTokenSource? _cts;

    public static StateDto State()
    {
        lock (Gate) { return new StateDto(_state, _prompt, _message, _user, _output.TakeLast(12).ToList()); }
    }

    public static bool Start(string username, string password, RunSignIn run, out string? problem)
    {
        lock (Gate)
        {
            if (_state is "running" or "code") { problem = "A Steam sign-in is already in progress."; return false; }
            _state = "running"; _prompt = null; _message = null; _user = username; _output.Clear();
            _cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        }
        problem = null;
        var token = _cts.Token;
        _ = Task.Run(async () =>
        {
            string? error;
            try
            {
                error = await run(username, password, async prompt =>
                {
                    var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    lock (Gate) { _state = "code"; _prompt = Friendly(prompt); _answer = tcs; }
                    using (token.Register(() => tcs.TrySetResult(null)))
                    {
                        string? code = await tcs.Task;
                        lock (Gate) { if (_state == "code") { _state = "running"; _prompt = null; } }
                        return code;
                    }
                }, line => { lock (Gate) { _output.Add(Redact(line, password)); if (_output.Count > 200) { _output.RemoveAt(0); } } }, token);
            }
            catch (Exception ex) { error = ex.Message; }
            if (error == null) { SteamAccount.Save(username, password, signedIn: true); }
            lock (Gate)
            {
                _state = error == null ? "done" : "failed";
                _message = error ?? $"Signed in as {username}. Installs and updates that need an account will use it.";
                _prompt = null; _answer = null;
            }
        });
        return true;
    }

    public static bool Answer(string code)
    {
        TaskCompletionSource<string?>? tcs;
        lock (Gate) { if (_state != "code") { return false; } tcs = _answer; _state = "running"; _prompt = null; }
        return tcs?.TrySetResult(code) ?? false;
    }

    public static void Cancel()
    {
        TaskCompletionSource<string?>? tcs;
        lock (Gate) { tcs = _answer; }
        tcs?.TrySetResult(null);
        try { _cts?.Cancel(); } catch { /* finished */ }
    }

    /// <summary>Tests: back to nothing in progress.</summary>
    public static void Reset()
    {
        Cancel();
        lock (Gate) { _state = "idle"; _prompt = null; _message = null; _user = null; _output.Clear(); }
    }

    private static string Friendly(string prompt) =>
        prompt.Contains("email", StringComparison.OrdinalIgnoreCase)
            ? "Steam sent a code to your email. Type it here."
            : prompt.Contains("Mobile App", StringComparison.OrdinalIgnoreCase) && !prompt.Contains("code", StringComparison.OrdinalIgnoreCase)
                ? "Approve the sign-in in the Steam Mobile App."
                : "Type the code from the Steam Mobile App (Steam Guard).";

    private static string Redact(string line, string password) =>
        password.Length > 0 ? line.Replace(password, "••••", StringComparison.Ordinal) : line;
}

public sealed record SteamSignInRequest(string? Username, string? Password);
public sealed record SteamCodeRequest(string? Code);
