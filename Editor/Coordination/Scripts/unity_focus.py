#!/usr/bin/env python3
"""
Unity Window Focus Management - Cross-platform
Zero-dependency solution to bring Unity Editor to foreground
- Windows: Uses PowerShell with user32.dll APIs
- macOS: Uses AppleScript via osascript
- Linux: Returns False (can be extended with wmctrl)
"""

import sys
import subprocess
import time
import platform

def focus_unity_windows():
    """
    Bring Unity Editor window to foreground on Windows.
    Uses PowerShell with Windows user32.dll APIs (no external dependencies).
    
    Returns:
        bool: True if Unity was successfully focused, False otherwise
    """
    
    ps_script = """
    Add-Type @"
    using System;
    using System.Runtime.InteropServices;
    public class Win32 {
        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);
        
        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        
        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);
    }
"@
    
    # Find Unity process
    $unity = Get-Process Unity* -ErrorAction SilentlyContinue | 
             Where-Object {$_.MainWindowTitle -ne ""} | 
             Select-Object -First 1
    
    if ($unity) {
        $handle = $unity.MainWindowHandle
        
        # Check if window is minimized
        if ([Win32]::IsIconic($handle)) {
            # Restore window (SW_RESTORE = 9)
            [Win32]::ShowWindow($handle, 9) | Out-Null
            Start-Sleep -Milliseconds 100
        }
        
        # Bring window to foreground
        $result = [Win32]::SetForegroundWindow($handle)
        
        if ($result) {
            Write-Output "SUCCESS"
        } else {
            Write-Output "FAILED"
        }
    } else {
        Write-Output "NOTFOUND"
    }
    """
    
    try:
        result = subprocess.run(
            ["powershell", "-ExecutionPolicy", "Bypass", "-Command", ps_script],
            capture_output=True,
            text=True,
            shell=False,
            timeout=5
        )
        
        output = result.stdout.strip()
        
        if "SUCCESS" in output:
            return True
        elif "NOTFOUND" in output:
            print("Unity Editor not found. Is it running?")
            return False
        else:
            return False
            
    except subprocess.TimeoutExpired:
        print("Focus operation timed out")
        return False
    except FileNotFoundError:
        print("PowerShell not found. This feature requires Windows.")
        return False
    except Exception as e:
        print(f"Error focusing Unity: {e}")
        return False

def focus_unity_macos():
    """
    Bring Unity Editor window to foreground on macOS.
    Uses AppleScript via osascript (no external dependencies).

    Strategy:
    1. Find Unity process by looking for process names containing "Unity"
    2. Use System Events to set frontmost and raise window
    3. Verify Unity is actually frontmost after activation

    Returns:
        bool: True if Unity was successfully focused, False otherwise
    """

    # Primary method: Use System Events to find and focus Unity process
    # This works regardless of how Unity is installed or named
    script_focus = '''
    tell application "System Events"
        -- Find Unity process (handles "Unity", "Unity Hub", "Unity 6000.x", etc.)
        set unityProc to ""
        set allProcs to name of every process whose background only is false
        repeat with procName in allProcs
            if procName contains "Unity" and procName does not contain "Unity Hub" then
                set unityProc to procName as text
                exit repeat
            end if
        end repeat

        if unityProc is "" then
            return "NOTFOUND"
        end if

        -- Activate the process
        tell process unityProc
            set frontmost to true
            delay 0.3
            -- Raise window if available
            try
                if (count of windows) > 0 then
                    perform action "AXRaise" of window 1
                end if
            end try
        end tell

        -- Verify it became frontmost
        delay 0.2
        set frontProc to name of first process whose frontmost is true
        if frontProc contains "Unity" then
            return "SUCCESS"
        else
            return "RETRY"
        end if
    end tell
    '''

    # Retry method: Use open command with bundle identifier
    script_open_bundle = '''
    do shell script "open -b com.unity3d.UnityEditor5.x 2>/dev/null || open -b com.unity3d.UnityEditor 2>/dev/null"
    delay 0.5
    tell application "System Events"
        set frontProc to name of first process whose frontmost is true
        if frontProc contains "Unity" then
            return "SUCCESS"
        else
            return "FAILED"
        end if
    end tell
    '''

    # Attempt 1: System Events process search
    try:
        result = subprocess.run(
            ['osascript', '-e', script_focus],
            capture_output=True,
            text=True,
            timeout=10
        )

        output = result.stdout.strip()

        if "SUCCESS" in output:
            return True
        elif "NOTFOUND" in output:
            print("Unity Editor process not found. Is it running?")
            return False
        # If RETRY or other, try next method

    except subprocess.TimeoutExpired:
        print("Focus attempt timed out, retrying...")
    except FileNotFoundError:
        print("osascript not found. This feature requires macOS.")
        return False
    except Exception as e:
        print(f"Focus attempt 1 failed: {e}")

    # Attempt 2: Open by bundle identifier
    try:
        result = subprocess.run(
            ['osascript', '-e', script_open_bundle],
            capture_output=True,
            text=True,
            timeout=10
        )

        output = result.stdout.strip()
        if "SUCCESS" in output:
            return True

    except Exception as e:
        print(f"Focus attempt 2 failed: {e}")

    # Attempt 3: Direct activation by name variants
    for app_name in ["Unity", "Unity Editor"]:
        try:
            script = f'tell application "{app_name}" to activate'
            result = subprocess.run(
                ['osascript', '-e', script],
                capture_output=True,
                text=True,
                timeout=5
            )
            if result.returncode == 0:
                time.sleep(0.3)
                # Verify
                verify = subprocess.run(
                    ['osascript', '-e',
                     'tell application "System Events" to return name of first process whose frontmost is true'],
                    capture_output=True, text=True, timeout=3
                )
                if "Unity" in verify.stdout:
                    return True
        except Exception:
            continue

    print("Could not focus Unity Editor after all attempts")
    return False

def focus_unity():
    """
    Cross-platform function to bring Unity Editor window to foreground.
    
    Supports:
    - Windows: Uses PowerShell with user32.dll
    - macOS: Uses AppleScript via osascript
    - Linux: Returns False (can be extended with wmctrl)
    
    Returns:
        bool: True if Unity was successfully focused, False otherwise
    """
    
    system = sys.platform
    
    if system == "win32":
        # Windows
        return focus_unity_windows()
    elif system == "darwin":
        # macOS
        return focus_unity_macos()
    elif system.startswith("linux"):
        # Linux - could be extended with wmctrl
        # For now, just return False
        print("Linux focus not yet implemented. Consider using wmctrl.")
        return False
    else:
        # Unknown platform
        print(f"Platform '{system}' not supported for window focus")
        return False

def focus_after_delay(seconds):
    """
    Focus Unity window after a specified delay.
    Cross-platform support.
    
    Args:
        seconds (float): Number of seconds to wait before focusing
        
    Returns:
        bool: True if Unity was successfully focused, False otherwise
    """
    if seconds > 0:
        time.sleep(seconds)
    return focus_unity()

def keep_unity_focused(duration=None, interval=0.5):
    """
    Continuously keep Unity window in focus for a specified duration.
    Cross-platform support.
    
    Args:
        duration (float, optional): How long to keep focusing in seconds. 
                                   If None, continues indefinitely.
        interval (float): How often to check/refocus in seconds (default: 0.5)
    """
    
    start_time = time.time()
    
    print(f"Keeping Unity focused{f' for {duration} seconds' if duration else ' (press Ctrl+C to stop)'}...")
    
    try:
        while True:
            focus_unity()
            time.sleep(interval)
            
            if duration and (time.time() - start_time) > duration:
                break
    except KeyboardInterrupt:
        print("\nStopped focusing Unity")

def test_focus():
    """Test function to verify Unity focusing works"""
    print("Testing Unity focus...")
    print(f"Platform: {sys.platform} ({platform.system()})")
    
    print("Attempting to focus Unity Editor window...")
    success = focus_unity()
    
    if success:
        print("[OK] Successfully focused Unity Editor")

        # Test platform-specific features
        if sys.platform == "darwin":
            print("Note: On macOS, you may need to grant accessibility permissions")
            print("System Preferences > Security & Privacy > Privacy > Accessibility")
    else:
        print("[FAILED] Could not focus Unity Editor")
    
    return success

if __name__ == "__main__":
    # Run test when executed directly
    import argparse
    
    parser = argparse.ArgumentParser(description='Unity window focus management')
    parser.add_argument('--test', action='store_true', help='Test focus functionality')
    parser.add_argument('--keep', type=float, metavar='SECONDS', 
                       help='Keep Unity focused for specified seconds')
    parser.add_argument('--delay', type=float, default=0, 
                       help='Delay before focusing (seconds)')
    
    args = parser.parse_args()
    
    if args.test:
        test_focus()
    elif args.keep:
        if args.delay:
            print(f"Waiting {args.delay} seconds...")
            time.sleep(args.delay)
        keep_unity_focused(args.keep)
    else:
        if args.delay:
            focus_after_delay(args.delay)
        else:
            success = focus_unity()
            if success:
                print("Unity Editor focused successfully")