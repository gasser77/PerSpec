using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.IO;
using System.Collections.Generic;
using System;
using System.Linq;

namespace PerSpec.UnityHelper.Editor
{
    /// <summary>
    /// Editor Window for running atomic task-based scenarios
    /// Executes simple reusable operations from config file
    /// </summary>
    public class ScenarioRunner : EditorWindow
    {
        private const string CONFIG_PATH_PREF_KEY = "ScenarioRunner_ConfigPath";
        private const string DEFAULT_CONFIG_PATH = "Assets/Scenarios/scenarios.json";

        private ScenarioConfig _config;
        private Vector2 _scrollPosition;
        private string _configPath;

        [MenuItem("Tools/PerSpec/Scenario Runner")]
        public static void ShowWindow()
        {
            var window = GetWindow<ScenarioRunner>("Scenario Runner");
            window.minSize = new Vector2(400, 300);
        }

        private void OnEnable()
        {
            _configPath = EditorPrefs.GetString(CONFIG_PATH_PREF_KEY, DEFAULT_CONFIG_PATH);
            RegisterExecutors();
            LoadConfig();
        }

        private void RegisterExecutors()
        {
            TaskExecutorRegistry.Register(new SceneTaskExecutor());
#if HAS_UNITY_LOCALIZATION
            TaskExecutorRegistry.Register(new LocalizationTaskExecutor());
#endif
            TaskExecutorRegistry.Register(new TmproTaskExecutor());
        }

        private void OnFocus()
        {
            // Reload scenarios when window gets focus (auto-refresh after external changes)
            LoadConfig();
        }

        private void LoadConfig()
        {
            Debug.Log($"[ScenarioRunner] LoadConfig {_configPath} {File.Exists(_configPath)}");

            // Reset running state on reload
            if (_isRunning)
            {
                StopTaskRunner();
            }

            if (File.Exists(_configPath))
            {
                Debug.Log("[ScenarioRunner] LoadConfig file exist");
                try
                {
                    string json = File.ReadAllText(_configPath);
                    _config = JsonUtility.FromJson<ScenarioConfig>(json);
                    Debug.Log($"[ScenarioRunner] Loaded {_config.scenarios.Count} scenarios with {CountTotalTasks()} total tasks");

                    // Auto-select if only one scenario
                    if (_config.scenarios.Count == 1)
                    {
                        _selectedScenario = _config.scenarios[0];
                        Debug.Log($"[ScenarioRunner] Auto-opened single scenario: {_selectedScenario.name}");
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError($"[ScenarioRunner] Failed to load scenarios: {e.Message}");
                    _config = new ScenarioConfig();
                }
            }
            else
            {
                Debug.LogWarning($"[ScenarioRunner] Scenario config not found. Use example in README.");
                _config = new ScenarioConfig();
            }
        }

        private int CountTotalTasks()
        {
            int count = 0;
            foreach (var scenario in _config.scenarios)
            {
                count += GetAllTasks(scenario).Count;
            }
            return count;
        }

        private List<Task> GetAllTasks(Scenario scenario)
        {
            var allTasks = new List<Task>();
            
            // Support new grouped format
            if (scenario.taskGroups != null && scenario.taskGroups.Count > 0)
            {
                foreach (var group in scenario.taskGroups)
                {
                    allTasks.AddRange(GetTasksFromGroup(group));
                }
            }
            // Fallback to old flat format
            else if (scenario.tasks != null)
            {
                allTasks.AddRange(scenario.tasks);
            }
            
            return allTasks;
        }

        private List<Task> GetTasksFromGroup(TaskGroup group)
        {
            var tasks = new List<Task>();
            
            // Add tasks from this group
            if (group.tasks != null)
                tasks.AddRange(group.tasks);
            
            // Recursively add tasks from subgroups
            if (group.subGroups != null)
            {
                foreach (var subGroup in group.subGroups)
                {
                    tasks.AddRange(GetTasksFromGroup(subGroup));
                }
            }
            
            return tasks;
        }

        private Scenario _selectedScenario = null;

        private void OnGUI()
        {
            GUILayout.Label("Scenario Runner", EditorStyles.boldLabel);
            GUILayout.Space(10);

            // Scenario file path
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Scenario File:", GUILayout.Width(85));
            string newPath = EditorGUILayout.TextField(_configPath);
            if (newPath != _configPath)
            {
                _configPath = newPath;
                EditorPrefs.SetString(CONFIG_PATH_PREF_KEY, _configPath);
            }
            if (GUILayout.Button("...", GUILayout.Width(30)))
            {
                string selected = EditorUtility.OpenFilePanel("Select Scenario File", "Assets", "json");
                if (!string.IsNullOrEmpty(selected))
                {
                    // Convert absolute path to project-relative path
                    if (selected.StartsWith(Application.dataPath))
                        selected = "Assets" + selected.Substring(Application.dataPath.Length);
                    _configPath = selected;
                    EditorPrefs.SetString(CONFIG_PATH_PREF_KEY, _configPath);
                    _selectedScenario = null;
                    LoadConfig();
                }
            }
            if (GUILayout.Button("Reload", GUILayout.Width(55)))
            {
                _selectedScenario = null;
                LoadConfig();
            }
            EditorGUILayout.EndHorizontal();

            if (_config == null || _config.scenarios == null || _config.scenarios.Count == 0)
            {
                EditorGUILayout.HelpBox("No scenarios loaded. Select a valid .json scenario file above.", MessageType.Warning);
                return;
            }

            GUILayout.Space(10);

            // If scenario is selected, show task details
            if (_selectedScenario != null)
            {
                DrawScenarioDetails(_selectedScenario);
            }
            else
            {
                DrawScenarioList();
            }
        }

        private void DrawScenarioList()
        {
            EditorGUILayout.LabelField($"Available Scenarios ({_config.scenarios.Count})", EditorStyles.boldLabel);
            GUILayout.Space(5);

            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);

            foreach (var scenario in _config.scenarios)
            {
                EditorGUILayout.BeginVertical("box");

                // Scenario name with status
                string statusIcon = GetScenarioStatusIcon(scenario);
                EditorGUILayout.LabelField($"{statusIcon} {scenario.name}", EditorStyles.boldLabel);

                // Description
                if (!string.IsNullOrEmpty(scenario.description))
                {
                    EditorGUILayout.LabelField(scenario.description, EditorStyles.wordWrappedLabel);
                }

                // Task progress
                var allTasks = GetAllTasks(scenario);
                int totalTasks = allTasks.Count;
                int completedTasks = allTasks.Where(t => t.status == "success").Count();
                int failedTasks = allTasks.Where(t => t.status == "failed").Count();
                int skippedTasks = allTasks.Where(t => t.status == "skipped").Count();
                
                EditorGUILayout.LabelField($"Progress: {completedTasks}/{totalTasks} ✅  {failedTasks} ❌  {skippedTasks} ⏭️", EditorStyles.miniLabel);

                GUILayout.Space(5);

                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Open", GUILayout.Height(30)))
                {
                    _selectedScenario = scenario;
                }
                if (GUILayout.Button("Reset Status", GUILayout.Height(30), GUILayout.Width(100)))
                {
                    ResetScenarioStatus(scenario);
                }
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.EndVertical();
                GUILayout.Space(5);
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawScenarioDetails(Scenario scenario)
        {
            // Back button
            if (GUILayout.Button("← Back to Scenarios"))
            {
                _selectedScenario = null;
                return;
            }

            GUILayout.Space(10);

            // Scenario header
            EditorGUILayout.LabelField(scenario.name, EditorStyles.boldLabel);
            if (!string.IsNullOrEmpty(scenario.description))
            {
                EditorGUILayout.LabelField(scenario.description, EditorStyles.wordWrappedLabel);
            }

            GUILayout.Space(10);

            // Global actions
            EditorGUILayout.BeginHorizontal();

            // Show Stop button when running, Run button otherwise
            if (_isRunning)
            {
                UnityEngine.GUI.backgroundColor = Color.red;
                if (GUILayout.Button("⏹ Stop", GUILayout.Height(30)))
                {
                    StopScenario(scenario);
                }
                UnityEngine.GUI.backgroundColor = Color.white;
            }
            else
            {
                if (GUILayout.Button("▶ Run All Pending", GUILayout.Height(30)))
                {
                    RunAllPendingTasks(scenario);
                }
            }

            UnityEngine.GUI.enabled = !_isRunning;
            if (GUILayout.Button("Reset All Status", GUILayout.Height(30)))
            {
                ResetScenarioStatus(scenario);
            }
            UnityEngine.GUI.enabled = true;
            EditorGUILayout.EndHorizontal();

            // Continue on error toggle
            EditorGUILayout.BeginHorizontal();
            bool newContinueOnError = EditorGUILayout.Toggle("Continue on Error", scenario.continueOnError);
            if (newContinueOnError != scenario.continueOnError)
            {
                scenario.continueOnError = newContinueOnError;
                SaveConfig();
            }
            EditorGUILayout.LabelField("(Skip failed tasks and continue)", EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();

            GUILayout.Space(10);

            // Task list
            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);

            // Support new grouped format
            if (scenario.taskGroups != null && scenario.taskGroups.Count > 0)
            {
                int taskIndex = 0;
                foreach (var group in scenario.taskGroups)
                {
                    DrawTaskGroup(scenario, group, ref taskIndex);
                }
            }
            // Fallback to old flat format
            else if (scenario.tasks != null)
            {
                for (int i = 0; i < scenario.tasks.Count; i++)
                {
                    DrawTaskRow(scenario, scenario.tasks[i], i);
                }
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawTaskGroup(Scenario scenario, TaskGroup group, ref int taskIndex)
        {
            EditorGUILayout.BeginVertical("box");

            // Group header
            EditorGUILayout.BeginHorizontal();

            // Expand/collapse button
            string arrow = group.expanded ? "▼" : "▶";
            if (GUILayout.Button(arrow, GUILayout.Width(25)))
            {
                group.expanded = !group.expanded;
            }

            // Group status and name
            string groupStatus = GetGroupStatus(group);
            EditorGUILayout.LabelField($"{groupStatus} {group.name}", EditorStyles.boldLabel);

            EditorGUILayout.EndHorizontal();

            // Group description
            if (!string.IsNullOrEmpty(group.description))
            {
                EditorGUILayout.LabelField(group.description, EditorStyles.miniLabel);
            }

            // Group progress (includes nested subgroups)
            var allGroupTasks = GetTasksFromGroup(group);
            int totalTasks = allGroupTasks.Count;
            int completedTasks = allGroupTasks.Where(t => t.status == "success").Count();
            int failedTasks = allGroupTasks.Where(t => t.status == "failed").Count();
            int subGroupCount = group.subGroups != null ? group.subGroups.Count : 0;
            
            string progressInfo = subGroupCount > 0 
                ? $"{completedTasks}/{totalTasks} tasks, {subGroupCount} subgroups  ({failedTasks} failed)"
                : $"{completedTasks}/{totalTasks} tasks  ({failedTasks} failed)";
            EditorGUILayout.LabelField(progressInfo, EditorStyles.miniLabel);

            // Group actions
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Run All in Group", GUILayout.Height(25)))
            {
                RunTaskGroup(scenario, group);
            }
            if (GUILayout.Button("Reset Group", GUILayout.Height(25), GUILayout.Width(100)))
            {
                ResetTaskGroup(group);
                SaveConfig();
                Repaint();
            }
            EditorGUILayout.EndHorizontal();

            // Show contents when expanded
            if (group.expanded)
            {
                GUILayout.Space(5);
                EditorGUI.indentLevel++;
                
                // Draw subgroups first
                if (group.subGroups != null && group.subGroups.Count > 0)
                {
                    foreach (var subGroup in group.subGroups)
                    {
                        DrawTaskGroup(scenario, subGroup, ref taskIndex);
                    }
                }
                
                // Then draw tasks
                if (group.tasks != null)
                {
                    for (int i = 0; i < group.tasks.Count; i++)
                    {
                        DrawTaskRow(scenario, group.tasks[i], taskIndex);
                        taskIndex++;
                    }
                }
                
                EditorGUI.indentLevel--;
            }
            else
            {
                // Increment task index even when collapsed (count all nested tasks)
                taskIndex += GetTasksFromGroup(group).Count;
            }

            EditorGUILayout.EndVertical();
            GUILayout.Space(5);
        }

        private string GetGroupStatus(TaskGroup group)
        {
            var allGroupTasks = GetTasksFromGroup(group);
            if (allGroupTasks.Count == 0)
                return "⏸️";

            int success = allGroupTasks.Where(t => t.status == "success").Count();
            int failed = allGroupTasks.Where(t => t.status == "failed").Count();
            int total = allGroupTasks.Count;

            if (failed > 0) return "❌";
            if (success == total) return "✅";
            if (success > 0) return "▶️";
            return "⏸️";
        }

        private void RunTaskGroup(Scenario scenario, TaskGroup group)
        {
            Debug.Log($"[ScenarioRunner] ▶️ Running group: {group.name}");
            
            var executor = new SceneTaskExecutor();
            int successCount = 0;
            int failCount = 0;
            
            // Get all tasks including from nested subgroups
            var allGroupTasks = GetTasksFromGroup(group);

            for (int i = 0; i < allGroupTasks.Count; i++)
            {
                var task = allGroupTasks[i];
                
                if (task.status != "pending" && task.status != "failed")
                    continue;

                Debug.Log($"[ScenarioRunner]   [{i + 1}/{allGroupTasks.Count}] {task.action}");
                task.status = "running";

                try
                {
                    bool success = executor.Execute(task);
                    if (success)
                    {
                        task.status = "success";
                        task.error = "";
                        successCount++;
                    }
                    else
                    {
                        task.status = "failed";
                        failCount++;
                        Debug.LogError($"[ScenarioRunner]   ❌ Task failed: {task.action}\n  {task.error}");
                        Debug.LogError("[ScenarioRunner] Stopping scenario due to error");
                        break;
                    }
                }
                catch (Exception e)
                {
                    task.status = "failed";
                    task.error = e.Message;
                    failCount++;
                    Debug.LogError($"[ScenarioRunner]   ❌ Task failed: {task.action}\n  {e.Message}");
                    Debug.LogError("[ScenarioRunner] Stopping scenario due to error");
                    break;
                }
            }

            UpdateScenarioStatus(scenario);
            SaveConfig();
            Debug.Log($"[ScenarioRunner] ✅ Group '{group.name}' completed: {successCount} success, {failCount} failed");
            Repaint();
        }

        private void ResetTaskGroup(TaskGroup group)
        {
            // Reset tasks in this group
            if (group.tasks != null)
            {
                foreach (var task in group.tasks)
                {
                    task.status = "pending";
                    task.error = "";
                }
            }
            
            // Recursively reset subgroups
            if (group.subGroups != null)
            {
                foreach (var subGroup in group.subGroups)
                {
                    ResetTaskGroup(subGroup);
                }
            }
            
            Debug.Log($"[ScenarioRunner] 🔄 Reset group: {group.name}");
        }

        private void DrawTaskRow(Scenario scenario, Task task, int index)
        {
            EditorGUILayout.BeginVertical("box");

            // Task header with user-friendly formatting
            string statusIcon = GetTaskStatusIcon(task);
            string displayText = FormatTaskDisplay(task, index);
            EditorGUILayout.LabelField($"{statusIcon} {displayText}", EditorStyles.boldLabel);

            // Parameters - SHOW FIRST so user can decide
            if (task.parameters != null && task.parameters.Count > 0)
            {
                EditorGUILayout.LabelField("Parameters:", EditorStyles.miniLabel);
                foreach (var param in task.parameters)
                {
                    EditorGUILayout.LabelField($"  • {param.key}: {param.value}", EditorStyles.miniLabel);
                }
                GUILayout.Space(5);
            }

            // Error message (if any)
            if (!string.IsNullOrEmpty(task.error))
            {
                EditorGUILayout.HelpBox(task.error, MessageType.Error);
            }

            // Action buttons - AFTER parameters
            EditorGUILayout.BeginHorizontal();
            
            UnityEngine.GUI.enabled = task.status == "pending" || task.status == "failed";
            if (GUILayout.Button("Run", GUILayout.Height(25)))
            {
                RunSingleTask(scenario, task, index);
            }
            UnityEngine.GUI.enabled = task.status == "pending";
            if (GUILayout.Button("Skip", GUILayout.Height(25)))
            {
                SkipTask(scenario, task);
            }
            UnityEngine.GUI.enabled = task.status != "pending";
            if (GUILayout.Button("Reset", GUILayout.Height(25)))
            {
                ResetTask(scenario, task);
            }
            UnityEngine.GUI.enabled = true;

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
            GUILayout.Space(3);
        }

        private string GetScenarioStatusIcon(Scenario scenario)
        {
            switch (scenario.status)
            {
                case "completed": return "✅";
                case "failed": return "❌";
                case "running": return "▶️";
                default: return "⏸️";
            }
        }

        private string GetTaskStatusIcon(Task task)
        {
            switch (task.status)
            {
                case "success": return "✅";
                case "failed": return "❌";
                case "skipped": return "⏭️";
                case "running": return "▶️";
                default: return "⏸️";
            }
        }

        private string FormatTaskDisplay(Task task, int index)
        {
            string prefix = $"[{index + 1}] ";
            
            // Format based on action type
            switch (task.action)
            {
                case "CreateScene":
                    return $"{prefix}Create Scene \"{GetParam(task, "name")}\"";
                
                case "AddGameObject":
                    string parent = GetParam(task, "parent");
                    string goName = GetParam(task, "name");
                    return string.IsNullOrEmpty(parent) 
                        ? $"{prefix}Add GameObject \"{goName}\""
                        : $"{prefix}Add GameObject \"{goName}\" under \"{parent}\"";
                
                case "AddComponent":
                    return $"{prefix}Add {GetParam(task, "component")} to \"{GetParam(task, "path")}\"";
                
                case "InstantiatePrefab":
                    string prefabPath = GetParam(task, "prefabPath");
                    string prefabName = System.IO.Path.GetFileNameWithoutExtension(prefabPath);
                    return $"{prefix}Instantiate Prefab \"{prefabName}\" as \"{GetParam(task, "name")}\"";
                
                case "SetProperty":
                    return $"{prefix}Set {GetParam(task, "component")}.{GetParam(task, "field")} on \"{GetParam(task, "path")}\"";
                
                case "SetTransform":
                    return $"{prefix}Set Transform on \"{GetParam(task, "path")}\"";
                
                case "SetRectTransform":
                    return $"{prefix}Set RectTransform on \"{GetParam(task, "path")}\"";
                
                case "SaveScene":
                    string scenePath = GetParam(task, "path");
                    string sceneName = System.IO.Path.GetFileNameWithoutExtension(scenePath);
                    return $"{prefix}Save Scene as \"{sceneName}\"";
                
                case "CreateCanvas":
                    return $"{prefix}Create Canvas \"{GetParam(task, "name")}\"";
                
                default:
                    return $"{prefix}{task.action}";
            }
        }

        private string GetParam(Task task, string key)
        {
            if (task.parameters != null)
            {
                var param = task.parameters.Find(p => p.key == key);
                if (param != null)
                    return param.value;
            }
            return "";
        }

        private void RunSingleTask(Scenario scenario, Task task, int index)
        {
            Debug.Log($"[ScenarioRunner] ▶️ Running task [{index + 1}]: {task.action}");
            
            task.status = "running";
            Repaint();

            var executor = new SceneTaskExecutor();
            
            try
            {
                bool success = executor.Execute(task);
                if (success)
                {
                    task.status = "success";
                    task.error = "";
                    Debug.Log($"[ScenarioRunner]   ✅ Task completed: {task.action}");
                }
                else
                {
                    task.status = "failed";
                    Debug.LogError($"[ScenarioRunner]   ❌ Task failed: {task.action}\n  {task.error}");
                }
            }
            catch (Exception e)
            {
                task.status = "failed";
                task.error = e.Message;
                Debug.LogError($"[ScenarioRunner]   ❌ Task failed: {task.action}\n  {e.Message}");
            }

            UpdateScenarioStatus(scenario);
            SaveConfig();
            Repaint();
        }

        // Track running state to prevent multiple simultaneous runs
        private bool _isRunning;
        private bool _stopRequested;

        // State for the update-based task runner
        private Scenario _runningScenario;
        private List<Task> _runningTasks;
        private int _currentTaskIndex;
        private int _successCount;
        private int _failCount;
        private bool _waitingForAsync;

        /// <summary>
        /// Stop the currently running scenario. Will stop before the next task starts.
        /// </summary>
        private void StopScenario(Scenario scenario)
        {
            if (_isRunning)
            {
                _stopRequested = true;
                StopTaskRunner();
                Debug.Log("[ScenarioRunner] ⏹ Scenario stopped");

                // Update any running task to pending
                var allTasks = GetAllTasks(scenario);
                foreach (var task in allTasks)
                {
                    if (task.status == "running")
                    {
                        task.status = "pending";
                    }
                }

                UpdateScenarioStatus(scenario);
                SaveConfig();
                Repaint();
            }
        }

        /// <summary>
        /// Unsubscribes from EditorApplication.update and resets running state.
        /// </summary>
        private void StopTaskRunner()
        {
            EditorApplication.update -= RunTaskStep;
            _isRunning = false;
            _stopRequested = false;
            _runningScenario = null;
            _runningTasks = null;
        }

        private void RunAllPendingTasks(Scenario scenario)
        {
            if (_isRunning)
            {
                Debug.LogWarning("[ScenarioRunner] A scenario is already running. Please wait for it to complete.");
                return;
            }

            Debug.Log($"[ScenarioRunner] ▶️ Running all pending tasks for: {scenario.name}");
            scenario.status = "running";

            _runningScenario = scenario;
            _runningTasks = GetAllTasks(scenario);
            _currentTaskIndex = 0;
            _successCount = 0;
            _failCount = 0;
            _stopRequested = false;
            _waitingForAsync = false;
            _isRunning = true;

            EditorApplication.update += RunTaskStep;
        }

        /// <summary>
        /// Called every editor frame via EditorApplication.update.
        /// Processes one task per frame, waits for async tasks to complete.
        /// </summary>
        private void RunTaskStep()
        {
            // If waiting for an async task, check if it's done
            if (_waitingForAsync)
            {
                var asyncTask = _runningTasks[_currentTaskIndex];
                if (!asyncTask.asyncCompleted)
                    return; // Still waiting, check again next frame

                // Async task completed
                _waitingForAsync = false;
                bool success = asyncTask.asyncSuccess;
                string errorMsg = asyncTask.error;

                if (!HandleTaskResult(asyncTask, success, errorMsg))
                    return; // Scenario stopped due to error

                _currentTaskIndex++;
                SaveConfig();
                Repaint();
                return;
            }

            // Check for stop request
            if (_stopRequested)
            {
                Debug.Log($"[ScenarioRunner] ⏹ Scenario stopped by user at task {_currentTaskIndex + 1}/{_runningTasks.Count}");
                UpdateScenarioStatus(_runningScenario);
                SaveConfig();
                Repaint();
                StopTaskRunner();
                return;
            }

            // Find the next pending/failed task
            while (_currentTaskIndex < _runningTasks.Count)
            {
                var task = _runningTasks[_currentTaskIndex];
                if (task.status == "pending" || task.status == "failed")
                    break;
                _currentTaskIndex++;
            }

            // All tasks processed
            if (_currentTaskIndex >= _runningTasks.Count)
            {
                UpdateScenarioStatus(_runningScenario);
                SaveConfig();
                Debug.Log($"[ScenarioRunner] ✅ Completed: {_successCount} success, {_failCount} failed");
                Repaint();
                StopTaskRunner();
                return;
            }

            // Execute the current task
            var currentTask = _runningTasks[_currentTaskIndex];
            BaseTaskExecutor executor = GetExecutor(currentTask);

            Debug.Log($"[ScenarioRunner]   [{_currentTaskIndex + 1}/{_runningTasks.Count}] {currentTask.action}");
            currentTask.status = "running";

            // Reset async flags
            currentTask.isAsync = false;
            currentTask.asyncCompleted = false;
            currentTask.asyncSuccess = false;

            Repaint();

            bool taskSuccess = false;
            string taskError = "";
            bool isAsyncTask = false;

            try
            {
                taskSuccess = executor.Execute(currentTask);
                isAsyncTask = taskSuccess && executor.IsAsyncTask(currentTask);
            }
            catch (Exception e)
            {
                taskSuccess = false;
                taskError = e.Message;
            }

            // If async, wait for completion on subsequent frames
            if (isAsyncTask)
            {
                currentTask.isAsync = true;
                _waitingForAsync = true;
                Debug.Log($"[ScenarioRunner]   ⏳ Waiting for async task to complete: {currentTask.action}");
                return; // Will check asyncCompleted on next frame
            }

            // Synchronous task - handle result immediately
            if (!HandleTaskResult(currentTask, taskSuccess, taskError))
                return; // Scenario stopped due to error

            _currentTaskIndex++;
            SaveConfig();
            Repaint();
        }

        /// <summary>
        /// Handles the result of a completed task. Returns false if the scenario should stop.
        /// </summary>
        private bool HandleTaskResult(Task task, bool success, string errorMsg)
        {
            if (success)
            {
                task.status = "success";
                task.error = "";
                _successCount++;
                Debug.Log($"[ScenarioRunner]   ✅ Task completed: {task.action}");
            }
            else
            {
                task.status = "failed";
                if (!string.IsNullOrEmpty(errorMsg))
                    task.error = errorMsg;
                _failCount++;
                Debug.LogError($"[ScenarioRunner]   ❌ Task failed: {task.action}\n  {task.error}");

                if (!_runningScenario.continueOnError)
                {
                    Debug.LogError("[ScenarioRunner] Stopping scenario due to error (continueOnError is OFF)");
                    UpdateScenarioStatus(_runningScenario);
                    SaveConfig();
                    Repaint();
                    StopTaskRunner();
                    return false;
                }

                Debug.LogWarning("[ScenarioRunner] Continuing to next task (continueOnError is ON)");
            }

            return true;
        }

        private BaseTaskExecutor GetExecutor(Task task)
        {
            switch (task.type)
            {
#if HAS_UNITY_LOCALIZATION
                case BaseTaskExecutor.ExecutorType.LOCALIZATION:
                    return new LocalizationTaskExecutor();
#endif
                case BaseTaskExecutor.ExecutorType.SCENE:
                    return new SceneTaskExecutor();
                default:
                    throw new ArgumentException($"Unknown executor type: {task.type}");
            }
        }

        private void SkipTask(Scenario scenario, Task task)
        {
            task.status = "skipped";
            task.error = "";
            Debug.Log($"[ScenarioRunner] ⏭️ Skipped task: {task.action}");
            UpdateScenarioStatus(scenario);
            SaveConfig();
            Repaint();
        }

        private void ResetTask(Scenario scenario, Task task)
        {
            task.status = "pending";
            task.error = "";
            UpdateScenarioStatus(scenario);
            SaveConfig();
            Repaint();
        }

        private void ResetScenarioStatus(Scenario scenario)
        {
            scenario.status = "pending";
            scenario.lastRun = "";
            
            // Reset all tasks in groups
            if (scenario.taskGroups != null)
            {
                foreach (var group in scenario.taskGroups)
                {
                    if (group.tasks != null)
                    {
                        foreach (var task in group.tasks)
                        {
                            task.status = "pending";
                            task.error = "";
                        }
                    }
                }
            }
            // Fallback to old format
            else if (scenario.tasks != null)
            {
                foreach (var task in scenario.tasks)
                {
                    task.status = "pending";
                    task.error = "";
                }
            }
            
            SaveConfig();
            Debug.Log($"[ScenarioRunner] 🔄 Reset status for: {scenario.name}");
            Repaint();
        }

        private void UpdateScenarioStatus(Scenario scenario)
        {
            var allTasks = GetAllTasks(scenario);
            int total = allTasks.Count;
            int success = allTasks.Where(t => t.status == "success").Count();
            int failed = allTasks.Where(t => t.status == "failed").Count();
            int pending = allTasks.Where(t => t.status == "pending").Count();

            if (failed > 0)
            {
                scenario.status = "failed";
            }
            else if (pending == 0)
            {
                scenario.status = "completed";
            }
            else if (success > 0)
            {
                scenario.status = "running";
            }

            scenario.lastRun = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
        }

        private void SaveConfig()
        {
            try
            {
                string json = JsonUtility.ToJson(_config, true);
                File.WriteAllText(_configPath, json);
                AssetDatabase.Refresh();
            }
            catch (Exception e)
            {
                Debug.LogError($"[ScenarioRunner] Failed to save config: {e.Message}");
            }
        }
    }

    [Serializable]
    public class ScenarioConfig
    {
        public List<Scenario> scenarios = new List<Scenario>();
    }

    [Serializable]
    public class Scenario
    {
        public string name;
        public string description;
        public string status = "pending"; // pending, running, completed, failed
        public string lastRun = "";
        public bool continueOnError = false; // If true, continue to next task even if current fails
        public List<TaskGroup> taskGroups = new List<TaskGroup>();

        // For backward compatibility with old format
        public List<Task> tasks = new List<Task>();
    }

    [Serializable]
    public class TaskGroup
    {
        public string name;
        public string description;
        public List<Task> tasks = new List<Task>();
        public List<TaskGroup> subGroups = new List<TaskGroup>(); // Recursive groups
        public bool expanded = false; // UI state
    }

    [Serializable]
    public class Task
    {
        public BaseTaskExecutor.ExecutorType type;   // Default to scene for backward compatibility
        public string action;
        public List<TaskParameter> parameters = new List<TaskParameter>();
        public string status = "pending"; // pending, success, failed, skipped, running
        public string error = "";
        public string result = "";  // Two-way communication: tasks can store output/results here

        // Async task support - for long-running operations
        [NonSerialized] public bool isAsync = false;           // Executor marks task as async
        [NonSerialized] public bool asyncCompleted = false;    // Set when async operation finishes
        [NonSerialized] public bool asyncSuccess = false;      // Result of async operation
    }

    [Serializable]
    public class TaskParameter
    {
        public string key;
        public string value;
    }
}
