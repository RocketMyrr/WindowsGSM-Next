using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WindowsGSM.Agent.Security;
using WindowsGSM.Contracts;

namespace WindowsGSM.Agent.Hub;

/// <summary>
/// "Who is asking", sent by the hub with every relayed request: the hub user's name and role, plus their
/// grants for the target machine only, re-scoped to "*/…" so they apply to the member's own servers. The member
/// runs the request as this user, so its normal permission checks decide what's allowed.
/// </summary>
public static class Delegation
{
    private sealed record Payload(
        [property: JsonPropertyName("u")] string Username,
        [property: JsonPropertyName("r")] Role Role,
        [property: JsonPropertyName("g")] Dictionary<string, Capability> Grants,
        [property: JsonPropertyName("via")] string Via);

    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    public static string Encode(AgentUser user, string machine, string hubName)
    {
        var grants = new Dictionary<string, Capability>(StringComparer.OrdinalIgnoreCase);
        foreach (var (scope, caps) in user.Grants)
        {
            int slash = scope.IndexOf('/');
            if (slash <= 0) { continue; }
            string m = scope[..slash], rest = scope[(slash + 1)..];
            if (m == "*") { grants[scope] = grants.GetValueOrDefault(scope) | caps; }
            else if (string.Equals(m, machine, StringComparison.OrdinalIgnoreCase)) { grants["*/" + rest] = grants.GetValueOrDefault("*/" + rest) | caps; }
        }
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(new Payload(user.Username, user.Role, grants, hubName), Json);
        return Convert.ToBase64String(json);
    }

    /// <summary>
    /// For the agent calling its own API as someone who isn't signed in (the Discord bot): the user's grants go
    /// as they are, machine ids included, so the hub's forwarding re-scopes them for each machine as usual.
    /// </summary>
    public static string EncodeAs(AgentUser user, string via)
    {
        var grants = new Dictionary<string, Capability>(user.Grants, StringComparer.OrdinalIgnoreCase);
        return Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new Payload(user.Username, user.Role, grants, via), Json));
    }

    public static AgentUser? Decode(string header)
    {
        if (string.IsNullOrWhiteSpace(header)) { return null; }
        try
        {
            var p = JsonSerializer.Deserialize<Payload>(Encoding.UTF8.GetString(Convert.FromBase64String(header)), Json);
            if (p == null || string.IsNullOrWhiteSpace(p.Username)) { return null; }
            return new AgentUser
            {
                Username = p.Username,
                Role = p.Role,
                Enabled = true,
                Grants = new Dictionary<string, Capability>(p.Grants ?? new(), StringComparer.OrdinalIgnoreCase),
                Via = string.IsNullOrWhiteSpace(p.Via) ? "hub" : p.Via,
            };
        }
        catch { return null; }
    }
}
