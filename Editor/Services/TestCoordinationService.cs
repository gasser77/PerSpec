using System;
using UnityEngine;
using UnityEditor;
using PerSpec.Editor.Coordination;

namespace PerSpec.Editor.Services
{
    /// <summary>
    /// Service for managing test coordination and execution
    /// </summary>
    public static class TestCoordinationService
    {
        #region Fields
        
        private static SQLiteManager _dbManager;
        // Run state lives in TestCoordinatorEditor, which owns the only dispatcher. A second
        // copy here is what let this service start a run the coordinator believed was not
        // happening.
        private static bool _isRunningTests => PerSpec.Editor.Coordination.TestCoordinatorEditor.IsRunningTests;
        private static int _currentRequestId => PerSpec.Editor.Coordination.TestCoordinatorEditor.CurrentRequestId;
        private static bool _pollingEnabled = true;
        
        #endregion
        
        #region Properties
        
        public static bool IsRunningTests => _isRunningTests;
        public static int CurrentRequestId => _currentRequestId;
        public static bool PollingEnabled 
        { 
            get => _pollingEnabled;
            set
            {
                _pollingEnabled = value;
                if (value)
                    BackgroundPoller.EnableBackgroundPolling();
                else
                    BackgroundPoller.DisableBackgroundPolling();
            }
        }
        
        public static bool IsDatabaseConnected => _dbManager != null;
        
        #endregion
        
        #region Initialization
        
        static TestCoordinationService()
        {
            Initialize();
        }
        
        public static void Initialize()
        {
            try
            {
                // No TestExecutor here: TestCoordinatorEditor owns the only one. Building a
                // second created a second TestRunnerApi ScriptableObject that nothing ran.
                _dbManager = new SQLiteManager();
                Debug.Log("[TestCoordination] Service initialized");
            }
            catch (Exception e)
            {
                Debug.LogError($"[TestCoordination] Failed to initialize: {e.Message}");
            }
        }
        
        #endregion
        
        #region Public Methods
        
        /// <summary>
        /// Check for pending test requests
        /// </summary>
        public static bool CheckPendingTests()
        {
            if (!IsDatabaseConnected || _isRunningTests)
                return false;
                
            try
            {
                var request = _dbManager.GetNextPendingRequest();
                if (request != null)
                {
                    ProcessTestRequest(request);
                    return true;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[TestCoordination] Error checking pending tests: {e.Message}");
            }
            
            return false;
        }
        
        /// <summary>
        /// Cancel the current test
        /// </summary>
        public static bool CancelCurrentTest()
        {
            if (_isRunningTests && _currentRequestId > 0)
            {
                // The coordinator owns the run and its terminal write. Cancelling here as
                // well would race it, and the old code logged the id AFTER clearing it, so
                // it always said "Cancelled test request -1".
                int cancelledId = _currentRequestId;
                PerSpec.Editor.Coordination.TestCoordinatorEditor.CancelCurrentTest();
                Debug.Log($"[TestCoordination] Cancelled test request {cancelledId}");
                return true;
            }
            return false;
        }
        
        /// <summary>
        /// Get database status
        /// </summary>
        public static string GetDatabaseStatus()
        {
            if (!IsDatabaseConnected)
                return "Database not connected";
                
            try
            {
                return _dbManager.GetSystemStatus();
            }
            catch (Exception e)
            {
                return $"Error: {e.Message}";
            }
        }
        
        /// <summary>
        /// Get current status summary
        /// </summary>
        public static string GetStatusSummary()
        {
            if (_isRunningTests)
                return $"Running test #{_currentRequestId}";
            
            return PollingEnabled ? "Idle (polling enabled)" : "Idle (polling disabled)";
        }
        
        /// <summary>
        /// Get pending test count
        /// </summary>
        public static int GetPendingTestCount()
        {
            if (!IsDatabaseConnected)
                return 0;
                
            try
            {
                // This would need to be added to SQLiteManager
                return 0; // Placeholder
            }
            catch
            {
                return 0;
            }
        }
        
        /// <summary>
        /// Force script compilation
        /// </summary>
        public static void ForceScriptCompilation()
        {
            UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation();
            Debug.Log("[TestCoordination] Script compilation requested");
        }
        
        #endregion
        
        #region Private Methods
        
        /// <summary>
        /// Hands the request to the one real dispatcher.
        ///
        /// This used to be a parallel implementation with its own filter builder, its own
        /// run-state flags, and no pre-flight check. It always used testNames - even for a
        /// class run, which testNames can never match - so a class dispatched from the
        /// Control Center silently ran nothing. It also skipped the "Both" platform
        /// rejection and could start a run while the coordinator believed none was active.
        /// </summary>
        private static void ProcessTestRequest(TestRequest request)
        {
            PerSpec.Editor.Coordination.TestCoordinatorEditor.ProcessTestRequest(request);
        }
        
        #endregion
    }
}