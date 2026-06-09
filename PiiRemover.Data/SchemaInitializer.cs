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
            ReplaceWith TEXT DEFAULT '████',
            IsActive    INTEGER DEFAULT 1
        );

        CREATE TABLE IF NOT EXISTS PiiPatterns (
            Id          INTEGER PRIMARY KEY,
            FieldId     INTEGER NOT NULL REFERENCES PiiFields(Id),
            PatternType TEXT NOT NULL CHECK(PatternType IN ('Regex','ConstList','LlmPrompt')),
            Pattern     TEXT NOT NULL,
            Priority    INTEGER DEFAULT 0
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
    }

    private static void Execute(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
