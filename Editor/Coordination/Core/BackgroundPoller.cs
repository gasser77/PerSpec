using System;
using System.Threading;
using UnityEngine;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.TestTools.TestRunner.Api;

namespace PerSpec.Editor.Coordination
{
    /// <summary>
    /// Background polling system that continues to run even when Unity loses focus
    /// Uses System.Threading.Timer for true background operation
    /// </summary>
    [InitializeOnLoad]
    public static class BackgroundPoller
    {
        private static System.Threading.Timer _backgroundTimer;
        private static SynchronizationContext _unitySyncContext;
        private static SQLiteManager _dbManager;
        private static bool _isEnabled = false;
        private static readonly object _lockObject = new object();
        private static DateTime _lastPollTime;
        private static int _pollInterval = 1000; // 1 second in milliseconds
        
        // Track if we're currently processing to avoid overlapping operations
        private static bool _isProcessing = false;

        // EditorPrefs is a main-thread-only API, but the poll callback runs on a
        // ThreadPool thread. Cache the value from the main thread and read the cache
        // in the callback - reading EditorPrefs there could throw, killing the timer
        // thread silently and leaving polling permanently dead.
        private static volatile bool _perspecEnabledCache = true;

        // Watchdog for a main-thread Post that never got pumped (editor asleep/frozen).
        private static DateTime _postPendingSince = DateTime.MinValue;
        private const double POST_WATCHDOG_SECONDS = 30.0;

        static BackgroundPoller()
        {
            // Check if PerSpec is initialized
            if (!SQLiteManager.IsPerSpecInitialized())
            {
                // Silent - PerSpecInitializer will show the prompt
                return;
            }

            // Check if PerSpec is enabled by checking EditorPrefs directly
            bool isEnabled = EditorPrefs.GetBool("PerSpec_Enabled", true);
            _perspecEnabledCache = isEnabled;
            if (!isEnabled)
            {
                Debug.Log("[BackgroundPoller] PerSpec is disabled - background polling will not start");
                return;
            }
            
            Debug.Log("[BackgroundPoller] Initializing background polling system");
            
            // Capture Unity's synchronization context for thread marshalling
            _unitySyncContext = SynchronizationContext.Current;
            
            // Initialize database manager
            try
            {
                _dbManager = new SQLiteManager();

                // Only proceed if database is ready
                if (!_dbManager.IsInitialized)
                {
                    Debug.LogWarning("[BackgroundPoller] Database not ready - background polling DISABLED. " +
                                     "Open Tools > PerSpec > Control Center to initialize the database.");
                    return;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[BackgroundPoller] Database init failed - background polling DISABLED: {e.Message}");
                return;
            }
            
            // Auto-enable background polling
            EnableBackgroundPolling();
            
            // Subscribe to domain reload to clean up
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
        }
        
        private static void OnBeforeAssemblyReload()
        {
            Debug.Log("[BackgroundPoller] Assembly reloading, stopping background timer");
            DisableBackgroundPolling();
        }
        
        public static void EnableBackgroundPolling()
        {
            lock (_lockObject)
            {
                // Check if PerSpec is enabled by checking EditorPrefs directly
                bool isEnabled = EditorPrefs.GetBool("PerSpec_Enabled", true);
                _perspecEnabledCache = isEnabled;
                if (!isEnabled)
                {
                    Debug.Log("[BackgroundPoller] Cannot enable - PerSpec is disabled");
                    return;
                }

                if (_isEnabled)
                {
                    Debug.Log("[BackgroundPoller] Background polling already enabled");
                    return;
                }

                _isEnabled = true;
                _lastPollTime = DateTime.Now;

                // Create and start the background timer
                _backgroundTimer = new System.Threading.Timer(
                    BackgroundPollCallback,
                    null,
                    0, // Start immediately
                    _pollInterval // Repeat every second
                );

                Debug.Log("[BackgroundPoller] Background polling ENABLED");
            }
        }
        
        public static void DisableBackgroundPolling()
        {
            lock (_lockObject)
            {
                if (!_isEnabled)
                {
                    Debug.Log("[BackgroundPoller] Background polling already disabled");
                    return;
                }
                
                _isEnabled = false;
                
                // Dispose of the timer
                _backgroundTimer?.Dispose();
                _backgroundTimer = null;
                
                Debug.Log("[BackgroundPoller] Background polling DISABLED");
            }
        }
        
        private static void BackgroundPollCallback(object state)
        {
            // Skip if already processing, disabled, or PerSpec is disabled.
            // _perspecEnabledCache is used instead of EditorPrefs because this runs on a
            // ThreadPool thread where Unity editor APIs are not legal to call.
            if (!_isEnabled || !_perspecEnabledCache)
            {
                return;
            }

            if (_isProcessing)
            {
                // Watchdog: a posted callback only runs when the editor pumps its main loop.
                // If the editor stayed asleep (or the post was dropped) the flag would latch
                // on forever and kill polling for the session, so release it after a grace
                // period and let the next tick re-post.
                if ((DateTime.Now - _postPendingSince).TotalSeconds < POST_WATCHDOG_SECONDS)
                {
                    return;
                }

                UnityEngine.Debug.LogWarning("[BackgroundPoller-Thread] Main-thread dispatch did not run within " +
                                             $"{POST_WATCHDOG_SECONDS}s - releasing lock and retrying");
                _isProcessing = false;
            }

            bool posted = false;

            try
            {
                _isProcessing = true;

                // Database operations are thread-safe with SQLite WAL mode.
                // Refresh requests are deliberately NOT handled here - AssetRefreshCoordinator
                // owns that queue, including its own background timer and the compile-aware
                // two-phase handling that lets `quick_refresh.py --wait` block through
                // compilation. A bare AssetDatabase.Refresh here raced it and marked requests
                // completed before compilation had even started.
                bool hasTestRequests = CheckForPendingTestRequests();

                if (hasTestRequests)
                {
                    Debug.Log("[BackgroundPoller-Thread] Found pending test request(s)");

                    // Marshal the processing back to Unity's main thread
                    var context = _unitySyncContext;
                    if (context != null)
                    {
                        posted = true;
                        _postPendingSince = DateTime.Now;
                        context.Post(_ =>
                        {
                            try
                            {
                                // Refresh the cached enable flag while we are legally on the main thread.
                                _perspecEnabledCache = EditorPrefs.GetBool("PerSpec_Enabled", true);
                                if (!_perspecEnabledCache) return;

                                Debug.Log("[BackgroundPoller-MainThread] Processing pending requests on main thread");

                                // Trigger test processing
                                ProcessPendingTestRequest();

                                // NOTE: Do NOT call CompilationPipeline.RequestScriptCompilation() here.
                                // A forced compile triggers a domain reload that destroys the in-flight
                                // TestExecutor, its EditorApplication.update file monitor and its
                                // ICallbacks registration - stranding the request at processing/executing
                                // forever. AssetRefreshCoordinator removed the same anti-pattern in 1.7.0.
                            }
                            catch (Exception ex)
                            {
                                Debug.LogError($"[BackgroundPoller-MainThread] Error processing requests: {ex.Message}");
                            }
                            finally
                            {
                                // Released only once the main-thread work is actually done, so the
                                // next timer tick cannot queue a duplicate dispatch behind this one.
                                _isProcessing = false;
                            }
                        }, null);
                    }
                }
            }
            catch (Exception ex)
            {
                // Log errors but don't crash the background thread
                // Note: Debug.Log might not work from background thread
                UnityEngine.Debug.LogError($"[BackgroundPoller-Thread] Error in background poll: {ex.Message}");
            }
            finally
            {
                // When work was posted, the Post callback owns clearing the flag.
                if (!posted)
                {
                    _isProcessing = false;
                }
            }
        }
        
        private static bool CheckForPendingTestRequests()
        {
            try
            {
                // Direct database check - thread safe
                var request = _dbManager.GetNextPendingRequest();
                return request != null;
            }
            catch
            {
                return false;
            }
        }
        
        private static void ProcessPendingTestRequest()
        {
            try
            {
                // Route through the single guarded dispatch entry point. It re-reads the
                // request on the main thread and enforces the busy / compiling / play-mode
                // guards, so this poller can never double-dispatch a run that
                // TestCoordinatorEditor's own update loop already picked up.
                TestCoordinatorEditor.TryDispatchNextRequest();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[BackgroundPoller] Error processing test request: {ex.Message}");
            }
        }
        
        
        // Menu items for manual control
        // Methods now accessed via Control Center
        public static void MenuEnablePolling()
        {
            EnableBackgroundPolling();
        }
        
        // Method now accessed via Control Center
        public static void MenuDisablePolling()
        {
            DisableBackgroundPolling();
        }
        
        // Method now accessed via Control Center
        public static void ShowPollingStatus()
        {
            Debug.Log($"[BackgroundPoller] Status: {(_isEnabled ? "ENABLED" : "DISABLED")}");
            if (_isEnabled)
            {
                Debug.Log($"  Last poll: {_lastPollTime:HH:mm:ss}");
                Debug.Log($"  Poll interval: {_pollInterval}ms");
                Debug.Log($"  Is processing: {_isProcessing}");
            }
        }
        
        // Method now accessed via Control Center
        public static void ForceScriptCompilation()
        {
            Debug.Log("[BackgroundPoller] Forcing script compilation");
            CompilationPipeline.RequestScriptCompilation();
        }
    }
}