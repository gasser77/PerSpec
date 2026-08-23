# Task Parameters Reference

Quick reference for scenarios.json task parameters. Unity Helper builds on PerSpec's coordination infrastructure.

## Scenario File Location

Place scenario JSON files in your project's `Assets/` folder, e.g. `Assets/Scenarios/scenarios.json`.

---

## Execution

### Via Unity UI (Scenario Runner)
Unity Menu → `Tools → PerSpec → Scenario Runner` — browse, run, skip, and retry tasks interactively.

### Via CLI
```bash
# Single inline action (no scenario file needed)
python PerSpec/Coordination/Scripts/unityhelper_coordinator.py execute \
  --action ExportHierarchyPrefab --param prefabPath=Assets/Prefabs/MyPrefab.prefab --focus --wait

# Full scenario file
python PerSpec/Coordination/Scripts/unityhelper_coordinator.py execute \
  --file Assets/Scenarios/scenarios.json --focus --wait
```

---

## Task Types

| Type | Value | Purpose | Executor |
|------|-------|---------|----------|
| **SCENE** | `0` | Scene/GameObject/Component operations | `SceneTaskExecutor.cs` |
| **LOCALIZATION** | `1` | Localization system operations | `LocalizationTaskExecutor.cs` |

## Available Actions & Parameters

### CreateScene
```json
{
    "action": "CreateScene",
    "parameters": [
        {"key": "name", "value": "SceneName"},
        {"key": "path", "value": "Assets/Scenes"}
    ]
}
```

### LoadScene
```json
{
    "action": "LoadScene",
    "parameters": [
        {"key": "path", "value": "Assets/Scenes/SceneName.unity"}
    ]
}
```

### AddGameObject
```json
{
    "action": "AddGameObject",
    "parameters": [
        {"key": "name", "value": "GameObjectName"},
        {"key": "parent", "value": "ParentPath"} // Optional
    ]
}
```

### AddComponent
```json
{
    "action": "AddComponent",
    "parameters": [
        {"key": "path", "value": "GameObject/Path"},
        {"key": "component", "value": "Namespace.TypeName, AssemblyName"}
    ]
}
```
**IMPORTANT:** `component` must be a **fully qualified assembly name** in .NET format:
- **Format:** `Namespace.TypeName, AssemblyName`
- **UI Components:** `"UnityEngine.UI.Image, UnityEngine.UI"`
- **Core Unity:** `"UnityEngine.Rigidbody, UnityEngine"`
- **TextMeshPro:** `"TMPro.TextMeshProUGUI, Unity.TextMeshPro"`
- **Project Scripts:** `"MyNamespace.WelcomeSceneManager, Assembly-CSharp"`

**Common Examples:**
```json
// Add Image component
{"key": "component", "value": "UnityEngine.UI.Image, UnityEngine.UI"}

// Add Button component
{"key": "component", "value": "UnityEngine.UI.Button, UnityEngine.UI"}

// Add TMP_Text component
{"key": "component", "value": "TMPro.TextMeshProUGUI, Unity.TextMeshPro"}

// Add custom script
{"key": "component", "value": "MyNamespace.LoginSceneManager, Assembly-CSharp"}
```

### InstantiatePrefab
```json
{
    "action": "InstantiatePrefab",
    "parameters": [
        {"key": "prefabPath", "value": "Assets/Prefabs/UI/Prefab.prefab"},
        {"key": "name", "value": "InstanceName"},
        {"key": "parent", "value": "ParentPath"} // Optional
    ]
}
```

### CreateCanvas
```json
{
    "action": "CreateCanvas",
    "parameters": [
        {"key": "name", "value": "CanvasName"}
    ]
}
```

### SetProperty
**Unified action for scene GameObjects, prefabs, and ScriptableObject assets**

**For current scene (backward compatible):**
```json
{
    "action": "SetProperty",
    "parameters": [
        {"key": "path", "value": "Canvas/Button"},
        {"key": "component", "value": "Image"},
        {"key": "field", "value": "sprite"},
        {"key": "value", "value": "Assets/Icons/Login.png"}
    ]
}
```

**For ScriptableObject asset:**
```json
{
    "action": "SetProperty",
    "parameters": [
        {"key": "owner", "value": "Assets/DataService/DefaultSprites.asset"},
        {"key": "field", "value": "gameAudio"},
        {"key": "value", "value": "Assets/ScreenTextures/GameAudio.png"}
    ]
}
```
Note: For assets, `path` and `component` are NOT needed - only `owner`, `field`, and `value`.

**For wiring component references (scene GameObjects):**
```json
{
    "action": "SetProperty",
    "parameters": [
        {"key": "path", "value": "Canvas/GameStatus"},
        {"key": "component", "value": "MyNamespace.GameStatusUI"},
        {"key": "field", "value": "gameIcon"},
        {"key": "value", "value": "Canvas/GameStatus/GameIcon"}
    ]
}
```
- `value` is a GameObject path in the scene (not starting with "Assets/")
- Automatically finds the GameObject and gets the required component type
- Works for Image, TMP_Text, Slider, etc.

**For prefab:**
```json
{
    "action": "SetProperty",
    "parameters": [
        {"key": "owner", "value": "Assets/Prefabs/UI/Button.prefab"},
        {"key": "path", "value": "Icon"},
        {"key": "component", "value": "Image"},
        {"key": "field", "value": "color"},
        {"key": "value", "value": "1,1,1,1"}
    ]
}
```

**For specific scene file:**
```json
{
    "action": "SetProperty",
    "parameters": [
        {"key": "owner", "value": "Assets/Scenes/Login.unity"},
        {"key": "path", "value": "Canvas/LoginButton"},
        {"key": "component", "value": "Button"},
        {"key": "field", "value": "interactable"},
        {"key": "value", "value": "true"}
    ]
}
```

**Supported value coercions (string → target field type):**
`bool`, `int`, `uint`, `long`, `float`, `double`, `string`, `Color` (hex/named/`r,g,b,a`), `Vector2/3/4`, `Vector2Int`, enums (incl. `TMPro.TextAlignmentOptions` shorthand), `Sprite` (asset path), and any `UnityEngine.Object` subclass (scene path → component, otherwise asset path). Integer-sized primitives `uint`/`long`/`double` were added 2026-05-28 to support Flexalon grid fields (`_columns`/`_rows` are `uint`) and similar integral-typed serialized fields.

### SetTransform
```json
{
    "action": "SetTransform",
    "parameters": [
        {"key": "path", "value": "GameObject/Path"},
        {"key": "position", "value": "0,0,0"}, // Optional
        {"key": "rotation", "value": "0,0,0"}, // Optional
        {"key": "scale", "value": "1,1,1"},    // Optional
        {"key": "siblingIndex", "value": "0"}  // Optional (-1 for last)
    ]
}
```

### SetRectTransform
```json
{
    "action": "SetRectTransform",
    "parameters": [
        {"key": "path", "value": "GameObject/Path"},
        {"key": "anchorMin", "value": "0,0"},           // Optional
        {"key": "anchorMax", "value": "1,1"},           // Optional
        {"key": "pivot", "value": "0.5,0.5"},           // Optional
        {"key": "anchoredPosition", "value": "0,0"},    // Optional
        {"key": "sizeDelta", "value": "100,100"},       // Optional
        {"key": "offsetMin", "value": "0,0"},           // Optional
        {"key": "offsetMax", "value": "0,0"}            // Optional
    ]
}
```

### DeleteGameObject
```json
{
    "action": "DeleteGameObject",
    "parameters": [
        {"key": "path", "value": "GameObject/Path"}
    ]
}
```

### SaveScene
```json
{
    "action": "SaveScene",
    "parameters": [
        {"key": "path", "value": "Assets/Scenes/SceneName.unity"}
    ]
}
```

### AddToBuildSettings
```json
{
    "action": "AddToBuildSettings",
    "parameters": [
        {"key": "scenePath", "value": "Assets/Scenes/SceneName.unity"}
    ]
}
```

### CreateScriptableObject
```json
{
    "action": "CreateScriptableObject",
    "parameters": [
        {"key": "type", "value": "MyNamespace.SpriteManager, Assembly-CSharp"},
        {"key": "path", "value": "Assets/DefaultSprites.asset"}
    ]
}
```
**IMPORTANT:** `type` must be a **fully qualified assembly name** in .NET format:
- **Format:** `Namespace.TypeName, AssemblyName`
- **Project ScriptableObjects:** `"MyNamespace.ThemeManager, Assembly-CSharp"`
- **Unity ScriptableObjects:** Use the assembly where the type is defined

**Common Examples:**
```json
// Custom ScriptableObject
{"key": "type", "value": "MyNamespace.ThemeManager, Assembly-CSharp"}
{"key": "type", "value": "MyNamespace.DataService, Assembly-CSharp"}
```

### SaveAsPrefab
Saves a GameObject from the current scene as a prefab asset.
```json
{
    "action": "SaveAsPrefab",
    "parameters": [
        {"key": "path", "value": "Canvas/StatCard"},
        {"key": "prefabPath", "value": "Assets/Prefabs/UI/StatCard.prefab"}
    ]
}
```
- `path` - GameObject path in scene hierarchy
- `prefabPath` - Where to save the prefab (creates directory if needed)
- Idempotent: Replaces existing prefab if it already exists

### SetListProperty
Sets a serialized `List<T>` field on any target: a ScriptableObject (`owner=*.asset`), a Prefab MonoBehaviour (`owner=*.prefab` + `path` + `component`), a scene-file MonoBehaviour (`owner=*.unity` + `path` + `component`), or an active-scene MonoBehaviour (`path` + `component`, no `owner`). Element types include primitives, strings, enums (parsed by enum-name), `Vector2/3/4`, `Color`, plus any `UnityEngine.Object` asset reference. Complex serialized structs use the `json` parameter (ScriptableObject targets only).

**Simple lists (pipe-separated values):**
```json
{
    "action": "SetListProperty",
    "parameters": [
        {"key": "owner", "value": "Assets/DataService/SpriteManager.asset"},
        {"key": "field", "value": "gameIcons"},
        {"key": "values", "value": "Assets/Icons/Game1.png|Assets/Icons/Game2.png|Assets/Icons/Game3.png"},
        {"key": "elementType", "value": "Sprite"},
        {"key": "append", "value": "false"}
    ]
}
```

**Complex/nested lists (JSON format):**
```json
{
    "action": "SetListProperty",
    "parameters": [
        {"key": "owner", "value": "Assets/DataService/SceneNavigationData.asset"},
        {"key": "field", "value": "scenes"},
        {"key": "json", "value": "[{\"scene\":\"Assets/Scenes/WelcomeScene.unity\",\"nextScenes\":[\"Assets/Scenes/SignInScene.unity\",\"Assets/Scenes/CreateAccountScene.unity\"],\"description\":\"[0] SignIn, [1] CreateAccount\"}]"}
    ]
}
```

**Parameters:**
- `owner` - Path to the ScriptableObject asset
- `field` - Name of the List<T> field to set
- `values` - Pipe-separated (`|`) asset paths (for simple lists)
- `json` - JSON array string (for complex/nested types)
- `elementType` (optional) - Type of list elements. Auto-detected from field if not provided
- `append` (optional) - "true" to append to existing list, default "false" replaces

**Supported Element Types:**
- `SceneAsset` - Unity scene files (.unity)
- `Sprite` - Sprite/texture assets
- `GameObject` / `Prefab` - Prefab assets
- `Texture2D` / `Texture` - Texture assets
- `AudioClip` / `Audio` - Audio files
- `Material` - Material assets
- `ScriptableObject` - Custom ScriptableObjects
- Any fully qualified type name

**JSON Format for Nested Types:**
When using `json` parameter, the format matches your class structure:
- String values starting with "Assets/" are loaded as assets
- Nested classes with `sceneAsset` field (like `SceneReference`) are auto-populated
- Arrays of strings become `List<SceneReference>` when the field type matches

**Example - Set simple scene list (via scenario JSON):**
```json
{
    "action": "SetListProperty",
    "parameters": [
        {"key": "owner", "value": "Assets/Data/NavData.asset"},
        {"key": "field", "value": "scenes"},
        {"key": "values", "value": "Assets/Scenes/Home.unity|Assets/Scenes/Game.unity"},
        {"key": "elementType", "value": "SceneAsset"}
    ]
}
```

**Example - Set complex navigation list (SceneNavigationData):**
```json
{
    "action": "SetListProperty",
    "parameters": [
        {"key": "owner", "value": "Assets/DataService/SceneNavigationData.asset"},
        {"key": "field", "value": "scenes"},
        {"key": "json", "value": "[{\"scene\":\"Assets/Scenes/WelcomeScene.unity\",\"nextScenes\":[\"Assets/Scenes/SignInScene.unity\",\"Assets/Scenes/CreateAccountScene.unity\"],\"description\":\"[0] SignIn, [1] CreateAccount\"}]"}
    ]
}
```

**Example - Append sprites to list:**
```json
{
    "action": "SetListProperty",
    "parameters": [
        {"key": "owner", "value": "Assets/Data/Icons.asset"},
        {"key": "field", "value": "iconList"},
        {"key": "values", "value": "Assets/Icons/New1.png|Assets/Icons/New2.png"},
        {"key": "append", "value": "true"}
    ]
}
```

## Localization Actions

### CreateLocalizationSettings
Checks if Unity Localization system is initialized. **Must be run FIRST before any other localization actions.**
```json
{
    "type": "localization",
    "action": "CreateLocalizationSettings",
    "parameters": []
}
```
- No parameters required
- Checks if LocalizationSettings exists
- If not exists, provides instructions to create it manually via Unity Editor
- Idempotent: Returns success if already exists
- **PREREQUISITE:** Must run before AddLanguage, CreateStringTable, SetString, or UpdateAll

### DebugAllLocalizationInfo
Logs comprehensive information about the localization system for debugging purposes.
```json
{
    "type": "localization",
    "action": "DebugAllLocalizationInfo",
    "parameters": []
}
```
- No parameters required
- Logs all available localization data to Unity Console:
  - Settings info (name, path, type)
  - Selected locale
  - Project locales (Editor)
  - Available locales (Runtime)
  - String and Asset databases
  - String table collections with sample entries
  - Asset table collections with sample entries
- Always succeeds if LocalizationSettings exists
- Useful for troubleshooting localization setup

### AddLanguage
Adds a language to the localization system.
```json
{
    "type": "localization",
    "action": "AddLanguage",
    "parameters": [
        {"key": "languageCode", "value": "es"},
        {"key": "displayName", "value": "Spanish"}
    ]
}
```
- `languageCode` - ISO language code (e.g., "es", "fr", "zh-Hant")
- `displayName` - Human-readable name for the language

### CreateStringTable
Creates a string table collection for all available locales.
```json
{
    "type": "localization",
    "action": "CreateStringTable",
    "parameters": [
        {"key": "tableName", "value": "General"}
    ]
}
```
- `tableName` - Name of the string table collection (required)
- Automatically includes all available locales
- Idempotent: Returns success if table already exists

### SetString
Sets a localized string value.
```json
{
    "type": "localization",
    "action": "SetString",
    "parameters": [
        {"key": "key", "value": "welcome_message"},
        {"key": "value", "value": "Welcome!"},
        {"key": "language", "value": "en"},
        {"key": "table", "value": "General"}
    ]
}
```
- `key` - Localization key identifier (required)
- `value` - The localized text (required)
- `language` - Language code (optional, defaults to "en")
- `table` - String table name (optional, defaults to "General")

### UpdateAll
Updates all localizations from a source language.
```json
{
    "type": "localization",
    "action": "UpdateAll",
    "parameters": [
        {"key": "sourceLanguage", "value": "en"},
        {"key": "force", "value": "true"},
        {"key": "table", "value": "General"}
    ]
}
```
- `sourceLanguage` - Source language to update from (optional, defaults to "en")
- `force` - Whether to force update existing translations (optional, defaults to "false")
- `table` - String table name to update (optional, defaults to "General")

### BulkSetStrings ⭐
**GAME CHANGER:** Import ALL strings from a file at once! Replaces dozens of SetString tasks.
```json
{
    "type": "localization",
    "action": "BulkSetStrings",
    "parameters": [
        {"key": "filePath", "value": "Assets/Localization/localization_strings.txt"},
        {"key": "language", "value": "en"},
        {"key": "table", "value": "General"}
    ]
}
```
- `filePath` - Path to text file with key=value or key:value format (required)
- `language` - Language code (optional, defaults to "en")
- `table` - Table name (optional, defaults to "General")
- File format: `key=value` or `key:value`, one per line
- Supports comments: lines starting with # or //
- Logs count of added/updated entries

### RemoveLanguage
Removes a language from the localization system.
```json
{
    "type": "localization",
    "action": "RemoveLanguage",
    "parameters": [
        {"key": "languageCode", "value": "es"}
    ]
}
```
- `languageCode` - ISO language code to remove (required)
- Idempotent: Returns success if already removed

### DeleteString
Removes a string key from all languages in a table.
```json
{
    "type": "localization",
    "action": "DeleteString",
    "parameters": [
        {"key": "key", "value": "obsolete_key"},
        {"key": "table", "value": "General"}
    ]
}
```
- `key` - Localization key to delete (required)
- `table` - Table name (optional, defaults to "General")

### GetString
Logs the current value of a localization key (useful for verification).
```json
{
    "type": "localization",
    "action": "GetString",
    "parameters": [
        {"key": "key", "value": "welcome_message"},
        {"key": "language", "value": "en"},
        {"key": "table", "value": "General"}
    ]
}
```
- `key` - Localization key (required)
- `language` - Language code (optional, defaults to "en")
- `table` - Table name (optional, defaults to "General")

### CreateAssetTable
Creates an asset table collection for localizing assets (sprites, audio, etc.).
```json
{
    "type": "localization",
    "action": "CreateAssetTable",
    "parameters": [
        {"key": "tableName", "value": "UI_Assets"}
    ]
}
```
- `tableName` - Name of the asset table collection (required)
- Automatically includes all available locales
- Idempotent: Returns success if table already exists

### SetAsset
Assigns an asset to a localization key.
```json
{
    "type": "localization",
    "action": "SetAsset",
    "parameters": [
        {"key": "key", "value": "flag_icon"},
        {"key": "assetPath", "value": "Assets/Sprites/Flags/US.png"},
        {"key": "language", "value": "en"},
        {"key": "table", "value": "UI_Assets"}
    ]
}
```
- `key` - Localization key (required)
- `assetPath` - Path to asset file (required)
- `language` - Language code (optional, defaults to "en")
- `table` - Asset table name (optional, defaults to "General")

### ValidateKeys
Quality assurance tool - checks which translations are missing or empty.
```json
{
    "type": "localization",
    "action": "ValidateKeys",
    "parameters": [
        {"key": "sourceLanguage", "value": "en"},
        {"key": "table", "value": "General"}
    ]
}
```
- `sourceLanguage` - Source language to compare against (optional, defaults to "en")
- `table` - Table name (optional, defaults to "General")
- Logs comprehensive validation report to Unity Console

### RemoveOrphanKeys
Repairs a SharedData↔table desync in a String Table Collection. Removes rows whose
`KeyId` has no SharedData key name (also any SharedData entry with a null/empty key).
Such rows are unreachable via `GetString` (no key name to look them up), crash
`ValidateKeys` / `UpdateAll` with `ArgumentNullException: key`, and corrupt the runtime
`StringDatabase` load so **every** key in the table resolves to `[key]`.
```json
{
    "type": "localization",
    "action": "RemoveOrphanKeys",
    "parameters": [
        {"key": "table", "value": "General"},
        {"key": "dryRun", "value": "true"}
    ]
}
```
- `table` - Table collection name (optional, defaults to "General")
- `dryRun` - `"true"` reports orphan KeyIds (and en values) without modifying; `"false"` (default) removes them
- Idempotent. Run `dryRun` first. Symptoms that call for it: `ValidateKeys`/`UpdateAll`
  throw `Value cannot be null. Parameter name: key`, or the running game shows
  `[some.key]` for keys that resolve fine in the editor.

### RenameKey
Safely renames a key across all languages.
```json
{
    "type": "localization",
    "action": "RenameKey",
    "parameters": [
        {"key": "oldKey", "value": "old_name"},
        {"key": "newKey", "value": "new_name"},
        {"key": "table", "value": "General"}
    ]
}
```
- `oldKey` - Current key name (required)
- `newKey` - New key name (required)
- `table` - Table name (optional, defaults to "General")
- Renames in SharedTableData (affects all languages)
- Validates old key exists and new key doesn't exist

### CopyStringAcrossLanguages
Copies a specific string from one language to another.
```json
{
    "type": "localization",
    "action": "CopyStringAcrossLanguages",
    "parameters": [
        {"key": "key", "value": "welcome_message"},
        {"key": "sourceLanguage", "value": "en"},
        {"key": "targetLanguage", "value": "es"},
        {"key": "table", "value": "General"}
    ]
}
```
- `key` - Localization key to copy (required)
- `sourceLanguage` - Source language code (required)
- `targetLanguage` - Target language code (required)
- `table` - Table name (optional, defaults to "General")
- More precise than UpdateAll for single key copies

### ExportTableToCSV
Exports table to CSV for translators.
```json
{
    "type": "localization",
    "action": "ExportTableToCSV",
    "parameters": [
        {"key": "table", "value": "General"},
        {"key": "outputPath", "value": "Assets/Localization/Export/General.csv"}
    ]
}
```
- `table` - Table name to export (required)
- `outputPath` - Where to save CSV file (required)
- CSV format: First column is Key, subsequent columns are language codes
- Properly escapes quotes and commas
- Creates directory if it doesn't exist

### ImportTableFromCSV
Imports translations from CSV.
```json
{
    "type": "localization",
    "action": "ImportTableFromCSV",
    "parameters": [
        {"key": "csvPath", "value": "Assets/Localization/Import/General_translated.csv"},
        {"key": "table", "value": "General"}
    ]
}
```
- `csvPath` - Path to CSV file (required)
- `table` - Target table name (required)
- CSV format: First column must be "Key", subsequent columns are language codes
- Handles quoted values and escaped quotes
- Logs count of imported and updated entries

### ExportHierarchy
Exports the **active scene** hierarchy to `task.result` with GameObjects and components.
```json
{
    "action": "ExportHierarchy",
    "parameters": []
}
```
- No parameters required
- Exports all GameObjects in active scene with their components
- Shows hierarchy structure with indentation
- Result stored in `task.result` (two-way communication)
- Also logs output to Unity Console

### ExportHierarchyScene
Opens a **specific scene file**, exports its hierarchy, then restores the previously active scene.
```json
{
    "action": "ExportHierarchyScene",
    "parameters": [
        {"key": "scenePath", "value": "Assets/Scenes/MyScene.unity"}
    ]
}
```
- `scenePath` - Path to the `.unity` file (required)
- Exports all GameObjects and components with indentation
- Restores the previously active scene after export
- Result stored in `task.result`

### ExportHierarchyPrefab
Exports the hierarchy of a **prefab asset** to `task.result` without loading a scene.
```json
{
    "action": "ExportHierarchyPrefab",
    "parameters": [
        {"key": "prefabPath", "value": "Assets/Prefabs/MyPrefab.prefab"},
        {"key": "nameFilter", "value": "Button"},
        {"key": "componentFilter", "value": "Image"},
        {"key": "maxDepth", "value": "5"},
        {"key": "includeComponents", "value": "true"}
    ]
}
```
- `prefabPath` - Path to the `.prefab` asset (required)
- `nameFilter` - Filter GameObjects by name substring (optional, case-insensitive)
- `componentFilter` - Filter GameObjects by component type (optional, short name or full type)
- `maxDepth` - Maximum hierarchy depth to traverse (optional, -1 for unlimited)
- `includeComponents` - Include component listing in output (optional, default: true)
- Loads the prefab via `AssetDatabase` — no scene required
- When filters are active, emits only matching GameObjects in flat mode
- When no filters, shows full tree hierarchy with indentation
- Result stored in `task.result`

### OpenPrefab
Opens a prefab in Prefab Mode for editing.
```json
{
    "action": "OpenPrefab",
    "parameters": [
        {"key": "prefabPath", "value": "Assets/Prefabs/MyPrefab.prefab"}
    ]
}
```
- `prefabPath` - Path to the prefab asset (required)
- Opens the prefab in Unity's Prefab Mode for direct editing
- Must be followed by `SavePrefab` to save changes
- Stores confirmation message in `task.result`

**Missing-script detection:** After opening, the prefab is scanned for GameObjects with missing `MonoBehaviour` script references. If any are found, the task **still succeeds** (returns `true`) but `task.result` is prefixed with `WARNING:` and lists the affected GameObject paths plus the unresolved script GUIDs (parsed from the prefab YAML). A `Debug.LogWarning` is also emitted to the Unity Console. Execution continues so scenarios that don't persist the prefab are unaffected; if a subsequent `SavePrefab` runs, Unity will refuse to write a prefab containing missing scripts and that task will fail with the natural error.

### SavePrefab
Saves the currently open prefab and closes Prefab Mode.
```json
{
    "action": "SavePrefab",
    "parameters": []
}
```
- No parameters required
- Saves the currently open prefab stage and closes Prefab Mode
- Must be called after `OpenPrefab` and any modifications
- Returns error if no prefab stage is currently open
- Stores saved prefab path in `task.result`

**Missing-script pre-flight:** Before calling Unity's save API, the open prefab is re-scanned for missing-script slots. If any are present, the task **fails** with `task.error` describing the affected GameObject paths and the unresolved script GUIDs (parsed from the prefab YAML), and instructs the caller to run `RemoveMissingScripts` first. This replaces Unity's opaque generic save error with actionable detail.

### RemoveMissingScripts
Strips missing-script `MonoBehaviour` slots from the currently open prefab so `SavePrefab` can succeed afterward. Use this when `OpenPrefab` reported a missing-script warning or `SavePrefab` failed for that reason.
```json
{
    "action": "RemoveMissingScripts",
    "parameters": []
}
```
With an optional sub-target:
```json
{
    "action": "RemoveMissingScripts",
    "parameters": [
        {"key": "target", "value": "Console/sus_op_ctrl"}
    ]
}
```
- `target` (optional): GameObject path under the open prefab root (resolved via `FindInActiveContext`). Without it, the entire prefab root is walked.
- Walks the (sub)tree, calls `GameObjectUtility.RemoveMonoBehavioursWithMissingScript` on each affected GameObject, and marks the stage dirty.
- Requires a prefab to be currently open (`OpenPrefab` first). Returns error otherwise.
- `task.result` reports the total slots removed and the list of GameObject paths that were cleaned. Empty result message ("No missing scripts found") when the (sub)tree was clean.
- This action does **not** save — pair with `SavePrefab` to persist the cleanup.

### SetParentByTransform
Sets the parent of a GameObject using Transform.Find() for Prefab Mode.
```json
{
    "action": "SetParentByTransform",
    "parameters": [
        {"key": "path", "value": "MyGameObject"},
        {"key": "parentPath", "value": "Parent/Child/Grandchild"}
    ]
}
```
- `path` - GameObject name to find via GameObject.Find() (required)
- `parentPath` - Relative path from prefab root using '/' separator (required)
- Only works in Prefab Mode (returns error otherwise)
- Uses Transform.Find() to traverse the hierarchy from prefab root
- Useful for reparenting within complex prefab structures
- Stores confirmation message in `task.result`

### TakeScreenshot
Captures a screenshot of the current Game view with configurable output and resolution.
```json
{
    "action": "TakeScreenshot",
    "parameters": [
        {"key": "outputDir", "value": "Assets/Screenshots"},
        {"key": "filename", "value": "welcome-scene"},
        {"key": "supersampling", "value": "2"}
    ]
}
```
- `outputDir` - Directory to save screenshots (optional, default: `Assets/Screenshots`)
- `filename` - Base filename without extension (optional, default: `screenshot`)
- `supersampling` - Resolution multiplier, e.g. 2 for 2x (optional, default: `1`)
- Automatically appends timestamp: `{filename}_{yyyyMMdd_HHmmss}.png`
- Creates output directory if it doesn't exist
- Stores the full output path in `task.result`

**Scenario JSON Example:**
```json
{
    "action": "TakeScreenshot",
    "parameters": [
        {"key": "outputDir", "value": "Assets/Screenshots"},
        {"key": "filename", "value": "test"}
    ]
}
```

### WaitForGameObject
Waits for a GameObject to appear in the scene, with optional component type verification. Uses async polling via `EditorApplication.delayCall`.
```json
{
    "action": "WaitForGameObject",
    "parameters": [
        {"key": "path", "value": "WelcomeSceneController"},
        {"key": "component", "value": "MyNamespace.WelcomeSceneManager, Assembly-CSharp"},
        {"key": "timeout", "value": "10"}
    ]
}
```
- `path` - GameObject name or hierarchy path to find via `GameObject.Find()` (required)
- `component` - Fully qualified component type to verify on the found object (optional)
- `timeout` - Maximum wait time in seconds (optional, default: `10`)
- Returns immediately if the GameObject (and optional component) is already present
- If not found, polls asynchronously until found or timeout
- Stores `"Found: {path}"` in `task.result` on success
- **Note:** Async polling only works when run via the Scenario Runner (EditorWindow). The CLI coordinator executes synchronously (immediate check only).

### CallMethod
Calls a method on a component via reflection. Replaces hardcoded test helper calls with a generic, parameterized action.
```json
{
    "action": "CallMethod",
    "parameters": [
        {"key": "path", "value": "WelcomeSceneController"},
        {"key": "component", "value": "MyNamespace.WelcomeSceneManager, Assembly-CSharp"},
        {"key": "method", "value": "TestHelper_ClickLoginButton"}
    ]
}
```
- `path` - GameObject name or hierarchy path (required)
- `component` - Fully qualified component type name (required)
- `method` - Method name to invoke (required). Supports public and non-public instance methods.
- Invokes parameterless methods only (no arguments)
- Stores the method return value (or `"void"`) in `task.result`

**Example: Full UI Flow as a Scenario**

The old hardcoded `TestRunnerUIFlow` can now be expressed as a declarative JSON scenario:
```json
{
    "name": "UI Flow Screenshots",
    "taskGroups": [{
        "name": "Welcome to Home Flow",
        "tasks": [
            {"action": "LoadScene", "parameters": [{"key": "path", "value": "Assets/Scenes/MainScene.unity"}]},
            {"action": "WaitForGameObject", "parameters": [{"key": "path", "value": "WelcomeSceneController"}, {"key": "timeout", "value": "10"}]},
            {"action": "TakeScreenshot", "parameters": [{"key": "outputDir", "value": "Assets/Screenshots/UIFlow"}, {"key": "filename", "value": "01_welcome"}]},
            {"action": "CallMethod", "parameters": [{"key": "path", "value": "WelcomeSceneController"}, {"key": "component", "value": "MyNamespace.WelcomeSceneManager, Assembly-CSharp"}, {"key": "method", "value": "TestHelper_ClickLoginButton"}]},
            {"action": "WaitForGameObject", "parameters": [{"key": "path", "value": "SignInSceneManager"}, {"key": "timeout", "value": "10"}]},
            {"action": "TakeScreenshot", "parameters": [{"key": "outputDir", "value": "Assets/Screenshots/UIFlow"}, {"key": "filename", "value": "02_signin"}]}
        ]
    }]
}
```

### InspectGameObject
Reads component field values from a GameObject at runtime. **Works in Play mode!**
```json
{
    "action": "InspectGameObject",
    "parameters": [
        {"key": "path", "value": "LoginSceneManager"},
        {"key": "component", "value": "LoginSceneManager"},
        {"key": "fields", "value": "loginButton,usernameInput,passwordInput"}
    ]
}
```
- `path` - GameObject path in hierarchy (required)
- `component` - Component type name (optional, inspects all if omitted)
- `fields` - Comma-separated field names or "all" (optional, defaults to "all")
- Returns field names and values in task.result
- Shows null/assigned state for Unity Object references
- Can inspect private and public fields
- **Use Case:** Debug component state, verify LoadVars() found references correctly

### WrapWithParent
Insert a new GameObject as an intermediate parent, either above a target (`mode=self`) or between a target and its existing children (`mode=children`). Preserves sibling order; auto-adds a stretched RectTransform when target is UI.
```json
{
  "action": "WrapWithParent",
  "parameters": [
    { "key": "target", "value": "Canvas" },
    { "key": "newParentName", "value": "SafeAreaRoot" },
    { "key": "mode", "value": "children" }
  ]
}
```
**Parameters:**
- `target` (required): Path of the GameObject to wrap or whose children to wrap.
- `newParentName` (required): Name of the new GameObject.
- `mode` (optional, default `"children"`):
  - `"children"` → new GO becomes a child of target; target's existing children re-parent under it. Use for inserting a layout root (e.g., `SafeAreaRoot`) inside a Canvas.
  - `"self"` → new GO becomes parent of target; target re-parents under it. Use for wrapping a single GO in a new container while keeping siblings.
- `worldPositionStays` (optional, default `"false"`): Passed to `Transform.SetParent`.

**Behavior:**
- Auto-adds `RectTransform` to the new GO (stretched, zero-offset, pivot 0.5,0.5) when target has a `RectTransform`. Otherwise plain `Transform`.
- Idempotent: re-running on an already-wrapped target returns success without modifying the scene.

### AddComponentToMatching
Add a component to every GameObject under `root` that passes a filter. Mirrors `SetPropertyOnMatching`'s filter model. Already-present components count as `alreadyHad`, not re-added.
```json
{
  "action": "AddComponentToMatching",
  "parameters": [
    { "key": "root", "value": "Canvas" },
    { "key": "component", "value": "Flexalon.FlexalonObject, Flexalon" },
    { "key": "pathFilter", "value": "Layout" },
    { "key": "maxDepth", "value": "3" }
  ]
}
```
**Parameters:**
- `component` (required): Fully qualified type name of the component to add.
- `root` (optional, default active scene root): Path of the subtree to walk.
- `withComponent` (optional): Only match GameObjects that already have this component type.
- `pathFilter` (optional): Substring filter on the GameObject's full scene path.
- `maxDepth` (optional, default `-1` unlimited): Walk depth from each root.

**Result JSON:** `{"matched":N,"added":M,"alreadyHad":K,"failed":F,"failures":[...]}`. Task succeeds when `failed == 0`.

### Validate `hasComponent` leaf (extension)
The `Validate` task's assertion grammar now accepts a `hasComponent` leaf that checks presence without requiring a field — use instead of `component`+`field`+`equals` when you only care that the component exists.
```json
{
  "rules": [
    {
      "name": "canvas-has-dpi-scaler",
      "target": { "root": "Canvas" },
      "assert": { "hasComponent": "Flexalon.FlexalonDpiScaler, Flexalon" }
    }
  ]
}
```
Composable with `not`/`all`/`any` like any other leaf.

## Additional Scene Actions

### ClosePrefab
Closes the Prefab Stage opened by `OpenPrefab` and returns the editor view to the main scene. Does **not** save — pair with `SavePrefab` first if you need edits persisted. Idempotent: succeeds with `task.result = "No prefab stage was open"` when nothing is open.
```json
{ "action": "ClosePrefab", "parameters": [] }
```
- No parameters.

### ReportMissingScripts
Diagnostic action: scans a prefab for GameObjects with missing `MonoBehaviour` script slots and reports them via `task.result` without modifying the asset. Uses Unity APIs only (`PrefabUtility.LoadPrefabContents`, `EditorJsonUtility.ToJson`, `AssetDatabase.GetDependencies`) — no `.prefab` text parsing.
```json
{
    "action": "ReportMissingScripts",
    "parameters": [
        { "key": "prefabPath", "value": "Assets/Prefabs/MyPrefab.prefab" }
    ]
}
```
- `prefabPath` (optional): scan this prefab via `PrefabUtility.LoadPrefabContents` (does not affect any open stage). When omitted, the currently open prefab stage is inspected instead.
- Reports each affected GameObject + missing-slot count, plus a list of resolvable dependency GUIDs.
- **Limitation:** orphan GUIDs (where both `.cs` and `.meta` were deleted) are not retrievable through Unity APIs — recover them via `git log --all --diff-filter=D` to identify deleted scripts.

### SetActive
Toggles `GameObject.activeSelf`. Searches inactive objects via `Resources.FindObjectsOfTypeAll` if `FindInActiveContext` doesn't resolve the path.
```json
{
    "action": "SetActive",
    "parameters": [
        { "key": "path", "value": "HUD/HealthBar" },
        { "key": "active", "value": "false" },
        { "key": "recursive", "value": "false" }
    ]
}
```
- `path` (required): GameObject path.
- `active` (required): `"true"` | `"false"`.
- `recursive` (optional, default `"false"`): when true, walks the entire subtree.

### SetSiblingIndex
Sets the GameObject's order among its siblings via `Transform.SetSiblingIndex`. Useful for layout groups and UI ordering.
```json
{
    "action": "SetSiblingIndex",
    "parameters": [
        { "key": "path", "value": "Canvas/Panel/Button" },
        { "key": "index", "value": "0" }
    ]
}
```
- `path` (required): GameObject path.
- `index` (required): integer; clamped by Unity to the parent's child count.

### DuplicateGameObject
Clones a GameObject (with all components and children) under the same parent. Idempotent: if a sibling with the resolved name already exists, succeeds without re-duplicating.
```json
{
    "action": "DuplicateGameObject",
    "parameters": [
        { "key": "path", "value": "Spawners/Enemy" },
        { "key": "newName", "value": "Enemy_Boss" }
    ]
}
```
- `path` (required): source GameObject.
- `newName` (optional, default `<name>_Copy`): name for the clone.

### MoveGameObject
Re-parents a GameObject. Empty `newParent` moves to scene root.
```json
{
    "action": "MoveGameObject",
    "parameters": [
        { "key": "path", "value": "OldParent/Item" },
        { "key": "newParent", "value": "NewParent/Container" },
        { "key": "worldPositionStays", "value": "false" }
    ]
}
```
- `path` (required): GameObject to move.
- `newParent` (required): destination parent path; empty string for scene root.
- `worldPositionStays` (optional, default `"false"`): passed to `Transform.SetParent`.

### RemoveComponent
Destroys a component on a GameObject. Idempotent: if the component is already absent, succeeds silently.
```json
{
    "action": "RemoveComponent",
    "parameters": [
        { "key": "path", "value": "Canvas/Panel" },
        { "key": "component", "value": "UnityEngine.UI.Image, UnityEngine.UI" }
    ]
}
```
- `path` (required): GameObject path.
- `component` (required): assembly-qualified component type name.

### RevertPrefabInstance
Removes ALL instance overrides from a scene prefab instance so it mirrors the prefab asset exactly (see the project's `PrefabEditRules.md` Case 1 — prefab is the single source of truth). Resolves the outermost prefab instance root from the given path.
```json
{
    "action": "RevertPrefabInstance",
    "parameters": [
        { "key": "path", "value": "SecretView" },
        { "key": "keepName", "value": "true" }
    ]
}
```
- `path` (required): the prefab instance root (or any child of it) in the scene.
- `keepName` (optional, default `"true"`): preserve the instance GameObject's scene name — instances are often renamed (prefab `SecretScreenView` instanced as `SecretView`) and code finds them by path. Pass `"false"` to let the revert restore the prefab's root name too.
- Follow with `SaveScene` to persist. Typical flow after moving shared changes into a prefab: `LoadScene` → `RevertPrefabInstance` → re-apply any genuinely scene-local values → `SaveScene`.

### RenameGameObject
Renames a GameObject. Idempotent: if a GameObject with the target name already exists at the resolved location, succeeds without modifying anything.
```json
{
    "action": "RenameGameObject",
    "parameters": [
        { "key": "path", "value": "Canvas/Panel/OldName" },
        { "key": "newName", "value": "NewName" }
    ]
}
```
- `path` (required): GameObject path.
- `newName` (required): new name (only the leaf name; doesn't move the GameObject).

### SetParent
Re-parents using `Transform.SetParent` (scene context). For Prefab Mode, prefer `SetParentByTransform` which uses `Transform.Find` from the prefab root.
```json
{
    "action": "SetParent",
    "parameters": [
        { "key": "path", "value": "OldParent/Child" },
        { "key": "parent", "value": "NewParent" }
    ]
}
```
- `path` (required): GameObject path.
- `parent` (required): new parent path; empty string for scene root.

### BatchSetProperty
Sets multiple fields/properties on a single component in one task. Saves dispatch overhead for many sequential `SetProperty` calls.
```json
{
    "action": "BatchSetProperty",
    "parameters": [
        { "key": "path", "value": "Canvas/Title" },
        { "key": "component", "value": "TMPro.TextMeshProUGUI, Unity.TextMeshPro" },
        { "key": "properties", "value": "{\"text\":\"Hello\",\"fontSize\":\"48\",\"color\":\"red\"}" }
    ]
}
```
- `path` (required): GameObject path.
- `component` (required): assembly-qualified component type.
- `properties` (required): JSON object mapping field/property name → value (each value parsed the same way `SetProperty` parses).

### GetProperty
Reads a field or readable property and returns the serialized value in `task.result`. Counterpart to `SetProperty`. Two forms:

**Scene GameObject (path + component + field):**
```json
{
    "action": "GetProperty",
    "parameters": [
        { "key": "path", "value": "Player" },
        { "key": "component", "value": "MyGame.PlayerController, Assembly-CSharp" },
        { "key": "field", "value": "health" }
    ]
}
```

**ScriptableObject asset (owner=*.asset + field):**
```json
{
    "action": "GetProperty",
    "parameters": [
        { "key": "owner", "value": "Assets/DataService/LocalizationService.asset" },
        { "key": "field", "value": "currentLanguage" }
    ]
}
```
- `path`+`component`+`field` for scene GameObjects, OR `owner`+`field` for ScriptableObject assets.
- Reads non-public members too. Indexed properties return an error — use specific field/getter instead.
- Result is JSON-friendly via `SerializeReflectedValue` (primitives, `Vector*`, `Color`, references encoded as paths/asset-paths).

### FindObjects
Walks the scene (or a subtree) and returns paths of GameObjects matching optional component-type and path-substring filters. Returns a JSON object in `task.result`: `{"count":N,"paths":[...]}`.
```json
{
    "action": "FindObjects",
    "parameters": [
        { "key": "root", "value": "Canvas" },
        { "key": "withComponent", "value": "UnityEngine.UI.Button, UnityEngine.UI" },
        { "key": "pathFilter", "value": "Submit" },
        { "key": "maxDepth", "value": "5" }
    ]
}
```
- `root` (optional, default active scene): subtree to walk; `"/"` or empty walks all scene roots.
- `withComponent` (optional): assembly-qualified component type — match GameObjects with this component.
- `pathFilter` (optional): substring match against the GameObject's full path.
- `maxDepth` (optional, default `"-1"` unlimited).

### SetPropertyOnMatching
Combines `FindObjects` and `SetProperty`: walks a subtree, finds GameObjects matching filters, and sets one field on the named component for each. Returns `{"matched":N,"set":M,"skipped":K,"failures":[...]}` in `task.result`.
```json
{
    "action": "SetPropertyOnMatching",
    "parameters": [
        { "key": "root", "value": "Canvas" },
        { "key": "withComponent", "value": "UnityEngine.UI.Image, UnityEngine.UI" },
        { "key": "field", "value": "color" },
        { "key": "value", "value": "transparent" },
        { "key": "pathFilter", "value": "Backdrop" },
        { "key": "maxDepth", "value": "-1" }
    ]
}
```
- `root` (optional, default scene roots): subtree to walk.
- `withComponent`, `field`, `value` (required): same semantics as `SetProperty`.
- `pathFilter`, `maxDepth` (optional): same semantics as `FindObjects`.
- Task succeeds when `failures.Length == 0`.

### ApplyRecipe
Invokes a user-authored **recipe** — a parameterized template of scene primitives. Recipes live in user JSON files and use `{{paramName}}` substitution. Schema: `{package_path}/Editor/Schemas/recipe.schema.json`.
```json
{
    "action": "ApplyRecipe",
    "parameters": [
        { "key": "recipeFile", "value": "Assets/Recipes/wireframe-card.recipe.json" },
        { "key": "params", "value": "{\"name\":\"FeatureCard\",\"parent\":\"Canvas/CardList\",\"title\":\"Hello\"}" }
    ]
}
```
- `recipeFile` (required): path to the recipe JSON.
- `params` (optional, default `"{}"`): JSON object whose keys match `{{tokens}}` in the recipe body.
- Pre-flight: any unresolved `{{token}}` after substitution causes the task to fail with the missing token name.
- Inner tasks run via the same dispatch as scenarios; aggregate result reports per-task success/failure.

### Validate
Runs project-authored invariant rules against the active scene/prefab. Each rule has a `target` (GameObject selector) and an `assert` (composable predicate over components/fields). Schema: `{package_path}/Editor/Schemas/validator-rules.schema.json`.
```json
{
    "action": "Validate",
    "parameters": [
        { "key": "rulesFile", "value": "Assets/Validators/canvas-rules.json" },
        { "key": "strict", "value": "true" }
    ]
}
```
- `rulesFile` (required): path to the rules JSON file.
- `strict` (optional, default `"true"`): when `true`, any rule violation fails the task; when `false`, violations are reported in `task.result` but the task succeeds.
- Result: per-rule summary (rule name, targets matched, violations) plus aggregate counts.
- See the **Validate `hasComponent` leaf (extension)** section for the assertion grammar.

### SetImporterProperty
Sets a field or property on the **AssetImporter** for a given asset (TextureImporter, ModelImporter, AudioImporter, etc.) and calls `SaveAndReimport()` to persist and reapply. Generic reflection-based — works on any AssetImporter subtype's public or non-public members. Use this instead of writing one-off editor scripts to flip import settings.
```json
{
    "action": "SetImporterProperty",
    "parameters": [
        { "key": "assetPath", "value": "Assets/ScreenTextures/welcomescreenbackground.png" },
        { "key": "field", "value": "spriteImportMode" },
        { "key": "value", "value": "Single" },
        { "key": "importerType", "value": "TextureImporter" }
    ]
}
```
- `assetPath` (required): path to the asset (Unity-format, starts with `Assets/`).
- `field` (required): the importer field or property name (e.g., `spriteImportMode`, `textureType`, `mipmapEnabled`, `wrapMode`, `filterMode`, `npotScale`, `isReadable`).
- `value` (required): value as a string. Parsed via `ConvertValue` — same rules as `SetProperty`: enums by name (`"Single"`, `"Sprite"`, `"Bilinear"`), bools (`"true"`/`"false"`), numbers, vectors (`"x,y"`), colors (`"#FFAA00"` or `"red"`).
- `importerType` (optional): expected importer subtype name (e.g., `"TextureImporter"`, `"ModelImporter"`, `"AudioImporter"`). When provided, the task fails fast if the asset's actual importer is a different type — guards against accidentally touching the wrong asset class.
- Calls `SaveAndReimport()` on success → asset is reimported in the same task, downstream tasks see the new state.
- **Common pairings:** to convert a PNG to a Single sprite, often you need two calls: first `field=textureType, value=Sprite`, then `field=spriteImportMode, value=Single`. Most TextureImporters created via drag-drop default to `Default` textureType, which makes sprite-related fields no-op.

### SetPluginPlatform
Configures a native plugin's platform compatibility on its **PluginImporter** — the settings shown in the plugin Inspector's "Select platforms for plugin" panel. PluginImporter exposes these only through methods (`SetCompatibleWithPlatform` / `SetCompatibleWithAnyPlatform` / `SetCompatibleWithEditor` / `SetPlatformData`), which `SetImporterProperty` (reflection on fields/properties) cannot reach.
```json
{
    "action": "SetPluginPlatform",
    "parameters": [
        { "key": "assetPath", "value": "Assets/Plugins/UnduguNativeFileDialogMac.mm" },
        { "key": "platform", "value": "StandaloneOSX" },
        { "key": "enable", "value": "true" },
        { "key": "dataKey", "value": "CPU" },
        { "key": "dataValue", "value": "AnyCPU" }
    ]
}
```
- `assetPath` (required): path to the plugin asset (`.mm`, `.dylib`, `.dll`, `.so`, `.a`, `.bundle`, …).
- `platform` (required): `"AnyPlatform"`, `"Editor"`, or a `BuildTarget` enum name (`"StandaloneOSX"`, `"iOS"`, `"Android"`, `"StandaloneWindows64"`, …).
- `enable` (required): `"true"` / `"false"`.
- `dataKey` / `dataValue` (optional): per-platform data via `SetPlatformData` — e.g. `CPU` = `AnyCPU` / `x86_64` / `ARM64`, `OS` for Editor.
- Calls `SaveAndReimport()` — the rewritten `.meta` (with the `PluginImporter:` block) is what must be committed.
- Typical use: enable an Objective-C++ source plugin (`.mm`) for `StandaloneOSX` so its `extern "C"` symbols compile into the IL2CPP GameAssembly (fixes `Undefined symbols ... referenced from [DllImport("__Internal")]` link errors). One task per platform/flag — e.g. disable `AnyPlatform`, disable `Editor`, enable `StandaloneOSX`.

### GetPlayerPref
Read a key from Unity's `PlayerPrefs`. Returns a JSON object in `task.result`: `{"hasKey":bool,"key":"...","type":"...","value":...}`. When `hasKey:false`, no `type`/`value` fields are present.
```json
{
    "action": "GetPlayerPref",
    "parameters": [
        { "key": "key",  "value": "Undugu_Language" },
        { "key": "type", "value": "string" }
    ]
}
```
- `key` (required): the PlayerPrefs key name.
- `type` (optional, default `"string"`): `"string" | "int" | "float"` — PlayerPrefs only stores these three. Wrong type silently returns the platform default for that type.

### SetPlayerPref
Write a key to `PlayerPrefs` and `PlayerPrefs.Save()` immediately (important in the Editor where Save isn't automatic).
```json
{
    "action": "SetPlayerPref",
    "parameters": [
        { "key": "key",   "value": "Undugu_Language" },
        { "key": "value", "value": "en" },
        { "key": "type",  "value": "string" }
    ]
}
```
- `key`, `value` (required).
- `type` (optional, default `"string"`): same as `GetPlayerPref`. `value` is parsed accordingly.

### DeletePlayerPref
Remove a key (or all keys when `key="*"`) from `PlayerPrefs`. Idempotent.
```json
{
    "action": "DeletePlayerPref",
    "parameters": [
        { "key": "key", "value": "Undugu_Language" }
    ]
}
```
- `key` (required): the PlayerPrefs key to delete; pass `"*"` to call `PlayerPrefs.DeleteAll()` (use with care).

### ImportUnityPackage
Import a `.unitypackage` into the project via `AssetDatabase.ImportPackage` (non-interactive by default). Use for third-party SDK drops that are distributed as unitypackages (e.g. Google Sign-In, Facebook SDK).
```json
{
    "action": "ImportUnityPackage",
    "parameters": [
        { "key": "path", "value": "/absolute/or/project-relative/path/to/Plugin.unitypackage" }
    ]
}
```
- `path` (required): the `.unitypackage` file. Absolute paths are allowed (the file does not need to live inside the project).
- `interactive` (optional, default `"false"`): `"true"` shows Unity's import-selection dialog (blocks automation — avoid in scenarios).
- Result: `"Import requested: <file>"`. Completion/failure is logged (`✓ unitypackage import completed` / `✗ unitypackage import FAILED`).
- ⚠️ Script-bearing packages recompile/domain-reload AFTER the task completes — always follow the scenario with `quick_refresh.py full --focus --wait` + `monitor_editmode_logs.py --errors` before using the imported types.

## GameObject Path Resolution — Inactive Objects (2026-08-07)

All scene-context actions that take a `path` (SetProperty, GetProperty, InspectGameObject, SetActive, CallMethod, value-side scene-path references, …) resolve it via a shared finder:

1. Active objects resolve exactly like `GameObject.Find` (unchanged fast path).
2. **Full paths of the form `Root/Sub/Path` now ALSO resolve INACTIVE objects**: the root name is matched against every loaded scene's root GameObjects, then the rest of the path descends with `Transform.Find`, which includes inactive children. Use this to target disabled UI such as TMP dropdown `Template` objects or hidden panels.
3. **Bare single names remain active-only** — give the full path when the target may be inactive.
4. In Prefab Mode (`OpenPrefab`), paths resolve inside the open prefab (inactive included) as before.

## Common Mistakes to Avoid

❌ **WRONG:**
- `"gameObjectPath"` → Use `"path"`
- `"componentType"` → Use `"component"`
- `"parentPath"` → Use `"parent"`
- `"component": "Image"` → Use fully qualified: `"UnityEngine.UI.Image, UnityEngine.UI"`
- `"component": "UnityEngine.UI.Image"` → Missing assembly: `"UnityEngine.UI.Image, UnityEngine.UI"`
- `"type": "MyNamespace.ThemeManager"` → Missing assembly: `"MyNamespace.ThemeManager, Assembly-CSharp"`

✅ **RIGHT:**
- Always use exact parameter names from this reference
- AddComponent requires **full assembly-qualified names**: `"Namespace.Type, AssemblyName"`
- CreateScriptableObject requires **full assembly-qualified names**: `"Namespace.Type, AssemblyName"`
- Check TaskExecutor.cs if unsure
- Parameters are case-sensitive
