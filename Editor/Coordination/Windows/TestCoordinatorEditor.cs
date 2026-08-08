using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine.TestTools;
using UnityEditor.TestTools.TestRunner.Api;
using System.Collections;
using TestMode = UnityEditor.TestTools.TestRunner.Api.TestMode;

namespace PerSpec.Editor.Coordination
{
    [InitializeOnLoad]
    public static class TestCoordinatorEditor
    {
        private static double _lastCheckTime;
        private static double _checkInterval = 1.0; // Check every 1 second
        private static bool _isRunningTests = false;
        private static SQLiteManager _dbManager;
        private static TestExecutor _testExecutor;
        private static int _currentRequestId = -1;

        // Background processing support.
        // Disabled by default: BackgroundPoller already owns the unfocused-editor wake-up
        // for both tests and refreshes and funnels into TryDispatchNextRequest. Running a
        // second timer here only duplicated database reads and dispatch attempts.
        private static SynchronizationContext _unitySyncContext;
        private static System.Threading.Timer _backgroundTimer;
        private static bool _useBackgroundPolling = false;
        private static DateTime _lastBackgroundPoll;

        // Statuses that must never be overwritten once written.
        private static readonly HashSet<string> TerminalStatuses = new HashSet<string>
        {
            "completed", "failed", "cancelled", "timeout", "inconclusive"
        };

        // SessionState survives domain reloads (but not editor restarts), which is exactly
        // the window in which an in-flight test run gets destroyed by a recompile.
        private const string SessionKeyActiveRequestId = "PerSpec.TestRun.ActiveRequestId";
        private const string SessionKeyDispatchTicks = "PerSpec.TestRun.DispatchTicks";
        private const string SessionKeyRetryCount = "PerSpec.TestRun.RetryCount";
        private const int MaxReloadRetries = 1;

        static TestCoordinatorEditor()
        {
            // Check if PerSpec is initialized
            if (!SQLiteManager.IsPerSpecInitialized())
            {
                // Silent - PerSpecInitializer will show the prompt
                return;
            }
            
            Debug.Log("[TestCoordinator] Initializing test coordination system");
            
            // Capture Unity's sync context for thread marshalling
            _unitySyncContext = SynchronizationContext.Current;
            
            try
            {
                _dbManager = new SQLiteManager();

                // Only proceed if database is ready
                if (!_dbManager.IsInitialized)
                {
                    Debug.LogWarning("[TestCoordinator] Database not ready - test polling DISABLED. " +
                                     "Open Tools > PerSpec > Control Center to initialize the database.");
                    return;
                }

                _testExecutor = new TestExecutor(_dbManager);
                
                EditorApplication.update += OnEditorUpdate;
                
                // Initialize last check time
                _lastCheckTime = EditorApplication.timeSinceStartup;
                
                // Set up background polling if enabled
                if (_useBackgroundPolling)
                {
                    SetupBackgroundPolling();
                }
                
                // Force Unity to run in background
                Application.runInBackground = true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TestCoordinator] Initialization failed - test polling DISABLED: {e.Message}");
                return;
            }

            // Update system heartbeat
            _dbManager.UpdateSystemHeartbeat("Unity");

            // Reconcile the run that was in flight when this domain reload happened.
            // Must run before RecoverOrphanedRequests so a recoverable run is completed
            // (or retried) rather than being aged out and marked failed.
            RecoverInterruptedTestRequest();

            // Recover any requests orphaned by domain reload
            RecoverOrphanedRequests();

            Debug.Log("[TestCoordinator] Test coordination system initialized");
        }

        #region Domain Reload Persistence

        /// <summary>
        /// Records the request that is about to be dispatched so it can be reconciled if a
        /// domain reload tears the run down mid-flight. SessionState survives assembly
        /// reloads, unlike every static field in this class.
        /// </summary>
        private static void RememberInFlightRequest(int requestId)
        {
            try
            {
                SessionState.SetInt(SessionKeyActiveRequestId, requestId);
                SessionState.SetString(SessionKeyDispatchTicks, DateTime.Now.Ticks.ToString());
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[TestCoordinator] Could not persist in-flight request: {ex.Message}");
            }
        }

        /// <summary>
        /// Clears the in-flight marker. The retry counter is kept only while a reload-retry
        /// is deliberately in progress, so a second interruption cannot loop forever.
        /// </summary>
        private static void ForgetInFlightRequest(bool clearRetryCount = true)
        {
            try
            {
                SessionState.EraseInt(SessionKeyActiveRequestId);
                SessionState.EraseString(SessionKeyDispatchTicks);
                if (clearRetryCount)
                {
                    SessionState.EraseInt(SessionKeyRetryCount);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[TestCoordinator] Could not clear in-flight request: {ex.Message}");
            }
        }

        /// <summary>
        /// Reconciles the test run that was executing when this domain reload started.
        /// Order of preference: recover real results from disk, otherwise retry the run once,
        /// otherwise mark it failed. A request is never left in a non-terminal state.
        /// </summary>
        private static void RecoverInterruptedTestRequest()
        {
            int requestId = SessionState.GetInt(SessionKeyActiveRequestId, -1);
            if (requestId < 0)
            {
                return;
            }

            bool retryScheduled = false;

            try
            {
                var request = _dbManager.GetRequestById(requestId);
                if (request == null)
                {
                    Debug.LogWarning($"[TestCoordinator] In-flight request #{requestId} no longer exists - nothing to recover");
                    return;
                }

                if (TerminalStatuses.Contains(request.Status))
                {
                    // The run finished before the reload landed. Nothing to do.
                    return;
                }

                Debug.LogWarning($"[TestCoordinator] Request #{requestId} was interrupted by a domain reload " +
                                 $"(status: {request.Status}) - attempting recovery");

                DateTime dispatchTime = ReadDispatchTime();

                // 1. Did Unity actually finish and write results before the reload?
                string resultFile = FindResultFileNewerThan(dispatchTime.AddSeconds(-5));
                if (!string.IsNullOrEmpty(resultFile) && TryRecoverFromResultFile(request, resultFile))
                {
                    Debug.Log($"[TestCoordinator] Recovered request #{requestId} from {Path.GetFileName(resultFile)}");
                    return;
                }

                // 2. No usable results - retry the run once before giving up.
                int retries = SessionState.GetInt(SessionKeyRetryCount, 0);
                if (retries < MaxReloadRetries)
                {
                    SessionState.SetInt(SessionKeyRetryCount, retries + 1);
                    retryScheduled = true;

                    _dbManager.UpdateRequestStatus(request.Id, "pending",
                        "Re-queued after being interrupted by a domain reload");
                    _dbManager.LogExecution(request.Id, "WARN", "Unity",
                        $"Run interrupted by domain reload - retrying (attempt {retries + 2})");

                    Debug.LogWarning($"[TestCoordinator] Re-queued request #{requestId} for one automatic retry");
                    return;
                }

                // 3. Already retried once - stop here so a flaky compile cannot loop.
                _dbManager.UpdateRequestStatus(request.Id, "failed",
                    "Interrupted by domain reload during test execution; the automatic retry was interrupted too");
                _dbManager.LogExecution(request.Id, "ERROR", "Unity",
                    "Run interrupted by domain reload twice - marked failed");

                Debug.LogError($"[TestCoordinator] Request #{requestId} failed - interrupted by domain reload twice");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[TestCoordinator] Error recovering interrupted request: {ex.Message}");
            }
            finally
            {
                ForgetInFlightRequest(clearRetryCount: !retryScheduled);
            }
        }

        /// <summary>
        /// Reads the dispatch timestamp stored alongside the in-flight request id.
        /// Falls back to "five minutes ago" so a missing value never widens the search
        /// window to the whole disk history.
        /// </summary>
        private static DateTime ReadDispatchTime()
        {
            string raw = SessionState.GetString(SessionKeyDispatchTicks, string.Empty);
            if (!string.IsNullOrEmpty(raw) && long.TryParse(raw, out long ticks))
            {
                try
                {
                    return new DateTime(ticks);
                }
                catch (ArgumentOutOfRangeException)
                {
                    // Corrupt value - fall through to the default below.
                }
            }

            return DateTime.Now.AddMinutes(-5);
        }

        /// <summary>
        /// Finds the newest test result XML written after the given cutoff, checking
        /// PerSpec/TestResults first and then Unity's own AppData output locations.
        /// </summary>
        private static string FindResultFileNewerThan(DateTime cutoff)
        {
            try
            {
                string projectPath = Directory.GetParent(Application.dataPath).FullName;
                string testResultsPath = Path.Combine(projectPath, "PerSpec", "TestResults");

                if (Directory.Exists(testResultsPath))
                {
                    var newest = Directory.GetFiles(testResultsPath, "TestResults_*.xml")
                        .Select(f => new FileInfo(f))
                        .Where(fi => fi.LastWriteTime >= cutoff)
                        .OrderByDescending(fi => fi.LastWriteTime)
                        .FirstOrDefault();

                    if (newest != null)
                    {
                        return newest.FullName;
                    }
                }

                // Fall back to Unity's own output locations.
                string appDataResult = FindAppDataTestResult();
                if (!string.IsNullOrEmpty(appDataResult) &&
                    File.GetLastWriteTime(appDataResult) >= cutoff)
                {
                    return appDataResult;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[TestCoordinator] Error searching for result files: {ex.Message}");
            }

            return null;
        }

        #endregion

        /// <summary>
        /// Recovers requests that were stuck in processing/executing status after a domain reload.
        /// When Unity recompiles scripts or reloads the domain, any in-progress test monitoring
        /// is lost, leaving requests in a stuck state. This method detects and handles them.
        /// </summary>
        private static void RecoverOrphanedRequests()
        {
            try
            {
                // Find requests stuck for more than 3 minutes
                var stuckRequests = _dbManager.GetStuckRequests(TimeSpan.FromMinutes(3));

                if (stuckRequests.Count == 0)
                    return;

                Debug.LogWarning($"[TestCoordinator] RecoverOrphanedRequests: Found {stuckRequests.Count} orphaned request(s) after domain reload");

                // SAFETY GATE: only act on requests demonstrably older than the threshold.
                // sqlite-net may translate the `CreatedAt < cutoff` LINQ predicate in ways that
                // mis-compare TEXT timestamps (Python-inserted) against tick-based parameters,
                // causing fresh rows to be falsely flagged as stuck. Re-verify in C# code.
                var nowTicks = DateTime.Now.Ticks;
                var thresholdTicks = TimeSpan.FromMinutes(3).Ticks;

                foreach (var request in stuckRequests)
                {
                    long ageTicks = nowTicks - request.CreatedAt.Ticks;
                    if (ageTicks < thresholdTicks)
                    {
                        Debug.LogWarning($"[TestCoordinator] Skipping request #{request.Id} - " +
                                         $"only {TimeSpan.FromTicks(Math.Max(0, ageTicks)).TotalSeconds:F1}s old " +
                                         $"(CreatedAt={request.CreatedAt:O}, status={request.Status}). " +
                                         $"GetStuckRequests returned it spuriously - likely TEXT/INT compare bug.");
                        continue;
                    }
                    Debug.LogWarning($"[TestCoordinator] Recovering stuck request #{request.Id} " +
                             $"(type: {request.RequestType}, platform: {request.TestPlatform}, status: {request.Status}, " +
                             $"CreatedAt={request.CreatedAt:O}, age={TimeSpan.FromTicks(ageTicks).TotalSeconds:F1}s)");

                    // Check if test results exist in AppData (Unity's default output location)
                    string appDataResult = FindAppDataTestResult();

                    if (!string.IsNullOrEmpty(appDataResult) && File.Exists(appDataResult))
                    {
                        // Check if the result file was written after the request was created
                        var resultFileTime = File.GetLastWriteTime(appDataResult);
                        if (resultFileTime > request.CreatedAt)
                        {
                            Debug.Log($"[TestCoordinator] Found potential results at {appDataResult} " +
                                     $"(written: {resultFileTime:HH:mm:ss}, request created: {request.CreatedAt:HH:mm:ss})");

                            // Try to recover using the result file
                            if (TryRecoverFromResultFile(request, appDataResult))
                            {
                                Debug.Log($"[TestCoordinator] Successfully recovered request #{request.Id} from AppData results");
                                continue;
                            }
                        }
                    }

                    // No valid results found - mark as failed due to domain reload
                    _dbManager.UpdateRequestStatus(request.Id, "failed",
                        "Request interrupted by domain reload - no results recovered");
                    _dbManager.LogExecution(request.Id, "WARN", "Unity",
                        "Request orphaned by domain reload, marked as failed");

                    Debug.LogWarning($"[TestCoordinator] Marked stuck request #{request.Id} as failed (no results found)");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[TestCoordinator] Error recovering orphaned requests: {ex.Message}");
            }
        }

        /// <summary>
        /// Finds Unity's default TestResults.xml location in AppData.
        /// Uses the shared candidate list from TestExecutor - Unity writes to
        /// LocalAppDataLow\{Company}\{Product}, not LocalAppData\Unity\Editor. Probing the
        /// wrong folder here is why orphan recovery used to discard real results and mark
        /// every interrupted run as failed.
        /// </summary>
        private static string FindAppDataTestResult()
        {
            try
            {
                foreach (var candidate in TestExecutor.GetAppDataResultCandidatePaths())
                {
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
            catch (Exception)
            {
                // Ignore errors finding AppData path
            }

            return null;
        }

        /// <summary>
        /// Attempts to recover a stuck request by parsing an existing result file
        /// </summary>
        private static bool TryRecoverFromResultFile(TestRequest request, string resultFilePath)
        {
            try
            {
                // Read and parse the XML file
                string xmlContent = File.ReadAllText(resultFilePath);

                // Basic XML validation
                if (!xmlContent.Contains("<test-run") || !xmlContent.Contains("</test-run>"))
                {
                    Debug.LogWarning($"[TestCoordinator] Result file appears incomplete or invalid");
                    return false;
                }

                // Parse test counts from XML attributes
                int total = ParseXmlAttribute(xmlContent, "total");
                int passed = ParseXmlAttribute(xmlContent, "passed");
                int failed = ParseXmlAttribute(xmlContent, "failed");
                int skipped = ParseXmlAttribute(xmlContent, "skipped");

                // Calculate duration
                float duration = 0;
                var durationMatch = System.Text.RegularExpressions.Regex.Match(xmlContent, @"duration=""([^""]+)""");
                if (durationMatch.Success && float.TryParse(durationMatch.Groups[1].Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float parsedDuration))
                {
                    duration = parsedDuration;
                }

                // Update the request with recovered results
                _dbManager.UpdateRequestResults(
                    request.Id,
                    "completed",
                    total,
                    passed,
                    failed,
                    skipped,
                    duration
                );

                _dbManager.LogExecution(request.Id, "INFO", "Unity",
                    $"Recovered from domain reload: {passed}/{total} passed, {failed} failed");

                // Copy the result file to PerSpec/TestResults for consistency
                CopyResultToPerSpecDirectory(resultFilePath, request.Id);

                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[TestCoordinator] Failed to recover from result file: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Parses an integer attribute from NUnit XML format
        /// </summary>
        private static int ParseXmlAttribute(string xml, string attributeName)
        {
            var match = System.Text.RegularExpressions.Regex.Match(xml, $@"{attributeName}=""(\d+)""");
            if (match.Success && int.TryParse(match.Groups[1].Value, out int value))
                return value;
            return 0;
        }

        /// <summary>
        /// Copies a recovered result file to the PerSpec/TestResults directory
        /// </summary>
        private static void CopyResultToPerSpecDirectory(string sourcePath, int requestId)
        {
            try
            {
                string projectPath = Directory.GetParent(Application.dataPath).FullName;
                string testResultsPath = Path.Combine(projectPath, "PerSpec", "TestResults");

                if (!Directory.Exists(testResultsPath))
                    Directory.CreateDirectory(testResultsPath);

                string destFileName = $"TestResults_Recovered_{requestId}_{DateTime.Now:yyyyMMdd_HHmmss}.xml";
                string destPath = Path.Combine(testResultsPath, destFileName);

                File.Copy(sourcePath, destPath, true);
                Debug.Log($"[TestCoordinator] Copied recovered results to {destFileName}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[TestCoordinator] Could not copy result file: {ex.Message}");
            }
        }
        
        private static void SetupBackgroundPolling()
        {
            _backgroundTimer = new System.Threading.Timer(
                BackgroundPollCallback,
                null,
                TimeSpan.FromSeconds(2), // Initial delay
                TimeSpan.FromSeconds(1)  // Repeat every second
            );
            
            Debug.Log("[TestCoordinator] Background polling enabled");
        }
        
        private static void BackgroundPollCallback(object state)
        {
            // Skip if already running tests
            if (_isRunningTests)
                return;
            
            try
            {
                // Check database from background thread (thread-safe)
                var request = _dbManager.GetNextPendingRequest();
                
                if (request != null)
                {
                    _lastBackgroundPoll = DateTime.Now;
                    Debug.Log($"[TestCoordinator-BG] Found pending test request #{request.Id}");

                    // Marshal back to Unity main thread.
                    // NOTE: no CompilationPipeline.RequestScriptCompilation() here - forcing a
                    // compile triggers a domain reload that destroys the in-flight run.
                    _unitySyncContext?.Post(_ => TryDispatchNextRequest(), null);
                }
            }
            catch (Exception ex)
            {
                // Log but don't crash the background thread
                UnityEngine.Debug.LogError($"[TestCoordinator-BG] Error: {ex.Message}");
            }
        }
        
        private static void OnEditorUpdate()
        {
            // Check for new requests periodically using Editor time
            double currentTime = EditorApplication.timeSinceStartup;
            
            if (currentTime - _lastCheckTime >= _checkInterval)
            {
                _lastCheckTime = currentTime;

                TryDispatchNextRequest();

                // Update heartbeat every check
                _dbManager.UpdateSystemHeartbeat("Unity");
            }
        }

        /// <summary>
        /// The single entry point for starting a queued test run. Every wake-up source
        /// (the editor update loop, this class's optional timer, and BackgroundPoller)
        /// funnels through here so a request can only ever be dispatched once, and only
        /// when the editor is actually able to run it.
        ///
        /// When a guard blocks dispatch the request is deliberately left 'pending' - the
        /// next tick, or the next domain load, picks it up with no manual intervention.
        /// </summary>
        internal static void TryDispatchNextRequest()
        {
            if (_isRunningTests) return;
            if (_dbManager == null || !_dbManager.IsInitialized) return;
            if (_testExecutor == null) return;

            // Dispatching into a compiling or play-mode-switching editor is what strands
            // runs: TestRunnerApi.Execute starts a run that the imminent domain reload
            // immediately destroys.
            if (EditorApplication.isCompiling ||
                EditorApplication.isUpdating ||
                EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return;
            }

            try
            {
                var pendingRequest = _dbManager.GetNextPendingRequest();

                if (pendingRequest != null)
                {
                    Debug.Log($"[TestCoordinator] Found pending request: {pendingRequest.Id}");
                    ProcessTestRequest(pendingRequest);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[TestCoordinator] Error checking for pending requests: {ex.Message}");
            }
        }

        internal static void ProcessTestRequest(TestRequest request)
        {
            // Re-assert the guards: this is public-ish surface reachable from the Control
            // Center and BackgroundPoller, not just from TryDispatchNextRequest.
            if (_isRunningTests)
            {
                Debug.Log($"[TestCoordinator] Ignoring request {request.Id} - a test run is already active");
                return;
            }

            if (EditorApplication.isCompiling ||
                EditorApplication.isUpdating ||
                EditorApplication.isPlayingOrWillChangePlaymode)
            {
                // Leave it pending; it will be dispatched once the editor settles.
                Debug.Log($"[TestCoordinator] Deferring request {request.Id} - editor is compiling or changing play mode");
                return;
            }

            // Unity cannot run EditMode and PlayMode tests in one Execute call. The Python
            // client submits these as two separate requests; reject the combined form
            // explicitly instead of silently running nothing.
            if (request.TestPlatform == "Both")
            {
                const string message = "Platform 'Both' cannot run in a single Unity test run - " +
                                       "submit EditMode and PlayMode as separate requests";
                Debug.LogError($"[TestCoordinator] Request {request.Id}: {message}");
                _dbManager.UpdateRequestStatus(request.Id, "failed", message);
                _dbManager.LogExecution(request.Id, "ERROR", "Unity", message);
                return;
            }

            _isRunningTests = true;
            _currentRequestId = request.Id;

            try
            {
                // Clean TestResults directory before running new tests
                CleanTestResultsDirectory();

                // Update status to processing
                _dbManager.UpdateRequestStatus(request.Id, "processing");
                _dbManager.LogExecution(request.Id, "INFO", "Unity", $"Processing test request {request.Id}");

                // Create test filter based on request
                Filter filter = CreateTestFilter(request);

                // Persist the in-flight marker so a domain reload mid-run can be reconciled.
                RememberInFlightRequest(request.Id);

                // Execute tests
                _testExecutor.ExecuteTests(request, filter, OnTestComplete);

                Debug.Log($"[TestCoordinator] Executing tests for request {request.Id}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[TestCoordinator] Error processing request {request.Id}: {ex.Message}");

                _dbManager.UpdateRequestStatus(request.Id, "failed", ex.Message);
                _dbManager.LogExecution(request.Id, "ERROR", "Unity", $"Failed to execute tests: {ex.Message}");

                ForgetInFlightRequest();
                _isRunningTests = false;
                _currentRequestId = -1;
            }
        }
        
        private static Filter CreateTestFilter(TestRequest request)
        {
            var filter = new Filter();
            
            // Set test mode
            if (request.TestPlatform == "EditMode")
            {
                filter.testMode = TestMode.EditMode;
            }
            else if (request.TestPlatform == "PlayMode")
            {
                filter.testMode = TestMode.PlayMode;
            }
            else
            {
                // "Both" is rejected before reaching here (see ProcessTestRequest); default
                // to EditMode so an unexpected platform value still runs something sane.
                filter.testMode = TestMode.EditMode;
            }

            // Apply filters based on request type
            switch (request.RequestType)
            {
                case "all":
                    // No additional filters needed
                    break;

                case "class":
                    if (!string.IsNullOrEmpty(request.TestFilter))
                    {
                        // groupNames is regex-matched against each test's full name, so this
                        // selects the class node and every method beneath it. testNames would
                        // require an exact full-name match, which a class name never satisfies -
                        // that is why class runs used to report 0 tests and "complete" instantly.
                        filter.groupNames = new[] { "^" + Regex.Escape(request.TestFilter) + @"(\.|$)" };
                    }
                    break;

                case "method":
                    if (!string.IsNullOrEmpty(request.TestFilter))
                    {
                        filter.testNames = new[] { request.TestFilter };
                    }
                    break;
                    
                case "category":
                    if (!string.IsNullOrEmpty(request.TestFilter))
                    {
                        filter.categoryNames = new[] { request.TestFilter };
                    }
                    break;
            }
            
            return filter;
        }
        
        private static void OnTestComplete(TestRequest request, bool success, string errorMessage, TestResultSummary summary)
        {
            try
            {
                // TestExecutor may already have written a precise terminal status - 'timeout'
                // from HandleTestTimeout, 'inconclusive' from the file-monitor path, or
                // 'completed' from RunFinished. This callback used to overwrite whatever was
                // there with 'failed'/'completed', destroying the distinction the caller needs.
                // Preserve an existing terminal status, but still record the counts.
                var currentStatus = _dbManager.GetRequestStatus(request.Id);
                bool alreadyTerminal = currentStatus != null && TerminalStatuses.Contains(currentStatus);

                if (success && summary != null)
                {
                    string statusToWrite = alreadyTerminal ? currentStatus : "completed";

                    // Update request with results
                    _dbManager.UpdateRequestResults(
                        request.Id,
                        statusToWrite,
                        summary.TotalTests,
                        summary.PassedTests,
                        summary.FailedTests,
                        summary.SkippedTests,
                        summary.Duration
                    );

                    _dbManager.LogExecution(request.Id, "INFO", "Unity",
                        $"Tests {statusToWrite}: {summary.PassedTests}/{summary.TotalTests} passed");

                    Debug.Log($"[TestCoordinator] Tests {statusToWrite} for request {request.Id}: " +
                             $"{summary.PassedTests}/{summary.TotalTests} passed");
                }
                else if (alreadyTerminal)
                {
                    // A precise terminal status is already recorded (e.g. 'timeout').
                    // Downgrading it to 'failed' here would lose that detail.
                    Debug.Log($"[TestCoordinator] Request {request.Id} already terminal ('{currentStatus}') - " +
                              "leaving status untouched");
                }
                else
                {
                    _dbManager.UpdateRequestStatus(request.Id, "failed", errorMessage);
                    _dbManager.LogExecution(request.Id, "ERROR", "Unity", $"Test execution failed: {errorMessage}");

                    Debug.LogError($"[TestCoordinator] Tests failed for request {request.Id}: {errorMessage}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[TestCoordinator] Error updating test results: {ex.Message}");
            }
            finally
            {
                // The run reached a conclusion, so there is nothing left for post-reload
                // recovery to reconcile - drop the marker and the retry budget.
                ForgetInFlightRequest();
                _isRunningTests = false;
                _currentRequestId = -1;
            }
        }
        
        // Window functionality now accessed via Control Center - Tools > PerSpec > Control Center
        public static void ShowTestCoordinatorWindow()
        {
            Debug.Log("[PerSpec] Test Coordinator is running in background mode.");
            Debug.Log("[PerSpec] Use the Commands and Debug menu items to interact with the coordinator.");
            Debug.Log($"[PerSpec] Current status: {(_isRunningTests ? $"Running test {_currentRequestId}" : "Idle")}");
            
            if (_dbManager != null)
            {
                var status = _dbManager.GetSystemStatus();
                Debug.Log($"[PerSpec] Database Status:\n{status}");
            }
        }
        
        // Method now accessed via Control Center
        public static void ManualCheckPendingRequests()
        {
            if (!_isRunningTests)
            {
                TryDispatchNextRequest();
            }
            else
            {
                Debug.Log($"[TestCoordinator] Currently running test request {_currentRequestId}");
            }
        }
        
        // Method now accessed via Control Center
        public static void ViewDatabaseStatus()
        {
            if (_dbManager != null)
            {
                var status = _dbManager.GetSystemStatus();
                Debug.Log($"[TestCoordinator] Database Status:\n{status}");
            }
        }
        
        // Method now accessed via Control Center
        public static void CancelCurrentTest()
        {
            if (_isRunningTests && _currentRequestId > 0)
            {
                int cancelledId = _currentRequestId;

                _dbManager.UpdateRequestStatus(cancelledId, "cancelled", "Cancelled by user");

                // Tear the run down properly: without this the executor keeps its file
                // monitor and TestRunnerApi callbacks registered and leaks into the next run.
                _testExecutor?.Abort();

                ForgetInFlightRequest();
                _isRunningTests = false;
                _currentRequestId = -1;
                Debug.Log($"[TestCoordinator] Cancelled test request {cancelledId}");
            }
            else
            {
                Debug.Log("[TestCoordinator] No test currently running");
            }
        }
        
        // Method now accessed via Control Center
        public static void TogglePolling()
        {
            if (_checkInterval > 0)
            {
                _checkInterval = 0;
                Debug.Log("[TestCoordinator] Polling disabled");
            }
            else
            {
                _checkInterval = 1.0;
                Debug.Log("[TestCoordinator] Polling enabled (1 second interval)");
            }
        }
        
        // Method now accessed via Control Center
        public static void DebugPollingStatus()
        {
            double currentTime = EditorApplication.timeSinceStartup;
            double timeSinceLastCheck = currentTime - _lastCheckTime;
            
            Debug.Log($"[TestCoordinator] Polling Debug Info:");
            Debug.Log($"  - Polling Enabled: {_checkInterval > 0}");
            Debug.Log($"  - Check Interval: {_checkInterval} seconds");
            Debug.Log($"  - Current Time: {currentTime:F2}");
            Debug.Log($"  - Last Check Time: {_lastCheckTime:F2}");
            Debug.Log($"  - Time Since Last Check: {timeSinceLastCheck:F2} seconds");
            Debug.Log($"  - Is Running Tests: {_isRunningTests}");
            Debug.Log($"  - Current Request ID: {_currentRequestId}");
        }
        
        private static void CleanTestResultsDirectory()
        {
            try
            {
                string projectPath = Directory.GetParent(Application.dataPath).FullName;
                string testResultsPath = Path.Combine(projectPath, "PerSpec", "TestResults");
                
                if (Directory.Exists(testResultsPath))
                {
                    // Get all files in the TestResults directory
                    string[] files = Directory.GetFiles(testResultsPath, "*", SearchOption.AllDirectories);
                    
                    foreach (string file in files)
                    {
                        try
                        {
                            File.Delete(file);
                        }
                        catch (Exception ex)
                        {
                            Debug.LogWarning($"[TestCoordinator] Failed to delete file {file}: {ex.Message}");
                        }
                    }
                    
                    // Get and delete all subdirectories
                    string[] directories = Directory.GetDirectories(testResultsPath, "*", SearchOption.AllDirectories);
                    
                    // Delete directories in reverse order (deepest first)
                    for (int i = directories.Length - 1; i >= 0; i--)
                    {
                        try
                        {
                            Directory.Delete(directories[i], true);
                        }
                        catch (Exception ex)
                        {
                            Debug.LogWarning($"[TestCoordinator] Failed to delete directory {directories[i]}: {ex.Message}");
                        }
                    }
                    
                    Debug.Log($"[TestCoordinator] Cleaned TestResults directory");
                }
                else
                {
                    // Create the directory if it doesn't exist
                    Directory.CreateDirectory(testResultsPath);
                    Debug.Log($"[TestCoordinator] Created TestResults directory");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[TestCoordinator] Error cleaning TestResults directory: {ex.Message}");
            }
        }
        
        #region Debug Methods (formerly in TestCoordinationDebug)
        
        // Force reinitialization - accessed via Control Center
        public static void ForceReinitialize()
        {
            Debug.Log("[TestCoordinator] Forcing reinitialization...");
            
            // This will trigger the static constructor again after domain reload
            EditorUtility.RequestScriptReload();
        }
        
        // Test database connection - accessed via Control Center
        public static void TestDatabaseConnection()
        {
            try
            {
                var dbManager = new SQLiteManager();
                Debug.Log("[TestCoordinator] Database connection successful");
                
                var pendingRequests = dbManager.GetAllPendingRequests();
                Debug.Log($"[TestCoordinator] Found {pendingRequests.Count} pending requests");
                
                foreach (var request in pendingRequests)
                {
                    Debug.Log($"  - Request #{request.Id}: {request.RequestType} on {request.TestPlatform} (Status: {request.Status})");
                }
                
                dbManager.UpdateSystemHeartbeat("Unity");
                Debug.Log("[TestCoordinator] Heartbeat updated");
            }
            catch (Exception e)
            {
                Debug.LogError($"[TestCoordinator] Database error: {e.Message}");
                Debug.LogError(e.StackTrace);
            }
        }
        
        // Manually process next request - accessed via Control Center
        public static void ManuallyProcessNextRequest()
        {
            try
            {
                var dbManager = new SQLiteManager();
                var nextRequest = dbManager.GetNextPendingRequest();
                
                if (nextRequest != null)
                {
                    Debug.Log($"[TestCoordinator] Processing request #{nextRequest.Id}");
                    
                    // Update to running
                    dbManager.UpdateRequestStatus(nextRequest.Id, "running");
                    
                    // Try to execute
                    var testExecutor = new TestExecutor(dbManager);
                    var filter = new Filter();
                    
                    if (nextRequest.TestPlatform == "EditMode")
                    {
                        filter.testMode = TestMode.EditMode;
                    }
                    else if (nextRequest.TestPlatform == "PlayMode")
                    {
                        filter.testMode = TestMode.PlayMode;
                    }
                    
                    testExecutor.ExecuteTests(nextRequest, filter, (req, success, error, summary) =>
                    {
                        if (success && summary != null)
                        {
                            Debug.Log($"[TestCoordinator] Test completed: {summary.PassedTests}/{summary.TotalTests} passed");
                            dbManager.UpdateRequestResults(req.Id, "completed", 
                                summary.TotalTests, summary.PassedTests, 
                                summary.FailedTests, summary.SkippedTests, summary.Duration);
                        }
                        else
                        {
                            Debug.LogError($"[TestCoordinator] Test failed: {error}");
                            dbManager.UpdateRequestStatus(req.Id, "failed", error);
                        }
                    });
                }
                else
                {
                    Debug.Log("[TestCoordinator] No pending requests found");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[TestCoordinator] Error processing request: {e.Message}");
                Debug.LogError(e.StackTrace);
            }
        }
        
        // Clear all pending requests - accessed via Control Center
        public static void ClearAllPendingRequests()
        {
            try
            {
                var dbManager = new SQLiteManager();
                var pendingRequests = dbManager.GetAllPendingRequests();
                
                foreach (var request in pendingRequests)
                {
                    dbManager.UpdateRequestStatus(request.Id, "cancelled", "Cancelled by debug tool");
                    Debug.Log($"[TestCoordinator] Cancelled request #{request.Id}");
                }
                
                Debug.Log($"[TestCoordinator] Cleared {pendingRequests.Count} pending requests");
            }
            catch (Exception e)
            {
                Debug.LogError($"[TestCoordinator] Error clearing requests: {e.Message}");
            }
        }
        
        #endregion

        #region Reset Support

        /// <summary>
        /// Stop all polling for reset operations
        /// </summary>
        public static void StopPolling()
        {
            try
            {
                Debug.Log("[TestCoordinatorEditor] Stopping polling for reset...");

                // Unsubscribe from EditorApplication.update
                EditorApplication.update -= OnEditorUpdate;

                // Dispose background timer if exists
                _backgroundTimer?.Dispose();
                _backgroundTimer = null;

                // Clear database manager reference (will be GC'd)
                _dbManager = null;
                _testExecutor = null;

                Debug.Log("[TestCoordinatorEditor] Polling stopped for reset");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[TestCoordinatorEditor] Error stopping polling: {ex.Message}");
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
                    Debug.LogWarning("[TestCoordinatorEditor] Cannot start polling - PerSpec not initialized");
                    return;
                }

                Debug.Log("[TestCoordinatorEditor] Restarting polling after reset...");

                // Recreate database manager
                _dbManager = new SQLiteManager();

                if (!_dbManager.IsInitialized)
                {
                    Debug.LogWarning("[TestCoordinatorEditor] Database not initialized, cannot start polling");
                    return;
                }

                // Recreate test executor
                _testExecutor = new TestExecutor(_dbManager);

                // Re-subscribe to EditorApplication.update
                EditorApplication.update += OnEditorUpdate;

                // Reset last check time
                _lastCheckTime = EditorApplication.timeSinceStartup;

                // Set up background polling if enabled
                if (_useBackgroundPolling)
                {
                    SetupBackgroundPolling();
                }

                // Update heartbeat
                _dbManager.UpdateSystemHeartbeat("Unity");

                Debug.Log("[TestCoordinatorEditor] Polling restarted after reset");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[TestCoordinatorEditor] Error restarting polling: {ex.Message}");
            }
        }

        #endregion
    }
}