#!/usr/bin/env python3
"""
Monitor EditMode logs from session files.
Reads directly from PerSpec/EditModeLogs/ session files.
"""

import os
import sys
import argparse
import io

# Set stdout to handle Unicode properly on Windows
if sys.platform == "win32":
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')
    sys.stderr = io.TextIOWrapper(sys.stderr.buffer, encoding='utf-8', errors='replace')
from datetime import datetime, timedelta
from pathlib import Path
import time
import re

def get_perspec_root():
    """Get the PerSpec root directory."""
    script_dir = Path(__file__).parent
    return script_dir.parent.parent  # Go up to PerSpec root

def parse_log_line(line):
    """Parse a log line and return structured data."""
    if not line.strip() or line.startswith('='):
        return None
        
    # Format: [HH:mm:ss.fff] [Level    ] Message
    if line.startswith('[') and '] [' in line:
        try:
            parts = line.split('] [', 1)
            if len(parts) >= 2:
                timestamp = parts[0].lstrip('[')
                remaining = parts[1]
                
                level_end = remaining.find('] ')
                if level_end > 0:
                    level = remaining[:level_end].strip()
                    message = remaining[level_end + 2:]
                    
                    return {
                        'timestamp': timestamp,
                        'level': level,
                        'message': message
                    }
        except:
            pass
    
    # Could be a stack trace line (indented)
    if line.startswith('    '):
        return {'continuation': line.strip()}
    
    return None

def is_compilation_error(message):
    """Check if a log message is a compilation error (CS/BC/DC/ECS error)."""
    # CS error codes: CS0001-CS9999 (C# compiler)
    cs_pattern = r'\bCS\d{4}\b'

    # BC error codes: BC0001-BC9999 (Burst Compiler)
    bc_pattern = r'\bBC\d{4}\b'

    # DC error codes: DC0001-DC9999 (Domain Compilation)
    dc_pattern = r'\bDC\d{4}\b'

    # Additional compilation-specific patterns
    compilation_patterns = [
        # Original CS patterns
        r'error CS\d{4}',
        r': error CS\d{4}',
        r'Compiler Error',
        r'compilation failed',
        r'All compiler errors',

        # Burst Compiler patterns
        r'error BC\d{4}',
        r': error BC\d{4}',
        r'Burst error',
        r'Burst compiler.*failed',
        r'burst\.initialize',
        r'BuildFailedException.*Burst',
        r'Internal compiler error.*Burst',

        # Domain Compilation patterns
        r'error DC\d{4}',
        r': error DC\d{4}',
        r'Domain.*compilation',
        r'Domain reload.*failed',

        # DOTS/ECS Source Generator patterns
        r'SGICE\d{3}',  # Source Generator Internal Compiler Error
        r'DOTS source generators',
        r'Source generator.*error',
        r'Entities\.ForEach.*error',
        r'partial keyword.*system',

        # ECS/DOTS specific compilation errors
        r'NativeArray.*disposed',
        r'JobHandle.*not.*completed',
        r'ComponentSystem.*error',
        r'SystemBase.*error',
        r'ISystem.*error',
        r'EntityCommandBuffer.*error',
        r'SharedStatic.*unmanaged',
        r'IJobParallelFor.*error'
    ]

    # Check for error codes
    if re.search(cs_pattern, message):
        return True
    if re.search(bc_pattern, message):
        return True
    if re.search(dc_pattern, message):
        return True

    # Check for compilation patterns
    for pattern in compilation_patterns:
        if re.search(pattern, message, re.IGNORECASE):
            return True

    return False

def get_error_type(message):
    """Determine the type of compilation error from the message."""
    # Check for specific error code patterns
    if re.search(r'\bCS\d{4}\b', message):
        return 'CS'  # C# Compiler
    elif re.search(r'\bBC\d{4}\b', message):
        return 'BC'  # Burst Compiler
    elif re.search(r'\bDC\d{4}\b', message):
        return 'DC'  # Domain Compilation
    elif re.search(r'SGICE\d{3}', message, re.IGNORECASE):
        return 'SG'  # Source Generator
    elif re.search(r'(Burst error|Burst compiler|burst\.)', message, re.IGNORECASE):
        return 'Burst'
    elif re.search(r'(Entities\.ForEach|EntityCommandBuffer|ComponentSystem|SystemBase|ISystem)', message, re.IGNORECASE):
        return 'ECS'
    elif re.search(r'(NativeArray|JobHandle|IJobParallelFor|SharedStatic)', message, re.IGNORECASE):
        return 'Jobs'
    elif re.search(r'Domain.*compilation|Domain reload', message, re.IGNORECASE):
        return 'Domain'
    else:
        return 'Compile'  # Generic compilation error

def is_ecs_error(message):
    """Check if a log message is specifically an ECS/DOTS/Burst error."""
    # BC error codes
    if re.search(r'\bBC\d{4}\b', message):
        return True

    # DC error codes
    if re.search(r'\bDC\d{4}\b', message):
        return True

    # ECS/DOTS/Burst specific patterns
    ecs_patterns = [
        r'Burst error',
        r'Burst compiler',
        r'burst\.initialize',
        r'SGICE\d{3}',
        r'DOTS source generators',
        r'Entities\.ForEach',
        r'EntityCommandBuffer',
        r'ComponentSystem',
        r'SystemBase',
        r'ISystem',
        r'NativeArray',
        r'JobHandle',
        r'IJobParallelFor',
        r'SharedStatic',
        r'Domain.*compilation.*ECS',
        r'partial keyword.*system'
    ]

    for pattern in ecs_patterns:
        if re.search(pattern, message, re.IGNORECASE):
            return True

    return False

def get_session_files():
    """Get all session files sorted by modification time."""
    logs_dir = get_perspec_root() / 'EditModeLogs'
    
    if not logs_dir.exists():
        return []
    
    session_files = []
    for file_path in logs_dir.glob('session_*.txt'):
        session_files.append({
            'path': file_path,
            'session_id': file_path.stem.replace('session_', ''),
            'modified': file_path.stat().st_mtime,
            'size': file_path.stat().st_size
        })
    
    return sorted(session_files, key=lambda x: x['modified'], reverse=True)

def read_session_logs(file_path, tail_lines=None, level_filter=None):
    """Read logs from a session file."""
    logs = []
    current_log = None
    
    try:
        with open(file_path, 'r', encoding='utf-8', errors='ignore') as f:
            lines = f.readlines()
            
            # If tailing, get only last N lines
            if tail_lines:
                # Find actual log lines (not header or continuations)
                log_indices = []
                for i, line in enumerate(lines):
                    parsed = parse_log_line(line)
                    if parsed and 'timestamp' in parsed:
                        log_indices.append(i)
                
                if log_indices and len(log_indices) > tail_lines:
                    start_index = log_indices[-tail_lines]
                    lines = lines[start_index:]
            
            for line in lines:
                parsed = parse_log_line(line)
                
                if parsed:
                    if 'timestamp' in parsed:
                        # New log entry
                        if current_log:
                            logs.append(current_log)
                        current_log = parsed
                        current_log['stack_trace'] = []
                    elif 'continuation' in parsed and current_log:
                        # Stack trace line
                        current_log['stack_trace'].append(parsed['continuation'])
            
            # Don't forget the last log
            if current_log:
                logs.append(current_log)
                
    except Exception as e:
        print(f"Error reading {file_path}: {e}")
    
    # Apply level filter
    if level_filter:
        level_filter_lower = [l.lower() for l in level_filter]
        logs = [log for log in logs if log.get('level', '').lower() in level_filter_lower]
    
    return logs

def display_logs(logs, show_stack=False, show_error_type=False):
    """Display logs with color coding and optional error type."""
    level_colors = {
        'error': '\033[91m',      # Red
        'exception': '\033[91m',  # Red
        'warning': '\033[93m',    # Yellow
        'info': '\033[92m',       # Green
        'debug': '\033[94m'       # Blue
    }
    reset_color = '\033[0m'

    for log in logs:
        level = log.get('level', '').lower()
        color = level_colors.get(level, '')
        message = log['message']

        # Add error type prefix if it's a compilation error
        if show_error_type and level in ['error', 'exception']:
            if is_compilation_error(message):
                error_type = get_error_type(message)
                print(f"{color}[{log['timestamp']}] [{error_type:7}] [{log['level']:9}] {message}{reset_color}")
            else:
                print(f"{color}[{log['timestamp']}] [{log['level']:9}] {message}{reset_color}")
        else:
            print(f"{color}[{log['timestamp']}] [{log['level']:9}] {message}{reset_color}")

        if show_stack and log.get('stack_trace'):
            for line in log['stack_trace']:
                print(f"    {line}")

def monitor_live(level_filter=None, refresh_rate=1.0):
    """Monitor logs in real-time."""
    print("Monitoring EditMode logs (Ctrl+C to stop)...")
    print("="*60)
    
    last_size = {}
    
    try:
        while True:
            session_files = get_session_files()
            if not session_files:
                time.sleep(refresh_rate)
                continue
            
            # Monitor the most recent session
            current_session = session_files[0]
            file_path = current_session['path']
            
            # Check if file has grown
            current_size = current_session['size']
            session_id = current_session['session_id']
            
            if session_id not in last_size:
                # First time seeing this session
                print(f"\nMonitoring session: {session_id}")
                print(f"File: {file_path}")
                print("="*60)
                last_size[session_id] = 0
            
            if current_size > last_size.get(session_id, 0):
                # Read only new content
                with open(file_path, 'r', encoding='utf-8', errors='ignore') as f:
                    f.seek(last_size.get(session_id, 0))
                    new_content = f.read()
                    
                    # Parse and display new logs
                    for line in new_content.splitlines():
                        parsed = parse_log_line(line)
                        if parsed and 'timestamp' in parsed:
                            if not level_filter or parsed['level'].lower() in [l.lower() for l in level_filter]:
                                level = parsed.get('level', '').lower()
                                color = {
                                    'error': '\033[91m',
                                    'exception': '\033[91m',
                                    'warning': '\033[93m',
                                    'info': '\033[92m',
                                    'debug': '\033[94m'
                                }.get(level, '')
                                reset = '\033[0m'
                                print(f"{color}[{parsed['timestamp']}] [{parsed['level']:9}] {parsed['message']}{reset}")
                
                last_size[session_id] = current_size
            
            time.sleep(refresh_rate)
            
    except KeyboardInterrupt:
        print("\nStopped monitoring.")

def main():
    parser = argparse.ArgumentParser(description='Monitor EditMode session logs')
    
    # Add top-level --errors flag for consistency with PlayMode logs
    parser.add_argument('--errors', action='store_true', help='Show only compilation errors (CS/BC/DC/ECS errors) from all sessions')
    parser.add_argument('--all-errors', action='store_true', help='Show all errors and exceptions from all sessions')
    parser.add_argument('--ecs-errors', action='store_true', help='Show only ECS/DOTS/Burst compilation errors from all sessions')
    parser.add_argument('-s', '--stack', action='store_true', help='Show stack traces')
    parser.add_argument('-n', '--lines', type=int, help='Number of lines to show')
    parser.add_argument('--no-limit', action='store_true', help='Bypass default line limits (useful with grep)')
    
    subparsers = parser.add_subparsers(dest='command', help='Commands')
    
    # Recent logs command
    recent_parser = subparsers.add_parser('recent', help='Show recent logs')
    recent_parser.add_argument('-n', '--lines', type=int, default=50, help='Number of lines to show')
    recent_parser.add_argument('--level', nargs='+', choices=['Info', 'Warning', 'Error', 'Exception', 'Debug'],
                              help='Filter by log level')
    recent_parser.add_argument('-s', '--stack', action='store_true', help='Show stack traces')
    
    # Live monitoring command
    live_parser = subparsers.add_parser('live', help='Monitor logs in real-time')
    live_parser.add_argument('-r', '--refresh', type=float, default=1.0, help='Refresh rate in seconds')
    live_parser.add_argument('--level', nargs='+', choices=['Info', 'Warning', 'Error', 'Exception', 'Debug'],
                            help='Filter by log level')
    
    # List sessions command
    list_parser = subparsers.add_parser('sessions', help='List all sessions')
    
    # Show specific session
    show_parser = subparsers.add_parser('show', help='Show logs from specific session')
    show_parser.add_argument('session_id', help='Session ID to show')
    show_parser.add_argument('-n', '--lines', type=int, help='Number of lines to show')
    show_parser.add_argument('--level', nargs='+', choices=['Info', 'Warning', 'Error', 'Exception', 'Debug'],
                            help='Filter by log level')
    show_parser.add_argument('-s', '--stack', action='store_true', help='Show stack traces')
    
    # Check errors command
    errors_parser = subparsers.add_parser('errors', help='Show only errors and exceptions')
    errors_parser.add_argument('-n', '--lines', type=int, default=20, help='Number of errors to show')
    errors_parser.add_argument('-s', '--stack', action='store_true', help='Show stack traces')
    
    args = parser.parse_args()
    
    # Handle --no-limit flag
    if args.no_limit:
        # Remove line limits when --no-limit is specified
        if hasattr(args, 'lines'):
            args.lines = None
    
    # Handle error flags
    if args.errors:
        # Unity does not recompile mid-play, so there is nothing new to check.
        try:
            from test_coordinator import unity_in_play_mode
            if unity_in_play_mode():
                print("[SKIPPED] Unity is in Play Mode - compile error check skipped")
                sys.exit(0)
        except ImportError:
            pass
        args.command = 'errors'
        args.compilation_only = True
        args.ecs_only = False
        if not hasattr(args, 'lines'):
            args.lines = None  # Show all errors
        if not hasattr(args, 'stack'):
            args.stack = False
    elif args.all_errors:
        args.command = 'errors'
        args.compilation_only = False
        args.ecs_only = False
        if not hasattr(args, 'lines'):
            args.lines = None  # Show all errors
        if not hasattr(args, 'stack'):
            args.stack = False
    elif args.ecs_errors:
        args.command = 'errors'
        args.compilation_only = False  # We'll filter differently
        args.ecs_only = True
        if not hasattr(args, 'lines'):
            args.lines = None  # Show all errors
        if not hasattr(args, 'stack'):
            args.stack = False
    else:
        args.compilation_only = True  # Default to compilation errors only
        args.ecs_only = False
    
    if not args.command:
        args.command = 'recent'
        args.lines = 50
        args.level = None
        args.stack = False
    
    logs_dir = get_perspec_root() / 'EditModeLogs'
    
    if args.command == 'sessions':
        # List all sessions
        session_files = get_session_files()
        
        if not session_files:
            print("No EditMode sessions found.")
            print(f"Logs directory: {logs_dir}")
            return
        
        print(f"\n=== EditMode Sessions ===")
        print(f"Directory: {logs_dir}\n")
        
        for session in session_files:
            mod_time = datetime.fromtimestamp(session['modified'])
            size_kb = session['size'] / 1024
            print(f"Session: {session['session_id']}")
            print(f"  Modified: {mod_time.strftime('%Y-%m-%d %H:%M:%S')}")
            print(f"  Size: {size_kb:.1f} KB")
            print(f"  File: {session['path'].name}")
            print()
    
    elif args.command == 'live':
        # Live monitoring
        monitor_live(level_filter=args.level, refresh_rate=args.refresh)
    
    elif args.command == 'show':
        # Show specific session
        session_files = get_session_files()
        session = next((s for s in session_files if s['session_id'] == args.session_id), None)
        
        if not session:
            print(f"Session {args.session_id} not found.")
            return
        
        logs = read_session_logs(session['path'], tail_lines=args.lines, level_filter=args.level)
        
        print(f"\n=== Session: {args.session_id} ===")
        print(f"File: {session['path']}")
        
        if logs:
            print(f"Showing {len(logs)} log entries\n")
            display_logs(logs, show_stack=args.stack, show_error_type=False)
        else:
            if args.level:
                print(f"No logs found with level: {', '.join(args.level)}")
            else:
                print("No logs found to display")
    
    elif args.command == 'errors':
        # Show errors from ALL sessions
        session_files = get_session_files()
        
        if not session_files:
            print("No sessions found.")
            print(f"EditMode logs will be created in: {logs_dir}")
            print("\nEditMode logs are automatically captured when:")
            print("  1. Unity Editor is running")
            print("  2. Compilation occurs")
            print("\nTo filter errors when logs exist:")
            print("  python monitor_editmode_logs.py --errors    # Compilation errors only")
            print("  python monitor_editmode_logs.py --all-errors # All errors")
            return
        
        # Collect errors from ALL sessions (up to 3 most recent)
        all_errors = []
        sessions_with_errors = []
        
        for session in session_files[:3]:  # Process up to 3 most recent sessions
            logs = read_session_logs(session['path'], level_filter=['Error', 'Exception', 'Assert'])
            if logs:
                sessions_with_errors.append(session['session_id'])
                # Add session info to each log
                for log in logs:
                    log['session_id'] = session['session_id']
                all_errors.extend(logs)
        
        # Filter errors based on flags
        if hasattr(args, 'ecs_only') and args.ecs_only:
            # Show only ECS/DOTS/Burst errors
            all_errors = [e for e in all_errors if is_ecs_error(e.get('message', ''))]
        elif hasattr(args, 'compilation_only') and args.compilation_only:
            # Show all compilation errors (CS/BC/DC/ECS)
            all_errors = [e for e in all_errors if is_compilation_error(e.get('message', ''))]

        # Sort by timestamp (assuming format HH:mm:ss.fff)
        all_errors.sort(key=lambda x: x.get('timestamp', ''))
        
        # Apply line limit if specified
        if args.lines and len(all_errors) > args.lines:
            all_errors = all_errors[-args.lines:]
        
        if all_errors:
            # Determine error type description
            if hasattr(args, 'ecs_only') and args.ecs_only:
                error_type = "ECS/DOTS/Burst errors"
            elif hasattr(args, 'compilation_only') and args.compilation_only:
                error_type = "Compilation errors"
            else:
                error_type = "All errors"

            print(f"\n=== {error_type} from EditMode Sessions ===")
            print(f"Sessions searched: {len(session_files)} (showing errors from {len(sessions_with_errors)})")
            print(f"{error_type} found: {len(all_errors)}")

            # Count by error type and level
            error_counts = {}
            compilation_type_counts = {}
            for log in all_errors:
                level = log.get('level', 'Unknown')
                error_counts[level] = error_counts.get(level, 0) + 1

                # Count compilation error types
                message = log.get('message', '')
                if is_compilation_error(message):
                    comp_type = get_error_type(message)
                    compilation_type_counts[comp_type] = compilation_type_counts.get(comp_type, 0) + 1

            print(f"Error levels: ", end="")
            for level, count in sorted(error_counts.items()):
                print(f"{level}: {count}  ", end="")
            print()

            if compilation_type_counts:
                print(f"Compilation types: ", end="")
                for comp_type, count in sorted(compilation_type_counts.items()):
                    print(f"{comp_type}: {count}  ", end="")
                print()

            print()
            # Show error types for compilation errors
            show_types = hasattr(args, 'compilation_only') and args.compilation_only
            show_types = show_types or (hasattr(args, 'ecs_only') and args.ecs_only)
            display_logs(all_errors, show_stack=args.stack, show_error_type=show_types)
        else:
            if hasattr(args, 'ecs_only') and args.ecs_only:
                print("No ECS/DOTS/Burst errors found in any EditMode session.")
            elif hasattr(args, 'compilation_only') and args.compilation_only:
                print("No compilation errors (CS/BC/DC/ECS errors) found in any EditMode session.")
            else:
                print("No errors found in any EditMode session.")
    
    else:  # recent
        # Show recent logs from current session
        session_files = get_session_files()
        
        if not session_files:
            print("No EditMode sessions found.")
            print(f"Logs will be created in: {logs_dir}")
            return
        
        current_session = session_files[0]
        logs = read_session_logs(current_session['path'], tail_lines=args.lines, level_filter=args.level)
        
        print(f"\n=== Recent EditMode Logs ===")
        print(f"Session: {current_session['session_id']}")
        
        if logs:
            print(f"Showing last {len(logs)} log entries\n")
            display_logs(logs, show_stack=args.stack, show_error_type=False)
        else:
            if args.level:
                print(f"No logs found with level: {', '.join(args.level)}")
            else:
                print("No logs found to display")

if __name__ == '__main__':
    main()