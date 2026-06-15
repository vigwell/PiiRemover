using Microsoft.Data.Sqlite;

namespace PiiRemover.Data;

public class SchemaInitializer
{
    // WAL mode: concurrent readers + one writer, no reader blocks writer.
    // busy_timeout: instead of failing immediately on lock contention, wait up to 5s.
    // synchronous=NORMAL: safe with WAL, faster than FULL.
    // cache_size: 32 MB page cache — trades a little RAM for fewer disk reads.
    private const string Pragmas = """
        PRAGMA journal_mode=WAL;
        PRAGMA busy_timeout=5000;
        PRAGMA synchronous=NORMAL;
        PRAGMA cache_size=-32768;
        PRAGMA foreign_keys=ON;
        """;

    // PiiPatterns table WITHOUT the PatternType CHECK constraint.
    // New pattern types (Like, BeginsWith, AfterLabel, etc.) are validated at the
    // application layer via the PatternType enum — no DB constraint needed.
    // Existing databases that still have the old CHECK are migrated in Initialize().
    private const string Ddl = """
        CREATE TABLE IF NOT EXISTS Clients (
            Id          INTEGER PRIMARY KEY,
            Name        TEXT NOT NULL,
            ApiKeyHash  TEXT NOT NULL UNIQUE,
            IsActive    INTEGER DEFAULT 1,
            CreatedAt   TEXT DEFAULT (datetime('now'))
        );

        CREATE TABLE IF NOT EXISTS PiiFields (
            Id          INTEGER PRIMARY KEY,
            ClientId    INTEGER REFERENCES Clients(Id),
            FieldName   TEXT NOT NULL,
            ReplaceWith TEXT DEFAULT '█',
            IsActive    INTEGER DEFAULT 1,
            IsPreserve  INTEGER DEFAULT 0,
            Priority    INTEGER DEFAULT 0
        );

        CREATE TABLE IF NOT EXISTS PiiPatterns (
            Id               INTEGER PRIMARY KEY,
            FieldId          INTEGER NOT NULL REFERENCES PiiFields(Id),
            PatternType      TEXT NOT NULL,
            Pattern          TEXT NOT NULL,
            Priority         INTEGER DEFAULT 0,
            ScopeStart       TEXT,
            ScopeEnd         TEXT,
            ScopeEndPosition INTEGER DEFAULT 500
        );

        CREATE TABLE IF NOT EXISTS RequestLogs (
            Id          INTEGER PRIMARY KEY,
            ClientId    INTEGER REFERENCES Clients(Id),
            RequestedAt TEXT NOT NULL DEFAULT (datetime('now')),
            FileName    TEXT,
            FileSizeKb  INTEGER,
            DurationMs  INTEGER,
            FieldsHit   TEXT,
            ErrorMsg    TEXT
        );

        CREATE INDEX IF NOT EXISTS ix_requestlogs_requestedat ON RequestLogs(RequestedAt);

        CREATE TABLE IF NOT EXISTS Settings (
            Key         TEXT PRIMARY KEY,
            Value       TEXT,
            Description TEXT
        );
        """;

    private readonly string _connectionString;

    public SchemaInitializer(string connectionString)
    {
        _connectionString = connectionString;
    }

    public void Initialize()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        Execute(conn, Pragmas);
        Execute(conn, Ddl);

        // ── Migration: remove CHECK constraint on PatternType ────────────────
        MigratePatternTypeConstraint(conn);

        // ── Migration: add IsPreserve column if missing ───────────────────────
        MigrateAddIsPreserve(conn);

        // ── Migration: add Priority column to PiiFields if missing ───────────
        MigrateAddFieldPriority(conn);

        // ── Migration: add ScopeStart / ScopeEnd columns to PiiPatterns ──────
        MigrateAddPatternScope(conn);

        // ── Migration: add WebhookUrl column to Clients ───────────────────────
        MigrateAddClientWebhook(conn);
    }

    /// <summary>
    /// If PiiPatterns still has the legacy CHECK(PatternType IN ('Regex','ConstList','LlmPrompt')),
    /// recreate it without the constraint so new pattern types can be stored.
    /// Runs in a transaction; original data is fully preserved.
    /// </summary>
    private static void MigratePatternTypeConstraint(SqliteConnection conn)
    {
        // Peek at the CREATE TABLE statement stored in sqlite_master
        using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText =
            "SELECT sql FROM sqlite_master WHERE type='table' AND name='PiiPatterns'";
        var ddl = checkCmd.ExecuteScalar() as string ?? string.Empty;

        if (!ddl.Contains("CHECK", StringComparison.OrdinalIgnoreCase))
            return; // already migrated or table doesn't exist yet

        // Recreate without the CHECK constraint inside a transaction
        using var tx = conn.BeginTransaction();
        try
        {
            Execute(conn, """
                CREATE TABLE PiiPatterns_new (
                    Id          INTEGER PRIMARY KEY,
                    FieldId     INTEGER NOT NULL REFERENCES PiiFields(Id),
                    PatternType TEXT NOT NULL,
                    Pattern     TEXT NOT NULL,
                    Priority    INTEGER DEFAULT 0
                )
                """);

            Execute(conn, "INSERT INTO PiiPatterns_new SELECT * FROM PiiPatterns");
            Execute(conn, "DROP TABLE PiiPatterns");
            Execute(conn, "ALTER TABLE PiiPatterns_new RENAME TO PiiPatterns");

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>Adds the IsPreserve column to PiiFields if it does not exist yet.</summary>
    private static void MigrateAddIsPreserve(SqliteConnection conn)
    {
        using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText =
            "SELECT sql FROM sqlite_master WHERE type='table' AND name='PiiFields'";
        var ddl = checkCmd.ExecuteScalar() as string ?? string.Empty;

        if (ddl.Contains("IsPreserve", StringComparison.OrdinalIgnoreCase))
            return; // already present

        Execute(conn, "ALTER TABLE PiiFields ADD COLUMN IsPreserve INTEGER DEFAULT 0");
    }

    /// <summary>Adds the Priority column to PiiFields if it does not exist yet.</summary>
    private static void MigrateAddFieldPriority(SqliteConnection conn)
    {
        using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText =
            "SELECT sql FROM sqlite_master WHERE type='table' AND name='PiiFields'";
        var ddl = checkCmd.ExecuteScalar() as string ?? string.Empty;

        if (ddl.Contains("Priority", StringComparison.OrdinalIgnoreCase))
            return; // already present

        Execute(conn, "ALTER TABLE PiiFields ADD COLUMN Priority INTEGER DEFAULT 0");
    }

    /// <summary>Adds ScopeStart and ScopeEnd columns to PiiPatterns if they do not exist yet.</summary>
    private static void MigrateAddPatternScope(SqliteConnection conn)
    {
        using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText =
            "SELECT sql FROM sqlite_master WHERE type='table' AND name='PiiPatterns'";
        var ddl = checkCmd.ExecuteScalar() as string ?? string.Empty;

        if (!ddl.Contains("ScopeStart", StringComparison.OrdinalIgnoreCase))
            Execute(conn, "ALTER TABLE PiiPatterns ADD COLUMN ScopeStart TEXT");

        if (!ddl.Contains("ScopeEnd", StringComparison.OrdinalIgnoreCase))
            Execute(conn, "ALTER TABLE PiiPatterns ADD COLUMN ScopeEnd TEXT");

        if (!ddl.Contains("ScopeEndPosition", StringComparison.OrdinalIgnoreCase))
            Execute(conn, "ALTER TABLE PiiPatterns ADD COLUMN ScopeEndPosition INTEGER DEFAULT 500");
    }

    /// <summary>Adds WebhookUrl column to Clients if missing.</summary>
    private static void MigrateAddClientWebhook(SqliteConnection conn)
    {
        using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='Clients'";
        var ddl = checkCmd.ExecuteScalar() as string ?? string.Empty;
        if (!ddl.Contains("WebhookUrl", StringComparison.OrdinalIgnoreCase))
            Execute(conn, "ALTER TABLE Clients ADD COLUMN WebhookUrl TEXT");
    }

    private static void Execute(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
