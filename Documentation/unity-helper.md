# Unity Helper — Scenario Execution for PerSpec

## Overview

Unity Helper adds declarative scene and asset automation to Unity via JSON task files. It is part of the PerSpec package and uses PerSpec's existing infrastructure — the same SQLite database (`PerSpec/test_coordination.db`), the same coordinator polling pattern, and the same Python CLI patterns.

### The Problem It Solves

AI assistants (like Claude) can write C# code but cannot directly interact with Unity Editor to create scenes, configure components, or instantiate prefabs. Unity Helper bridges that gap:

1. AI writes declarative task lists in JSON
2. The `ScenarioExecutorCoordinator` (C#) polls PerSpec's database for pending requests and executes them
3. The Scenario Runner EditorWindow lets you approve, run, skip, or retry tasks interactively
4. The JSON file becomes a living execution history with status, errors, and results

---

## How It Works

Unity Helper follows the same request-response pattern as PerSpec's built-in coordinators (`MenuItemCoordinator`, `SceneHierarchyCoordinator`, etc.):

```
Python CLI --> PerSpec/test_coordination.db --> ScenarioExecutorCoordinator --> Task Executors
```

| What PerSpec Provides (used as-is) | What Unity Helper Adds |
|------------------------------------|----------------------|
| SQLite database + WAL mode | `scenario_execution_requests` table |
| `test_coordinator.py` CLI pattern | `unityhelper_coordinator.py` |
| `EditorApplication.update` polling pattern | `ScenarioExecutorCoordinator.cs` |
| Asset refresh, log monitoring, test execution | Task executor framework for scene/asset ops |

### Components

All C# code lives in the package under `Editor/UnityHelper/`:

| Component | File | Purpose |
|-----------|------|---------|
| **Scenario Runner** | `ScenarioRunner.cs` | EditorWindow -- interactive task tree UI |
| **Scenario Coordinator** | `ScenarioExecutorCoordinator.cs` | Polls PerSpec's DB, dispatches to executors |
| **Task Registry** | `TaskExecutorRegistry.cs` | Maps task types to executor instances |
| **Base Executor** | `BaseTaskExecutor.cs` | Abstract base with parameter helpers and async support |
| **ITaskExecutor** | `ITaskExecutor.cs` | Interface contract for all executors |
| **Scene Executor** | `SceneTaskExecutor.cs` | 47 scene/asset/component actions |
| **Localization Executor** | `LocalizationTaskExecutor.cs` | 17 localization actions (requires `com.unity.localization`) |
| **TMP Executor** | `TmproTaskExecutor.cs` | 4 TextMeshPro-specific actions |

Python CLI:

| Script | Location | Purpose |
|--------|----------|---------|
| **unityhelper_coordinator.py** | `Editor/Coordination/Scripts/` | Submit scenario requests via CLI |

---

## Quick Start

### Prerequisites

PerSpec must be installed and initialized.

### Run Scenarios via Unity UI

1. **Open:** Unity Menu -> `Tools -> PerSpec -> Scenario Runner`
2. **Browse:** See all scenarios with progress indicators
3. **Expand:** Click "Open" to view hierarchical task groups
4. **Review:** Each task shows a description and parameters
5. **Execute:** Choose your approach:
   - **Run** -- Execute single task
   - **Run All in Group** -- Execute all tasks in a group (including nested)
   - **Run All Pending** -- Execute everything remaining
   - **Skip** -- Mark task as skipped
   - **Reset** -- Return task to pending status
6. **Track:** Status icons show what happened:
   - Success | Failed (shows error) | Skipped | Pending | Running

The JSON file auto-updates after every action. Failed tasks show exact error messages. You can resume from any point.

### Run Scenarios via CLI

```bash
# Run a single action inline (no scenario file needed)
python PerSpec/Coordination/Scripts/unityhelper_coordinator.py execute \
  --action ExportHierarchyPrefab \
  --param prefabPath=Assets/Prefabs/MyPrefab.prefab --focus --wait

python PerSpec/Coordination/Scripts/unityhelper_coordinator.py execute \
  --action SetProperty \
  --param path=MyGO --param component=MyNS.MyComp,Assembly-CSharp \
  --param field=myField --param value=123 --focus --wait

# Execute a scenario file
python PerSpec/Coordination/Scripts/unityhelper_coordinator.py execute \
  --file Assets/Scenarios/scenarios.json --focus --wait

# Run a specific scenario by name
python PerSpec/Coordination/Scripts/unityhelper_coordinator.py execute \
  --file Assets/Scenarios/scenarios.json \
  --target MyScenarioName --focus --wait

# Run only pending tasks (resume interrupted execution)
python PerSpec/Coordination/Scripts/unityhelper_coordinator.py execute \
  --file Assets/Scenarios/scenarios.json --pending-only --focus --wait

# Run only failed tasks (retry)
python PerSpec/Coordination/Scripts/unityhelper_coordinator.py execute \
  --file Assets/Scenarios/scenarios.json --failed-only --focus --wait

# Check request status
python PerSpec/Coordination/Scripts/unityhelper_coordinator.py status <request_id>

# List recent requests
python PerSpec/Coordination/Scripts/unityhelper_coordinator.py list

# Cancel a pending request
python PerSpec/Coordination/Scripts/unityhelper_coordinator.py cancel <request_id>
```

### Scenario File Format

Place scenario JSON files in your project, e.g. `Assets/Scenarios/scenarios.json`:

```json
{
    "scenarios": [
        {
            "name": "Your Scenario Name",
            "description": "What this scenario does",
            "taskGroups": [
                {
                    "name": "Group Name",
                    "description": "What this group does",
                    "tasks": [
                        {
                            "action": "LoadScene",
                            "parameters": [
                                {"key": "path", "value": "Assets/Scenes/MainScene.unity"}
                            ]
                        },
                        {
                            "action": "SetProperty",
                            "parameters": [
                                {"key": "path", "value": "MyObject"},
                                {"key": "component", "value": "MyNamespace.MyComponent, Assembly-CSharp"},
                                {"key": "field", "value": "myField"},
                                {"key": "value", "value": "newValue"}
                            ]
                        },
                        {
                            "action": "SaveScene",
                            "parameters": [
                                {"key": "path", "value": "Assets/Scenes/MainScene.unity"}
                            ]
                        }
                    ]
                }
            ]
        }
    ]
}
```

**Parameter format:** `[{"key": "...", "value": "..."}]` array (Unity JsonUtility compatible).

---

## Task Types

| Type | Value | Purpose | Executor |
|------|-------|---------|----------|
| **SCENE** | `0` | Scene, GameObject, Component, Prefab, Asset operations | `SceneTaskExecutor.cs` |
| **LOCALIZATION** | `1` | Localization strings, tables, languages, CSV import/export | `LocalizationTaskExecutor.cs` |

See `unity-helper-tasks.md` for the complete action and parameter reference.

### Extensible Executor Registration

Projects can register custom executors for new task types:

```csharp
[InitializeOnLoad]
public static class MyExecutorSetup
{
    static MyExecutorSetup()
    {
        TaskExecutorRegistry.Register(new MyCustomTaskExecutor());
    }
}
```

---

## Two-Way Communication

All task communication flows through the scenario JSON using three result fields:

| Field | Purpose |
|-------|---------|
| `status` | `pending` / `success` / `failed` / `skipped` / `running` |
| `error` | Short error message if failed (empty on success) |
| `result` | Detailed output data (text or JSON) |

Every task verifies its own success before marking complete.

---

## Localization Support

The `LocalizationTaskExecutor` requires `com.unity.localization` to be installed. If the package is not present, localization tasks are automatically excluded at compile time via the `HAS_UNITY_LOCALIZATION` define.

---

## Design Philosophy

**Atomic Tasks > Complex Scripts**
- Simple, reusable operations
- Anyone can read and edit JSON
- No C# coding required for new scenes
- Declarative -- describe what you want, not how to build it
- Easy to debug -- task-by-task execution with clear status

**Built on PerSpec, not beside it**
- Uses PerSpec's `test_coordination.db` directly -- no separate database
- `ScenarioExecutorCoordinator` follows the same `[InitializeOnLoad]` + `EditorApplication.update` pattern as PerSpec's coordinators
- Python CLI follows `test_coordinator.py` pattern -- insert request, poll for completion
- All PerSpec tools (refresh, logs, tests, cleanup) work alongside scenario execution