using Microsoft.Data.Sqlite;
using WindowsGSM.Agent.Api;
using WindowsGSM.Engine.Events;
using WindowsGSM.Engine.Servers;
using WindowsGSM.Engine.Services;

namespace WindowsGSM.Agent.Hosting;

/// <summary>A point on a history chart: an average over its bucket (players: the most at once).</summary>
public sealed record HistoryPoint(DateTimeOffset At, double? Cpu, double? Ram, double? Disk, int? Players, int? MaxPlayers);

/// <summary>One visit by one player.</summary>
public sealed record PlayerSession(string Name, DateTimeOffset JoinedAt, DateTimeOffset? LeftAt);

/// <summary>A regular: how much they've played in the period asked about.</summary>
public sealed record PlayerTotal(string Name, double Seconds, int Sessions, DateTimeOffset LastSeen, bool Online);

/// <summary>Share of time a server was running (percent), over the last day, week and month. Null: too new to say.</summary>
public sealed record UptimeDto(double? Day, double? Week, double? Month, DateTimeOffset? Since,
    double? AgentDay = null, double? AgentWeek = null, double? AgentMonth = null);

/// <summary>
/// Long-term history for this machine (configs/next/history.db, SQLite): per-minute CPU/RAM/players for each
/// server and CPU/RAM/disk for the machine, rolled up to hourly after <see cref="MinuteDays"/> days and kept for
/// <see cref="HourDays"/>; and player sessions (who played, when, for how long). Each machine keeps its own —
/// a hub asks the machine, so nothing is lost while the hub is down and nothing is stored twice.
/// </summary>
public sealed class MetricsHistory : IDisposable
{
    public const int MinuteDays = 14;
    public const int HourDays = 400;
    private const string Machine = "machine"; // the "server" id used for this machine's own rows

    private readonly AgentContext _ctx;
    private readonly string _connectionString;
    private readonly object _gate = new();
    private readonly Dictionary<string, Bucket> _buckets = new();
    private readonly Dictionary<string, HashSet<string>> _online = new(); // server → player names in an open session
    private readonly IDisposable _subscription;
    private readonly Timer _timer;
    private DateTimeOffset _lastRollup = DateTimeOffset.MinValue;

    private sealed class Bucket
    {
        public long Minute;
        public double Cpu, Ram, Disk;
        public int Count, DiskCount;
        public int? Players, MaxPlayers;
    }

    public MetricsHistory(AgentContext ctx, string? file = null)
    {
        _ctx = ctx;
        file ??= Path.Combine(ctx.ConfigDir, "history.db");
        _connectionString = new SqliteConnectionStringBuilder { DataSource = file, Pooling = true }.ToString();
        using (var db = Open())
        {
            Exec(db, """
                PRAGMA journal_mode = WAL;
                CREATE TABLE IF NOT EXISTS minute (server TEXT NOT NULL, at INTEGER NOT NULL, cpu REAL, ram REAL, disk REAL, players INTEGER, max_players INTEGER, PRIMARY KEY (server, at)) WITHOUT ROWID;
                CREATE TABLE IF NOT EXISTS hour   (server TEXT NOT NULL, at INTEGER NOT NULL, cpu REAL, ram REAL, disk REAL, players INTEGER, max_players INTEGER, PRIMARY KEY (server, at)) WITHOUT ROWID;
                CREATE TABLE IF NOT EXISTS sessions (id INTEGER PRIMARY KEY, server TEXT NOT NULL, name TEXT NOT NULL, joined INTEGER NOT NULL, left INTEGER);
                CREATE INDEX IF NOT EXISTS sessions_by_server ON sessions (server, joined);
                CREATE TABLE IF NOT EXISTS perf (server TEXT NOT NULL, at INTEGER NOT NULL, value REAL NOT NULL, PRIMARY KEY (server, at)) WITHOUT ROWID;
                """);
            // Sessions still open from last time (the agent didn't stop cleanly): they ended by the server's last
            // recorded minute, as far as we can tell.
            Exec(db, """
                UPDATE sessions SET left = MAX(joined, COALESCE((SELECT MAX(at) + 60 FROM minute m WHERE m.server = sessions.server), joined))
                WHERE left IS NULL
                """);
        }
        _subscription = ctx.Engine.Events.Subscribe(OnEvent);
        _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20));
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connectionString);
        db.Open();
        return db;
    }

    private static void Exec(SqliteConnection db, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) { cmd.Parameters.AddWithValue(n, v ?? DBNull.Value); }
        cmd.ExecuteNonQuery();
    }

    // ───────────────────────────── Recording ─────────────────────────────

    private void OnEvent(EngineEvent e)
    {
        switch (e)
        {
            case ServerMetricsSampled m:
                Add(m.ServerId, m.Sample.At, m.Sample.CpuPercent, m.Sample.MemoryMb, null, m.Sample.Players, m.Sample.MaxPlayers);
                break;
            case PlayersChanged p:
                Players(p.ServerId, p.Players.Select(x => x.Name?.Trim() ?? "").Where(n => n.Length > 0 && n.Length <= 64).ToHashSet(StringComparer.Ordinal), e.At);
                break;
            case ServerStateChanged s when s.To != ServerState.Running:
                Players(s.ServerId, new HashSet<string>(), e.At);
                break;
            case ServerListChanged { Removed: true } r:
                Players(r.ServerId, new HashSet<string>(), e.At);
                break;
        }
    }

    private void Add(string server, DateTimeOffset at, double cpu, double ram, double? disk, int? players, int? maxPlayers)
    {
        long minute = at.ToUnixTimeSeconds() / 60 * 60;
        Bucket? done = null;
        lock (_gate)
        {
            if (!_buckets.TryGetValue(server, out var b)) { _buckets[server] = b = new Bucket { Minute = minute }; }
            if (b.Minute != minute)
            {
                done = b;
                _buckets[server] = b = new Bucket { Minute = minute };
            }
            b.Cpu += cpu; b.Ram += ram; b.Count++;
            if (disk != null) { b.Disk += disk.Value; b.DiskCount++; }
            if (players != null) { b.Players = Math.Max(b.Players ?? 0, players.Value); }
            if (maxPlayers != null) { b.MaxPlayers = maxPlayers; }
        }
        if (done != null) { Write(server, done); }
    }

    private void Write(string server, Bucket b)
    {
        if (b.Count == 0) { return; }
        try
        {
            using var db = Open();
            Exec(db, "INSERT OR REPLACE INTO minute (server, at, cpu, ram, disk, players, max_players) VALUES (@s, @at, @cpu, @ram, @disk, @p, @mp)",
                ("@s", server), ("@at", b.Minute), ("@cpu", Math.Round(b.Cpu / b.Count, 1)), ("@ram", Math.Round(b.Ram / b.Count, 1)),
                ("@disk", b.DiskCount > 0 ? Math.Round(b.Disk / b.DiskCount, 1) : null), ("@p", b.Players), ("@mp", b.MaxPlayers));
        }
        catch { /* history is best effort; the next minute tries again */ }
    }

    private void Players(string server, HashSet<string> now, DateTimeOffset at)
    {
        List<string> joined, left;
        lock (_gate)
        {
            var before = _online.TryGetValue(server, out var set) ? set : new HashSet<string>(StringComparer.Ordinal);
            joined = now.Where(n => !before.Contains(n)).ToList();
            left = before.Where(n => !now.Contains(n)).ToList();
            _online[server] = now;
        }
        if (joined.Count == 0 && left.Count == 0) { return; }
        try
        {
            using var db = Open();
            using var tx = db.BeginTransaction();
            long t = at.ToUnixTimeSeconds();
            foreach (string n in left) { Exec(db, "UPDATE sessions SET left = @t WHERE server = @s AND name = @n AND left IS NULL", ("@t", t), ("@s", server), ("@n", n)); }
            foreach (string n in joined) { Exec(db, "INSERT INTO sessions (server, name, joined) VALUES (@s, @n, @t)", ("@s", server), ("@n", n), ("@t", t)); }
            tx.Commit();
        }
        catch { /* best effort */ }
    }

    /// <summary>Every 20 s: sample the machine itself, close finished minutes, and once an hour roll up and prune.</summary>
    private void Tick()
    {
        var m = _ctx.Metrics.Sample();
        var now = DateTimeOffset.UtcNow;
        if (m != null) { Add(Machine, now, m.CpuPercent, m.RamPercent, m.DiskPercent, null, null); }

        // A server that stopped sending samples still has an unwritten last minute.
        long minute = now.ToUnixTimeSeconds() / 60 * 60;
        List<(string, Bucket)> stale;
        lock (_gate)
        {
            stale = _buckets.Where(kv => kv.Value.Minute < minute - 60).Select(kv => (kv.Key, kv.Value)).ToList();
            foreach (var (k, _) in stale) { _buckets.Remove(k); }
        }
        foreach (var (server, b) in stale) { Write(server, b); }

        if (now - _lastRollup > TimeSpan.FromHours(1)) { _lastRollup = now; Rollup(now); }
    }

    /// <summary>Minutes older than <see cref="MinuteDays"/> become hours; hours older than <see cref="HourDays"/> go.</summary>
    public void Rollup(DateTimeOffset now)
    {
        try
        {
            using var db = Open();
            using var tx = db.BeginTransaction();
            long cutoff = (now - TimeSpan.FromDays(MinuteDays)).ToUnixTimeSeconds() / 3600 * 3600;
            Exec(db, """
                INSERT OR REPLACE INTO hour (server, at, cpu, ram, disk, players, max_players)
                SELECT server, at / 3600 * 3600, ROUND(AVG(cpu), 1), ROUND(AVG(ram), 1), ROUND(AVG(disk), 1), MAX(players), MAX(max_players)
                FROM minute WHERE at < @cut GROUP BY server, at / 3600;
                DELETE FROM minute WHERE at < @cut;
                DELETE FROM hour WHERE at < @old;
                DELETE FROM sessions WHERE joined < @old;
                DELETE FROM perf WHERE at < @perfOld;
                """, ("@cut", cutoff), ("@old", (now - TimeSpan.FromDays(HourDays)).ToUnixTimeSeconds()),
                ("@perfOld", (now - TimeSpan.FromDays(PerfDays)).ToUnixTimeSeconds()));
            tx.Commit();
        }
        catch { /* try again next hour */ }
    }

    /// <summary>
    /// Writes the minutes still being collected, so a chart includes "now". They stay open: the row is
    /// rewritten with the full minute's average when it closes.
    /// </summary>
    public void Flush()
    {
        List<(string, Bucket)> all;
        lock (_gate) { all = _buckets.Select(kv => (kv.Key, Copy(kv.Value))).ToList(); }
        foreach (var (server, b) in all) { Write(server, b); }
    }

    private static Bucket Copy(Bucket b) => new()
    {
        Minute = b.Minute, Cpu = b.Cpu, Ram = b.Ram, Disk = b.Disk, Count = b.Count, DiskCount = b.DiskCount, Players = b.Players, MaxPlayers = b.MaxPlayers,
    };

    // ───────────────────────────── Reading ─────────────────────────────

    /// <summary>The ranges the UI offers: how far back, and how wide each point is.</summary>
    public static (TimeSpan Span, int BucketSeconds)? Range(string? range) => range switch
    {
        "6h" => (TimeSpan.FromHours(6), 120),
        "24h" or null or "" => (TimeSpan.FromHours(24), 300),
        "7d" => (TimeSpan.FromDays(7), 1800),
        "30d" => (TimeSpan.FromDays(30), 7200),
        "1y" => (TimeSpan.FromDays(365), 86400),
        _ => null,
    };

    /// <summary>History for a server (or null for this machine), oldest first.</summary>
    public IReadOnlyList<HistoryPoint> Query(string? server, TimeSpan span, int bucketSeconds, DateTimeOffset? now = null)
    {
        Flush();
        var to = now ?? DateTimeOffset.UtcNow;
        long from = (to - span).ToUnixTimeSeconds();
        var points = new List<HistoryPoint>();
        using var db = Open();
        using var cmd = db.CreateCommand();
        // Recent minutes plus older hours, then bucketed to the width asked for.
        cmd.CommandText = """
            SELECT at / @b * @b AS t, AVG(cpu), AVG(ram), AVG(disk), MAX(players), MAX(max_players) FROM (
                SELECT at, cpu, ram, disk, players, max_players FROM minute WHERE server = @s AND at >= @from
                UNION ALL
                SELECT at, cpu, ram, disk, players, max_players FROM hour WHERE server = @s AND at >= @from
                    AND at < COALESCE((SELECT MIN(at) FROM minute WHERE server = @s), 9223372036854775807)
            ) GROUP BY t ORDER BY t
            """;
        cmd.Parameters.AddWithValue("@b", bucketSeconds);
        cmd.Parameters.AddWithValue("@s", server ?? Machine);
        cmd.Parameters.AddWithValue("@from", from);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            points.Add(new HistoryPoint(DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(0)),
                Num(r, 1), Num(r, 2), Num(r, 3), r.IsDBNull(4) ? null : r.GetInt32(4), r.IsDBNull(5) ? null : r.GetInt32(5)));
        }
        return points;
    }

    // ───────────────────────────── In-game performance ─────────────────────────────

    /// <summary>How long server FPS / TPS samples are kept.</summary>
    public const int PerfDays = 30;

    /// <summary>One in-game performance sample (server FPS or TPS) for a minute.</summary>
    public void RecordPerf(string server, DateTimeOffset at, double value)
    {
        try
        {
            using var db = Open();
            Exec(db, "INSERT OR REPLACE INTO perf (server, at, value) VALUES (@s, @at, @v)",
                ("@s", server), ("@at", at.ToUnixTimeSeconds() / 60 * 60), ("@v", Math.Round(value, 1)));
        }
        catch { /* best effort */ }
    }

    /// <summary>Average and lowest value per bucket, oldest first.</summary>
    public IReadOnlyList<(DateTimeOffset At, double Avg, double Min)> QueryPerf(string server, TimeSpan span, int bucketSeconds, DateTimeOffset? now = null)
    {
        var list = new List<(DateTimeOffset, double, double)>();
        long from = ((now ?? DateTimeOffset.UtcNow) - span).ToUnixTimeSeconds();
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT at / @b * @b AS t, AVG(value), MIN(value) FROM perf WHERE server = @s AND at >= @from GROUP BY t ORDER BY t";
        cmd.Parameters.AddWithValue("@b", bucketSeconds);
        cmd.Parameters.AddWithValue("@s", server);
        cmd.Parameters.AddWithValue("@from", from);
        using var r = cmd.ExecuteReader();
        while (r.Read()) { list.Add((DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(0)), Math.Round(r.GetDouble(1), 1), Math.Round(r.GetDouble(2), 1))); }
        return list;
    }

    private static double? Num(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : Math.Round(r.GetDouble(i), 1);

    /// <summary>
    /// How much of the time a server was running: a minute counts when it was sampled running (the monitor only
    /// samples running servers). Counted from when the server was first seen, so a new server isn't marked down
    /// for the time before it existed. Time the agent itself wasn't running counts as down — nobody was watching.
    /// The last 14 days come from minutes; older days from hours (an hour with any running time counts whole).
    /// </summary>
    public UptimeDto Uptime(string server, DateTimeOffset? now = null)
    {
        Flush();
        var to = now ?? DateTimeOffset.UtcNow;
        long end = to.ToUnixTimeSeconds();
        using var db = Open();
        long? Scalar(string sql, params (string, object)[] args)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (n, v) in args) { cmd.Parameters.AddWithValue(n, v); }
            var o = cmd.ExecuteScalar();
            return o is null or DBNull ? null : Convert.ToInt64(o);
        }
        long? first = Scalar("SELECT MIN(at) FROM (SELECT at FROM minute WHERE server = @s UNION ALL SELECT at FROM hour WHERE server = @s)", ("@s", server));
        if (first == null) { return new UptimeDto(null, null, null, null); }

        // The same count for "machine" rows says how much of that time the agent itself was running (watching).
        double? Percent(string who, TimeSpan window)
        {
            long start = Math.Max(end - (long)window.TotalSeconds, first.Value);
            long total = (end - start) / 60;
            if (total < 10) { return null; } // a few minutes old: nothing to say yet
            long whoOldest = Scalar("SELECT MIN(at) FROM minute WHERE server = @s", ("@s", who)) ?? end;
            long up = Scalar("SELECT COUNT(*) FROM minute WHERE server = @s AND at >= @from", ("@s", who), ("@from", start)) ?? 0;
            if (start < whoOldest)
            {
                up += 60 * (Scalar("SELECT COUNT(*) FROM hour WHERE server = @s AND at >= @from AND at < @until", ("@s", who), ("@from", start / 3600 * 3600), ("@until", whoOldest)) ?? 0);
            }
            return Math.Round(Math.Min(100, up * 100.0 / total), 1);
        }
        return new UptimeDto(Percent(server, TimeSpan.FromDays(1)), Percent(server, TimeSpan.FromDays(7)), Percent(server, TimeSpan.FromDays(30)), DateTimeOffset.FromUnixTimeSeconds(first.Value),
            Percent(Machine, TimeSpan.FromDays(1)), Percent(Machine, TimeSpan.FromDays(7)), Percent(Machine, TimeSpan.FromDays(30)));
    }

    /// <summary>The most players online at once (all the given servers together) since <paramref name="since"/>.</summary>
    public int PeakPlayers(IReadOnlyCollection<string> servers, DateTimeOffset since)
    {
        if (servers.Count == 0) { return 0; }
        Flush();
        using var db = Open();
        using var cmd = db.CreateCommand();
        var names = servers.Select((s, i) => "@s" + i).ToList();
        cmd.CommandText = $"SELECT MAX(t) FROM (SELECT at, SUM(players) AS t FROM minute WHERE at >= @from AND players IS NOT NULL AND server IN ({string.Join(",", names)}) GROUP BY at)";
        cmd.Parameters.AddWithValue("@from", since.ToUnixTimeSeconds());
        int i = 0;
        foreach (string s in servers) { cmd.Parameters.AddWithValue("@s" + i++, s); }
        var o = cmd.ExecuteScalar();
        return o is null or DBNull ? 0 : Convert.ToInt32(o);
    }

    /// <summary>Recent visits to a server, newest first.</summary>
    public IReadOnlyList<PlayerSession> Sessions(string server, int limit)
    {
        var list = new List<PlayerSession>();
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT name, joined, left FROM sessions WHERE server = @s ORDER BY joined DESC LIMIT @n";
        cmd.Parameters.AddWithValue("@s", server);
        cmd.Parameters.AddWithValue("@n", Math.Clamp(limit, 1, 1000));
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new PlayerSession(r.GetString(0), DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(1)), r.IsDBNull(2) ? null : DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(2))));
        }
        return list;
    }

    /// <summary>Who played most in the last <paramref name="days"/> days.</summary>
    public IReadOnlyList<PlayerTotal> TopPlayers(string server, int days, int limit, DateTimeOffset? now = null)
    {
        var to = now ?? DateTimeOffset.UtcNow;
        long nowS = to.ToUnixTimeSeconds();
        var list = new List<PlayerTotal>();
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT name, SUM(COALESCE(left, @now) - MAX(joined, @from)), COUNT(*), MAX(COALESCE(left, @now)), MAX(left IS NULL)
            FROM sessions WHERE server = @s AND COALESCE(left, @now) >= @from
            GROUP BY name ORDER BY 2 DESC LIMIT @n
            """;
        cmd.Parameters.AddWithValue("@s", server);
        cmd.Parameters.AddWithValue("@now", nowS);
        cmd.Parameters.AddWithValue("@from", (to - TimeSpan.FromDays(days)).ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("@n", Math.Clamp(limit, 1, 200));
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new PlayerTotal(r.GetString(0), Math.Max(0, r.GetDouble(1)), r.GetInt32(2), DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(3)), r.GetInt64(4) == 1));
        }
        return list;
    }

    /// <summary>Test hook: record a sample as if the engine had.</summary>
    public void Record(string? server, DateTimeOffset at, double cpu, double ram, double? disk = null, int? players = null, int? maxPlayers = null) =>
        Add(server ?? Machine, at, cpu, ram, disk, players, maxPlayers);

    /// <summary>Test hook: the players on a server changed.</summary>
    public void RecordPlayers(string server, IEnumerable<string> names, DateTimeOffset at) => Players(server, names.ToHashSet(StringComparer.Ordinal), at);

    public void Dispose()
    {
        _subscription.Dispose();
        _timer.Dispose();
        Flush();
        // Close open sessions: we can't know when they end once we stop watching.
        List<string> servers;
        lock (_gate) { servers = _online.Keys.ToList(); }
        foreach (string s in servers) { Players(s, new HashSet<string>(), DateTimeOffset.UtcNow); }
        SqliteConnection.ClearAllPools();
    }
}
