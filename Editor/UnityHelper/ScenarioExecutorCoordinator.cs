using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;
using SQLite;

namespace PerSpec.UnityHelper.Editor
{
    /// <summary>
    /// Database model for scenario execution requests.
    /// Matches the table created by unityhelper_coordinator.py.
    /// </summary>
    [Table("scenario_execution_requests")]
    public class ScenarioExecutionRequest
    {
        [PrimaryKey, AutoIncrement, Column("id")]
        public int Id { get; set; }

        [Column("scenarios_file")]
        public string ScenariosFile { get; set; }

        [Column("target")]
        public string Target { get; set; }

        [Column("options")]
        public string Options { get; set; }

        [Column("status")]
        public string Status { get; set; }

        [Column("priority")]
        public int Priority { get; set; }

        [Column("created_at")]
        public string CreatedAt { get; set; }

        [Column("started_at")]
        public string StartedAt { get; set; }

        [Column("completed_at")]
        public string CompletedAt { get; set; }

        [Column("result")]
        public string Result { get; set; }

        [Column("error_message")]
        public string ErrorMessage { get; set; }

        [Column("tasks_total")]
        public int TasksTotal { get; set; }

        [Column("tasks_success")]
        public int TasksSuccess { get; set; }

        [Column("tasks_failed")]
        public int TasksFailed { get; set; }
    }

    /// <summary>
    /// Options parsed from JSON
    /// </summary>
    [Serializable]
    public class ScenarioExecutionOptions
    {
        public string scenarios_file;
        public string target;
        public bool pending_only;
        public bool failed_only;
    }

    /// <summary>
    /// Polls PerSpec's database for scenario execution requests and executes them.
    /// Follows the same pattern as PerSpec's MenuItemCoordinator:
    /// - Uses PerSpec's test_coordination.db (no separate database)
    /// - [InitializeOnLoad] + EditorApplication.update polling
    /// - EditorApplication.delayCall for deferred execution
    ///
    /// Opens its own SQLiteConnection to the same DB — no changes to PerSpec's SQLiteManager needed.
    /// </summary>
    [InitializeOnLoad]
    public static class ScenarioExecutorCoordinator
    {
        private const double CHECK_INTERVAL = 1.0;
        private const double EXECUTION_TIMEOUT = 300.0; // 5 minutes

        private static double _lastCheckTime;
        private static bool _isExecuting;
        private static SQLiteConnection _connection;
        private static int _currentRequestId = -1;
        private static DateTime _executionStartTime;
        private static bool _pollingEnabled = true;

        // State machine for async-aware execution across editor frames.
        // When a task is async (IsAsyncTask returns true), we park execution here and
        // resume on subsequent OnEditorUpdate ticks after task.asyncCompleted is set.
        private class ExecutionState
        {
            public ScenarioExecutionRequest Request;
            public ScenarioConfig Config;
            public string ScenariosPath;
            public ScenarioExecutionOptions Options;
            public List<QueueEntry> Queue;
            public int Index;
            public ExecutionResult Result = new ExecutionResult();
            public Task PendingAsyncTask;
            public Scenario PendingAsyncScenario;
            public HashSet<Scenario> TouchedScenarios = new HashSet<Scenario>();
        }
        private class QueueEntry
        {
            public Scenario Scenario;
            public Task Task;
        }
        private static ExecutionState _execState;

        static ScenarioExecutorCoordinator()
        {
            // Check if PerSpec is initialized by looking for its directory
            string projectPath = Directory.GetParent(Application.dataPath).FullName;
            if (!Directory.Exists(Path.Combine(projectPath, "PerSpec")))
                return;

            EditorApplication.delayCall += Initialize;
        }

        private static void Initialize()
        {
            string projectPath = Directory.GetParent(Application.dataPath).FullName;
            string dbPath = Path.Combine(projectPath, "PerSpec", "test_coordination.db");

            if (!File.Exists(dbPath))
                return;

            try
            {
                _connection = new SQLiteConnection(dbPath, SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.FullMutex);
                _connection.BusyTimeout = TimeSpan.FromSeconds(5);

                var tableExists = _connection.ExecuteScalar<int>(
                    "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='scenario_execution_requests'");

                if (tableExists == 0)
                {
                    _connection.Dispose();
                    _connection = null;
                    return;
                }

                // Register executors once at startup
                TaskExecutorRegistry.Register(new SceneTaskExecutor());
#if HAS_UNITY_LOCALIZATION
                TaskExecutorRegistry.Register(new LocalizationTaskExecutor());
#endif
                TaskExecutorRegistry.Register(new TmproTaskExecutor());

                EditorApplication.update += OnEditorUpdate;
                _lastCheckTime = EditorApplication.timeSinceStartup;

                Debug.Log("[ScenarioExecutorCoordinator] Initialized");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ScenarioExecutorCoordinator] Failed to initialize: {e.Message}");
                _connection = null;
            }
        }

        private static void OnEditorUpdate()
        {
            if (!_pollingEnabled || _connection == null)
                return;

            // Step the execution state machine every frame while active —
            // CHECK_INTERVAL gates DB polling only, not in-progress execution.
            if (_isExecuting && _execState != null)
            {
                if ((DateTime.Now - _executionStartTime).TotalSeconds > EXECUTION_TIMEOUT)
                {
                    Debug.LogWarning($"[ScenarioExecutorCoordinator] Execution timeout for request {_currentRequestId}");
                    FinalizeExecution(false);
                    return;
                }
                StepExecution();
                return;
            }

            if (EditorApplication.timeSinceStartup - _lastCheckTime < CHECK_INTERVAL)
                return;

            _lastCheckTime = EditorApplication.timeSinceStartup;

            if (_isExecuting)
            {
                if ((DateTime.Now - _executionStartTime).TotalSeconds > EXECUTION_TIMEOUT)
                {
                    Debug.LogWarning($"[ScenarioExecutorCoordinator] Execution timeout for request {_currentRequestId}");
                    CompleteExecution(false, null, "Execution timed out");
                }
                return;
            }

            CheckForPendingRequests();
        }

        private static void CheckForPendingRequests()
        {
            try
            {
                var request = _connection.Table<ScenarioExecutionRequest>()
                    .Where(r => r.Status == "pending")
                    .OrderByDescending(r => r.Priority)
                    .ThenBy(r => r.CreatedAt)
                    .FirstOrDefault();

                if (request != null)
                    ExecuteRequest(request);
            }
            catch (Exception e)
            {
                if (e.Message.Contains("no such table"))
                {
                    _pollingEnabled = false;
                    EditorApplication.update -= OnEditorUpdate;
                }
                else
                {
                    Debug.LogError($"[ScenarioExecutorCoordinator] Error checking requests: {e.Message}");
                }
            }
        }

        private static void ExecuteRequest(ScenarioExecutionRequest request)
        {
            _isExecuting = true;
            _currentRequestId = request.Id;
            _executionStartTime = DateTime.Now;

            Debug.Log($"[ScenarioExecutorCoordinator] Starting: {request.Target} from {request.ScenariosFile}");
            UpdateRequestStatus(request.Id, "running");

            // Initialize the state machine on next tick; StepExecution drives it.
            EditorApplication.delayCall += () =>
            {
                try
                {
                    var options = JsonUtility.FromJson<ScenarioExecutionOptions>(request.Options);

                    string projectPath = Directory.GetParent(Application.dataPath).FullName;
                    string scenariosPath = Path.Combine(projectPath, request.ScenariosFile);

                    if (!File.Exists(scenariosPath))
                        throw new FileNotFoundException($"Scenarios file not found: {scenariosPath}");

                    string json = File.ReadAllText(scenariosPath);
                    var config = JsonUtility.FromJson<ScenarioConfig>(json);

                    var queue = BuildQueueForTarget(config, request.Target, options);

                    _execState = new ExecutionState
                    {
                        Request = request,
                        Config = config,
                        ScenariosPath = scenariosPath,
                        Options = options,
                        Queue = queue,
                        Index = 0,
                    };
                    Debug.Log($"[ScenarioExecutorCoordinator] Queued {queue.Count} task(s) for execution");
                }
                catch (Exception e)
                {
                    Debug.LogError($"[ScenarioExecutorCoordinator] Execution init failed: {e.Message}");
                    _execState = null;
                    CompleteExecution(false, null, e.Message);
                }
            };
        }

        // Build a flat (scenario, task) queue honoring the same target routing
        // logic as ExecuteTarget. Returns empty list on unknown target; caller handles.
        private static List<QueueEntry> BuildQueueForTarget(ScenarioConfig config, string target, ScenarioExecutionOptions options)
        {
            var queue = new List<QueueEntry>();

            if (target == "all")
            {
                foreach (var scenario in config.scenarios)
                    AppendScenarioTasks(queue, scenario, GetAllTasks(scenario));
                return queue;
            }

            if (target.StartsWith("scenario:"))
            {
                var parts = target.Split(':');
                int scenarioIdx = int.Parse(parts[1]);
                if (scenarioIdx < 0 || scenarioIdx >= config.scenarios.Count)
                    return queue; // empty → Finalize will mark failed
                var scenario = config.scenarios[scenarioIdx];

                if (parts.Length >= 4 && parts[2] == "group")
                {
                    int groupIdx = int.Parse(parts[3]);
                    if (scenario.taskGroups == null || groupIdx >= scenario.taskGroups.Count)
                        return queue;
                    AppendScenarioTasks(queue, scenario, GetTasksFromGroup(scenario.taskGroups[groupIdx]));
                    return queue;
                }

                AppendScenarioTasks(queue, scenario, GetAllTasks(scenario));
                return queue;
            }

            var namedScenario = config.scenarios.Find(s => s.name == target);
            if (namedScenario != null)
                AppendScenarioTasks(queue, namedScenario, GetAllTasks(namedScenario));
            return queue;
        }

        private static void AppendScenarioTasks(List<QueueEntry> queue, Scenario scenario, List<Task> tasks)
        {
            foreach (var t in tasks)
                queue.Add(new QueueEntry { Scenario = scenario, Task = t });
        }

        // State machine step. Drives the queue one task per tick and honors async tasks.
        private static void StepExecution()
        {
            var state = _execState;
            if (state == null) return;

            // Is there a pending async task from the prior tick?
            if (state.PendingAsyncTask != null)
            {
                if (!state.PendingAsyncTask.asyncCompleted)
                    return; // still waiting — check again next tick

                // Async task finished — collect its result.
                var async = state.PendingAsyncTask;
                bool asyncOk = async.asyncSuccess;
                if (asyncOk)
                {
                    async.status = "success";
                    async.error = "";
                    state.Result.Success++;
                }
                else
                {
                    async.status = "failed";
                    state.Result.Failed++;
                    state.Result.ErrorMessage = async.error;
                    Debug.LogError($"[ScenarioExecutorCoordinator] Async task failed: {async.action} — {async.error}");
                    FinalizeExecution(false);
                    return;
                }
                state.PendingAsyncTask = null;
                state.PendingAsyncScenario = null;
            }

            // Done?
            if (state.Index >= state.Queue.Count)
            {
                FinalizeExecution(state.Result.Failed == 0);
                return;
            }

            // Pick next task.
            var entry = state.Queue[state.Index];
            state.Index++;
            state.TouchedScenarios.Add(entry.Scenario);
            entry.Scenario.status = "running";

            state.Result.Total++;

            if (!ShouldExecuteTask(entry.Task, state.Options))
                return; // skipped; continue next tick

            entry.Task.status = "running";
            entry.Task.isAsync = false;
            entry.Task.asyncCompleted = false;
            entry.Task.asyncSuccess = false;

            bool syncOk;
            try
            {
                syncOk = TaskExecutorRegistry.ExecuteTask(entry.Task);
            }
            catch (Exception e)
            {
                entry.Task.status = "failed";
                entry.Task.error = e.Message;
                state.Result.Failed++;
                state.Result.ErrorMessage = e.Message;
                Debug.LogError($"[ScenarioExecutorCoordinator] Task exception: {entry.Task.action} — {e.Message}");
                FinalizeExecution(false);
                return;
            }

            // Async? park and resume later.
            if (syncOk && TaskExecutorRegistry.IsAsyncTask(entry.Task))
            {
                entry.Task.isAsync = true;
                state.PendingAsyncTask = entry.Task;
                state.PendingAsyncScenario = entry.Scenario;
                return;
            }

            // Sync result.
            if (syncOk)
            {
                entry.Task.status = "success";
                entry.Task.error = "";
                state.Result.Success++;
            }
            else
            {
                entry.Task.status = "failed";
                state.Result.Failed++;
                state.Result.ErrorMessage = entry.Task.error;
                Debug.LogError($"[ScenarioExecutorCoordinator] Task failed: {entry.Task.action} — {entry.Task.error}");
                FinalizeExecution(false);
            }
        }

        private static void FinalizeExecution(bool overallSuccess)
        {
            var state = _execState;
            if (state == null)
            {
                CompleteExecution(overallSuccess, null, overallSuccess ? null : "Finalize called without state");
                return;
            }

            // Update per-scenario status for all scenarios we touched.
            foreach (var scenario in state.TouchedScenarios)
                UpdateScenarioStatus(scenario);

            state.Result.Message = $"Completed: {state.Result.Success}/{state.Result.Total} tasks successful, {state.Result.Failed} failed";

            // Write back the scenarios file with updated statuses.
            try
            {
                string updatedJson = JsonUtility.ToJson(state.Config, true);
                File.WriteAllText(state.ScenariosPath, updatedJson);
                AssetDatabase.Refresh();
            }
            catch (Exception e)
            {
                Debug.LogError($"[ScenarioExecutorCoordinator] Failed to write scenarios file: {e.Message}");
            }

            UpdateRequestResults(state.Request.Id, state.Result);

            string msg = state.Result.Message;
            string err = state.Result.Failed > 0 ? state.Result.ErrorMessage : null;
            _execState = null;
            CompleteExecution(state.Result.Failed == 0, msg, err);
        }

        private static ExecutionResult ExecuteTarget(ScenarioConfig config, string target, ScenarioExecutionOptions options)
        {
            if (target == "all")
            {
                var result = new ExecutionResult();
                foreach (var scenario in config.scenarios)
                {
                    var scenarioResult = ExecuteScenario(scenario, options);
                    result.Total += scenarioResult.Total;
                    result.Success += scenarioResult.Success;
                    result.Failed += scenarioResult.Failed;

                    if (scenarioResult.Failed > 0)
                    {
                        result.ErrorMessage = scenarioResult.ErrorMessage;
                        break;
                    }
                }
                result.Message = $"Completed: {result.Success}/{result.Total} tasks successful, {result.Failed} failed";
                return result;
            }

            if (target.StartsWith("scenario:"))
            {
                var parts = target.Split(':');
                int scenarioIdx = int.Parse(parts[1]);

                if (scenarioIdx < 0 || scenarioIdx >= config.scenarios.Count)
                    return new ExecutionResult { ErrorMessage = $"Scenario {scenarioIdx} not found", Failed = 1 };

                var scenario = config.scenarios[scenarioIdx];

                if (parts.Length >= 4 && parts[2] == "group")
                {
                    int groupIdx = int.Parse(parts[3]);
                    if (scenario.taskGroups == null || groupIdx >= scenario.taskGroups.Count)
                        return new ExecutionResult { ErrorMessage = $"Group {groupIdx} not found", Failed = 1 };

                    var result = ExecuteTasks(GetTasksFromGroup(scenario.taskGroups[groupIdx]), options);
                    UpdateScenarioStatus(scenario);
                    result.Message = $"Completed: {result.Success}/{result.Total} tasks successful, {result.Failed} failed";
                    return result;
                }

                return ExecuteScenario(scenario, options);
            }

            // Find by name
            var namedScenario = config.scenarios.Find(s => s.name == target);
            if (namedScenario != null)
                return ExecuteScenario(namedScenario, options);

            return new ExecutionResult { ErrorMessage = $"Scenario '{target}' not found", Failed = 1 };
        }

        private static ExecutionResult ExecuteScenario(Scenario scenario, ScenarioExecutionOptions options)
        {
            Debug.Log($"[ScenarioExecutorCoordinator] Executing scenario: {scenario.name}");
            scenario.status = "running";

            var result = ExecuteTasks(GetAllTasks(scenario), options);

            scenario.status = result.Failed > 0 ? "failed" : "completed";
            scenario.lastRun = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
            result.Message = $"Completed: {result.Success}/{result.Total} tasks successful, {result.Failed} failed";

            Debug.Log($"[ScenarioExecutorCoordinator] Scenario '{scenario.name}' {scenario.status}: {result.Success}/{result.Total}");
            return result;
        }

        private static ExecutionResult ExecuteTasks(List<Task> tasks, ScenarioExecutionOptions options)
        {
            var result = new ExecutionResult();

            foreach (var task in tasks)
            {
                result.Total++;

                if (!ShouldExecuteTask(task, options))
                    continue;

                task.status = "running";

                try
                {
                    bool success = TaskExecutorRegistry.ExecuteTask(task);

                    if (success)
                    {
                        task.status = "success";
                        task.error = "";
                        result.Success++;
                    }
                    else
                    {
                        task.status = "failed";
                        result.Failed++;
                        result.ErrorMessage = task.error;
                        Debug.LogError($"[ScenarioExecutorCoordinator] Task failed: {task.action} — {task.error}");
                        break;
                    }
                }
                catch (Exception e)
                {
                    task.status = "failed";
                    task.error = e.Message;
                    result.Failed++;
                    result.ErrorMessage = e.Message;
                    Debug.LogError($"[ScenarioExecutorCoordinator] Task exception: {task.action} — {e.Message}");
                    break;
                }
            }

            return result;
        }

        private static List<Task> GetAllTasks(Scenario scenario)
        {
            var tasks = new List<Task>();

            if (scenario.taskGroups != null)
            {
                foreach (var group in scenario.taskGroups)
                    tasks.AddRange(GetTasksFromGroup(group));
            }

            if (scenario.tasks != null)
                tasks.AddRange(scenario.tasks);

            return tasks;
        }

        private static List<Task> GetTasksFromGroup(TaskGroup group)
        {
            var tasks = new List<Task>();

            if (group.tasks != null)
                tasks.AddRange(group.tasks);

            if (group.subGroups != null)
            {
                foreach (var subGroup in group.subGroups)
                    tasks.AddRange(GetTasksFromGroup(subGroup));
            }

            return tasks;
        }

        private static bool ShouldExecuteTask(Task task, ScenarioExecutionOptions options)
        {
            string status = task.status ?? "pending";

            if (options.pending_only && status != "pending")
                return false;

            if (options.failed_only && status != "failed")
                return false;

            if (!options.pending_only && !options.failed_only)
            {
                if (status != "pending" && status != "failed")
                    return false;
            }

            return true;
        }

        private static void UpdateScenarioStatus(Scenario scenario)
        {
            var allTasks = GetAllTasks(scenario);
            int success = allTasks.Count(t => t.status == "success");
            int failed = allTasks.Count(t => t.status == "failed");
            int pending = allTasks.Count(t => t.status == "pending" || string.IsNullOrEmpty(t.status));

            if (failed > 0)
                scenario.status = "failed";
            else if (pending == 0)
                scenario.status = "completed";
            else if (success > 0)
                scenario.status = "running";
            else
                scenario.status = "pending";

            scenario.lastRun = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
        }

        private static void UpdateRequestStatus(int requestId, string status)
        {
            try
            {
                var request = _connection.Table<ScenarioExecutionRequest>().FirstOrDefault(r => r.Id == requestId);
                if (request == null) return;

                request.Status = status;
                if (status == "running")
                    request.StartedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");

                _connection.Update(request);
            }
            catch (Exception e)
            {
                Debug.LogError($"[ScenarioExecutorCoordinator] Error updating status: {e.Message}");
            }
        }

        private static void UpdateRequestResults(int requestId, ExecutionResult result)
        {
            try
            {
                var request = _connection.Table<ScenarioExecutionRequest>().FirstOrDefault(r => r.Id == requestId);
                if (request == null) return;

                request.TasksTotal = result.Total;
                request.TasksSuccess = result.Success;
                request.TasksFailed = result.Failed;
                request.Result = result.Message;

                if (!string.IsNullOrEmpty(result.ErrorMessage))
                    request.ErrorMessage = result.ErrorMessage;

                _connection.Update(request);
            }
            catch (Exception e)
            {
                Debug.LogError($"[ScenarioExecutorCoordinator] Error updating results: {e.Message}");
            }
        }

        private static void CompleteExecution(bool success, string result, string errorMessage)
        {
            if (_currentRequestId > 0)
            {
                try
                {
                    var request = _connection.Table<ScenarioExecutionRequest>().FirstOrDefault(r => r.Id == _currentRequestId);
                    if (request != null)
                    {
                        request.Status = success ? "completed" : "failed";
                        request.CompletedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");

                        if (!string.IsNullOrEmpty(result))
                            request.Result = result;

                        if (!string.IsNullOrEmpty(errorMessage))
                            request.ErrorMessage = errorMessage;

                        _connection.Update(request);
                    }

                    var duration = (DateTime.Now - _executionStartTime).TotalSeconds;
                    Debug.Log($"[ScenarioExecutorCoordinator] Request {_currentRequestId} {(success ? "completed" : "failed")} in {duration:F2}s");
                }
                catch (Exception e)
                {
                    Debug.LogError($"[ScenarioExecutorCoordinator] Error completing execution: {e.Message}");
                }
            }

            _isExecuting = false;
            _currentRequestId = -1;
        }

        public static void EnablePolling(bool enable)
        {
            _pollingEnabled = enable;
            Debug.Log($"[ScenarioExecutorCoordinator] Polling {(enable ? "enabled" : "disabled")}");
        }

        private class ExecutionResult
        {
            public int Total { get; set; }
            public int Success { get; set; }
            public int Failed { get; set; }
            public string Message { get; set; }
            public string ErrorMessage { get; set; }
        }
    }
}
