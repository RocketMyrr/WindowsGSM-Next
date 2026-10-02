namespace WindowsGSM.Contracts;

/// <summary>
/// What a user may do to a server. The first ten keep the legacy dashboard's bit values so legacy
/// permissions import unchanged.
/// </summary>
[Flags]
public enum Capability : long
{
    None = 0,
    View = 1 << 0,
    Start = 1 << 1,
    Stop = 1 << 2,
    Restart = 1 << 3,
    Kill = 1 << 4,
    Update = 1 << 5,
    Backup = 1 << 6,
    Console = 1 << 7,
    EditConfig = 1 << 8,
    Files = 1 << 9,
    Restore = 1 << 10,
    Addons = 1 << 11,
    Schedules = 1 << 12,
    Delete = 1 << 13,

    /// <summary>Install or import new servers on a machine (granted on "machine/*").</summary>
    Install = 1 << 14,

    All = View | Start | Stop | Restart | Kill | Update | Backup | Console | EditConfig | Files | Restore | Addons | Schedules | Delete | Install,
}

/// <summary>
/// Account roles, lowest to highest. A role gives a baseline on every server of every machine; grants add
/// more on specific machines or servers.
/// </summary>
public enum Role
{
    /// <summary>Nothing by default — only what grants give (the legacy "Member").</summary>
    Member,

    /// <summary>Sees everything, changes nothing.</summary>
    Viewer,

    /// <summary>Day-to-day running: start, stop, restart, update, back up, use the console.</summary>
    Operator,

    /// <summary>Everything on every server, and manages Member/Viewer/Operator accounts.</summary>
    Admin,

    /// <summary>Everything, including admins, security settings and machines.</summary>
    Owner,
}

public static class Roles
{
    public static Capability Baseline(Role role) => role switch
    {
        Role.Owner or Role.Admin => Capability.All,
        Role.Operator => Capability.View | Capability.Start | Capability.Stop | Capability.Restart | Capability.Update | Capability.Backup | Capability.Console,
        Role.Viewer => Capability.View,
        _ => Capability.None,
    };

    /// <summary>
    /// Grant scope for one server ("machine/server"), a whole machine ("machine/*") or everything ("*/*").
    /// </summary>
    public static string Scope(string machine, string server) => $"{machine}/{server}";
}
