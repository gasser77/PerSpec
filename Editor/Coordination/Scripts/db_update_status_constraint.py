#!/usr/bin/env python3
"""
Update database constraint to support new test status values.

Idempotent: detects whether the test_requests CHECK constraint already
includes every required status value and skips the rebuild if so.
"""

# Prevent Python from creating .pyc files
import sys
import os
sys.dont_write_bytecode = True
os.environ['PYTHONDONTWRITEBYTECODE'] = '1'

import sqlite3
from pathlib import Path

REQUIRED_STATUSES = (
    'pending',
    'processing',
    'executing',
    'finalizing',
    'running',
    'completed',
    'failed',
    'timeout',
    'cancelled',
    'inconclusive',
    'no_match',
)

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

def _schema_has_all_statuses(conn):
    """Return True if test_requests CHECK constraint already lists every required status."""
    cursor = conn.cursor()
    cursor.execute("""
        SELECT sql FROM sqlite_master
        WHERE type='table' AND name='test_requests'
    """)
    row = cursor.fetchone()
    if not row or not row[0]:
        return False
    schema_sql = row[0]
    return all(f"'{status}'" in schema_sql for status in REQUIRED_STATUSES)

def update_status_constraint():
    """Update the status constraint to include new states"""
    db_path = get_db_path()

    if not Path(db_path).exists():
        print(f"Database not found at {db_path}")
        return False

    conn = None
    try:
        conn = sqlite3.connect(db_path)
        cursor = conn.cursor()

        if _schema_has_all_statuses(conn):
            print("Status constraint already includes all required values - nothing to do")
            return True

        conn.execute("BEGIN TRANSACTION")

        print("Updating test_requests table constraint...")

        # SQLite doesn't support ALTER CONSTRAINT directly, so we need to:
        # 1. Create a new table with the updated constraint
        # 2. Copy data from old table
        # 3. Drop old table
        # 4. Rename new table

        # Drop any leftover staging table from a previous failed migration.
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
                    'cancelled', 'inconclusive', 'no_match'
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

        cursor.execute("PRAGMA table_info(test_requests)")
        old_columns = cursor.fetchall()
        print(f"Old table has {len(old_columns)} columns")

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

        cursor.execute("SELECT COUNT(*) FROM test_requests_new")
        count = cursor.fetchone()[0]
        print(f"Copied {count} records to new table")

        cursor.execute("DROP TABLE test_requests")
        cursor.execute("ALTER TABLE test_requests_new RENAME TO test_requests")

        cursor.execute("""
            CREATE INDEX IF NOT EXISTS idx_test_requests_status
            ON test_requests(status)
        """)
        cursor.execute("""
            CREATE INDEX IF NOT EXISTS idx_test_requests_created
            ON test_requests(created_at)
        """)

        conn.commit()
        print("Successfully updated status constraint")

        cursor.execute("""
            SELECT sql FROM sqlite_master
            WHERE type='table' AND name='test_requests'
        """)
        schema = cursor.fetchone()[0]
        print("\nNew table schema:")
        print(schema)

        # Validate: insert a row that exercises every new status, then remove it.
        cursor.execute("""
            INSERT INTO test_requests (request_type, test_platform, status)
            VALUES ('test', 'EditMode', 'processing')
        """)
        test_id = cursor.lastrowid
        for status in ('executing', 'finalizing', 'inconclusive', 'timeout', 'no_match'):
            cursor.execute(
                "UPDATE test_requests SET status = ? WHERE id = ?",
                (status, test_id),
            )
        cursor.execute("DELETE FROM test_requests WHERE id = ?", (test_id,))
        conn.commit()
        print("\nValidation successful - new statuses work correctly")

        return True

    except sqlite3.Error as e:
        print(f"Database error: {e}")
        if conn:
            conn.rollback()
        return False
    except Exception as e:
        print(f"Unexpected error: {e}")
        if conn:
            conn.rollback()
        return False
    finally:
        if conn:
            conn.close()

if __name__ == "__main__":
    print("Database Status Constraint Updater")
    print("=" * 50)

    db_path = get_db_path()
    print(f"Database path: {db_path}")

    if update_status_constraint():
        print("\n[SUCCESS] Update completed successfully!")
        print("\nThe following status values are now supported:")
        print("  - pending (request queued)")
        print("  - processing (Unity processing request)")
        print("  - executing (tests running)")
        print("  - finalizing (collecting results)")
        print("  - completed (all done)")
        print("  - failed (execution failed)")
        print("  - timeout (execution timed out)")
        print("  - cancelled (user cancelled)")
        print("  - inconclusive (test ran but no usable results)")
        print("  - no_match (the filter matched zero tests - nothing ran)")
        print("  - running (legacy, same as executing)")
    else:
        print("\n[FAILED] Update failed!")
        print("Please check the error messages above.")
