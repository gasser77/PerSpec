#!/usr/bin/env python3
"""
Asset Refresh Coordinator - Python interface for Unity asset refresh coordination
"""


# Prevent Python from creating .pyc files
import sys
import os
sys.dont_write_bytecode = True
os.environ['PYTHONDONTWRITEBYTECODE'] = '1'
import sqlite3
import json
import time
from datetime import datetime, timedelta
from pathlib import Path
from enum import Enum
from typing import Optional, List, Dict, Any

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
    perspec_dir.mkdir(exist_ok=True)
    return str(perspec_dir / "test_coordination.db")


_DOTNET_EPOCH = datetime(1, 1, 1)

def _dotnet_ticks_now() -> int:
    """Return the current local time as .NET DateTime.Now.Ticks.

    sqlite-net (Unity side) defaults to StoreDateTimeAsTicks=true and stores
    `created_at` as an INT64 tick count of local time since 0001-01-01. If Python
    INSERTs a TEXT timestamp instead, a later sqlite-net `_connection.Update(entity)`
    (every status change) reads the column, SQLite coerces the TEXT via NUMERIC
    affinity to its leading integer prefix (e.g. "2026-07-06T..." becomes 2026), and
    writes back 2026 as INT64. Subsequent maintenance `DELETE WHERE created_at < ?`
    then matches and deletes the still-in-flight request.

    Before v1.7.0 a refresh completed in ~0.1s, so the row was gone before cleanup
    ran and the corruption was harmless. Now that a refresh is held through the
    'compiling' + domain-reload phases (seconds), cleanup can delete it mid-flight -
    so we write INT64 ticks up front, exactly as test_coordinator.py does.
    """
    delta = datetime.now() - _DOTNET_EPOCH
    # DateTime.Ticks is 100-nanosecond intervals; preserve microsecond precision.
    return delta.days * 864_000_000_000 + delta.seconds * 10_000_000 + delta.microseconds * 10

def _format_db_timestamp(value):
    """Render a created_at/started_at/completed_at value as a readable local datetime.

    Values are stored as .NET INT64 ticks (by both Python and Unity's sqlite-net);
    older rows may still hold ISO text. Return the input unchanged if it can't be parsed.
    """
    if value is None:
        return None
    if isinstance(value, (int, float)):
        try:
            return (_DOTNET_EPOCH + timedelta(microseconds=int(value) / 10)).strftime('%Y-%m-%d %H:%M:%S')
        except (OverflowError, ValueError):
            return str(value)
    return str(value)

class RefreshType(Enum):
    FULL = "full"
    SELECTIVE = "selective"

class ImportOptions(Enum):
    DEFAULT = "default"
    SYNCHRONOUS = "synchronous"
    FORCE_UPDATE = "force_update"

class RefreshStatus(Enum):
    PENDING = "pending"       # queued, Unity has not yet picked it up
    RUNNING = "running"       # Unity received it, importing assets
    COMPILING = "compiling"   # scripts recompiling, domain reload pending
    COMPLETED = "completed"   # refresh + any compilation + domain reload finished
    FAILED = "failed"         # the refresh request itself could not be processed
    CANCELLED = "cancelled"

class AssetRefreshCoordinator:
    def __init__(self, db_path: Optional[str] = None):
        """Initialize the asset refresh coordinator"""
        if db_path is None:
            db_path = get_db_path()
        
        self.db_path = db_path
        self._verify_database()
    
    def _verify_database(self):
        """Verify database exists and has required tables"""
        conn = sqlite3.connect(self.db_path)
        cursor = conn.cursor()
        
        try:
            cursor.execute("""
                SELECT name FROM sqlite_master 
                WHERE type='table' AND name='asset_refresh_requests'
            """)
            if not cursor.fetchone():
                raise RuntimeError("asset_refresh_requests table not found. Run db_initializer.py first.")
        finally:
            conn.close()
    
    def submit_refresh_request(self, 
                              refresh_type: RefreshType = RefreshType.FULL,
                              paths: Optional[List[str]] = None,
                              import_options: ImportOptions = ImportOptions.DEFAULT,
                              priority: int = 0) -> int:
        """Submit an asset refresh request to the database"""
        conn = sqlite3.connect(self.db_path)
        cursor = conn.cursor()
        
        try:
            # Convert paths list to JSON string if provided
            paths_json = json.dumps(paths) if paths else None
            
            # Write created_at as .NET INT64 ticks so sqlite-net round-trips it safely.
            # See _dotnet_ticks_now() docstring for the SQLite type-affinity corruption
            # (and mid-flight row deletion) this avoids.
            cursor.execute("""
                INSERT INTO asset_refresh_requests
                (refresh_type, paths, import_options, status, priority, created_at)
                VALUES (?, ?, ?, 'pending', ?, ?)
            """, (
                refresh_type.value,
                paths_json,
                import_options.value,
                priority,
                _dotnet_ticks_now()
            ))
            
            request_id = cursor.lastrowid
            conn.commit()
            
            print(f"[SUCCESS] Submitted asset refresh request #{request_id}")
            print(f"  Type: {refresh_type.value}")
            if paths:
                print(f"  Paths: {', '.join(paths)}")
            print(f"  Options: {import_options.value}")
            
            return request_id
            
        except sqlite3.Error as e:
            print(f"[ERROR] Failed to submit refresh request: {e}")
            raise
        finally:
            conn.close()
    
    def get_request_status(self, request_id: int) -> Optional[Dict[str, Any]]:
        """Get the status of a specific refresh request"""
        conn = sqlite3.connect(self.db_path)
        conn.row_factory = sqlite3.Row
        cursor = conn.cursor()
        
        try:
            cursor.execute("""
                SELECT * FROM asset_refresh_requests WHERE id = ?
            """, (request_id,))
            
            row = cursor.fetchone()
            if row:
                result = dict(row)
                # Parse paths JSON if present
                if result.get('paths'):
                    result['paths'] = json.loads(result['paths'])
                return result
            return None
            
        finally:
            conn.close()
    
    # Human-readable description of each non-terminal phase
    _PHASE_TEXT = {
        'pending': "Queued - waiting for Unity to pick up the request...",
        'running': "Unity is importing assets...",
        'compiling': "Unity is compiling scripts (domain reload pending)...",
    }

    def wait_for_completion(self, request_id: int, timeout: int = 300) -> str:
        """Wait for a refresh request to fully complete.

        'completed' is only reported once Unity has finished importing assets AND any
        resulting script compilation + domain reload have finished, so it means Unity is
        running the new code. Full recompiles can take minutes - hence the 300s default.
        """
        start_time = time.time()
        last_status = None

        print(f"Waiting for refresh request #{request_id} to complete...")

        while time.time() - start_time < timeout:
            status_data = self.get_request_status(request_id)

            if not status_data:
                print(f"[ERROR] Request #{request_id} not found")
                return "not_found"

            current_status = status_data['status']

            if current_status != last_status:
                elapsed = time.time() - start_time
                phase = self._PHASE_TEXT.get(current_status, "")
                suffix = f" - {phase}" if phase else ""
                print(f"  Status: {current_status} ({elapsed:.1f}s){suffix}")
                last_status = current_status

            if current_status in ['completed', 'failed', 'cancelled']:
                return current_status

            time.sleep(0.5)

        phase = self._PHASE_TEXT.get(last_status, last_status)
        print(f"[WARNING] Timeout after {timeout}s waiting for request #{request_id} "
              f"(last phase: {last_status} - {phase})")
        return "timeout"
    
    def cancel_request(self, request_id: int) -> bool:
        """Cancel a pending or running refresh request"""
        conn = sqlite3.connect(self.db_path)
        cursor = conn.cursor()
        
        try:
            cursor.execute("""
                UPDATE asset_refresh_requests 
                SET status = 'cancelled', completed_at = ?
                WHERE id = ? AND status IN ('pending', 'running')
            """, (datetime.now().isoformat(), request_id))
            
            if cursor.rowcount > 0:
                conn.commit()
                print(f"[SUCCESS] Cancelled refresh request #{request_id}")
                return True
            else:
                print(f"[WARNING] Request #{request_id} not found or already completed")
                return False
                
        except sqlite3.Error as e:
            print(f"[ERROR] Failed to cancel request: {e}")
            return False
        finally:
            conn.close()
    
    def get_pending_requests(self) -> List[Dict[str, Any]]:
        """Get all pending refresh requests"""
        conn = sqlite3.connect(self.db_path)
        conn.row_factory = sqlite3.Row
        cursor = conn.cursor()
        
        try:
            cursor.execute("""
                SELECT * FROM asset_refresh_requests 
                WHERE status = 'pending'
                ORDER BY priority DESC, created_at ASC
            """)
            
            results = []
            for row in cursor.fetchall():
                result = dict(row)
                if result.get('paths'):
                    result['paths'] = json.loads(result['paths'])
                results.append(result)
            
            return results
            
        finally:
            conn.close()
    
    def print_summary(self, request_id: int):
        """Print a summary of the refresh request"""
        status_data = self.get_request_status(request_id)
        
        if not status_data:
            print(f"Request #{request_id} not found")
            return
        
        print(f"\n{'='*60}")
        print(f"Asset Refresh Request #{request_id} Summary")
        print(f"{'='*60}")
        print(f"Type: {status_data['refresh_type']}")
        print(f"Status: {status_data['status']}")
        print(f"Import Options: {status_data['import_options']}")
        
        if status_data.get('paths'):
            print(f"Paths: {', '.join(status_data['paths'])}")
        
        if status_data.get('created_at'):
            print(f"Created: {_format_db_timestamp(status_data['created_at'])}")

        if status_data.get('started_at'):
            print(f"Started: {_format_db_timestamp(status_data['started_at'])}")

        if status_data.get('completed_at'):
            print(f"Completed: {_format_db_timestamp(status_data['completed_at'])}")
        
        if status_data.get('duration_seconds'):
            print(f"Duration: {status_data['duration_seconds']:.2f} seconds")
        
        if status_data.get('result_message'):
            print(f"Result: {status_data['result_message']}")

        if status_data.get('error_message'):
            print(f"Error: {status_data['error_message']}")

        print(f"{'='*60}")

        # A 'completed' refresh with an error_message means compilation produced CS errors.
        # The refresh itself succeeded, but the code will not run until the errors are fixed.
        if status_data.get('status') == 'completed' and status_data.get('error_message'):
            print("[WARNING] Compilation errors detected - run "
                  "monitor_editmode_logs.py --errors before running tests")
        print()

# Convenience functions
def refresh_all_assets(import_options: ImportOptions = ImportOptions.DEFAULT,
                       wait: bool = False,
                       timeout: int = 300) -> int:
    """Quick function to refresh all assets"""
    coordinator = AssetRefreshCoordinator()
    request_id = coordinator.submit_refresh_request(
        RefreshType.FULL,
        import_options=import_options
    )
    
    if wait:
        final_status = coordinator.wait_for_completion(request_id, timeout)
        coordinator.print_summary(request_id)
    
    return request_id

def refresh_specific_paths(paths: List[str],
                          import_options: ImportOptions = ImportOptions.DEFAULT,
                          wait: bool = False,
                          timeout: int = 300) -> int:
    """Quick function to refresh specific paths"""
    coordinator = AssetRefreshCoordinator()
    request_id = coordinator.submit_refresh_request(
        RefreshType.SELECTIVE,
        paths=paths,
        import_options=import_options
    )
    
    if wait:
        final_status = coordinator.wait_for_completion(request_id, timeout)
        coordinator.print_summary(request_id)
    
    return request_id

if __name__ == "__main__":
    # Example usage
    coordinator = AssetRefreshCoordinator()
    
    # Submit a full refresh
    request_id = coordinator.submit_refresh_request(RefreshType.FULL)
    
    # Wait for completion
    status = coordinator.wait_for_completion(request_id)
    
    # Print summary
    coordinator.print_summary(request_id)