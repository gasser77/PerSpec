using System;
using System.Linq;
using System.IO;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using PerSpec.Editor.Services;
using PerSpec.Editor.Coordination;
using PerSpec.Editor.Initialization;

namespace PerSpec.Editor.Windows
{
    /// <summary>
    /// Central control panel for all PerSpec features
    /// </summary>
    public class PerSpecControlCenter : EditorWindow
    {
        #region Constants
        
        private const string WINDOW_TITLE = "Control Center";
        private static readonly Vector2 MIN_SIZE = new Vector2(600, 500);

        private static readonly string[] LLM_OPTIONS = new string[]
        {
            "Claude (CLAUDE.md)",
            "Cursor (.cursorrules)",
            "Agents (AGENTS.md)",
            "Gemini CLI (GEMINI.md)",
            "Aider (Conventions.md)",
            "Windsurf (.windsurf/rules/)",
            "OpenAI (.openai.md)",
            "DeepSeek (.deepseek.md)"
        };
        
        #endregion
        
        #region Fields
        
        private int selectedTab = 0;
        private string[] tabNames = new string[]
        {
            "Dashboard",
            "Test Coordinator",
            "Debug Settings",
            "Initialization",
            "LLM Setup",
            "About"
        };
        
        private Vector2 scrollPosition;
        private GUIStyle headerStyle;
        private GUIStyle sectionStyle;
        private GUIStyle statusStyle;

        // LLM Setup tab
        private Vector2 llmScrollPosition;
        
        #endregion
        
        #region Unity Menu
        
        [MenuItem("Tools/PerSpec/Control Center", priority = -200)]
        public static void ShowWindow()
        {
            var window = GetWindow<PerSpecControlCenter>(false, WINDOW_TITLE);
            window.minSize = MIN_SIZE;
            window.Show();
        }
        
        [MenuItem("Tools/PerSpec/Quick Actions/Run Pending Tests", priority = 1)]
        private static void QuickRunTests()
        {
            if (TestCoordinationService.CheckPendingTests())
                Debug.Log("[PerSpec] Started pending tests");
            else
                Debug.Log("[PerSpec] No pending tests found");
        }
        
        [MenuItem("Tools/PerSpec/Quick Actions/Toggle Debug Logging", priority = 2)]
        private static void QuickToggleDebug()
        {
            DebugService.ToggleDebugLogging();
        }
        
        [MenuItem("Tools/PerSpec/Quick Actions/Open Working Directory", priority = 3)]
        private static void QuickOpenDirectory()
        {
            InitializationService.OpenWorkingDirectory();
        }
        
        #endregion
        
        #region Unity Lifecycle
        
        private void OnEnable()
        {
            titleContent = new GUIContent(WINDOW_TITLE, EditorGUIUtility.IconContent("d_Settings").image);
        }
        
        private void OnGUI()
        {
            InitStyles();
            
            // Header
            DrawHeader();
            
            // Tab bar
            EditorGUILayout.Space(5);
            selectedTab = GUILayout.Toolbar(selectedTab, tabNames, GUILayout.Height(30));
            EditorGUILayout.Space(5);
            
            // Tab content
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
            
            switch (selectedTab)
            {
                case 0: DrawDashboardTab(); break;
                case 1: DrawTestCoordinatorTab(); break;
                case 2: DrawDebugSettingsTab(); break;
                case 3: DrawInitializationTab(); break;
                case 4: DrawLLMSetupTab(); break;
                case 5: DrawAboutTab(); break;
            }
            
            EditorGUILayout.EndScrollView();
        }
        
        #endregion
        
        #region GUI Drawing - Header
        
        private void InitStyles()
        {
            if (headerStyle == null)
            {
                headerStyle = new GUIStyle(EditorStyles.largeLabel)
                {
                    fontSize = 18,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.MiddleCenter
                };
            }
            
            if (sectionStyle == null)
            {
                sectionStyle = new GUIStyle(EditorStyles.helpBox)
                {
                    padding = new RectOffset(10, 10, 10, 10)
                };
            }
            
            if (statusStyle == null)
            {
                statusStyle = new GUIStyle(EditorStyles.label)
                {
                    richText = true,
                    alignment = TextAnchor.MiddleCenter
                };
            }
        }
        
        private void DrawHeader()
        {
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Control Center", headerStyle);
            EditorGUILayout.LabelField("Unity Test Framework with TDD & SQLite Coordination", 
                new GUIStyle(EditorStyles.centeredGreyMiniLabel));
        }
        
        #endregion
        
        #region GUI Drawing - Dashboard Tab
        
        private void DrawDashboardTab()
        {
            // Quick Status
            DrawSection("System Status", () =>
            {
                DrawStatusRow("Initialization", InitializationService.IsInitialized ? "✓ Ready" : "✗ Not initialized", 
                    InitializationService.IsInitialized ? Color.green : Color.red);
                
                DrawStatusRow("Test Coordinator", TestCoordinationService.GetStatusSummary(), 
                    TestCoordinationService.IsRunningTests ? Color.yellow : Color.green);
                
                DrawStatusRow("Debug Logging", DebugService.DebugStatus,
                    DebugService.IsDebugEnabled ? Color.green : Color.gray);

                DrawStatusRow("DOTS Support", DOTSService.DOTSStatus,
                    DOTSService.IsDOTSEnabled ? Color.cyan : Color.gray);

                DrawStatusRow("Compiler Directives", BuildProfileHelper.ConfigurationMode,
                    BuildProfileHelper.HasActiveBuildProfile ? Color.cyan : Color.white);
            });
            
            EditorGUILayout.Space(10);
            
            // Quick Actions
            DrawSection("Quick Actions", () =>
            {
                // Single Update Scripts button
                if (GUILayout.Button("Update Scripts", GUILayout.Height(35)))
                {
                    var result = InitializationService.RefreshCoordinationScripts();
                    if (!string.IsNullOrEmpty(result))
                    {
                        ShowNotification(new GUIContent(result.Replace("\n", " ")), 3.0f);
                    }
                    else
                    {
                        ShowNotification(new GUIContent("Failed to update scripts"));
                    }
                }

                EditorGUILayout.Space(5);

                // Enable/Disable PerSpec toggle
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("PerSpec Status:", GUILayout.Width(100));

                bool isEnabled = PerSpecSettings.IsEnabled;
                GUI.backgroundColor = isEnabled ? Color.green : Color.red;
                string toggleText = isEnabled ? "Enabled" : "Disabled";

                if (GUILayout.Button(toggleText, GUILayout.Height(25)))
                {
                    PerSpecSettings.Toggle();
                    Repaint();
                }
                GUI.backgroundColor = Color.white;

                EditorGUILayout.EndHorizontal();

                if (!isEnabled)
                {
                    EditorGUILayout.HelpBox(
                        "PerSpec is currently DISABLED. All polling, logging, and test coordination features are inactive.",
                        MessageType.Warning
                    );
                }

                EditorGUILayout.Space(10);

                // .gitignore management
                EditorGUILayout.BeginHorizontal();

                if (GUILayout.Button("Add to .gitignore", GUILayout.Height(25)))
                {
                    InitializationService.UpdateGitIgnore();
                    ShowNotification(new GUIContent("PerSpec added to .gitignore"));
                }

                GUI.backgroundColor = Color.red;
                if (GUILayout.Button("Remove PerSpec", GUILayout.Height(25)))
                {
                    if (EditorUtility.DisplayDialog(
                        "Remove PerSpec",
                        "WARNING: This will DELETE the PerSpec directory and all its contents!\n\n" +
                        "Compiler directives will be removed:\n" +
                        "• PERSPEC_DEBUG\n" +
                        "• PERSPEC_DOTS_ENABLED\n\n" +
                        "This includes:\n" +
                        "• Test results\n" +
                        "• Database\n" +
                        "• Logs\n" +
                        "• Scripts\n\n" +
                        "This cannot be undone. Are you sure?",
                        "Remove PerSpec", "Cancel"))
                    {
                        // Remove compiler directives first
                        BuildProfileHelper.RemoveCompilerDirective("PERSPEC_DEBUG");
                        BuildProfileHelper.RemoveCompilerDirective("PERSPEC_DOTS_ENABLED");

                        if (PerSpecSettings.RemovePerSpec())
                        {
                            Close();
                        }
                    }
                }
                GUI.backgroundColor = Color.white;

                EditorGUILayout.EndHorizontal();
            });
            
            EditorGUILayout.Space(10);
            
            // Statistics
            DrawSection("Statistics", () =>
            {
                EditorGUILayout.LabelField("Unity Version:", Application.unityVersion);
                EditorGUILayout.LabelField("Database Size:", 
                    $"{InitializationService.DatabaseSize / 1024f:F1} KB");
                EditorGUILayout.LabelField("Log Location:", 
                    "PerSpec/EditModeLogs/");
            });
        }
        
        #endregion
        
        #region GUI Drawing - Test Coordinator Tab
        
        private void DrawTestCoordinatorTab()
        {
            DrawInfoBox(
                "Test Coordinator manages automated test execution through SQLite database coordination. " +
                "Python scripts can submit test requests that Unity will automatically execute."
            );
            
            EditorGUILayout.Space(10);
            
            // Status
            DrawSection("Test Status", () =>
            {
                EditorGUILayout.LabelField("Status:", TestCoordinationService.GetStatusSummary());
                
                if (TestCoordinationService.IsRunningTests)
                {
                    EditorGUILayout.LabelField("Current Request:", 
                        $"#{TestCoordinationService.CurrentRequestId}");
                        
                    if (GUILayout.Button("Cancel Current Test"))
                    {
                        TestCoordinationService.CancelCurrentTest();
                    }
                }
            });
            
            EditorGUILayout.Space(10);
            
            // Controls
            DrawSection("Test Controls", () =>
            {
                bool polling = TestCoordinationService.PollingEnabled;
                bool newPolling = EditorGUILayout.Toggle("Auto-Polling Enabled", polling);
                if (newPolling != polling)
                {
                    TestCoordinationService.PollingEnabled = newPolling;
                }

                EditorGUILayout.Space(5);

                EditorGUILayout.HelpBox(
                    "Test execution and compilation controls have been moved to the Debug Settings tab.",
                    MessageType.Info
                );
            });
            
            EditorGUILayout.Space(10);
            
            // Database Info
            DrawSection("Database Status", () =>
            {
                EditorGUILayout.TextArea(TestCoordinationService.GetDatabaseStatus(), 
                    GUILayout.Height(100));
            });
            
            EditorGUILayout.Space(10);
            
            // Database Maintenance
            DrawSection("Database Maintenance", () =>
            {
                // Show database size
                var dbManager = new SQLiteManager();

                // Initialize / Migrate Database - always available, even when the DB is missing
                // or has a stale schema. Creates the DB if absent and self-heals constraints
                // (e.g. the asset_refresh_requests 'compiling' status) via the C# repair, then
                // runs the full Python migration sweep.
                if (GUILayout.Button("Initialize / Migrate Database", GUILayout.Height(30)))
                {
                    if (EditorUtility.DisplayDialog("Initialize / Migrate Database",
                        "Creates the database if it is missing and upgrades its schema to the " +
                        "current version (e.g. adds the 'compiling' refresh status). Safe to run anytime.\n\nContinue?",
                        "Run", "Cancel"))
                    {
                        bool ready = DatabaseInitializer.EnsureDatabaseReady();
                        DatabaseMaintenanceRunner.RunMaintenanceManually();
                        ShowNotification(new GUIContent(ready
                            ? "Database initialized / migrated"
                            : "Database init reported an error - check Console"));
                    }
                }

                EditorGUILayout.Space(5);

                if (dbManager.IsInitialized)
                {
                    long dbSize = dbManager.GetDatabaseSize();
                    float sizeMB = dbSize / (1024f * 1024f);
                    
                    // Color code based on size
                    Color sizeColor = Color.white;
                    if (sizeMB > 500) sizeColor = Color.red;
                    else if (sizeMB > 100) sizeColor = Color.yellow;
                    else if (sizeMB > 50) sizeColor = new Color(1f, 0.8f, 0f); // Orange
                    
                    GUI.color = sizeColor;
                    EditorGUILayout.LabelField("Database Size:", $"{sizeMB:F2} MB");
                    GUI.color = Color.white;
                    
                    // Show maintenance status
                    EditorGUILayout.LabelField("Maintenance Status:");
                    EditorGUILayout.TextArea(DatabaseMaintenanceService.GetMaintenanceStatus(), 
                        GUILayout.Height(60));
                    
                    EditorGUILayout.Space(5);
                    
                    // Maintenance buttons
                    EditorGUILayout.BeginHorizontal();
                    
                    if (GUILayout.Button("Clean Old Data (2h)", GUILayout.Height(25)))
                    {
                        if (EditorUtility.DisplayDialog("Clean Old Data", 
                            "This will delete all test results, logs, and execution data older than 2 hours.\n\nContinue?", 
                            "Clean", "Cancel"))
                        {
                            dbManager.PerformFullMaintenance(2);
                            ShowNotification(new GUIContent("Old data cleaned"));
                        }
                    }
                    
                    if (GUILayout.Button("Vacuum Database", GUILayout.Height(25)))
                    {
                        if (EditorUtility.DisplayDialog("Vacuum Database", 
                            "This will reclaim unused space in the database. The operation may take a moment.\n\nContinue?", 
                            "Vacuum", "Cancel"))
                        {
                            dbManager.VacuumDatabase();
                            ShowNotification(new GUIContent("Database vacuumed"));
                        }
                    }
                    
                    EditorGUILayout.EndHorizontal();
                    
                    EditorGUILayout.BeginHorizontal();
                    
                    if (sizeMB > 10)
                    {
                        GUI.color = Color.yellow;
                        if (GUILayout.Button("Aggressive Cleanup", GUILayout.Height(25)))
                        {
                            if (EditorUtility.DisplayDialog("Aggressive Cleanup", 
                                "This will delete ALL data older than 30 minutes and vacuum the database.\n\nContinue?", 
                                "Clean", "Cancel"))
                            {
                                // Aggressive cleanup (console logs now in files)
                                dbManager.DeleteOldTestResults(0);
                                dbManager.DeleteOldExecutionLogs(0);
                                dbManager.DeleteOldRefreshRequests(0);
                                dbManager.VacuumDatabase();
                                ShowNotification(new GUIContent("Aggressive cleanup complete"));
                            }
                        }
                        GUI.color = Color.white;
                    }
                    
                    if (sizeMB > 1)
                    {
                        GUI.color = Color.red;
                        if (GUILayout.Button("Reset Database", GUILayout.Height(25)))
                        {
                            if (EditorUtility.DisplayDialog("Reset Database", 
                                "WARNING: This will DELETE ALL data in the database!\n\nThis cannot be undone.\n\nAre you sure?", 
                                "Reset", "Cancel"))
                            {
                                dbManager.ResetDatabase();
                                ShowNotification(new GUIContent("Database reset complete"));
                            }
                        }
                        GUI.color = Color.white;
                    }
                    
                    EditorGUILayout.EndHorizontal();
                    
                    EditorGUILayout.Space(5);
                    
                    // Force maintenance button
                    if (GUILayout.Button("Force Scheduled Maintenance", GUILayout.Height(20)))
                    {
                        DatabaseMaintenanceService.ForceMaintenance();
                        ShowNotification(new GUIContent("Maintenance triggered"));
                    }
                }
                else
                {
                    EditorGUILayout.HelpBox("Database not initialized", MessageType.Warning);
                }
            });
        }
        
        #endregion
        
        #region GUI Drawing - Debug Settings Tab
        
        private void DrawDebugSettingsTab()
        {
            DrawInfoBox(
                "Debug Settings control PerSpec's debugging features, test execution, and compilation. " +
                "When debug logging is disabled, all PerSpecDebug.Log() calls are completely stripped with zero runtime overhead."
            );
            
            EditorGUILayout.Space(10);
            
            // Current Status
            DrawSection("Debug Status", () =>
            {
                string status = DebugService.IsDebugEnabled 
                    ? "● ENABLED - Debug logs will be included" 
                    : "● DISABLED - Debug logs are stripped";
                
                Color color = DebugService.IsDebugEnabled ? Color.green : Color.gray;
                GUI.color = color;
                EditorGUILayout.LabelField(status, statusStyle);
                GUI.color = Color.white;
                
                EditorGUILayout.Space(5);
                EditorGUILayout.LabelField("Configuration Mode:", BuildProfileHelper.ConfigurationMode);
                
            });
            
            EditorGUILayout.Space(10);

            // Test Execution Controls
            DrawSection("Test Execution", () =>
            {
                EditorGUILayout.BeginHorizontal();

                if (GUILayout.Button("Check Pending Tests", GUILayout.Height(30)))
                {
                    if (TestCoordinationService.CheckPendingTests())
                        ShowNotification(new GUIContent("Started pending tests"));
                    else
                        ShowNotification(new GUIContent("No pending tests"));
                }

                if (GUILayout.Button("Force Compilation", GUILayout.Height(30)))
                {
                    TestCoordinationService.ForceScriptCompilation();
                    ShowNotification(new GUIContent("Forcing script compilation"));
                }

                EditorGUILayout.EndHorizontal();
            });

            EditorGUILayout.Space(10);

            // Debug Logging Controls
            DrawSection("Debug Logging", () =>
            {
                EditorGUILayout.BeginHorizontal();

                GUI.backgroundColor = DebugService.IsDebugEnabled ? Color.red : Color.green;
                string buttonText = DebugService.IsDebugEnabled
                    ? "Disable Debug Logging"
                    : "Enable Debug Logging";

                if (GUILayout.Button(buttonText, GUILayout.Height(40)))
                {
                    DebugService.ToggleDebugLogging();
                }
                GUI.backgroundColor = Color.white;

                EditorGUILayout.EndHorizontal();

                EditorGUILayout.Space(10);

                if (GUILayout.Button("Test Log Levels"))
                {
                    DebugService.TestLogLevels();
                }
            });

            EditorGUILayout.Space(10);

            // DOTS/Entities Support
            DrawSection("DOTS/Entities Support", () =>
            {
                string dotsStatus = DOTSService.IsDOTSEnabled
                    ? "● ENABLED - DOTS code will be included"
                    : "● DISABLED - DOTS code is stripped";

                Color dotsColor = DOTSService.IsDOTSEnabled ? Color.cyan : Color.gray;
                GUI.color = dotsColor;
                EditorGUILayout.LabelField(dotsStatus, statusStyle);
                GUI.color = Color.white;

                EditorGUILayout.Space(5);

                EditorGUILayout.HelpBox(
                    "Enable DOTS support only if you have Unity.Entities package installed. " +
                    "When disabled, all DOTS-related code is stripped from compilation.",
                    MessageType.Info
                );

                EditorGUILayout.Space(10);

                EditorGUILayout.BeginHorizontal();

                GUI.backgroundColor = DOTSService.IsDOTSEnabled ? Color.red : Color.green;
                string dotsButtonText = DOTSService.IsDOTSEnabled
                    ? "Disable DOTS Support"
                    : "Enable DOTS Support";

                if (GUILayout.Button(dotsButtonText, GUILayout.Height(40)))
                {
                    DOTSService.ToggleDOTS();
                }
                GUI.backgroundColor = Color.white;

                EditorGUILayout.EndHorizontal();
            });

            EditorGUILayout.Space(10);

            // Usage Example
            DrawSection("Usage Example", () =>
            {
                string code = @"using PerSpec;

// These calls are stripped when disabled:
PerSpecDebug.Log(""[TEST] Starting test"");
PerSpecDebug.LogError(""[ERROR] Test failed"");
PerSpecDebug.LogTestSetup(""Creating prefab"");
PerSpecDebug.LogTestComplete(""Test passed"");";
                
                EditorGUILayout.TextArea(code, GUILayout.Height(100));
            });
        }
        
        #endregion
        
        #region GUI Drawing - Initialization Tab
        
        private void DrawInitializationTab()
        {
            DrawInfoBox(
                "PerSpec requires a working directory in your project root for SQLite database and scripts. " +
                "This directory is created at: ProjectRoot/PerSpec/"
            );
            
            EditorGUILayout.Space(10);
            
            // Status
            DrawSection("Initialization Status", () =>
            {
                bool initialized = InitializationService.IsInitialized;
                
                string status = initialized 
                    ? "✓ PerSpec is initialized and ready" 
                    : "✗ PerSpec is not initialized";
                    
                Color color = initialized ? Color.green : Color.red;
                GUI.color = color;
                EditorGUILayout.LabelField(status, statusStyle);
                GUI.color = Color.white;
                
                if (initialized)
                {
                    EditorGUILayout.Space(5);
                    EditorGUILayout.LabelField("Working Directory:", 
                        InitializationService.ProjectPerSpecPath);
                    EditorGUILayout.LabelField("Database:", 
                        InitializationService.DatabasePath);
                    EditorGUILayout.LabelField("Status:", 
                        InitializationService.GetStatusSummary());
                }
            });
            
            EditorGUILayout.Space(10);
            
            // Controls
            DrawSection("Initialization Controls", () =>
            {
                if (!InitializationService.IsInitialized)
                {
                    GUI.backgroundColor = Color.green;
                    if (GUILayout.Button("Initialize PerSpec", GUILayout.Height(40)))
                    {
                        if (InitializationService.Initialize())
                        {
                            ShowNotification(new GUIContent("PerSpec initialized successfully"));
                        }
                    }
                    GUI.backgroundColor = Color.white;
                }
                else
                {
                    EditorGUILayout.BeginHorizontal();
                    
                    if (GUILayout.Button("Open Directory", GUILayout.Height(30)))
                    {
                        InitializationService.OpenWorkingDirectory();
                    }
                    
                    GUI.backgroundColor = Color.red;
                    if (GUILayout.Button("Reset", GUILayout.Height(30)))
                    {
                        if (EditorUtility.DisplayDialog("Reset PerSpec",
                            "This will perform a complete reset:\n\n" +
                            "✓ Stop all coordination services\n" +
                            "✓ Close database connections\n" +
                            "✓ Clean database tables (keeps file)\n" +
                            "✓ Delete all logs and test results\n" +
                            "✓ Restart coordination services\n\n" +
                            "Your settings and compiler symbols will be preserved.\n\n" +
                            "This takes 5-10 seconds. Continue?",
                            "Reset", "Cancel"))
                        {
                            // Show progress bar
                            bool success = false;
                            try
                            {
                                success = ResetService.Reset((message, progress) => {
                                    EditorUtility.DisplayProgressBar("Resetting PerSpec", message, progress);
                                });
                            }
                            finally
                            {
                                EditorUtility.ClearProgressBar();
                            }

                            if (success)
                            {
                                EditorUtility.DisplayDialog("Reset Complete",
                                    "PerSpec has been reset successfully!\n\n" +
                                    "✓ All coordination services running\n" +
                                    "✓ Database clean and ready\n" +
                                    "✓ All logs cleared\n" +
                                    "✓ Settings preserved",
                                    "OK");
                            }
                            else
                            {
                                EditorUtility.DisplayDialog("Reset Completed with Warnings",
                                    "Reset finished but encountered some errors.\n\n" +
                                    "Check the console for details.\n" +
                                    "If issues persist, try closing Unity and\n" +
                                    "manually deleting the PerSpec folder.",
                                    "OK");
                            }
                        }
                    }
                    GUI.backgroundColor = Color.white;
                    
                    EditorGUILayout.EndHorizontal();
                }
            });
            
            // Script Management (only show if initialized)
            if (InitializationService.IsInitialized)
            {
                EditorGUILayout.Space(10);
                
                DrawSection("Script Management", () =>
                {
                    // Package location info
                    EditorGUILayout.LabelField("Package Location:", 
                        PackagePathResolver.GetPackageLocationInfo());
                    
                    // Check if scripts need refresh
                    bool needsRefresh = InitializationService.ScriptsNeedRefresh();
                    if (needsRefresh)
                    {
                        EditorGUILayout.HelpBox(
                            "Wrapper scripts appear to be outdated or using old package paths. " +
                            "Click 'Refresh Scripts' to update them.",
                            MessageType.Warning);
                    }
                    
                    EditorGUILayout.Space(5);

                    EditorGUILayout.HelpBox(
                        "Script update functionality has been moved to the Dashboard tab.\n" +
                        "Use the 'Update Scripts' button there to refresh all scripts.",
                        MessageType.Info
                    );
                    
                    EditorGUILayout.Space(5);
                    
                    // Info about wrapper scripts
                    EditorGUILayout.HelpBox(
                        "Wrapper scripts allow you to run PerSpec commands from the command line:\n" +
                        "• refresh - Refresh Unity assets\n" +
                        "• test - Run Unity tests\n" +
                        "• logs - View Unity console logs\n" +
                        "• init_db - Initialize database",
                        MessageType.Info);
                });
            }
        }
        
        #endregion
        
        #region GUI Drawing - LLM Setup Tab
        
        private void DrawLLMSetupTab()
        {
            // Detect existing LLM configurations (moved outside to be accessible by both sections)
            var detectedConfigs = DetectLLMConfigurations();

            DrawSection("LLM Configuration Management", () =>
            {
                EditorGUILayout.HelpBox("Configure your AI coding assistant with PerSpec's TDD instructions.", MessageType.Info);
                EditorGUILayout.Space(10);

                if (detectedConfigs.Count > 0)
                {
                    EditorGUILayout.LabelField("Detected Configurations:", EditorStyles.boldLabel);
                    foreach (var config in detectedConfigs)
                    {
                        EditorGUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField($"• {config.Key}", GUILayout.Width(200));

                        if (GUILayout.Button("Update", GUILayout.Width(80)))
                        {
                            UpdateLLMConfiguration(config.Value);
                        }

                        if (GUILayout.Button("View", GUILayout.Width(80)))
                        {
                            EditorUtility.RevealInFinder(config.Value);
                        }

                        EditorGUILayout.EndHorizontal();
                    }
                }
                else
                {
                    EditorGUILayout.HelpBox("No LLM configuration files detected in your project.", MessageType.Warning);
                }

                EditorGUILayout.Space(20);
                EditorGUILayout.LabelField("Select LLM Configurations to Create/Update:", EditorStyles.boldLabel);

                // Checkbox list in scroll view
                EditorGUILayout.BeginVertical(EditorStyles.helpBox, GUILayout.Height(120));
                llmScrollPosition = EditorGUILayout.BeginScrollView(llmScrollPosition);

                for (int i = 0; i < LLM_OPTIONS.Length; i++)
                {
                    string prefKey = $"PerSpec.LLM.Selected.{i}";
                    bool currentValue = EditorPrefs.GetBool(prefKey, false);
                    bool newValue = EditorGUILayout.ToggleLeft(LLM_OPTIONS[i], currentValue);

                    if (newValue != currentValue)
                    {
                        EditorPrefs.SetBool(prefKey, newValue);
                    }
                }

                EditorGUILayout.EndScrollView();
                EditorGUILayout.EndVertical();

                EditorGUILayout.Space(10);
                EditorGUILayout.BeginHorizontal();

                if (GUILayout.Button("Create/Update Selected", GUILayout.Height(30)))
                {
                    CreateOrUpdateSelectedLLMConfigurations();
                }

                if (GUILayout.Button("Select All", GUILayout.Width(80), GUILayout.Height(30)))
                {
                    for (int i = 0; i < LLM_OPTIONS.Length; i++)
                    {
                        EditorPrefs.SetBool($"PerSpec.LLM.Selected.{i}", true);
                    }
                }

                if (GUILayout.Button("Clear All", GUILayout.Width(80), GUILayout.Height(30)))
                {
                    for (int i = 0; i < LLM_OPTIONS.Length; i++)
                    {
                        EditorPrefs.SetBool($"PerSpec.LLM.Selected.{i}", false);
                    }
                }

                EditorGUILayout.EndHorizontal();
                
                EditorGUILayout.Space(10);

                EditorGUILayout.HelpBox(
                    "Agent definitions and Python scripts are now automatically copied during initialization and updates.\n" +
                    "If you need to manually refresh, use the 'Update Scripts' button in the Dashboard tab.",
                    MessageType.Info
                );
            });
            
            EditorGUILayout.Space(10);
            
            // Command Permissions Section
            DrawSection("Command Execution Permissions", () =>
            {
                // Warning message
                EditorGUILayout.HelpBox(
                    "⚠️ WARNING: Enabling command execution allows AI assistants to run system commands.\n" +
                    "• Only enable for trusted AI providers\n" +
                    "• Commands can modify files and execute scripts\n" +
                    "• You can revoke permissions at any time",
                    MessageType.Warning);
                
                EditorGUILayout.Space(10);
                
                if (detectedConfigs.Count > 0)
                {
                    EditorGUILayout.LabelField("Configure Permissions per Provider:", EditorStyles.boldLabel);
                    EditorGUILayout.Space(5);
                    
                    foreach (var config in detectedConfigs)
                    {
                        DrawProviderPermissions(config.Key);
                        EditorGUILayout.Space(5);
                    }
                }
                else
                {
                    EditorGUILayout.LabelField("No LLM providers detected. Create a configuration first.", EditorStyles.centeredGreyMiniLabel);
                }
                
                EditorGUILayout.Space(10);
                
                // Show providers with active permissions
                var providersWithPermissions = LLMPermissionManager.GetProvidersWithPermissions();
                if (providersWithPermissions.Count > 0)
                {
                    EditorGUILayout.LabelField("Active Permissions:", EditorStyles.boldLabel);
                    foreach (var provider in providersWithPermissions)
                    {
                        var state = LLMPermissionManager.GetPermissions(provider);
                        string perms = "";
                        if (state.BashEnabled) perms += "Bash ";
                        if (state.PythonEnabled) perms += "Python";
                        
                        EditorGUILayout.BeginHorizontal();
                        GUI.color = Color.yellow;
                        EditorGUILayout.LabelField($"⚠️ {provider}: {perms}", EditorStyles.boldLabel);
                        GUI.color = Color.white;
                        
                        if (GUILayout.Button("Revoke All", GUILayout.Width(80)))
                        {
                            if (EditorUtility.DisplayDialog("Revoke Permissions",
                                $"Revoke all command execution permissions for {provider}?",
                                "Revoke", "Cancel"))
                            {
                                LLMPermissionManager.ClearPermissions(provider);
                                ShowNotification(new GUIContent($"Revoked permissions for {provider}"));
                            }
                        }
                        EditorGUILayout.EndHorizontal();
                    }
                }
            });
            
            DrawSection("Instructions", () =>
            {
                string instructions = @"1. Detect: Scans your project for existing LLM configuration files
2. Update: Appends PerSpec's TDD instructions to existing configs
3. Create: Creates a new configuration file with PerSpec instructions
4. Agents: Copies specialized agent definitions to .claude/agents/

Supported LLMs:
• Claude Code - CLAUDE.md
• Cursor - .cursorrules
• Agents - AGENTS.md
• Gemini CLI - GEMINI.md
• Aider - Conventions.md
• Windsurf - .windsurf/rules/
• OpenAI - .openai.md
• DeepSeek - .deepseek.md";

                EditorGUILayout.TextArea(instructions, GUILayout.Height(220));
            });
        }
        
        private Dictionary<string, string> DetectLLMConfigurations()
        {
            var configs = new Dictionary<string, string>();
            string projectPath = Directory.GetParent(Application.dataPath).FullName;

            // Check for various LLM configuration files
            var llmFiles = new Dictionary<string, string>
            {
                { "Claude (CLAUDE.md)", "CLAUDE.md" },
                { "Cursor (.cursorrules)", ".cursorrules" },
                { "Agents", "AGENTS.md" },
                { "Gemini CLI", "GEMINI.md" },
                { "Aider", "Conventions.md" },
                { "OpenAI", ".openai.md" },
                { "DeepSeek", ".deepseek.md" }
            };

            foreach (var file in llmFiles)
            {
                string fullPath = Path.Combine(projectPath, file.Value);
                if (File.Exists(fullPath))
                {
                    configs[file.Key] = fullPath;
                }
            }

            // Special case: Check for Windsurf directory
            string windsurfRulesPath = Path.Combine(projectPath, ".windsurf", "rules");
            if (Directory.Exists(windsurfRulesPath))
            {
                configs["Windsurf"] = windsurfRulesPath;
            }

            return configs;
        }
        
        private void DrawProviderPermissions(string providerDisplay)
        {
            // Extract clean provider name from display string
            string provider = providerDisplay.Split('(')[0].Trim();
            var permissions = LLMPermissionManager.GetPermissions(provider);
            
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            
            // Provider name
            EditorGUILayout.LabelField(providerDisplay, GUILayout.Width(200));
            
            // Bash permission toggle
            bool newBash = permissions.BashEnabled;
            GUI.backgroundColor = newBash ? Color.red : Color.green;
            string bashLabel = newBash ? "Bash ✓" : "Bash ✗";
            
            if (GUILayout.Button(bashLabel, GUILayout.Width(70)))
            {
                newBash = !newBash;
                
                if (newBash && !permissions.BashEnabled)
                {
                    if (EditorUtility.DisplayDialog("Enable Bash Commands",
                        $"Allow {provider} to execute bash/shell commands?\n\n" +
                        "This includes:\n" +
                        "• File system operations (ls, cd, find)\n" +
                        "• Git commands\n" +
                        "• System utilities\n\n" +
                        "Only enable if you trust this AI provider.",
                        "Enable", "Cancel"))
                    {
                        LLMPermissionManager.SetBashPermission(provider, true);
                        UpdateLLMConfigurationPermissions(provider);
                        ShowNotification(new GUIContent($"Bash enabled for {provider}"));
                    }
                }
                else if (!newBash && permissions.BashEnabled)
                {
                    LLMPermissionManager.SetBashPermission(provider, false);
                    UpdateLLMConfigurationPermissions(provider);
                    ShowNotification(new GUIContent($"Bash disabled for {provider}"));
                }
            }
            
            // Python permission toggle
            bool newPython = permissions.PythonEnabled;
            GUI.backgroundColor = newPython ? Color.red : Color.green;
            string pythonLabel = newPython ? "Python ✓" : "Python ✗";
            
            if (GUILayout.Button(pythonLabel, GUILayout.Width(70)))
            {
                newPython = !newPython;
                
                if (newPython && !permissions.PythonEnabled)
                {
                    if (EditorUtility.DisplayDialog("Enable Python Execution",
                        $"Allow {provider} to execute Python scripts?\n\n" +
                        "This includes:\n" +
                        "• PerSpec coordination scripts\n" +
                        "• Python script execution\n" +
                        "• Package management (pip)\n\n" +
                        "Only enable if you trust this AI provider.",
                        "Enable", "Cancel"))
                    {
                        LLMPermissionManager.SetPythonPermission(provider, true);
                        UpdateLLMConfigurationPermissions(provider);
                        ShowNotification(new GUIContent($"Python enabled for {provider}"));
                    }
                }
                else if (!newPython && permissions.PythonEnabled)
                {
                    LLMPermissionManager.SetPythonPermission(provider, false);
                    UpdateLLMConfigurationPermissions(provider);
                    ShowNotification(new GUIContent($"Python disabled for {provider}"));
                }
            }
            
            GUI.backgroundColor = Color.white;
            
            // Status indicator
            if (permissions.HasAnyPermission)
            {
                GUI.color = Color.yellow;
                EditorGUILayout.LabelField("⚠️ Active", GUILayout.Width(60));
                GUI.color = Color.white;
            }
            else
            {
                EditorGUILayout.LabelField("Disabled", GUILayout.Width(60));
            }
            
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }
        
        private void UpdateLLMConfigurationPermissions(string provider)
        {
            // Find the config file for this provider
            var configs = DetectLLMConfigurations();
            string configPath = null;
            
            foreach (var config in configs)
            {
                if (config.Key.StartsWith(provider))
                {
                    configPath = config.Value;
                    break;
                }
            }
            
            if (!string.IsNullOrEmpty(configPath) && File.Exists(configPath))
            {
                try
                {
                    string content = File.ReadAllText(configPath);
                    string updatedContent = LLMPermissionManager.UpdatePermissionBlock(content, provider);
                    
                    if (content != updatedContent)
                    {
                        File.WriteAllText(configPath, updatedContent);
                        Debug.Log($"[PerSpec] Updated permissions in {Path.GetFileName(configPath)} for {provider}");
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError($"[PerSpec] Failed to update permissions in config: {e.Message}");
                }
            }
        }
        
        private void UpdateLLMConfiguration(string configPath)
        {
            try
            {
                string actualFilePath = configPath;

                // Special handling for Windsurf (directory-based config)
                if (Directory.Exists(configPath) && configPath.Contains(".windsurf"))
                {
                    // Find perspec.md in the directory, or create it
                    actualFilePath = Path.Combine(configPath, "perspec.md");

                    if (!File.Exists(actualFilePath))
                    {
                        // Create new file if it doesn't exist
                        string llmContent = GetLLMContent();
                        string provider = "Windsurf";
                        llmContent = LLMPermissionManager.UpdatePermissionBlock(llmContent, provider);
                        File.WriteAllText(actualFilePath, llmContent);

                        EditorUtility.DisplayDialog("Success",
                            "Created perspec.md in .windsurf/rules/ with PerSpec instructions.",
                            "OK");
                        AssetDatabase.Refresh();
                        return;
                    }
                }

                string llmContent2 = GetLLMContent();
                string existingContent = File.ReadAllText(actualFilePath);

                // Determine provider from config path
                string provider2 = GetProviderFromPath(actualFilePath);

                // Check for existing block markers
                string startMarker = GetStartMarker(actualFilePath);
                string endMarker = GetEndMarker(actualFilePath);

                if (existingContent.Contains(startMarker) && existingContent.Contains(endMarker))
                {
                    // Replace existing content between markers
                    string updatedContent = ReplaceContentBetweenMarkers(
                        existingContent,
                        llmContent2,
                        startMarker,
                        endMarker
                    );

                    // Add permission block if enabled
                    updatedContent = LLMPermissionManager.UpdatePermissionBlock(updatedContent, provider2);

                    File.WriteAllText(actualFilePath, updatedContent);

                    EditorUtility.DisplayDialog("Success",
                        $"Updated {Path.GetFileName(actualFilePath)} with latest PerSpec instructions.",
                        "OK");
                }
                else
                {
                    // No markers found - just append with markers at the end
                    string separator = "\n\n";
                    string contentWithMarkers = $"{separator}{startMarker}\n{llmContent2}\n{endMarker}";
                    // Add permission block if enabled
                    string fullContent = existingContent + contentWithMarkers;
                    fullContent = LLMPermissionManager.UpdatePermissionBlock(fullContent, provider2);

                    File.WriteAllText(actualFilePath, fullContent);

                    EditorUtility.DisplayDialog("Success",
                        $"Added PerSpec instructions to {Path.GetFileName(actualFilePath)}.",
                        "OK");
                }

                AssetDatabase.Refresh();
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog("Error",
                    $"Failed to update configuration: {e.Message}",
                    "OK");
            }
        }
        
        private void CreateOrUpdateSelectedLLMConfigurations()
        {
            var selectedConfigs = new List<int>();
            for (int i = 0; i < LLM_OPTIONS.Length; i++)
            {
                if (EditorPrefs.GetBool($"PerSpec.LLM.Selected.{i}", false))
                {
                    selectedConfigs.Add(i);
                }
            }

            if (selectedConfigs.Count == 0)
            {
                EditorUtility.DisplayDialog("No Selection",
                    "Please select at least one LLM configuration to create/update.",
                    "OK");
                return;
            }

            int created = 0;
            int updated = 0;
            var errors = new List<string>();

            foreach (int index in selectedConfigs)
            {
                try
                {
                    string configPath = GetLLMConfigPath(index);
                    bool exists = File.Exists(configPath);

                    if (exists)
                    {
                        UpdateLLMConfiguration(configPath);
                        updated++;
                    }
                    else
                    {
                        CreateLLMConfiguration(index);
                        created++;
                    }
                }
                catch (Exception e)
                {
                    errors.Add($"{LLM_OPTIONS[index]}: {e.Message}");
                }
            }

            string message = "";
            if (created > 0) message += $"Created {created} configuration(s)\n";
            if (updated > 0) message += $"Updated {updated} configuration(s)\n";
            if (errors.Count > 0) message += $"\nErrors:\n{string.Join("\n", errors)}";

            EditorUtility.DisplayDialog("Operation Complete", message, "OK");
            AssetDatabase.Refresh();
        }

        private string GetLLMConfigPath(int llmIndex)
        {
            string projectPath = Directory.GetParent(Application.dataPath).FullName;

            switch (llmIndex)
            {
                case 0: // Claude
                    return Path.Combine(projectPath, "CLAUDE.md");
                case 1: // Cursor
                    return Path.Combine(projectPath, ".cursorrules");
                case 2: // Agents
                    return Path.Combine(projectPath, "AGENTS.md");
                case 3: // Gemini CLI
                    return Path.Combine(projectPath, "GEMINI.md");
                case 4: // Aider
                    return Path.Combine(projectPath, "Conventions.md");
                case 5: // Windsurf
                    return Path.Combine(projectPath, ".windsurf", "rules");
                case 6: // OpenAI
                    return Path.Combine(projectPath, ".openai.md");
                case 7: // DeepSeek
                    return Path.Combine(projectPath, ".deepseek.md");
                default:
                    throw new ArgumentException($"Invalid LLM index: {llmIndex}");
            }
        }

        private void CreateLLMConfiguration(int llmIndex)
        {
            string configPath = GetLLMConfigPath(llmIndex);
            string actualFilePath = configPath;

            // Special handling for Windsurf (directory-based config)
            if (llmIndex == 5) // Windsurf
            {
                // Create directory if it doesn't exist
                if (!Directory.Exists(configPath))
                {
                    Directory.CreateDirectory(configPath);
                }

                // Create a file inside the directory
                actualFilePath = Path.Combine(configPath, "perspec.md");
            }

            if (File.Exists(actualFilePath))
            {
                if (!EditorUtility.DisplayDialog("File Exists",
                    $"Configuration file already exists at:\n{actualFilePath}\n\nOverwrite?",
                    "Overwrite", "Cancel"))
                {
                    return;
                }
            }

            try
            {
                string llmContent = GetLLMContent();

                // Add permission block if enabled
                string providerName = GetProviderFromIndex(llmIndex);
                llmContent = LLMPermissionManager.UpdatePermissionBlock(llmContent, providerName);

                File.WriteAllText(actualFilePath, llmContent);

                string successMessage = llmIndex == 5
                    ? $"Created {Path.GetFileName(actualFilePath)} in .windsurf/rules/ with PerSpec instructions."
                    : $"Created {Path.GetFileName(actualFilePath)} with PerSpec instructions.";

                EditorUtility.DisplayDialog("Success", successMessage, "OK");

                AssetDatabase.Refresh();
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog("Error",
                    $"Failed to create configuration: {e.Message}",
                    "OK");
            }
        }
        
        private void CopyAgentDefinitions()
        {
            try
            {
                string projectPath = Directory.GetParent(Application.dataPath).FullName;
                string targetDir = Path.Combine(projectPath, ".claude", "agents");
                string sourceDir = Path.Combine(PackagePathResolver.PackagePath, "Documentation", "agents");
                
                if (!Directory.Exists(sourceDir))
                {
                    EditorUtility.DisplayDialog("Error",
                        "Agent definitions not found in package.",
                        "OK");
                    return;
                }
                
                // Create target directory
                Directory.CreateDirectory(targetDir);
                
                // Track copied and updated files
                int copiedCount = 0;
                int updatedCount = 0;
                List<string> existingFiles = new List<string>();
                
                // Copy all agent files
                foreach (string file in Directory.GetFiles(sourceDir, "*.md"))
                {
                    string fileName = Path.GetFileName(file);
                    string targetPath = Path.Combine(targetDir, fileName);
                    
                    if (File.Exists(targetPath))
                    {
                        existingFiles.Add(fileName);
                        updatedCount++;
                    }
                    else
                    {
                        copiedCount++;
                    }
                    
                    File.Copy(file, targetPath, true);
                }
                
                // Build result message
                string message = "Agent definitions synchronized:\n";
                if (copiedCount > 0)
                    message += $"• {copiedCount} new agent(s) copied\n";
                if (updatedCount > 0)
                    message += $"• {updatedCount} existing agent(s) updated\n";
                message += $"\nLocation: {targetDir}";
                
                EditorUtility.DisplayDialog("Success", message, "OK");
                
                AssetDatabase.Refresh();
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog("Error",
                    $"Failed to copy agent definitions: {e.Message}",
                    "OK");
            }
        }
        
        private string GetLLMContent()
        {
            string llmPath = Path.Combine(PackagePathResolver.PackagePath, "Documentation", "LLM.md");
            
            if (!File.Exists(llmPath))
            {
                throw new FileNotFoundException($"LLM.md not found at: {llmPath}");
            }
            
            return File.ReadAllText(llmPath);
        }
        
        #endregion
        
        #region GUI Drawing - About Tab
        
        private void DrawAboutTab()
        {
            DrawSection("About PerSpec", () =>
            {
                // Try to load the logo
                var logo = Resources.Load<Texture2D>("Icons/digitraver");
                if (logo != null)
                {
                    var rect = GUILayoutUtility.GetRect(64, 64, GUILayout.ExpandWidth(false));
                    rect.x = (EditorGUIUtility.currentViewWidth - 64) / 2 - 10;
                    GUI.DrawTexture(rect, logo, ScaleMode.ScaleToFit);
                    EditorGUILayout.Space(10);
                }

                // Center-aligned text
                var centeredStyle = new GUIStyle(EditorStyles.boldLabel);
                centeredStyle.alignment = TextAnchor.MiddleCenter;

                EditorGUILayout.LabelField("Made in San Francisco", centeredStyle);
                EditorGUILayout.LabelField("By Valentin Burov", centeredStyle);

                EditorGUILayout.Space(10);

                // Get package version dynamically
                string packageVersion = "1.3.1"; // Default fallback
                try
                {
                    var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForPackageName("com.digitraver.perspec");
                    if (packageInfo != null)
                    {
                        packageVersion = packageInfo.version;
                    }
                }
                catch (Exception)
                {
                    // Use fallback version if package info is not available
                }

                EditorGUILayout.LabelField("Version:", packageVersion);
                EditorGUILayout.LabelField("Unity:", Application.unityVersion);
                
                EditorGUILayout.Space(10);
                
                EditorGUILayout.LabelField("PerSpec is a Test-Driven Development framework for Unity", 
                    EditorStyles.wordWrappedLabel);
                EditorGUILayout.LabelField("that uses SQLite coordination for automated testing.", 
                    EditorStyles.wordWrappedLabel);
                    
                EditorGUILayout.Space(10);
                
                if (GUILayout.Button("Fork & Contribute on GitHub"))
                {
                    Application.OpenURL("https://github.com/thePostFuturist/PerSpec");
                }
                
                if (GUILayout.Button("Join us at digitRaver.com"))
                {
                    Application.OpenURL("https://digitRaver.com");
                }
                
                if (GUILayout.Button("View Documentation"))
                {
                    Application.OpenURL("https://github.com/thePostFuturist/PerSpec");
                }
                
                if (GUILayout.Button("Report Issue"))
                {
                    Application.OpenURL("https://github.com/thePostFuturist/PerSpec/issues");
                }
            });
        }
        
        #endregion
        
        #region Helper Methods
        
        private void DrawSection(string title, Action content)
        {
            EditorGUILayout.BeginVertical(sectionStyle);
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
            EditorGUILayout.Space(5);
            content?.Invoke();
            EditorGUILayout.EndVertical();
        }
        
        private void DrawInfoBox(string message)
        {
            EditorGUILayout.HelpBox(message, MessageType.Info);
        }
        
        private void DrawStatusRow(string label, string status, Color color)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, GUILayout.Width(120));
            
            var oldColor = GUI.color;
            GUI.color = color;
            EditorGUILayout.LabelField(status);
            GUI.color = oldColor;
            
            EditorGUILayout.EndHorizontal();
        }
        
        #endregion
        
        #region Block Marker Helpers
        
        private string GetStartMarker(string configPath)
        {
            string fileName = Path.GetFileName(configPath);
            
            // Use appropriate comment syntax based on file type
            if (fileName.EndsWith(".md"))
                return "<!-- PERSPEC_CONFIG_START -->";
            else if (fileName.EndsWith(".yml") || fileName.EndsWith(".yaml"))
                return "# PERSPEC_CONFIG_START";
            else
                return "# PERSPEC_CONFIG_START";
        }
        
        private string GetEndMarker(string configPath)
        {
            string fileName = Path.GetFileName(configPath);
            
            // Use appropriate comment syntax based on file type
            if (fileName.EndsWith(".md"))
                return "<!-- PERSPEC_CONFIG_END -->";
            else if (fileName.EndsWith(".yml") || fileName.EndsWith(".yaml"))
                return "# PERSPEC_CONFIG_END";
            else
                return "# PERSPEC_CONFIG_END";
        }
        
        private string ReplaceContentBetweenMarkers(string content, string newContent, string startMarker, string endMarker)
        {
            int startIndex = content.IndexOf(startMarker);
            int endIndex = content.IndexOf(endMarker);
            
            if (startIndex == -1 || endIndex == -1 || endIndex <= startIndex)
            {
                throw new InvalidOperationException("Invalid marker positions in content");
            }
            
            // Find the end of the end marker line
            int endMarkerEnd = content.IndexOf('\n', endIndex);
            if (endMarkerEnd == -1)
                endMarkerEnd = content.Length;
            
            // Build the new content
            string beforeMarkers = content.Substring(0, startIndex);
            string afterMarkers = content.Substring(endMarkerEnd);
            
            return $"{beforeMarkers}{startMarker}\n{newContent}\n{endMarker}{afterMarkers}";
        }
        
        private string GetProviderFromPath(string configPath)
        {
            string fileName = Path.GetFileName(configPath);

            if (fileName.Contains("CLAUDE") || fileName.Contains("claude"))
                return "Claude";
            else if (fileName.Contains("cursor"))
                return "Cursor";
            else if (fileName.Contains("AGENTS") || fileName.Contains("agents"))
                return "Agents";
            else if (fileName.Contains("GEMINI") || fileName.Contains("gemini"))
                return "Gemini";
            else if (fileName.Contains("Conventions") || fileName.Contains("conventions"))
                return "Aider";
            else if (fileName.Contains("windsurf") || configPath.Contains(".windsurf"))
                return "Windsurf";
            else if (fileName.Contains("openai") || fileName.Contains("OpenAI"))
                return "OpenAI";
            else if (fileName.Contains("deepseek") || fileName.Contains("DeepSeek"))
                return "DeepSeek";

            return "Unknown";
        }
        
        private string GetProviderFromIndex(int index)
        {
            switch (index)
            {
                case 0: return "Claude";
                case 1: return "Cursor";
                case 2: return "Agents";
                case 3: return "Gemini";
                case 4: return "Aider";
                case 5: return "Windsurf";
                case 6: return "OpenAI";
                case 7: return "DeepSeek";
                default: return "Unknown";
            }
        }
        
        #endregion
    }
}