using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FilipBoard.Core;
using Microsoft.Data.Sqlite;

namespace FilipBoard.Data;

public sealed record HistoryPoint(DateTime Utc, double Value);
public sealed record LogEntry(DateTime Utc, string Level, string Category, string Message);

/// <summary>SQLite persistence: settings, profiles, verification records, sensor history and event log.</summary>
public sealed class SqliteStore : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly object _lock = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false, Converters = { new JsonStringEnumConverter() } };

    public string Path { get; }

    public SqliteStore(string path)
    {
        Path = path;
        if (path != ":memory:") Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        _db = new SqliteConnection($"Data Source={path}");
        _db.Open();
        Exec("PRAGMA journal_mode=WAL;");
        Initialise();
    }

    public static string DefaultPath() => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FilipBoardFanControl", "data.db");

    private void Initialise()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS kv (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS profiles (name TEXT PRIMARY KEY, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS verifications (channel TEXT PRIMARY KEY, fingerprint TEXT NOT NULL, utc TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS history (ts INTEGER NOT NULL, sensor TEXT NOT NULL, kind INTEGER NOT NULL, value REAL NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_history ON history(sensor, ts);
            CREATE TABLE IF NOT EXISTS events (ts INTEGER NOT NULL, level TEXT NOT NULL, category TEXT NOT NULL, message TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_events ON events(ts);
            INSERT OR IGNORE INTO kv(key,value) VALUES ('schema','1');
            """);
    }

    private void Exec(string sql, params (string, object?)[] args)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    private static long ToUnix(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();
    private static DateTime FromUnix(long s) => DateTimeOffset.FromUnixTimeSeconds(s).UtcDateTime;

    // ---- settings ----
    public AppSettings LoadSettings()
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT value FROM kv WHERE key='settings'";
            var v = cmd.ExecuteScalar() as string;
            if (v is null) return new AppSettings();
            try { return JsonSerializer.Deserialize<AppSettings>(v, Json) ?? new AppSettings(); }
            catch (JsonException) { return new AppSettings(); }
        }
    }

    public void SaveSettings(AppSettings s) =>
        Exec("INSERT INTO kv(key,value) VALUES('settings',$v) ON CONFLICT(key) DO UPDATE SET value=$v", ("$v", JsonSerializer.Serialize(s, Json)));

    // ---- profiles ----
    public List<FanProfile> LoadProfiles()
    {
        var list = new List<FanProfile>();
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT json FROM profiles";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                try { list.Add(ProfileSerializer.FromJson(r.GetString(0))); } catch (FormatException) { /* skip corrupt row */ }
            }
        }
        // Built-ins always present (restored if missing) and listed first, in canonical order.
        var builtIns = BuiltInProfiles.Create();
        foreach (var b in builtIns)
        {
            var stored = list.FirstOrDefault(p => p.Name == b.Name);
            if (stored != null) { stored.BuiltIn = b.BuiltIn; }
            else list.Add(b);
        }
        var order = builtIns.Select(b => b.Name).ToList();
        return list.OrderBy(p => order.IndexOf(p.Name) is var i && i >= 0 ? i : int.MaxValue).ThenBy(p => p.Name).ToList();
    }

    public void SaveProfile(FanProfile p) =>
        Exec("INSERT INTO profiles(name,json) VALUES($n,$j) ON CONFLICT(name) DO UPDATE SET json=$j",
            ("$n", p.Name), ("$j", ProfileSerializer.ToJson(p)));

    public void DeleteProfile(string name) => Exec("DELETE FROM profiles WHERE name=$n", ("$n", name));

    // ---- verification records ----
    public List<VerificationRecord> LoadVerifications()
    {
        var list = new List<VerificationRecord>();
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT channel,fingerprint,utc FROM verifications";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new(r.GetString(0), r.GetString(1), DateTime.Parse(r.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        }
        return list;
    }

    public void SaveVerification(VerificationRecord v) =>
        Exec("INSERT INTO verifications(channel,fingerprint,utc) VALUES($c,$f,$u) ON CONFLICT(channel) DO UPDATE SET fingerprint=$f, utc=$u",
            ("$c", v.ChannelId), ("$f", v.Fingerprint), ("$u", v.VerifiedUtc.ToString("o", CultureInfo.InvariantCulture)));

    public void DeleteVerification(string channel) => Exec("DELETE FROM verifications WHERE channel=$c", ("$c", channel));

    // ---- history ----
    public void AddSamples(DateTime utc, IEnumerable<SensorReading> readings)
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            using var cmd = _db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO history(ts,sensor,kind,value) VALUES($t,$s,$k,$v)";
            var t = cmd.Parameters.Add("$t", SqliteType.Integer);
            var s = cmd.Parameters.Add("$s", SqliteType.Text);
            var k = cmd.Parameters.Add("$k", SqliteType.Integer);
            var v = cmd.Parameters.Add("$v", SqliteType.Real);
            foreach (var r in readings)
            {
                if (r.Value is not { } val || double.IsNaN(val) || r.IsMock) continue; // mock data is never persisted
                t.Value = ToUnix(utc); s.Value = r.Id; k.Value = (int)r.Kind; v.Value = val;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>Returns history averaged into at most <paramref name="maxPoints"/> buckets.</summary>
    public List<HistoryPoint> QueryHistory(string sensorId, DateTime fromUtc, DateTime toUtc, int maxPoints = 300)
    {
        var from = ToUnix(fromUtc); var to = ToUnix(toUtc);
        var bucket = Math.Max(1, (to - from) / Math.Max(1, maxPoints));
        var list = new List<HistoryPoint>();
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT (ts/$b)*$b AS bt, AVG(value) FROM history WHERE sensor=$s AND ts BETWEEN $f AND $t GROUP BY bt ORDER BY bt";
            cmd.Parameters.AddWithValue("$b", bucket);
            cmd.Parameters.AddWithValue("$s", sensorId);
            cmd.Parameters.AddWithValue("$f", from);
            cmd.Parameters.AddWithValue("$t", to);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(new(FromUnix(r.GetInt64(0)), r.GetDouble(1)));
        }
        return list;
    }

    public List<string> KnownSensors()
    {
        var list = new List<string>();
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT sensor FROM history ORDER BY sensor";
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(r.GetString(0));
        }
        return list;
    }

    public void Prune(int retentionDays)
    {
        var cutoff = ToUnix(DateTime.UtcNow.AddDays(-Math.Max(1, retentionDays)));
        Exec("DELETE FROM history WHERE ts < $c", ("$c", cutoff));
        Exec("DELETE FROM events WHERE ts < $c", ("$c", cutoff));
    }

    // ---- event log ----
    public void Log(string level, string category, string message, DateTime? utc = null) =>
        Exec("INSERT INTO events(ts,level,category,message) VALUES($t,$l,$c,$m)",
            ("$t", ToUnix(utc ?? DateTime.UtcNow)), ("$l", level), ("$c", category), ("$m", message));

    public List<LogEntry> RecentLog(int max = 500, string? category = null)
    {
        var list = new List<LogEntry>();
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT ts,level,category,message FROM events " +
                              (category != null ? "WHERE category=$c " : "") + "ORDER BY ts DESC, rowid DESC LIMIT $n";
            if (category != null) cmd.Parameters.AddWithValue("$c", category);
            cmd.Parameters.AddWithValue("$n", max);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(new(FromUnix(r.GetInt64(0)), r.GetString(1), r.GetString(2), r.GetString(3)));
        }
        return list;
    }

    // ---- CSV export ----
    private static string Csv(string s) => s.Contains('"') || s.Contains(',') || s.Contains('\n') || s.Contains('\r')
        ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    public void ExportHistoryCsv(string file, DateTime fromUtc, DateTime toUtc)
    {
        var sb = new StringBuilder("timestamp_utc,sensor,value\r\n");
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT ts,sensor,value FROM history WHERE ts BETWEEN $f AND $t ORDER BY ts, sensor";
            cmd.Parameters.AddWithValue("$f", ToUnix(fromUtc));
            cmd.Parameters.AddWithValue("$t", ToUnix(toUtc));
            using var r = cmd.ExecuteReader();
            while (r.Read())
                sb.Append(FromUnix(r.GetInt64(0)).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(',')
                  .Append(Csv(r.GetString(1))).Append(',')
                  .Append(r.GetDouble(2).ToString("0.###", CultureInfo.InvariantCulture)).Append("\r\n");
        }
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
    }

    public void ExportLogCsv(string file)
    {
        var sb = new StringBuilder("timestamp_utc,level,category,message\r\n");
        foreach (var e in RecentLog(100000))
            sb.Append(e.Utc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(',')
              .Append(Csv(e.Level)).Append(',').Append(Csv(e.Category)).Append(',').Append(Csv(e.Message)).Append("\r\n");
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
    }

    public void Dispose() { lock (_lock) _db.Dispose(); }
}
