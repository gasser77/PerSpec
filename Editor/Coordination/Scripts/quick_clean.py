#!/usr/bin/env python3
"""
Quick Clean - Database cleanup utility for PerSpec
Clears console logs and performs database maintenance
"""

# Prevent Python from creating .pyc files
import sys
import os
sys.dont_write_bytecode = True
os.environ['PYTHONDONTWRITEBYTECODE'] = '1'
import sqlite3
import argparse
from pathlib import Path
from datetime import datetime, timedelta

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
    return perspec_dir / "test_coordination.db"

class DatabaseCleaner:
    def __init__(self):
        self.db_path = get_db_path()
        if not self.db_path.exists():
            raise FileNotFoundError(f"Database not found at {self.db_path}")
    
    def get_connection(self):
        """Get a database connection"""
        conn = sqlite3.connect(str(self.db_path))
        conn.execute("PRAGMA journal_mode=WAL")
        return conn
    
    def get_database_size(self):
        """Get database file size in MB"""
        return self.db_path.stat().st_size / (1024 * 1024)
    
    def clean_console_logs(self, keep_hours=0):
        """Clean console logs older than specified hours"""
        conn = self.get_connection()
        cursor = conn.cursor()
        
        try:
            if keep_hours <= 0:
                # Delete all console logs
                cursor.execute("DELETE FROM console_logs")
                deleted = cursor.rowcount
                print(f"Deleted ALL {deleted} console log entries")
            else:
                # Delete logs older than keep_hours
                cutoff_time = datetime.now() - timedelta(hours=keep_hours)
                cursor.execute("DELETE FROM console_logs WHERE timestamp < ?", 
                             (cutoff_time.strftime('%Y-%m-%d %H:%M:%S'),))
                deleted = cursor.rowcount
                print(f"Deleted {deleted} console logs older than {keep_hours} hour(s)")
            
            conn.commit()
            return deleted
            
        except Exception as e:
            print(f"Error cleaning console logs: {e}")
            conn.rollback()
            return 0
        finally:
            conn.close()
    
    def clean_old_data(self, keep_hours=2):
        """Clean old data from all tables"""
        conn = self.get_connection()
        cursor = conn.cursor()
        
        try:
            cutoff_time = datetime.now() - timedelta(hours=keep_hours)
            cutoff_str = cutoff_time.strftime('%Y-%m-%d %H:%M:%S')
            
            # Clean test data
            cursor.execute("DELETE FROM test_results WHERE created_at < ?", (cutoff_str,))
            deleted_results = cursor.rowcount
            
            # Must list EVERY terminal status: one left out is a row that is never cleaned.
            cursor.execute("DELETE FROM test_requests WHERE created_at < ? AND status IN "
                           "('completed', 'failed', 'cancelled', 'timeout', 'inconclusive', 'no_match')",
                           (cutoff_str,))
            deleted_requests = cursor.rowcount
            
            # Clean execution logs
            cursor.execute("DELETE FROM execution_log WHERE created_at < ?", (cutoff_str,))
            deleted_logs = cursor.rowcount
            
            # Clean asset refresh requests
            cursor.execute("DELETE FROM asset_refresh_requests WHERE created_at < ? AND status IN ('completed', 'failed', 'cancelled')", 
                         (cutoff_str,))
            deleted_refresh = cursor.rowcount
            
            # Clean menu requests
            cursor.execute("DELETE FROM menu_item_requests WHERE created_at < ? AND status IN ('completed', 'failed', 'cancelled')", 
                         (cutoff_str,))
            deleted_menu = cursor.rowcount
            
            conn.commit()
            
            print(f"Cleaned old data (>{keep_hours} hours):")
            print(f"  Test results: {deleted_results}")
            print(f"  Test requests: {deleted_requests}")
            print(f"  Execution logs: {deleted_logs}")
            print(f"  Asset refresh: {deleted_refresh}")
            print(f"  Menu requests: {deleted_menu}")
            
            return sum([deleted_results, deleted_requests, deleted_logs, deleted_refresh, deleted_menu])
            
        except Exception as e:
            print(f"Error cleaning old data: {e}")
            conn.rollback()
            return 0
        finally:
            conn.close()
    
    def vacuum_database(self):
        """Vacuum database to reclaim space"""
        conn = self.get_connection()
        
        try:
            # Get size before
            size_before = self.get_database_size()
            
            # VACUUM must be run outside a transaction
            conn.execute("VACUUM")
            conn.close()
            
            # Get size after
            size_after = self.get_database_size()
            
            saved = size_before - size_after
            if saved > 0:
                print(f"Database compacted: {size_before:.2f} MB -> {size_after:.2f} MB (saved {saved:.2f} MB)")
            else:
                print(f"Database size: {size_after:.2f} MB")
            
            return True
            
        except Exception as e:
            print(f"Error vacuuming database: {e}")
            return False
    
    def get_stats(self):
        """Get database statistics"""
        conn = self.get_connection()
        cursor = conn.cursor()
        
        try:
            stats = {}
            
            # Count entries in each table
            tables = ['console_logs', 'test_requests', 'test_results', 
                     'execution_log', 'asset_refresh_requests', 
                     'menu_item_requests', 'system_status']
            
            for table in tables:
                try:
                    cursor.execute(f"SELECT COUNT(*) FROM {table}")
                    stats[table] = cursor.fetchone()[0]
                except:
                    stats[table] = 0
            
            # Get console logs by level
            try:
                cursor.execute("""
                    SELECT log_level, COUNT(*) 
                    FROM console_logs 
                    GROUP BY log_level
                """)
                log_levels = dict(cursor.fetchall())
                stats['log_levels'] = log_levels
            except:
                stats['log_levels'] = {}
            
            return stats
            
        finally:
            conn.close()

def main():
    # Ensure UTF-8 encoding for emoji/Unicode characters
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')

    parser = argparse.ArgumentParser(description='Clean PerSpec database')
    
    subparsers = parser.add_subparsers(dest='command', help='Commands')
    
    # Logs command
    logs_parser = subparsers.add_parser('logs', help='Clean console logs')
    logs_parser.add_argument('--keep', type=float, default=0, 
                           help='Keep logs from last N hours (0 = delete all)')
    
    # All command
    all_parser = subparsers.add_parser('all', help='Clean all old data')
    all_parser.add_argument('--keep', type=float, default=2,
                          help='Keep data from last N hours')
    
    # Vacuum command
    vacuum_parser = subparsers.add_parser('vacuum', help='Compact database')
    
    # Stats command
    stats_parser = subparsers.add_parser('stats', help='Show database statistics')
    
    # Quick command (most common use case)
    quick_parser = subparsers.add_parser('quick', help='Quick cleanup (clear all logs + vacuum)')
    
    args = parser.parse_args()
    
    if not args.command:
        # Default to stats if no command given
        args.command = 'stats'
    
    try:
        cleaner = DatabaseCleaner()
        
        print("=" * 60)
        print("PerSpec Database Cleaner")
        print("=" * 60)
        print(f"Database: {cleaner.db_path}")
        print(f"Size: {cleaner.get_database_size():.2f} MB")
        print()
        
        if args.command == 'logs':
            cleaner.clean_console_logs(args.keep)
            cleaner.vacuum_database()
            
        elif args.command == 'all':
            cleaner.clean_console_logs(args.keep)
            cleaner.clean_old_data(args.keep)
            cleaner.vacuum_database()
            
        elif args.command == 'vacuum':
            cleaner.vacuum_database()
            
        elif args.command == 'quick':
            print("Performing quick cleanup...")
            cleaner.clean_console_logs(0)  # Delete all logs
            cleaner.clean_old_data(0.5)     # Keep 30 minutes of other data
            cleaner.vacuum_database()
            
        elif args.command == 'stats':
            stats = cleaner.get_stats()
            print("Database Statistics:")
            print("-" * 40)
            
            for table, count in stats.items():
                if table != 'log_levels':
                    print(f"  {table}: {count} entries")
            
            if stats.get('log_levels'):
                print("\nConsole Log Levels:")
                for level, count in stats['log_levels'].items():
                    print(f"  {level}: {count}")
            
            print()
            print(f"Total Size: {cleaner.get_database_size():.2f} MB")
        
        print("=" * 60)
        
    except Exception as e:
        print(f"Error: {e}")
        sys.exit(1)

if __name__ == "__main__":
    main()