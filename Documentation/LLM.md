<!-- PERSPEC_CONFIG_START -->

> **Purpose**: TDD guidance for Claude Code in Unity projects using PerSpec framework.

## 📋 Quick Navigation
- [Script Locations](#script-locations) 🔍
- [Natural Language Commands](#natural-language-commands) 🗣️
- [TDD Workflow](#tdd-workflow) ⭐
- [Critical Patterns](#critical-patterns) 🚨
- [Test Requirements](#test-requirements) 🧪
- [Important Rules](#important-rules) ⚠️

## 🔍 Script Locations

```bash
# SINGLE SOURCE OF TRUTH
Packages/com.digitraver.perspec/Editor/Coordination/Scripts/  # ALL PYTHON SCRIPTS HERE ONLY
PerSpec/Coordination/Scripts/       # Working copies (auto-synced, DO NOT EDIT)
PerSpec/package_location.txt        # Package path reference
```

### 🔄 Python Script Synchronization - CRITICAL!

**THE SYNC COMMAND YOU MUST USE:**
```bash
# ALWAYS run this to sync Python scripts from package to PerSpec
python Packages/com.digitraver.perspec/ScriptingTools/sync_python_scripts.py

# This copies ALL 22 scripts from:
#   Packages/com.digitraver.perspec/Editor/Coordination/Scripts/  (ONLY SOURCE - NO OTHER DIRS!)
# To:
#   PerSpec/Coordination/Scripts/  (working directory - DO NOT EDIT HERE)
```

**When to sync:**
- **After package updates** - MANDATORY
- **After pulling from git** - MANDATORY
- **When setting up project** - FIRST THING YOU DO
- **If scripts are missing** - RUN SYNC
- **If --errors doesn't work** - RUN SYNC FIRST

## 🗣️ Natural Language Commands

| User Says           | Execute                                                                                     |
| ------------------- | ------------------------------------------------------------------------------------------- |
| "sync python"       | `python Packages/com.digitraver.perspec/ScriptingTools/sync_python_scripts.py`              |
| "copy python scripts"| `python Packages/com.digitraver.perspec/ScriptingTools/sync_python_scripts.py`              |
| "LLM setup"         | `python Packages/com.digitraver.perspec/ScriptingTools/sync_python_scripts.py`              |
| "show/get errors"   | `python PerSpec/Coordination/Scripts/monitor_editmode_logs.py --errors`                     |
| "run tests"         | `python PerSpec/Coordination/Scripts/quick_test.py all -p edit --wait`                      |
| "refresh Unity"     | `python PerSpec/Coordination/Scripts/quick_refresh.py full --wait` (blocks until compile + domain reload finish) |
| "show logs"         | `python PerSpec/Coordination/Scripts/monitor_editmode_logs.py recent -n 50`                |
| "export logs"       | `python PerSpec/Coordination/Scripts/monitor_editmode_logs.py sessions`                     |
| "monitor logs live" | `python PerSpec/Coordination/Scripts/monitor_editmode_logs.py live`                         |
| "test results"      | `python PerSpec/Coordination/Scripts/test_results.py latest`                                |
| "did MY run pass"   | `python PerSpec/Coordination/Scripts/test_results.py latest --for-request <id>`             |
| "show test results" | `python PerSpec/Coordination/Scripts/test_results.py latest -v`                             |
| "failed tests"      | `python PerSpec/Coordination/Scripts/test_results.py failed`                                |
| "open console"      | `python PerSpec/Coordination/Scripts/quick_menu.py execute "Window/General/Console" --wait` |
| "save project"      | `python PerSpec/Coordination/Scripts/quick_menu.py execute "File/Save Project" --wait`      |
| "clear logs"        | `python PerSpec/Coordination/Scripts/quick_clean.py quick`                                  |
| "clean database"    | `python PerSpec/Coordination/Scripts/quick_clean.py all --keep 0.5`                         |
| "show playmode logs"| `python PerSpec/Coordination/Scripts/test_playmode_logs.py`                                 |
| "show playmode errors"| `python PerSpec/Coordination/Scripts/test_playmode_logs.py --errors`                       |
| "show cs errors"    | `python PerSpec/Coordination/Scripts/test_playmode_logs.py --cs-errors`                     |
| "search playmode logs"| `python PerSpec/Coordination/Scripts/test_playmode_logs.py --search <keyword>`             |
| "find in logs"      | `python PerSpec/Coordination/Scripts/test_playmode_logs.py -S <keyword> -i`                 |
| "export scene"      | `python PerSpec/Coordination/Scripts/scene_hierarchy.py export full --focus --wait`                 |
| "export hierarchy"  | `python PerSpec/Coordination/Scripts/scene_hierarchy.py export full --focus --wait --show`          |
| "export gameobject" | `python PerSpec/Coordination/Scripts/scene_hierarchy.py export object <path> --focus --wait`        |
| "show scene json"   | `python PerSpec/Coordination/Scripts/scene_hierarchy.py latest --show`                      |
| "list scene exports"| `python PerSpec/Coordination/Scripts/scene_hierarchy.py list`                               |
| "run scenario"      | `python PerSpec/Coordination/Scripts/unityhelper_coordinator.py execute --file <path> --focus --wait` |
| "scenario status"   | `python PerSpec/Coordination/Scripts/unityhelper_coordinator.py status <id>`                  |
| "list scenarios"    | `python PerSpec/Coordination/Scripts/unityhelper_coordinator.py list`                         |
| "open scenario runner"| Unity Menu → `Tools → PerSpec → Scenario Runner`                                          |
| "enable DOTS"       | Open Control Center > Debug Settings > Click "Enable DOTS Support"                          |
| "check DOTS status" | Open Control Center > Dashboard > Check "DOTS Support" row                                  |
| "write DOTS test"   | First check if DOTS is enabled, then use DOTSTestBase with #if PERSPEC_DOTS_ENABLED         |

**Intent Mapping:**
- "Something wrong" → Check errors
- "Tests failing" → Run with verbose: `quick_test.py all -v --wait`
- "Unity not responding" → Refresh Unity
- **Timeout?** → Tell user to click Unity window for focus
- **DOTS world null?** → Ensure using DOTSTestBase AND DOTS is enabled
- **Database too large?** → Run: `quick_clean.py quick`
- **PlayMode logs?** → Check `PerSpec/PlayModeLogs/` directory
- **Writing DOTS code?** → FIRST check if `PERSPEC_DOTS_ENABLED` is set
- **DOTSTestBase not found?** → Enable DOTS support in Control Center
- **Build/automate scene?** → Write scenario JSON + use Scenario Runner or `unityhelper_coordinator.py`
- **Scene setup from CLI?** → `unityhelper_coordinator.py execute --file <path> --focus --wait`

## 📊 Log Monitoring

### EditMode Logs
```bash
# View recent logs from current session
python PerSpec/Coordination/Scripts/monitor_editmode_logs.py recent -n 50

# Show only compilation errors (CS errors) from all sessions
python PerSpec/Coordination/Scripts/monitor_editmode_logs.py --errors

# Show ALL errors and exceptions from all sessions
python PerSpec/Coordination/Scripts/monitor_editmode_logs.py --all-errors

# Show compilation errors with stack traces
python PerSpec/Coordination/Scripts/monitor_editmode_logs.py --errors -s

# Monitor logs in real-time
python PerSpec/Coordination/Scripts/monitor_editmode_logs.py live

# List all sessions (keeps 3 most recent)
python PerSpec/Coordination/Scripts/monitor_editmode_logs.py sessions

# Bypass line limit for grep/filtering (useful for searching)
python PerSpec/Coordination/Scripts/monitor_editmode_logs.py --no-limit | grep "PATTERN"
```

### PlayMode Logs
```bash
# View PlayMode logs (last 50 by default)
python PerSpec/Coordination/Scripts/test_playmode_logs.py

# Show ALL errors and exceptions (runtime + compilation)
python PerSpec/Coordination/Scripts/test_playmode_logs.py --errors

# Show ONLY compilation errors (CS errors)
python PerSpec/Coordination/Scripts/test_playmode_logs.py --cs-errors

# List available sessions
python PerSpec/Coordination/Scripts/test_playmode_logs.py -l

# View errors with stack traces
python PerSpec/Coordination/Scripts/test_playmode_logs.py --errors -s

# View compilation errors with stack traces
python PerSpec/Coordination/Scripts/test_playmode_logs.py --cs-errors -s

# Show all logs without limit
python PerSpec/Coordination/Scripts/test_playmode_logs.py -a

# Bypass default 50 line limit for grep/filtering
python PerSpec/Coordination/Scripts/test_playmode_logs.py --no-limit | grep "PATTERN"

# Show most recent session only
python PerSpec/Coordination/Scripts/test_playmode_logs.py --tail

# Search for keywords in logs
python PerSpec/Coordination/Scripts/test_playmode_logs.py --search "error"
python PerSpec/Coordination/Scripts/test_playmode_logs.py -S "timeout" "failed"  # ALL keywords must match
python PerSpec/Coordination/Scripts/test_playmode_logs.py -S "player" "health" --any  # ANY keyword matches
python PerSpec/Coordination/Scripts/test_playmode_logs.py --search "exception" -i  # Case-insensitive search
python PerSpec/Coordination/Scripts/test_playmode_logs.py -S "null" --errors  # Search within errors only
```

**Important Changes:**
- `--errors` now shows ALL errors (runtime + compilation) - more useful for PlayMode debugging
- `--cs-errors` for compilation errors only (what --errors used to do)
- `--all-errors` is deprecated (same as --errors)

**Note:** Logs are stored as files in `PerSpec/PlayModeLogs/`. PlayMode logs clear on entry, captured every 5 seconds during play.

## 📈 Test Results Viewer

### View Test Results
```bash
# Show latest test results with summary (names the classes it contains)
python PerSpec/Coordination/Scripts/test_results.py latest

# Confirm a SPECIFIC request's results - refuses to show another run's, and exits
# 5 when the request never finished, 3 when nothing on disk matches its filter
python PerSpec/Coordination/Scripts/test_results.py latest --for-request 400

# Only consider results written recently
python PerSpec/Coordination/Scripts/test_results.py latest --newer-than 15m

# Show latest with detailed output (all tests)
python PerSpec/Coordination/Scripts/test_results.py latest -v

# Show latest as JSON
python PerSpec/Coordination/Scripts/test_results.py latest --json

# List available test result files
python PerSpec/Coordination/Scripts/test_results.py list -n 20

# Show specific test result file
python PerSpec/Coordination/Scripts/test_results.py show TestResults_20250912_141430.xml -v

# Show only failed tests from recent runs
python PerSpec/Coordination/Scripts/test_results.py failed -n 5 -v

# Show statistics from recent test runs
python PerSpec/Coordination/Scripts/test_results.py stats -n 10

# Clean old test result files (keep 10 most recent)
python PerSpec/Coordination/Scripts/test_results.py clean --keep 10 --confirm
```

**Note:** Test results are stored as XML files in `PerSpec/TestResults/`. The viewer parses these files to show test outcomes, durations, and failure details.

## 🗑️ Database Maintenance

### Manual Cleanup Commands
```bash
# Quick cleanup (clear all logs + compact database)
python PerSpec/Coordination/Scripts/quick_clean.py quick

# Clear only console logs
python PerSpec/Coordination/Scripts/quick_clean.py logs --keep 0

# Clean all old data (keep last 30 minutes)
python PerSpec/Coordination/Scripts/quick_clean.py all --keep 0.5

# Show database statistics
python PerSpec/Coordination/Scripts/quick_clean.py stats
```

### Automatic Settings
- Console logs retention: **30 minutes**
- Cleanup frequency: **Every 15 minutes**
- Database size trigger: **50 MB**

### After Package Updates
```bash
# Run migration to add new tables
python PerSpec/Coordination/Scripts/db_migrate.py

# Update status constraint for new test states (if needed)
python PerSpec/Coordination/Scripts/db_update_status_constraint.py
```

**When to run db_update_status_constraint.py:**
- After updating from older PerSpec versions
- If you see errors like: `CHECK constraint failed: status IN ('pending', 'running', 'completed', 'failed', 'cancelled')`
- When tests show wrong status or complete prematurely

## 🚀 TDD Workflow

### 🛑 CRITICAL: NEVER SKIP THESE STEPS!
**⚠️ WARNING**: Skipping ANY step will cause test failures and waste time!

### 📌 MANDATORY 4-Step Process (ALWAYS FOLLOW IN ORDER!)
```bash
# 1. Write tests & code
# 🔴 IMPORTANT: Steps 2 & 3 MUST also be run IMMEDIATELY after writing ANY code!
# Don't wait until test time - verify compilation RIGHT AFTER coding!

# 2. ⚡ ALWAYS REFRESH UNITY FIRST! (DO NOT SKIP!)
python PerSpec/Coordination/Scripts/quick_refresh.py full --wait
# ❌ NEVER run tests without refreshing - Unity won't see your changes!
# 📌 Run this IMMEDIATELY after writing/editing ANY C# code!
# ✅ As of v1.7.0, --wait genuinely BLOCKS until asset import + script
#    compilation + domain reload all finish. When it returns 'completed',
#    Unity is running your new code. Full recompiles take a while, so this
#    can take minutes - do NOT lower --timeout below the 300s default.

# 3. 🚨 MANDATORY: Check compilation errors (NEVER SKIP THIS!)
python PerSpec/Coordination/Scripts/monitor_editmode_logs.py --errors
# ⛔ STOP HERE if ANY errors exist!
# ❌ DO NOT PROCEED to step 4 if compilation errors exist!
# Tests will be INCONCLUSIVE if code doesn't compile
# 📌 Run this IMMEDIATELY after EVERY refresh to verify code compiles!

# 4. Run tests ONLY after steps 2 & 3 succeed
python PerSpec/Coordination/Scripts/quick_test.py all -p edit --wait
```

### ❌ COMMON MISTAKES TO AVOID:
- **Running tests without refreshing Unity** → Tests run old code
- **Skipping error check** → Tests show INCONCLUSIVE
- **Running tests with compilation errors** → Wastes time, tests can't run
- **Lowering `--timeout` on refresh** → `--wait` now blocks through the full compile + domain reload (v1.7.0); a short timeout returns before Unity finishes. Keep it at 300s+.

### 📋 Step-by-Step Checklist (USE THIS EVERY TIME!):
☐ Code written/modified
☐ Unity refreshed (`quick_refresh.py full --wait`)
☐ Compilation checked (`monitor_editmode_logs.py --errors`)
☐ No errors found (or all fixed)
☐ Tests executed (`quick_test.py`)

### ⚠️ Understanding Test Status
**As of PerSpec v1.6.0**, the `--wait` flag waits for **true completion**:
- ✅ The DB row has reached a terminal status (`completed`, `failed`, `cancelled`, `timeout`, `inconclusive`, `no_match`)
- ✅ A matching `TestResults_*.xml` is present in `PerSpec/TestResults/`
  (imported from Unity's AppData fallback if the in-process exporter didn't write one)

Terminal statuses and what they mean:
- **`completed`** — Unity ran the tests and produced results
- **`inconclusive`** — Tests ran but produced no usable results (e.g., a method-level run where every test was skipped). Also used when compilation errors prevent execution. It does NOT mean the name was wrong — that is `no_match`.
- **`no_match`** — The filter matched **zero tests**. Nothing ran. This is a caller mistake, not a condition of the project, and it is detected before PlayMode is entered. Retrying will not help: read the `error_message`, which names the filter and usually suggests the fully qualified name you meant. `quick_test.py` exits **6**.
- **`failed`** — Dispatch threw, or orphan recovery flagged the row after a domain reload
- **`timeout`** — `HandleTestTimeout` fired (>5 min batch / >10 min single), or the
  stuck-run watchdog finalized a run that never reported (>10 min batch / >15 min
  single). PlayMode runs have no in-process timeout, so the watchdog is the only thing
  that can end a run wedged inside play mode.
- **`cancelled`** — User cancelled via CLI

If `quick_test.py --wait` exits 0 and
`test_results.py latest --for-request <id> -v` shows the run, the tests are truly
finished.

> **Do not confirm a run with a bare `test_results.py latest`.** It shows the newest
> XML on disk, which after a timed-out run is the PREVIOUS run's - a silent false
> green. `--for-request <id>` proves the results belong to the run you asked about and
> exits non-zero when they cannot be attributed.

### ⚠️ Understanding Refresh Status
**As of PerSpec v1.7.0**, `quick_refresh.py --wait` is compile-aware. It blocks through asset import AND any resulting script compilation + domain reload, so `completed` means Unity is running your new code (not just that the import started). The request moves through these statuses:
- **`pending`** — queued; Unity has not picked it up yet
- **`running`** — Unity received the request and is importing assets
- **`compiling`** — scripts are recompiling; a domain reload is pending
- **`completed`** — import + compilation + domain reload all finished

Special case: if compilation finished **with errors**, no domain reload happens, so the request still ends as **`completed`** but its summary carries an `error_message` and prints a `[WARNING] Compilation errors detected` line. That is not a refresh failure — step 3 (`monitor_editmode_logs.py --errors`) still owns error triage and MUST be run. `failed` is reserved for the refresh machinery itself breaking (or a request orphaned by a domain reload / editor restart).

**🚨 CRITICAL PROTOCOL**: 
1. **ALWAYS** refresh Unity after ANY code change
2. **ALWAYS** check for compilation errors BEFORE running tests
3. **NEVER** skip the error check - even for "simple" changes
4. If compilation errors exist:
   - Tests CANNOT run and will be marked INCONCLUSIVE
   - You MUST fix ALL compilation errors before running tests
   - Check errors with: `python PerSpec/Coordination/Scripts/monitor_editmode_logs.py --errors`
   - After fixing errors, go back to step 2 (refresh Unity)

### 🎯 Test Execution
```bash
# Run ALL tests
quick_test.py all -p edit --wait

# Run by CLASS (use FULL namespace)
quick_test.py class Tests.PlayMode.SimplePerSpecTest -p play --wait

# Run specific METHOD
quick_test.py method Tests.PlayMode.SimplePerSpecTest.Should_Pass -p play --wait
```

## 🔍 Post-Code-Writing Verification (MANDATORY FOR LLMs)

### 🚨 CRITICAL REQUIREMENT FOR ALL LLMs
**After writing or modifying ANY C# code, you MUST IMMEDIATELY verify it compiles:**

```bash
# MANDATORY AFTER ANY CODE CHANGE - DO NOT SKIP!
# 1. Refresh Unity to pick up changes
python PerSpec/Coordination/Scripts/quick_refresh.py full --wait

# 2. Check for compilation errors
python PerSpec/Coordination/Scripts/monitor_editmode_logs.py --errors
```

### ⚠️ THIS APPLIES TO:
- ✅ After creating new C# files
- ✅ After editing existing C# code
- ✅ After refactoring or renaming
- ✅ After adding/removing using statements
- ✅ After ANY code modification whatsoever

### 📌 WHY THIS IS MANDATORY:
- **Immediate feedback**: Catch errors before user discovers them
- **Professional quality**: Ensure all delivered code compiles
- **Time saving**: Fix issues immediately while context is fresh
- **User trust**: Demonstrate thoroughness and professionalism

### ❌ NEVER:
- Skip verification "because it's a simple change"
- Assume code compiles without checking
- Wait for user to ask about errors
- Leave compilation errors unfixed

### ✅ ALWAYS:
1. Write/modify code
2. **IMMEDIATELY** run refresh
3. **IMMEDIATELY** check for errors
4. If errors exist, fix them NOW
5. Re-verify after fixes

**Remember**: The user expects working code. It's YOUR responsibility as an LLM to ensure code compiles BEFORE considering any task complete.

## 🤖 Agent Usage

### Standing authorization

The user has already authorized delegation. Do **not** ask permission before
spawning a subagent. Every trigger in the list below counts as a request the
user has already made. Delegation is the default for bulk work, not an
escalation.

Why this matters: every tool call re-reads the entire context, so cost tracks
context size multiplied by call count. A subagent reads on its own budget and
hands back a summary, so the main thread stops growing.

### Delegate by default (do not ask first)

- Web research of any kind
- Any search that will touch more than three files
- Any question about scene or prefab contents - export the hierarchy, do not read YAML
- Log triage and error filtering
- Running the test loop and reporting results
- Generating or updating documentation

### Handle inline

- A single targeted edit to a file already in context
- Answering from information already in the conversation
- Anything the user is watching step by step

### Model tiers

Pick the tier from the shape of the work, not from the importance of the outcome.

| Tier | Use for | Relative cache-read cost |
|------|---------|--------------------------|
| `haiku` | Running scripts, polling, filtering logs, mechanical text transforms. No judgment. | 1x |
| `sonnet` | High-volume reading with a small answer. Search, inspection, doc generation, routine test authoring. | 2x |
| `opus` | Design judgment, concurrency reasoning, root-causing a bug that has already resisted one attempt. | 5x |

**Rule of thumb:** if a task's output is much smaller than its input, it belongs
in a subagent, and usually on `haiku` or `sonnet`.

Do not lower a tier on work where a wrong answer is expensive to detect. A cheap
subagent that quietly returns a wrong result costs more than it saves.

### A subagent returns a summary, not a transcript

The whole point is keeping bulk out of the main thread. A subagent that pastes
back every file it read, or the full log it just filtered, has saved nothing and
cost more than doing the work inline. Return findings, counts, file paths, and
line numbers.

## 🌳 Scene Hierarchy Export

### Analyze with the export, never by reading YAML

**The export is the supported way to inspect scene and prefab contents.** Do not
open `.unity`, `.prefab`, or `.asset` files and read the raw YAML. Export the
hierarchy and read the JSON.

Why the export wins:
- It resolves GUIDs and fileIDs for you. The JSON carries real component type
  names and real serialized values, so there is nothing left to look up by hand.
- It has already applied prefab overrides and `m_RemovedComponents`, so what you
  read is what the scene actually contains, not what the prefab claims.
- Raw scene YAML runs to hundreds of thousands of lines. Reading it burns context
  for an answer the export hands over in one field.

How to do it:
1. Export the narrowest scope that answers the question. Use
   `export object <path>` over `export full` whenever you know the branch.
2. Find the answer in the JSON with `grep -n`, then read a small window around the
   hit with `sed -n`. Do not `cat` the export.
3. Leave `--show` off unless the user asked to see the JSON. It prints everything.

Delegate this to **unity-scene-inspector** (`sonnet`). Megabytes of JSON in, one
line out, and none of it lands in the main thread.

If the export genuinely cannot answer the question - a broken reference with no
resolvable target, or an asset Unity refuses to load - say so and ask before
falling back to raw YAML.

### Export Unity Scene to JSON
```bash
# Export full scene hierarchy
python PerSpec/Coordination/Scripts/scene_hierarchy.py export full --focus --wait

# Export specific GameObject and its children
python PerSpec/Coordination/Scripts/scene_hierarchy.py export object "Player" --focus --wait

# Export and display JSON
python PerSpec/Coordination/Scripts/scene_hierarchy.py export full --focus --wait --show

# View latest export
python PerSpec/Coordination/Scripts/scene_hierarchy.py latest --show

# List export files
python PerSpec/Coordination/Scripts/scene_hierarchy.py list
```

### JSON Output Features
- **Full component serialization** - No GUIDs, actual script names and values
- **Transform data** - Position, rotation, scale arrays
- **Component properties** - Serialized using Unity's SerializedObject
- **Recursive hierarchy** - Complete GameObject tree with children
- **Inactive GameObjects** - Optional inclusion (use `--no-inactive` to exclude)

### Setup Required (One-Time)
```bash
# Add scene hierarchy table to database
python PerSpec/Coordination/Scripts/add_scene_hierarchy_table.py
```

**Output Location:** `PerSpec/SceneHierarchy/hierarchy_[timestamp].json`

## 📖 Documentation Access

**Get package path first:**
```bash
cat PerSpec/package_location.txt  # Returns: Packages/com.digitraver.perspec
```

**Then read as needed:**
| Scenario | Read |
|----------|------|
| Writing Unity tests | `{package_path}/Documentation/unity-test-guide.md` |
| DOTS/ECS work | `{package_path}/Documentation/dots-test-guide.md` |
| Python issues | `{package_path}/Documentation/coordination-guide.md` |
| Using agents | `{package_path}/Documentation/agents/[agent-name].md` |
| Unity Helper overview | `{package_path}/Documentation/unity-helper.md` |
| Unity Helper tasks | `{package_path}/Documentation/unity-helper-tasks.md` |

## 🎯 Test Facade Pattern

### ✅ CORRECT Pattern
```csharp
// PRODUCTION CLASS
public class PlayerController : MonoBehaviour 
{
    private float health = 100f;
    
    public void TakeDamage(float amount) {
        if (!isInvulnerable)
            health -= amount;
    }
    
    #if UNITY_EDITOR
    // Test facades - ONLY in production code
    public void Test_SetHealth(float value) => health = value;
    public float Test_GetHealth() => health;
    #endif
}

// TEST CODE - No directives needed!
[UnityTest]
public IEnumerator Should_TakeDamage() => UniTask.ToCoroutine(async () => {
    var player = Object.Instantiate(prefab).GetComponent<PlayerController>();
    
    player.Test_SetHealth(100f);  // Direct call - no #if needed
    player.TakeDamage(30f);
    
    Assert.AreEqual(70f, player.Test_GetHealth());
});
```

### ❌ FORBIDDEN
- Compiler directives in test code
- Using reflection for private access
- Making private methods public
- Test parameters in production methods

## 🚫 Compilation Error Fix Guidelines

### ❌ NEVER Remove Functionality to "Fix" Errors
```csharp
// ❌ ABSOLUTELY FORBIDDEN - Removing functionality
public void ProcessData() {
    // TODO: Fix compilation issue
    // var tracker = PipelineDistortionTracker.Instance;
    // if (tracker != null) {
    //     tracker.Track(data);
    // }
}

// ❌ WRONG - Commenting out broken code
public void Initialize() {
    // systemManager.RegisterSystem(this);  // CS0103: The name 'systemManager' does not exist
}

// ✅ CORRECT - Fix the actual issue
public void ProcessData() {
    var tracker = PipelineDistortionTracker.Instance;  // Add proper using statement or reference
    if (tracker != null) {
        tracker.Track(data);
    }
}

// ✅ CORRECT - Resolve missing dependencies
private SystemManager systemManager;
public void Initialize() {
    systemManager = GetComponent<SystemManager>();
    systemManager.RegisterSystem(this);
}
```

### Proper Compilation Error Resolution
| Error Type | ❌ WRONG Approach | ✅ CORRECT Approach |
|------------|-------------------|---------------------|
| Missing type | Comment out code | Add using statement or assembly reference |
| Undefined variable | Remove functionality | Declare variable or inject dependency |
| Method not found | Delete method call | Implement method or find correct API |
| Interface not implemented | Remove interface | Implement all required methods |
| Ambiguous reference | Comment out usage | Use fully qualified name |

### Resolution Steps for Compilation Errors
1. **Identify the root cause** - Don't just hide the symptom
2. **Check for missing references**:
   - Assembly references in .asmdef
   - Using statements at top of file
   - NuGet packages or dependencies
3. **Research the proper API**:
   - Check Unity documentation
   - Look for similar usage in codebase
   - Verify version compatibility
4. **Implement proper solution**:
   - Add missing dependencies
   - Create required methods/properties
   - Use correct namespace
5. **NEVER**:
   - Leave TODO comments for compilation fixes
   - Comment out functional code
   - Remove features to make it compile

### Example: Fixing Missing Type Error
```csharp
// ❌ WRONG - Removing functionality
public class GameManager : MonoBehaviour {
    void Start() {
        // TODO: Fix PipelineDistortionTracker not found
        // InitializeTracker();
    }
}

// ✅ CORRECT - Add proper reference
using MyGame.Analytics;  // Add missing using

public class GameManager : MonoBehaviour {
    void Start() {
        InitializeTracker();
    }

    void InitializeTracker() {
        var tracker = PipelineDistortionTracker.Instance;
        tracker.Initialize();
    }
}
```

## ⚠️ Critical Patterns

### CS1626 - Yield in Try-Catch
```csharp
// ❌ WRONG
[UnityTest]
public IEnumerator BadTest() {
    try {
        yield return new WaitForSeconds(1); // CS1626!
    } catch { }
}

// ✅ CORRECT
[UnityTest]
public IEnumerator GoodTest() => UniTask.ToCoroutine(async () => {
    try {
        await UniTask.Delay(1000);
    } catch (Exception ex) {
        Debug.LogError($"[ERROR] {ex.Message}");
        throw;
    }
});
```

### Never async void
```csharp
// ❌ Crashes Unity
public async void BadMethod() { }

// ✅ Use UniTask
public async UniTask GoodMethod() { }
public async UniTaskVoid FireAndForget() { }
```

### Thread Safety
```csharp
public async UniTask UpdateSafely(GameObject obj) {
    await UniTask.SwitchToMainThread();  // Unity APIs need main thread
    obj.transform.position = Vector3.zero;
}
```

## 🏗️ SOLID Principles

### Single Responsibility
```csharp
// ✅ Each class does ONE thing
public class PlayerMovement : MonoBehaviour { }
public class PlayerCombat : MonoBehaviour { }
```

### Open/Closed
```csharp
// ✅ Extend via abstraction
public abstract class Weapon : ScriptableObject {
    public abstract UniTask<float> CalculateDamageAsync(Enemy target);
}
```

### Dependency Inversion
**🚨 NEVER use Singleton MonoBehaviours!**

Use instead:
- Static classes for utilities
- ScriptableObjects for configuration
- POCO for data

## 🔧 Component References

### ✅ FindVars Pattern (REQUIRED)
```csharp
public class ExampleComponent : MonoBehaviour {
    [SerializeField] private AudioSource audioSource;
    
    [ContextMenu("Find Vars")]
    public void FindVars() {
        audioSource = GetComponent<AudioSource>();
    }
}
```

### ❌ NEVER
- Get components at runtime
- Add components at runtime  
- Use reflection for components

## 🔷 DOTS/ECS Development

### 🚨 CRITICAL: Check DOTS Status BEFORE Writing Code

**DOTS support is OPTIONAL and gated behind a compiler directive. You MUST verify it's enabled before writing ANY DOTS code!**

### Check DOTS Status
```bash
# Check if DOTS is enabled (look for PERSPEC_DOTS_ENABLED in output)
python PerSpec/Coordination/Scripts/quick_refresh.py full --wait
# Then check Unity console or Control Center > Dashboard for "DOTS Support" status
```

### Enable DOTS Support
1. Open Control Center: **Tools > PerSpec > Control Center**
2. Go to **Debug Settings** tab
3. Click **"Enable DOTS Support"** button
4. Unity will recompile with `PERSPEC_DOTS_ENABLED` directive

### ⚠️ What Happens If DOTS Is Disabled
- All code in `PerSpec.Runtime.DOTS` and `PerSpec.Editor.DOTS` assemblies is **stripped**
- `DOTSTestBase`, `DOTSTestFactory`, `DOTSTestConfiguration` are **not available**
- Any test using `#if PERSPEC_DOTS_ENABLED` will be **excluded**
- Compilation errors if you try to use DOTS types without enabling

### ✅ Before Writing DOTS Code
1. **Ask user**: "Is DOTS support enabled in PerSpec Control Center?"
2. **Or check**: Look for `PERSPEC_DOTS_ENABLED` in Unity's Scripting Define Symbols
3. **If disabled**: Guide user to enable it before proceeding

### DOTS Code Requirements
- All DOTS test files MUST be wrapped with `#if PERSPEC_DOTS_ENABLED`
- Inherit from `DOTSTestBase` for ECS tests (auto-sets DefaultGameObjectInjectionWorld)
- Ensure Unity.Entities package is installed when DOTS is enabled

```csharp
#if PERSPEC_DOTS_ENABLED
using PerSpec.Editor.DOTS;

public class MyDOTSTest : DOTSTestBase
{
    // Your DOTS test code here
}
#endif
```

## 🧪 Test Requirements

### MANDATORY Base Classes
```csharp
using PerSpec.Runtime.Unity;
using PerSpec.Runtime.DOTS;  // For DOTS tests (requires PERSPEC_DOTS_ENABLED)

[TestFixture]
public class MyTest : UniTaskTestBase  // For Unity tests
{
}

[TestFixture] 
public class MyDOTSTest : DOTSTestBase  // For DOTS/ECS tests (auto-sets DefaultWorld)
{
}
```

### Required References
```json
{
    "references": [
        "PerSpec.Runtime",
        "PerSpec.Runtime.Debug",
        "UniTask",
        "UnityEngine.TestRunner"
    ]
}
```

### Prefab Pattern Default
Use for: MonoBehaviours, components, UI, gameplay
Skip for: Pure utilities, math, string helpers

### Test Pattern
```csharp
[UnityTest]
public IEnumerator TestName() => UniTask.ToCoroutine(async () => {
    try {
        // Arrange
        var prefab = Resources.Load<GameObject>("TestPrefabs/Player");
        var instance = Object.Instantiate(prefab);
        
        // Act
        await instance.GetComponent<Player>().DoActionAsync();
        
        // Assert
        Assert.IsTrue(condition);
    } finally {
        if (instance) Object.DestroyImmediate(instance);
    }
});
```

## 🚨 Important Rules

### ALWAYS
✅ Use UniTask (never Task/coroutines)
✅ Use FindVars for components
✅ Stay on main thread for Unity APIs
✅ Use test facades for private access
✅ Follow 4-step TDD workflow
✅ Fix compilation errors properly with real solutions
✅ Check if DOTS is enabled BEFORE writing any DOTS code
✅ Wrap DOTS test files with #if PERSPEC_DOTS_ENABLED

### NEVER
❌ async void → Use UniTask/UniTaskVoid
❌ Singleton MonoBehaviours
❌ Runtime GetComponent
❌ Reflection for private access
❌ Compiler directives in tests (except #if PERSPEC_DOTS_ENABLED for DOTS tests)
❌ Skip TDD steps
❌ Comment out code to "fix" compilation errors
❌ Remove functionality instead of fixing dependencies
❌ Generate .meta files → Unity creates these automatically
❌ Write DOTS code without first verifying PERSPEC_DOTS_ENABLED is set  

## 🎮 Unity Menu Execution

### Execute Unity Menu Items from Python
```bash
# List available menu items
python PerSpec/Coordination/Scripts/quick_menu.py list

# Execute a menu item
python PerSpec/Coordination/Scripts/quick_menu.py execute "Menu/Path" --wait

# Check status of request
python PerSpec/Coordination/Scripts/quick_menu.py status <request_id>

# Cancel pending request
python PerSpec/Coordination/Scripts/quick_menu.py cancel <request_id>
```

### Common Menu Items
| Menu Path | Purpose |
|-----------|---------|
| `Assets/Refresh` | Refresh asset database |
| `Assets/Create/C# Script` | Create new C# script |
| `Assets/Create/Folder` | Create new folder |
| `File/Save Project` | Save all project files |
| `Edit/Play` | Enter play mode |
| `Edit/Pause` | Pause play mode |
| `Edit/Stop` | Exit play mode |
| `Window/General/Console` | Open console window |
| `Window/General/Test Runner` | Open test runner |

**Notes:**
- Menu items that open dialogs will timeout after 30 seconds
- Use `--wait` flag to wait for completion
- Use `--timeout N` to set custom timeout in seconds
- Higher priority requests execute first

## 📊 Quick Reference

### 🔴 STOP! Before Running ANY Test:
1. Did you refresh Unity? → If no, run `quick_refresh.py full --wait`
2. Did you check for errors? → If no, run `monitor_editmode_logs.py --errors`
3. Are there compilation errors? → If yes, FIX THEM FIRST
4. Only NOW can you run tests → `quick_test.py all -p edit --wait`

### Compilation Error Handling
| Situation | Action | Command |
|-----------|--------|---------|
| **BEFORE ANY TEST** | **ALWAYS refresh Unity** | `quick_refresh.py full --wait` |
| **After EVERY refresh** | **ALWAYS check errors** | `monitor_editmode_logs.py --errors` |
| Errors found | FIX before testing | Do NOT run tests |
| Tests show "inconclusive" | Check compilation | `monitor_editmode_logs.py --errors` |
| Tests show "no_match" | The filter names nothing — fix the name | Read the `error_message`; it suggests the right one |
| Tests timeout | Check Unity focus + errors | Click Unity + check errors |

**Test Result States:**
- **PASSED**: Test executed and succeeded
- **FAILED**: Test executed but assertion failed  
- **INCONCLUSIVE**: Test couldn't run (compilation error/timeout)
- **SKIPPED**: Test intentionally not run

### Error Fixes
| Error | Solution |
|-------|----------|
| CS1626 | UniTask.ToCoroutine() |
| async void | UniTask/UniTaskVoid |
| Thread error | SwitchToMainThread() |
| Null components | FindVars pattern |
| DefaultGameObjectInjectionWorld null | Inherit from DOTSTestBase (and enable DOTS) |
| NativeArray disposed | Use try-finally with Dispose() |
| DOTSTestBase not found | Enable DOTS in Control Center > Debug Settings |
| PerSpec.Editor.DOTS missing | Enable PERSPEC_DOTS_ENABLED compiler directive |

### Project Structure
```
TestFramework/
├── Packages/com.digitraver.perspec/  # Package
├── Assets/Tests/                      # Your tests
├── PerSpec/                           # Working dir
│   ├── Coordination/Scripts/          # Python tools
│   ├── TestResults/                   # XML results
│   ├── PlayModeLogs/                  # PlayMode console logs (auto-cleared)
│   └── test_coordination.db           # SQLite
└── CustomScripts/Output/              # Generated
```

### PlayMode Log Capture
- **Location**: `PerSpec/PlayModeLogs/` - auto-cleared on Play Mode enter
- **Writing**: Every 5 seconds + final flush on exit
- **Performance**: No PlayerPrefs, no Update() - 95% faster
- **View logs**: `python PerSpec/Coordination/Scripts/test_playmode_logs.py`

### EditMode Log Capture
- **Location**: `PerSpec/EditModeLogs/` - session-based files
- **Sessions**: Keeps 3 most recent sessions, older auto-deleted
- **Writing**: Immediate on log receive (no buffering)
- **Compilation**: Errors captured even during compilation failures
- **View logs**: `python PerSpec/Coordination/Scripts/monitor_editmode_logs.py`
  - `recent -n 50` - Show last 50 logs
  - `--errors` - Show only compilation errors (CS errors)
  - `--all-errors` - Show all errors and exceptions
  - `live` - Monitor in real-time
  - `sessions` - List all sessions

### Available Agents

Defined in `{package_path}/Documentation/agents/` and copied to `.claude/agents/`.

| Agent | Tier | Use for |
|-------|------|---------|
| **unity-log-triage** | `haiku` | Run the log scripts, return distinct errors with counts. Not the log. |
| **unity-test-runner** | `haiku` | Own the refresh → check errors → run loop. Returns pass/fail sets. |
| **unity-asmdef-doctor** | `haiku` | Assembly definition diagnosis: missing references, wrong assembly. |
| **test-coordination-agent** | `haiku` | Drive test execution through the SQLite request queue. |
| **unity-scene-inspector** | `sonnet` | Narrow questions about scene and prefab contents via hierarchy export. |
| **unity-codebase-scout** | `sonnet` | Find-usages and find-implementations across a large project. |
| **test-writer-agent** | `sonnet` | Comprehensive tests with TDD and UniTask patterns. |
| **refactor-agent** | `sonnet` | Split large files, extract interfaces, SOLID. |
| **batch-refactor-agent** | `sonnet` | Batch C# processing across many files. |
| **architecture-agent** | `sonnet` | Document architecture, find redundancies. |
| **dots-performance-profiler** | `opus` | Burst, job dependencies, allocator analysis. Real reasoning. |

## 📝 Critical Reminders

> **🔴 AFTER WRITING CODE:** IMMEDIATELY refresh Unity AND check for errors!
> **🔴 BEFORE RUNNING TESTS:** ALWAYS refresh Unity AND check for errors!
> **🔷 BEFORE WRITING DOTS CODE:** ALWAYS verify PERSPEC_DOTS_ENABLED is set!
> **Finished editing?** Run verification NOW (refresh + error check)
> **Pivoting?** Ask user first
> **New directory?** Needs asmdef
> **Errors?** Log with context
> **Test prefabs?** Use Editor scripts
> **Tests failing?** Did you refresh Unity? Did you check for compilation errors?
> **Code complete?** Did you verify it compiles? NO EXCEPTIONS!
> **Writing DOTS?** Did you check if DOTS support is enabled? Wrap with #if PERSPEC_DOTS_ENABLED!
<!-- PERSPEC_CONFIG_END -->