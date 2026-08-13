using System;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEditor;
using PerSpec.Editor.TestExport;

namespace PerSpec.Editor.Coordination
{
    /// <summary>
    /// Monitors for PlayMode test completion after Unity exits Play mode
    /// Since EditorApplication.update doesn't run during Play mode, we need to check after
    /// </summary>
    [InitializeOnLoad]
    public static class PlayModeTestCompletionChecker
    {
        private static string _testResultsPath;
        
        static PlayModeTestCompletionChecker()
        {
            string projectPath = Directory.GetParent(Application.dataPath).FullName;
            _testResultsPath = Path.Combine(projectPath, "PerSpec", "TestResults");
            
            // Subscribe to play mode state changes
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            
            Debug.Log("[PlayModeTestCompletionChecker] Initialized");
        }
        
        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            Debug.Log($"[PlayModeTestCompletionChecker] Play mode state changed to: {state}");

            // When exiting play mode, check for test results.
            // Guard against mid-PlayMode domain reloads (scripts recompile during play),
            // which also fire EnteredEditMode but should not trigger result publishing.
            if (state == PlayModeStateChange.EnteredEditMode && !EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.Log("[PlayModeTestCompletionChecker] Exited Play mode, checking for test results...");
                CheckForCompletedTests();
            }
        }
        
        private static void CheckForCompletedTests()
        {
            try
            {
                var dbManager = new SQLiteManager();
                
                // Get all running PlayMode test requests
                var runningRequests = dbManager.GetRunningRequests()
                    .Where(r => r.TestPlatform == "PlayMode")
                    .ToList();
                
                if (runningRequests.Count == 0)
                {
                    Debug.Log("[PlayModeTestCompletionChecker] No running PlayMode tests to check");
                    return;
                }
                
                Debug.Log($"[PlayModeTestCompletionChecker] Found {runningRequests.Count} running PlayMode test(s)");
                
                // Pick the most-recently-started request to update
                var requestToUpdate = runningRequests.OrderByDescending(r => r.Id).FirstOrDefault();

                if (requestToUpdate == null)
                {
                    return;
                }

                // Only consider XML files written after this request started (minus a 5-second
                // clock-skew buffer) so we never pick up a stale file from a previous run.
                DateTime? minTime = requestToUpdate.StartedAt.HasValue
                    ? requestToUpdate.StartedAt.Value.AddSeconds(-5)
                    : (DateTime?)null;

                // Look for the latest test result file that belongs to the current run
                var latestResultFile = GetLatestResultFile(minTime, requestToUpdate);

                if (!string.IsNullOrEmpty(latestResultFile))
                {
                    Debug.Log($"[PlayModeTestCompletionChecker] Found result file: {latestResultFile}");
                    ParseXmlAndUpdateRequest(latestResultFile, requestToUpdate, dbManager);
                }
                else
                {
                    Debug.Log("[PlayModeTestCompletionChecker] No usable result files in PerSpec/TestResults, checking Unity default location...");

                    // Check Unity's default location and copy if found. That helper already
                    // routes through ParseXmlAndUpdateRequest, which is now the single path
                    // that can mark a PlayMode request terminal - the old summary-file branch
                    // here wrote 'completed' from numbers no one could verify.
                    var copiedFile = CopyFromUnityDefaultLocation(requestToUpdate);
                    if (string.IsNullOrEmpty(copiedFile))
                    {
                        Debug.LogWarning("[PlayModeTestCompletionChecker] No test results found in any location");
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[PlayModeTestCompletionChecker] Error checking for completed tests: {e.Message}");
            }
        }
        
        /// <summary>
        /// Newest result file that both post-dates the run and actually contains the run's tests.
        /// A timestamp alone cannot tell one run's output from another's, which is how a request
        /// for one class ended up reporting a different class's results.
        /// </summary>
        private static string GetLatestResultFile(DateTime? minModifiedTime, TestRequest request)
        {
            if (!Directory.Exists(_testResultsPath)) return null;

            var candidates = Directory.GetFiles(_testResultsPath, "*.xml")
                .Select(f => new FileInfo(f))
                .Where(fi => minModifiedTime == null || fi.LastWriteTime >= minModifiedTime.Value)
                .OrderByDescending(fi => fi.LastWriteTime)
                .Select(fi => fi.FullName)
                .ToList();

            // This runs at the true end of the run, so a broader run's file is better than
            // nothing - it is adopted with its matching subset only.
            string chosen = TestResultVerifier.PickBest(candidates, request, true, out var verification);

            if (chosen == null && candidates.Count > 0)
            {
                TestResultVerifier.LogRejection("PlayModeTestCompletionChecker", verification);
            }

            return chosen;
        }
        
        /// <summary>
        /// Imports Unity's own TestResults.xml when the in-process exporter did not write one
        /// (the usual case for PlayMode, where the domain reload kills the callbacks).
        ///
        /// The candidate must post-date the request AND contain the request's tests. The old
        /// flat "modified in the last 5 minutes" window was wide enough to import the previous
        /// run's results and report them as this run's.
        /// </summary>
        private static string CopyFromUnityDefaultLocation(TestRequest request)
        {
            try
            {
                string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "Low";

                // Shared candidate list so every recovery path probes the same folders.
                string[] possiblePaths = TestExecutor.GetAppDataResultCandidatePaths().ToArray();

                // Anchor freshness to the request itself rather than to wall-clock now.
                DateTime cutoff = request?.StartedAt ?? request?.CreatedAt ?? DateTime.MaxValue;
                if (cutoff != DateTime.MaxValue)
                {
                    cutoff = cutoff.AddSeconds(-5);  // clock-skew buffer
                }

                string sourceFile = null;

                foreach (var candidateFile in possiblePaths)
                {
                    if (!File.Exists(candidateFile))
                    {
                        continue;
                    }

                    var fileInfo = new FileInfo(candidateFile);
                    if (fileInfo.LastWriteTime < cutoff)
                    {
                        Debug.Log($"[PlayModeTestCompletionChecker] Ignoring stale test results at: " +
                                  $"{candidateFile} (modified {fileInfo.LastWriteTime:s}, cutoff {cutoff:s})");
                        continue;
                    }

                    var verification = TestResultVerifier.Verify(candidateFile, request);
                    if (!verification.CanAdoptAsLastResort)
                    {
                        TestResultVerifier.LogRejection("PlayModeTestCompletionChecker", verification);
                        continue;
                    }

                    sourceFile = candidateFile;
                    Debug.Log($"[PlayModeTestCompletionChecker] Found matching test results at: " +
                              $"{candidateFile} (modified {fileInfo.LastWriteTime:s})");
                    break;
                }

                if (sourceFile == null)
                {
                    Debug.Log($"[PlayModeTestCompletionChecker] No usable test results found. Searched locations:");
                    foreach (var path in possiblePaths)
                    {
                        Debug.Log($"  - {path}");
                    }
                    return null;
                }

                // Ensure TestResults directory exists
                if (!Directory.Exists(_testResultsPath))
                    Directory.CreateDirectory(_testResultsPath);

                // Copy with timestamp
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string destFile = Path.Combine(_testResultsPath, $"TestResults_{timestamp}.xml");
                File.Copy(sourceFile, destFile, true);

                Debug.Log($"[PlayModeTestCompletionChecker] Copied from {sourceFile} to {destFile}");

                try
                {
                    var dbManager = new SQLiteManager();
                    ParseXmlAndUpdateRequest(destFile, request, dbManager);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[PlayModeTestCompletionChecker] Failed to update database: {ex.Message}");
                }

                return destFile;
            }
            catch (Exception e)
            {
                Debug.LogError($"[PlayModeTestCompletionChecker] Error copying from Unity default location: {e.Message}");
                return null;
            }
        }
        
        /// <summary>
        /// The single path that can mark a PlayMode request terminal from a results file.
        ///
        /// Counts come from the matching test-case leaves, so a broader run's file reports only
        /// this request's subset, and the pre-1.9.0 double-counted root attributes are ignored.
        /// </summary>
        private static void ParseXmlAndUpdateRequest(string xmlPath, TestRequest request, SQLiteManager dbManager)
        {
            try
            {
                var verification = TestResultVerifier.Verify(xmlPath, request);

                float duration = verification.Duration;
                if (duration <= 0.1f && request.StartedAt.HasValue)
                {
                    duration = (float)(DateTime.Now - request.StartedAt.Value).TotalSeconds;
                }

                // Unity is known to write a completely empty XML for some single-method runs.
                if (verification.Match == TestResultMatch.Empty && request.RequestType == "method")
                {
                    Debug.Log($"[PlayModeTestCompletionChecker] Empty XML for individual test method - generating placeholder XML");

                    try
                    {
                        string generatedXmlPath = SingleTestXMLGenerator.GenerateInconclusiveTestXML(
                            request.TestFilter,
                            request.TestPlatform
                        );

                        Debug.Log($"[PlayModeTestCompletionChecker] Generated XML for individual test at: {generatedXmlPath}");
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[PlayModeTestCompletionChecker] Failed to generate XML: {ex.Message}");
                    }

                    FinalizeRequest(dbManager, request, "inconclusive", 1, 0, 0, 1, duration,
                        $"Individual test ran for {duration:F2}s but Unity produced no results");
                    return;
                }

                // Nothing executed, or nothing that belongs to this request. Either way this is
                // not evidence the requested tests passed - it must never be reported as green.
                if (verification.IsDefinitiveMiss)
                {
                    Debug.LogWarning($"[PlayModeTestCompletionChecker] {verification.Reason}");

                    FinalizeRequest(dbManager, request, "inconclusive", 0, 0, 0, 0, duration,
                        verification.Reason);
                    return;
                }

                if (!verification.CanAdoptAsLastResort)
                {
                    // Unreadable file - leave the request alone so a later pass can retry.
                    TestResultVerifier.LogRejection("PlayModeTestCompletionChecker", verification);
                    return;
                }

                // A generated placeholder is not the record of a real run.
                string status = verification.IsSynthetic ? "inconclusive" : "completed";
                string reason = verification.Match == TestResultMatch.Exact && !verification.IsSynthetic
                    ? null
                    : verification.Reason;

                Debug.Log($"[PlayModeTestCompletionChecker] Verified XML - Matched: {verification.MatchedCases}, " +
                          $"Passed: {verification.Passed}, Failed: {verification.Failed} ({verification.Reason})");

                FinalizeRequest(dbManager, request, status,
                    verification.MatchedCases,
                    verification.Passed,
                    verification.Failed,
                    verification.Skipped + verification.Inconclusive,
                    duration,
                    reason);
            }
            catch (Exception e)
            {
                Debug.LogError($"[PlayModeTestCompletionChecker] Error parsing XML file: {e.Message}");
            }
        }

        /// <summary>
        /// Writes the terminal status and hands the in-flight marker back to the coordinator, so
        /// its domain-reload recovery does not later mistake a finished run for an interrupted one.
        /// </summary>
        private static void FinalizeRequest(SQLiteManager dbManager, TestRequest request, string status,
                                            int total, int passed, int failed, int skipped,
                                            float duration, string reason)
        {
            dbManager.UpdateRequestResults(request.Id, status, total, passed, failed, skipped, duration, reason);

            dbManager.LogExecution(request.Id, status == "completed" ? "INFO" : "WARNING",
                "PlayModeTestCompletionChecker",
                reason ?? $"Test {status} (parsed from XML): {passed}/{total} passed");

            Debug.Log($"[PlayModeTestCompletionChecker] Request {request.Id} marked as '{status}' from XML");

            TestCoordinatorEditor.NotifyRequestFinalizedExternally(request.Id);
        }
        
        // Integrated into main coordinator - accessed via Control Center
        public static void ManualCheck()
        {
            CheckForCompletedTests();
        }
    }
}