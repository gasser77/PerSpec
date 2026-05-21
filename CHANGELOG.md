# Changelog

All notable changes to the PerSpec Testing Framework will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added — Unity Helper toolbox expansion
- **16 new scene actions** in `SceneTaskExecutor` (43 total): `WrapWithParent`, `AddComponentToMatching`, `Validate`, `BatchSetProperty`, `ApplyRecipe`, `SetActive`, `SetSiblingIndex`, `DuplicateGameObject`, `MoveGameObject`, `RemoveComponent`, `GetProperty`, `FindObjects`, `SetPropertyOnMatching`, `ClosePrefab`, `RemoveMissingScripts`, `ReportMissingScripts`. Switch dispatch retains both `SetParent` and `SetParentByTransform`.
- **`FindInActiveContext`** scene+prefab-aware lookup replacing 50× call sites that used bare `GameObject.Find`. Actions now work uniformly inside the active scene and inside Prefab Mode.
- **List-property variants** (`SetListPropertyOnPrefab` / `OnSceneFile` / `OnGameObject` / `OnScriptableObject`) for cleaner dispatch and prefab-stage tracking.
- **JSON Schemas** in `Editor/Schemas/` (`scenario.schema.json`, `recipe.schema.json`, `validator-rules.schema.json`) for editor autocomplete and pre-execution validation.
- **`unityhelper_coordinator.py`** runs Draft-07 schema validation against `scenario.schema.json` before submission. Falls back gracefully if `jsonschema` is not installed; bypassable with `--skip-schema-validation`.
- **`TmproTaskExecutor`** with 4 TextMeshPro-specific actions, and **`Runtime/Localization/ArabicShaper.cs`** for Arabic text shaping.
- **Async-task plumbing**: `TaskExecutorRegistry.IsAsyncTask(task)` exposed for non-UI coordinators; `ScenarioExecutorCoordinator` drives execution via a per-frame state machine that honors async tasks.
- **`LocalizationTaskExecutor`** expanded with additional actions.

### Added — Missing-script handling on prefabs
- **`OpenPrefab`** now scans the opened stage for GameObjects with missing `MonoBehaviour` script slots. Detection uses `GameObjectUtility.GetMonoBehavioursWithMissingScriptCount` plus `AssetDatabase.GetDependencies` + `MonoScript.GetClass()` for class-load failures (no `.prefab` text parsing). When found, the task **succeeds with a `WARNING:`-prefixed `task.result`** and a `Debug.LogWarning` listing affected GameObject paths and the resolvable broken-script paths.
- **`SavePrefab`** runs the same scan as a pre-flight; if missing slots exist, the task **fails** with an actionable error listing affected paths and broken script references, replacing Unity's opaque generic save error.
- **`RemoveMissingScripts`** new action — strips missing-script slots from the open prefab (or a scoped sub-target via `target` param) so `SavePrefab` can succeed afterward. Uses `GameObjectUtility.RemoveMonoBehavioursWithMissingScript`.
- **`ReportMissingScripts`** new diagnostic action — reports missing-script GameObjects + resolvable dependency GUIDs for a prefab via Unity APIs only (`PrefabUtility.LoadPrefabContents`, `EditorJsonUtility.ToJson`, `AssetDatabase`). Documents that orphan GUIDs (where both `.cs` and `.meta` were deleted) are not retrievable through Unity APIs and require git history (`git log --all --diff-filter=D`).

### Documentation
- `Documentation/unity-helper-tasks.md` (1219 lines): documented every dispatched scene action — 43 scene + 17 localization sections, plus the `Validate hasComponent` extension.
- `Documentation/LLM.md`: added "Unity Helper — Augmented Toolbox" section with explicit pre-flight instructions for AI agents to load the action catalogue and JSON Schemas before authoring scenarios/recipes/validator-rules.

## [1.6.1] - 2026-05-13

### Documentation
- **architecture.md**: Updated state machine to reflect the fine-grained statuses (`processing`, `executing`, `finalizing`, `timeout`, `inconclusive`) introduced in v1.6.0. Documented the `created_at` storage convention (`.NET DateTime.Now.Ticks` from Python) and the SQLite NUMERIC-affinity corruption it prevents. Rewrote the `TestExecutor` and `PlayModeTestCompletionChecker` flow sections to describe the canonical/fallback completion paths and the `MIN_RUN_SECONDS` / `IsXmlComplete` / write-time-freshness guards. Updated Python `wait_for_completion` example with the v1.6.0 retry + XML-wait semantics.
- **LLM.md**: Replaced the "Understanding Test Status" callout. The previous text described the pre-v1.6.0 behavior where `--wait` only waited for request processing (not actual test execution) and instructed readers to visually verify completion. That guidance is now obsolete and actively misleading — `--wait` now blocks until BOTH the row reaches a terminal status AND the XML is on disk. New text lists the terminal statuses and what each means.

### Notes
- No code changes; documentation-only release so openupm picks up the corrected guidance alongside the v1.6.0 fixes.

## [1.6.0] - 2026-05-12

### Fixed
- **Root cause of `Request N not found` on `quick_test.py method ... -p play --wait`: created_at storage corruption**
  - Python's `INSERT INTO test_requests` relied on SQLite's `CURRENT_TIMESTAMP` default, which stores a TEXT value like `"2026-05-13 01:25:38"`. Unity's sqlite-net layer reads/writes `created_at` as a `DateTime` mapped to INT64 ticks. When sqlite-net's `_connection.Update(entity)` ran on a Python-inserted row (any status update — `processing`, `executing`, `failed`), it triggered SQLite's NUMERIC affinity coercion that turned the TEXT into the integer `2026` (leading digit prefix). The fresh row's `created_at` then satisfied `created_at < ticks_cutoff` for every subsequent cleanup `DELETE FROM test_requests WHERE created_at < ?`, so the row vanished mid-run.
  - Fix: `test_coordinator.py` now writes `created_at` as `.NET DateTime.Now.Ticks` (INT64) via the new `_dotnet_ticks_now()` helper. sqlite-net round-trips the integer without coercion and cleanup comparisons behave correctly. Verified end-to-end against the 7-second `Should_Wait_7_Seconds_Before_Publishing` reproduction: row now transitions `pending → processing → completed`, total=1 passed=1, duration=7.04s.
  - Defense in depth: `TestCoordinatorEditor.RecoverOrphanedRequests` (`TestCoordinatorEditor.cs:92-143`) now re-verifies request age in C# before marking anything `failed`, so even if a future SQL query spuriously returns fresh rows, they won't be flagged as orphaned-by-domain-reload.
- **Premature `completed` status for single-method PlayMode runs (independent issue surfaced during this investigation)**
  - Single-method PlayMode runs could flip to `completed` the instant an AppData `TestResults.xml` was observed, racing the actual test execution.
  - `TestExecutor.cs` no longer marks a request `completed` from the AppData branch of file monitoring. The canonical completion path in `CheckAndProcessResultFile` now also enforces:
    - At least `MIN_RUN_SECONDS` (3s) elapsed since dispatch
    - `IsXmlComplete` (already required for the PerSpec branch) — now applied to the AppData branch too via the regular monitoring loop
    - File write-time newer than `_monitorStartDateTime - 5s` so stale XMLs from a prior run cannot trigger completion
  - The AppData-copy branch in `GetLatestResultFile` now only upgrades `processing → executing` and lets the standard monitoring loop drive completion.
- **Results XML missing from `PerSpec/TestResults/` on the `RunFinished` path**
  - Added `EnsureResultXmlInPerSpec()` invoked from `RunFinished`. If neither the `TestResultXMLExporter` callback nor file monitoring wrote a fresh XML into `PerSpec/TestResults/`, it copies Unity's AppData `TestResults.xml` in before the row is marked `completed`. Guarantees `test_results.py latest` can see the file.
- **Schema CHECK constraint silently rejecting `'inconclusive'` writes**
  - `'inconclusive'` is now in the CHECK list across `db_initializer.py`, `db_update_status_constraint.py`, and `db_auto_maintenance.py`.
  - New migration `apply_migration_v5` in `db_auto_maintenance.py` adds the value to existing databases.
  - `db_update_status_constraint.py` is now idempotent.
- **Python poller aborting on a single missing-row read**
  - `TestCoordinator.wait_for_completion` tolerates up to 10 consecutive `fetchone() == None` reads (≈10s) before raising. Absorbs concurrent cleanup, mid-VACUUM, and other transient invisibilities.
- **`test_results.py latest` and `wait_for_completion` blind to Unity's AppData XML**
  - Both now enumerate Unity's `%LocalAppData%Low/<Company>/<Product>/TestResults.xml` fallback locations and import the file into `PerSpec/TestResults/` if it's newer than anything we already have.

### Changed
- `quick_test.py --wait` now blocks until BOTH the DB row reaches a terminal status AND a matching results XML is on disk in `PerSpec/TestResults/`. Previous behavior returned as soon as the row was processed by Unity (which, due to the race fixed above, often happened before the run actually finished). Help text updated; `test_coordinator.py:wait_for_completion` docstring updated.
- `wait_for_completion` now accepts `xml_grace_seconds` (default 15) and `missing_row_retries` (default 10) parameters for callers who need to tune.
- `CheckAndProcessResultFile` now emits `'inconclusive'` for method-level runs whose entire result set is skipped (previously emitted `'completed'`).

### Added
- `EnsureResultXmlInPerSpec()` helper in `TestExecutor.cs`.
- `_appdata_unity_candidates()` / `_import_appdata_xml_into_perspec()` helpers in `test_results.py`.
- `_await_results_xml()`, `_appdata_low_path()`, `_appdata_unity_dirs()`, `_dotnet_ticks_now()`, `_parse_request_timestamp()` helpers in `test_coordinator.py`.

### Internal
- Fixed long-standing typo in `db_auto_maintenance.py` migration v4: `menu_requests` → `menu_item_requests`. The typo caused v4 to fail with `no such table` on every run, which broke the migration chain so v5 (and any future migrations) could never apply.

### Technical Details
- Minor version bump because `--wait` semantics changed in a way callers can observe (it now blocks for longer in the common case). No public C# API surface changes.
- Re-run `python PerSpec/Coordination/Scripts/db_update_status_constraint.py` after pulling this version to add `'inconclusive'` to existing databases; the migration is idempotent.

## [1.5.20] - 2026-04-16

### Added
- **Unity Helper — Declarative Scenario Execution**
  - Scenario-based scene and asset automation via JSON task files
  - `ScenarioRunner.cs` EditorWindow for interactive task execution (run, skip, retry, reset)
  - `ScenarioExecutorCoordinator.cs` polls PerSpec's SQLite database for scenario requests
  - `SceneTaskExecutor.cs` with 22 scene/asset/component actions (CreateScene, AddGameObject, SetProperty, InstantiatePrefab, SetTransform, SetRectTransform, SaveAsPrefab, SetListProperty, TakeScreenshot, WaitForGameObject, CallMethod, InspectGameObject, ExportHierarchy, etc.)
  - `LocalizationTaskExecutor.cs` with 17 localization actions (gated by `HAS_UNITY_LOCALIZATION` compile guard via asmdef `versionDefines`)
  - `unityhelper_coordinator.py` Python CLI for submitting scenario requests from terminal
  - Extensible executor registration via `TaskExecutorRegistry`
  - Two-way communication through JSON `status`/`error`/`result` fields
  - Self-verification for all mutation actions (AddGameObject, SetProperty, DeleteGameObject, etc.)
  - New assembly: `PerSpec.Editor.UnityHelper` (Editor-only, with optional localization dependency)

- **Unity Helper Documentation**
  - `Documentation/unity-helper.md` — overview, architecture, quick start, design philosophy
  - `Documentation/unity-helper-tasks.md` — complete action and parameter reference for all 39 actions
  - Added Unity Helper natural language commands and intent mappings to `Documentation/LLM.md`
  - Added Unity Helper links to `Documentation/index.md`

### Technical Details
- Unity Helper code located in `Editor/UnityHelper/` (7 C# files, ~5K LOC)
- Python coordinator at `Editor/Coordination/Scripts/unityhelper_coordinator.py` (373 lines)
- Uses same SQLite coordination pattern as existing PerSpec coordinators
- Localization support conditional via `HAS_UNITY_LOCALIZATION` — defined automatically when `com.unity.localization >= 1.0.0` is installed

## [1.5.19] - 2026-02-17

### Fixed
- **Orphaned Test Requests After Domain Reload**
  - Test requests were getting permanently stuck in "processing" or "executing" status after Unity domain reloads
  - Root cause: Domain reload destroys the `TestExecutor` instance and its file monitoring callbacks
  - New `TestCoordinatorEditor` creates fresh instances on reload, losing track of in-progress tests
  - Added `RecoverOrphanedRequests()` method that runs on initialization to detect and handle stuck requests
  - Requests stuck for more than 3 minutes are automatically recovered or marked as failed
  - Recovery attempts to find and parse test results from Unity's AppData `TestResults.xml`
  - If valid results found, request is marked as completed with accurate test counts
  - If no results found, request is marked as failed with clear error message
  - Recovered result files are copied to `PerSpec/TestResults/` for consistency

### Added
- **GetStuckRequests() Method in SQLiteManager**
  - New method to find requests stuck in active states (processing, executing, running, finalizing)
  - Takes `TimeSpan maxAge` parameter to identify likely orphaned requests
  - Used by recovery logic to detect requests that outlived their monitoring callbacks

### Technical Details
- Recovery runs in static constructor after database initialization
- Checks AppData path: `%LocalAppData%/Unity/Editor/TestResults.xml`
- Validates result file timestamp is after request creation time
- Parses NUnit XML format for test counts (total, passed, failed, skipped) and duration
- Logs all recovery actions for debugging visibility

## [1.5.18] - 2026-02-17

### Added
- **Debug Logging for Early Completion Investigation**
  - Added comprehensive `[TestExecutor-FM-DEBUG]` logs to trace early PlayMode test completion
  - Logs when `_monitorStartDateTime` is set and its value
  - Logs all XML files found and their exact timestamps
  - Logs cutoff time calculations for freshness checks
  - Logs whether each file passes/fails the freshness check
  - **Critical**: Logs when completion logic at lines 654-705 triggers
  - Identifies the exact provenance of early completion issues

### Technical Details
- Debug logs use `[TestExecutor-FM-DEBUG]` prefix for easy filtering
- To view logs: `python PerSpec/Coordination/Scripts/monitor_editmode_logs.py --no-limit | grep "FM-DEBUG"`
- Logs cover: StartFileMonitoring(), GetLatestResultFile(), AppData check, completion logic
- Timestamps use ISO 8601 format (`:O` specifier) for precise comparison

## [1.5.17] - 2026-02-17

### Fixed
- **Complete Fix: Early PlayMode Test Result Publishing**
  - Previous fix (v1.5.16) was incomplete because `_currentRequest.StartedAt` is NULL when `GetLatestResultFile()` is called from `StartFileMonitoring()`
  - The request status is only updated to "executing" (which sets `StartedAt`) AFTER file monitoring starts
  - Added new `_monitorStartDateTime` field that captures `DateTime.Now` at the START of `StartFileMonitoring()`
  - All freshness checks now use `_monitorStartDateTime` instead of `_currentRequest.StartedAt`
  - Added freshness filter to `PerSpec/TestResults` directory check (previously had no freshness check at all)
  - Stale result files (written before monitoring started minus 30 seconds buffer) are now correctly skipped
  - Prevents previous run's results from being immediately published when a new test run begins

### Technical Details
- `StartFileMonitoring()` now sets `_monitorStartDateTime = DateTime.Now` before calling `GetLatestResultFile()`
- `GetLatestResultFile()` uses `_monitorStartDateTime` (with 30-second buffer) or falls back to `DateTime.Now.AddMinutes(-5)`
- Both PerSpec/TestResults and AppData file checks now have consistent freshness guards
- The fix ensures tests genuinely run before their results are published

## [1.5.16] - 2026-02-17

### Fixed
- **Root Cause: `TestExecutor` Consuming Stale AppData Results at Startup**
  - `TestExecutor.GetLatestResultFile()` was copying and immediately processing Unity's AppData `TestResults.xml` during `StartFileMonitoring()` — before the current test even entered Play Mode
  - The previous run's AppData file passed no freshness check, so `RequestType == "method"` tests were instantly marked `completed` with the wrong results
  - Added a guard: the AppData file's source `LastWriteTime` must be ≥ `request.StartedAt - 30s`; stale files are skipped with a log message

- **`PlayModeTestCompletionChecker` Stale PerSpec XML Filter**
  - `GetLatestResultFile()` now accepts a `minModifiedTime` parameter and only considers XML files written at or after the current request's `StartedAt` timestamp (minus a 5-second buffer)
  - Prevents old PerSpec TestResults files from satisfying the post-PlayMode completion check

- **False Trigger During Mid-PlayMode Domain Reload**
  - Unity fires `EnteredEditMode` during in-play script recompilation (domain reload), not only on genuine Play Mode exit
  - `OnPlayModeStateChanged` now guards with `!EditorApplication.isPlayingOrWillChangePlaymode` so `CheckForCompletedTests()` is only called on genuine PlayMode exit

### Added
- **Diagnostic Test: `LongRunningPlayModeTest`**
  - `Assets/Tests/PlayMode/LongRunningPlayModeTest.cs` — awaits 7 seconds and asserts `>= 6.5s` elapsed
  - Confirms result publishing only occurs after the test genuinely completes

## [1.5.15] - 2026-02-11

### Fixed
- **PlayMode Test Results Not Retrieved**
  - Fixed status mismatch preventing PlayMode test results from being captured
  - `GetRunningRequests()` now includes all active test states: `running`, `processing`, `executing`, `finalizing`
  - Previously only checked for `"running"` status, but test execution uses: `pending → processing → executing → finalizing → completed`
  - `PlayModeTestCompletionChecker` now correctly finds active PlayMode tests after domain reload
  - Test results XML files are now properly parsed and database is updated with results

### Technical Details
- Modified `SQLiteManager.GetRunningRequests()` to query for all active states
- Root cause: Test coordinator set status to "processing"/"executing" but completion checker only looked for "running"
- After Unity exits PlayMode, the checker now finds the request and parses the XML results file

## [1.5.14] - 2025-12-04

### Changed
- Version bump for package release

## [1.5.13] - 2025-12-04

### Added
- **Comprehensive Reset System for Control Center**
  - New "Reset" button in Control Center with full system reset capability
  - Stops all coordination services (BackgroundPoller, TestCoordinator, AssetRefresh, MenuItem, SceneHierarchy)
  - Closes database connections with aggressive garbage collection (2.5s wait)
  - Drops and recreates all database tables without deleting the database file
  - Cleans all log directories (EditModeLogs, PlayModeLogs, TestResults, SceneHierarchy)
  - Restarts all coordination services automatically
  - Complete reset in ~5 seconds without Unity restart required
  - Preserves EditorPrefs settings and compiler symbols (PERSPEC_DEBUG, PERSPEC_DOTS_ENABLED)

- **Python Script Enhancement**
  - Added `reset_tables()` function to `db_initializer.py` for safe database reset
  - Keeps database file intact while dropping and recreating all tables
  - New command: `python db_initializer.py reset` (keeps file)
  - Preserved old behavior: `python db_initializer.py reset_full` (deletes file)

### Fixed
- **Database Reset Permission Errors**
  - Fixed "file in use" errors during database reset operations
  - Extended wait time from 1s to 2.5s for database connection cleanup
  - Added multiple garbage collection cycles to ensure connections close
  - Database tables now reset via SQL commands instead of file deletion
  - Prevents Windows "PermissionError: file being used by another process"

### Improved
- **Service Coordination**
  - All coordinators now have public `StopPolling()` and `StartPolling()` methods
  - Better cleanup of background timers and database connections
  - More robust error handling with continue-on-error approach
  - Detailed progress reporting via progress bar (7 steps)

### Technical Details
- ResetService.cs: ~437 lines of comprehensive reset orchestration
- Modified 5 coordinator files with reset support
- Python script updated with table-preserving reset function
- All changes in package location (Packages/com.digitraver.perspec/)

## [1.5.12] - 2025-12-03

### Fixed
- **Windows Unicode Encoding Error in Python Scripts**
  - Fixed UnicodeEncodeError when Python scripts print Unity logs containing emoji characters
  - Windows cmd.exe defaults to cp1252 encoding, which cannot display Unicode emoji like ✅ and ❌
  - Added UTF-8 encoding configuration to all Python coordination scripts
  - Scripts now call `sys.stdout.reconfigure(encoding='utf-8', errors='replace')` at startup
  - Prevents crashes when displaying Unity debug logs with Unicode characters

### Technical Details
- Affected 9 scripts: test_playmode_logs.py, test_results.py, quick_test.py, scene_hierarchy.py, quick_clean.py, quick_menu.py, quick_refresh.py, db_migrate.py, editor_log_monitor.py
- monitor_editmode_logs.py already had a similar fix using io.TextIOWrapper
- Uses Python 3.7+ `reconfigure()` method for clean, cross-platform UTF-8 support
- The `errors='replace'` parameter ensures graceful fallback if any character cannot be encoded

## [1.5.11]
- **Some Unity 6 LTS breakage**

## [1.5.10] - 2025-11-26

### Fixed
- **Debug Test Log Levels Button**
  - Fixed TestLogLevels() method to properly demonstrate conditional compilation
  - PerSpecDebug calls now wrapped in #if PERSPEC_DEBUG blocks
  - When debug is disabled, button correctly shows only Unity Debug.Log messages
  - When debug is enabled, button shows both Unity and PerSpecDebug logs
  - Added PerSpec.Runtime.Debug assembly reference to Editor.Services assembly

### Technical Details
- TestLogLevels() now actually calls PerSpecDebug methods instead of just printing messages about them
- Demonstrates that PerSpecDebug calls are stripped at compile time when PERSPEC_DEBUG is not defined
- Provides clear visual confirmation of debug logging state

## [1.5.9] - 2025-11-26

### Changed
- **Debug Logging Refactored to Use NamedBuildTarget API**
  - Migrated from legacy `csc.rsp` file approach to Unity's PlayerSettings scripting define symbols
  - Created `DebugLoggingService` using NamedBuildTarget API (mirrors DOTSService architecture)
  - Now consistent with DOTS implementation for managing compiler directives
  - Supports per-platform debug logging configuration
  - Supports BuildProfile integration in Unity 6+
  - Updated `DebugService` to delegate to new `DebugLoggingService`
  - Updated `PerSpecDebugWindow` to use new service
  - Removed `EnableDebugLogging()` and `DisableDebugLogging()` methods from `PerSpecDebugSettings` (Runtime assembly cannot reference Editor)

### Technical Details
- `PerSpecDebugSettings.IsDebugEnabled` (Runtime property) still works in all builds for checking if debug logging is compiled in
- To enable/disable debug logging in Editor: Use `DebugLoggingService.EnableDebugLogging()` or `DebugService.EnableDebugLogging()`
- Control Center UI automatically uses new service
- No `csc.rsp` file management required - all handled through PlayerSettings
- Scripting define symbol `PERSPEC_DEBUG` managed via NamedBuildTarget API across all platforms

## [1.5.8] - 2025-11-21

### Changed
- **DOTSService Code Cleanup**
  - Removed legacy csc.rsp migration code (from v1.5.4-1.5.6)
  - Simplified EnableDOTS() and DisableDOTS() methods
  - Removed migration methods: MigrateFromCscRsp() and CleanupCscRsp()
  - Removed csc.rsp-related constants: CSC_RSP_PATH and OLD_DOTS_DEFINE
  - Removed System.IO using statement (no longer needed)
  - Reduced code complexity by ~80 lines

### Technical Details
- Migration from csc.rsp to PlayerSettings was introduced in v1.5.7
- Users on versions older than v1.5.7 should upgrade to v1.5.7+ before updating to this version
- DOTSService now exclusively uses NamedBuildTarget API with PlayerSettings
- No breaking changes - all public APIs (EnableDOTS, DisableDOTS, ToggleDOTS, IsDOTSEnabled) remain identical
- BuildProfile support for Unity 6+ maintained

## [1.5.7] - 2025-11-20

### Fixed
- **Scene Hierarchy Exporter Unity Version Compatibility**
  - Fixed compilation error on Unity 2021.3 using `FindObjectsByType()` API
  - `FindObjectsByType()` was introduced in Unity 2021.3.18 but preprocessor directives only support major.minor versions
  - Added compiler directive using `UNITY_2022_2_OR_NEWER` as safe cutoff
  - Unity 2021.3.x now uses legacy `FindObjectsOfType()` API for guaranteed compatibility
  - Unity 2022.2+ uses modern `FindObjectsByType()` with performance optimizations (FindObjectsSortMode.None, FindObjectsInactive control)
  - Code now compiles correctly across all supported Unity versions

## [1.5.6] - 2025-11-19

### Fixed
- **README Documentation**
  - Removed broken "Technical Architecture Deep Dive" link that pointed to non-existent section
  - Link was causing 404 errors on GitHub

### Improved
- **TDD Workflow Documentation**
  - Added LLM automation instructions to "The Recommended TDD Workflow" section
  - Each step (2-4) now shows both manual Control Center actions and LLM prompts
  - Added "Automating with LLM Prompts" subsection with command reference table
  - Clearly documents that capable LLMs automatically run verification after code changes
- **CLAUDE.md LLM Instructions**
  - Added "Natural Language Prompt Recognition" subsection
  - Explicit mapping of natural language prompts to Python commands
  - Mandatory automatic triggering instructions for LLMs after code changes
  - Emphasizes that automatic verification is NOT optional for LLM assistants

### Added
- **Natural Language Prompt Table**
  - "refresh Unity" → `quick_refresh.py full --wait`
  - "show errors" or "get errors" → `monitor_editmode_logs.py --errors`
  - "run tests" → `quick_test.py all -p edit --wait`
- **Cross-references**
  - README TDD workflow links to LLM automation section
  - README references AI/LLM Integration Guide for advanced usage

## [1.5.5] - 2025-11-19

### Improved
- **README Documentation**
  - Replaced git URL installation with OpenUPM CLI instructions
  - Added manual installation section with required scoped registry configuration
  - Clearly documents all three required scopes: `com.digitraver.perspec`, `com.cysharp.unitask`, `com.gilzoide.sqlite-net`
  - Reorganized Command Reference to appear after Quick Start
  - Simplified command descriptions to focus on Control Center UI

### Changed
- **Installation Instructions**
  - Removed non-functional git URL method
  - OpenUPM CLI is now the recommended installation method
  - Manual manifest.json configuration available as collapsible alternative

## [1.5.4] - 2025-11-19

### Fixed
- **DOTS Directive Not Reaching Package Assemblies**
  - `Assets/csc.rsp` only affected Assets/ assemblies, not package assemblies
  - DOTSService now uses NamedBuildTarget API with PlayerSettings
  - PlayerSettings scripting define symbols are truly global (reach all assemblies)
  - Properly handles BuildProfiles in Unity 6+ via `#if UNITY_6000_0_OR_NEWER`

### Changed
- **DOTSService Implementation**
  - Switched from csc.rsp to PlayerSettings.SetScriptingDefineSymbols
  - Uses modern NamedBuildTarget API (Unity 2021.2+)
  - Adds directive to all platforms (Standalone, iOS, Android, WebGL, etc.)
  - Updates both BuildProfile and PlayerSettings in Unity 6+

### Added
- **Migration from csc.rsp**
  - Automatically migrates existing `PERSPEC_DOTS_ENABLED` from csc.rsp to PlayerSettings
  - Cleans up empty csc.rsp files after migration

## [1.5.3] - 2025-11-19

### Fixed
- **Automatic SQLite Table Initialization**
  - SQLite tables are now automatically created when PerSpec is enabled
  - Eliminates "no such table" errors when Python scripts access database before initialization
  - New `DatabaseInitializer.cs` creates all 8 tables using C# (no Python dependency)
  - Called automatically on Editor startup when PerSpec is enabled
  - Called when enabling PerSpec via Control Center
  - Called on-demand when SQLiteManager detects missing database

### Added
- **scene_hierarchy_requests Table in db_initializer.py**
  - Python script now creates all 8 tables (was missing scene_hierarchy_requests)
  - Maintains parity between C# and Python database initialization

### Improved
- **Database Initialization Robustness**
  - Multiple entry points ensure database is always ready
  - Uses `CREATE TABLE IF NOT EXISTS` for idempotent operations
  - WAL mode enabled for better concurrency

## [1.5.2] - 2025-11-19

### Changed
- **DOTSService Now Uses csc.rsp**
  - Switched from BuildProfile/PlayerSettings to csc.rsp file approach
  - PERSPEC_DOTS_ENABLED directive now managed alongside PERSPEC_DEBUG
  - Multiple directives can coexist in Assets/csc.rsp (one per line)
  - Removed dependency on BuildProfileHelper for DOTS toggle
  - Simplified status display in Control Center

### Improved
- **Unified Compiler Directive Management**
  - Both debug logging and DOTS support now use same csc.rsp mechanism
  - Consistent behavior across all Unity versions
  - No more BuildProfile vs PlayerSettings confusion
  - Handles edge cases: deletes csc.rsp when last directive is removed

## [1.5.1] - 2025-11-19

### Fixed
- **Thread Safety Bug in PerSpecDebug**
  - Removed non-thread-safe dictionary cache that could cause race conditions during async operations
  - Feature logging methods (LogFeatureStart, LogFeatureProgress, etc.) now inline ToUpper() calls
  - Eliminates potential InvalidOperationException during concurrent logging

### Added
- **Simplified Debug Logging Control via csc.rsp**
  - New `PerSpecDebugSettings.EnableDebugLogging()` - creates Assets/csc.rsp
  - New `PerSpecDebugSettings.DisableDebugLogging()` - deletes Assets/csc.rsp
  - New `PerSpecDebugSettings.IsCscRspPresent` property
  - New `PerSpecDebug.VerifyEnabled()` method to confirm logging status

### Changed
- **Debug Directive Management**
  - PERSPEC_DEBUG now controlled via Assets/csc.rsp file (global to all code)
  - Bypasses BuildProfile/PlayerSettings synchronization issues
  - Single source of truth - file present = logging enabled
  - Improved validation messages with clear instructions

### Improved
- **Startup Validation**
  - ValidateDebugConfiguration now detects mismatches between file and compile state
  - Clear warning when csc.rsp exists but recompilation is needed
  - Helpful instructions in console for enabling/disabling logging

## [1.5.0] - 2025-11-19

### Breaking Changes
- **DOTS/Entities Now Optional**
  - DOTS support is now gated behind `PERSPEC_DOTS_ENABLED` compiler directive
  - Users must manually enable DOTS support in Control Center > Debug Settings
  - Unity.Entities removed from package dependencies (now optional)
  - Minimum Unity version lowered to 2021.3 (from 6000.0)

### Added
- **DOTSService Toggle System**
  - New `DOTSService` class for managing DOTS/Entities compiler directive
  - Toggle in Control Center Dashboard shows DOTS status
  - Toggle in Control Center Debug Settings tab to enable/disable DOTS support
  - Methods: `IsDOTSEnabled`, `EnableDOTS()`, `DisableDOTS()`, `ToggleDOTS()`

- **Conditional DOTS Compilation**
  - Pure DOTS asmdefs use `defineConstraints` for `PERSPEC_DOTS_ENABLED`
  - Mixed asmdefs use `versionDefines` to detect Unity.Entities package
  - All DOTS C# files wrapped with `#if PERSPEC_DOTS_ENABLED` guards

### Changed
- **Package Configuration**
  - Updated `package.json` minimum Unity version from 6000.0 to 2021.3
  - Removed `com.unity.entities` from required dependencies
  - Added "DOTS" to package keywords

- **Assembly Definitions**
  - `PerSpec.Runtime.DOTS.asmdef` - Added defineConstraints
  - `PerSpec.Editor.DOTS.asmdef` - Added defineConstraints
  - `PerSpec.Runtime.asmdef` - Removed DOTS refs, added versionDefines
  - `PerSpec.Editor.asmdef` - Removed DOTS refs, added versionDefines
  - `PerSpec.Editor.Coordination.asmdef` - Removed DOTS refs, added versionDefines
  - `PerSpec.Editor.PrefabFactories.asmdef` - Removed DOTS refs, added versionDefines

### Migration Notes
- If upgrading from 1.4.x with DOTS code, enable DOTS support manually:
  1. Open Control Center (Tools > PerSpec > Control Center)
  2. Go to Debug Settings tab
  3. Click "Enable DOTS Support"
- Ensure Unity.Entities package is installed before enabling DOTS support

## [1.4.0] - 2025-11-07

### Added
- **New LLM Provider Support**
  - Added Windsurf IDE support with directory-based configuration (`.windsurf/rules/`)
  - Added OpenAI direct API support (`.openai.md`)
  - Added DeepSeek model support (`.deepseek.md`)
  - Total of 8 supported LLM providers now available in Control Center

### Improved
- **LLM Configuration Management**
  - Enhanced `CreateLLMConfiguration()` to handle directory-based configs (Windsurf)
  - Updated `UpdateLLMConfiguration()` with special handling for Windsurf directory structure
  - Improved `DetectLLMConfigurations()` to detect directory-based configs
  - Better provider detection logic in `GetProviderFromPath()` for all new providers

### Changed
- **Control Center UI**
  - Updated LLM Setup tab instructions to list all 8 supported providers
  - Increased instructions text area height to 220px for better readability
  - Enhanced provider selection interface with new options

## [1.3.4] - 2025-01-27

### Fixed
- **LLM Setup Tab in Control Center**
  - Replaced problematic dropdown with checkbox system using EditorPrefs
  - Fixed issue where dropdown selection wouldn't persist between window reopens
  - Added persistent state storage that survives Unity restarts and script recompiles
  - Checkboxes now properly maintain their selected state across all sessions

### Improved
- **LLM Configuration UI**
  - Added scrollable area for checkbox list (120px height)
  - New "Select All" and "Clear All" buttons for bulk operations
  - Support for creating/updating multiple LLM configurations at once
  - Better visual feedback with clear checkbox states
  - Window title simplified from "PerSpec Control Center" to "Control Center"

### Added
- **Multi-Selection Support**
  - Can now select and create multiple LLM configurations simultaneously
  - Batch processing with detailed success/error reporting
  - Centralized configuration path management

## [1.3.3] - 2025-01-22

### Added
- **PlayMode Log Search Functionality**
  - New `--search` or `-S` flag to search for keywords across all PlayMode log files
  - Support for multiple keywords with AND/OR logic
  - Case-insensitive search option with `-i` or `--ignore-case`
  - `--any` flag to match ANY keyword instead of ALL keywords
  - Keyword highlighting in search results (yellow background)
  - Search works in both message content and stack traces
  - Combinable with existing filters (--errors, --cs-errors, --level)
  - Shows search statistics including matches found and search mode

### Improved
- **PlayMode Log Viewer**
  - Enhanced help text with search examples
  - Better user guidance for search operations
  - Documentation updated with search usage examples

## [1.3.2] - 2025-01-20

### Added
- **ECS/DOTS Compilation Error Detection**
  - BC error codes (BC0001-BC9999) for Burst Compiler errors
  - DC error codes (DC0001-DC9999) for Domain Compilation errors
  - SGICE error codes for Source Generator Internal Compiler Errors
  - Detection of ECS-specific patterns (Entities.ForEach, EntityCommandBuffer, etc.)
  - Job System error patterns (NativeArray, JobHandle, IJobParallelFor)
  - New `--ecs-errors` flag to filter only ECS/DOTS/Burst errors

### Improved
- **Error Categorization in EditMode Logs**
  - Error types now displayed with compilation errors (CS/BC/DC/SG/Burst/ECS/Jobs)
  - Enhanced `is_compilation_error()` function with comprehensive patterns
  - Better distinction between C# compiler, Burst, and Domain compilation errors
  - Error statistics showing distribution by compilation type
  - More accurate error filtering for ECS/DOTS development workflows

### Changed
- **EditMode Log Monitor Enhancements**
  - Updated `--errors` flag description to include BC/DC/ECS errors
  - Added error type prefixes when viewing compilation errors
  - Improved error counting with categorization by type
  - Extended compilation patterns to cover DOTS ecosystem

## [1.3.1] - 2025-01-13

### Added
- **Python Script Synchronization System**
  - New `sync_python_scripts.py` tool for syncing scripts from package to PerSpec directory
  - Automatic copying of all Python scripts from package to working directory
  - Support for multiple source directories within the package
  - Detailed sync report showing copied files and any failures
  - File size information in sync output for verification

### Fixed
- **PlayMode Error Filtering**
  - Changed `--errors` flag to show ALL errors and exceptions (not just compilation errors)
  - Added new `--cs-errors` flag specifically for compilation errors (CS errors)
  - Aligned error filtering behavior between PlayMode and EditMode log viewers
  - Maintained backward compatibility with `--all-errors` flag (now same as `--errors`)

### Improved
- **Python Script Management**
  - Scripts now properly maintained in package and synced to PerSpec directory
  - Clear separation between package source and working directory
  - Better workflow for package updates and git operations
  - Documentation updated with sync instructions

## [1.3.0] - 2025-01-13

### Added
- **Scene Hierarchy Export System**
  - New functionality to export Unity scene hierarchy to JSON format
  - Full hierarchy export with all GameObjects and components
  - Single GameObject export with detailed component properties
  - Component serialization with actual values (no GUIDs)
  - Transform data export (position, rotation, scale)
  - Support for inactive GameObjects (configurable)
  - Automatic output directory cleanup before each export

- **Database Support**
  - Added `scene_hierarchy_requests` table for request tracking
  - SceneHierarchyRequest model in SQLiteManager
  - Polling-based coordination between Python and Unity

- **Python CLI Tool** (`scene_hierarchy.py`)
  - Export full scene hierarchy or single GameObject
  - Wait for completion with timeout support
  - List and manage export files
  - Pretty-print JSON output
  - Clean up old export files

- **Unity Components**
  - SceneHierarchyExporter: Core JSON serialization logic
  - SceneHierarchyCoordinator: Database polling and request execution
  - SerializedObject-based property extraction for accurate values

### Technical Details
- Output directory: `PerSpec/SceneHierarchy/`
- File format: `hierarchy_YYYYMMDD_HHMMSS.json`
- Uses Newtonsoft.Json for robust JSON serialization
- Thread-safe file operations with proper error handling

## [1.2.1] - 2025-09-13

### Fixed
- **Test Completion Detection Reliability**
  - Fixed issue where test status remained "processing" even after tests completed
  - Added fallback detection when RunStarted callback doesn't fire
  - File monitoring now processes XML files even without RunStarted callback
  - Added delayed fallback to assume tests started after Execute() call
  - Improved robustness for both EditMode and PlayMode tests

### Improved
- **Debug Logging**
  - Added more detailed logging for test execution flow
  - Better visibility into callback firing and file monitoring states
  - Clear indication when fallback mechanisms are triggered
  - Database status update logging for troubleshooting

### Added
- **SQLiteManager Enhancement**
  - Added GetRequestStatus() method to check current status before updates
  - Prevents unnecessary status transitions

## [1.2.0] - 2025-09-12

### Added
- **Test Execution State Management**
  - New granular test execution states: `processing`, `executing`, `finalizing`, `timeout`
  - Real-time progress tracking with percentage updates during test execution
  - Per-test completion monitoring with count tracking
  - Progress logging to database for visibility

- **Test Results Viewer** (`test_results.py`)
  - View latest test results with detailed summary
  - List and analyze multiple test run files
  - Filter to show only failed tests across sessions
  - Statistics aggregation from recent test runs
  - Clean up old result files with configurable retention
  - JSON output support for automation

- **Database Auto-Maintenance System**
  - Automatic schema updates on package installation/update
  - Migration system with version tracking
  - Weekly automatic maintenance checks
  - Manual maintenance via Unity menu: `Tools > PerSpec > Database > Run Maintenance Now`
  - Old data cleanup (>7 days for tests, >1 hour for logs)
  - Database optimization with VACUUM
  - Performance indexes for faster queries

- **Long-Running Test Support**
  - Example tests demonstrating proper long-duration handling (30s, 10s, quick)
  - Progress reporting during extended test execution
  - Proper timeout handling for individual vs batch tests

### Fixed
- **Premature Test Completion Detection** (Critical Fix)
  - Tests no longer incorrectly show as "failed" while still running
  - XML file validation ensures complete results before processing
  - File stability checking (3-second wait for size stabilization)
  - Test count validation against expected number
  - Proper handling of PlayMode vs EditMode completion detection

- **Database Schema Constraints**
  - Updated constraints to support new test execution states
  - Added migration script (`db_update_status_constraint.py`) for existing projects
  - Fixed "CHECK constraint failed" errors on status updates
  - Backward compatibility with legacy "running" status

### Changed
- **Test Coordination Workflow**
  - `--wait` flag now waits for full test execution completion (breaking change)
  - Added `--wait-processing` flag for legacy behavior (backward compatibility)
  - Status transitions now properly reflect test execution phases
  - Improved status reporting with clear execution state messages
  - Better distinction between request processing and test execution

- **File Monitoring Logic**
  - Enhanced detection of complete XML files with schema validation
  - Validation of test results before marking as complete
  - Proper handling of PlayMode vs EditMode test completion
  - More robust error handling with retry logic
  - File size stability checking before processing

### Improved
- **Test Status Accuracy**
  - Clear differentiation between "processing" and "executing" states
  - Accurate duration reporting for all test types
  - Better handling of timeout scenarios
  - Improved error messages for failed tests

- **Database Performance**
  - Added indexes on frequently queried columns
  - Automatic cleanup of old data
  - VACUUM optimization after migrations
  - Reduced database size through regular maintenance

### Documentation
- Updated CLAUDE.md with database maintenance instructions
- Added Test Results Viewer usage examples
- Documented new status states and their meanings
- Added troubleshooting guide for schema updates

## [1.1.7] - 2025-09-09

### Added
- `--errors` flag for both EditMode and PlayMode log scripts
- Direct error filtering without piping to grep
- Error filtering searches across ALL session batches/files
- Stack trace support with `-s` flag for error output
- Error statistics showing counts by type (Error, Exception, Assert)
- Better error messages when log directories don't exist

### Changed
- monitor_editmode_logs.py now searches all sessions (up to 3) for errors
- test_playmode_logs.py filters errors across all batch files in session
- Consistent command-line interface between EditMode and PlayMode logs
- Updated documentation with new error filtering examples

### Improved
- Error filtering performance with direct flag instead of grep
- User experience with clearer error messages and statistics
- Command consistency across all log monitoring scripts

## [1.1.6] - 2025-09-08

### Added
- New file-based logging system for EditMode and PlayMode
- EditModeLogCapture.cs for session-based logging (keeps 3 sessions)
- CompilationErrorCapture.cs for reliable compilation error capture
- monitor_editmode_logs.py for viewing EditMode logs
- test_playmode_logs.py for viewing PlayMode logs
- Immediate log writes without buffering
- Automatic session cleanup to prevent disk bloat

### Changed
- Complete rewrite of log capture system from database to file-based
- EditMode logs now stored in PerSpec/EditModeLogs/ as session files
- PlayMode logs remain in PerSpec/PlayModeLogs/ with 5-second batches
- Compilation errors now reliably captured even during Unity failures
- Removed database dependency for all logging operations

### Removed
- ConsoleLogCapture.cs (database-based capture)
- RobustLogHandler.cs (problematic ILogHandler interception)
- UnityConsoleSessionManager.cs (database session management)
- EnhancedConsoleWindow.cs (database-based viewer)
- TestLogGenerator.cs (obsolete test generator)
- monitor_logs.py (database log queries)
- quick_logs.py (database log commands)
- console_log_reader.py (database reader)
- add_console_logs_table.py (database migration)

### Performance
- Eliminated ILogHandler overhead
- Removed EditorPrefs persistence overhead
- No database queries for log retrieval
- Immediate file writes (no buffering delays)
- Significantly improved reliability during compilation errors

## [1.1.5] - 2025-09-08

### Added
- Database performance optimization script (optimize_database.py)
- Composite indexes on console_logs table for faster queries
- Connection pooling for SQLite operations
- Timestamp conversion caching in monitor_logs.py
- Performance benchmark script (benchmark_performance.py)
- Exponential backoff for test polling operations
- Auto-VACUUM after database cleanup operations
- String caching in PerSpecDebug to reduce allocations

### Changed
- Increased default polling interval from 0.5s to 2.0s for reduced CPU usage
- Optimized string operations in PerSpecDebug using pre-cached constants
- Database queries now use optimized composite indexes
- Improved cleanup operations with automatic VACUUM and ANALYZE

### Performance
- Query times reduced from 10-50ms to <0.05ms (10-50x improvement)
- Batch insert performance: 0.6ms for 100 rows
- CPU usage reduced by ~70% through reduced polling frequency
- Database size optimization from 5.71MB to 0.61MB after cleanup
- Memory allocations reduced through string caching

## [1.1.4] - 2025-09-07

### Added
- Automatic package update detection with script and LLM config refresh
- Auto-refresh of Python coordination scripts on package version change
- Auto-update of LLM configurations (CLAUDE.md, .cursorrules, etc.) on package update
- Preservation of user permission settings during automatic updates

### Changed
- Improved PlayMode log capture reliability with faster processing
- Reduced log batching delay from 30 frames to 5 frames for better real-time capture
- Increased log queue capacity from 1,000 to 10,000 entries
- Reduced processing interval from 0.5s to 0.1s for faster log persistence
- Removed colorama dependency from monitor_logs.py for simpler LLM-friendly output
- monitor_logs.py now outputs plain text without color formatting

### Fixed
- PlayMode logs being missed during high-volume logging scenarios
- Log queue overflow causing silent data loss
- Thread safety issues with frame count access in PlayMode
- EnhancedConsole refresh timing using EditorApplication.timeSinceStartup

## [1.1.2] - Sep 1, 2025

### Added
- Enhanced CLAUDE.md documentation with MCP-like natural language command mappings
- Test results automatic export to PerSpec/TestResults/ directory
- Console log export functionality to PerSpec/Logs/ with auto-cleanup
- Improved command execution permissions documentation
- Simplified script access with fixed paths in PerSpec/Coordination/Scripts/

### Changed
- Updated natural language command recognition for better user experience
- Improved test result file management with timestamp-based naming
- Enhanced error message filtering and logging patterns
- Streamlined TDD workflow documentation with clearer step-by-step instructions

### Fixed
- Test result persistence across Unity restarts
- Console log export path consistency
- Background polling reliability improvements
- SQLite coordination edge cases during Unity focus loss

## [1.0.0] - Aug 28, 2025

### Added
- Initial release of PerSpec Testing Framework
- UniTask-based async test support with zero allocations
- DOTS/ECS testing base classes and helpers
- SQLite-based test coordination between Python and Unity
- Intelligent console log capture with stack trace truncation
- Background test polling using System.Threading.Timer
- Comprehensive 4-step workflow (Write → Refresh → Check → Test)
- Python CLI tools for test execution and monitoring
- Editor menu integration under Tools > PerSpec
- Support for EditMode and PlayMode tests
- Asset refresh coordination system
- Real-time test status monitoring
- Console log filtering and export capabilities

### Features
- **UniTaskTestBase** - Base class for async Unity testing
- **DOTSTestBase** - Base class for ECS/DOTS testing
- **Test Coordinator** - Main Unity editor window for test management
- **Console Log Capture** - Real-time Unity console monitoring
- **Background Polling** - Tests execute even when Unity loses focus
- **Python CLI Tools**:
  - `quick_test.py` - Execute tests with various filters
  - `quick_logs.py` - Monitor and retrieve console logs
  - `quick_refresh.py` - Force Unity asset refresh
  - `db_initializer.py` - Initialize SQLite database

### Technical Details
- Unity 2021.3+ support
- UniTask 2.3.3+ integration
- Unity Test Framework 1.3.0+ compatibility
- Thread-safe SQLite operations
- Intelligent stack trace truncation for LLM optimization

## [Unreleased]

### Planned
- GitHub Actions integration
- Cloud test result storage
- Performance profiling tools
- Visual test result dashboard
- Test coverage reporting