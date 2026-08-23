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

        /// <summary>True from the moment a request is claimed until its run reaches a terminal state.</summary>
        public static bool IsRunningTests => _isRunningTests;

        /// <summary>Id of the request currently claimed, or -1.</summary>
        public static int CurrentRequestId => _currentRequestId;
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
            "completed", "failed", "cancelled", "timeout", "inconclusive", "no_match"
        };

        // SessionState survives domain reloads (but not editor restarts), which is exactly
        // the window in which an in-flight test run gets destroyed by a recompile.
        private const string SessionKeyActiveRequestId = "PerSpec.TestRun.ActiveRequestId";
        private const string SessionKeyDispatchTicks = "PerSpec.TestRun.DispatchTicks";
        private const string SessionKeyRetryCount = "PerSpec.TestRun.RetryCount";
        private const string SessionKeyPlatform = "PerSpec.TestRun.Platform";

        // Set only while the filter is being resolved and no run has started yet. A reload
        // in that window is not an interrupted run - there is no XML to hunt for and no
        // PlayMode completion checker to wait on.
        private const string SessionKeyPreflight = "PerSpec.TestRun.Preflight";
        private const int MaxReloadRetries = 1;

        // How long to let PlayModeTestCompletionChecker finish a PlayMode run before the
        // interruption ladder takes over. Entering play mode always triggers a domain reload,
        // so that reload must not be mistaken for a crash.
        private const double PlayModeReconcileDelaySeconds = 15.0;

        // --- Stuck-run watchdog --------------------------------------------------------
        // TestExecutor's MAX_WAIT_TIME monitor lives on EditorApplication.update and dies
        // with the domain reload that entering PlayMode ALWAYS causes, so a PlayMode run
        // has no in-process timeout at all. PlayModeTestCompletionChecker only fires on
        // EnteredEditMode, so a run that never leaves PlayMode is never finalised. And
        // RecoverOrphanedRequests runs only from the static constructor, i.e. only on a
        // domain reload - which a wedged PlayMode run never triggers.
        //
        // This sweep is the backstop that keeps the promise: no request stays non-terminal
        // forever.
        private const double WatchdogSweepIntervalSeconds = 30.0;
        private const double WatchdogGraceSeconds = 300.0;  // margin over TestExecutor's ceiling
        private const double WatchdogFloorSeconds = 120.0;  // lowest ceiling an override may set

        private const string PrefWatchdogEnabled = "PerSpec_Watchdog_Enabled";
        private const string PrefWatchdogTimeoutSeconds = "PerSpec_Watchdog_TimeoutSeconds"; // 0 = auto
        private const string PrefWatchdogStopPlayMode = "PerSpec_Watchdog_StopPlayMode";

        private static double _lastWatchdogSweepTime;

        // Requests this session has already driven to a terminal status. Entries are added
        // only after the write is confirmed, so a locked database is retried rather than
        // silently abandoned for the rest of the session.
        private static readonly HashSet<int> _watchdogHandled = new HashSet<int>();

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

                // First sweep is one full interval away: the static constructor is about to
                // run RecoverOrphanedRequests, which covers everything a sweep would find.
                _lastWatchdogSweepTime = _lastCheckTime;
                
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
        private static void RememberInFlightRequest(TestRequest request)
        {
            try
            {
                SessionState.SetInt(SessionKeyActiveRequestId, request.Id);
                SessionState.SetString(SessionKeyDispatchTicks, DateTime.Now.Ticks.ToString());

                // The platform decides how a reload should be interpreted: for PlayMode it is
                // routine, for EditMode it means a recompile ate the run.
                SessionState.SetString(SessionKeyPlatform, request.TestPlatform ?? string.Empty);
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
                SessionState.EraseString(SessionKeyPlatform);
                SessionState.EraseBool(SessionKeyPreflight);
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
        ///
        /// Not every domain reload is an interruption. Entering PlayMode always causes one, and
        /// exiting it causes another - and [InitializeOnLoad] constructors run BEFORE
        /// playModeStateChanged(EnteredEditMode), so this method reaches the row before
        /// PlayModeTestCompletionChecker gets a chance to finish it. Treating either reload as a
        /// crash re-queued a live run to 'pending', which dispatched it a second time and let the
        /// duplicate adopt the first run's results.
        /// </summary>
        private static void RecoverInterruptedTestRequest()
        {
            int requestId = SessionState.GetInt(SessionKeyActiveRequestId, -1);
            if (requestId < 0)
            {
                return;
            }

            // Reload while play mode is running or starting: routine, and the marker must
            // survive it. Returning before the try block keeps the finally from erasing it.
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isPlaying)
            {
                Debug.Log($"[TestCoordinator] Request #{requestId}: domain reload is part of entering " +
                          "or holding PlayMode - not an interruption");
                return;
            }

            bool retryScheduled = false;
            bool deferred = false;

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

                // 0. The reload landed while the filter was still being resolved, so no run
                //    was ever started. Hunting for a results file is guaranteed to find
                //    nothing, and the PlayMode hand-off below would burn its whole delay
                //    waiting on a completion checker that has no run to check. Re-queue.
                if (SessionState.GetBool(SessionKeyPreflight, false))
                {
                    SessionState.EraseBool(SessionKeyPreflight);

                    int preflightRetries = SessionState.GetInt(SessionKeyRetryCount, 0);
                    if (preflightRetries < MaxReloadRetries)
                    {
                        SessionState.SetInt(SessionKeyRetryCount, preflightRetries + 1);
                        retryScheduled = true;

                        _dbManager.UpdateRequestStatus(request.Id, "pending",
                            "Re-queued: domain reload landed while resolving the filter, before any test ran");
                        _dbManager.LogExecution(request.Id, "WARN", "Unity",
                            "Domain reload during filter resolution - re-queued");

                        Debug.LogWarning($"[TestCoordinator] Re-queued request #{requestId} - reload " +
                                         "interrupted filter resolution before the run started");
                        return;
                    }

                    // Already retried once: fall through to the failure rung below.
                }

                Debug.LogWarning($"[TestCoordinator] Request #{requestId} was interrupted by a domain reload " +
                                 $"(status: {request.Status}) - attempting recovery");

                DateTime dispatchTime = ResolveRunAnchor(request);

                // 1. Did Unity actually finish and write results before the reload?
                string resultFile = FindResultFileNewerThan(dispatchTime.AddSeconds(-5), request);
                if (!string.IsNullOrEmpty(resultFile) && TryRecoverFromResultFile(request, resultFile))
                {
                    Debug.Log($"[TestCoordinator] Recovered request #{requestId} from {Path.GetFileName(resultFile)}");
                    return;
                }

                // 2. This is the reload that fires as PlayMode exits. PlayModeTestCompletionChecker
                //    has not run yet and owns completion for these runs, so hand off to it and only
                //    fall back to the retry ladder if it has produced nothing a few seconds later.
                if (IsPlayModeRequest(request))
                {
                    deferred = true;
                    ScheduleDeferredReconcile(requestId, PlayModeReconcileDelaySeconds);
                    Debug.Log($"[TestCoordinator] Request #{requestId} is a PlayMode run - waiting " +
                              $"{PlayModeReconcileDelaySeconds:F0}s for the completion checker before recovering");
                    return;
                }

                // 3. No usable results - retry the run once before giving up.
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

                // 4. Already retried once - stop here so a flaky compile cannot loop.
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
                // A deferred reconcile still needs the marker, so leave it in place for it.
                if (!deferred)
                {
                    ForgetInFlightRequest(clearRetryCount: !retryScheduled);
                }
            }
        }

        /// <summary>
        /// Whether the in-flight request is a PlayMode run, preferring the platform recorded at
        /// dispatch time over the DB row.
        /// </summary>
        private static bool IsPlayModeRequest(TestRequest request)
        {
            string platform = SessionState.GetString(SessionKeyPlatform, string.Empty);
            if (string.IsNullOrEmpty(platform))
            {
                platform = request?.TestPlatform;
            }

            return platform == "PlayMode";
        }

        /// <summary>
        /// Re-runs the interruption ladder after a delay, but only if the request is still
        /// non-terminal by then. Gives PlayModeTestCompletionChecker time to publish real results
        /// instead of racing it.
        ///
        /// This covers domain reloads only. A genuine editor crash or restart wipes SessionState
        /// entirely, so that case is - and always was - owned by RecoverOrphanedRequests and its
        /// 3-minute stuck-request sweep.
        /// </summary>
        private static void ScheduleDeferredReconcile(int requestId, double delaySeconds)
        {
            double dueTime = EditorApplication.timeSinceStartup + delaySeconds;
            EditorApplication.CallbackFunction tick = null;

            tick = () =>
            {
                if (EditorApplication.timeSinceStartup < dueTime)
                {
                    return;
                }

                EditorApplication.update -= tick;

                try
                {
                    // Something finished it in the meantime (the usual case) - stop here.
                    if (SessionState.GetInt(SessionKeyActiveRequestId, -1) != requestId)
                    {
                        return;
                    }

                    var request = _dbManager?.GetRequestById(requestId);
                    if (request == null || TerminalStatuses.Contains(request.Status))
                    {
                        ForgetInFlightRequest();
                        return;
                    }

                    Debug.LogWarning($"[TestCoordinator] PlayMode request #{requestId} still {request.Status} " +
                                     $"after {delaySeconds:F0}s - running interruption recovery");

                    // Clear the platform marker so the ladder is not deferred a second time.
                    SessionState.EraseString(SessionKeyPlatform);

                    RecoverInterruptedTestRequest();
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[TestCoordinator] Deferred reconcile failed: {ex.Message}");
                }
            };

            EditorApplication.update += tick;
        }

        /// <summary>
        /// Clears the in-flight marker when another component finished the request.
        /// Called by PlayModeTestCompletionChecker so a completed run is never later mistaken
        /// for an interrupted one.
        /// </summary>
        internal static void NotifyRequestFinalizedExternally(int requestId)
        {
            try
            {
                if (SessionState.GetInt(SessionKeyActiveRequestId, -1) == requestId)
                {
                    ForgetInFlightRequest();
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[TestCoordinator] Could not clear in-flight marker for #{requestId}: {ex.Message}");
            }
        }

        /// <summary>
        /// When this run was dispatched, in decreasing order of reliability.
        ///
        /// SessionState is lost on an editor restart, so fall back to the request's own
        /// timestamps - they survive anything. If even those are unknown, return MaxValue:
        /// "I do not know when this ran" has to mean "adopt nothing", not "adopt anything from
        /// the last five minutes", which is what the old fallback meant.
        /// </summary>
        private static DateTime ResolveRunAnchor(TestRequest request)
        {
            if (request == null)
            {
                return DateTime.MaxValue;
            }

            // The SessionState stamp describes whichever request is currently marked
            // in-flight, so it is only valid for THAT request. Reading it for any other id
            // yields a different run's clock - harmless for the single original caller,
            // wrong the moment a sweep over many rows reuses this.
            if (SessionState.GetInt(SessionKeyActiveRequestId, -1) == request.Id)
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
                        // Corrupt value - fall through.
                    }
                }
            }

            // StartedAt is stamped on the 'processing' write and never re-stamped, so it is
            // the only column that means "dispatched". CreatedAt is when Python inserted the
            // row and can be arbitrarily older when the request queued behind a compile or
            // another run - the single largest source of false timeouts.
            if (request.StartedAt.HasValue && request.StartedAt.Value != default)
            {
                return request.StartedAt.Value;
            }

            if (request.CreatedAt != default)
            {
                return request.CreatedAt;
            }

            return DateTime.MaxValue;
        }

        /// <summary>
        /// Finds the newest test result XML that was written after the given cutoff AND actually
        /// contains this request's tests, checking PerSpec/TestResults first and then Unity's own
        /// AppData output locations.
        /// </summary>
        private static string FindResultFileNewerThan(DateTime cutoff, TestRequest request)
        {
            return FindResultFileNewerThan(cutoff, request, out _, out _);
        }

        /// <summary>
        /// As above, but reports what it saw. A caller that is about to write a fallback
        /// verdict needs to distinguish "no results file existed at all" from "files existed
        /// but demonstrably belonged to another run" - those are very different failures and
        /// must not collapse into the same status.
        /// </summary>
        private static string FindResultFileNewerThan(DateTime cutoff, TestRequest request,
                                                      out TestResultVerification verification,
                                                      out bool sawAnyCandidate)
        {
            verification = default;
            sawAnyCandidate = false;

            try
            {
                string projectPath = Directory.GetParent(Application.dataPath).FullName;
                string testResultsPath = Path.Combine(projectPath, "PerSpec", "TestResults");

                var candidates = new List<string>();

                if (Directory.Exists(testResultsPath))
                {
                    candidates.AddRange(Directory.GetFiles(testResultsPath, "TestResults_*.xml")
                        .Select(f => new FileInfo(f))
                        .Where(fi => fi.LastWriteTime >= cutoff)
                        .OrderByDescending(fi => fi.LastWriteTime)
                        .Select(fi => fi.FullName));
                }

                // Fall back to Unity's own output locations.
                string appDataResult = FindAppDataTestResult();
                if (!string.IsNullOrEmpty(appDataResult) &&
                    File.GetLastWriteTime(appDataResult) >= cutoff)
                {
                    candidates.Add(appDataResult);
                }

                sawAnyCandidate = candidates.Count > 0;

                // Recovery is the end of the line for this run, so a broader run's file is
                // better than nothing - it contributes only its matching subset.
                string chosen = TestResultVerifier.PickBest(candidates, request, true, out verification);

                if (chosen == null && candidates.Count > 0)
                {
                    TestResultVerifier.LogRejection("TestCoordinator", verification);
                }

                return chosen;
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

                    FinalizeStuckRequest(
                        request,
                        ResolveRunAnchor(request),
                        fallbackStatus: "failed",
                        fallbackMessage: "Request interrupted by domain reload - no results recovered",
                        source: "OrphanRecovery");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[TestCoordinator] Error recovering orphaned requests: {ex.Message}");
            }
        }

        /// <summary>
        /// Drives one non-terminal request to a terminal status: adopt real results if any
        /// exist, otherwise 'inconclusive' when results existed but demonstrably were not
        /// this request's, otherwise the caller's fallback verdict. A request is never left
        /// non-terminal.
        ///
        /// Shared by startup orphan recovery ('failed' - the editor restarted and the run is
        /// gone) and the periodic watchdog ('timeout' - the run may still be alive but has
        /// blown its ceiling). One decision ladder, two policies.
        ///
        /// Returns true when the row is confirmed terminal afterwards. A swallowed write -
        /// SQLiteManager logs and returns on a locked database - must be retried by the
        /// caller, not mistaken for success.
        /// </summary>
        private static bool FinalizeStuckRequest(TestRequest request, DateTime anchor,
                                                 string fallbackStatus, string fallbackMessage,
                                                 string source)
        {
            // 1. Recover first, always. A PlayMode run whose completion checker never fired
            //    may have written a perfectly good XML that simply nobody adopted.
            string resultFile = FindResultFileNewerThan(anchor.AddSeconds(-5), request,
                                                        out var verification,
                                                        out bool sawResultsFile);

            if (!string.IsNullOrEmpty(resultFile) &&
                TryRecoverFromResultFile(request, resultFile, out verification))
            {
                Debug.Log($"[TestCoordinator-{source}] Recovered request #{request.Id} from " +
                          $"{Path.GetFileName(resultFile)} instead of marking it '{fallbackStatus}'");
                return true;
            }

            // 2. Results existed but were provably not this request's. Never report that as
            //    green, and never conflate it with "the run vanished". Of the two shapes,
            //    'no_match' is the one the caller can fix by correcting the name; 'inconclusive'
            //    means nothing ran at all, which a broken run explains just as well.
            if (sawResultsFile && verification.IsDefinitiveMiss)
            {
                string missStatus = verification.MissStatus;

                _dbManager.UpdateRequestStatus(request.Id, missStatus, verification.Reason);
                _dbManager.LogExecution(request.Id, "WARN", "Unity", $"{source}: {verification.Reason}");

                Debug.LogWarning($"[TestCoordinator-{source}] Marked request #{request.Id} " +
                                 $"{missStatus}: {verification.Reason}");
                return true;
            }

            // 3. Nothing usable - the caller's verdict.
            string message = fallbackMessage;
            if (sawResultsFile && !string.IsNullOrEmpty(verification.Reason))
            {
                message += $" Result files were seen but rejected: {verification.Reason}";
            }

            float elapsed = anchor == DateTime.MaxValue
                ? 0f
                : (float)Math.Max(0.0, (DateTime.Now - anchor).TotalSeconds);

            // UpdateRequestResults rather than UpdateRequestStatus: it stamps CompletedAt,
            // records the elapsed time, and zeroes the counts so nothing downstream can read
            // stale numbers off a row that produced no results.
            _dbManager.UpdateRequestResults(request.Id, fallbackStatus, 0, 0, 0, 0, elapsed, message);
            _dbManager.LogExecution(request.Id, "ERROR", "Unity", $"{source}: {message}");

            Debug.LogWarning($"[TestCoordinator-{source}] Marked request #{request.Id} " +
                             $"as '{fallbackStatus}': {message}");

            string after = _dbManager.GetRequestStatus(request.Id);
            return after != null && TerminalStatuses.Contains(after);
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
        /// Attempts to recover a stuck request by parsing an existing result file.
        ///
        /// Returns false when the file cannot be attributed to this request, so the caller can
        /// fall through to its own handling rather than reporting someone else's results.
        /// </summary>
        private static bool TryRecoverFromResultFile(TestRequest request, string resultFilePath)
        {
            return TryRecoverFromResultFile(request, resultFilePath, out _);
        }

        private static bool TryRecoverFromResultFile(TestRequest request, string resultFilePath,
                                                     out TestResultVerification verification)
        {
            verification = default;

            try
            {
                // Content check first. Timestamps cannot tell one run's output from another's,
                // and this path used to read the root count attributes straight into the request -
                // which is how a request reported a different class's green results.
                verification = TestResultVerifier.Verify(resultFilePath, request);

                if (!verification.CanAdoptAsLastResort)
                {
                    TestResultVerifier.LogRejection("TestCoordinator", verification);
                    return false;
                }

                // Counts come from the matching test-case leaves, so a broader run's file
                // contributes only this request's subset.
                string status = verification.IsSynthetic ? "inconclusive" : "completed";
                string reason = verification.Match == TestResultMatch.Exact && !verification.IsSynthetic
                    ? null
                    : verification.Reason;

                _dbManager.UpdateRequestResults(
                    request.Id,
                    status,
                    verification.MatchedCases,
                    verification.Passed,
                    verification.Failed,
                    verification.Skipped + verification.Inconclusive,
                    verification.Duration,
                    reason
                );

                _dbManager.LogExecution(request.Id, "INFO", "Unity",
                    $"Recovered from domain reload: {verification.Passed}/{verification.MatchedCases} passed, " +
                    $"{verification.Failed} failed ({verification.Reason})");

                // Copy the result file to PerSpec/TestResults for consistency.
                // No gate needed there - this is its only caller and it sits behind the check above.
                CopyResultToPerSpecDirectory(resultFilePath, request);

                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[TestCoordinator] Failed to recover from result file: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Copies a recovered result file to the PerSpec/TestResults directory
        /// </summary>
        private static void CopyResultToPerSpecDirectory(string sourcePath, TestRequest request)
        {
            try
            {
                string projectPath = Directory.GetParent(Application.dataPath).FullName;
                string testResultsPath = Path.Combine(projectPath, "PerSpec", "TestResults");

                if (!Directory.Exists(testResultsPath))
                    Directory.CreateDirectory(testResultsPath);

                // Put the request's identity in the file name so a human looking at the folder
                // can tell which run each artifact came from without opening it.
                string tag = SanitizeForFileName(request.TestFilter);
                if (!string.IsNullOrEmpty(tag))
                {
                    tag = "_" + tag;
                }

                string destFileName =
                    $"TestResults_Recovered_{request.Id}_{request.RequestType}{tag}_{DateTime.Now:yyyyMMdd_HHmmss}.xml";
                string destPath = Path.Combine(testResultsPath, destFileName);

                File.Copy(sourcePath, destPath, true);
                Debug.Log($"[TestCoordinator] Copied recovered results to {destFileName}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[TestCoordinator] Could not copy result file: {ex.Message}");
            }
        }

        /// <summary>
        /// Trims a test filter down to something safe and short enough for a file name.
        /// </summary>
        private static string SanitizeForFileName(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            // The trailing segment is the useful part - the namespace is just noise here.
            int lastDot = value.LastIndexOf('.');
            string shortName = lastDot >= 0 && lastDot < value.Length - 1
                ? value.Substring(lastDot + 1)
                : value;

            var cleaned = new System.Text.StringBuilder(shortName.Length);
            foreach (char c in shortName)
            {
                cleaned.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '-');
            }

            return cleaned.ToString();
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
        
        #region Stuck-Run Watchdog

        /// <summary>
        /// Backstop that guarantees no request stays non-terminal forever. Self-throttling
        /// and idempotent, so it is safe to call from any tick source: the editor update
        /// loop (main thread, throttled when the editor is unfocused) and BackgroundPoller's
        /// threading timer (fires unfocused, marshals here) both drive it.
        /// </summary>
        internal static void TickWatchdog()
        {
            if (_dbManager == null || !_dbManager.IsInitialized) return;
            if (!EditorPrefs.GetBool(PrefWatchdogEnabled, true)) return;

            double now = EditorApplication.timeSinceStartup;
            if (now - _lastWatchdogSweepTime < WatchdogSweepIntervalSeconds) return;
            _lastWatchdogSweepTime = now;

            // A reload is imminent. RecoverInterruptedTestRequest owns that case and runs on
            // the other side with strictly better information than this sweep has.
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;

            RunWatchdogSweep();
        }

        private static void RunWatchdogSweep()
        {
            try
            {
                // Query with the smallest ceiling any request can have, then apply the exact
                // per-request ceiling in C#. Same reasoning as RecoverOrphanedRequests'
                // safety gate: sqlite-net's CreatedAt comparison against TEXT timestamps is
                // not trustworthy on its own, so the SQL filter is a pre-filter and never the
                // decision.
                var candidates = _dbManager.GetStuckRequests(TimeSpan.FromSeconds(WatchdogFloorSeconds));
                if (candidates.Count == 0) return;

                foreach (var request in candidates)
                {
                    if (_watchdogHandled.Contains(request.Id)) continue;

                    DateTime anchor = ResolveRunAnchor(request);
                    if (anchor == DateTime.MaxValue) continue;   // unknown start - never guess

                    double elapsed = (DateTime.Now - anchor).TotalSeconds;
                    double ceiling = ResolveWatchdogCeiling(request);
                    if (elapsed < ceiling) continue;

                    // Re-read immediately before acting: PlayModeTestCompletionChecker's
                    // FinalizeRequest or a Python-side cancel may have finished this row
                    // since GetStuckRequests ran.
                    string liveStatus = _dbManager.GetRequestStatus(request.Id);
                    if (liveStatus == null || TerminalStatuses.Contains(liveStatus)) continue;
                    request.Status = liveStatus;

                    bool owned = IsOwnedByThisSession(request.Id);
                    bool inPlayMode = EditorApplication.isPlaying;

                    Debug.LogWarning(
                        $"[TestCoordinator-Watchdog] Request #{request.Id} has been '{liveStatus}' " +
                        $"for {elapsed:F0}s (ceiling {ceiling:F0}s, platform {request.TestPlatform}, " +
                        $"type {request.RequestType}, isPlaying={inPlayMode}, " +
                        $"ownedByThisSession={owned}) - finalizing");

                    string why = inPlayMode
                        ? "Unity was still in PlayMode - the run never returned to EditMode, " +
                          "so PlayModeTestCompletionChecker never ran."
                        : "No in-process test monitor was alive to time this run out.";

                    bool finalized = FinalizeStuckRequest(
                        request,
                        anchor,
                        fallbackStatus: "timeout",
                        fallbackMessage:
                            $"Watchdog: no result was published within {ceiling:F0}s of dispatch " +
                            $"(elapsed {elapsed:F0}s, status '{liveStatus}', platform " +
                            $"{request.TestPlatform}). {why}",
                        source: "Watchdog");

                    if (!finalized)
                    {
                        // The write did not land - a locked database logs and returns. Leave
                        // the id unhandled so the next sweep retries it rather than
                        // abandoning the row for the rest of the session.
                        continue;
                    }

                    _watchdogHandled.Add(request.Id);

                    // Order matters: the play-mode decision reads the in-flight marker that
                    // ReleaseLocalRunIfOwned is about to erase.
                    MaybeStopPlayModeFor(request, owned);
                    ReleaseLocalRunIfOwned(request.Id);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[TestCoordinator-Watchdog] Sweep failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Stays strictly above TestExecutor's own ceilings so a live in-process monitor
        /// always gets to write the more precise result first (a synthesised XML for method
        /// runs, the rejection breadcrumb, the OnTestComplete handoff).
        /// 300 -> 600 for batch runs, 600 -> 900 for single methods.
        ///
        /// Deliberately above Python's default --timeout 300 as well: --wait giving up is a
        /// client-side decision and does not mean the run is dead.
        /// </summary>
        private static double ResolveWatchdogCeiling(TestRequest request)
        {
            int overrideSeconds = EditorPrefs.GetInt(PrefWatchdogTimeoutSeconds, 0);
            if (overrideSeconds > 0)
            {
                return Math.Max(WatchdogFloorSeconds, overrideSeconds);
            }

            double executorCeiling = request.RequestType == "method"
                ? TestExecutor.MAX_WAIT_TIME_INDIVIDUAL
                : TestExecutor.MAX_WAIT_TIME;

            return executorCeiling + WatchdogGraceSeconds;
        }

        private static bool IsOwnedByThisSession(int requestId)
        {
            return (_isRunningTests && _currentRequestId == requestId) ||
                   SessionState.GetInt(SessionKeyActiveRequestId, -1) == requestId;
        }

        /// <summary>
        /// If this editor session still believes it is running the request the watchdog just
        /// terminated, tear the local run down. Without this _isRunningTests latches on and
        /// TryDispatchNextRequest's first guard refuses every later request for the rest of
        /// the session - the run is dead AND nothing new can start.
        ///
        /// A request owned by nobody here belongs to a previous session (an editor restart)
        /// or to another editor instance. There is no owner column to check, so the database
        /// write is all we do: aborting a run this session does not own would be guesswork.
        /// </summary>
        private static void ReleaseLocalRunIfOwned(int requestId)
        {
            if (_isRunningTests && _currentRequestId == requestId)
            {
                // Mirrors CancelCurrentTest. Without Abort the file monitor and the
                // TestRunnerApi callbacks leak into the next run and compete for its files.
                _testExecutor?.Abort();
                _isRunningTests = false;
                _currentRequestId = -1;
            }

            if (SessionState.GetInt(SessionKeyActiveRequestId, -1) == requestId)
            {
                ForgetInFlightRequest();
            }
        }

        /// <summary>
        /// Opt-in only. Stopping play mode is a visible, irreversible side effect on a
        /// session the user may be driving by hand, and it buys the database nothing - the
        /// row is already terminal by the time this runs. The dangerous shape is real: a
        /// stale 'processing' row plus a user who pressed Play for unrelated debugging.
        /// Useful for unattended or CI editors, hence the pref.
        /// </summary>
        private static void MaybeStopPlayModeFor(TestRequest request, bool ownedByThisSession)
        {
            if (!EditorApplication.isPlaying) return;
            if (!ownedByThisSession) return;   // never stop a session we did not start
            if (!EditorPrefs.GetBool(PrefWatchdogStopPlayMode, false)) return;

            Debug.LogWarning($"[TestCoordinator-Watchdog] Stopping PlayMode - request " +
                             $"#{request.Id} timed out and {PrefWatchdogStopPlayMode} is enabled");
            EditorApplication.isPlaying = false;
        }

        /// <summary>
        /// Runs the sweep now, ignoring the interval and the already-handled set.
        /// Exposed for the Control Center.
        /// </summary>
        public static void ForceWatchdogSweep()
        {
            _watchdogHandled.Clear();
            RunWatchdogSweep();
            Debug.Log("[TestCoordinator-Watchdog] Manual sweep complete");
        }

        // Test facades. The watchdog's two pure decisions - how long a run is allowed and
        // when it is considered to have started - are where a false timeout would come
        // from, so they are the parts worth pinning down in tests. Facades rather than
        // reflection, per the project's testing rules.
        public static double Test_ResolveWatchdogCeiling(TestRequest request)
            => ResolveWatchdogCeiling(request);

        public static DateTime Test_ResolveRunAnchor(TestRequest request)
            => ResolveRunAnchor(request);

        #endregion

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

            // Outside the dispatch gate on purpose. It carries its own 30s accumulator (a
            // stuck-request query is far too expensive at the 1s dispatch rate), and
            // TogglePolling sets _checkInterval to 0 - which must not take the watchdog with
            // it, nor run it every frame.
            TickWatchdog();
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

        public static void ProcessTestRequest(TestRequest request)
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

            // Claim the slot BEFORE the asynchronous pre-flight below. Both the editor-update
            // poll and BackgroundPoller funnel into TryDispatchNextRequest, which gates on
            // _isRunningTests alone - an unguarded pre-flight window would dispatch the same
            // row twice.
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
                RememberInFlightRequest(request);
                SessionState.SetBool(SessionKeyPreflight, true);

                // Resolve the filter against the real test tree before anything runs. A filter
                // that selects nothing is a caller mistake and is fully detectable here;
                // entering PlayMode to discover it costs a full cycle and lands on a status
                // that cannot be told apart from a compile failure.
                TestFilterPreflight.Resolve(request, filter.testMode,
                    outcome => OnPreflightResolved(request, filter, outcome));
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

        /// <summary>
        /// Second half of <see cref="ProcessTestRequest"/>, resumed once the filter has been
        /// resolved against Unity's test tree.
        /// </summary>
        private static void OnPreflightResolved(TestRequest request, Filter filter, PreflightResult outcome)
        {
            // The world may have moved while the pre-flight was in flight: the user cancelled,
            // or a domain reload reset the statics and handed the row to
            // RecoverInterruptedTestRequest. Either way this result is stale, and somebody else
            // owns the statics now - do NOT clear them here.
            if (!_isRunningTests || _currentRequestId != request.Id)
            {
                Debug.Log($"[TestCoordinator] Discarding stale pre-flight result for request {request.Id}");
                return;
            }

            SessionState.EraseBool(SessionKeyPreflight);

            if (outcome.Verdict == PreflightVerdict.NoMatch)
            {
                WriteNoMatch(request, outcome);

                ForgetInFlightRequest();
                _isRunningTests = false;
                _currentRequestId = -1;
                return;
            }

            try
            {
                // Matched, or could not be verified. An unverifiable pre-flight must never
                // block a run - proceeding is exactly what shipped before this check existed.
                _testExecutor.ExecuteTests(request, filter, OnTestComplete);

                Debug.Log($"[TestCoordinator] Executing tests for request {request.Id} ({outcome.Reason})");
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

        /// <summary>
        /// Records a request whose filter selected nothing. No test ran, so the row gets zero
        /// counts and a message naming the filter.
        /// </summary>
        private static void WriteNoMatch(TestRequest request, PreflightResult outcome)
        {
            string message = outcome.ErrorMessage;

            // UpdateRequestResults rather than UpdateRequestStatus: it also writes the zero
            // counts and stamps completion. A 'no_match' row still carrying a previous
            // total_tests would reintroduce exactly the ambiguity this status removes.
            _dbManager.UpdateRequestResults(request.Id, "no_match", 0, 0, 0, 0, 0f, message);

            // A database whose CHECK constraint predates 'no_match' rejects that write, and
            // SQLiteManager swallows the failure - which would strand the row mid-flight,
            // strictly worse than the old behaviour. Verify, and fall back if it did not take.
            var stored = _dbManager.GetRequestById(request.Id);
            if (stored != null && stored.Status != "no_match")
            {
                Debug.LogWarning("[TestCoordinator] The database rejected status 'no_match' - run " +
                                 "PerSpec/Coordination/Scripts/db_update_status_constraint.py to update it. " +
                                 "Falling back to 'inconclusive'.");
                _dbManager.UpdateRequestResults(request.Id, "inconclusive", 0, 0, 0, 0, 0f, message);
            }

            _dbManager.LogExecution(request.Id, "ERROR", "Unity", message);
            Debug.LogError($"[TestCoordinator] Request {request.Id}: {message}");
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
        
        /// <summary>
        /// Trims PerSpec/TestResults before a dispatch, keeping the most recent runs.
        ///
        /// This used to delete everything. That destroyed the very evidence needed to tell one
        /// run's output from another's, and left the "is this file newer than anything local?"
        /// heuristic comparing against an empty folder - so any AppData XML, however old, looked
        /// like the freshest results available. Retention is safe now that every adoption path
        /// checks file contents against the request.
        /// </summary>
        private static void CleanTestResultsDirectory()
        {
            const int KeepMostRecentRuns = 5;

            try
            {
                string projectPath = Directory.GetParent(Application.dataPath).FullName;
                string testResultsPath = Path.Combine(projectPath, "PerSpec", "TestResults");

                if (!Directory.Exists(testResultsPath))
                {
                    Directory.CreateDirectory(testResultsPath);
                    Debug.Log($"[TestCoordinator] Created TestResults directory");
                    return;
                }

                // Keep the newest N XML files and whatever sits beside them (.summary.txt).
                var keep = new HashSet<string>(
                    Directory.GetFiles(testResultsPath, "*.xml")
                        .Select(f => new FileInfo(f))
                        .OrderByDescending(fi => fi.LastWriteTime)
                        .Take(KeepMostRecentRuns)
                        .SelectMany(fi => new[]
                        {
                            fi.FullName,
                            Path.ChangeExtension(fi.FullName, ".summary.txt")
                        }),
                    StringComparer.OrdinalIgnoreCase);

                int deleted = 0;

                foreach (string file in Directory.GetFiles(testResultsPath, "*", SearchOption.AllDirectories))
                {
                    if (keep.Contains(file))
                    {
                        continue;
                    }

                    try
                    {
                        File.Delete(file);
                        deleted++;
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[TestCoordinator] Failed to delete file {file}: {ex.Message}");
                    }
                }

                // Subdirectories are not part of the retention set - nothing writes into them.
                string[] directories = Directory.GetDirectories(testResultsPath, "*", SearchOption.AllDirectories);
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

                Debug.Log($"[TestCoordinator] Trimmed TestResults directory " +
                          $"(deleted {deleted}, kept the {KeepMostRecentRuns} most recent runs)");
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