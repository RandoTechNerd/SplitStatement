using Microsoft.Data.Sqlite;

namespace MoneySplit;

/// <summary>
/// SQLite plumbing. The database lives next to the app so it's easy to back up;
/// the schema is created on first run (the Python seeder uses the identical DDL).
/// </summary>
public static class Db
{
    public static string Path { get; private set; } = "moneysplit.db";

    public static SqliteConnection Open()
    {
        var con = new SqliteConnection($"Data Source={Path}");
        con.Open();
        return con;
    }

    public static void Init(string dbPath)
    {
        Path = dbPath;
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS cards (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL UNIQUE,
                due_day INTEGER,
                color TEXT DEFAULT '#4F86C6',
                default_bucket TEXT DEFAULT 'Shared',  -- where new charges land before review
                default_payer TEXT DEFAULT 'Aryn',     -- who usually pays the issuer: Mel | Aryn | Both
                carry_mel REAL DEFAULT 0,              -- rollover owed by Mel from previous cycles
                carry_aryn REAL DEFAULT 0,
                stmt_balance REAL,                     -- last entered balance-due, for discrepancy checks
                stmt_balance_at TEXT,
                note TEXT,
                archived INTEGER DEFAULT 0,
                sort INTEGER DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS txns (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                card_id INTEGER NOT NULL REFERENCES cards(id),
                txn_date TEXT,
                post_date TEXT,
                description TEXT NOT NULL,
                category TEXT,
                amount REAL NOT NULL,                  -- positive = charge, negative = refund/credit
                bucket TEXT NOT NULL DEFAULT 'Shared', -- Shared | Mel | Aryn | Skip
                note TEXT,
                needs_review INTEGER DEFAULT 0,
                settled_id INTEGER REFERENCES settlements(id),
                source TEXT,
                hash TEXT,
                created_at TEXT DEFAULT (datetime('now'))
            );
            CREATE INDEX IF NOT EXISTS idx_txns_card ON txns(card_id, settled_id);
            CREATE UNIQUE INDEX IF NOT EXISTS idx_txns_hash ON txns(hash) WHERE hash IS NOT NULL;

            -- Merchant memory: counts of how each normalized merchant has been bucketed.
            CREATE TABLE IF NOT EXISTS rules (
                merchant TEXT PRIMARY KEY,
                shared_n INTEGER DEFAULT 0,
                mel_n INTEGER DEFAULT 0,
                aryn_n INTEGER DEFAULT 0,
                updated_at TEXT DEFAULT (datetime('now'))
            );

            CREATE TABLE IF NOT EXISTS settlements (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                card_id INTEGER REFERENCES cards(id),
                settled_at TEXT DEFAULT (datetime('now')),
                label TEXT,
                shared_total REAL, mel_total REAL, aryn_total REAL,
                mel_part REAL, aryn_part REAL,
                payments TEXT,                         -- JSON: [{who, method, amount, date, note}]
                note TEXT,
                by TEXT
            );

            CREATE TABLE IF NOT EXISTS meta (k TEXT PRIMARY KEY, v TEXT);

            -- Running history of actions, so you can see what happened when.
            CREATE TABLE IF NOT EXISTS activity (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                at TEXT DEFAULT (datetime('now','localtime')),
                kind TEXT,
                detail TEXT
            );
            """;
        cmd.ExecuteNonQuery();

        // migrations for older databases
        foreach (var ddl in new[]
        {
            "ALTER TABLE cards ADD COLUMN file_hint TEXT",
            "ALTER TABLE cards ADD COLUMN last4 TEXT",
            "ALTER TABLE txns ADD COLUMN flag_reason TEXT",
            "ALTER TABLE txns ADD COLUMN flag_by TEXT",      // 'app' | 'Aryn' | 'Mel'; NULL = not flagged
            "ALTER TABLE txns ADD COLUMN receipt TEXT",      // filename under data\receipts
            "ALTER TABLE cards ADD COLUMN card_type TEXT",   // CardCatalog key; NULL = unknown
            "ALTER TABLE cards ADD COLUMN rewards TEXT",      // one-line reward summary
        })
        {
            try
            {
                using var alter = con.CreateCommand();
                alter.CommandText = ddl;
                alter.ExecuteNonQuery();
            }
            catch (SqliteException) { /* column already exists */ }
        }
    }
}
