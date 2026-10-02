namespace WindowsGSM.Contracts;

// Everything the v2 API sends and receives. JSON is camelCase; enums are strings. Every server-scoped shape
// carries its machine id so the same types work for one agent and for a hub relaying many.

// ── Errors ──

/// <summary>Every non-2xx response. <see cref="Code"/> is stable for programs; <see cref="Error"/> is for people.</summary>
public sealed record ApiError(string Error, string Code, IReadOnlyList<string>? Details = null);

/// <summary>Returned when a request started a job.</summary>
public sealed record JobAccepted(string JobId, JobDto Job);

// ── Machines ──

public sealed record MachineDto(string Id, string Name, bool IsLocal, bool Online, string Version, DateTimeOffset? LastSeen,
    HostMetricsDto? Metrics, int ServerCount);

public sealed record HostMetricsDto(double CpuPercent, double RamPercent, double RamTotalGb, double DiskPercent, double DiskTotalGb,
    int Cores, string? CpuName, DateTimeOffset At);

public sealed record AgentInfoDto(string Machine, string MachineName, string Version, DateTimeOffset StartedAt, bool SetupRequired);

// ── Servers ──

/// <param name="State">Stopped, Starting, Running, Stopping, Restarting, Installing, Updating, UpdatingAddons, BackingUp, Restoring, Deleting.</param>
/// <param name="BusyWith">What an operation is doing right now ("Updating", "Backing up"…), or null.</param>
/// <param name="Can">What the caller may do to this server.</param>
public sealed record ServerDto(
    string Machine, string Id, string Name, string Game, string State, string? BusyWith,
    string? Ip, string? Port, string? QueryPort,
    int? Players, int? MaxPlayers, double? CpuPercent, double? MemoryMb, DateTimeOffset? StartedAt,
    bool AutoStart, bool AutoRestart, bool AutoUpdate, bool CrashLoopSuspended,
    string? IconUrl, Capability Can, string? ArtUrl = null, string? BannerUrl = null, IReadOnlyList<string>? Tags = null, bool UpdateAvailable = false, bool UpdatesHeld = false);

/// <summary>Replaces a server's tags.</summary>
public sealed record TagsRequest(IReadOnlyList<string>? Tags);

public sealed record PlayerDto(int Id, string Name, long Score, double? ConnectedSeconds);

public sealed record SampleDto(DateTimeOffset At, double CpuPercent, double MemoryMb, int? Players, int? MaxPlayers);

/// <summary>Console output since a point. When <see cref="Reset"/> is true the buffer was cleared; replace, don't append.</summary>
public sealed record ConsoleDto(IReadOnlyList<string> Lines, long Seq, int Generation, bool Reset);

public sealed record CommandRequest(string Command, bool PreferRcon = false);

public sealed record CommandResultDto(bool Sent, string? Route, string? Reply, string? Error);

public sealed record LogDto(IReadOnlyList<string> Lines);

// ── Settings ──

public sealed record CustomSettingDto(string Key, string Label, string Value, IReadOnlyList<string> Options);

public sealed record ServerSettingsDto(string Machine, string Id, string Game, bool IsSteam, string? SteamBranchLastInstalled,
    IReadOnlyDictionary<string, string> Values, IReadOnlyList<CustomSettingDto> Custom, bool CustomReplacesBuiltIns, string? KnownSaveCommand = null);

/// <summary>Only the keys present are changed. All-or-nothing: one bad value and nothing is written.</summary>
public sealed record SettingsUpdateRequest(IReadOnlyDictionary<string, string?> Values);

public sealed record SteamBranchDto(string Name, string? BuildId, bool PasswordRequired, DateTimeOffset? UpdatedAt, string? Description);

public sealed record UpdateCheckDto(string? LocalBuild, string? RemoteBuild, bool UpdateAvailable, string? Error);

// ── Games & install ──

public sealed record GameDto(string Name, bool IsPlugin, bool IsSteam, string? AppId, string? IconUrl, string? Description,
    string? Author, string? Version, string? Color, IReadOnlyList<string> Consents, string? ArtUrl = null, string? BannerUrl = null);

public sealed record BrokenPluginDto(string FileName, string? Error);

public sealed record InstallRequest(string Game, string Name, string? SteamBranch = null, string? SteamBranchPassword = null,
    IReadOnlyList<string>? Consents = null, string? Template = null);

public sealed record ImportRequest(string Game, string Name, string Folder);

// ── Jobs & prompts ──

public sealed record JobDto(string Machine, string Id, string Kind, string? ServerId, string Title, string Status, int? Percent,
    string? Stage, string? Error, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, IReadOnlyList<string> RecentLog);

public sealed record PromptDto(string Machine, string Id, string JobId, string? ServerId, string Key, string Title, string Message,
    DateTimeOffset AskedAt, DateTimeOffset ExpiresAt);

public sealed record PromptAnswer(bool Answer);

// ── Backups ──

public sealed record BackupDto(string Name, long Size, DateTimeOffset Created, string Format);

public sealed record BackupSettingsDto(IReadOnlyList<string> Paths, IReadOnlyList<string> ExternalLocations, bool BeforeStart,
    int KeepCount, int KeepDays, string Location, string? CopyTo = null);

public sealed record BackupRequest(bool Everything = false);

public sealed record RestoreRequest(string Name, bool IncludeConfig = false);

// ── Add-ons ──

public sealed record AddonDto(string Key, string Label, bool Present, bool Managed);

public sealed record CustomAddonDto(string Id, string Name, string Url, string? Subfolder);

public sealed record CustomAddonRequest(string Name, string Url, string? Subfolder);

public sealed record AddonManageRequest(bool Managed);

// ── Schedules ──

/// <param name="Action">Restart, Start, Stop, Backup, Update, Command, Rcon (Exec is read-only: legacy crontab files only).</param>
/// <param name="Source">RestartSetting, CrontabFile or Managed — only Managed entries are editable here.</param>
public sealed record ScheduleDto(string Cron, string Action, string Payload, string Arguments, bool Enabled, string Source, DateTimeOffset? Next);

public sealed record ScheduleEntryRequest(string Cron, string Action, string? Payload = null, string? Arguments = null, bool Enabled = true);

public sealed record SchedulesRequest(IReadOnlyList<ScheduleEntryRequest> Entries);

/// <summary>The legacy "restart on schedule" setting: its cron (null keeps it) and whether it runs.</summary>
public sealed record RestartSettingRequest(string? Cron, bool Enabled);

// ── Files ──

public sealed record FileEntryDto(string Name, bool IsDirectory, long? Size, DateTimeOffset Modified);

public sealed record FolderDto(string Path, IReadOnlyList<FileEntryDto> Entries);

public sealed record TextFileDto(string Path, string Name, long Size, DateTimeOffset Modified, string? Content, bool Binary, bool ReadOnly, string? Note);

/// <param name="ExpectedModified">The file's modified time when it was opened — the save is refused if it changed since.</param>
public sealed record FileWriteRequest(string Path, string Content, DateTimeOffset? ExpectedModified = null);

public sealed record CreateFolderRequest(string? Path, string Name);

public sealed record RenameRequest(string Path, string NewName);

public sealed record PathRequest(string Path);

// ── Auth & accounts ──

public sealed record LoginRequest(string Username, string Password, string? Code = null);

/// <param name="TwoFactorRequired">Credentials were right but a code is needed — send them again with <c>code</c>.</param>
public sealed record LoginResult(bool Ok, bool TwoFactorRequired, MeDto? Me);

public sealed record MeDto(string Username, Role Role, bool TwoFactorEnabled, string Machine, bool CanManageUsers, bool IsOwner);

/// <param name="Token">The one-time setup code the agent shows — needed only when not on the machine itself.</param>
public sealed record SetupRequest(string Username, string Password, string? MachineName = null, string? Token = null);

public sealed record PasswordChangeRequest(string CurrentPassword, string NewPassword);

public sealed record CodeRequest(string Code);

public sealed record TwoFactorSetupDto(string Secret, string Uri);

public sealed record SessionDto(string Id, bool Current, DateTimeOffset CreatedAt, DateTimeOffset LastSeenAt, string? Ip, string? Device);

public sealed record UserDto(string Username, Role Role, bool Enabled, IReadOnlyDictionary<string, Capability> Grants,
    bool TwoFactorEnabled, DateTimeOffset CreatedAt, DateTimeOffset? LastLoginAt, string? LastLoginIp);

/// <summary>Create or update. On update, a blank password leaves it unchanged.</summary>
public sealed record UserRequest(string Username, Role Role, bool Enabled, IReadOnlyDictionary<string, Capability>? Grants, string? Password);

public sealed record AuditDto(DateTimeOffset At, string? User, string? Ip, string Action, string? Machine, string? Server, bool Ok, string? Detail);

// ── Events (WebSocket /api/v2/events) ──

/// <summary>
/// One event on the stream. <see cref="Seq"/> increases by one per event on an agent; reconnect with the last
/// seq seen to resume without gaps. A <c>reset</c> event means the gap was too big — refetch, then carry on.
/// </summary>
/// <param name="Type">serverState, serverList, serverConfig, console, job, prompt, promptResolved, metrics, players, alert, log, reset, hello.</param>
public sealed record EventEnvelope(long Seq, string Type, string? Machine, string? Server, DateTimeOffset At, object? Data);

/// <summary>Sent by the client after connecting. Topics: servers, jobs, metrics, alerts, logs, console:{machine}/{server}.</summary>
public sealed record SubscribeMessage(string Type, IReadOnlyList<string>? Topics, long? Since);

// ── Readiness ──

/// <param name="Status">Pass, Info, Warning or Fail.</param>
public sealed record ReadinessCheckDto(string Scope, string Name, string Status, string Message);

// ── Game config files ──

/// <param name="Format">Properties, Cfg, Ini, Xml, Json or Yaml.</param>
/// <param name="Known">A file this game is known to use (shown first).</param>
public sealed record ConfigFileDto(string Path, string Name, string Format, long Size, DateTimeOffset Modified, bool Known, string? Label);

/// <param name="Type">bool, number or text.</param>
/// <param name="Comment">The file's own explanation of the setting, if it has one.</param>
public sealed record ConfigEntryDto(string Id, string? Section, string Key, string Value, string Type, string? Comment, bool ReadOnly);

public sealed record GameConfigDto(string Path, string Format, DateTimeOffset Modified, IReadOnlyList<ConfigEntryDto> Entries, string? Note);

public sealed record ConfigChangeDto(string Id, string Value);

/// <param name="ExpectedModified">The file's modified time when it was read — refused if it changed since.</param>
public sealed record GameConfigUpdateRequest(string Path, DateTimeOffset? ExpectedModified, IReadOnlyList<ConfigChangeDto> Changes);
