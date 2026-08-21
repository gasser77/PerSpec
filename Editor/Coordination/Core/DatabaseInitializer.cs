using System;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEditor;
using SQLite;

namespace PerSpec.Editor.Coordination
{
    /// <summary>
    /// Automatically initializes the SQLite database when PerSpec is enabled.
    /// Creates all required tables using CREATE TABLE IF NOT EXISTS for idempotence.
    /// </summary>
    [InitializeOnLoad]
    public static class DatabaseInitializer
    {
        private const string ENABLED_PREF_KEY = "PerSpec_Enabled";
        private static bool _hasInitializedThisSession;

        static DatabaseInitializer()
        {
            // Use delayCall to ensure proper initialization order
            EditorApplication.delayCall += InitializeIfNeeded;
        }

        private static void InitializeIfNeeded()
        {
            // Avoid multiple initializations in same session
            if (_hasInitializedThisSession) return;

            // Only run if PerSpec should be running (enabled AND initialized)
            // Inline the ShouldRun check to avoid circular assembly dependency
            string projectPath = Directory.GetParent(Application.dataPath).FullName;
            string perspecPath = Path.Combine(projectPath, "PerSpec");

            bool isEnabled = EditorPrefs.GetBool(ENABLED_PREF_KEY, true);
            bool isInitialized = Directory.Exists(perspecPath);

            if (!isEnabled || !isInitialized) return;

            EnsureDatabaseReady();

            _hasInitializedThisSession = true;
        }

        /// <summary>
        /// Ensures the database exists and its schema is current. Creates the DB and all
        /// tables if the file is missing; otherwise verifies and self-heals the existing
        /// schema WITHOUT re-running full initialization (which can throw when SQLiteManager
        /// already holds the DB open). This is the entry point callers should use.
        /// </summary>
        /// <returns>True if the database is ready, false on error.</returns>
        public static bool EnsureDatabaseReady()
        {
            string projectPath = Directory.GetParent(Application.dataPath).FullName;
            string dbPath = Path.Combine(projectPath, "PerSpec", "test_coordination.db");
            return File.Exists(dbPath) ? EnsureSchemaCurrent() : EnsureDatabaseExists();
        }

        /// <summary>
        /// Ensures the database file and all tables exist.
        /// Safe to call multiple times - uses CREATE TABLE IF NOT EXISTS.
        /// </summary>
        /// <returns>True if database is ready, false on error</returns>
        public static bool EnsureDatabaseExists()
        {
            try
            {
                string projectPath = Directory.GetParent(Application.dataPath).FullName;
                string perspecPath = Path.Combine(projectPath, "PerSpec");
                string dbPath = Path.Combine(perspecPath, "test_coordination.db");

                // Check if PerSpec folder exists
                if (!Directory.Exists(perspecPath))
                {
                    Debug.LogWarning("[DatabaseInitializer] PerSpec folder does not exist. Cannot initialize database.");
                    return false;
                }

                // Create database and tables
                using (var connection = new SQLiteConnection(dbPath, SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create | SQLiteOpenFlags.FullMutex))
                {
                    connection.BusyTimeout = TimeSpan.FromSeconds(5);

                    // Enable WAL mode for better concurrency
                    connection.Execute("PRAGMA journal_mode=WAL");

                    // Create all tables
                    CreateTables(connection);

                    // Create all indexes
                    CreateIndexes(connection);

                    // Repair schemas that predate current constraints (self-healing migration)
                    EnsureRefreshStatusConstraint(connection);
                    EnsureTestStatusConstraint(connection);

                    // Initialize system status if empty
                    InitializeSystemStatus(connection);
                }

                Debug.Log("[DatabaseInitializer] Database initialized successfully");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[DatabaseInitializer] Error initializing database: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Verifies and self-heals the schema of an EXISTING database without re-running full
        /// initialization. Opens a short-lived read/write connection (no Create flag, no PRAGMA
        /// re-set) so it does not conflict with a connection SQLiteManager already holds open,
        /// then applies the idempotent constraint repair. Safe to call once per session.
        /// </summary>
        public static bool EnsureSchemaCurrent()
        {
            try
            {
                string projectPath = Directory.GetParent(Application.dataPath).FullName;
                string dbPath = Path.Combine(projectPath, "PerSpec", "test_coordination.db");

                if (!File.Exists(dbPath)) return false;

                using (var connection = new SQLiteConnection(dbPath, SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.FullMutex))
                {
                    connection.BusyTimeout = TimeSpan.FromSeconds(5);
                    EnsureRefreshStatusConstraint(connection);
                    EnsureTestStatusConstraint(connection);
                }

                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[DatabaseInitializer] Error verifying database schema: {e.Message}");
                return false;
            }
        }

        private static void CreateTables(SQLiteConnection connection)
        {
            // Table 1: test_requests
            connection.Execute(@"
                CREATE TABLE IF NOT EXISTS test_requests (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    request_type TEXT NOT NULL,
                    test_filter TEXT,
                    test_platform TEXT NOT NULL,
                    status TEXT NOT NULL DEFAULT 'pending',
                    priority INTEGER DEFAULT 0,
                    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                    started_at TIMESTAMP,
                    completed_at TIMESTAMP,
                    result_summary TEXT,
                    error_message TEXT,
                    total_tests INTEGER DEFAULT 0,
                    passed_tests INTEGER DEFAULT 0,
                    failed_tests INTEGER DEFAULT 0,
                    skipped_tests INTEGER DEFAULT 0,
                    duration_seconds REAL DEFAULT 0.0
                )
            ");

            // Table 2: test_results
            connection.Execute(@"
                CREATE TABLE IF NOT EXISTS test_results (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    request_id INTEGER NOT NULL,
                    test_name TEXT NOT NULL,
                    test_class TEXT,
                    test_method TEXT,
                    result TEXT NOT NULL,
                    duration_ms REAL DEFAULT 0.0,
                    error_message TEXT,
                    stack_trace TEXT,
                    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                    FOREIGN KEY (request_id) REFERENCES test_requests(id) ON DELETE CASCADE
                )
            ");

            // Table 3: system_status
            connection.Execute(@"
                CREATE TABLE IF NOT EXISTS system_status (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    component TEXT NOT NULL,
                    status TEXT NOT NULL,
                    last_heartbeat TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                    message TEXT,
                    metadata TEXT
                )
            ");

            // Table 4: execution_log
            connection.Execute(@"
                CREATE TABLE IF NOT EXISTS execution_log (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    request_id INTEGER,
                    log_level TEXT NOT NULL,
                    message TEXT NOT NULL,
                    source TEXT,
                    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                    FOREIGN KEY (request_id) REFERENCES test_requests(id) ON DELETE CASCADE
                )
            ");

            // Table 5: asset_refresh_requests
            connection.Execute(@"
                CREATE TABLE IF NOT EXISTS asset_refresh_requests (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    refresh_type TEXT NOT NULL DEFAULT 'full',
                    paths TEXT,
                    import_options TEXT DEFAULT 'default',
                    status TEXT NOT NULL DEFAULT 'pending' CHECK(status IN ('pending', 'running', 'compiling', 'completed', 'failed', 'cancelled')),
                    priority INTEGER DEFAULT 0,
                    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                    started_at TIMESTAMP,
                    completed_at TIMESTAMP,
                    duration_seconds REAL DEFAULT 0.0,
                    result_message TEXT,
                    error_message TEXT
                )
            ");

            // Table 6: console_logs
            connection.Execute(@"
                CREATE TABLE IF NOT EXISTS console_logs (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    session_id TEXT NOT NULL,
                    log_level TEXT NOT NULL,
                    message TEXT NOT NULL,
                    stack_trace TEXT,
                    truncated_stack TEXT,
                    source_file TEXT,
                    source_line INTEGER,
                    timestamp TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                    frame_count INTEGER,
                    is_truncated BOOLEAN DEFAULT 0,
                    context TEXT,
                    request_id INTEGER,
                    FOREIGN KEY (request_id) REFERENCES test_requests(id)
                )
            ");

            // Table 7: menu_item_requests
            connection.Execute(@"
                CREATE TABLE IF NOT EXISTS menu_item_requests (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    menu_path TEXT NOT NULL,
                    status TEXT NOT NULL DEFAULT 'pending',
                    priority INTEGER DEFAULT 0,
                    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                    started_at TIMESTAMP,
                    completed_at TIMESTAMP,
                    duration_seconds REAL DEFAULT 0.0,
                    result TEXT,
                    error_message TEXT
                )
            ");

            // Table 8: scene_hierarchy_requests
            connection.Execute(@"
                CREATE TABLE IF NOT EXISTS scene_hierarchy_requests (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    request_type TEXT NOT NULL DEFAULT 'full',
                    target_path TEXT,
                    include_inactive INTEGER DEFAULT 1,
                    include_components INTEGER DEFAULT 1,
                    status TEXT NOT NULL DEFAULT 'pending',
                    priority INTEGER DEFAULT 0,
                    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                    started_at TIMESTAMP,
                    completed_at TIMESTAMP,
                    output_file TEXT,
                    error_message TEXT
                )
            ");
        }

        private static void CreateIndexes(SQLiteConnection connection)
        {
            // Test request indexes
            connection.Execute("CREATE INDEX IF NOT EXISTS idx_requests_status ON test_requests(status)");
            connection.Execute("CREATE INDEX IF NOT EXISTS idx_requests_created ON test_requests(created_at DESC)");

            // Test result indexes
            connection.Execute("CREATE INDEX IF NOT EXISTS idx_results_request ON test_results(request_id)");

            // Execution log indexes
            connection.Execute("CREATE INDEX IF NOT EXISTS idx_log_request ON execution_log(request_id)");

            // System status indexes
            connection.Execute("CREATE INDEX IF NOT EXISTS idx_status_component ON system_status(component)");

            // Asset refresh indexes
            connection.Execute("CREATE INDEX IF NOT EXISTS idx_refresh_status ON asset_refresh_requests(status)");
            connection.Execute("CREATE INDEX IF NOT EXISTS idx_refresh_created ON asset_refresh_requests(created_at DESC)");

            // Console log indexes
            connection.Execute("CREATE INDEX IF NOT EXISTS idx_console_logs_session ON console_logs(session_id, timestamp DESC)");
            connection.Execute("CREATE INDEX IF NOT EXISTS idx_console_logs_level ON console_logs(log_level, timestamp DESC)");
            connection.Execute("CREATE INDEX IF NOT EXISTS idx_console_logs_request ON console_logs(request_id, timestamp DESC)");

            // Menu item request indexes
            connection.Execute("CREATE INDEX IF NOT EXISTS idx_menu_status ON menu_item_requests(status)");
            connection.Execute("CREATE INDEX IF NOT EXISTS idx_menu_created ON menu_item_requests(created_at DESC)");

            // Scene hierarchy indexes
            connection.Execute("CREATE INDEX IF NOT EXISTS idx_hierarchy_status ON scene_hierarchy_requests(status)");
        }

        /// <summary>
        /// Self-healing migration for the asset_refresh_requests status CHECK constraint.
        /// The 'compiling' status (two-phase refresh, added in 1.7.0) is rejected by any DB
        /// whose table was created before it existed, causing a "CHECK constraint failed"
        /// error on every compilation. SQLite cannot ALTER a CHECK constraint, so we rebuild
        /// the table. This is the pure-C# equivalent of Python migration v6 and runs on every
        /// editor load without depending on the Python maintenance runner or a script sync.
        /// Idempotent: a no-op once the constraint already allows 'compiling'.
        /// </summary>
        private static void EnsureRefreshStatusConstraint(SQLiteConnection connection)
        {
            var tableSql = connection.ExecuteScalar<string>(
                "SELECT sql FROM sqlite_master WHERE type='table' AND name='asset_refresh_requests'");

            // Table not created yet, or already carries the current constraint - nothing to do.
            if (string.IsNullOrEmpty(tableSql)) return;
            if (tableSql.Contains("'compiling'")) return;

            connection.RunInTransaction(() =>
            {
                connection.Execute("DROP TABLE IF EXISTS asset_refresh_requests_new");
                connection.Execute(@"
                    CREATE TABLE asset_refresh_requests_new (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        refresh_type TEXT NOT NULL DEFAULT 'full' CHECK(refresh_type IN ('full', 'selective')),
                        paths TEXT,
                        import_options TEXT DEFAULT 'default' CHECK(import_options IN ('default', 'synchronous', 'force_update')),
                        status TEXT NOT NULL DEFAULT 'pending' CHECK(status IN ('pending', 'running', 'compiling', 'completed', 'failed', 'cancelled')),
                        priority INTEGER DEFAULT 0,
                        created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                        started_at TIMESTAMP,
                        completed_at TIMESTAMP,
                        duration_seconds REAL DEFAULT 0.0,
                        result_message TEXT,
                        error_message TEXT
                    )
                ");
                connection.Execute(@"
                    INSERT INTO asset_refresh_requests_new (
                        id, refresh_type, paths, import_options, status, priority,
                        created_at, started_at, completed_at, duration_seconds,
                        result_message, error_message
                    )
                    SELECT
                        id, refresh_type, paths, import_options, status, priority,
                        created_at, started_at, completed_at, duration_seconds,
                        result_message, error_message
                    FROM asset_refresh_requests
                ");
                connection.Execute("DROP TABLE asset_refresh_requests");
                connection.Execute("ALTER TABLE asset_refresh_requests_new RENAME TO asset_refresh_requests");
                connection.Execute("CREATE INDEX IF NOT EXISTS idx_refresh_status ON asset_refresh_requests(status)");
                connection.Execute("CREATE INDEX IF NOT EXISTS idx_refresh_created ON asset_refresh_requests(created_at DESC)");
            });

            Debug.Log("[DatabaseInitializer] Upgraded asset_refresh_requests constraint (added 'compiling')");
        }

        /// <summary>
        /// Self-healing migration for the test_requests status CHECK constraint.
        /// Databases created by an older Python db_initializer carry a constraint that
        /// predates 'finalizing', 'timeout', 'inconclusive' and 'no_match'. Writing one of those
        /// statuses then throws "CHECK constraint failed", the error is swallowed, and the
        /// request keeps its previous status - which is one way a run appears stuck.
        /// SQLite cannot ALTER a CHECK constraint, so rebuild the table.
        /// Idempotent, and a no-op on tables that have no status CHECK at all.
        /// </summary>
        private static void EnsureTestStatusConstraint(SQLiteConnection connection)
        {
            var tableSql = connection.ExecuteScalar<string>(
                "SELECT sql FROM sqlite_master WHERE type='table' AND name='test_requests'");

            if (string.IsNullOrEmpty(tableSql)) return;

            // No CHECK on this table (C#-created schema): nothing can be rejected.
            if (!tableSql.Contains("CHECK")) return;

            // Already permits the full status set.
            string[] required = { "'pending'", "'processing'", "'executing'", "'finalizing'",
                                  "'running'", "'completed'", "'failed'", "'timeout'",
                                  "'cancelled'", "'inconclusive'", "'no_match'" };
            if (required.All(status => tableSql.Contains(status))) return;

            connection.RunInTransaction(() =>
            {
                connection.Execute("DROP TABLE IF EXISTS test_requests_new");
                connection.Execute(@"
                    CREATE TABLE test_requests_new (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        request_type TEXT NOT NULL CHECK(request_type IN ('all', 'class', 'method', 'category')),
                        test_filter TEXT,
                        test_platform TEXT NOT NULL CHECK(test_platform IN ('EditMode', 'PlayMode', 'Both')),
                        status TEXT NOT NULL DEFAULT 'pending' CHECK(status IN (
                            'pending', 'processing', 'executing', 'finalizing',
                            'running', 'completed', 'failed', 'timeout',
                            'cancelled', 'inconclusive', 'no_match'
                        )),
                        priority INTEGER DEFAULT 0,
                        created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                        started_at TIMESTAMP,
                        completed_at TIMESTAMP,
                        result_summary TEXT,
                        error_message TEXT,
                        total_tests INTEGER DEFAULT 0,
                        passed_tests INTEGER DEFAULT 0,
                        failed_tests INTEGER DEFAULT 0,
                        skipped_tests INTEGER DEFAULT 0,
                        duration_seconds REAL DEFAULT 0.0
                    )
                ");
                connection.Execute(@"
                    INSERT INTO test_requests_new (
                        id, request_type, test_filter, test_platform, status, priority,
                        created_at, started_at, completed_at, result_summary, error_message,
                        total_tests, passed_tests, failed_tests, skipped_tests, duration_seconds
                    )
                    SELECT
                        id, request_type, test_filter, test_platform, status, priority,
                        created_at, started_at, completed_at, result_summary, error_message,
                        total_tests, passed_tests, failed_tests, skipped_tests, duration_seconds
                    FROM test_requests
                ");
                connection.Execute("DROP TABLE test_requests");
                connection.Execute("ALTER TABLE test_requests_new RENAME TO test_requests");
                connection.Execute("CREATE INDEX IF NOT EXISTS idx_test_requests_status ON test_requests(status)");
                connection.Execute("CREATE INDEX IF NOT EXISTS idx_test_requests_created ON test_requests(created_at DESC)");
            });

            Debug.Log("[DatabaseInitializer] Upgraded test_requests status constraint (added finalizing/timeout/inconclusive/no_match)");
        }

        private static void InitializeSystemStatus(SQLiteConnection connection)
        {
            // Check if Database component already exists
            var count = connection.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM system_status WHERE component = 'Database'"
            );

            if (count == 0)
            {
                connection.Execute(@"
                    INSERT INTO system_status (component, status, message)
                    VALUES ('Database', 'online', 'Database initialized successfully')
                ");
            }
        }
    }
}
