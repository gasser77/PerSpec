#!/usr/bin/env python3
"""
Database Auto-Maintenance Script
Automatically updates database schema to match current package version
Run this after package updates to ensure database compatibility
"""

# Prevent Python from creating .pyc files
import sys
import os
sys.dont_write_bytecode = True
os.environ['PYTHONDONTWRITEBYTECODE'] = '1'

import sqlite3
from pathlib import Path
import json
from datetime import datetime, timedelta

_DOTNET_EPOCH = datetime(1, 1, 1)

def _dotnet_ticks_days_ago(days: int) -> int:
    """Return a .NET DateTime tick count for `days` ago in local time.

    Mirrors test_coordinator._dotnet_ticks_now so cleanup cutoffs are comparable
    with the tick values actually stored in created_at columns.
    """
    delta = (datetime.now() - timedelta(days=days)) - _DOTNET_EPOCH
    return delta.days * 864_000_000_000 + delta.seconds * 10_000_000 + delta.microseconds * 10

def get_project_root():
    """Find Unity project root by looking for Assets folder"""
    current = Path.cwd()
    while current != current.parent:
        if (current / "Assets").exists():
            return current
        current = current.parent
    return Path.cwd()

def get_db_path():
    """Get database path in PerSpec folder"""
    project_root = get_project_root()
    perspec_dir = project_root / "PerSpec"
    return str(perspec_dir / "test_coordination.db")

def get_schema_version(conn):
    """Get current schema version from database"""
    cursor = conn.cursor()
    try:
        # Check if schema_version table exists
        cursor.execute("""
            SELECT name FROM sqlite_master 
            WHERE type='table' AND name='schema_version'
        """)
        if not cursor.fetchone():
            return 0
        
        # Get current version. Use MAX(version) rather than the most-recent applied_at:
        # CURRENT_TIMESTAMP has 1-second resolution, so migrations applied in the same
        # second tie on applied_at and can be returned out of order, causing the last
        # migration to be re-run (harmlessly) on every invocation.
        cursor.execute("SELECT MAX(version) FROM schema_version")
        result = cursor.fetchone()
        return result[0] if result and result[0] is not None else 0
    except:
        return 0

def set_schema_version(conn, version, description=""):
    """Update schema version in database"""
    cursor = conn.cursor()
    
    # Create version table if it doesn't exist
    cursor.execute("""
        CREATE TABLE IF NOT EXISTS schema_version (
            version INTEGER PRIMARY KEY,
            description TEXT,
            applied_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
        )
    """)
    
    # Insert new version
    cursor.execute("""
        INSERT OR REPLACE INTO schema_version (version, description, applied_at)
        VALUES (?, ?, CURRENT_TIMESTAMP)
    """, (version, description))
    
    conn.commit()

def apply_migration_v1(conn):
    """Migration v1: Add new test status values"""
    print("  Applying Migration v1: Add new test status values...")
    cursor = conn.cursor()
    
    try:
        # Check if we need to update the constraint
        cursor.execute("""
            SELECT sql FROM sqlite_master 
            WHERE type='table' AND name='test_requests'
        """)
        result = cursor.fetchone()
        
        needs_rebuild = result and (
            'processing' not in result[0] or "'inconclusive'" not in result[0]
        )
        if needs_rebuild:
            print("    Updating test_requests status constraint...")

            # Drop staging table from any prior failed migration attempt.
            cursor.execute("DROP TABLE IF EXISTS test_requests_new")

            # Create new table with updated constraint
            cursor.execute("""
                CREATE TABLE test_requests_new (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    request_type TEXT NOT NULL,
                    test_filter TEXT,
                    test_platform TEXT NOT NULL,
                    priority INTEGER DEFAULT 0,
                    status TEXT DEFAULT 'pending' CHECK(status IN (
                        'pending', 'processing', 'executing', 'finalizing',
                        'running', 'completed', 'failed', 'timeout',
                        'cancelled', 'inconclusive'
                    )),
                    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                    started_at TIMESTAMP,
                    completed_at TIMESTAMP,
                    total_tests INTEGER,
                    passed_tests INTEGER,
                    failed_tests INTEGER,
                    skipped_tests INTEGER,
                    duration_seconds REAL,
                    error_message TEXT
                )
            """)
            
            # Copy data
            cursor.execute("""
                INSERT INTO test_requests_new 
                SELECT id, request_type, test_filter, test_platform, priority, 
                       status, created_at, started_at, completed_at,
                       total_tests, passed_tests, failed_tests, skipped_tests,
                       duration_seconds, error_message
                FROM test_requests
            """)
            
            # Drop old and rename new
            cursor.execute("DROP TABLE test_requests")
            cursor.execute("ALTER TABLE test_requests_new RENAME TO test_requests")
            
            # Recreate indexes
            cursor.execute("CREATE INDEX IF NOT EXISTS idx_test_requests_status ON test_requests(status)")
            cursor.execute("CREATE INDEX IF NOT EXISTS idx_test_requests_created ON test_requests(created_at)")
            
            print("    Status constraint updated successfully")
        else:
            print("    Status constraint already up-to-date")
            
        return True
    except Exception as e:
        print(f"    Error in migration v1: {e}")
        return False

def apply_migration_v2(conn):
    """Migration v2: Add refresh_requests status values"""
    print("  Applying Migration v2: Update refresh_requests status values...")
    cursor = conn.cursor()
    
    try:
        # Check if refresh_requests table exists
        cursor.execute("""
            SELECT sql FROM sqlite_master 
            WHERE type='table' AND name='refresh_requests'
        """)
        result = cursor.fetchone()
        
        if result and 'processing' not in str(result[0]):
            print("    Updating refresh_requests status constraint...")
            
            # Similar process for refresh_requests
            cursor.execute("""
                CREATE TABLE IF NOT EXISTS refresh_requests_new (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    refresh_type TEXT NOT NULL,
                    import_options TEXT,
                    priority INTEGER DEFAULT 0,
                    status TEXT DEFAULT 'pending' CHECK(status IN (
                        'pending', 'processing', 'running', 'completed', 'failed', 'cancelled'
                    )),
                    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                    started_at TIMESTAMP,
                    completed_at TIMESTAMP,
                    duration_seconds REAL,
                    result_message TEXT
                )
            """)
            
            # Copy data
            cursor.execute("""
                INSERT INTO refresh_requests_new 
                SELECT * FROM refresh_requests
            """)
            
            # Drop old and rename new
            cursor.execute("DROP TABLE refresh_requests")
            cursor.execute("ALTER TABLE refresh_requests_new RENAME TO refresh_requests")
            
            print("    Refresh requests constraint updated successfully")
        else:
            print("    Refresh requests constraint already up-to-date")
            
        return True
    except Exception as e:
        print(f"    Error in migration v2: {e}")
        return False

def apply_migration_v3(conn):
    """Migration v3: Clean up old data and optimize database"""
    print("  Applying Migration v3: Database optimization...")
    cursor = conn.cursor()
    
    try:
        # Clean old test requests (older than 7 days).
        #
        # created_at is stored either as an INT64 .NET tick count (sqlite-net on the
        # Unity side, and Python inserts, both use ticks) or as legacy TEXT. SQLite
        # sorts every INTEGER before every TEXT, so comparing a tick value against
        # datetime('now', ...) is unconditionally true and would delete live rows -
        # including the request a --wait is currently polling. Compare each storage
        # class against a cutoff of its own type.
        cutoff_ticks = _dotnet_ticks_days_ago(7)

        cursor.execute("""
            DELETE FROM test_requests
            WHERE typeof(created_at) IN ('integer', 'real')
              AND created_at < ?
        """, (cutoff_ticks,))
        deleted_tests = cursor.rowcount

        cursor.execute("""
            DELETE FROM test_requests
            WHERE typeof(created_at) = 'text'
              AND created_at < datetime('now', '-7 days')
        """)
        deleted_tests += cursor.rowcount
        
        # Clean old test results
        cursor.execute("""
            DELETE FROM test_results 
            WHERE request_id NOT IN (SELECT id FROM test_requests)
        """)
        deleted_results = cursor.rowcount
        
        # Clean old logs (console_logs has timestamp column, not created_at)
        cursor.execute("""
            DELETE FROM console_logs 
            WHERE timestamp < datetime('now', '-1 hours')
        """)
        deleted_logs = cursor.rowcount
        
        print(f"    Cleaned: {deleted_tests} old requests, {deleted_results} orphaned results, {deleted_logs} old logs")
        print("    Database will be optimized after migrations complete")
        
        return True
    except Exception as e:
        print(f"    Error in migration v3: {e}")
        return False

def apply_migration_v4(conn):
    """Migration v4: Add missing indexes for performance"""
    print("  Applying Migration v4: Performance indexes...")
    cursor = conn.cursor()
    
    try:
        # Add indexes for better query performance
        indexes = [
            ("idx_test_results_request", "test_results(request_id)"),
            ("idx_console_logs_timestamp", "console_logs(timestamp)"),
            ("idx_execution_log_request", "execution_log(request_id)"),
            # The table is named menu_item_requests in db_initializer.py.
            # The previous typo `menu_requests` caused v4 to fail every run,
            # which broke the migration chain (v5 never ran).
            ("idx_menu_item_requests_status", "menu_item_requests(status)"),
        ]
        
        for idx_name, idx_def in indexes:
            cursor.execute(f"CREATE INDEX IF NOT EXISTS {idx_name} ON {idx_def}")
            print(f"    Created index: {idx_name}")
        
        return True
    except Exception as e:
        print(f"    Error in migration v4: {e}")
        return False

def apply_migration_v5(conn):
    """Migration v5: Add 'inconclusive' to test_requests status CHECK constraint"""
    print("  Applying Migration v5: Add 'inconclusive' status value...")
    cursor = conn.cursor()

    try:
        cursor.execute("""
            SELECT sql FROM sqlite_master
            WHERE type='table' AND name='test_requests'
        """)
        result = cursor.fetchone()
        if not result or not result[0]:
            print("    test_requests table missing - skipping")
            return True

        if "'inconclusive'" in result[0]:
            print("    'inconclusive' already present - skipping")
            return True

        print("    Rebuilding test_requests to add 'inconclusive'...")

        cursor.execute("DROP TABLE IF EXISTS test_requests_new")
        cursor.execute("""
            CREATE TABLE test_requests_new (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                request_type TEXT NOT NULL,
                test_filter TEXT,
                test_platform TEXT NOT NULL,
                priority INTEGER DEFAULT 0,
                status TEXT DEFAULT 'pending' CHECK(status IN (
                    'pending', 'processing', 'executing', 'finalizing',
                    'running', 'completed', 'failed', 'timeout',
                    'cancelled', 'inconclusive'
                )),
                created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                started_at TIMESTAMP,
                completed_at TIMESTAMP,
                total_tests INTEGER,
                passed_tests INTEGER,
                failed_tests INTEGER,
                skipped_tests INTEGER,
                duration_seconds REAL,
                error_message TEXT
            )
        """)

        cursor.execute("""
            INSERT INTO test_requests_new (
                id, request_type, test_filter, test_platform, priority,
                status, created_at, started_at, completed_at,
                total_tests, passed_tests, failed_tests, skipped_tests,
                duration_seconds, error_message
            )
            SELECT
                id, request_type, test_filter, test_platform, priority,
                status, created_at, started_at, completed_at,
                total_tests, passed_tests, failed_tests, skipped_tests,
                duration_seconds, error_message
            FROM test_requests
        """)

        cursor.execute("DROP TABLE test_requests")
        cursor.execute("ALTER TABLE test_requests_new RENAME TO test_requests")
        cursor.execute("CREATE INDEX IF NOT EXISTS idx_test_requests_status ON test_requests(status)")
        cursor.execute("CREATE INDEX IF NOT EXISTS idx_test_requests_created ON test_requests(created_at)")

        print("    'inconclusive' added to status constraint")
        return True
    except Exception as e:
        print(f"    Error in migration v5: {e}")
        return False


def apply_migration_v6(conn):
    """Migration v6: Add 'compiling' to asset_refresh_requests status CHECK constraint.

    Enables two-phase refresh completion: Unity marks a refresh 'compiling' while
    scripts recompile + domain reload, and only writes 'completed' once that finishes.
    Targets the real 'asset_refresh_requests' table (note: the legacy v2 migration
    targeted a phantom 'refresh_requests' name and never touched this table).
    """
    print("  Applying Migration v6: Add 'compiling' refresh status value...")
    cursor = conn.cursor()

    try:
        cursor.execute("""
            SELECT sql FROM sqlite_master
            WHERE type='table' AND name='asset_refresh_requests'
        """)
        result = cursor.fetchone()
        if not result or not result[0]:
            print("    asset_refresh_requests table missing - skipping")
            return True

        if "'compiling'" in result[0]:
            print("    'compiling' already present - skipping")
            return True

        print("    Rebuilding asset_refresh_requests to add 'compiling'...")

        cursor.execute("DROP TABLE IF EXISTS asset_refresh_requests_new")
        cursor.execute("""
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
        """)

        cursor.execute("""
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
        """)

        cursor.execute("DROP TABLE asset_refresh_requests")
        cursor.execute("ALTER TABLE asset_refresh_requests_new RENAME TO asset_refresh_requests")
        cursor.execute("CREATE INDEX IF NOT EXISTS idx_refresh_status ON asset_refresh_requests(status)")
        cursor.execute("CREATE INDEX IF NOT EXISTS idx_refresh_created ON asset_refresh_requests(created_at DESC)")

        print("    'compiling' added to status constraint")
        return True
    except Exception as e:
        print(f"    Error in migration v6: {e}")
        return False


def run_maintenance():
    """Run all database maintenance tasks"""
    print("\n" + "="*60)
    print("PerSpec Database Auto-Maintenance")
    print("="*60)
    
    db_path = get_db_path()
    
    # Check if database exists
    if not Path(db_path).exists():
        print(f"Database not found at: {db_path}")
        print("Database will be created on first use.")
        return True
    
    print(f"Database path: {db_path}")
    
    try:
        conn = sqlite3.connect(db_path)
        conn.execute("PRAGMA journal_mode=WAL")
        
        # Get current schema version
        current_version = get_schema_version(conn)
        print(f"\nCurrent schema version: {current_version}")
        
        # Define migrations
        migrations = [
            (1, "Add new test status values", apply_migration_v1),
            (2, "Update refresh_requests status", apply_migration_v2),
            (3, "Clean old data and optimize", apply_migration_v3),
            (4, "Add performance indexes", apply_migration_v4),
            (5, "Add 'inconclusive' status value", apply_migration_v5),
            (6, "Add 'compiling' refresh status value", apply_migration_v6),
        ]
        
        # Apply pending migrations
        applied_count = 0
        for version, description, migration_func in migrations:
            if version > current_version:
                print(f"\nMigration {version}: {description}")
                
                # Begin transaction for this migration
                conn.execute("BEGIN TRANSACTION")
                
                try:
                    if migration_func(conn):
                        set_schema_version(conn, version, description)
                        conn.commit()
                        applied_count += 1
                        print(f"  [SUCCESS] Migration {version} completed")
                    else:
                        conn.rollback()
                        print(f"  [WARNING] Migration {version} skipped")
                        break
                except Exception as e:
                    conn.rollback()
                    print(f"  [ERROR] Migration {version} failed: {e}")
                    break
        
        if applied_count == 0:
            print("\nDatabase is already up-to-date!")
        else:
            print(f"\nApplied {applied_count} migration(s) successfully")
            
            # Run VACUUM after all migrations complete
            try:
                conn.execute("VACUUM")
                print("Database optimized (VACUUM completed)")
            except:
                pass  # VACUUM is optional optimization
        
        # Show final database stats
        cursor = conn.cursor()
        cursor.execute("SELECT page_count * page_size / 1024.0 / 1024.0 FROM pragma_page_count(), pragma_page_size()")
        db_size = cursor.fetchone()[0]
        
        print(f"\nDatabase size: {db_size:.2f} MB")
        
        # Final schema version
        final_version = get_schema_version(conn)
        print(f"Final schema version: {final_version}")
        
        conn.close()
        
        print("\n" + "="*60)
        print("[SUCCESS] Database maintenance completed!")
        print("="*60 + "\n")
        
        return True
        
    except Exception as e:
        print(f"\n[ERROR] Database maintenance failed: {e}")
        return False

if __name__ == "__main__":
    success = run_maintenance()
    sys.exit(0 if success else 1)