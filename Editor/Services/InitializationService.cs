using System;
using System.IO;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;
using Debug = UnityEngine.Debug;

namespace PerSpec.Editor.Services
{
    /// <summary>
    /// Service for managing PerSpec initialization and setup
    /// </summary>
    public static class InitializationService
    {
        #region Menu Items
        
        [MenuItem("Tools/PerSpec/Refresh Coordination Scripts", false, 15)]
        private static void MenuRefreshScripts()
        {
            if (!IsInitialized)
            {
                Debug.LogError("[PerSpec] Cannot refresh scripts - PerSpec not initialized. Use Tools > PerSpec > Initialize first.");
                return;
            }
            
            var result = RefreshCoordinationScripts();
            if (!string.IsNullOrEmpty(result))
            {
                Debug.Log($"[PerSpec] {result}");
            }
            else
            {
                Debug.LogError("[PerSpec] Failed to refresh coordination scripts");
            }
        }
        
        [MenuItem("Tools/PerSpec/Refresh Coordination Scripts", true)]
        private static bool ValidateMenuRefreshScripts()
        {
            return IsInitialized;
        }
        
        [MenuItem("Tools/PerSpec/Initialize Database", false, 16)]
        private static void MenuInitializeDatabase()
        {
            if (!IsInitialized)
            {
                Debug.LogError("[PerSpec] Cannot initialize database - PerSpec not initialized. Use Tools > PerSpec > Initialize first.");
                return;
            }
            
            if (File.Exists(DatabasePath))
            {
                Debug.Log("[PerSpec] Database already exists at: " + DatabasePath);
                return;
            }
            
            if (InitializeDatabase())
            {
                Debug.Log("[PerSpec] Database initialized successfully");
                EditorUtility.DisplayDialog("Success", "Database initialized successfully!\n\n" + DatabasePath, "OK");
            }
            else
            {
                Debug.LogError("[PerSpec] Failed to initialize database");
                EditorUtility.DisplayDialog("Error", "Failed to initialize database.\n\nCheck the console for details.\n\nYou can manually run:\npython PerSpec/Coordination/Scripts/db_initializer.py", "OK");
            }
        }
        
        [MenuItem("Tools/PerSpec/Initialize Database", true)]
        private static bool ValidateMenuInitializeDatabase()
        {
            return IsInitialized && !File.Exists(DatabasePath);
        }
        
        #endregion
        
        #region Properties
        
        public static string ProjectPerSpecPath => Path.Combine(Directory.GetParent(Application.dataPath).FullName, "PerSpec");
        public static string DatabasePath => Path.Combine(ProjectPerSpecPath, "test_coordination.db");
        public static string CoordinationScriptsPath => Path.Combine(ProjectPerSpecPath, "Coordination", "Scripts");
        public static string TestResultsPath => Path.Combine(ProjectPerSpecPath, "TestResults");
        
        public static bool IsInitialized => Directory.Exists(ProjectPerSpecPath);
        
        public static DateTime? InitializedDate
        {
            get
            {
                if (IsInitialized)
                    return Directory.GetCreationTime(ProjectPerSpecPath);
                return null;
            }
        }
        
        public static long DatabaseSize
        {
            get
            {
                if (File.Exists(DatabasePath))
                    return new FileInfo(DatabasePath).Length;
                return 0;
            }
        }
        
        #endregion
        
        #region Public Methods
        
        /// <summary>
        /// Initialize PerSpec working directories
        /// </summary>
        public static bool Initialize()
        {
            try
            {
                // Create main directory
                if (!Directory.Exists(ProjectPerSpecPath))
                {
                    Directory.CreateDirectory(ProjectPerSpecPath);
                    Debug.Log($"[PerSpec] Created working directory: {ProjectPerSpecPath}");
                }

                // Create Coordination/Scripts subdirectory and copy scripts
                if (!Directory.Exists(CoordinationScriptsPath))
                {
                    Directory.CreateDirectory(CoordinationScriptsPath);
                    CopyCoordinationScripts();
                }

                // Create TestResults subdirectory
                if (!Directory.Exists(TestResultsPath))
                {
                    Directory.CreateDirectory(TestResultsPath);
                    Debug.Log($"[PerSpec] Created TestResults directory: {TestResultsPath}");
                }

                // Create .gitignore
                CreateGitIgnore();

                // Add PerSpec to project .gitignore (if not already there)
                UpdateGitIgnore();

                // Create package location file
                PackageLocationTracker.UpdateLocationFile();

                // Copy agent definitions automatically
                CopyAgentDefinitions();

                // Update LLM configurations automatically
                UpdateLLMConfigurations();

                // Initialize the SQLite database
                if (!InitializeDatabase())
                {
                    Debug.LogWarning("[PerSpec] Directories created but database initialization failed. You may need to run: python PerSpec/Coordination/Scripts/db_initializer.py");
                    // Still return true as directories were created successfully
                }

                // Enable PerSpecDebug by default
                BuildProfileHelper.AddCompilerDirective("PERSPEC_DEBUG");
                Debug.Log("[PerSpec] PERSPEC_DEBUG symbol added - debug logging enabled");

                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[PerSpec] Failed to initialize: {e.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// Reset PerSpec installation
        /// </summary>
        public static bool Reset()
        {
            try
            {
                if (Directory.Exists(ProjectPerSpecPath))
                {
                    Directory.Delete(ProjectPerSpecPath, true);
                    Debug.Log("[PerSpec] Reset complete. Directories removed.");
                    return true;
                }
                return false;
            }
            catch (Exception e)
            {
                Debug.LogError($"[PerSpec] Failed to reset: {e.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// Open working directory in file explorer
        /// </summary>
        public static void OpenWorkingDirectory()
        {
            if (IsInitialized)
            {
                EditorUtility.RevealInFinder(ProjectPerSpecPath);
            }
        }
        
        /// <summary>
        /// Get initialization status summary
        /// </summary>
        public static string GetStatusSummary()
        {
            if (!IsInitialized)
                return "Not initialized";
                
            var dbSize = DatabaseSize / 1024f; // Convert to KB
            return $"Initialized • DB: {dbSize:F1} KB • Created: {InitializedDate:yyyy-MM-dd}";
        }
        
        /// <summary>
        /// Refresh coordination scripts from package
        /// </summary>
        public static string RefreshCoordinationScripts()
        {
            try
            {
                if (!IsInitialized)
                {
                    Debug.LogError("[PerSpec] Cannot refresh scripts - PerSpec not initialized");
                    return null;
                }
                
                // Copy coordination scripts from package
                var copyResult = CopyCoordinationScripts();
                
                // Update package location file (still useful for reference)
                PackageLocationTracker.UpdateLocationFile();
                
                Debug.Log($"[PerSpec] {copyResult}");
                return copyResult;
            }
            catch (Exception e)
            {
                Debug.LogError($"[PerSpec] Failed to refresh scripts: {e.Message}");
                return null;
            }
        }
        
        /// <summary>
        /// Force update coordination scripts (delete existing and copy fresh)
        /// </summary>
        public static string ForceUpdateCoordinationScripts()
        {
            try
            {
                if (!IsInitialized)
                {
                    Debug.LogError("[PerSpec] Cannot force update scripts - PerSpec not initialized");
                    return null;
                }
                
                // Delete existing scripts directory to ensure clean update
                if (Directory.Exists(CoordinationScriptsPath))
                {
                    Directory.Delete(CoordinationScriptsPath, true);
                    Debug.Log($"[PerSpec] Removed existing scripts from {CoordinationScriptsPath}");
                }
                
                // Recreate directory
                Directory.CreateDirectory(CoordinationScriptsPath);
                
                // Copy fresh scripts from package
                var copyResult = CopyCoordinationScripts();
                
                // Update package location file
                PackageLocationTracker.UpdateLocationFile();
                
                Debug.Log($"[PerSpec] Force update: {copyResult}");
                return $"Force update: {copyResult}";
            }
            catch (Exception e)
            {
                Debug.LogError($"[PerSpec] Failed to force update scripts: {e.Message}");
                return null;
            }
        }
        
        /// <summary>
        /// Check if wrapper scripts need refreshing
        /// </summary>
        public static bool ScriptsNeedRefresh()
        {
            if (!IsInitialized)
                return false;
                
            // Check if coordination scripts exist
            if (!Directory.Exists(CoordinationScriptsPath))
                return true;
                
            // Check if the test script exists
            string testScript = Path.Combine(CoordinationScriptsPath, "quick_test.py");
            return !File.Exists(testScript);
        }
        
        /// <summary>
        /// Update all detected LLM configuration files with latest PerSpec instructions
        /// </summary>
        public static void UpdateLLMConfigurations()
        {
            try
            {
                string projectPath = Directory.GetParent(Application.dataPath).FullName;
                
                // Get the latest LLM content from package
                string llmPath = Path.Combine(PackagePathResolver.PackagePath, "Documentation", "LLM.md");
                if (!File.Exists(llmPath))
                {
                    Debug.LogWarning("[PerSpec] LLM.md not found in package, skipping LLM config update");
                    return;
                }
                
                string llmContent = File.ReadAllText(llmPath);
                
                // Check for various LLM configuration files
                var llmFiles = new Dictionary<string, string>
                {
                    { "Claude", "CLAUDE.md" },
                    { "Cursor", ".cursorrules" },
                    { "OpenAI", ".openai-codex.md" },
                    { "Gemini", Path.Combine(".gemini", "config.md") },
                    { "OpenRouter", ".buddyrules" }
                };
                
                int updatedCount = 0;
                
                foreach (var llmFile in llmFiles)
                {
                    string fullPath = Path.Combine(projectPath, llmFile.Value);
                    if (File.Exists(fullPath))
                    {
                        try
                        {
                            UpdateSingleLLMConfiguration(fullPath, llmContent, llmFile.Key);
                            updatedCount++;
                        }
                        catch (Exception e)
                        {
                            Debug.LogWarning($"[PerSpec] Failed to update {llmFile.Value}: {e.Message}");
                        }
                    }
                }
                
                if (updatedCount > 0)
                {
                    Debug.Log($"[PerSpec] Updated {updatedCount} LLM configuration file(s) with latest instructions");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[PerSpec] Failed to update LLM configurations: {e.Message}");
            }
        }
        
        /// <summary>
        /// Copy agent definitions to .claude/agents/ directory
        /// </summary>
        public static bool CopyAgentDefinitions()
        {
            try
            {
                string projectPath = Directory.GetParent(Application.dataPath).FullName;
                string targetDir = Path.Combine(projectPath, ".claude", "agents");
                string sourceDir = Path.Combine(PackagePathResolver.PackagePath, "Documentation", "agents");

                if (!Directory.Exists(sourceDir))
                {
                    Debug.LogWarning("[PerSpec] Agent definitions source directory not found");
                    return false;
                }

                // Create target directory
                Directory.CreateDirectory(targetDir);

                int copiedCount = 0;
                foreach (string file in Directory.GetFiles(sourceDir, "*.md"))
                {
                    string fileName = Path.GetFileName(file);
                    string targetPath = Path.Combine(targetDir, fileName);
                    File.Copy(file, targetPath, true);
                    copiedCount++;
                }

                if (copiedCount > 0)
                {
                    Debug.Log($"[PerSpec] Copied {copiedCount} agent definition(s) to {targetDir}");
                }

                return copiedCount > 0;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PerSpec] Failed to copy agent definitions: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Update the project's root .gitignore to include PerSpec directory
        /// </summary>
        public static void UpdateGitIgnore()
        {
            try
            {
                string projectPath = Directory.GetParent(Application.dataPath).FullName;
                string gitignorePath = Path.Combine(projectPath, ".gitignore");

                // Check if .gitignore exists
                if (!File.Exists(gitignorePath))
                {
                    // Create a new .gitignore with PerSpec entry
                    string content = "# PerSpec working directory\nPerSpec/\n";
                    File.WriteAllText(gitignorePath, content);
                    Debug.Log("[PerSpec] Created .gitignore and added PerSpec directory");
                    return;
                }

                // Read existing .gitignore
                string existingContent = File.ReadAllText(gitignorePath);

                // Check if PerSpec is already in .gitignore
                if (existingContent.Contains("PerSpec/") || existingContent.Contains("/PerSpec"))
                {
                    // Already in .gitignore
                    return;
                }

                // Add PerSpec to .gitignore
                string newContent = existingContent;
                if (!existingContent.EndsWith("\n"))
                {
                    newContent += "\n";
                }

                newContent += "\n# PerSpec working directory\nPerSpec/\n";
                File.WriteAllText(gitignorePath, newContent);
                Debug.Log("[PerSpec] Added PerSpec directory to project .gitignore");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PerSpec] Could not update .gitignore: {e.Message}");
            }
        }

        #endregion

        #region Private Methods
        
        private static string CopyCoordinationScripts()
        {
            try
            {
                // STEP 1: Get package location
                string packagePath = PackagePathResolver.PackagePath;
                if (string.IsNullOrEmpty(packagePath))
                {
                    Debug.LogError("[PerSpec] Could not find package path");
                    return "Failed: Package path not found";
                }

                // SINGLE SOURCE OF TRUTH - ONLY ONE DIRECTORY!
                string sourceDir = Path.Combine(packagePath, "Editor", "Coordination", "Scripts");
                if (!Directory.Exists(sourceDir))
                {
                    Debug.LogError($"[PerSpec] Source directory not found: {sourceDir}");
                    return "Failed: Source directory not found";
                }

                // STEP 2: Nuke everything in destination
                if (Directory.Exists(CoordinationScriptsPath))
                {
                    var oldFiles = Directory.GetFiles(CoordinationScriptsPath, "*.py");
                    foreach (var file in oldFiles)
                    {
                        File.Delete(file);
                    }
                }
                else
                {
                    Directory.CreateDirectory(CoordinationScriptsPath);
                }

                // STEP 3: Copy everything from source
                var sourceFiles = Directory.GetFiles(sourceDir, "*.py");
                int count = 0;
                foreach (var sourceFile in sourceFiles)
                {
                    string fileName = Path.GetFileName(sourceFile);
                    string destFile = Path.Combine(CoordinationScriptsPath, fileName);
                    File.Copy(sourceFile, destFile, true);
                    count++;
                }

                Debug.Log($"[PerSpec] Copied {count} Python scripts from {sourceDir} to {CoordinationScriptsPath}");
                return $"Copied {count} Python scripts";
            }
            catch (Exception e)
            {
                Debug.LogError($"[PerSpec] Failed to copy coordination scripts: {e.Message}");
                return $"Failed: {e.Message}";
            }
        }

        
        private static void CreateGitIgnore()
        {
            string gitignorePath = Path.Combine(ProjectPerSpecPath, ".gitignore");
            if (!File.Exists(gitignorePath))
            {
                string content = @"# PerSpec working files
*.db
*.db-shm
*.db-wal
*.log
*.tmp";
                File.WriteAllText(gitignorePath, content);
            }
        }
        
        private static void UpdateSingleLLMConfiguration(string configPath, string llmContent, string provider)
        {
            string existingContent = File.ReadAllText(configPath);
            
            // Determine the correct markers based on file type
            string startMarker = "<!-- PERSPEC_CONFIG_START -->";
            string endMarker = "<!-- PERSPEC_CONFIG_END -->";
            
            if (configPath.EndsWith(".cursorrules") || configPath.EndsWith(".buddyrules"))
            {
                startMarker = "# PERSPEC_CONFIG_START";
                endMarker = "# PERSPEC_CONFIG_END";
            }

            // LLM.md ships wrapped in its own markers; strip them so the block
            // is wrapped exactly once (otherwise end markers accumulate per sync)
            llmContent = StripConfigMarkers(llmContent);

            // Check if content already has PerSpec block
            if (existingContent.Contains(startMarker) && existingContent.Contains(endMarker))
            {
                // Replace existing content between markers; span to the LAST end
                // marker so duplicates left by earlier versions are healed
                int startIndex = existingContent.IndexOf(startMarker);
                int endIndex = existingContent.LastIndexOf(endMarker) + endMarker.Length;
                
                if (startIndex != -1 && endIndex > startIndex)
                {
                    string before = existingContent.Substring(0, startIndex);
                    string after = existingContent.Substring(endIndex);
                    
                    // Build new content with markers
                    string newBlock = startMarker + "\n" + llmContent + "\n" + endMarker;
                    existingContent = before + newBlock + after;
                }
            }
            else
            {
                // Append PerSpec content with markers
                string newBlock = "\n\n" + startMarker + "\n" + llmContent + "\n" + endMarker;
                existingContent += newBlock;
            }
            
            // Apply permission block based on EditorPrefs settings
            existingContent = LLMPermissionManager.UpdatePermissionBlock(existingContent, provider);

            // Write back the updated content
            File.WriteAllText(configPath, existingContent);
        }

        private static string StripConfigMarkers(string content)
        {
            string[] markerLines =
            {
                "<!-- PERSPEC_CONFIG_START -->",
                "<!-- PERSPEC_CONFIG_END -->",
                "# PERSPEC_CONFIG_START",
                "# PERSPEC_CONFIG_END"
            };

            var lines = content.Split('\n')
                .Where(line => !markerLines.Contains(line.Trim()))
                .ToArray();
            return string.Join("\n", lines).Trim();
        }
        
        /// <summary>
        /// Initialize the SQLite database by running the Python script
        /// </summary>
        private static bool InitializeDatabase()
        {
            try
            {
                // Check if database already exists
                if (File.Exists(DatabasePath))
                {
                    Debug.Log("[PerSpec] Database already exists");
                    return true;
                }
                
                string pythonScript = Path.Combine(CoordinationScriptsPath, "db_initializer.py");
                
                // Check if Python script exists
                if (!File.Exists(pythonScript))
                {
                    Debug.LogError($"[PerSpec] Database initialization script not found: {pythonScript}");
                    return false;
                }
                
                // Prepare process to run Python script
                var processInfo = new ProcessStartInfo
                {
                    FileName = "python",
                    Arguments = $"\"{pythonScript}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = Directory.GetParent(Application.dataPath).FullName
                };
                
                Debug.Log("[PerSpec] Initializing database...");
                
                using (var process = Process.Start(processInfo))
                {
                    if (process == null)
                    {
                        Debug.LogError("[PerSpec] Failed to start Python process");
                        return false;
                    }
                    
                    // Wait for process to complete (5 second timeout)
                    bool completed = process.WaitForExit(5000);
                    
                    if (!completed)
                    {
                        Debug.LogError("[PerSpec] Database initialization timed out");
                        try { process.Kill(); } catch { }
                        return false;
                    }
                    
                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();
                    
                    if (process.ExitCode == 0)
                    {
                        Debug.Log($"[PerSpec] Database initialized successfully: {DatabasePath}");
                        if (!string.IsNullOrEmpty(output))
                        {
                            Debug.Log($"[PerSpec] Output: {output}");
                        }
                        return true;
                    }
                    else
                    {
                        Debug.LogError($"[PerSpec] Database initialization failed with exit code {process.ExitCode}");
                        if (!string.IsNullOrEmpty(error))
                        {
                            Debug.LogError($"[PerSpec] Error: {error}");
                        }
                        return false;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[PerSpec] Failed to initialize database: {e.Message}");
                Debug.LogError($"[PerSpec] Make sure Python is installed and available in PATH");
                return false;
            }
        }
        
        #endregion
    }
}