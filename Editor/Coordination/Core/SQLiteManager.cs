using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using SQLite;

namespace PerSpec.Editor.Coordination
{
    [Table("test_requests")]
    public class TestRequest
    {
        [PrimaryKey, AutoIncrement, Column("id")]
        public int Id { get; set; }
        
        [Column("request_type")]
        public string RequestType { get; set; }
        
        [Column("test_filter")]
        public string TestFilter { get; set; }
        
        [Column("test_platform")]
        public string TestPlatform { get; set; }
        
        [Column("status")]
        public string Status { get; set; }
        
        [Column("priority")]
        public int Priority { get; set; }
        
        [Column("created_at")]
        public DateTime CreatedAt { get; set; }
        
        [Column("started_at")]
        public DateTime? StartedAt { get; set; }
        
        [Column("completed_at")]
        public DateTime? CompletedAt { get; set; }
        
        [Column("error_message")]
        public string ErrorMessage { get; set; }
        
        [Column("total_tests")]
        public int TotalTests { get; set; }
        
        [Column("passed_tests")]
        public int PassedTests { get; set; }
        
        [Column("failed_tests")]
        public int FailedTests { get; set; }
        
        [Column("skipped_tests")]
        public int SkippedTests { get; set; }
        
        [Column("duration_seconds")]
        public float DurationSeconds { get; set; }
    }
    
    [Table("test_results")]
    public class TestResultRecord
    {
        [PrimaryKey, AutoIncrement, Column("id")]
        public int Id { get; set; }
        
        [Column("request_id")]
        public int RequestId { get; set; }
        
        [Column("test_name")]
        public string TestName { get; set; }
        
        [Column("test_class")]
        public string TestClass { get; set; }
        
        [Column("test_method")]
        public string TestMethod { get; set; }
        
        [Column("result")]
        public string Result { get; set; }
        
        [Column("duration_ms")]
        public float DurationMs { get; set; }
        
        [Column("error_message")]
        public string ErrorMessage { get; set; }
        
        [Column("stack_trace")]
        public string StackTrace { get; set; }
        
        [Column("created_at")]
        public DateTime CreatedAt { get; set; }
    }
    
    [Table("execution_log")]
    public class ExecutionLog
    {
        [PrimaryKey, AutoIncrement, Column("id")]
        public int Id { get; set; }
        
        [Column("request_id")]
        public int? RequestId { get; set; }
        
        [Column("log_level")]
        public string LogLevel { get; set; }
        
        [Column("source")]
        public string Source { get; set; }
        
        [Column("message")]
        public string Message { get; set; }
        
        [Column("created_at")]
        public DateTime CreatedAt { get; set; }
    }
    
    [Table("system_status")]
    public class SystemStatus
    {
        [PrimaryKey, AutoIncrement, Column("id")]
        public int Id { get; set; }
        
        [Column("component")]
        public string Component { get; set; }
        
        [Column("status")]
        public string Status { get; set; }
        
        [Column("last_heartbeat")]
        public DateTime LastHeartbeat { get; set; }
        
        [Column("message")]
        public string Message { get; set; }
        
        [Column("metadata")]
        public string Metadata { get; set; }
    }
    
    public class TestResultSummary
    {
        public int TotalTests { get; set; }
        public int PassedTests { get; set; }
        public int FailedTests { get; set; }
        public int SkippedTests { get; set; }
        public float Duration { get; set; }
    }
    
    [Table("asset_refresh_requests")]
    public class AssetRefreshRequest
    {
        [PrimaryKey, AutoIncrement, Column("id")]
        public int Id { get; set; }
        
        [Column("refresh_type")]
        public string RefreshType { get; set; }
        
        [Column("paths")]
        public string Paths { get; set; }
        
        [Column("import_options")]
        public string ImportOptions { get; set; }
        
        [Column("status")]
        public string Status { get; set; }
        
        [Column("priority")]
        public int Priority { get; set; }
        
        [Column("created_at")]
        public DateTime CreatedAt { get; set; }
        
        [Column("started_at")]
        public DateTime? StartedAt { get; set; }
        
        [Column("completed_at")]
        public DateTime? CompletedAt { get; set; }
        
        [Column("duration_seconds")]
        public float DurationSeconds { get; set; }
        
        [Column("result_message")]
        public string ResultMessage { get; set; }
        
        [Column("error_message")]
        public string ErrorMessage { get; set; }
    }
    
    [Table("menu_item_requests")]
    public class MenuItemRequest
    {
        [PrimaryKey, AutoIncrement, Column("id")]
        public int Id { get; set; }

        [Column("menu_path")]
        public string MenuPath { get; set; }

        [Column("status")]
        public string Status { get; set; }

        [Column("priority")]
        public int Priority { get; set; }

        [Column("created_at")]
        public DateTime CreatedAt { get; set; }

        [Column("started_at")]
        public DateTime? StartedAt { get; set; }

        [Column("completed_at")]
        public DateTime? CompletedAt { get; set; }

        [Column("duration_seconds")]
        public float DurationSeconds { get; set; }

        [Column("result")]
        public string Result { get; set; }

        [Column("error_message")]
        public string ErrorMessage { get; set; }
    }

    [Table("scene_hierarchy_requests")]
    public class SceneHierarchyRequest
    {
        [PrimaryKey, AutoIncrement, Column("id")]
        public int Id { get; set; }

        [Column("request_type")]
        public string RequestType { get; set; }

        [Column("target_path")]
        public string TargetPath { get; set; }

        [Column("include_inactive")]
        public bool IncludeInactive { get; set; }

        [Column("include_components")]
        public bool IncludeComponents { get; set; }

        [Column("status")]
        public string Status { get; set; }

        [Column("priority")]
        public int Priority { get; set; }

        [Column("created_at")]
        public DateTime CreatedAt { get; set; }

        [Column("started_at")]
        public DateTime? StartedAt { get; set; }

        [Column("completed_at")]
        public DateTime? CompletedAt { get; set; }

        [Column("output_file")]
        public string OutputFile { get; set; }

        [Column("error_message")]
        public string ErrorMessage { get; set; }
    }

    public class SQLiteManager
    {
        private readonly string _dbPath;
        private readonly SQLiteConnection _connection;
        private readonly bool _isInitialized;
        
        public bool IsInitialized => _isInitialized;
        
        public static bool IsPerSpecInitialized()
        {
            string projectPath = Directory.GetParent(Application.dataPath).FullName;
            string perspecPath = Path.Combine(projectPath, "PerSpec");
            return Directory.Exists(perspecPath);
        }
        
        public SQLiteManager()
        {
            // Get path to PerSpec folder in project root
            string projectPath = Directory.GetParent(Application.dataPath).FullName;
            string perspecPath = Path.Combine(projectPath, "PerSpec");
            _dbPath = Path.Combine(perspecPath, "test_coordination.db");
            
            // Check if PerSpec is initialized
            if (!Directory.Exists(perspecPath))
            {
                _isInitialized = false;
                return;
            }
            
            // Try to create database if it doesn't exist
            if (!File.Exists(_dbPath))
            {
                // Attempt to initialize the database
                if (!DatabaseInitializer.EnsureDatabaseExists())
                {
                    _isInitialized = false;
                    return;
                }
            }
            
            try
            {
                _connection = new SQLiteConnection(_dbPath, SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.FullMutex);
                _connection.BusyTimeout = TimeSpan.FromSeconds(5);
                _isInitialized = true;
            }
            catch (Exception e)
            {
                _isInitialized = false;
                // A locked or unreadable database silently disabled all coordination, so
                // requests sat pending with no clue why. Say so once, loudly.
                Debug.LogWarning($"[SQLiteManager] Could not open {_dbPath} - PerSpec coordination is DISABLED " +
                                 $"for this session: {e.Message}");
            }
        }
        
        public TestRequest GetNextPendingRequest()
        {
            if (!_isInitialized) return null;
            
            try
            {
                var query = _connection.Table<TestRequest>()
                    .Where(r => r.Status == "pending")
                    .OrderByDescending(r => r.Priority)
                    .ThenBy(r => r.CreatedAt)
                    .FirstOrDefault();
                
                return query;
            }
            catch (Exception e)
            {
                Debug.LogError($"[SQLiteManager] Error getting pending request: {e.Message}");
                return null;
            }
        }
        
        /// <summary>
        /// Generic base method for updating status with common timestamp logic
        /// </summary>
        private void UpdateStatusBase<T>(T entity, string status, Action<T> additionalUpdates = null) where T : class
        {
            if (!_isInitialized || entity == null) return;
            
            try
            {
                // Use reflection to set common status fields
                var statusProp = entity.GetType().GetProperty("Status");
                if (statusProp != null)
                {
                    statusProp.SetValue(entity, status);
                }
                
                // Set timestamps based on status.
                // The live test pipeline writes 'processing' then 'executing' and never
                // 'running', so stamping only on "running" left StartedAt permanently null
                // and broke every duration fallback that depended on it. Stamp on the first
                // active status and never re-stamp.
                if (status == "running" || status == "processing" || status == "executing")
                {
                    var startedProp = entity.GetType().GetProperty("StartedAt");
                    if (startedProp != null)
                    {
                        var existing = startedProp.GetValue(entity);
                        bool alreadyStamped = existing is DateTime stamped
                            ? stamped != default(DateTime)
                            : existing != null;

                        if (!alreadyStamped)
                        {
                            startedProp.SetValue(entity, DateTime.Now);
                        }
                    }
                }
                else if (status == "completed" || status == "failed" || status == "cancelled")
                {
                    var completedProp = entity.GetType().GetProperty("CompletedAt");
                    if (completedProp != null)
                    {
                        completedProp.SetValue(entity, DateTime.Now);
                    }
                }
                
                // Apply any additional updates
                additionalUpdates?.Invoke(entity);
                
                // Update in database
                _connection.Update(entity);
            }
            catch (Exception e)
            {
                Debug.LogError($"[SQLiteManager] Error updating status: {e.Message}");
            }
        }
        
        public string GetRequestStatus(int requestId)
        {
            if (!_isInitialized) return null;
            
            try
            {
                var request = _connection.Table<TestRequest>().FirstOrDefault(r => r.Id == requestId);
                return request?.Status;
            }
            catch (Exception e)
            {
                Debug.LogError($"[SQLiteManager] Error getting request status: {e.Message}");
                return null;
            }
        }
        
        public void UpdateRequestStatus(int requestId, string status, string errorMessage = null)
        {
            if (!_isInitialized) return;
            
            try
            {
                var request = _connection.Table<TestRequest>().FirstOrDefault(r => r.Id == requestId);
                
                if (request != null)
                {
                    UpdateStatusBase(request, status, r =>
                    {
                        if (!string.IsNullOrEmpty(errorMessage))
                        {
                            r.ErrorMessage = errorMessage;
                        }
                    });
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[SQLiteManager] Error updating request status: {e.Message}");
            }
        }
        
        /// <param name="errorMessage">
        /// Optional explanation stored alongside the counts. Needed because a run can now finish
        /// with results that do not match its filter - the counts alone cannot say why.
        /// </param>
        public void UpdateRequestResults(int requestId, string status, int totalTests, int passedTests,
                                        int failedTests, int skippedTests, float duration,
                                        string errorMessage = null)
        {
            try
            {
                var request = _connection.Table<TestRequest>().FirstOrDefault(r => r.Id == requestId);

                if (request != null)
                {
                    request.Status = status;
                    request.CompletedAt = DateTime.Now;
                    request.TotalTests = totalTests;
                    request.PassedTests = passedTests;
                    request.FailedTests = failedTests;
                    request.SkippedTests = skippedTests;
                    request.DurationSeconds = duration;

                    if (!string.IsNullOrEmpty(errorMessage))
                    {
                        request.ErrorMessage = errorMessage;
                    }

                    _connection.Update(request);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[SQLiteManager] Error updating request results: {e.Message}");
            }
        }
        
        public void InsertTestResult(int requestId, string testName, string testClass, string testMethod,
                                    string result, float durationMs, string errorMessage = null, string stackTrace = null)
        {
            try
            {
                var testResult = new TestResultRecord
                {
                    RequestId = requestId,
                    TestName = testName,
                    TestClass = testClass,
                    TestMethod = testMethod,
                    Result = result,
                    DurationMs = durationMs,
                    ErrorMessage = errorMessage,
                    StackTrace = stackTrace,
                    CreatedAt = DateTime.Now
                };
                
                _connection.Insert(testResult);
            }
            catch (Exception e)
            {
                Debug.LogError($"[SQLiteManager] Error inserting test result: {e.Message}");
            }
        }
        
        public void LogExecution(int? requestId, string logLevel, string source, string message)
        {
            if (!_isInitialized) return;
            
            try
            {
                var log = new ExecutionLog
                {
                    RequestId = requestId,
                    LogLevel = logLevel,
                    Source = source,
                    Message = message,
                    CreatedAt = DateTime.Now
                };
                
                _connection.Insert(log);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SQLiteManager] Failed to write log: {e.Message}");
            }
        }
        
        public void UpdateSystemHeartbeat(string component)
        {
            try
            {
                var status = _connection.Table<SystemStatus>()
                    .FirstOrDefault(s => s.Component == component);
                
                if (status != null)
                {
                    status.Status = "online";
                    status.LastHeartbeat = DateTime.Now;
                    status.Message = "Active";
                    _connection.Update(status);
                }
                else
                {
                    _connection.Insert(new SystemStatus
                    {
                        Component = component,
                        Status = "online",
                        LastHeartbeat = DateTime.Now,
                        Message = "Active"
                    });
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SQLiteManager] Failed to update heartbeat: {e.Message}");
            }
        }
        
        public string GetSystemStatus()
        {
            var status = new System.Text.StringBuilder();
            
            try
            {
                // Get pending requests count
                int pendingCount = _connection.Table<TestRequest>()
                    .Where(r => r.Status == "pending")
                    .Count();
                status.AppendLine($"Pending Requests: {pendingCount}");
                
                // Get running requests count
                int runningCount = _connection.Table<TestRequest>()
                    .Where(r => r.Status == "running")
                    .Count();
                status.AppendLine($"Running Requests: {runningCount}");
                
                // Get system components status
                var components = _connection.Table<SystemStatus>()
                    .OrderBy(s => s.Component)
                    .ToList();
                
                status.AppendLine("\nComponent Status:");
                foreach (var comp in components)
                {
                    status.AppendLine($"  {comp.Component}: {comp.Status} (Last: {comp.LastHeartbeat})");
                }
            }
            catch (Exception e)
            {
                status.AppendLine($"Error getting status: {e.Message}");
            }
            
            return status.ToString();
        }
        
        public List<TestRequest> GetAllPendingRequests()
        {
            try
            {
                return _connection.Table<TestRequest>()
                    .Where(r => r.Status == "pending")
                    .OrderByDescending(r => r.Priority)
                    .ThenBy(r => r.CreatedAt)
                    .ToList();
            }
            catch (Exception e)
            {
                Debug.LogError($"[SQLiteManager] Error getting pending requests: {e.Message}");
                return new List<TestRequest>();
            }
        }
        
        /// <summary>
        /// Gets all active test requests (running, processing, executing, or finalizing).
        /// The test execution uses: pending → processing → executing → finalizing → completed
        /// Some legacy paths use "running" directly, so we include all active states.
        /// </summary>
        public List<TestRequest> GetRunningRequests()
        {
            try
            {
                return _connection.Table<TestRequest>()
                    .Where(r => r.Status == "running" ||
                                r.Status == "processing" ||
                                r.Status == "executing" ||
                                r.Status == "finalizing")
                    .ToList();
            }
            catch (Exception e)
            {
                Debug.LogError($"[SQLiteManager] Error getting running requests: {e.Message}");
                return new List<TestRequest>();
            }
        }

        /// <summary>
        /// Gets requests that appear stuck in processing/executing status.
        /// These are likely orphaned due to domain reload during test execution.
        /// </summary>
        /// <param name="maxAge">Maximum age for a request to still be considered active</param>
        /// <returns>List of requests that are likely stuck</returns>
        public List<TestRequest> GetStuckRequests(TimeSpan maxAge)
        {
            if (!_isInitialized) return new List<TestRequest>();

            try
            {
                var cutoff = DateTime.Now - maxAge;
                return _connection.Table<TestRequest>()
                    .Where(r => (r.Status == "processing" ||
                                 r.Status == "executing" ||
                                 r.Status == "running" ||
                                 r.Status == "finalizing") &&
                                 r.CreatedAt < cutoff)
                    .ToList();
            }
            catch (Exception e)
            {
                Debug.LogError($"[SQLiteManager] Error getting stuck requests: {e.Message}");
                return new List<TestRequest>();
            }
        }
        
        public TestRequest GetRequestById(int id)
        {
            try
            {
                return _connection.Table<TestRequest>()
                    .FirstOrDefault(r => r.Id == id);
            }
            catch (Exception e)
            {
                Debug.LogError($"[SQLiteManager] Error getting request by id: {e.Message}");
                return null;
            }
        }
        
        // Menu Item Request Methods
        public MenuItemRequest GetNextPendingMenuRequest()
        {
            try
            {
                // Check if table exists first
                if (!TableExists("menu_item_requests"))
                {
                    // Table doesn't exist - this is expected for projects that haven't migrated yet
                    return null;
                }
                
                var query = _connection.Table<MenuItemRequest>()
                    .Where(r => r.Status == "pending")
                    .OrderByDescending(r => r.Priority)
                    .ThenBy(r => r.CreatedAt)
                    .FirstOrDefault();
                
                return query;
            }
            catch (Exception)
            {
                // Re-throw to let MenuItemCoordinator handle it
                throw;
            }
        }
        
        public void UpdateMenuRequestStatus(int requestId, string status, string result = null, string errorMessage = null)
        {
            if (!_isInitialized) return;
            
            try
            {
                var request = _connection.Table<MenuItemRequest>().FirstOrDefault(r => r.Id == requestId);
                
                if (request != null)
                {
                    request.Status = status;
                    
                    if (status == "running" && request.StartedAt == null)
                    {
                        request.StartedAt = DateTime.Now;
                    }
                    else if (status == "completed" || status == "failed")
                    {
                        request.CompletedAt = DateTime.Now;
                        
                        if (request.StartedAt.HasValue)
                        {
                            request.DurationSeconds = (float)(request.CompletedAt.Value - request.StartedAt.Value).TotalSeconds;
                        }
                        
                        if (result != null)
                            request.Result = result;
                        
                        if (errorMessage != null)
                            request.ErrorMessage = errorMessage;
                    }
                    
                    _connection.Update(request);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[SQLiteManager] Error updating menu request status: {e.Message}");
            }
        }
        
        // Asset Refresh Methods
        public AssetRefreshRequest GetNextPendingRefreshRequest()
        {
            try
            {
                var query = _connection.Table<AssetRefreshRequest>()
                    .Where(r => r.Status == "pending")
                    .OrderByDescending(r => r.Priority)
                    .ThenBy(r => r.CreatedAt)
                    .FirstOrDefault();
                
                return query;
            }
            catch (Exception e)
            {
                Debug.LogError($"[SQLiteManager] Error getting pending refresh request: {e.Message}");
                return null;
            }
        }
        
        public void UpdateRefreshRequestStatus(int requestId, string status, string resultMessage = null, string errorMessage = null)
        {
            if (!_isInitialized) return;
            
            try
            {
                var request = _connection.Table<AssetRefreshRequest>().FirstOrDefault(r => r.Id == requestId);
                
                if (request != null)
                {
                    UpdateStatusBase(request, status, r =>
                    {
                        // Calculate duration when completing
                        if ((status == "completed" || status == "failed" || status == "cancelled") && r.StartedAt.HasValue)
                        {
                            r.DurationSeconds = (float)(DateTime.Now - r.StartedAt.Value).TotalSeconds;
                        }
                        
                        if (!string.IsNullOrEmpty(resultMessage))
                        {
                            r.ResultMessage = resultMessage;
                        }
                        
                        if (!string.IsNullOrEmpty(errorMessage))
                        {
                            r.ErrorMessage = errorMessage;
                        }
                    });
                    
                    Debug.Log($"[SQLiteManager] Updated refresh request {requestId} status to {status}");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[SQLiteManager] Error updating refresh request status: {e.Message}");
            }
        }
        
        public List<AssetRefreshRequest> GetPendingRefreshRequests()
        {
            try
            {
                return _connection.Table<AssetRefreshRequest>()
                    .Where(r => r.Status == "pending")
                    .OrderByDescending(r => r.Priority)
                    .ThenBy(r => r.CreatedAt)
                    .ToList();
            }
            catch (Exception e)
            {
                Debug.LogError($"[SQLiteManager] Error getting pending refresh requests: {e.Message}");
                return new List<AssetRefreshRequest>();
            }
        }
        
        public List<AssetRefreshRequest> GetRunningRefreshRequests()
        {
            return GetRefreshRequestsByStatus("running");
        }

        public List<AssetRefreshRequest> GetRefreshRequestsByStatus(string status)
        {
            try
            {
                return _connection.Table<AssetRefreshRequest>()
                    .Where(r => r.Status == status)
                    .ToList();
            }
            catch (Exception e)
            {
                Debug.LogError($"[SQLiteManager] Error getting refresh requests with status '{status}': {e.Message}");
                return new List<AssetRefreshRequest>();
            }
        }
        
        // Console logging removed - now using file-based EditModeLogCapture
        
        public void StartConsoleSession(string sessionId, string reason)
        {
            try
            {
                // Ensure table exists
                _connection.Execute(@"
                    CREATE TABLE IF NOT EXISTS console_sessions (
                        session_id TEXT PRIMARY KEY,
                        start_time INTEGER,
                        end_time INTEGER,
                        reason TEXT
                    )");
                
                _connection.Execute(
                    "INSERT INTO console_sessions (session_id, start_time, reason) VALUES (?, ?, ?)",
                    sessionId, DateTime.Now, reason);
                
                // Clean up old sessions - keep only current + 3 historical
                CleanupOldSessions(sessionId);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SQLiteManager] Error starting console session: {ex.Message}");
            }
        }
        
        public void EndConsoleSession(string sessionId)
        {
            try
            {
                _connection.Execute(
                    "UPDATE console_sessions SET end_time = ? WHERE session_id = ?",
                    DateTime.Now, sessionId);
            }
            catch (Exception)
            {
                // Ignore - session tracking is optional
            }
        }
        
        public bool HasCompilationErrors()
        {
            try
            {
                // Check if recent logs contain compilation errors
                var recentTime = DateTime.Now.AddMinutes(-1);
                var count = _connection.ExecuteScalar<int>(
                    "SELECT COUNT(*) FROM console_logs WHERE timestamp > ? AND message LIKE '%error CS%'",
                    recentTime);
                return count > 0;
            }
            catch
            {
                return false;
            }
        }
        
        private class SessionResult
        {
            public string session_id { get; set; }
        }
        
        private void CleanupOldSessions(string currentSessionId)
        {
            try
            {
                // Get all sessions ordered by most recent first
                var sessionResults = _connection.Query<SessionResult>(@"
                    SELECT session_id 
                    FROM (
                        SELECT session_id, MAX(id) as max_id
                        FROM console_logs 
                        WHERE session_id != ?
                        GROUP BY session_id
                    )
                    ORDER BY max_id DESC", currentSessionId).ToList();
                
                var sessions = sessionResults.Select(s => s.session_id).ToList();
                
                // Keep only 3 most recent historical sessions
                if (sessions.Count > 3)
                {
                    var sessionsToDelete = sessions.Skip(3).ToList();
                    
                    foreach (var sessionId in sessionsToDelete)
                    {
                        // Delete logs from old session
                        int deletedLogs = _connection.Execute(
                            "DELETE FROM console_logs WHERE session_id = ?", 
                            sessionId);
                        
                        // Delete session record
                        _connection.Execute(
                            "DELETE FROM console_sessions WHERE session_id = ?", 
                            sessionId);
                        
                        if (deletedLogs > 0)
                        {
                            Debug.Log($"[SQLiteManager] Cleaned up old session {sessionId} ({deletedLogs} logs)");
                        }
                    }
                    
                    // Vacuum to reclaim space if significant data was deleted
                    if (sessionsToDelete.Count > 10)
                    {
                        VacuumDatabase();
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[SQLiteManager] Error cleaning up old sessions: {e.Message}");
            }
        }
        
        public List<string> GetRecentSessions(int count = 4)
        {
            try
            {
                var sessionResults = _connection.Query<SessionResult>(@"
                    SELECT session_id 
                    FROM (
                        SELECT session_id, MAX(id) as max_id
                        FROM console_logs 
                        GROUP BY session_id
                    )
                    ORDER BY max_id DESC 
                    LIMIT ?", count).ToList();
                
                return sessionResults.Select(s => s.session_id).ToList();
            }
            catch (Exception e)
            {
                Debug.LogError($"[SQLiteManager] Error getting recent sessions: {e.Message}");
                return new List<string>();
            }
        }
        
        public Dictionary<string, int> GetSessionLogCounts()
        {
            try
            {
                var result = new Dictionary<string, int>();
                var sessions = GetRecentSessions();
                
                foreach (var sessionId in sessions)
                {
                    var count = _connection.ExecuteScalar<int>(
                        "SELECT COUNT(*) FROM console_logs WHERE session_id = ?",
                        sessionId);
                    result[sessionId] = count;
                }
                
                return result;
            }
            catch (Exception e)
            {
                Debug.LogError($"[SQLiteManager] Error getting session log counts: {e.Message}");
                return new Dictionary<string, int>();
            }
        }
        
        public int GetTotalLogCount()
        {
            try
            {
                return _connection.ExecuteScalar<int>("SELECT COUNT(*) FROM console_logs");
            }
            catch (Exception e)
            {
                Debug.LogError($"[SQLiteManager] Error getting total log count: {e.Message}");
                return 0;
            }
        }
        
        // Scene Hierarchy Methods
        public SceneHierarchyRequest GetNextPendingHierarchyRequest()
        {
            try
            {
                // Check if table exists first
                if (!TableExists("scene_hierarchy_requests"))
                {
                    // Table doesn't exist - this is expected for projects that haven't migrated yet
                    return null;
                }

                var query = _connection.Table<SceneHierarchyRequest>()
                    .Where(r => r.Status == "pending")
                    .OrderByDescending(r => r.Priority)
                    .ThenBy(r => r.CreatedAt)
                    .FirstOrDefault();

                return query;
            }
            catch (Exception e)
            {
                Debug.LogError($"[SQLiteManager] Error getting pending hierarchy request: {e.Message}");
                return null;
            }
        }

        public void UpdateHierarchyRequestStatus(int requestId, string status, string outputFile = null, string errorMessage = null)
        {
            if (!_isInitialized) return;

            try
            {
                var request = _connection.Table<SceneHierarchyRequest>().FirstOrDefault(r => r.Id == requestId);

                if (request != null)
                {
                    request.Status = status;

                    if (status == "running" && request.StartedAt == null)
                    {
                        request.StartedAt = DateTime.Now;
                    }
                    else if (status == "completed" || status == "failed")
                    {
                        request.CompletedAt = DateTime.Now;
                    }

                    if (outputFile != null)
                        request.OutputFile = outputFile;

                    if (errorMessage != null)
                        request.ErrorMessage = errorMessage;

                    _connection.Update(request);

                    Debug.Log($"[SQLiteManager] Updated hierarchy request {requestId} status to {status}");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[SQLiteManager] Error updating hierarchy request status: {e.Message}");
            }
        }

        // Database Utility Methods
        public bool TableExists(string tableName)
        {
            if (!_isInitialized) return false;
            
            try
            {
                var result = _connection.ExecuteScalar<int>(
                    "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=?", 
                    tableName);
                return result > 0;
            }
            catch (Exception e)
            {
                Debug.LogError($"[SQLiteManager] Error checking table existence: {e.Message}");
                return false;
            }
        }
        
        // Database Maintenance Methods
        public void DeleteOldTestResults(int hoursToKeep = 2)
        {
            try
            {
                var cutoffTime = DateTime.Now.AddHours(-hoursToKeep);
                
                // Delete old test results
                int deletedResults = _connection.Execute("DELETE FROM test_results WHERE created_at < ?", cutoffTime);
                
                // Delete old test requests
                int deletedRequests = _connection.Execute("DELETE FROM test_requests WHERE created_at < ? AND status IN ('completed', 'failed', 'cancelled', 'inconclusive')", cutoffTime);
                
                if (deletedResults > 0 || deletedRequests > 0)
                {
                    Debug.Log($"[SQLiteManager] Deleted {deletedResults} test results and {deletedRequests} test requests older than {hoursToKeep} hours");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[SQLiteManager] Error deleting old test data: {e.Message}");
            }
        }
        
        public void DeleteOldExecutionLogs(int hoursToKeep = 2)
        {
            try
            {
                var cutoffTime = DateTime.Now.AddHours(-hoursToKeep);
                int deleted = _connection.Execute("DELETE FROM execution_log WHERE created_at < ?", cutoffTime);
                
                if (deleted > 0)
                {
                    Debug.Log($"[SQLiteManager] Deleted {deleted} execution logs older than {hoursToKeep} hours");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[SQLiteManager] Error deleting old execution logs: {e.Message}");
            }
        }
        
        public void DeleteOldRefreshRequests(int hoursToKeep = 2)
        {
            try
            {
                var cutoffTime = DateTime.Now.AddHours(-hoursToKeep);
                int deleted = _connection.Execute("DELETE FROM asset_refresh_requests WHERE created_at < ? AND status IN ('completed', 'failed', 'cancelled')", cutoffTime);
                
                if (deleted > 0)
                {
                    Debug.Log($"[SQLiteManager] Deleted {deleted} old refresh requests");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[SQLiteManager] Error deleting old refresh requests: {e.Message}");
            }
        }
        
        public void VacuumDatabase()
        {
            try
            {
                Debug.Log("[SQLiteManager] Starting database VACUUM...");
                _connection.Execute("VACUUM");
                Debug.Log("[SQLiteManager] Database VACUUM completed successfully");
            }
            catch (Exception e)
            {
                Debug.LogError($"[SQLiteManager] Error performing VACUUM: {e.Message}");
            }
        }
        
        public long GetDatabaseSize()
        {
            try
            {
                var dbPath = Path.Combine(Application.dataPath, "..", "PerSpec", "test_coordination.db");
                if (File.Exists(dbPath))
                {
                    var fileInfo = new FileInfo(dbPath);
                    return fileInfo.Length;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[SQLiteManager] Error getting database size: {e.Message}");
            }
            return 0;
        }
        
        public void PerformFullMaintenance(float hoursToKeep = 2)
        {
            try
            {
                Debug.Log("[SQLiteManager] Starting full database maintenance...");
                
                // Get size before
                long sizeBefore = GetDatabaseSize();
                
                // Console logs now managed by file-based EditModeLogCapture (3 session limit)
                DeleteOldTestResults((int)Math.Ceiling(hoursToKeep));
                DeleteOldExecutionLogs((int)Math.Ceiling(hoursToKeep));
                DeleteOldRefreshRequests((int)Math.Ceiling(hoursToKeep));
                
                // Vacuum to reclaim space
                VacuumDatabase();
                
                // Get size after
                long sizeAfter = GetDatabaseSize();
                
                // Report results
                long savedBytes = sizeBefore - sizeAfter;
                if (savedBytes > 0)
                {
                    float savedMB = savedBytes / (1024f * 1024f);
                    Debug.Log($"[SQLiteManager] Maintenance complete. Freed {savedMB:F2} MB (from {sizeBefore / (1024f * 1024f):F2} MB to {sizeAfter / (1024f * 1024f):F2} MB)");
                }
                else
                {
                    Debug.Log($"[SQLiteManager] Maintenance complete. Database size: {sizeAfter / (1024f * 1024f):F2} MB");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[SQLiteManager] Error performing full maintenance: {e.Message}");
            }
        }
        
        public void ResetDatabase()
        {
            try
            {
                Debug.LogWarning("[SQLiteManager] Resetting database - deleting ALL data...");
                
                // Delete everything from all tables
                _connection.Execute("DELETE FROM console_logs");
                _connection.Execute("DELETE FROM test_results");
                _connection.Execute("DELETE FROM test_requests");
                _connection.Execute("DELETE FROM execution_log");
                _connection.Execute("DELETE FROM asset_refresh_requests");
                _connection.Execute("DELETE FROM system_status");
                
                // Reset auto-increment counters
                _connection.Execute("DELETE FROM sqlite_sequence");
                
                // Vacuum to reclaim all space
                VacuumDatabase();
                
                Debug.Log("[SQLiteManager] Database reset complete");
            }
            catch (Exception e)
            {
                Debug.LogError($"[SQLiteManager] Error resetting database: {e.Message}");
            }
        }
        
        // Console log query methods removed - now using file-based EditModeLogCapture
        
        ~SQLiteManager()
        {
            try
            {
                _connection?.Close();
                _connection?.Dispose();
            }
            catch
            {
                // Ignore disposal errors
            }
        }
    }
}