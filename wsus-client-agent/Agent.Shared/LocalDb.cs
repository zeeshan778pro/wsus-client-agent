using Microsoft.Data.Sqlite;

namespace Agent.Shared;

/// <summary>
/// This agent's entire persistence layer - a single local SQLite file
/// under C:\ProgramData\WsusClientAgent, deliberately independent of
/// any other application's database. The service (SYSTEM) writes scan
/// results and status here; the tray app (the logged-in user) reads
/// them and writes command rows here - install.ps1 grants the local
/// Users group modify rights on the folder so both can open the file.
/// SQLite's own file locking handles the two-process access safely as
/// long as each connection is short-lived (open, do the work, close),
/// which every method here does.
/// </summary>
public class LocalDb
{
    private readonly string _connectionString;

    public static string DefaultDbPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "WsusClientAgent", "agent.db");

    public LocalDb(string? dbPath = null)
    {
        var path = dbPath ?? DefaultDbPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        EnsureCreated();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        // WAL mode lets a reader (tray) and a writer (service) use the
        // file at the same time without blocking each other on every
        // single access - the default rollback-journal mode would
        // serialize far more aggressively across two separate processes.
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    private void EnsureCreated()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS updates (
                update_id     TEXT PRIMARY KEY,
                kb_article    TEXT,
                title         TEXT NOT NULL,
                severity      TEXT,
                size_mb       REAL,
                status        TEXT NOT NULL DEFAULT 'Pending',
                error_detail  TEXT,
                discovered_at TEXT NOT NULL,
                updated_at    TEXT
            );

            CREATE TABLE IF NOT EXISTS commands (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                action         TEXT NOT NULL,
                update_ids_csv TEXT,
                created_at     TEXT NOT NULL,
                processed      INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS status (
                id                 INTEGER PRIMARY KEY CHECK (id = 1),
                last_scan_at       TEXT,
                pending_reboot     INTEGER NOT NULL DEFAULT 0,
                last_error         TEXT,
                scan_in_progress   INTEGER NOT NULL DEFAULT 0,
                install_in_progress INTEGER NOT NULL DEFAULT 0
            );
            INSERT OR IGNORE INTO status (id) VALUES (1);

            -- Permanent record of install attempts - separate from
            -- `updates`, which only ever reflects the LATEST scan's
            -- still-pending items and drops a row the moment WUA stops
            -- offering it (i.e. right after a successful install). This
            -- is what "Installation Status" reads, so a completed
            -- install doesn't just vanish from the agent's own UI the
            -- way it does from the transient pending list.
            CREATE TABLE IF NOT EXISTS history (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                update_id     TEXT NOT NULL,
                kb_article    TEXT,
                title         TEXT NOT NULL,
                status        TEXT NOT NULL,
                detail        TEXT,
                occurred_at   TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    // ── Updates ──────────────────────────────────────────────────────────

    /// <summary>
    /// Replaces the update list wholesale, except any row currently
    /// Installing (see class remarks) - a fresh scan always reflects
    /// exactly what WSUS currently offers, so an update that's been
    /// installed or superseded since the last scan simply disappears
    /// rather than lingering as a stale row.
    /// </summary>
    public void ReplaceUpdates(IEnumerable<UpdateRecord> found)
    {
        var foundList = found.ToList();
        var now = DateTime.UtcNow.ToString("o");

        using var conn = Open();
        using var tx = conn.BeginTransaction();

        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM updates WHERE status != 'Installing' AND update_id NOT IN (SELECT value FROM json_each(@ids))";
            del.Parameters.AddWithValue("@ids", System.Text.Json.JsonSerializer.Serialize(foundList.Select(u => u.UpdateId)));
            del.ExecuteNonQuery();
        }

        foreach (var u in foundList)
        {
            using var upsert = conn.CreateCommand();
            upsert.Transaction = tx;
            upsert.CommandText = """
                INSERT INTO updates (update_id, kb_article, title, severity, size_mb, status, error_detail, discovered_at, updated_at)
                VALUES (@id, @kb, @title, @sev, @size, @status, @err, @now, @now)
                ON CONFLICT(update_id) DO UPDATE SET
                    kb_article=@kb, title=@title, severity=@sev, size_mb=@size, updated_at=@now
                    -- status/error_detail deliberately NOT overwritten here - a
                    -- record already tracked as Downloading/Installing/Failed
                    -- keeps that state until the install phase changes it,
                    -- rather than a routine re-scan silently resetting it.
                """;
            upsert.Parameters.AddWithValue("@id", u.UpdateId);
            upsert.Parameters.AddWithValue("@kb", (object?)u.KbArticle ?? DBNull.Value);
            upsert.Parameters.AddWithValue("@title", u.Title);
            upsert.Parameters.AddWithValue("@sev", (object?)u.Severity ?? DBNull.Value);
            upsert.Parameters.AddWithValue("@size", (object?)u.SizeMb ?? DBNull.Value);
            upsert.Parameters.AddWithValue("@status", u.Status);
            upsert.Parameters.AddWithValue("@err", (object?)u.ErrorDetail ?? DBNull.Value);
            upsert.Parameters.AddWithValue("@now", now);
            upsert.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public void SetUpdateStatus(string updateId, string status, string? errorDetail = null)
    {
        using var conn = Open();
        var now = DateTime.UtcNow.ToString("o");

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "UPDATE updates SET status=@status, error_detail=@err, updated_at=@now WHERE update_id=@id";
            cmd.Parameters.AddWithValue("@status", status);
            cmd.Parameters.AddWithValue("@err", (object?)errorDetail ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@now", now);
            cmd.Parameters.AddWithValue("@id", updateId);
            cmd.ExecuteNonQuery();
        }

        // Only terminal outcomes are worth a permanent record - Pending/
        // Downloading/Installing are just this update's current place in
        // one in-progress attempt, not something "Installation Status"
        // needs to remember after the fact.
        if (status is not ("Installed" or "Failed")) return;

        string? kb = null;
        string? title = null;
        using (var lookup = conn.CreateCommand())
        {
            lookup.CommandText = "SELECT kb_article, title FROM updates WHERE update_id=@id";
            lookup.Parameters.AddWithValue("@id", updateId);
            using var reader = lookup.ExecuteReader();
            if (!reader.Read()) return;
            kb = reader.IsDBNull(0) ? null : reader.GetString(0);
            title = reader.GetString(1);
        }

        using (var insert = conn.CreateCommand())
        {
            insert.CommandText = "INSERT INTO history (update_id, kb_article, title, status, detail, occurred_at) VALUES (@id, @kb, @title, @status, @detail, @now)";
            insert.Parameters.AddWithValue("@id", updateId);
            insert.Parameters.AddWithValue("@kb", (object?)kb ?? DBNull.Value);
            insert.Parameters.AddWithValue("@title", title);
            insert.Parameters.AddWithValue("@status", status);
            insert.Parameters.AddWithValue("@detail", (object?)errorDetail ?? DBNull.Value);
            insert.Parameters.AddWithValue("@now", now);
            insert.ExecuteNonQuery();
        }
    }

    public List<HistoryEntry> GetHistory(int limit = 200)
    {
        var result = new List<HistoryEntry>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT kb_article, title, status, detail, occurred_at FROM history ORDER BY occurred_at DESC LIMIT @limit";
        cmd.Parameters.AddWithValue("@limit", limit);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new HistoryEntry
            {
                KbArticle = reader.IsDBNull(0) ? null : reader.GetString(0),
                Title = reader.GetString(1),
                Status = reader.GetString(2),
                Detail = reader.IsDBNull(3) ? null : reader.GetString(3),
                OccurredAt = DateTime.Parse(reader.GetString(4)),
            });
        }
        return result;
    }

    public List<UpdateRecord> GetUpdates()
    {
        var result = new List<UpdateRecord>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT update_id, kb_article, title, severity, size_mb, status, error_detail, discovered_at, updated_at FROM updates ORDER BY severity DESC, title";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new UpdateRecord
            {
                UpdateId = reader.GetString(0),
                KbArticle = reader.IsDBNull(1) ? null : reader.GetString(1),
                Title = reader.GetString(2),
                Severity = reader.IsDBNull(3) ? null : reader.GetString(3),
                SizeMb = reader.IsDBNull(4) ? null : reader.GetDouble(4),
                Status = reader.GetString(5),
                ErrorDetail = reader.IsDBNull(6) ? null : reader.GetString(6),
                DiscoveredAt = DateTime.Parse(reader.GetString(7)),
                UpdatedAt = reader.IsDBNull(8) ? null : DateTime.Parse(reader.GetString(8)),
            });
        }
        return result;
    }

    // ── Commands (tray -> service) ───────────────────────────────────────

    public void EnqueueCommand(string action, string? updateIdsCsv = null)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO commands (action, update_ids_csv, created_at, processed) VALUES (@action, @ids, @now, 0)";
        cmd.Parameters.AddWithValue("@action", action);
        cmd.Parameters.AddWithValue("@ids", (object?)updateIdsCsv ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public List<AgentCommand> GetUnprocessedCommands()
    {
        var result = new List<AgentCommand>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, action, update_ids_csv, created_at FROM commands WHERE processed = 0 ORDER BY id";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new AgentCommand
            {
                Id = reader.GetInt64(0),
                Action = reader.GetString(1),
                UpdateIdsCsv = reader.IsDBNull(2) ? null : reader.GetString(2),
                CreatedAt = DateTime.Parse(reader.GetString(3)),
            });
        }
        return result;
    }

    public void MarkCommandProcessed(long id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE commands SET processed = 1 WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    // ── Status ───────────────────────────────────────────────────────────

    public AgentStatus GetStatus()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT last_scan_at, pending_reboot, last_error, scan_in_progress, install_in_progress FROM status WHERE id = 1";
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return new AgentStatus();
        return new AgentStatus
        {
            LastScanAt = reader.IsDBNull(0) ? null : DateTime.Parse(reader.GetString(0)),
            PendingReboot = reader.GetInt32(1) != 0,
            LastError = reader.IsDBNull(2) ? null : reader.GetString(2),
            ScanInProgress = reader.GetInt32(3) != 0,
            InstallInProgress = reader.GetInt32(4) != 0,
        };
    }

    public void UpdateStatus(Action<StatusUpdate> configure)
    {
        var update = new StatusUpdate();
        configure(update);
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        var sets = new List<string>();
        if (update.LastScanAt.IsSet) sets.Add("last_scan_at=@last_scan_at");
        if (update.PendingReboot.IsSet) sets.Add("pending_reboot=@pending_reboot");
        if (update.LastError.IsSet) sets.Add("last_error=@last_error");
        if (update.ScanInProgress.IsSet) sets.Add("scan_in_progress=@scan_in_progress");
        if (update.InstallInProgress.IsSet) sets.Add("install_in_progress=@install_in_progress");
        if (sets.Count == 0) return;

        cmd.CommandText = $"UPDATE status SET {string.Join(", ", sets)} WHERE id = 1";
        if (update.LastScanAt.IsSet) cmd.Parameters.AddWithValue("@last_scan_at", (object?)update.LastScanAt.Value?.ToString("o") ?? DBNull.Value);
        if (update.PendingReboot.IsSet) cmd.Parameters.AddWithValue("@pending_reboot", update.PendingReboot.Value ? 1 : 0);
        if (update.LastError.IsSet) cmd.Parameters.AddWithValue("@last_error", (object?)update.LastError.Value ?? DBNull.Value);
        if (update.ScanInProgress.IsSet) cmd.Parameters.AddWithValue("@scan_in_progress", update.ScanInProgress.Value ? 1 : 0);
        if (update.InstallInProgress.IsSet) cmd.Parameters.AddWithValue("@install_in_progress", update.InstallInProgress.Value ? 1 : 0);
        cmd.ExecuteNonQuery();
    }
}

/// <summary>Tiny "was this field actually set" wrapper so UpdateStatus's
/// partial-update builder only touches columns the caller cares about.</summary>
public struct Settable<T>
{
    public bool IsSet { get; private set; }
    public T? Value { get; private set; }
    public void Set(T value) { Value = value; IsSet = true; }
}

public class StatusUpdate
{
    public Settable<DateTime?> LastScanAt;
    public Settable<bool> PendingReboot;
    public Settable<string?> LastError;
    public Settable<bool> ScanInProgress;
    public Settable<bool> InstallInProgress;
}
