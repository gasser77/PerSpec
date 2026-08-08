#!/usr/bin/env python3
"""
Quick Test Runner - Simple interface for common test operations
"""


# Prevent Python from creating .pyc files
import sys
import os
sys.dont_write_bytecode = True
os.environ['PYTHONDONTWRITEBYTECODE'] = '1'
import sys
import argparse
import subprocess
import json
from test_coordinator import TestCoordinator, TestPlatform, TestRequestType

def check_compilation_errors():
    """Check whether the current Unity EditMode session logged compilation errors.

    Reads the newest EditMode session log directly. The previous implementation shelled
    out to a 'quick_logs.py' script that no longer exists, so it always failed silently
    and reported "no errors" - removing the one guard meant to stop a doomed run.

    Only the newest session is inspected: older session files keep errors that have
    already been fixed, and treating those as current would block every run.
    """
    try:
        from monitor_editmode_logs import (
            get_session_files,
            read_session_logs,
            is_compilation_error,
        )
    except ImportError as e:
        print(f"Warning: could not load the log reader, skipping compilation check: {e}")
        return False, None

    try:
        sessions = get_session_files()
        if not sessions:
            # No logs yet (fresh project / Unity never opened) - nothing to judge.
            return False, None

        logs = read_session_logs(
            sessions[0]['path'],
            level_filter=['Error', 'Exception', 'Assert']
        )
        errors = [log for log in logs if is_compilation_error(log.get('message', ''))]

        if not errors:
            return False, None

        first = errors[0].get('message', '').strip().splitlines()[0]
        return True, f"Found {len(errors)} compilation error(s) in the current Unity session.\nFirst: {first}"
    except Exception as e:
        print(f"Warning: Could not check for compilation errors: {e}")
        return False, None

def main():
    # Ensure UTF-8 encoding for emoji/Unicode characters
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')

    parser = argparse.ArgumentParser(description='Quick Unity test runner')
    parser.add_argument('action', choices=['all', 'class', 'method', 'category', 'status', 'cancel', 'stuck'],
                       help='Action to perform')
    parser.add_argument('target', nargs='?', help='Target (class/method/category name or request ID)')
    parser.add_argument('--repair', action='store_true',
                       help='With "stuck": cancel every non-terminal request that is listed')
    parser.add_argument('-p', '--platform', choices=['edit', 'play', 'both'], default='edit',
                       help='Test platform (default: edit)')
    parser.add_argument('--priority', type=int, default=0,
                       help='Priority level (higher runs first)')
    parser.add_argument('--wait', action='store_true',
                       help='Wait until Unity finishes the run and results XML is on disk')
    parser.add_argument('--timeout', type=int, default=300,
                       help='Timeout in seconds (default: 300)')
    parser.add_argument('--focus', action='store_true',
                       help='Focus Unity window before running tests (Windows only)')
    parser.add_argument('--skip-compilation-check', action='store_true',
                       help='Skip checking for compilation errors before running tests')
    
    args = parser.parse_args()
    
    # Map platform strings to enum
    platform_map = {
        'edit': TestPlatform.EDIT_MODE,
        'play': TestPlatform.PLAY_MODE,
        'both': TestPlatform.BOTH
    }
    platform = platform_map[args.platform]
    
    coordinator = TestCoordinator()
    
    try:
        if args.action == 'stuck':
            stuck = coordinator.get_nonterminal_requests()
            if not stuck:
                print("No in-flight or stuck test requests")
            else:
                print(f"In-flight / stuck test requests ({len(stuck)}):")
                for req in stuck:
                    print(f"  #{req['id']}: {req['request_type']} on {req['test_platform']} "
                          f"- status '{req['status']}', {coordinator.describe_age(req['created_at'])}")

                if args.repair:
                    print("\nCancelling the requests listed above...")
                    for req in stuck:
                        coordinator.cancel_request(req['id'])
                else:
                    print("\nRun with --repair to cancel these requests")

        elif args.action == 'status':
            if not args.target:
                # Show all pending requests
                requests = coordinator.get_pending_requests()
                if requests:
                    print("Pending test requests:")
                    for req in requests:
                        print(f"  #{req['id']}: {req['request_type']} on {req['test_platform']} "
                              f"(priority: {req['priority']})")
                else:
                    print("No pending test requests")

                # Pending-only used to hide runs that Unity started but never finished.
                in_flight = [r for r in coordinator.get_nonterminal_requests()
                             if r['status'] != 'pending']
                if in_flight:
                    print("\nIn-flight (not yet terminal):")
                    for req in in_flight:
                        print(f"  #{req['id']}: {req['request_type']} on {req['test_platform']} "
                              f"- status '{req['status']}', {coordinator.describe_age(req['created_at'])}")
            else:
                # Show specific request status
                request_id = int(args.target)
                status = coordinator.get_request_status(request_id)
                if status:
                    coordinator.print_summary(request_id)
                else:
                    print(f"Request {request_id} not found")
        
        elif args.action == 'cancel':
            if not args.target:
                print("Error: Request ID required for cancel")
                sys.exit(1)
            request_id = int(args.target)
            coordinator.cancel_request(request_id)
        
        else:
            # Submit test request
            request_type_map = {
                'all': TestRequestType.ALL,
                'class': TestRequestType.CLASS,
                'method': TestRequestType.METHOD,
                'category': TestRequestType.CATEGORY
            }
            request_type = request_type_map[args.action]
            
            # For 'all' tests, target is optional
            test_filter = args.target if args.action != 'all' else None
            
            if args.action != 'all' and not test_filter:
                print(f"Error: {args.action} requires a target")
                sys.exit(1)
            
            # Check for compilation errors unless explicitly skipped
            if not args.skip_compilation_check:
                print("Checking for compilation errors...")
                has_errors, error_msg = check_compilation_errors()
                
                if has_errors:
                    print("\n" + "="*60)
                    print("WARNING: COMPILATION ERRORS DETECTED!")
                    print("="*60)
                    print(f"\n{error_msg}")
                    print("\nTests cannot run with compilation errors.")
                    print("Tests will be marked as INCONCLUSIVE.")
                    print("\nTo fix:")
                    print("1. Run: python PerSpec/Coordination/Scripts/monitor_editmode_logs.py --errors")
                    print("2. Fix the compilation errors")
                    print("3. Refresh Unity again")
                    print("4. Check for errors again")
                    print("5. Then run tests")
                    print("\nTo skip this check (not recommended):")
                    print("  Add --skip-compilation-check flag")
                    print("="*60)
                    sys.exit(2)  # Exit code 2 for compilation errors
                else:
                    print("[OK] No compilation errors found")
            
            # Focus Unity BEFORE submitting request for immediate processing
            if args.focus:
                try:
                    import unity_focus
                    print("Focusing Unity window...")
                    if unity_focus.focus_unity():
                        print("Unity window focused")
                    else:
                        print("Could not focus Unity window")
                except ImportError:
                    print("Warning: unity_focus module not found")
                except Exception as e:
                    print(f"Could not focus Unity: {e}")
            
            # Unity cannot run EditMode and PlayMode tests in a single test run, so
            # 'both' is submitted as two separate requests rather than one request
            # Unity would have to reject.
            if args.platform == 'both':
                platforms = [TestPlatform.EDIT_MODE, TestPlatform.PLAY_MODE]
            else:
                platforms = [platform]

            request_ids = []

            for index, target_platform in enumerate(platforms):
                if len(platforms) > 1:
                    print(f"\n--- {target_platform.value} ---")

                request_id = coordinator.submit_test_request(
                    request_type,
                    target_platform,
                    test_filter,
                    args.priority
                )
                request_ids.append(request_id)

                if args.wait:
                    print(f"Waiting for completion (timeout: {args.timeout}s)...")
                    coordinator.wait_for_completion(request_id, args.timeout)
                    coordinator.print_summary(request_id)
                elif len(platforms) > 1 and index == 0:
                    # Without --wait both requests are queued at once; Unity's single
                    # dispatch funnel still runs them one after the other.
                    continue

            if not args.wait:
                for request_id in request_ids:
                    print(f"Use 'python quick_test.py status {request_id}' to check progress")
    
    except KeyboardInterrupt:
        print("\nOperation cancelled")
        sys.exit(1)
    except Exception as e:
        print(f"Error: {e}")
        sys.exit(1)

if __name__ == "__main__":
    main()