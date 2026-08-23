using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEditor;
using UnityEditor.Compilation;
using Newtonsoft.Json;

namespace PerSpec.Editor.Coordination
{
    /// <summary>
    /// Polls database for asset refresh requests and executes them
    /// Enhanced with background processing support
    /// </summary>
    [InitializeOnLoad]
    public static class AssetRefreshCoordinator
    {
        private static double _lastCheckTime;
        private static double _checkInterval = 1.0; // Check every 1 second
        private static bool _isRefreshing = false;
        private static SQLiteManager _dbManager;
        private static int _currentRequestId = -1;
        private static DateTime _refreshStartTime;
        private static bool _pollingEnabled = true;
        private static SynchronizationContext _unitySyncContext;
        
        // Background processing support
        private static System.Threading.Timer _fallbackTimer;
        private static bool _useBackgroundFallback = true;
        private static DateTime _lastBackgroundCheck;

        // Two-phase compile-aware completion support
        private static bool _compileObserved = false;      // a compile started for the active request
        private static int _graceHops = 0;                 // delayCall hops elapsed since import finished
        private const int GraceHops = 3;                   // hops to wait for a compile to start before finalizing
        private const string SessionKeyRequestId = "PerSpec.AssetRefresh.CompilingRequestId";

        static AssetRefreshCoordinator()
        {
            // Check if PerSpec is initialized
            if (!SQLiteManager.IsPerSpecInitialized())
            {
                // Silent - PerSpecInitializer will show the prompt
                return;
            }
            
            Debug.Log("[AssetRefreshCoordinator] Initializing asset refresh coordination");
            
            // Capture Unity's sync context for thread marshalling
            _unitySyncContext = SynchronizationContext.Current;
            
            try
            {
                _dbManager = new SQLiteManager();
                
                // Only proceed if database is ready
                if (!_dbManager.IsInitialized)
                {
                    return;
                }
                
                EditorApplication.update += OnEditorUpdate;
                _lastCheckTime = EditorApplication.timeSinceStartup;

                // Watch compilation so we can report a 'compiling' phase and only mark a
                // request 'completed' once compilation + domain reload actually finish.
                CompilationPipeline.compilationStarted += OnCompilationStarted;
                CompilationPipeline.compilationFinished += OnCompilationFinished;

                // Reconcile any request left mid-flight by the domain reload that just ran.
                RecoverInterruptedRequests();

                // Set up background fallback timer if enabled
                if (_useBackgroundFallback)
                {
                    SetupBackgroundFallback();
                }
            }
            catch (Exception)
            {
                // Silent failure
                return;
            }
            
            // Force Unity to run in background
            Application.runInBackground = true;
            
            Debug.Log("[AssetRefreshCoordinator] Asset refresh coordination initialized");
        }
        
        private static void SetupBackgroundFallback()
        {
            // Create a timer that runs even when Unity loses focus
            _fallbackTimer = new System.Threading.Timer(
                BackgroundCheck,
                null,
                TimeSpan.FromSeconds(2), // Initial delay
                TimeSpan.FromSeconds(1)  // Repeat interval
            );
            
            Debug.Log("[AssetRefreshCoordinator] Background fallback timer enabled");
        }
        
        private static void BackgroundCheck(object state)
        {
            // Skip if already refreshing or a compile is in progress
            if (_isRefreshing || _compileObserved)
                return;
            
            try
            {
                // Check database from background thread (thread-safe)
                var request = _dbManager.GetNextPendingRefreshRequest();
                
                if (request != null)
                {
                    _lastBackgroundCheck = DateTime.Now;
                    Debug.Log($"[AssetRefreshCoordinator-BG] Found pending request #{request.Id}");
                    
                    // Marshal back to Unity main thread
                    _unitySyncContext?.Post(_ =>
                    {
                        // Don't start a refresh while a compile/domain reload is pending; the
                        // pending row simply waits and is picked up after the reload.
                        // (No forced RequestScriptCompilation: AssetDatabase.Refresh already
                        // schedules a compile when scripts actually changed, and forcing one on
                        // every refresh would make each unfocused refresh pay a full recompile.)
                        if (!_isRefreshing && !_compileObserved && !EditorApplication.isCompiling && request != null)
                        {
                            ProcessRefreshRequest(request);
                        }
                    }, null);
                }
            }
            catch (Exception ex)
            {
                // Log but don't crash the background thread
                UnityEngine.Debug.LogError($"[AssetRefreshCoordinator-BG] Error: {ex.Message}");
            }
        }
        
        private static void OnEditorUpdate()
        {
            if (!_pollingEnabled) return;

            double currentTime = EditorApplication.timeSinceStartup;

            if (currentTime - _lastCheckTime >= _checkInterval)
            {
                _lastCheckTime = currentTime;

                // Don't pick up new work while refreshing or while a compile/reload is pending.
                if (!_isRefreshing && !_compileObserved && !EditorApplication.isCompiling)
                {
                    CheckForPendingRefreshRequests();
                }
            }
        }
        
        private static void CheckForPendingRefreshRequests()
        {
            try
            {
                var request = _dbManager.GetNextPendingRefreshRequest();
                
                if (request != null)
                {
                    Debug.Log($"[AssetRefreshCoordinator] Found pending refresh request #{request.Id}");
                    ProcessRefreshRequest(request);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AssetRefreshCoordinator] Error checking for requests: {ex.Message}");
            }
        }
        
        private static void ProcessRefreshRequest(AssetRefreshRequest request)
        {
            try
            {
                _isRefreshing = true;
                _currentRequestId = request.Id;
                _refreshStartTime = DateTime.Now;
                
                // Update status to running
                _dbManager.UpdateRefreshRequestStatus(request.Id, "running");
                _dbManager.LogExecution(request.Id, "INFO", "AssetRefreshCoordinator", 
                    $"Starting asset refresh (Type: {request.RefreshType}, Options: {request.ImportOptions})");
                
                Debug.Log($"[AssetRefreshCoordinator] Starting refresh for request #{request.Id}");
                Debug.Log($"  Type: {request.RefreshType}");
                Debug.Log($"  Options: {request.ImportOptions}");
                
                // Parse import options
                var importOptions = ImportAssetOptions.Default;
                if (request.ImportOptions == "synchronous")
                {
                    importOptions = ImportAssetOptions.ForceSynchronousImport;
                }
                else if (request.ImportOptions == "force_update")
                {
                    importOptions = ImportAssetOptions.ForceUpdate;
                }
                
                // Store request ID for the postprocessor to use
                AssetRefreshPostprocessor.SetCurrentRequestId(request.Id);
                
                if (request.RefreshType == "selective" && !string.IsNullOrEmpty(request.Paths))
                {
                    // Parse paths from JSON
                    try
                    {
                        var paths = JsonConvert.DeserializeObject<List<string>>(request.Paths);
                        if (paths != null && paths.Count > 0)
                        {
                            Debug.Log($"[AssetRefreshCoordinator] Refreshing {paths.Count} specific paths");
                            foreach (var path in paths)
                            {
                                AssetDatabase.ImportAsset(path, importOptions);
                                Debug.Log($"  Importing: {path}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[AssetRefreshCoordinator] Error parsing paths: {ex.Message}");
                        AssetDatabase.Refresh(importOptions);
                    }
                }
                else
                {
                    // Full refresh
                    Debug.Log("[AssetRefreshCoordinator] Starting full asset refresh");
                    AssetDatabase.Refresh(importOptions);
                }
                
                // If using synchronous import, we can mark as complete immediately
                if (importOptions == ImportAssetOptions.ForceSynchronousImport)
                {
                    OnRefreshComplete();
                }
                else
                {
                    // Set a delayed callback as fallback if postprocessor doesn't fire
                    EditorApplication.delayCall += () =>
                    {
                        EditorApplication.delayCall += () =>
                        {
                            // Check if still running after 2 frames
                            if (_isRefreshing && _currentRequestId == request.Id)
                            {
                                Debug.Log("[AssetRefreshCoordinator] No assets changed, marking refresh as complete");
                                OnRefreshComplete();
                            }
                        };
                    };
                }
                
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AssetRefreshCoordinator] Error processing refresh request: {ex.Message}");
                _dbManager.UpdateRefreshRequestStatus(request.Id, "failed", null, ex.Message);
                _dbManager.LogExecution(request.Id, "ERROR", "AssetRefreshCoordinator",
                    $"Failed to process refresh: {ex.Message}");
                _isRefreshing = false;
                _currentRequestId = -1;
                _compileObserved = false;
                _graceHops = 0;
                AssetRefreshPostprocessor.ClearCurrentRequestId();
                SessionState.EraseInt(SessionKeyRequestId);
            }
        }
        
        /// <summary>
        /// Called by AssetRefreshPostprocessor (and the synchronous/delayCall fallbacks) when
        /// asset IMPORT finishes. This is no longer terminal: a script change triggers a
        /// compilation + domain reload after import, and we only report 'completed' once that
        /// has actually happened. So here we either hand off to the compile path (if a compile
        /// is already running) or open a short grace window to see whether one starts.
        /// </summary>
        public static void OnRefreshComplete()
        {
            if (_currentRequestId < 0) return;

            // A compile already started -> OnCompilationStarted/Finished plus the post-reload
            // recovery own completion. Nothing to do here.
            if (_compileObserved || EditorApplication.isCompiling)
                return;

            // Import done; a script-triggered compilation (if any) starts within a frame or two.
            // Wait a few delayCall hops - NOT a wall-clock window - to see if one begins, then
            // finalize. delayCall is driven by the same editor ticks the original fallback relied
            // on, so a no-compile refresh still completes in a handful of frames even when Unity
            // is unfocused/idle (a wall-clock window can hang there because update ticks stall).
            _graceHops = 0;
            ScheduleGraceCheck();
        }

        private static void ScheduleGraceCheck()
        {
            EditorApplication.delayCall += () =>
            {
                if (_currentRequestId < 0) return;                          // already finalized/reset
                if (_compileObserved || EditorApplication.isCompiling)      // compile path owns it now
                    return;

                if (_graceHops++ < GraceHops)
                {
                    ScheduleGraceCheck();
                }
                else
                {
                    FinalizeCompleted("Asset refresh completed - no script compilation triggered");
                }
            };
        }

        /// <summary>
        /// Terminal completion writer for the live domain: the no-compile path and the
        /// compile-error path (where no domain reload occurs). The compile-success path is
        /// finalized after the domain reload by RecoverInterruptedRequests instead.
        /// </summary>
        private static void FinalizeCompleted(string message, string errorMessage = null)
        {
            if (_currentRequestId < 0) return;

            int completedId = _currentRequestId;
            try
            {
                Debug.Log($"[AssetRefreshCoordinator] Refresh request #{completedId} completed");
                _dbManager?.UpdateRefreshRequestStatus(completedId, "completed", message, errorMessage);
                _dbManager?.LogExecution(completedId, "INFO", "AssetRefreshCoordinator", message);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AssetRefreshCoordinator] Error marking request as complete: {ex.Message}");
            }
            finally
            {
                _isRefreshing = false;
                _currentRequestId = -1;
                _compileObserved = false;
                _graceHops = 0;
                AssetRefreshPostprocessor.ClearCurrentRequestId();
                SessionState.EraseInt(SessionKeyRequestId);
            }
        }

        private static void OnCompilationStarted(object context)
        {
            if (_currentRequestId < 0 || _dbManager == null) return;

            _compileObserved = true;

            // Remember which request is compiling so we can confirm the match after the reload.
            SessionState.SetInt(SessionKeyRequestId, _currentRequestId);

            try
            {
                _dbManager.UpdateRefreshRequestStatus(_currentRequestId, "compiling",
                    "Script compilation started - domain reload pending");
                _dbManager.LogExecution(_currentRequestId, "INFO", "AssetRefreshCoordinator",
                    "Script compilation started");
                Debug.Log($"[AssetRefreshCoordinator] Request #{_currentRequestId} entered 'compiling' state");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AssetRefreshCoordinator] Error updating to compiling: {ex.Message}");
            }
        }

        private static void OnCompilationFinished(object context)
        {
            if (_currentRequestId < 0 || !_compileObserved || _dbManager == null) return;

            int errors = 0;
            try { errors = CompilationErrorCapture.GetErrorCount(); }
            catch { /* if unavailable, treat as success and let the reload path finalize */ }

            if (errors > 0)
            {
                // Compilation failed -> NO domain reload will follow and this handler is still
                // loaded, so finalize now (otherwise the poller would hang). The refresh itself
                // succeeded; error triage is workflow step 3 (monitor_editmode_logs.py --errors).
                FinalizeCompleted(
                    $"Asset refresh completed - compilation finished with {errors} error(s). Run monitor_editmode_logs.py --errors before running tests",
                    $"{errors} compilation error(s)");
            }
            // errors == 0: a domain reload is imminent. Do nothing here - the [InitializeOnLoad]
            // recovery after the reload writes 'completed', which proves the new code is loaded.
        }

        /// <summary>
        /// Runs from the static ctor after every domain reload. Reconciles requests left in a
        /// non-terminal state by the reload: a fresh 'compiling' row whose compile just
        /// succeeded is marked 'completed' (the reload that ran this ctor is the proof the new
        /// code is loaded); stale rows are failed. Also recovers 'running' rows orphaned by a
        /// reload/restart during import (never recovered under the previous implementation).
        /// </summary>
        private static void RecoverInterruptedRequests()
        {
            if (_dbManager == null) return;

            try
            {
                long nowTicks = DateTime.Now.Ticks;
                long compilingThreshold = TimeSpan.FromMinutes(30).Ticks;
                long runningThreshold = TimeSpan.FromMinutes(10).Ticks;

                // (a) 'compiling' rows: the compile finished and triggered this reload.
                foreach (var request in _dbManager.GetRefreshRequestsByStatus("compiling"))
                {
                    long ageTicks = nowTicks - request.CreatedAt.Ticks;

                    if (ageTicks >= compilingThreshold)
                    {
                        _dbManager.UpdateRefreshRequestStatus(request.Id, "failed", null,
                            "Orphaned 'compiling' request (stale > 30 min)");
                        Debug.LogWarning($"[AssetRefreshCoordinator] Marked stale compiling request #{request.Id} as failed");
                        continue;
                    }

                    if (EditorApplication.isCompiling)
                    {
                        // A compile is running now; let the live handlers / next reload finish it.
                        continue;
                    }

                    bool matched = SessionState.GetInt(SessionKeyRequestId, -1) == request.Id;
                    _dbManager.UpdateRefreshRequestStatus(request.Id, "completed",
                        "Compilation and domain reload completed");
                    _dbManager.LogExecution(request.Id, "INFO", "AssetRefreshCoordinator",
                        $"Recovered after domain reload - new code loaded (session match: {matched})");
                    Debug.Log($"[AssetRefreshCoordinator] Request #{request.Id} completed after domain reload");
                }

                // (b) 'running' rows orphaned by a reload/restart during import.
                foreach (var request in _dbManager.GetRefreshRequestsByStatus("running"))
                {
                    long ageTicks = nowTicks - request.CreatedAt.Ticks;
                    if (ageTicks < runningThreshold)
                        continue;

                    _dbManager.UpdateRefreshRequestStatus(request.Id, "failed", null,
                        "Orphaned by domain reload / editor restart");
                    Debug.LogWarning($"[AssetRefreshCoordinator] Marked orphaned running request #{request.Id} as failed");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AssetRefreshCoordinator] Error recovering interrupted requests: {ex.Message}");
            }
            finally
            {
                SessionState.EraseInt(SessionKeyRequestId);
            }
        }
        
        // Methods now accessed via Control Center
        public static void ManualCheckPendingRequests()
        {
            CheckForPendingRefreshRequests();
        }
        
        // Method now accessed via Control Center
        public static void ViewPendingRequests()
        {
            var requests = _dbManager.GetPendingRefreshRequests();
            
            if (requests.Count == 0)
            {
                Debug.Log("[AssetRefreshCoordinator] No pending refresh requests");
            }
            else
            {
                Debug.Log($"[AssetRefreshCoordinator] Found {requests.Count} pending refresh request(s):");
                foreach (var req in requests)
                {
                    Debug.Log($"  #{req.Id}: {req.RefreshType} (Priority: {req.Priority})");
                }
            }
        }
        
        // Method now accessed via Control Center
        public static void TogglePolling()
        {
            _pollingEnabled = !_pollingEnabled;
            Debug.Log($"[AssetRefreshCoordinator] Polling {(_pollingEnabled ? "enabled" : "disabled")}");
        }
        
        // Method now accessed via Control Center
        public static void ForceRefreshNow()
        {
            Debug.Log("[AssetRefreshCoordinator] Forcing asset refresh");
            AssetDatabase.Refresh();
        }

        #region Reset Support

        /// <summary>
        /// Stop all polling for reset operations
        /// </summary>
        public static void StopPolling()
        {
            try
            {
                Debug.Log("[AssetRefreshCoordinator] Stopping polling for reset...");

                // Disable polling flag
                _pollingEnabled = false;

                // Unsubscribe from EditorApplication.update
                EditorApplication.update -= OnEditorUpdate;

                // Unsubscribe from compilation events
                CompilationPipeline.compilationStarted -= OnCompilationStarted;
                CompilationPipeline.compilationFinished -= OnCompilationFinished;

                // Dispose fallback timer if exists
                _fallbackTimer?.Dispose();
                _fallbackTimer = null;

                // Clear database manager reference (will be GC'd)
                _dbManager = null;

                Debug.Log("[AssetRefreshCoordinator] Polling stopped for reset");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AssetRefreshCoordinator] Error stopping polling: {ex.Message}");
            }
        }

        /// <summary>
        /// Restart polling after reset operations
        /// </summary>
        public static void StartPolling()
        {
            try
            {
                if (!SQLiteManager.IsPerSpecInitialized())
                {
                    Debug.LogWarning("[AssetRefreshCoordinator] Cannot start polling - PerSpec not initialized");
                    return;
                }

                Debug.Log("[AssetRefreshCoordinator] Restarting polling after reset...");

                // Recreate database manager
                _dbManager = new SQLiteManager();

                if (!_dbManager.IsInitialized)
                {
                    Debug.LogWarning("[AssetRefreshCoordinator] Database not initialized, cannot start polling");
                    return;
                }

                // Re-enable polling
                _pollingEnabled = true;

                // Re-subscribe to EditorApplication.update
                EditorApplication.update += OnEditorUpdate;

                // Re-subscribe to compilation events
                CompilationPipeline.compilationStarted += OnCompilationStarted;
                CompilationPipeline.compilationFinished += OnCompilationFinished;

                // Reset last check time
                _lastCheckTime = EditorApplication.timeSinceStartup;

                // Set up background fallback timer if enabled
                if (_useBackgroundFallback)
                {
                    SetupBackgroundFallback();
                }

                Debug.Log("[AssetRefreshCoordinator] Polling restarted after reset");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AssetRefreshCoordinator] Error restarting polling: {ex.Message}");
            }
        }

        #endregion
    }
}