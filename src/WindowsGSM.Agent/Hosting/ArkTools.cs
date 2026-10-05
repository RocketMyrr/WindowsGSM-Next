using System.Text.Json;
using System.Text.RegularExpressions;
using WindowsGSM.Agent.Api;
using WindowsGSM.Engine.Servers;
using WindowsGSM.Functions;

namespace WindowsGSM.Agent.Hosting;

/// <summary>
/// ARK: Survival Ascended mods (CurseForge project ids in -mods=) and ARK clusters (ASA and ARK: Survival Evolved):
/// servers sharing -clusterid and -ClusterDirOverride so players can carry characters and items between maps.
/// Both live in the server's extra start parameters, so they show (and can be edited) in Settings too.
/// </summary>
public sealed partial class ArkTools
{
    public const string AsaAppId = "2430930", AseAppId = "376030";

    public sealed record ModEntry(string Id, string? Name);
    public sealed record ClusterInfo(string? ClusterId, string? Dir, IReadOnlyList<string> Members);

    private readonly AgentContext _ctx;
    public ArkTools(AgentContext ctx) => _ctx = ctx;

    /// <summary>"asa", "ase" or null (not ARK).</summary>
    public string? Kind(ServerInstance s) => _ctx.Engine.Games.Get(s.Game)?.AppId switch
    {
        AsaAppId => "asa",
        AseAppId => "ase",
        _ => s.Game.Contains("Ascended", StringComparison.OrdinalIgnoreCase) ? "asa" : null,
    };

    public static string DefaultClusterDir(string clusterId) =>
        Path.Combine(global::WindowsGSM.Hosting.WgsmEnvironment.DataRoot, "clusters", clusterId);

    // ── Start parameter editing ──

    [GeneratedRegex("""(?:^|\s)-(?<key>[A-Za-z]+)=(?<value>"[^"]*"|\S*)""")]
    private static partial Regex Flag();

    /// <summary>The value of -key=… (case-insensitive) in a parameter string, without quotes.</summary>
    public static string? GetFlag(string? param, string key)
    {
        foreach (Match m in Flag().Matches(param ?? ""))
        {
            if (m.Groups["key"].Value.Equals(key, StringComparison.OrdinalIgnoreCase)) { return m.Groups["value"].Value.Trim('"'); }
        }
        return null;
    }

    /// <summary>Sets (or with null, removes) -key=value, keeping everything else as it was.</summary>
    public static string SetFlag(string? param, string key, string? value)
    {
        string p = param ?? "";
        p = Flag().Replace(p, m => m.Groups["key"].Value.Equals(key, StringComparison.OrdinalIgnoreCase) ? " " : m.Value);
        p = Regex.Replace(p, @"\s{2,}", " ").TrimEnd();
        if (value != null)
        {
            string v = value.Contains(' ') ? $"\"{value}\"" : value;
            p = (p.Length == 0 ? "" : p + " ") + $"-{key}={v}";
        }
        return p;
    }

    private static void SetParam(ServerInstance s, Func<string, string> change)
    {
        string before = ServerConfig.GetSetting(s.Id, ServerConfig.SettingName.ServerParam) ?? "";
        string after = change(before);
        if (after == before) { return; }
        ServerConfig.SetSetting(s.Id, ServerConfig.SettingName.ServerParam, after.Replace("\r", "").Replace("\n", " "));
        s.ReloadConfig();
    }

    // ── Mods (ASA) ──

    private static string NamesFile(string serverId) => ServerPath.GetServersConfigs(serverId, "ark-mods.json");

    public IReadOnlyList<ModEntry> Mods(ServerInstance s)
    {
        var names = ReadNames(s.Id);
        string? list = GetFlag(s.Config.ServerParam, "mods");
        return (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(id => new ModEntry(id, names.GetValueOrDefault(id))).ToList();
    }

    /// <summary>Saves the mod list (order matters: ARK loads them in this order). Returns a problem, or null.</summary>
    public string? SetMods(ServerInstance s, IReadOnlyList<ModEntry> mods)
    {
        if (mods.Count > 100) { return "Up to 100 mods."; }
        foreach (var m in mods)
        {
            if (!Regex.IsMatch(m.Id ?? "", @"^\d{3,10}$")) { return $"\"{m.Id}\" isn't a CurseForge project id (the number on the mod's CurseForge page, e.g. 928793)."; }
            if ((m.Name?.Length ?? 0) > 80) { return "Mod names are up to 80 characters."; }
        }
        var ids = mods.Select(m => m.Id).Distinct().ToList();
        SetParam(s, p => SetFlag(p, "mods", ids.Count == 0 ? null : string.Join(",", ids)));
        var names = mods.Where(m => !string.IsNullOrWhiteSpace(m.Name)).GroupBy(m => m.Id).ToDictionary(g => g.Key, g => g.First().Name!.Trim());
        global::WindowsGSM.Hosting.SafeJson.Write(NamesFile(s.Id), names);
        return null;
    }

    private static Dictionary<string, string> ReadNames(string serverId)
    {
        return global::WindowsGSM.Hosting.SafeJson.Read<Dictionary<string, string>>(NamesFile(serverId)) ?? new();
    }

    // ── Clusters ──

    public ClusterInfo Cluster(ServerInstance s)
    {
        string? id = GetFlag(s.Config.ServerParam, "clusterid");
        string? dir = GetFlag(s.Config.ServerParam, "ClusterDirOverride");
        var members = id == null ? new List<string>() : _ctx.Engine.Servers.All
            .Where(o => Kind(o) == Kind(s) && string.Equals(GetFlag(o.Config.ServerParam, "clusterid"), id, StringComparison.Ordinal))
            .Select(o => o.Id).ToList();
        return new ClusterInfo(id, dir, members);
    }

    /// <summary>
    /// Puts <paramref name="serverIds"/> in cluster <paramref name="clusterId"/> (one shared folder), and takes any other
    /// server of that cluster out. A null/empty id takes the given servers out of their cluster. Returns a problem, or null.
    /// </summary>
    public string? SetCluster(string? clusterId, IReadOnlyList<string> serverIds, string? dir)
    {
        var servers = serverIds.Select(id => _ctx.Engine.Servers.Get(id)).ToList();
        if (servers.Any(s => s == null)) { return "One of those servers doesn't exist."; }
        var kinds = servers.Select(s => Kind(s!)).Distinct().ToList();
        if (kinds.Any(k => k == null)) { return "Only ARK servers can be in an ARK cluster."; }
        if (kinds.Count > 1) { return "ARK: Survival Evolved and Ascended servers can't share a cluster."; }

        if (string.IsNullOrWhiteSpace(clusterId))
        {
            foreach (var s in servers) { SetParam(s!, p => SetFlag(SetFlag(p, "clusterid", null), "ClusterDirOverride", null)); }
            return null;
        }
        clusterId = clusterId.Trim();
        if (!Regex.IsMatch(clusterId, "^[A-Za-z0-9_-]{3,40}$")) { return "Cluster ids are 3–40 letters, numbers, - or _ (no spaces)."; }
        if (servers.Count < 1) { return "Pick the servers to put in the cluster."; }
        string folder = string.IsNullOrWhiteSpace(dir) ? DefaultClusterDir(clusterId) : dir.Trim();
        if (!Path.IsPathFullyQualified(folder) || folder.Contains('"')) { return "The cluster folder must be a full path like D:\\ARK\\cluster."; }
        Directory.CreateDirectory(folder);

        foreach (var other in _ctx.Engine.Servers.All.Where(o => !serverIds.Contains(o.Id) && GetFlag(o.Config.ServerParam, "clusterid") == clusterId))
        {
            SetParam(other, p => SetFlag(SetFlag(p, "clusterid", null), "ClusterDirOverride", null));
        }
        foreach (var s in servers) { SetParam(s!, p => SetFlag(SetFlag(p, "clusterid", clusterId), "ClusterDirOverride", folder)); }
        return null;
    }
}
