#!/usr/bin/env python3
"""
Unity Test Coordinator - Python Interface
Provides functions to submit test requests and monitor results
"""


# Prevent Python from creating .pyc files
import sys
import os
sys.dont_write_bytecode = True
os.environ['PYTHONDONTWRITEBYTECODE'] = '1'
import sqlite3
import json
import time
from datetime import datetime
from pathlib import Path
from typing import Optional, Dict, List, Tuple
from enum import Enum

class TestPlatform(Enum):
    EDIT_MODE = "EditMode"
    PLAY_MODE = "PlayMode"
    BOTH = "Both"

class TestRequestType(Enum):
    ALL = "all"
    CLASS = "class"
    METHOD = "method"
    CATEGORY = "category"

class TestStatus(Enum):
    PENDING = "pending"
    RUNNING = "running"
    COMPLETED = "completed"
    FAILED = "failed"
    CANCELLED = "cancelled"

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
    `created_at` as an INT64 tick count of local time since 0001-01-01.
    If Python INSERTs a TEXT timestamp instead, a later sqlite-net
    `_connection.Update(entity)` reads the column, SQLite coerces the TEXT
    via NUMERIC affinity to its leading integer prefix (e.g. "2026-05-13 01:25:38"
    becomes 2026), and writes back 2026 as INT64. Subsequent maintenance
    `DELETE WHERE created_at < ?` then matches and deletes the fresh request.

    Writing the timestamp as INT64 ticks up front avoids the coercion entirely.
    """
    delta = datetime.now() - _DOTNET_EPOCH
    # DateTime.Ticks is 100-nanosecond intervals; preserve microsecond precision.
    return delta.days * 864_000_000_000 + delta.seconds * 10_000_000 + delta.microseconds * 10

class TestCoordinator:
    def __init__(self):
        self.db_path = Path(get_db_path())
        self._ensure_database_exists()
    
    def _ensure_database_exists(self):
        """Ensure the database exists"""
        if not self.db_path.exists():
            raise FileNotFoundError(f"Database not found at {self.db_path}. Run db_initializer.py first.")
    
    def _get_connection(self) -> sqlite3.Connection:
        """Get a database connection with proper settings"""
        conn = sqlite3.connect(str(self.db_path))
        conn.row_factory = sqlite3.Row  # Enable column access by name
        conn.execute("PRAGMA journal_mode=WAL")
        return conn
    
    def submit_test_request(self, 
                           request_type: TestRequestType,
                           platform: TestPlatform,
                           test_filter: Optional[str] = None,
                           priority: int = 0) -> int:
        """
        Submit a new test request to the queue
        
        Args:
            request_type: Type of test to run (all, class, method, category)
            platform: Test platform (EditMode, PlayMode, Both)
            test_filter: Optional filter string (class name, method name, or category)
            priority: Priority level (higher numbers run first)
            
        Returns:
            Request ID of the submitted test
        """
        conn = self._get_connection()
        try:
            cursor = conn.cursor()
            # Write created_at as .NET INT64 ticks so sqlite-net round-trips it
            # safely. See _dotnet_ticks_now() docstring for the SQLite type-affinity
            # corruption this avoids.
            now_ticks = _dotnet_ticks_now()
            cursor.execute("""
                INSERT INTO test_requests (request_type, test_filter, test_platform, priority, created_at)
                VALUES (?, ?, ?, ?, ?)
            """, (request_type.value, test_filter, platform.value, priority, now_ticks))

            request_id = cursor.lastrowid

            # Log the submission (let SQLite default created_at; execution_log is
            # not consulted by the cleanup queries that triggered the corruption).
            cursor.execute("""
                INSERT INTO execution_log (request_id, log_level, source, message)
                VALUES (?, 'INFO', 'Python', ?)
            """, (request_id, f"Test request submitted: {request_type.value} on {platform.value}"))
            
            conn.commit()
            
            print(f"[SUCCESS] Test request submitted with ID: {request_id}")
            print(f"   Type: {request_type.value}")
            print(f"   Platform: {platform.value}")
            if test_filter:
                print(f"   Filter: {test_filter}")
            
            return request_id
            
        except sqlite3.Error as e:
            print(f"[ERROR] Error submitting test request: {e}")
            conn.rollback()
            raise
        finally:
            conn.close()
    
    def get_request_status(self, request_id: int) -> Optional[Dict]:
        """
        Get the current status of a test request
        
        Args:
            request_id: ID of the test request
            
        Returns:
            Dictionary with request details or None if not found
        """
        conn = self._get_connection()
        try:
            cursor = conn.cursor()
            cursor.execute("""
                SELECT * FROM test_requests WHERE id = ?
            """, (request_id,))
            
            row = cursor.fetchone()
            if row:
                return dict(row)
            return None
            
        finally:
            conn.close()
    
    def wait_for_completion(self, request_id: int, timeout: int = 300, poll_interval: float = 1.0,
                            xml_grace_seconds: float = 15.0,
                            missing_row_retries: int = 10) -> Dict:
        """
        Wait for a test request to fully complete.

        A request is considered fully complete when BOTH:
          1. The DB row has a terminal status ('completed', 'failed', 'cancelled',
             'timeout', 'inconclusive'), AND
          2. A matching results XML file is present in PerSpec/TestResults/. If
             only Unity's AppData copy exists, this method imports it into
             PerSpec/TestResults/ before returning.

        Transient invisibility of the row (e.g. mid-VACUUM or concurrent cleanup)
        is tolerated up to ``missing_row_retries`` consecutive polls.

        Args:
            request_id: ID of the test request
            timeout: Maximum seconds to wait for the run to complete
            poll_interval: Seconds between status checks
            xml_grace_seconds: After terminal status, seconds to wait for XML
            missing_row_retries: Consecutive "not found" reads tolerated

        Returns:
            Final status dictionary
        """
        terminal_statuses = {'completed', 'failed', 'cancelled', 'timeout', 'inconclusive'}
        start_time = time.time()
        last_status = None
        consecutive_misses = 0
        request_created_at = None
        pending_hint_shown = False
        pending_hint_after = 15.0

        while time.time() - start_time < timeout:
            status = self.get_request_status(request_id)

            if not status:
                consecutive_misses += 1
                if consecutive_misses == 1 or consecutive_misses % 3 == 0:
                    print(f"[WARN] Request {request_id} momentarily not found "
                          f"(retry {consecutive_misses}/{missing_row_retries})")
                if consecutive_misses >= missing_row_retries:
                    raise ValueError(
                        f"Request {request_id} not found after "
                        f"{missing_row_retries} consecutive polls"
                    )
                time.sleep(poll_interval)
                continue

            consecutive_misses = 0
            if request_created_at is None:
                request_created_at = status.get('created_at')

            if status['status'] != last_status:
                print(f"[STATUS] {status['status']}")
                last_status = status['status']

            # A request that Unity never picks up stays 'pending' in silence until the
            # timeout expires. Say something actionable instead of nothing.
            if (not pending_hint_shown
                    and status['status'] == 'pending'
                    and time.time() - start_time > pending_hint_after):
                pending_hint_shown = True
                print(f"[HINT] Unity has not picked up request {request_id} after "
                      f"{pending_hint_after:.0f}s. The editor may be unfocused, compiling, "
                      f"or PerSpec coordination may be disabled.")
                print("[HINT] Check the Unity console for '[TestCoordinator]' lines, "
                      "or run: quick_test.py stuck")

            if status['status'] in terminal_statuses:
                self._await_results_xml(request_id, request_created_at, xml_grace_seconds)
                return status

            time.sleep(poll_interval)

        raise TimeoutError(f"Request {request_id} did not complete within {timeout} seconds")

    def _await_results_xml(self, request_id: int, request_created_at,
                           xml_grace_seconds: float):
        """After terminal status, ensure a fresh results XML exists in PerSpec/TestResults.

        Polls PerSpec/TestResults for an XML newer than the request's creation
        time. If none appears within ``xml_grace_seconds``, attempts a one-shot
        copy from Unity's AppData fallback locations.
        """
        try:
            from datetime import datetime, timedelta
            import shutil

            project_root = Path(get_project_root())
            results_dir = project_root / "PerSpec" / "TestResults"
            results_dir.mkdir(parents=True, exist_ok=True)

            cutoff = None
            if request_created_at is not None:
                cutoff = self._parse_request_timestamp(request_created_at)
                if cutoff is not None:
                    cutoff -= timedelta(seconds=5)

            def _matching_xml():
                xmls = sorted(
                    results_dir.glob("TestResults_*.xml"),
                    key=lambda p: p.stat().st_mtime,
                    reverse=True,
                )
                for xml in xmls:
                    if cutoff is None:
                        return xml
                    if datetime.fromtimestamp(xml.stat().st_mtime) >= cutoff:
                        return xml
                return None

            deadline = time.time() + xml_grace_seconds
            while time.time() < deadline:
                if _matching_xml():
                    return
                time.sleep(1.0)

            # Fallback: copy from Unity's AppData fallback locations.
            appdata_low = self._appdata_low_path()
            if not appdata_low:
                print("[WARN] No fresh XML in PerSpec/TestResults; AppData fallback unavailable on this platform")
                return

            for candidate in self._appdata_unity_dirs(appdata_low):
                source = candidate / "TestResults.xml"
                if not source.exists():
                    continue
                src_mtime = datetime.fromtimestamp(source.stat().st_mtime)
                if cutoff is not None and src_mtime < cutoff:
                    continue
                dest = results_dir / f"TestResults_{src_mtime.strftime('%Y%m%d_%H%M%S')}.xml"
                try:
                    shutil.copy2(str(source), str(dest))
                    print(f"[INFO] Imported results XML from Unity AppData: {source} -> {dest}")
                    return
                except OSError as e:
                    print(f"[WARN] Failed to copy {source}: {e}")

            print(f"[WARN] Request {request_id} reached terminal status but no results XML was found")
        except Exception as e:
            print(f"[WARN] Error while waiting for results XML: {e}")

    @staticmethod
    def _parse_request_timestamp(value):
        """Parse a created_at value that may be .NET ticks (int) or ISO text."""
        from datetime import datetime as _dt, timedelta as _td
        if isinstance(value, (int, float)):
            # .NET DateTime.Ticks - 100-ns intervals since 0001-01-01.
            try:
                return _DOTNET_EPOCH + _td(microseconds=int(value) // 10)
            except (OverflowError, ValueError):
                return None
        try:
            text = str(value).replace('Z', '')
            return _dt.fromisoformat(text)
        except (TypeError, ValueError):
            return None

    @staticmethod
    def _appdata_low_path() -> Optional[Path]:
        """Return %LocalAppData%Low on Windows, else None."""
        local_app = os.environ.get("LOCALAPPDATA")
        if not local_app:
            return None
        low = Path(local_app + "Low")
        return low if low.exists() else None

    @staticmethod
    def _appdata_unity_dirs(appdata_low: Path) -> List[Path]:
        """Mirror C# TestExecutor.cs:602-616 AppData fallback enumeration.

        We don't know Unity's CompanyName/ProductName from Python, so we walk
        appdata_low/* and accept any directory containing TestResults.xml.
        """
        candidates = []
        try:
            project_root = Path(get_project_root())
            project_name = project_root.name
            preferred_product_names = {project_name, "TestFramework"}

            for company_dir in appdata_low.iterdir():
                if not company_dir.is_dir():
                    continue
                for product_dir in company_dir.iterdir():
                    if not product_dir.is_dir():
                        continue
                    if (product_dir / "TestResults.xml").exists():
                        score = 0
                        if product_dir.name in preferred_product_names:
                            score += 2
                        if company_dir.name == "DefaultCompany":
                            score += 1
                        candidates.append((score, product_dir))
        except OSError:
            return []
        candidates.sort(key=lambda t: t[0], reverse=True)
        return [c for _, c in candidates]
    
    def get_test_results(self, request_id: int) -> List[Dict]:
        """
        Get detailed test results for a request
        
        Args:
            request_id: ID of the test request
            
        Returns:
            List of test result dictionaries
        """
        conn = self._get_connection()
        try:
            cursor = conn.cursor()
            cursor.execute("""
                SELECT * FROM test_results 
                WHERE request_id = ?
                ORDER BY test_name
            """, (request_id,))
            
            return [dict(row) for row in cursor.fetchall()]
            
        finally:
            conn.close()
    
    def cancel_request(self, request_id: int) -> bool:
        """
        Cancel a test request that has not reached a terminal state.

        Covers every non-terminal status, not just pending/running: a request wedged
        at 'processing', 'executing' or 'finalizing' is exactly the one a user needs
        to clear, and it used to be uncancellable from Python.

        Args:
            request_id: ID of the test request

        Returns:
            True if cancelled successfully
        """
        conn = self._get_connection()
        try:
            cursor = conn.cursor()

            cursor.execute("SELECT status FROM test_requests WHERE id = ?", (request_id,))
            row = cursor.fetchone()
            if row is None:
                print(f"[WARNING] Request {request_id} not found")
                return False

            previous_status = row['status']

            cursor.execute("""
                UPDATE test_requests
                SET status = 'cancelled',
                    completed_at = CURRENT_TIMESTAMP,
                    error_message = 'Cancelled by user'
                WHERE id = ? AND status IN (
                    'pending', 'processing', 'executing', 'running', 'finalizing'
                )
            """, (request_id,))

            if cursor.rowcount > 0:
                conn.commit()
                print(f"[CANCELLED] Request {request_id} cancelled (was '{previous_status}')")
                return True
            else:
                print(f"[WARNING] Request {request_id} is already terminal ('{previous_status}')")
                return False

        except sqlite3.Error as e:
            print(f"[ERROR] Error cancelling request: {e}")
            conn.rollback()
            return False
        finally:
            conn.close()

    def get_pending_requests(self) -> List[Dict]:
        """Get all pending test requests"""
        conn = self._get_connection()
        try:
            cursor = conn.cursor()
            cursor.execute("""
                SELECT * FROM test_requests
                WHERE status = 'pending'
                ORDER BY priority DESC, created_at ASC
            """)

            return [dict(row) for row in cursor.fetchall()]

        finally:
            conn.close()

    def get_nonterminal_requests(self) -> List[Dict]:
        """Get every request that has not reached a terminal state.

        Unlike get_pending_requests this includes in-flight statuses, so a run that
        Unity picked up but never finished is actually visible.
        """
        conn = self._get_connection()
        try:
            cursor = conn.cursor()
            cursor.execute("""
                SELECT * FROM test_requests
                WHERE status NOT IN (
                    'completed', 'failed', 'cancelled', 'timeout', 'inconclusive'
                )
                ORDER BY created_at ASC
            """)

            return [dict(row) for row in cursor.fetchall()]

        finally:
            conn.close()

    def describe_age(self, created_at) -> str:
        """Human-readable age of a request, tolerant of tick and text timestamps."""
        parsed = self._parse_request_timestamp(created_at)
        if parsed is None:
            return "unknown age"

        seconds = max(0.0, (datetime.now() - parsed).total_seconds())
        if seconds < 60:
            return f"{seconds:.0f}s old"
        if seconds < 3600:
            return f"{seconds / 60:.1f}m old"
        return f"{seconds / 3600:.1f}h old"
    
    def get_execution_log(self, request_id: Optional[int] = None, limit: int = 100) -> List[Dict]:
        """
        Get execution log entries
        
        Args:
            request_id: Optional request ID to filter by
            limit: Maximum number of entries to return
            
        Returns:
            List of log entry dictionaries
        """
        conn = self._get_connection()
        try:
            cursor = conn.cursor()
            
            if request_id:
                cursor.execute("""
                    SELECT * FROM execution_log 
                    WHERE request_id = ?
                    ORDER BY created_at DESC
                    LIMIT ?
                """, (request_id, limit))
            else:
                cursor.execute("""
                    SELECT * FROM execution_log 
                    ORDER BY created_at DESC
                    LIMIT ?
                """, (limit,))
            
            return [dict(row) for row in cursor.fetchall()]
            
        finally:
            conn.close()
    
    def update_system_heartbeat(self, component: str = "Python"):
        """Update system heartbeat for monitoring"""
        conn = self._get_connection()
        try:
            cursor = conn.cursor()
            cursor.execute("""
                INSERT INTO system_status (component, status, last_heartbeat, message)
                VALUES (?, 'online', CURRENT_TIMESTAMP, 'Active')
                ON CONFLICT(component) DO UPDATE SET
                    status = 'online',
                    last_heartbeat = CURRENT_TIMESTAMP,
                    message = 'Active'
            """)
            conn.commit()
        except sqlite3.Error:
            # Ignore errors for heartbeat
            pass
        finally:
            conn.close()
    
    def print_summary(self, request_id: int):
        """Print a nice summary of test results"""
        status = self.get_request_status(request_id)
        if not status:
            print(f"[ERROR] Request {request_id} not found")
            return
        
        print("\n" + "="*60)
        print(f"Test Request #{request_id} Summary")
        print("="*60)
        print(f"Status: {status['status']}")
        print(f"Platform: {status['test_platform']}")
        print(f"Type: {status['request_type']}")
        
        if status['test_filter']:
            print(f"Filter: {status['test_filter']}")
        
        if status['status'] == 'completed':
            print(f"\nResults:")
            print(f"  Total: {status['total_tests']}")
            print(f"  Passed: {status['passed_tests']}")
            print(f"  Failed: {status['failed_tests']}")
            print(f"  Skipped: {status['skipped_tests']}")
            print(f"  Duration: {status['duration_seconds']:.2f} seconds")
            
            # Show failed tests if any
            if status['failed_tests'] > 0:
                results = self.get_test_results(request_id)
                failed = [r for r in results if r['result'] == 'Failed']
                if failed:
                    print("\nFailed Tests:")
                    for test in failed:
                        print(f"  [FAILED] {test['test_name']}")
                        if test['error_message']:
                            print(f"     {test['error_message']}")
        
        elif status['status'] == 'failed':
            print(f"\n[ERROR] {status['error_message']}")
        
        print("="*60 + "\n")


# Convenience functions for quick operations
def run_all_tests(platform: TestPlatform = TestPlatform.BOTH) -> int:
    """Run all tests on specified platform"""
    coordinator = TestCoordinator()
    return coordinator.submit_test_request(TestRequestType.ALL, platform)

def run_test_class(class_name: str, platform: TestPlatform = TestPlatform.EDIT_MODE) -> int:
    """Run tests for a specific class"""
    coordinator = TestCoordinator()
    return coordinator.submit_test_request(TestRequestType.CLASS, platform, class_name)

def run_test_method(method_name: str, platform: TestPlatform = TestPlatform.EDIT_MODE) -> int:
    """Run a specific test method"""
    coordinator = TestCoordinator()
    return coordinator.submit_test_request(TestRequestType.METHOD, platform, method_name)

def run_test_category(category: str, platform: TestPlatform = TestPlatform.BOTH) -> int:
    """Run tests by category"""
    coordinator = TestCoordinator()
    return coordinator.submit_test_request(TestRequestType.CATEGORY, platform, category)

if __name__ == "__main__":
    # Example usage
    coordinator = TestCoordinator()
    
    # Submit a test request
    request_id = coordinator.submit_test_request(
        TestRequestType.ALL,
        TestPlatform.EDIT_MODE,
        priority=1
    )
    
    # Wait for completion (with timeout)
    try:
        final_status = coordinator.wait_for_completion(request_id, timeout=60)
        coordinator.print_summary(request_id)
    except TimeoutError as e:
        print(f"[TIMEOUT] {e}")
        coordinator.cancel_request(request_id)