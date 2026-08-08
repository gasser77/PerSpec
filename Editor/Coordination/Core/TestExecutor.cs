using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Xml.Linq;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEditor;
using UnityEngine.TestTools;
using UnityEditor.TestTools.TestRunner.Api;
using PerSpec.Editor.TestExport;
using TestMode = UnityEditor.TestTools.TestRunner.Api.TestMode;

namespace PerSpec.Editor.Coordination
{


    public class TestExecutor : ICallbacks
    {
        private SQLiteManager _dbManager;
        private TestRequest _currentRequest;
        private Action<TestRequest, bool, string, TestResultSummary> _onComplete;
        private TestResultSummary _currentSummary;
        private Dictionary<string, TestResult> _testResults;
        private float _startTime;
        private TestRunnerApi _testApi;
        private TestResultXMLExporter _xmlExporter;
        
        // File monitoring fields
        private string _testResultsPath;
        private string _initialResultSnapshot;
        private EditorApplication.CallbackFunction _fileMonitorCallback;
        private double _monitorStartTime;
        private double _lastFileCheckTime;
        private const double FILE_CHECK_INTERVAL = 2.0; // Check every 2 seconds
        private const double FILE_STABILITY_WAIT = 3.0; // Wait for file to stabilize
        private const double MAX_WAIT_TIME = 300.0; // 5 minute timeout for batch tests
        private const double MAX_WAIT_TIME_INDIVIDUAL = 600.0; // 10 minute timeout for individual tests
        private const double MIN_RUN_SECONDS = 3.0; // Reject "completion" that fires within this many seconds of dispatch
        private bool _isMonitoring;
        private bool _hasCompletedViaCallback;
        private bool _testsStarted;
        private int _testsCompleted;
        private int _expectedTestCount;
        private string _lastDetectedFile;
        private long _lastFileSize;
        private double _fileStableTime;
        private DateTime _monitorStartDateTime;
        
        public TestExecutor(SQLiteManager dbManager)
        {
            _dbManager = dbManager;
            _testResults = new Dictionary<string, TestResult>();
            _testApi = ScriptableObject.CreateInstance<TestRunnerApi>();

            // Without this the ScriptableObject is destroyed on scene change / reload while
            // this instance still holds the reference, so later UnregisterCallbacks/Execute
            // calls hit a fake-null object and throw.
            _testApi.hideFlags = HideFlags.HideAndDontSave;

            // Initialize test results path
            string projectPath = Directory.GetParent(Application.dataPath).FullName;
            _testResultsPath = Path.Combine(projectPath, "PerSpec", "TestResults");
        }

        /// <summary>
        /// The locations Unity itself may write TestResults.xml to, in order of likelihood.
        /// Unity writes under LocalAppDataLow\{Company}\{Product} - NOT LocalAppData\Unity\Editor.
        /// Shared so every recovery path probes the same folders.
        /// </summary>
        internal static IEnumerable<string> GetAppDataResultCandidatePaths()
        {
            string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "Low";
            string projectFolderName = Path.GetFileName(Directory.GetParent(Application.dataPath).FullName);

            yield return Path.Combine(appDataPath, Application.companyName, Application.productName, "TestResults.xml");
            yield return Path.Combine(appDataPath, "DefaultCompany", Application.productName, "TestResults.xml");
            yield return Path.Combine(appDataPath, "DefaultCompany", "TestFramework", "TestResults.xml");
            yield return Path.Combine(appDataPath, "DefaultCompany", projectFolderName, "TestResults.xml");
        }
        
        public void ExecuteTests(TestRequest request, Filter filter, Action<TestRequest, bool, string, TestResultSummary> onComplete)
        {
            // Starting a run while the editor is compiling guarantees the imminent domain
            // reload destroys it. Fail loudly here rather than leaving a half-started run.
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                const string message = "Cannot start tests while Unity is compiling or importing assets";
                Debug.LogError($"[TestExecutor] {message}");
                _dbManager.LogExecution(request.Id, "ERROR", "TestExecutor", message);
                onComplete?.Invoke(request, false, message, null);
                return;
            }

            _currentRequest = request;
            _onComplete = onComplete;
            _currentSummary = new TestResultSummary();
            _testResults.Clear();
            _startTime = Time.realtimeSinceStartup;
            _hasCompletedViaCallback = false;
            _testsStarted = false;
            _testsCompleted = 0;
            _expectedTestCount = 0;
            
            try
            {
                // Start file monitoring before test execution
                StartFileMonitoring();
                
                // Register XML exporter to save results to PerSpec/TestResults
                _xmlExporter = new TestResultXMLExporter();
                _testApi.RegisterCallbacks(_xmlExporter);
                Debug.Log($"[TestExecutor] XML Exporter registered for path: {_xmlExporter.OutputPath}");
                
                // Register callbacks
                _testApi.RegisterCallbacks(this);
                
                // Create execution settings with synchronous run for PlayMode to avoid issues
                var settings = new ExecutionSettings(filter);
                
                // For PlayMode tests, we rely heavily on file monitoring due to Unity Test Framework limitations
                if (filter.testMode == TestMode.PlayMode)
                {
                    Debug.Log($"[TestExecutor] PlayMode test detected, relying on file monitoring for completion");
                    _dbManager.LogExecution(request.Id, "INFO", "TestExecutor", "PlayMode test - using file monitoring");
                }
                
                try
                {
                    // Execute tests with the filter
                    _testApi.Execute(settings);
                    
                    Debug.Log($"[TestExecutor] Started test execution for request {request.Id} with file monitoring");
                    _dbManager.LogExecution(request.Id, "INFO", "TestExecutor", "Test execution started with file monitoring");
                    
                    // Set a delayed fallback to assume tests started if RunStarted doesn't fire
                    EditorApplication.delayCall += () => {
                        if (!_testsStarted && _isMonitoring && !_hasCompletedViaCallback)
                        {
                            Debug.LogWarning($"[TestExecutor] RunStarted callback didn't fire after delay, assuming tests started");
                            _testsStarted = true;
                            
                            // Update to executing status
                            if (_currentRequest != null)
                            {
                                _dbManager.UpdateRequestStatus(_currentRequest.Id, "executing");
                                _dbManager.LogExecution(_currentRequest.Id, "INFO", "TestExecutor", 
                                    "Test execution assumed started (callback missing)");
                            }
                        }
                    };
                }
                catch (NullReferenceException nre)
                {
                    // Known issue with PlayMode tests - rely on file monitoring
                    Debug.LogWarning($"[TestExecutor] PlayMode test execution error (expected): {nre.Message}");
                    Debug.Log($"[TestExecutor] Continuing with file monitoring for request {request.Id}");
                    _dbManager.LogExecution(request.Id, "WARNING", "TestExecutor", 
                        "PlayMode execution error - continuing with file monitoring");
                    
                    // Assume tests started even with the error
                    _testsStarted = true;
                    _dbManager.UpdateRequestStatus(request.Id, "executing");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[TestExecutor] Failed to start test execution: {e.Message}");
                _dbManager.LogExecution(request.Id, "ERROR", "TestExecutor", $"Failed to start: {e.Message}");
                
                // Don't immediately fail for PlayMode - let file monitoring try
                if (filter.testMode != TestMode.PlayMode)
                {
                    if (_onComplete != null)
                    {
                        _onComplete(_currentRequest, false, e.Message, null);
                    }
                    Cleanup();
                }
                else
                {
                    Debug.Log($"[TestExecutor] PlayMode error - continuing with file monitoring");
                }
            }
        }
        
        public void RunStarted(ITestAdaptor testsToRun)
        {
            try
            {
                Debug.Log($"[TestExecutor] Test run started via callback");
                
                if (_currentRequest != null)
                {
                    _testsStarted = true;
                    _expectedTestCount = CountTests(testsToRun);
                    _currentSummary.TotalTests = _expectedTestCount;
                    
                    // Update status to executing
                    _dbManager.UpdateRequestStatus(_currentRequest.Id, "executing");
                    _dbManager.LogExecution(_currentRequest.Id, "INFO", "TestExecutor", 
                        $"Test execution started - {_expectedTestCount} tests to run");
                    
                    Debug.Log($"[TestExecutor] Test run started - Total tests: {_expectedTestCount}");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[TestExecutor] Error in RunStarted: {e.Message}");
            }
        }
        
        public void RunFinished(ITestResultAdaptor result)
        {
            try
            {
                Debug.Log($"[TestExecutor] Test run finished via callback");
                
                // Prevent duplicate completion
                if (_hasCompletedViaCallback)
                {
                    Debug.Log($"[TestExecutor] Test already completed, skipping callback processing");
                    return;
                }
                
                if (_currentRequest != null)
                {
                    // Mark as completed via callback
                    _hasCompletedViaCallback = true;
                    
                    // Stop file monitoring since callback worked
                    StopFileMonitoring();
                    
                    // Calculate duration.
                    // Time.realtimeSinceStartup is not monotonic across editor domain
                    // reloads, which produced negative durations. Prefer wall-clock from
                    // when monitoring started and only fall back to the frame clock.
                    _currentSummary.Duration = _monitorStartDateTime != default
                        ? (float)(DateTime.Now - _monitorStartDateTime).TotalSeconds
                        : Math.Max(0f, Time.realtimeSinceStartup - _startTime);
                    
                    // Update to finalizing status
                    _dbManager.UpdateRequestStatus(_currentRequest.Id, "finalizing");
                    _dbManager.LogExecution(_currentRequest.Id, "INFO", "TestExecutor", "Finalizing test results");
                    
                    // Process all test results
                    ProcessTestResults(result);

                    // Persisting results and importing XML must never block the terminal
                    // status write below. Monitoring is already stopped and the completion
                    // flag is already set at this point, so an escape here would strand the
                    // request at 'finalizing' with no path out.
                    try
                    {
                        // Save individual test results to database
                        SaveTestResultsToDatabase();

                        // Belt-and-braces: ensure the XML actually landed in PerSpec/TestResults
                        // so the Python test_results.py viewer can see it. The XMLExporter
                        // callback usually handles this, but if it didn't fire (PlayMode
                        // reliability), copy Unity's AppData file in now.
                        EnsureResultXmlInPerSpec();
                    }
                    catch (Exception persistEx)
                    {
                        Debug.LogError($"[TestExecutor] Error persisting results (continuing to completion): {persistEx.Message}");
                        _dbManager.LogExecution(_currentRequest.Id, "ERROR", "TestExecutor",
                            $"Error persisting results: {persistEx.Message}");
                    }

                    // Now mark as fully completed
                    _dbManager.UpdateRequestStatus(_currentRequest.Id, "completed");
                    _dbManager.LogExecution(_currentRequest.Id, "INFO", "TestExecutor", 
                        $"Test execution completed: {_currentSummary.PassedTests}/{_currentSummary.TotalTests} passed");
                    
                    Debug.Log($"[TestExecutor] Test results - Passed: {_currentSummary.PassedTests}, " +
                             $"Failed: {_currentSummary.FailedTests}, Skipped: {_currentSummary.SkippedTests}");
                    
                    // Notify completion
                    if (_onComplete != null)
                    {
                        _onComplete(_currentRequest, true, null, _currentSummary);
                    }
                }
                
                Cleanup();
            }
            catch (Exception e)
            {
                Debug.LogError($"[TestExecutor] Error in RunFinished: {e.Message}");

                // Even if callback fails, try to complete via file monitoring
                if (!_hasCompletedViaCallback)
                {
                    Debug.Log($"[TestExecutor] Callback failed, relying on file monitoring");
                }
                else if (_currentRequest != null)
                {
                    // We already claimed the completion and stopped monitoring, so no other
                    // path will finish this request. Give it a terminal status rather than
                    // leaving it parked at 'finalizing' forever.
                    var liveStatus = _dbManager.GetRequestStatus(_currentRequest.Id);
                    if (liveStatus == "finalizing" || liveStatus == "executing" || liveStatus == "processing")
                    {
                        _dbManager.UpdateRequestStatus(_currentRequest.Id, "failed",
                            $"Failed while finalizing results: {e.Message}");
                        _dbManager.LogExecution(_currentRequest.Id, "ERROR", "TestExecutor",
                            $"RunFinished threw after completion was claimed: {e.Message}");
                    }

                    var interruptedRequest = _currentRequest;
                    var notify = _onComplete;
                    Cleanup();
                    notify?.Invoke(interruptedRequest, false, e.Message, null);
                }
            }
        }
        
        public void TestStarted(ITestAdaptor test)
        {
            try
            {
                if (test.Method != null)
                {
                    Debug.Log($"[TestExecutor] Test started: {test.FullName}");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[TestExecutor] Error in TestStarted: {e.Message}");
            }
        }
        
        public void TestFinished(ITestResultAdaptor result)
        {
            try
            {
                if (result.Test.Method != null)
                {
                    _testsCompleted++;
                    Debug.Log($"[TestExecutor] Test finished ({_testsCompleted}/{_expectedTestCount}): {result.Test.FullName} - {result.TestStatus}");
                    
                    // Update progress in database
                    if (_currentRequest != null && _expectedTestCount > 0)
                    {
                        float progress = (float)_testsCompleted / _expectedTestCount * 100;
                        _dbManager.LogExecution(_currentRequest.Id, "INFO", "TestExecutor", 
                            $"Progress: {_testsCompleted}/{_expectedTestCount} tests completed ({progress:F1}%)");
                    }
                    
                    // Store test result
                    _testResults[result.Test.FullName] = new TestResult
                    {
                        Name = result.Test.FullName,
                        ClassName = result.Test.Parent?.Name,
                        MethodName = result.Test.Method.Name,
                        Status = result.TestStatus,
                        Duration = (float)(result.Duration * 1000), // Convert to milliseconds
                        Message = result.Message,
                        StackTrace = result.StackTrace
                    };
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[TestExecutor] Error in TestFinished: {e.Message}");
            }
        }
        
        private void ProcessTestResults(ITestResultAdaptor result)
        {
            // Reset counters
            _currentSummary.PassedTests = 0;
            _currentSummary.FailedTests = 0;
            _currentSummary.SkippedTests = 0;
            
            // Count results recursively
            CountTestResults(result);
        }
        
        private void CountTestResults(ITestResultAdaptor result)
        {
            if (result.Test.Method != null)
            {
                // This is a test method
                switch (result.TestStatus)
                {
                    case TestStatus.Passed:
                        _currentSummary.PassedTests++;
                        break;
                    case TestStatus.Failed:
                        _currentSummary.FailedTests++;
                        break;
                    case TestStatus.Skipped:
                        _currentSummary.SkippedTests++;
                        break;
                }
            }
            
            // Process child results
            if (result.Children != null)
            {
                foreach (var child in result.Children)
                {
                    CountTestResults(child);
                }
            }
        }
        
        private int CountTests(ITestAdaptor test)
        {
            if (test.Method != null)
            {
                return 1; // This is a test method
            }
            
            int count = 0;
            if (test.Children != null)
            {
                foreach (var child in test.Children)
                {
                    count += CountTests(child);
                }
            }
            
            return count;
        }
        
        private void SaveTestResultsToDatabase()
        {
            foreach (var kvp in _testResults)
            {
                var result = kvp.Value;
                
                string resultString = result.Status switch
                {
                    TestStatus.Passed => "Passed",
                    TestStatus.Failed => "Failed",
                    TestStatus.Skipped => "Skipped",
                    _ => "Inconclusive"
                };
                
                _dbManager.InsertTestResult(
                    _currentRequest.Id,
                    result.Name,
                    result.ClassName,
                    result.MethodName,
                    resultString,
                    result.Duration,
                    result.Message,
                    result.StackTrace
                );
            }
        }
        
        private void Cleanup()
        {
            // Stop file monitoring
            StopFileMonitoring();
            
            // Unregister callbacks
            if (_testApi != null)
            {
                _testApi.UnregisterCallbacks(this);
                
                // Unregister XML exporter
                if (_xmlExporter != null)
                {
                    _testApi.UnregisterCallbacks(_xmlExporter);
                    _xmlExporter = null;
                }
            }
            
            _currentRequest = null;
            _onComplete = null;
            _testResults.Clear();
            _hasCompletedViaCallback = false;
        }
        
        #region File Monitoring Methods
        
        private void StartFileMonitoring()
        {
            if (_isMonitoring) 
            {
                Debug.Log($"[TestExecutor-FM] Already monitoring, skipping start");
                return;
            }
            
            _isMonitoring = true;
            _hasCompletedViaCallback = false;
            _monitorStartTime = EditorApplication.timeSinceStartup;
            _lastFileCheckTime = _monitorStartTime;
            _monitorStartDateTime = DateTime.Now;

            Debug.Log($"[TestExecutor-FM-DEBUG] === START FILE MONITORING ===");
            Debug.Log($"[TestExecutor-FM-DEBUG] Request ID: {_currentRequest?.Id}");
            Debug.Log($"[TestExecutor-FM-DEBUG] Request Type: {_currentRequest?.RequestType}");
            Debug.Log($"[TestExecutor-FM-DEBUG] Test Platform: {_currentRequest?.TestPlatform}");
            Debug.Log($"[TestExecutor-FM-DEBUG] _monitorStartDateTime set to: {_monitorStartDateTime:O}");
            Debug.Log($"[TestExecutor-FM-DEBUG] Cutoff time will be: {_monitorStartDateTime:O}");

            // Take snapshot of current files (forInitialSnapshot=true prevents completion logic from running)
            _initialResultSnapshot = GetLatestResultFile(forInitialSnapshot: true);
            Debug.Log($"[TestExecutor-FM] Initial snapshot: {_initialResultSnapshot ?? "NULL"}");
            
            // Set up monitoring callback
            _fileMonitorCallback = MonitorResultFiles;
            EditorApplication.update += _fileMonitorCallback;
            
            Debug.Log($"[TestExecutor-FM] Started file monitoring for request {_currentRequest?.Id}");
            Debug.Log($"[TestExecutor-FM] Monitor start time: {_monitorStartTime:F2}");
            Debug.Log($"[TestExecutor-FM] EditorApplication.update callback registered: {_fileMonitorCallback != null}");
        }
        
        private void StopFileMonitoring()
        {
            if (!_isMonitoring) return;
            
            _isMonitoring = false;
            
            if (_fileMonitorCallback != null)
            {
                EditorApplication.update -= _fileMonitorCallback;
                _fileMonitorCallback = null;
            }
            
            Debug.Log($"[TestExecutor] Stopped file monitoring");
        }
        
        private void MonitorResultFiles()
        {
            if (!_isMonitoring || _hasCompletedViaCallback) 
            {
                // Silently skip - this gets called every frame when complete
                return;
            }
            
            double currentTime = EditorApplication.timeSinceStartup;
            
            // Check for timeout - use longer timeout for individual tests
            double timeoutValue = (_currentRequest != null && _currentRequest.RequestType == "method") 
                ? MAX_WAIT_TIME_INDIVIDUAL 
                : MAX_WAIT_TIME;
            
            if (currentTime - _monitorStartTime > timeoutValue)
            {
                Debug.LogError($"[TestExecutor-FM] Test execution timed out after {timeoutValue} seconds");
                HandleTestTimeout();
                return;
            }
            
            // Check for new files periodically
            if (currentTime - _lastFileCheckTime >= FILE_CHECK_INTERVAL)
            {
                Debug.Log($"[TestExecutor-FM] File check triggered at {currentTime:F2} (interval: {FILE_CHECK_INTERVAL}s)");
                _lastFileCheckTime = currentTime;
                CheckForNewResultFiles();
            }
        }
        
        private void CheckForNewResultFiles()
        {
            Debug.Log($"[TestExecutor-FM] Checking for new files in: {_testResultsPath}");
            
            if (!Directory.Exists(_testResultsPath)) 
            {
                Debug.LogWarning($"[TestExecutor-FM] TestResults directory doesn't exist: {_testResultsPath}");
                return;
            }
            
            string latestFile = GetLatestResultFile(forInitialSnapshot: false);
            Debug.Log($"[TestExecutor-FM] Latest file: {latestFile ?? "NULL"}");
            Debug.Log($"[TestExecutor-FM] Initial snapshot: {_initialResultSnapshot ?? "NULL"}");
            
            // Check if a new file appeared or existing file changed
            if (!string.IsNullOrEmpty(latestFile) && latestFile != _initialResultSnapshot)
            {
                var fileInfo = new FileInfo(latestFile);
                long currentSize = fileInfo.Exists ? fileInfo.Length : 0;
                
                // Check if this is a new file or if the size has changed
                if (latestFile != _lastDetectedFile || currentSize != _lastFileSize)
                {
                    Debug.Log($"[TestExecutor-FM] File change detected: {latestFile} (size: {currentSize})");
                    _lastDetectedFile = latestFile;
                    _lastFileSize = currentSize;
                    _fileStableTime = EditorApplication.timeSinceStartup;
                }
                else if ((EditorApplication.timeSinceStartup - _fileStableTime) >= FILE_STABILITY_WAIT)
                {
                    // File has been stable for required time
                    Debug.Log($"[TestExecutor-FM] File stable for {FILE_STABILITY_WAIT}s, checking if complete");
                    
                    // Process if we haven't completed
                    // Allow processing even if _testsStarted is false (callback might not have fired)
                    if (_isMonitoring && !_hasCompletedViaCallback)
                    {
                        if (!_testsStarted)
                        {
                            Debug.LogWarning($"[TestExecutor-FM] Processing result file even though RunStarted didn't fire");
                            _testsStarted = true; // Assume tests ran if we have a result file
                            
                            // Update status if needed
                            if (_currentRequest != null)
                            {
                                var currentStatus = _dbManager.GetRequestStatus(_currentRequest.Id);
                                if (currentStatus == "processing")
                                {
                                    _dbManager.UpdateRequestStatus(_currentRequest.Id, "executing");
                                    _dbManager.LogExecution(_currentRequest.Id, "INFO", "TestExecutor",
                                        "Test execution detected via file monitoring");
                                }
                            }
                        }
                        CheckAndProcessResultFile(latestFile);
                    }
                }
            }
            else
            {
                Debug.Log($"[TestExecutor-FM] No new file detected");
            }
        }
        
        private string GetLatestResultFile(bool forInitialSnapshot = false)
        {
            Debug.Log($"[TestExecutor-FM-DEBUG] === GetLatestResultFile() CALLED ===");
            Debug.Log($"[TestExecutor-FM-DEBUG] forInitialSnapshot: {forInitialSnapshot}");
            Debug.Log($"[TestExecutor-FM-DEBUG] _monitorStartDateTime: {_monitorStartDateTime:O}");
            Debug.Log($"[TestExecutor-FM-DEBUG] _monitorStartDateTime == default: {_monitorStartDateTime == default}");

            // First check PerSpec/TestResults directory
            if (Directory.Exists(_testResultsPath))
            {
                // Use _monitorStartDateTime for freshness check (set at START of monitoring)
                // For initial snapshot: no buffer (capture state at monitoring start)
                // For monitoring: allow 5 second backward buffer for clock skew
                DateTime cutoffTime = _monitorStartDateTime != default
                    ? (forInitialSnapshot ? _monitorStartDateTime : _monitorStartDateTime.AddSeconds(-5))
                    : DateTime.Now.AddMinutes(-5);  // Fallback: only use files from last 5 minutes

                Debug.Log($"[TestExecutor-FM-DEBUG] PerSpec/TestResults cutoff: {cutoffTime:O}");

                var allXmlFiles = Directory.GetFiles(_testResultsPath, "*.xml");
                Debug.Log($"[TestExecutor-FM-DEBUG] Found {allXmlFiles.Length} XML files in PerSpec/TestResults");

                foreach (var f in allXmlFiles)
                {
                    var fi = new FileInfo(f);
                    bool passesFilter = fi.LastWriteTime >= cutoffTime;
                    Debug.Log($"[TestExecutor-FM-DEBUG]   File: {Path.GetFileName(f)}, Written: {fi.LastWriteTime:O}, PassesFreshness: {passesFilter}");
                }

                var xmlFiles = allXmlFiles
                    .Select(f => new FileInfo(f))
                    .Where(fi => fi.LastWriteTime >= cutoffTime)
                    .OrderByDescending(fi => fi.LastWriteTime)
                    .Select(fi => fi.FullName)
                    .FirstOrDefault();

                if (!string.IsNullOrEmpty(xmlFiles))
                {
                    Debug.Log($"[TestExecutor-FM-DEBUG] RETURNING PerSpec file: {xmlFiles}");
                    return xmlFiles;
                }
                else
                {
                    Debug.Log($"[TestExecutor-FM-DEBUG] No fresh files in PerSpec/TestResults, checking AppData...");
                }
            }
            
            // Fallback: Check Unity's default location in user's AppData
            try
            {
                // Shared candidate list - see GetAppDataResultCandidatePaths
                string[] possiblePaths = GetAppDataResultCandidatePaths().ToArray();

                // Try each possible path
                foreach (var testResultFile in possiblePaths)
                {
                    if (!File.Exists(testResultFile))
                    {
                        continue;
                    }

                    Debug.Log($"[TestExecutor] Found test results at: {testResultFile}");
                    Debug.Log($"[TestExecutor] Company: {Application.companyName}, Product: {Application.productName}");

                    // Guard: skip stale AppData files written before monitoring started.
                    // Use _monitorStartDateTime which is set at the START of monitoring,
                    // before StartedAt is available. This prevents previous run's results
                    // from being treated as the current run's results.
                    // For initial snapshot: no buffer (capture state at monitoring start)
                    // For monitoring: allow 5 second backward buffer for clock skew
                    DateTime cutoffTime = _monitorStartDateTime != default
                        ? (forInitialSnapshot ? _monitorStartDateTime : _monitorStartDateTime.AddSeconds(-5))
                        : DateTime.Now.AddMinutes(-5);  // Fallback: only use files from last 5 minutes

                    Debug.Log($"[TestExecutor-FM-DEBUG] === AppData Check ===");
                    var sourceInfo = new FileInfo(testResultFile);
                    Debug.Log($"[TestExecutor-FM-DEBUG] Found AppData file: {testResultFile}");
                    Debug.Log($"[TestExecutor-FM-DEBUG] AppData file LastWriteTime: {sourceInfo.LastWriteTime:O}");
                    Debug.Log($"[TestExecutor-FM-DEBUG] AppData cutoff time: {cutoffTime:O}");
                    Debug.Log($"[TestExecutor-FM-DEBUG] File passes freshness: {sourceInfo.LastWriteTime >= cutoffTime}");

                    if (sourceInfo.LastWriteTime < cutoffTime)
                    {
                        Debug.Log($"[TestExecutor] Skipping stale AppData results: " +
                                  $"file written {sourceInfo.LastWriteTime:s}, " +
                                  $"cutoff time {cutoffTime:s}");
                        continue;
                    }

                    // Copy to PerSpec/TestResults for consistency
                    if (!Directory.Exists(_testResultsPath))
                        Directory.CreateDirectory(_testResultsPath);

                    string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    string destPath = Path.Combine(_testResultsPath, $"TestResults_{timestamp}.xml");
                    File.Copy(testResultFile, destPath, true);
                    Debug.Log($"[TestExecutor] Copied test results to: {destPath}");

                    // We have a fresh AppData XML. The canonical completion path is
                    // RunFinished. Do NOT mark the request 'completed' here unless
                    // RunFinished failed to fire AND the file is genuinely done.
                    //
                    // The previous code marked EditMode/method requests completed the
                    // instant any fresh file appeared - racing the actual test run and
                    // surfacing as "Request N not found" / empty results on the Python side.
                    if (!forInitialSnapshot && _currentRequest != null && !_hasCompletedViaCallback)
                    {
                        // Upgrade processing -> executing once we know Unity is writing files.
                        var liveStatus = _dbManager.GetRequestStatus(_currentRequest.Id);
                        if (liveStatus == "processing" || liveStatus == "pending")
                        {
                            _dbManager.UpdateRequestStatus(_currentRequest.Id, "executing");
                        }

                        // CheckAndProcessResultFile (called from the PerSpec branch in
                        // CheckForNewResultFiles) is the single completion path. Letting
                        // the regular monitoring loop pick up the freshly-copied destPath
                        // means it will be subjected to size-stability + IsXmlComplete
                        // checks before the request is marked completed.
                        Debug.Log($"[TestExecutor-FM] Copied AppData XML to {destPath} - letting standard monitoring drive completion");
                    }

                    return destPath;
                }

                Debug.Log($"[TestExecutor] No test results found in any default locations. Searched:");
                foreach (var path in possiblePaths)
                {
                    Debug.Log($"  - {path}");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TestExecutor] Error checking Unity default locations: {e.Message}");
            }
            
            return null;
        }
        
        private void CheckAndProcessResultFile(string xmlPath)
        {
            try
            {
                Debug.Log($"[TestExecutor-FM] Processing result file: {xmlPath}");

                // Reject completions that fire suspiciously early - this is the symptom
                // we observed where a method-level run's row flipped to 'completed' before
                // Unity had actually executed the test.
                double elapsed = EditorApplication.timeSinceStartup - _monitorStartTime;
                if (elapsed < MIN_RUN_SECONDS)
                {
                    Debug.Log($"[TestExecutor-FM] Only {elapsed:F2}s elapsed since dispatch (< {MIN_RUN_SECONDS}s), holding off completion");
                    return;
                }

                // First validate the XML is complete
                if (!IsXmlComplete(xmlPath))
                {
                    Debug.Log($"[TestExecutor-FM] XML file not complete yet, continuing to monitor");
                    return;
                }

                // Reject XML files written before this run started.
                if (_monitorStartDateTime != default)
                {
                    DateTime fileWriteTime = new FileInfo(xmlPath).LastWriteTime;
                    if (fileWriteTime < _monitorStartDateTime.AddSeconds(-5))
                    {
                        Debug.LogWarning($"[TestExecutor-FM] XML at {xmlPath} predates this run (written {fileWriteTime:O}, monitor started {_monitorStartDateTime:O}) - ignoring");
                        return;
                    }
                }

                // Look for corresponding summary file
                string summaryPath = xmlPath.Replace(".xml", ".summary.txt");

                if (File.Exists(summaryPath))
                {
                    ParseSummaryFile(summaryPath);
                }
                else if (File.Exists(xmlPath))
                {
                    ParseXmlFile(xmlPath);
                }

                // Validate results match expectations
                if (_expectedTestCount > 0 && _currentSummary.TotalTests != _expectedTestCount)
                {
                    Debug.LogWarning($"[TestExecutor-FM] Test count mismatch - Expected: {_expectedTestCount}, Found: {_currentSummary.TotalTests}");
                    // For PlayMode, this might be okay as we rely on file monitoring
                    if (_currentRequest?.TestPlatform != "PlayMode")
                    {
                        return; // Don't complete yet
                    }
                }

                // Mark as completed via file monitoring
                Debug.Log($"[TestExecutor] Test results validated and parsed from file for request {_currentRequest?.Id}");
                Debug.Log($"[TestExecutor] Summary - Total: {_currentSummary.TotalTests}, Passed: {_currentSummary.PassedTests}, Failed: {_currentSummary.FailedTests}");

                if (_currentRequest != null && _onComplete != null && !_hasCompletedViaCallback)
                {
                    // Choose terminal status: 'inconclusive' when a method-level run produced
                    // no meaningful results (everything skipped); 'completed' otherwise.
                    string terminalStatus = "completed";
                    if (_currentRequest.RequestType == "method"
                        && _currentSummary.TotalTests > 0
                        && _currentSummary.PassedTests == 0
                        && _currentSummary.FailedTests == 0
                        && _currentSummary.SkippedTests == _currentSummary.TotalTests)
                    {
                        terminalStatus = "inconclusive";
                    }

                    // Update status to finalizing
                    _dbManager.UpdateRequestStatus(_currentRequest.Id, "finalizing");
                    _dbManager.LogExecution(_currentRequest.Id, "INFO", "TestExecutor", "Finalizing test results from XML file");

                    // Save results to database
                    SaveTestResultsToDatabase();

                    // Persist counts + duration + terminal status in one update.
                    float duration = _currentSummary.Duration;
                    if (duration <= 0.1f && _currentRequest.StartedAt.HasValue)
                    {
                        duration = (float)(DateTime.Now - _currentRequest.StartedAt.Value).TotalSeconds;
                    }
                    _dbManager.UpdateRequestResults(
                        _currentRequest.Id,
                        terminalStatus,
                        _currentSummary.TotalTests,
                        _currentSummary.PassedTests,
                        _currentSummary.FailedTests,
                        _currentSummary.SkippedTests,
                        duration
                    );
                    _dbManager.LogExecution(_currentRequest.Id, "INFO", "TestExecutor",
                        $"Test execution {terminalStatus} via file monitoring: {_currentSummary.PassedTests}/{_currentSummary.TotalTests} passed");

                    Debug.Log($"[TestExecutor] Database status updated to '{terminalStatus}' for request {_currentRequest.Id}");

                    _hasCompletedViaCallback = true;

                    var completedRequest = _currentRequest;
                    var notify = _onComplete;
                    var summary = _currentSummary;

                    notify(completedRequest, true, null, summary);

                    // Full teardown, not just StopFileMonitoring. A later RunFinished will
                    // early-return on the completion guard and never reach its own Cleanup,
                    // so without this the TestRunnerApi callbacks (including the XML exporter)
                    // stay registered and pile up on every subsequent run.
                    Cleanup();
                }
                else
                {
                    Debug.LogWarning($"[TestExecutor] Unable to complete request - Request: {_currentRequest != null}, OnComplete: {_onComplete != null}, AlreadyCompleted: {_hasCompletedViaCallback}");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[TestExecutor] Error processing result file: {e.Message}");
            }
        }
        
        private void EnsureResultXmlInPerSpec()
        {
            try
            {
                if (string.IsNullOrEmpty(_testResultsPath))
                {
                    return;
                }
                if (!Directory.Exists(_testResultsPath))
                {
                    Directory.CreateDirectory(_testResultsPath);
                }

                // Has the XMLExporter callback (or earlier monitoring) already written a
                // result file newer than this run's start? If so we're done.
                DateTime cutoff = _monitorStartDateTime != default
                    ? _monitorStartDateTime.AddSeconds(-5)
                    : DateTime.Now.AddMinutes(-5);
                bool freshExists = Directory.GetFiles(_testResultsPath, "TestResults_*.xml")
                    .Select(f => new FileInfo(f))
                    .Any(fi => fi.LastWriteTime >= cutoff);
                if (freshExists)
                {
                    return;
                }

                // Walk Unity's AppData fallback locations and copy in the first match.
                foreach (var source in GetAppDataResultCandidatePaths())
                {
                    if (!File.Exists(source)) continue;
                    var sourceInfo = new FileInfo(source);
                    if (sourceInfo.LastWriteTime < cutoff) continue;

                    string timestamp = sourceInfo.LastWriteTime.ToString("yyyyMMdd_HHmmss");
                    string dest = Path.Combine(_testResultsPath, $"TestResults_{timestamp}.xml");
                    File.Copy(source, dest, true);
                    Debug.Log($"[TestExecutor] EnsureResultXmlInPerSpec: imported {source} -> {dest}");
                    return;
                }

                Debug.LogWarning("[TestExecutor] EnsureResultXmlInPerSpec: no fresh AppData TestResults.xml found");
            }
            catch (Exception e)
            {
                Debug.LogError($"[TestExecutor] EnsureResultXmlInPerSpec failed: {e.Message}");
            }
        }

        private bool IsXmlComplete(string xmlPath)
        {
            try
            {
                // Check if file can be read and parsed
                var doc = XDocument.Load(xmlPath);
                var root = doc.Root;
                
                if (root == null) return false;
                
                // Check for essential attributes that indicate completion
                var total = root.Attribute("total")?.Value;
                var duration = root.Attribute("duration")?.Value;
                
                // Must have total count and duration to be considered complete
                if (string.IsNullOrEmpty(total) || string.IsNullOrEmpty(duration))
                {
                    return false;
                }
                
                // Check if all test results are present
                int totalCount = int.Parse(total);
                int resultCount = root.Descendants("test-case").Count();
                
                // For empty test runs (no tests found), this is still complete
                if (totalCount == 0 && resultCount == 0)
                {
                    return true;
                }
                
                // Otherwise, result count should match total
                return resultCount >= totalCount;
            }
            catch (Exception)
            {
                // File might still be writing or corrupted
                return false;
            }
        }
        
        private void ParseSummaryFile(string summaryPath)
        {
            var lines = File.ReadAllLines(summaryPath);
            
            foreach (var line in lines)
            {
                if (line.Contains("Total Tests:"))
                {
                    if (int.TryParse(Regex.Match(line, @"\d+").Value, out int totalTests))
                        _currentSummary.TotalTests = totalTests;
                }
                else if (line.Contains("Passed:"))
                {
                    if (int.TryParse(Regex.Match(line, @"\d+").Value, out int passedTests))
                        _currentSummary.PassedTests = passedTests;
                }
                else if (line.Contains("Failed:"))
                {
                    if (int.TryParse(Regex.Match(line, @"\d+").Value, out int failedTests))
                        _currentSummary.FailedTests = failedTests;
                }
                else if (line.Contains("Skipped:"))
                {
                    if (int.TryParse(Regex.Match(line, @"\d+").Value, out int skippedTests))
                        _currentSummary.SkippedTests = skippedTests;
                }
                else if (line.Contains("Duration:"))
                {
                    var match = Regex.Match(line, @"[\d.]+");
                    if (match.Success)
                    {
                        if (float.TryParse(match.Value, out float duration))
                            _currentSummary.Duration = duration;
                    }
                }
            }
            
            Debug.Log($"[TestExecutor] Parsed summary - Total: {_currentSummary.TotalTests}, " +
                     $"Passed: {_currentSummary.PassedTests}, Failed: {_currentSummary.FailedTests}");
        }
        
        private void ParseXmlFile(string xmlPath)
        {
            try
            {
                var doc = XDocument.Load(xmlPath);
                var testRun = doc.Root;
                
                if (testRun != null)
                {
                    _currentSummary.TotalTests = int.Parse(testRun.Attribute("total")?.Value ?? "0");
                    _currentSummary.PassedTests = int.Parse(testRun.Attribute("passed")?.Value ?? "0");
                    _currentSummary.FailedTests = int.Parse(testRun.Attribute("failed")?.Value ?? "0");
                    _currentSummary.SkippedTests = int.Parse(testRun.Attribute("skipped")?.Value ?? "0");
                    
                    var duration = testRun.Attribute("duration")?.Value;
                    if (!string.IsNullOrEmpty(duration))
                    {
                        if (float.TryParse(duration, out float durationValue))
                            _currentSummary.Duration = durationValue;
                    }
                    
                    // Check for empty results from individual test execution
                    if (_currentSummary.TotalTests == 0 && _currentRequest != null && _currentRequest.RequestType == "method")
                    {
                        Debug.LogWarning($"[TestExecutor] Empty XML for individual test method - generating proper XML");
                        
                        // Generate a proper XML file for the individual test
                        try
                        {
                            string generatedXmlPath = TestExport.SingleTestXMLGenerator.GenerateInconclusiveTestXML(
                                _currentRequest.TestFilter,
                                _currentRequest.TestPlatform
                            );
                            
                            Debug.Log($"[TestExecutor] Generated proper XML for individual test at: {generatedXmlPath}");
                        }
                        catch (Exception ex)
                        {
                            Debug.LogError($"[TestExecutor] Failed to generate XML: {ex.Message}");
                        }
                        
                        // Set minimal results to indicate the test ran but results are unavailable
                        _currentSummary.TotalTests = 1;
                        _currentSummary.SkippedTests = 1;  // Mark as inconclusive
                    }
                }
                
                Debug.Log($"[TestExecutor] Parsed XML - Total: {_currentSummary.TotalTests}, " +
                         $"Passed: {_currentSummary.PassedTests}, Failed: {_currentSummary.FailedTests}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[TestExecutor] Error parsing XML: {e.Message}");
            }
        }
        
        private void HandleTestTimeout()
        {
            StopFileMonitoring();
            
            if (_currentRequest != null && _onComplete != null && !_hasCompletedViaCallback)
            {
                _hasCompletedViaCallback = true;
                
                // Determine which timeout value was used
                double timeoutValue = (_currentRequest.RequestType == "method") 
                    ? MAX_WAIT_TIME_INDIVIDUAL 
                    : MAX_WAIT_TIME;
                
                // Generate timeout XML for individual tests
                if (_currentRequest.RequestType == "method")
                {
                    try
                    {
                        string xmlPath = TestExport.SingleTestXMLGenerator.GenerateTestXML(
                            _currentRequest.TestFilter,
                            false,  // Test did not pass
                            _currentRequest.TestPlatform,
                            "Test execution timed out",
                            $"The test did not complete within {timeoutValue} seconds. This may indicate the test is stuck or taking longer than expected.",
                            (float)timeoutValue
                        );
                        Debug.Log($"[TestExecutor] Generated timeout XML for individual test at: {xmlPath}");
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[TestExecutor] Failed to generate timeout XML: {ex.Message}");
                    }
                }
                
                // Update database with timeout status
                _dbManager.UpdateRequestResults(
                    _currentRequest.Id,
                    "timeout",  // Use "timeout" status instead of "failed"
                    1,          // Assume 1 test for individual tests
                    0,          // No passed tests
                    1,          // Mark as failed due to timeout
                    0,          // No skipped tests
                    (float)timeoutValue
                );
                
                _dbManager.LogExecution(_currentRequest.Id, "ERROR", "TestExecutor",
                    $"Test execution timed out after {timeoutValue} seconds");

                var timedOutRequest = _currentRequest;
                var notify = _onComplete;

                notify(timedOutRequest, false, $"Test execution timed out after {timeoutValue} seconds", null);

                // Release the TestRunnerApi callbacks - nothing else will do it on this path.
                Cleanup();
            }
        }

        /// <summary>
        /// Tears down an in-flight run without reporting a result. Used when the run is
        /// cancelled externally, so the monitor and TestRunnerApi callbacks do not leak
        /// into the next run.
        /// </summary>
        internal void Abort()
        {
            try
            {
                _hasCompletedViaCallback = true;
                Cleanup();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TestExecutor] Error aborting run: {e.Message}");
            }
        }
        
        #endregion
        
        private class TestResult
        {
            public string Name { get; set; }
            public string ClassName { get; set; }
            public string MethodName { get; set; }
            public TestStatus Status { get; set; }
            public float Duration { get; set; }
            public string Message { get; set; }
            public string StackTrace { get; set; }
        }
    }
}