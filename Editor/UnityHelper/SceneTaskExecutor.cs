using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine.UI;
using TMPro;

namespace PerSpec.UnityHelper.Editor
{
    public class SceneTaskExecutor : BaseTaskExecutor
    {
        public override ExecutorType Type => ExecutorType.SCENE;
        
        private UnityEngine.SceneManagement.Scene currentScene;
        private bool _lastExecuteWasAsync;

        public override bool IsAsyncTask(Task task) => _lastExecuteWasAsync;

        public override bool Execute(Task task)
        {
            try
            {
                _lastExecuteWasAsync = false;
                switch (task.action)
                {
                    case "CreateScene":
                        return CreateScene(task);
                    case "LoadScene":
                        return LoadScene(task);
                    case "AddGameObject":
                        return AddGameObject(task);
                    case "AddComponent":
                        return AddComponent(task);
                    case "InstantiatePrefab":
                        return InstantiatePrefab(task);
                    case "SetProperty":
                        return SetPropertyUnified(task);
                    case "SetTransform":
                        return SetTransform(task);
                    case "SetRectTransform":
                        return SetRectTransform(task);
                    case "SaveScene":
                        return SaveScene(task);
                    case "CreateCanvas":
                        return CreateCanvas(task);
                    case "DeleteGameObject":
                        return DeleteGameObject(task);
                    case "AddToBuildSettings":
                        return AddToBuildSettings(task);
                    case "RenameGameObject":
                        return RenameGameObject(task);
                    case "CreateScriptableObject":
                        return CreateScriptableObject(task);
                    case "SaveAsPrefab":
                        return SaveAsPrefab(task);
                    case "ExportHierarchy":
                        return ExportHierarchy(task);
                    case "ExportHierarchyPrefab":
                        return ExportHierarchyPrefab(task);
                    case "ExportHierarchyScene":
                        return ExportHierarchyScene(task);
                    case "InspectGameObject":
                        return InspectGameObject(task);
                    case "SetListProperty":
                        return SetListProperty(task);
                    case "TakeScreenshot":
                        return TakeScreenshot(task);
                    case "WaitForGameObject":
                        return WaitForGameObject(task);
                    case "CallMethod":
                        return CallMethod(task);
                    case "SetParent":
                        return SetParent(task);
                    case "SetParentByTransform":
                        return SetParentByTransform(task);
                    case "OpenPrefab":
                        return OpenPrefab(task);
                    case "SavePrefab":
                        return SavePrefab(task);
                    case "ClosePrefab":
                        return ClosePrefab(task);
                    case "RemoveMissingScripts":
                        return RemoveMissingScripts(task);
                    case "ReportMissingScripts":
                        return ReportMissingScripts(task);
                    case "SetActive":
                        return SetActive(task);
                    case "SetSiblingIndex":
                        return SetSiblingIndex(task);
                    case "DuplicateGameObject":
                        return DuplicateGameObject(task);
                    case "MoveGameObject":
                        return MoveGameObject(task);
                    case "RemoveComponent":
                        return RemoveComponent(task);
                    case "BatchSetProperty":
                        return BatchSetProperty(task);
                    case "GetProperty":
                        return GetProperty(task);
                    case "FindObjects":
                        return FindObjects(task);
                    case "ApplyRecipe":
                        return ApplyRecipe(task);
                    case "SetPropertyOnMatching":
                        return SetPropertyOnMatching(task);
                    case "Validate":
                        return Validate(task);
                    case "WrapWithParent":
                        return WrapWithParent(task);
                    case "AddComponentToMatching":
                        return AddComponentToMatching(task);
                    case "SetImporterProperty":
                        return SetImporterProperty(task);
                    case "SetPluginPlatform":
                        return SetPluginPlatform(task);
                    case "GetPlayerPref":
                        return GetPlayerPref(task);
                    case "SetPlayerPref":
                        return SetPlayerPref(task);
                    case "DeletePlayerPref":
                        return DeletePlayerPref(task);
                    case "ImportUnityPackage":
                        return ImportUnityPackage(task);
                    default:
                        task.error = $"Unknown action: {task.action}";
                        return false;
                }
            }
            catch (Exception ex)
            {
                task.error = ex.Message;
                return false;
            }
        }

        // OpenPrefab opens the prefab in Unity's visible Prefab Stage so the user can SEE edits
        // happening live in the Editor. The PrefabStage handle is cached here so subsequent tasks
        // can find their targets without depending on PrefabStageUtility.GetCurrentPrefabStage()
        // (which returns null until Unity processes a focus change — usually the next frame).
        // SavePrefab persists the modified contents back to the .prefab asset and clears the cache.
        private static UnityEditor.SceneManagement.PrefabStage _openStage;
        private static string _openPrefabPath;

        /// <summary>
        /// Finds a GameObject by name or hierarchy path that respects the active editing context:
        /// 1) If OpenPrefab loaded a prefab into memory, search inside that prefab's contents first
        ///    and do NOT fall back to scene Find — edits in Prefab Mode must land in the prefab.
        /// 2) If a Prefab Stage is open in the Editor, use it instead.
        /// 3) Otherwise behaves identically to GameObject.Find against the active scenes.
        ///
        /// Path forms accepted (mirroring GameObject.Find):
        ///   - "Name"               — first match by name in the active context
        ///   - "Root/Child/Leaf"    — full path including the root name
        ///   - "Child/Leaf"         — relative to the prefab root (Prefab Mode only)
        /// </summary>
        private static GameObject FindInActiveContext(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            // Prefer the stage we explicitly opened — this avoids a one-frame delay where
            // GetCurrentPrefabStage() may not yet reflect a freshly-opened stage.
            GameObject root = null;
            if (_openStage != null) root = _openStage.prefabContentsRoot;
            if (root == null)
            {
                var stage = PrefabStageUtility.GetCurrentPrefabStage();
                if (stage != null) root = stage.prefabContentsRoot;
            }

            if (root != null)
            {
                if (path == root.name) return root;

                if (path.StartsWith(root.name + "/", StringComparison.Ordinal))
                {
                    string sub = path.Substring(root.name.Length + 1);
                    var t = root.transform.Find(sub);
                    if (t != null) return t.gameObject;
                }

                var rel = root.transform.Find(path);
                if (rel != null) return rel.gameObject;

                if (path.IndexOf('/') < 0)
                {
                    foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                    {
                        if (t.name == path) return t.gameObject;
                    }
                }
                return null;
            }

            return GameObject.Find(path);
        }

        private bool CreateScene(Task task)
        {
            string name = GetParam(task, "name");
            string scenePath = $"Assets/Scenes/{name}.unity";
            
            if (System.IO.File.Exists(scenePath))
            {
                currentScene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
                Debug.Log($"[SceneTaskExecutor]Scene already exists, opened: {name}");
                return true;
            }
            
            currentScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Debug.Log($"[SceneTaskExecutor]Created scene: {name} (empty - no camera)");
            return true;
        }

        private bool AddGameObject(Task task)
        {
            string name = GetParam(task, "name");
            string parent = GetOptionalParam(task, "parent");

            var fullPath = string.IsNullOrEmpty(parent) ? name : $"{parent}/{name}";

            if (FindInActiveContext(fullPath) != null)
            {
                Debug.Log($"[SceneTaskExecutor]GameObject already exists: {fullPath}");
                return true;
            }

            var go = new GameObject(name);

            if (!string.IsNullOrEmpty(parent))
            {
                var parentGO = FindInActiveContext(parent);
                if (parentGO == null)
                {
                    task.error = $"Parent GameObject not found: {parent}";
                    GameObject.DestroyImmediate(go);
                    return false;
                }

                // If parent has Canvas or RectTransform, add RectTransform to child
                if (parentGO.GetComponent<Canvas>() != null || parentGO.GetComponent<RectTransform>() != null)
                {
                    go.AddComponent<RectTransform>();
                }

                go.transform.SetParent(parentGO.transform, false);
            }

            // VERIFY: Confirm GameObject was created and is findable
            if (FindInActiveContext(fullPath) == null)
            {
                task.error = $"GameObject creation verification failed: {fullPath}";
                return false;
            }

            Debug.Log($"[SceneTaskExecutor]Created GameObject: {name}" + (parent != null ? $" under {parent}" : "") + " ✓");
            return true;
        }

        // Other methods would be implemented similarly...
        // For brevity, I'm showing only a couple of methods
        // Each method should follow the same pattern:
        // 1. Get parameters using GetParam/GetOptionalParam
        // 2. Perform the operation
        // 3. Return true on success, false on failure with task.error set
        
        // Placeholder for other methods - they would be implemented similarly
        private bool LoadScene(Task task) 
        { 
            string path = GetParam(task, "path");
            
            if (!System.IO.File.Exists(path))
            {
                task.error = $"Scene file not found: {path}";
                return false;
            }
            
            currentScene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            
            // TODO: VERIFY scene is actually active and loaded
            // if (EditorSceneManager.GetActiveScene().path != path) return false;
            
            Debug.Log($"[SceneTaskExecutor]Loaded scene: {path}");
            return true;
        }
        /// <summary>
        /// Imports a .unitypackage into the project (non-interactive by default). Absolute
        /// or project-relative path. Script-bearing packages trigger a recompile/domain
        /// reload AFTER import — callers should follow the scenario with a quick_refresh +
        /// compile-error check before relying on the imported types.
        /// </summary>
        private bool ImportUnityPackage(Task task)
        {
            string path = GetParam(task, "path");
            bool interactive = GetOptionalParam(task, "interactive", "false").ToLowerInvariant() == "true";

            if (!System.IO.File.Exists(path))
            {
                task.error = $"Package file not found: {path}";
                return false;
            }

            AssetDatabase.importPackageCompleted += OnImportPackageCompleted;
            AssetDatabase.importPackageFailed += OnImportPackageFailed;
            AssetDatabase.ImportPackage(path, interactive);

            task.result = $"Import requested: {System.IO.Path.GetFileName(path)}";
            Debug.Log($"[SceneTaskExecutor]Importing unitypackage: {path} (interactive={interactive})");
            return true;
        }

        private static void OnImportPackageCompleted(string packageName)
        {
            AssetDatabase.importPackageCompleted -= OnImportPackageCompleted;
            Debug.Log($"[SceneTaskExecutor]✓ unitypackage import completed: {packageName}");
        }

        private static void OnImportPackageFailed(string packageName, string errorMessage)
        {
            AssetDatabase.importPackageFailed -= OnImportPackageFailed;
            Debug.LogError($"[SceneTaskExecutor]✗ unitypackage import FAILED: {packageName} — {errorMessage}");
        }

        private bool AddComponent(Task task)
        {
            string path = GetParam(task, "path");
            string component = GetParam(task, "component");

            var go = FindInActiveContext(path);
            if (go == null)
            {
                task.error = $"GameObject not found: {path}";
                return false;
            }

            // Resolve the component type - search all loaded assemblies
            System.Type componentType = ResolveType(component);
            if (componentType == null)
            {
                task.error = $"Component type not found: {component}. Use fully qualified name: 'Namespace.Type, AssemblyName'";
                return false;
            }

            // Check if component already exists (idempotent)
            if (go.GetComponent(componentType) != null)
            {
                Debug.Log($"[SceneTaskExecutor]Component already exists: {component} on {path}");
                return true;
            }

            go.AddComponent(componentType);
            
            // VERIFY: Confirm component was added
            if (go.GetComponent(componentType) == null)
            {
                task.error = $"Component addition verification failed: {component} on {path}";
                return false;
            }

            Debug.Log($"[SceneTaskExecutor]Added component: {component} to {path} ✓");
            return true;
        }

        private bool SetParent(Task task)
        {
            string path = GetParam(task, "path");
            string parent = GetParam(task, "parent");
            string worldPositionStaysStr = GetOptionalParam(task, "worldPositionStays", "false");
            bool worldPositionStays = bool.Parse(worldPositionStaysStr);

            var go = FindInActiveContext(path);
            if (go == null)
            {
                task.error = $"GameObject not found: {path}";
                return false;
            }

            if (string.IsNullOrEmpty(parent))
            {
                go.transform.SetParent(null, worldPositionStays);
            }
            else
            {
                var parentGo = FindInActiveContext(parent);
                if (parentGo == null)
                {
                    task.error = $"Parent GameObject not found: {parent}";
                    return false;
                }
                go.transform.SetParent(parentGo.transform, worldPositionStays);
            }

            Debug.Log($"[SceneTaskExecutor] SetParent: {path} → {parent ?? "root"} ✓");
            return true;
        }

        private bool InstantiatePrefab(Task task)
        { 
            string prefabPath = GetParam(task, "prefabPath");
            string name = GetParam(task, "name");
            string parent = GetOptionalParam(task, "parent");

            // Check if already exists (idempotent)
            if (FindInActiveContext(name) != null)
            {
                Debug.Log($"[SceneTaskExecutor]GameObject already exists: {name}");
                return true;
            }

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
            {
                task.error = $"Prefab not found at: {prefabPath}";
                return false;
            }

            // Use PrefabUtility to maintain prefab connection
            var instance = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
            if (instance == null)
            {
                task.error = $"Failed to instantiate prefab: {prefabPath}";
                return false;
            }
            instance.name = name;

            if (!string.IsNullOrEmpty(parent))
            {
                var parentGO = FindInActiveContext(parent);
                if (parentGO == null)
                {
                    task.error = $"Parent GameObject not found: {parent}";
                    return false;
                }
                instance.transform.SetParent(parentGO.transform, false);
            }

            // VERIFY: Confirm instantiation succeeded
            var verifyPath = string.IsNullOrEmpty(parent) ? name : $"{parent}/{name}";
            if (FindInActiveContext(verifyPath) == null)
            {
                task.error = $"Prefab instantiation verification failed: {verifyPath}";
                return false;
            }

            Debug.Log($"[SceneTaskExecutor]Instantiated prefab: {name}" + (parent != null ? $" under {parent}" : "") + " ✓");
            return true;
        }
        private bool SetPropertyUnified(Task task)
        {
            string ownerPath = GetOptionalParam(task, "owner");
            string goPath = GetOptionalParam(task, "path");
            string componentName = GetOptionalParam(task, "component");
            string fieldName = GetParam(task, "field");
            string valuePath = GetParam(task, "value");

            if (string.IsNullOrEmpty(fieldName))
            {
                task.error = "SetProperty requires 'field' parameter";
                return false;
            }

            if (valuePath == null)
            {
                task.error = "SetProperty requires 'value' parameter";
                return false;
            }

            // Case 1: Prefab asset (owner is .prefab + path + component provided)
            if (!string.IsNullOrEmpty(ownerPath) && ownerPath.EndsWith(".prefab") &&
                !string.IsNullOrEmpty(goPath) && !string.IsNullOrEmpty(componentName))
            {
                return SetPropertyOnPrefab(task, ownerPath, goPath, componentName, fieldName, valuePath);
            }

            // Case 2: Scene file (owner is .unity + path + component provided)
            if (!string.IsNullOrEmpty(ownerPath) && ownerPath.EndsWith(".unity") &&
                !string.IsNullOrEmpty(goPath) && !string.IsNullOrEmpty(componentName))
            {
                return SetPropertyOnSceneFile(task, ownerPath, goPath, componentName, fieldName, valuePath);
            }

            // Case 3: Scene GameObject (path + component provided, no owner)
            if (string.IsNullOrEmpty(ownerPath) && !string.IsNullOrEmpty(goPath) && !string.IsNullOrEmpty(componentName))
            {
                return SetPropertyOnGameObject(task, goPath, componentName, fieldName, valuePath);
            }

            // Case 4: ScriptableObject asset (owner provided, no path/component)
            if (!string.IsNullOrEmpty(ownerPath))
            {
                return SetPropertyOnScriptableObject(task, ownerPath, fieldName, valuePath);
            }

            task.error = "SetProperty requires either 'path' + 'component' (for scene GameObjects) or 'owner' (for ScriptableObject/Prefab/Scene assets)";
            return false;
        }

        private bool SetPropertyOnPrefab(Task task, string prefabPath, string goPath, string componentName, string fieldName, string valuePath)
        {
            var prefabRoot = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefabRoot == null)
            {
                task.error = $"Prefab not found: {prefabPath}";
                return false;
            }

            // Find child by name (supports root name or child name)
            GameObject target = null;
            if (prefabRoot.name == goPath)
            {
                target = prefabRoot;
            }
            else
            {
                var allChildren = prefabRoot.GetComponentsInChildren<Transform>(true);
                foreach (var t in allChildren)
                {
                    if (t.name == goPath)
                    {
                        target = t.gameObject;
                        break;
                    }
                }
            }

            if (target == null)
            {
                task.error = $"GameObject '{goPath}' not found in prefab: {prefabPath}";
                return false;
            }

            System.Type componentType = ResolveType(componentName);
            if (componentType == null)
            {
                task.error = $"Component type not found: {componentName}";
                return false;
            }

            var component = target.GetComponent(componentType);
            if (component == null)
            {
                task.error = $"Component {componentName} not found on '{goPath}' in prefab {prefabPath}";
                return false;
            }

            var field = componentType.GetField(fieldName,
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);

            if (field == null)
            {
                task.error = $"Field '{fieldName}' not found on {componentName}";
                return false;
            }

            // Resolve value — for UnityObject references, search within the prefab first
            object value;
            if (typeof(UnityEngine.Object).IsAssignableFrom(field.FieldType))
            {
                // Try to find as child GO in the same prefab
                GameObject refGo = null;
                var allChildren = prefabRoot.GetComponentsInChildren<Transform>(true);
                foreach (var t in allChildren)
                {
                    if (t.name == valuePath)
                    {
                        refGo = t.gameObject;
                        break;
                    }
                }

                if (refGo != null)
                {
                    if (field.FieldType == typeof(GameObject))
                        value = refGo;
                    else if (typeof(Component).IsAssignableFrom(field.FieldType))
                        value = refGo.GetComponent(field.FieldType);
                    else
                        value = refGo;
                }
                else
                {
                    value = AssetDatabase.LoadAssetAtPath(valuePath, field.FieldType);
                }
            }
            else
            {
                value = ConvertValue(field.FieldType, valuePath);
            }

            field.SetValue(component, value);
            EditorUtility.SetDirty(prefabRoot);
            PrefabUtility.SavePrefabAsset(prefabRoot);
            Debug.Log($"[SceneTaskExecutor] SetPropertyOnPrefab: {prefabPath}/{goPath}/{componentName}.{fieldName} = {valuePath} ✓");
            return true;
        }

        private bool SetPropertyOnSceneFile(Task task, string scenePath, string goPath, string componentName, string fieldName, string valuePath)
        {
            var previousScene = EditorSceneManager.GetActiveScene();
            string previousScenePath = previousScene.path;

            var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);

            var go = FindInActiveContext(goPath);
            if (go == null)
            {
                if (!string.IsNullOrEmpty(previousScenePath))
                    EditorSceneManager.OpenScene(previousScenePath, OpenSceneMode.Single);
                task.error = $"GameObject not found in scene '{scenePath}': {goPath}";
                return false;
            }

            bool result = SetPropertyOnGameObject(task, goPath, componentName, fieldName, valuePath);

            if (result)
            {
                EditorSceneManager.SaveScene(scene);
                Debug.Log($"[SceneTaskExecutor] SetPropertyOnSceneFile: {scenePath}/{goPath}/{componentName}.{fieldName} = {valuePath} ✓");
            }

            if (!string.IsNullOrEmpty(previousScenePath))
                EditorSceneManager.OpenScene(previousScenePath, OpenSceneMode.Single);

            return result;
        }

        private bool SetPropertyOnGameObject(Task task, string goPath, string componentName, string fieldName, string valuePath)
        {
            var go = FindInActiveContext(goPath);
            if (go == null)
            {
                task.error = $"GameObject not found: {goPath}";
                return false;
            }

            // Get component type - use ResolveType to search all assemblies including Assembly-CSharp
            System.Type componentType = ResolveType(componentName);
            if (componentType == null)
            {
                task.error = $"Component type not found: {componentName}. Use fully qualified name: 'Namespace.Type, AssemblyName'";
                return false;
            }

            var component = go.GetComponent(componentType);
            if (component == null)
            {
                task.error = $"Component {componentName} not found on {goPath}";
                return false;
            }

            // Handle sprite field specially for Image component
            if (fieldName == "sprite" && component is Image image)
            {
                var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(valuePath);
                if (sprite == null)
                {
                    task.error = $"Sprite not found: {valuePath}";
                    return false;
                }
                image.sprite = sprite;
                EditorUtility.SetDirty(go);
                PrefabUtility.RecordPrefabInstancePropertyModifications(component);
                Debug.Log($"[SceneTaskExecutor] SetProperty: {goPath}/{componentName}.{fieldName} = {valuePath} ✓");
                return true;
            }

            // Handle color field — supports named colors, hex, and comma-separated RGBA via ParseColor
            if (fieldName == "color" && component is Graphic graphic)
            {
                graphic.color = ParseColor(valuePath);
                EditorUtility.SetDirty(go);
                PrefabUtility.RecordPrefabInstancePropertyModifications(component);
                Debug.Log($"[SceneTaskExecutor] SetProperty: {goPath}/{componentName}.{fieldName} = {valuePath} ✓");
                return true;
            }

            // Use reflection for other fields
            var field = componentType.GetField(fieldName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var property = componentType.GetProperty(fieldName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            if (field != null)
            {
                object value = ConvertValue(field.FieldType, valuePath);
                field.SetValue(component, value);
                EditorUtility.SetDirty(go);
                PrefabUtility.RecordPrefabInstancePropertyModifications(component);
                Debug.Log($"[SceneTaskExecutor] SetProperty: {goPath}/{componentName}.{fieldName} = {valuePath} ✓");
                return true;
            }
            else if (property != null && property.CanWrite)
            {
                object value = ConvertValue(property.PropertyType, valuePath);
                property.SetValue(component, value);
                EditorUtility.SetDirty(go);
                PrefabUtility.RecordPrefabInstancePropertyModifications(component);
                Debug.Log($"[SceneTaskExecutor] SetProperty: {goPath}/{componentName}.{fieldName} = {valuePath} ✓");
                return true;
            }

            task.error = $"Field or property '{fieldName}' not found on {componentName}";
            return false;
        }

        private object ConvertValue(System.Type targetType, string valuePath)
        {
            if (targetType == typeof(Sprite))
                return AssetDatabase.LoadAssetAtPath<Sprite>(valuePath);
            if (targetType == typeof(bool))
                return bool.Parse(valuePath);
            if (targetType == typeof(int))
                return int.Parse(valuePath);
            if (targetType == typeof(uint))
                return uint.Parse(valuePath);
            if (targetType == typeof(long))
                return long.Parse(valuePath);
            if (targetType == typeof(float))
                return float.Parse(valuePath);
            if (targetType == typeof(double))
                return double.Parse(valuePath);
            if (targetType == typeof(string))
                return valuePath;
            if (targetType == typeof(Color))
                return ParseColor(valuePath);

            if (targetType == typeof(Vector2))
            {
                var parts = valuePath.Split(',');
                return new Vector2(float.Parse(parts[0]), float.Parse(parts[1]));
            }
            if (targetType == typeof(Vector3))
            {
                var parts = valuePath.Split(',');
                return new Vector3(float.Parse(parts[0]), float.Parse(parts[1]), parts.Length > 2 ? float.Parse(parts[2]) : 0f);
            }
            if (targetType == typeof(Vector4))
            {
                var parts = valuePath.Split(',');
                return new Vector4(float.Parse(parts[0]), float.Parse(parts[1]), parts.Length > 2 ? float.Parse(parts[2]) : 0f, parts.Length > 3 ? float.Parse(parts[3]) : 0f);
            }
            if (targetType == typeof(Vector2Int))
            {
                var parts = valuePath.Split(',');
                return new Vector2Int(int.Parse(parts[0]), int.Parse(parts[1]));
            }

            // Handle enums with shorthand support (e.g., TextAlignmentOptions, FontStyles)
            if (targetType.IsEnum)
            {
                // TMP TextAlignmentOptions shorthand: "center", "topleft", etc.
                if (targetType.FullName == "TMPro.TextAlignmentOptions")
                {
                    var aligned = ParseTextAlignment(valuePath);
                    if (aligned != null) return aligned;
                }
                return Enum.Parse(targetType, valuePath);
            }

            // Try finding as scene GameObject first, then as asset
            if (typeof(UnityEngine.Object).IsAssignableFrom(targetType))
            {
                // For Component types (RectTransform, TMP_Text, etc.), try scene lookup
                if (typeof(Component).IsAssignableFrom(targetType))
                {
                    var sceneGo = FindInActiveContext(valuePath);
                    if (sceneGo != null)
                        return sceneGo.GetComponent(targetType);
                }

                // For GameObject type
                if (targetType == typeof(GameObject))
                {
                    var sceneGo = FindInActiveContext(valuePath);
                    if (sceneGo != null)
                        return sceneGo;
                }

                // Fall back to asset loading
                return AssetDatabase.LoadAssetAtPath(valuePath, targetType);
            }

            return valuePath;
        }

        private bool SetPropertyOnScriptableObject(Task task, string ownerPath, string fieldName, string valuePath)
        {
            // Load the ScriptableObject asset
            var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(ownerPath);
            if (asset == null)
            {
                task.error = $"ScriptableObject not found: {ownerPath}";
                return false;
            }

            // Use reflection to get the field (check both public and private)
            var assetType = asset.GetType();
            var field = assetType.GetField(fieldName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            if (field == null)
            {
                task.error = $"Field '{fieldName}' not found in {assetType.Name}";
                return false;
            }

            // Handle different field types
            object valueToSet = null;

            if (field.FieldType == typeof(Sprite))
            {
                valueToSet = AssetDatabase.LoadAssetAtPath<Sprite>(valuePath);
                if (valueToSet == null)
                {
                    task.error = $"Sprite not found: {valuePath}";
                    return false;
                }
            }
            else if (field.FieldType == typeof(SceneAsset))
            {
                valueToSet = AssetDatabase.LoadAssetAtPath<SceneAsset>(valuePath);
                if (valueToSet == null)
                {
                    task.error = $"Scene not found: {valuePath}";
                    return false;
                }
            }
            else if (typeof(ScriptableObject).IsAssignableFrom(field.FieldType))
            {
                valueToSet = AssetDatabase.LoadAssetAtPath<ScriptableObject>(valuePath);
                if (valueToSet == null)
                {
                    task.error = $"ScriptableObject not found: {valuePath}";
                    return false;
                }
            }
            // Handle nested class with sceneAsset field (like SceneReference)
            else if (field.FieldType.IsClass && valuePath.EndsWith(".unity"))
            {
                var sceneAssetField = field.FieldType.GetField("sceneAsset",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

                if (sceneAssetField != null && sceneAssetField.FieldType == typeof(SceneAsset))
                {
                    var sceneAsset = AssetDatabase.LoadAssetAtPath<SceneAsset>(valuePath);
                    if (sceneAsset == null)
                    {
                        task.error = $"Scene not found: {valuePath}";
                        return false;
                    }

                    // Create instance of the nested type and set sceneAsset
                    valueToSet = Activator.CreateInstance(field.FieldType);
                    sceneAssetField.SetValue(valueToSet, sceneAsset);

                    // Also set cached sceneName if exists
                    var sceneNameField = field.FieldType.GetField("sceneName",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (sceneNameField != null)
                    {
                        sceneNameField.SetValue(valueToSet, sceneAsset.name);
                    }
                }
                else
                {
                    task.error = $"Unsupported field type: {field.FieldType.Name}";
                    return false;
                }
            }
            else
            {
                // Fall back to ConvertValue for primitives, string, enum, Vector*, Color, etc.
                // Generalizes the SO-property path to anything ConvertValue handles.
                try
                {
                    valueToSet = ConvertValue(field.FieldType, valuePath);
                }
                catch (Exception ex)
                {
                    task.error = $"Unsupported field type: {field.FieldType.Name} ({ex.Message})";
                    return false;
                }
            }

            // Set the field value
            field.SetValue(asset, valueToSet);

            // Mark as dirty and save
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();

            Debug.Log($"[SceneTaskExecutor]SetProperty: {fieldName} = {valuePath} ✓");
            return true;
        }
        
        private bool SetTransform(Task task) 
        { 
            string path = GetParam(task, "path");
            var go = FindInActiveContext(path);
            if (go == null)
            {
                task.error = $"GameObject not found: {path}";
                return false;
            }

            string position = GetOptionalParam(task, "position");
            if (!string.IsNullOrEmpty(position))
                go.transform.localPosition = ParseVector3(position);
                
            string rotation = GetOptionalParam(task, "rotation");
            if (!string.IsNullOrEmpty(rotation))
                go.transform.localEulerAngles = ParseVector3(rotation);
                
            string scale = GetOptionalParam(task, "scale");
            if (!string.IsNullOrEmpty(scale))
                go.transform.localScale = ParseVector3(scale);
                
            string siblingIndex = GetOptionalParam(task, "siblingIndex");
            if (!string.IsNullOrEmpty(siblingIndex))
            {
                int index = int.Parse(siblingIndex);
                if (index == -1)
                    go.transform.SetAsLastSibling();
                else if (index == 0)
                    go.transform.SetAsFirstSibling();
                else
                    go.transform.SetSiblingIndex(index);
            }

            // TODO: VERIFY transform values were actually set
            // Read back and compare to ensure values match what was requested
            
            Debug.Log($"[SceneTaskExecutor]Set transform on {path}");
            return true;
        }
        
        private Vector3 ParseVector3(string value)
        {
            var parts = value.Split(',');
            if (parts.Length != 3)
                throw new Exception($"Invalid Vector3 format: {value}");
            return new Vector3(float.Parse(parts[0]), float.Parse(parts[1]), float.Parse(parts[2]));
        }
        private bool SetRectTransform(Task task)
        {
            string path = GetParam(task, "path");
            var go = FindInActiveContext(path);
            if (go == null)
            {
                task.error = $"GameObject not found: {path}";
                return false;
            }

            var rectTransform = go.GetComponent<RectTransform>();
            if (rectTransform == null)
            {
                task.error = $"RectTransform component not found on: {path}";
                return false;
            }

            // Anchor preset shorthand (apply before individual values so explicit anchorMin/Max can override)
            string anchorPreset = GetOptionalParam(task, "anchorPreset");
            if (!string.IsNullOrEmpty(anchorPreset))
                ApplyAnchorPreset(rectTransform, anchorPreset);

            string anchorMin = GetOptionalParam(task, "anchorMin");
            string anchorMax = GetOptionalParam(task, "anchorMax");
            string offsetMin = GetOptionalParam(task, "offsetMin");
            string offsetMax = GetOptionalParam(task, "offsetMax");
            string anchoredPosition = GetOptionalParam(task, "anchoredPosition");
            string sizeDelta = GetOptionalParam(task, "sizeDelta");
            string pivot = GetOptionalParam(task, "pivot");

            // Width/height shortcuts (set sizeDelta partially)
            string width = GetOptionalParam(task, "width");
            string height = GetOptionalParam(task, "height");
            if (!string.IsNullOrEmpty(width) || !string.IsNullOrEmpty(height))
            {
                float w = !string.IsNullOrEmpty(width) ? float.Parse(width) : rectTransform.sizeDelta.x;
                float h = !string.IsNullOrEmpty(height) ? float.Parse(height) : rectTransform.sizeDelta.y;
                rectTransform.sizeDelta = new Vector2(w, h);
            }

            // Margin shorthand: 1 value = uniform, 4 values = left,top,right,bottom
            string margin = GetOptionalParam(task, "margin");
            if (!string.IsNullOrEmpty(margin))
            {
                var parts = margin.Split(',');
                if (parts.Length == 1)
                {
                    float m = float.Parse(parts[0]);
                    rectTransform.offsetMin = new Vector2(m, m);
                    rectTransform.offsetMax = new Vector2(-m, -m);
                }
                else if (parts.Length == 4)
                {
                    float left = float.Parse(parts[0]), top = float.Parse(parts[1]);
                    float right = float.Parse(parts[2]), bottom = float.Parse(parts[3]);
                    rectTransform.offsetMin = new Vector2(left, bottom);
                    rectTransform.offsetMax = new Vector2(-right, -top);
                }
            }

            if (!string.IsNullOrEmpty(anchorMin)) rectTransform.anchorMin = ParseVector2(anchorMin);
            if (!string.IsNullOrEmpty(anchorMax)) rectTransform.anchorMax = ParseVector2(anchorMax);
            if (!string.IsNullOrEmpty(offsetMin)) rectTransform.offsetMin = ParseVector2(offsetMin);
            if (!string.IsNullOrEmpty(offsetMax)) rectTransform.offsetMax = ParseVector2(offsetMax);
            if (!string.IsNullOrEmpty(anchoredPosition)) rectTransform.anchoredPosition = ParseVector2(anchoredPosition);
            if (!string.IsNullOrEmpty(sizeDelta)) rectTransform.sizeDelta = ParseVector2(sizeDelta);
            if (!string.IsNullOrEmpty(pivot)) rectTransform.pivot = ParseVector2(pivot);

            EditorUtility.SetDirty(go);
            // Record prefab-instance property modifications so overrides persist to the scene YAML.
            // Without this, reflection-style edits don't appear in PrefabInstance.m_Modifications and
            // are lost on scene reload.
            PrefabUtility.RecordPrefabInstancePropertyModifications(rectTransform);
            Debug.Log($"[SceneTaskExecutor]Set RectTransform on {path} ✓");
            return true;
        }

        private void ApplyAnchorPreset(RectTransform rt, string preset)
        {
            switch (preset.ToLower().Replace(" ", "").Replace("-", ""))
            {
                case "stretch":
                    rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
                    rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
                    break;
                case "stretchhorizontal":
                    rt.anchorMin = new Vector2(0, 0.5f); rt.anchorMax = new Vector2(1, 0.5f);
                    break;
                case "stretchvertical":
                    rt.anchorMin = new Vector2(0.5f, 0); rt.anchorMax = new Vector2(0.5f, 1);
                    break;
                case "topleft":
                    rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(0, 1);
                    rt.pivot = new Vector2(0, 1);
                    break;
                case "topcenter": case "top":
                    rt.anchorMin = new Vector2(0.5f, 1); rt.anchorMax = new Vector2(0.5f, 1);
                    rt.pivot = new Vector2(0.5f, 1);
                    break;
                case "topright":
                    rt.anchorMin = new Vector2(1, 1); rt.anchorMax = new Vector2(1, 1);
                    rt.pivot = new Vector2(1, 1);
                    break;
                case "middleleft": case "left":
                    rt.anchorMin = new Vector2(0, 0.5f); rt.anchorMax = new Vector2(0, 0.5f);
                    rt.pivot = new Vector2(0, 0.5f);
                    break;
                case "center": case "middle":
                    rt.anchorMin = new Vector2(0.5f, 0.5f); rt.anchorMax = new Vector2(0.5f, 0.5f);
                    rt.pivot = new Vector2(0.5f, 0.5f);
                    break;
                case "middleright": case "right":
                    rt.anchorMin = new Vector2(1, 0.5f); rt.anchorMax = new Vector2(1, 0.5f);
                    rt.pivot = new Vector2(1, 0.5f);
                    break;
                case "bottomleft":
                    rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.zero;
                    rt.pivot = Vector2.zero;
                    break;
                case "bottomcenter": case "bottom":
                    rt.anchorMin = new Vector2(0.5f, 0); rt.anchorMax = new Vector2(0.5f, 0);
                    rt.pivot = new Vector2(0.5f, 0);
                    break;
                case "bottomright":
                    rt.anchorMin = new Vector2(1, 0); rt.anchorMax = new Vector2(1, 0);
                    rt.pivot = new Vector2(1, 0);
                    break;
                case "topstretch":
                    rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(1, 1);
                    rt.pivot = new Vector2(0.5f, 1);
                    break;
                case "bottomstretch":
                    rt.anchorMin = new Vector2(0, 0); rt.anchorMax = new Vector2(1, 0);
                    rt.pivot = new Vector2(0.5f, 0);
                    break;
                default:
                    Debug.LogWarning($"[SceneTaskExecutor] Unknown anchor preset: {preset}");
                    break;
            }
        }

        private Vector2 ParseVector2(string value)
        {
            var parts = value.Split(',');
            if (parts.Length != 2)
                throw new Exception($"Invalid Vector2 format: {value}");
            return new Vector2(float.Parse(parts[0]), float.Parse(parts[1]));
        }
        private bool SaveScene(Task task)
        {
            string path = GetParam(task, "path");
            var activeScene = EditorSceneManager.GetActiveScene();

            // Ensure directory exists
            var directory = System.IO.Path.GetDirectoryName(path);
            if (!System.IO.Directory.Exists(directory))
            {
                System.IO.Directory.CreateDirectory(directory);
            }

            // Save scene to specified path (no dialog)
            bool saved = EditorSceneManager.SaveScene(activeScene, path);

            // VERIFY: Confirm save succeeded
            if (!saved)
            {
                task.error = $"Scene save failed: {path}";
                return false;
            }

            Debug.Log($"[SceneTaskExecutor]Saved scene: {path} ✓");
            return true;
        }
        private bool CreateCanvas(Task task) 
        { 
            string name = GetParam(task, "name");
            
            // Check if Canvas already exists (idempotent)
            var existing = FindInActiveContext(name);
            if (existing != null)
            {
                Debug.Log($"[SceneTaskExecutor]Canvas already exists: {name}");
                return true;
            }
            
            var canvasGO = new GameObject(name);
            var canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            
            var scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            
            canvasGO.AddComponent<GraphicRaycaster>();
            
            // TODO: VERIFY Canvas was created and has all required components
            // if (FindInActiveContext(name)?.GetComponent<Canvas>() == null) return false;
            
            Debug.Log($"[SceneTaskExecutor]Created Canvas: {name}");
            return true;
        }
        private bool DeleteGameObject(Task task) 
        { 
            string path = GetParam(task, "path");
            var go = FindInActiveContext(path);
            if (go == null)
            {
                Debug.Log($"[SceneTaskExecutor]GameObject already deleted or not found: {path}");
                return true;
            }
            
            GameObject.DestroyImmediate(go);
            
            // VERIFY: Confirm deletion succeeded
            if (FindInActiveContext(path) != null)
            {
                task.error = $"GameObject deletion verification failed: {path} still exists";
                return false;
            }
            
            Debug.Log($"[SceneTaskExecutor]Deleted GameObject: {path} ✓");
            return true;
        }
        private bool AddToBuildSettings(Task task) 
        { 
            string scenePath = GetParam(task, "scenePath");
            
            if (!System.IO.File.Exists(scenePath))
            {
                Debug.LogWarning($"[SceneTaskExecutor]Scene file not found: {scenePath}. Skipping AddToBuildSettings.");
                return true;
            }
            
            // Get current build settings scenes
            var scenes = new System.Collections.Generic.List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            
            // Check if scene already exists (idempotent)
            bool sceneExists = scenes.Exists(s => s.path == scenePath);
            if (sceneExists)
            {
                Debug.Log($"[SceneTaskExecutor]Scene already in Build Settings: {scenePath}");
                return true;
            }
            
            // Add new scene
            scenes.Add(new EditorBuildSettingsScene(scenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();
            
            // TODO: VERIFY scene is actually in build settings after adding
            // Re-check EditorBuildSettings.scenes to confirm it's there
            
            Debug.Log($"[SceneTaskExecutor]Added to Build Settings: {scenePath}");
            return true;
        }
        
        private bool RenameGameObject(Task task) 
        { 
            string path = GetParam(task, "path");
            string newName = GetParam(task, "newName");
            
            var go = FindInActiveContext(path);
            if (go == null)
            {
                // Check if it's already renamed (idempotent)
                var alreadyRenamed = FindInActiveContext(newName);
                if (alreadyRenamed != null)
                {
                    Debug.Log($"[SceneTaskExecutor]GameObject already renamed to: {newName}");
                    return true;
                }
                task.error = $"GameObject not found: {path}";
                return false;
            }
            
            go.name = newName;
            
            // TODO: VERIFY GameObject is findable by new name and not by old name
            // if (FindInActiveContext(newName) == null || FindInActiveContext(path) != null) return false;
            
            Debug.Log($"[SceneTaskExecutor]Renamed GameObject: {path} → {newName}");
            return true;
        }
        
        private bool CreateScriptableObject(Task task) 
        { 
            string typeName = GetParam(task, "type");
            string path = GetParam(task, "path");
            
            // Check if asset already exists (idempotent)
            if (AssetDatabase.LoadAssetAtPath<ScriptableObject>(path) != null)
            {
                Debug.Log($"[SceneTaskExecutor]ScriptableObject already exists: {path}");
                return true;
            }
            
            // Find the type - search all loaded assemblies
            System.Type type = null;
            
            // First try direct GetType
            type = System.Type.GetType(typeName);
            
            // If not found, search all loaded assemblies
            if (type == null)
            {
                foreach (var assembly in System.AppDomain.CurrentDomain.GetAssemblies())
                {
                    type = assembly.GetType(typeName.Split(',')[0].Trim()); // Get type name without assembly part
                    if (type != null)
                        break;
                }
            }
            
            // Validate it's a ScriptableObject
            if (type == null || !typeof(ScriptableObject).IsAssignableFrom(type))
            {
                task.error = $"ScriptableObject type not found or invalid: {typeName}";
                return false;
            }
            
            // Create the asset
            ScriptableObject asset = ScriptableObject.CreateInstance(type);
            
            // Ensure directory exists
            string directory = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !System.IO.Directory.Exists(directory))
            {
                System.IO.Directory.CreateDirectory(directory);
            }
            
            // Create and save the asset
            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            
            // TODO: VERIFY ScriptableObject exists at path and is correct type
            // if (AssetDatabase.LoadAssetAtPath<ScriptableObject>(path) == null) return false;
            
            Debug.Log($"[SceneTaskExecutor]Created ScriptableObject: {path} (type: {typeName})");
            return true;
        }
        
        private bool SaveAsPrefab(Task task) 
        { 
            string path = GetParam(task, "path");
            string prefabPath = GetParam(task, "prefabPath");
            
            // Find GameObject in scene
            GameObject go = FindInActiveContext(path);
            if (go == null)
            {
                task.error = $"GameObject not found: {path}";
                return false;
            }
            
            // Ensure directory exists
            string directory = System.IO.Path.GetDirectoryName(prefabPath);
            if (!System.IO.Directory.Exists(directory))
            {
                System.IO.Directory.CreateDirectory(directory);
                AssetDatabase.Refresh();
            }
            
            // Check if prefab already exists
            if (System.IO.File.Exists(prefabPath))
            {
                Debug.Log($"[SceneTaskExecutor]Prefab already exists, replacing: {prefabPath}");
                AssetDatabase.DeleteAsset(prefabPath);
            }
            
            // Save as prefab
            PrefabUtility.SaveAsPrefabAsset(go, prefabPath);
            
            // TODO: VERIFY prefab file exists and is loadable
            // if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) == null) return false;
            
            Debug.Log($"[SceneTaskExecutor]Saved prefab: {prefabPath}");
            return true;
        }
        
        /// <summary>
        /// Options controlling hierarchy export/search behavior.
        /// Shared by ExportHierarchy, ExportHierarchyScene, ExportHierarchyPrefab.
        /// </summary>
        private class HierarchyExportOptions
        {
            /// <summary>Max traversal depth. -1 = unlimited. 0 = only roots.</summary>
            public int maxDepth = -1;

            /// <summary>Case-insensitive substring match on GameObject name. Null/empty = disabled.</summary>
            public string nameFilter;

            /// <summary>Component type name (short or fully-qualified). Null/empty = disabled.</summary>
            public string componentFilter;

            /// <summary>Whether to emit component list per GameObject.</summary>
            public bool includeComponents = true;

            /// <summary>True if any find-style filter is active - switches to flat output mode.</summary>
            public bool HasFilter => !string.IsNullOrEmpty(nameFilter) || !string.IsNullOrEmpty(componentFilter);
        }

        /// <summary>
        /// Parses hierarchy options from task parameters.
        /// Optional params: maxDepth, nameFilter, componentFilter, includeComponents
        /// </summary>
        private HierarchyExportOptions ParseHierarchyOptions(Task task)
        {
            var opts = new HierarchyExportOptions();

            string maxDepthStr = GetOptionalParam(task, "maxDepth");
            if (!string.IsNullOrEmpty(maxDepthStr) && int.TryParse(maxDepthStr, out int d))
                opts.maxDepth = d;

            opts.nameFilter = GetOptionalParam(task, "nameFilter");
            opts.componentFilter = GetOptionalParam(task, "componentFilter");

            string incComp = GetOptionalParam(task, "includeComponents");
            if (!string.IsNullOrEmpty(incComp) && bool.TryParse(incComp, out bool ic))
                opts.includeComponents = ic;

            return opts;
        }

        private bool ExportHierarchy(Task task)
        {
            var opts = ParseHierarchyOptions(task);
            var activeScene = EditorSceneManager.GetActiveScene();
            var rootObjects = activeScene.GetRootGameObjects();

            var output = new System.Text.StringBuilder();
            output.AppendLine($"=== Scene Hierarchy: {activeScene.name} ===");
            output.AppendLine($"Path: {activeScene.path}");
            output.AppendLine($"Root GameObjects: {rootObjects.Length}");
            AppendOptionsHeader(output, opts);
            output.AppendLine();

            int matchCount = 0;
            foreach (var root in rootObjects)
                ExportGameObjectRecursive(root, output, 0, "", opts, ref matchCount);

            if (opts.HasFilter)
                output.AppendLine($"\n=== Matches: {matchCount} ===");

            task.result = output.ToString();
            return true;
        }

        private bool ExportHierarchyScene(Task task)
        {
            string scenePath = GetParam(task, "scenePath");
            var opts = ParseHierarchyOptions(task);

            if (!System.IO.File.Exists(scenePath))
            {
                task.error = $"Scene file not found: {scenePath}";
                return false;
            }

            var previousScene = EditorSceneManager.GetActiveScene();
            string previousScenePath = previousScene.path;

            var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);

            var output = new System.Text.StringBuilder();
            output.AppendLine($"=== Scene Hierarchy: {scene.name} ===");
            output.AppendLine($"Path: {scenePath}");
            output.AppendLine($"Root GameObjects: {scene.GetRootGameObjects().Length}");
            AppendOptionsHeader(output, opts);
            output.AppendLine();

            int matchCount = 0;
            foreach (var root in scene.GetRootGameObjects())
                ExportGameObjectRecursive(root, output, 0, "", opts, ref matchCount);

            if (opts.HasFilter)
                output.AppendLine($"\n=== Matches: {matchCount} ===");

            task.result = output.ToString();
            Debug.Log($"[SceneTaskExecutor] ExportHierarchyScene: {scenePath} ✓");

            if (!string.IsNullOrEmpty(previousScenePath))
                EditorSceneManager.OpenScene(previousScenePath, OpenSceneMode.Single);

            return true;
        }

        private bool ExportHierarchyPrefab(Task task)
        {
            string prefabPath = GetParam(task, "prefabPath");
            string outputPath = GetOptionalParam(task, "outputPath");
            var opts = ParseHierarchyOptions(task);

            GameObject prefab = null;

            // Check if prefab is currently open in Prefab Mode
            var currentStage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
            if (currentStage != null && currentStage.prefabAssetPath == prefabPath)
            {
                prefab = currentStage.prefabContentsRoot;
                Debug.Log($"[SceneTaskExecutor] Using currently open prefab stage: {prefabPath}");
            }
            else
            {
                prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                if (prefab == null)
                {
                    task.error = $"Prefab not found at: {prefabPath}";
                    return false;
                }
            }

            var output = new System.Text.StringBuilder();
            output.AppendLine($"=== Prefab Hierarchy: {prefab.name} ===");
            output.AppendLine($"Path: {prefabPath}");
            AppendOptionsHeader(output, opts);
            output.AppendLine();

            int matchCount = 0;
            ExportGameObjectRecursive(prefab, output, 0, "", opts, ref matchCount);

            if (opts.HasFilter)
                output.AppendLine($"\n=== Matches: {matchCount} ===");

            string result = output.ToString();
            task.result = result;
            Debug.Log($"[SceneTaskExecutor] ExportHierarchyPrefab: {prefabPath} ✓ (matches: {matchCount})");

            if (!string.IsNullOrEmpty(outputPath))
            {
                System.IO.File.WriteAllText(outputPath, result);
                Debug.Log($"[SceneTaskExecutor] Saved hierarchy to: {outputPath}");
            }

            return true;
        }

        private void AppendOptionsHeader(System.Text.StringBuilder output, HierarchyExportOptions opts)
        {
            if (opts.maxDepth >= 0)
                output.AppendLine($"MaxDepth: {opts.maxDepth}");
            if (!string.IsNullOrEmpty(opts.nameFilter))
                output.AppendLine($"NameFilter: \"{opts.nameFilter}\"");
            if (!string.IsNullOrEmpty(opts.componentFilter))
                output.AppendLine($"ComponentFilter: \"{opts.componentFilter}\"");
            if (opts.HasFilter)
                output.AppendLine("Mode: FLAT (find mode - showing only matches)");
        }

        /// <summary>
        /// Core recursive walker. In tree mode emits indented hierarchy with explicit parent/path.
        /// In find mode (any filter active), emits a flat list of only matching GameObjects with full path.
        /// Respects maxDepth (-1 = unlimited, 0 = only roots).
        /// </summary>
        private void ExportGameObjectRecursive(GameObject go, System.Text.StringBuilder output, int depth, string parentPath, HierarchyExportOptions opts, ref int matchCount)
        {
            string fullPath = string.IsNullOrEmpty(parentPath) ? go.name : $"{parentPath}/{go.name}";
            string parentDisplay = string.IsNullOrEmpty(parentPath) ? "<root>" : parentPath;

            if (opts.HasFilter)
            {
                // Flat find mode - only emit matching GameObjects (but still traverse all children)
                if (MatchesFilter(go, opts))
                {
                    matchCount++;
                    output.AppendLine($"[{go.name}]  (path: {fullPath})  (parent: {parentDisplay})");
                    if (opts.includeComponents)
                        AppendComponents(output, go, "  ");
                }
            }
            else
            {
                // Tree mode
                string indent = new string(' ', depth * 2);
                output.AppendLine($"{indent}[{go.name}]  (parent: {parentDisplay})  (path: {fullPath})");
                if (opts.includeComponents)
                    AppendComponents(output, go, new string(' ', (depth + 1) * 2));
            }

            // Respect max depth
            if (opts.maxDepth >= 0 && depth >= opts.maxDepth)
                return;

            for (int i = 0; i < go.transform.childCount; i++)
                ExportGameObjectRecursive(go.transform.GetChild(i).gameObject, output, depth + 1, fullPath, opts, ref matchCount);
        }

        private void AppendComponents(System.Text.StringBuilder output, GameObject go, string indent)
        {
            var components = go.GetComponents<Component>();
            foreach (var comp in components)
            {
                if (comp == null) continue;
                output.AppendLine($"{indent}• {comp.GetType().Name}");
            }
        }

        /// <summary>
        /// True if GameObject matches all active filters (name substring AND component presence).
        /// Name match is case-insensitive substring.
        /// Component match tries ResolveType first, then falls back to short-name comparison.
        /// </summary>
        private bool MatchesFilter(GameObject go, HierarchyExportOptions opts)
        {
            if (!string.IsNullOrEmpty(opts.nameFilter))
            {
                if (go.name.IndexOf(opts.nameFilter, StringComparison.OrdinalIgnoreCase) < 0)
                    return false;
            }

            if (!string.IsNullOrEmpty(opts.componentFilter))
            {
                var type = ResolveType(opts.componentFilter);
                if (type != null)
                {
                    if (go.GetComponent(type) == null)
                        return false;
                }
                else
                {
                    // Fallback: match by short type name
                    bool found = false;
                    foreach (var c in go.GetComponents<Component>())
                    {
                        if (c == null) continue;
                        if (c.GetType().Name.Equals(opts.componentFilter, StringComparison.OrdinalIgnoreCase))
                        {
                            found = true;
                            break;
                        }
                    }
                    if (!found) return false;
                }
            }

            return true;
        }
        
        private bool InspectGameObject(Task task)
        {
            string path = GetParam(task, "path");
            string componentType = GetOptionalParam(task, "component");
            string fieldsParam = GetOptionalParam(task, "fields") ?? "all";
            
            // Find GameObject
            GameObject go = FindInActiveContext(path);
            if (go == null)
            {
                task.error = $"GameObject not found: {path}";
                return false;
            }
            
            var output = new System.Text.StringBuilder();
            output.AppendLine($"=== GameObject Inspector: {go.name} ===");
            output.AppendLine($"Path: {path}");
            output.AppendLine($"Active: {go.activeInHierarchy}");
            output.AppendLine();
            
            // If component type specified, inspect that component
            if (!string.IsNullOrEmpty(componentType))
            {
                Component comp = go.GetComponent(componentType);
                if (comp == null)
                {
                    task.error = $"Component '{componentType}' not found on GameObject '{path}'";
                    return false;
                }
                
                InspectComponent(comp, output, fieldsParam);
            }
            else
            {
                // Inspect all components
                var components = go.GetComponents<Component>();
                foreach (var comp in components)
                {
                    if (comp == null) continue;
                    InspectComponent(comp, output, fieldsParam);
                    output.AppendLine();
                }
            }
            
            string result = output.ToString();
            task.result = result;
            return true;
        }
        
        private void InspectComponent(Component comp, System.Text.StringBuilder output, string fieldsFilter)
        {
            output.AppendLine($"[{comp.GetType().Name}]");

            var type = comp.GetType();
            var fields = type.GetFields(System.Reflection.BindingFlags.Public |
                                       System.Reflection.BindingFlags.NonPublic |
                                       System.Reflection.BindingFlags.Instance);

            // Parse fields filter
            string[] requestedFields = fieldsFilter.ToLower() == "all"
                ? null
                : fieldsFilter.Split(',').Select(f => f.Trim()).ToArray();

            foreach (var field in fields)
            {
                // Skip if specific fields requested and this isn't one of them
                if (requestedFields != null && !requestedFields.Contains(field.Name.ToLower()))
                    continue;

                try
                {
                    object value = field.GetValue(comp);
                    string valueStr = value == null ? "null" : value.ToString();

                    // Special formatting for Unity objects
                    if (value is UnityEngine.Object unityObj)
                    {
                        valueStr = unityObj == null ? "null" : $"{unityObj.name} ({unityObj.GetType().Name})";
                    }

                    output.AppendLine($"  {field.Name}: {valueStr}");
                }
                catch (Exception ex)
                {
                    output.AppendLine($"  {field.Name}: <error: {ex.Message}>");
                }
            }
        }

        /// <summary>
        /// Resolves a type from a fully qualified type name by searching all loaded assemblies.
        /// Type.GetType() only searches the calling assembly and mscorlib by default,
        /// so we need this helper to find types in Assembly-CSharp and other Unity assemblies.
        /// </summary>
        private System.Type ResolveType(string typeName)
        {
            // First try the standard Type.GetType
            var type = System.Type.GetType(typeName);
            if (type != null)
                return type;

            // Parse the type name to extract just the type part (before comma)
            string typeNameOnly = typeName.Split(',')[0].Trim();

            // Search all loaded assemblies
            foreach (var assembly in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                type = assembly.GetType(typeNameOnly);
                if (type != null)
                    return type;
            }

            // Not found in any assembly
            return null;
        }

        /// <summary>
        /// Sets a list property on a ScriptableObject.
        /// Parameters:
        ///   - owner: Path to ScriptableObject asset
        ///   - field: Field name (e.g., "sprites", "scenes")
        ///   - values: Pipe-separated asset paths for simple lists (e.g., "Assets/Scenes/A.unity|Assets/Scenes/B.unity")
        ///   - json: JSON array for complex/nested types (e.g., '[{"scene":"Assets/Scenes/A.unity","nextScenes":["Assets/Scenes/B.unity"]}]')
        ///   - elementType: (optional) Type of list elements - auto-detected if not provided
        ///                  Supported: SceneAsset, Sprite, GameObject, Texture2D, AudioClip, Material, ScriptableObject
        ///   - append: (optional) "true" to append to existing list, default replaces
        /// </summary>
        // SetListProperty — set a serialized List<T> field on ANY target:
        //   - ScriptableObject asset (owner=*.asset path, no path/component)
        //   - Prefab MonoBehaviour (owner=*.prefab + path + component)
        //   - Scene-file MonoBehaviour (owner=*.unity + path + component)
        //   - Active-scene MonoBehaviour (path + component, no owner)
        //
        // Supports element types ConvertValue handles (primitives, string, enum, Vector*,
        // Color, …) plus UnityEngine.Object asset references (Sprite/Texture/etc.). For
        // complex serializable structs use the 'json' parameter.
        private bool SetListProperty(Task task)
        {
            string ownerPath = GetOptionalParam(task, "owner");
            string goPath = GetOptionalParam(task, "path");
            string componentName = GetOptionalParam(task, "component");
            string fieldName = GetParam(task, "field");
            string valuesStr = GetOptionalParam(task, "values");
            string jsonStr = GetOptionalParam(task, "json");
            string elementTypeName = GetOptionalParam(task, "elementType");
            string appendStr = GetOptionalParam(task, "append");
            bool append = appendStr?.ToLower() == "true";

            if (string.IsNullOrEmpty(fieldName))
            {
                task.error = "SetListProperty requires 'field' parameter";
                return false;
            }
            if (string.IsNullOrEmpty(valuesStr) && string.IsNullOrEmpty(jsonStr))
            {
                task.error = "SetListProperty requires either 'values' (pipe-separated) or 'json' (JSON array) parameter";
                return false;
            }

            // Case 1: Prefab MonoBehaviour
            if (!string.IsNullOrEmpty(ownerPath) && ownerPath.EndsWith(".prefab") &&
                !string.IsNullOrEmpty(goPath) && !string.IsNullOrEmpty(componentName))
            {
                return SetListPropertyOnPrefab(task, ownerPath, goPath, componentName, fieldName,
                    valuesStr, jsonStr, elementTypeName, append);
            }

            // Case 2: Scene-file MonoBehaviour (open + edit + save + restore previous scene)
            if (!string.IsNullOrEmpty(ownerPath) && ownerPath.EndsWith(".unity") &&
                !string.IsNullOrEmpty(goPath) && !string.IsNullOrEmpty(componentName))
            {
                return SetListPropertyOnSceneFile(task, ownerPath, goPath, componentName, fieldName,
                    valuesStr, jsonStr, elementTypeName, append);
            }

            // Case 3: Active-scene MonoBehaviour
            if (string.IsNullOrEmpty(ownerPath) && !string.IsNullOrEmpty(goPath) && !string.IsNullOrEmpty(componentName))
            {
                return SetListPropertyOnGameObject(task, goPath, componentName, fieldName,
                    valuesStr, jsonStr, elementTypeName, append);
            }

            // Case 4: ScriptableObject asset (legacy / current behavior)
            if (!string.IsNullOrEmpty(ownerPath))
            {
                return SetListPropertyOnScriptableObject(task, ownerPath, fieldName,
                    valuesStr, jsonStr, elementTypeName, append);
            }

            task.error = "SetListProperty requires either 'path'+'component' (scene GO), or 'owner' (asset path: .prefab/.unity/.asset).";
            return false;
        }

        private bool SetListPropertyOnPrefab(Task task, string prefabPath, string goPath,
            string componentName, string fieldName, string valuesStr, string jsonStr,
            string elementTypeName, bool append)
        {
            var prefabRoot = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefabRoot == null) { task.error = $"Prefab not found: {prefabPath}"; return false; }

            GameObject target = (prefabRoot.name == goPath) ? prefabRoot : null;
            if (target == null)
            {
                foreach (var t in prefabRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (t.name == goPath) { target = t.gameObject; break; }
                }
            }
            if (target == null) { task.error = $"GameObject '{goPath}' not found in prefab: {prefabPath}"; return false; }

            System.Type componentType = ResolveType(componentName);
            if (componentType == null) { task.error = $"Component type not found: {componentName}"; return false; }

            var component = target.GetComponent(componentType);
            if (component == null) { task.error = $"Component {componentName} not found on '{goPath}' in prefab {prefabPath}"; return false; }

            var field = ResolveListField(componentType, fieldName, out string err);
            if (field == null) { task.error = err; return false; }

            if (!ApplyListValue(component, field, valuesStr, jsonStr, elementTypeName, append, out string applyErr, out int successCount, out int totalCount))
            {
                task.error = applyErr;
                return false;
            }

            EditorUtility.SetDirty(prefabRoot);
            PrefabUtility.SavePrefabAsset(prefabRoot);
            Debug.Log($"[SceneTaskExecutor] SetListProperty (prefab): {prefabPath}/{goPath}/{componentName}.{fieldName} = {successCount}/{totalCount} ✓");
            task.result = $"Set {successCount} items in {fieldName}";
            return true;
        }

        private bool SetListPropertyOnSceneFile(Task task, string scenePath, string goPath,
            string componentName, string fieldName, string valuesStr, string jsonStr,
            string elementTypeName, bool append)
        {
            var previousScene = EditorSceneManager.GetActiveScene();
            string previousScenePath = previousScene.path;

            var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);

            bool result = SetListPropertyOnGameObject(task, goPath, componentName, fieldName,
                valuesStr, jsonStr, elementTypeName, append);

            if (result) EditorSceneManager.SaveScene(scene);
            if (!string.IsNullOrEmpty(previousScenePath))
                EditorSceneManager.OpenScene(previousScenePath, OpenSceneMode.Single);

            return result;
        }

        private bool SetListPropertyOnGameObject(Task task, string goPath, string componentName,
            string fieldName, string valuesStr, string jsonStr, string elementTypeName, bool append)
        {
            var go = FindInActiveContext(goPath);
            if (go == null) { task.error = $"GameObject not found: {goPath}"; return false; }

            System.Type componentType = ResolveType(componentName);
            if (componentType == null)
            {
                task.error = $"Component type not found: {componentName}. Use fully qualified name: 'Namespace.Type, AssemblyName'";
                return false;
            }

            var component = go.GetComponent(componentType);
            if (component == null) { task.error = $"Component {componentName} not found on {goPath}"; return false; }

            var field = ResolveListField(componentType, fieldName, out string err);
            if (field == null) { task.error = err; return false; }

            if (!ApplyListValue(component, field, valuesStr, jsonStr, elementTypeName, append, out string applyErr, out int successCount, out int totalCount))
            {
                task.error = applyErr;
                return false;
            }

            EditorUtility.SetDirty(go);
            PrefabUtility.RecordPrefabInstancePropertyModifications(component);
            Debug.Log($"[SceneTaskExecutor] SetListProperty (scene-go): {goPath}/{componentName}.{fieldName} = {successCount}/{totalCount} ✓");
            task.result = $"Set {successCount} items in {fieldName}";
            return true;
        }

        private bool SetListPropertyOnScriptableObject(Task task, string ownerPath, string fieldName,
            string valuesStr, string jsonStr, string elementTypeName, bool append)
        {
            var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(ownerPath);
            if (asset == null) { task.error = $"ScriptableObject not found: {ownerPath}"; return false; }

            var field = ResolveListField(asset.GetType(), fieldName, out string err);
            if (field == null) { task.error = err; return false; }

            if (!ApplyListValue(asset, field, valuesStr, jsonStr, elementTypeName, append, out string applyErr, out int successCount, out int totalCount))
            {
                task.error = applyErr;
                return false;
            }

            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
            Debug.Log($"[SceneTaskExecutor] SetListProperty (SO): {fieldName} = {successCount}/{totalCount} ✓");
            task.result = $"Set {successCount} items in {fieldName}";
            return true;
        }

        // Resolves a public/non-public List<T> field by name; returns null + error message on miss.
        private System.Reflection.FieldInfo ResolveListField(System.Type ownerType, string fieldName, out string error)
        {
            error = null;
            var field = ownerType.GetField(fieldName,
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);
            if (field == null) { error = $"Field '{fieldName}' not found in {ownerType.Name}"; return null; }
            if (!field.FieldType.IsGenericType || field.FieldType.GetGenericTypeDefinition() != typeof(List<>))
            { error = $"Field '{fieldName}' is not a List<T> type (found {field.FieldType.Name})"; return null; }
            return field;
        }

        // Builds the list value (from `values` pipe-separated OR `json` array) and assigns it
        // to the target object's field. Element types supported:
        //   - Anything ConvertValue handles (primitives, string, enum, Vector*, Color, …)
        //   - UnityEngine.Object asset references via LoadAssetByType
        //   - Complex types only via JSON path
        private bool ApplyListValue(object target, System.Reflection.FieldInfo field,
            string valuesStr, string jsonStr, string elementTypeName, bool append,
            out string error, out int successCount, out int totalCount)
        {
            error = null;
            successCount = 0;
            totalCount = 0;

            System.Type elementType = field.FieldType.GetGenericArguments()[0];

            if (!string.IsNullOrEmpty(jsonStr))
            {
                if (!(target is ScriptableObject so))
                {
                    error = "JSON list element is currently supported only for ScriptableObject targets. For MonoBehaviour list fields, pass 'values' (pipe-separated) instead.";
                    return false;
                }
                var task = new Task(); // placeholder error sink; we re-route through existing helper
                bool ok = SetListPropertyFromJson(task, so, field, elementType, jsonStr, append);
                if (!ok) { error = task.error; return false; }
                successCount = totalCount = 1;
                return true;
            }

            if (!string.IsNullOrEmpty(elementTypeName))
            {
                var overridden = ResolveAssetType(elementTypeName);
                if (overridden == null)
                {
                    error = $"Unknown element type: {elementTypeName}.";
                    return false;
                }
                elementType = overridden;
            }

            string[] valuePaths = valuesStr.Split('|').Select(v => v.Trim()).Where(v => !string.IsNullOrEmpty(v)).ToArray();
            totalCount = valuePaths.Length;

            var listType = typeof(List<>).MakeGenericType(elementType);
            object list = append ? (field.GetValue(target) ?? Activator.CreateInstance(listType))
                                 : Activator.CreateInstance(listType);
            var addMethod = listType.GetMethod("Add");

            bool isObjectRef = typeof(UnityEngine.Object).IsAssignableFrom(elementType);

            foreach (string valuePath in valuePaths)
            {
                object item = null;
                if (isObjectRef)
                {
                    item = LoadAssetByType(valuePath, elementType);
                    if (item == null)
                    {
                        Debug.LogWarning($"[SceneTaskExecutor] SetListProperty: asset not found: {valuePath}");
                        continue;
                    }
                }
                else
                {
                    try { item = ConvertValue(elementType, valuePath); }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[SceneTaskExecutor] SetListProperty: failed to parse '{valuePath}' as {elementType.Name}: {ex.Message}");
                        continue;
                    }
                }
                addMethod.Invoke(list, new object[] { item });
                successCount++;
            }

            field.SetValue(target, list);
            return true;
        }

        /// <summary>
        /// Check if type is a simple Unity asset type (not a complex nested class)
        /// </summary>
        private bool IsSimpleAssetType(System.Type type)
        {
            return typeof(UnityEngine.Object).IsAssignableFrom(type) ||
                   type == typeof(string) ||
                   type.IsPrimitive;
        }

        /// <summary>
        /// Sets a list property from JSON for complex/nested types
        /// </summary>
        private bool SetListPropertyFromJson(Task task, ScriptableObject asset, System.Reflection.FieldInfo field,
            System.Type elementType, string jsonStr, bool append)
        {
            try
            {
                // Parse JSON array
                var jsonEntries = ParseJsonArray(jsonStr);
                if (jsonEntries == null)
                {
                    task.error = "Failed to parse JSON array. Expected format: [{...}, {...}]";
                    return false;
                }

                // Get or create the list
                var listType = typeof(List<>).MakeGenericType(elementType);
                object list;

                if (append)
                {
                    list = field.GetValue(asset);
                    if (list == null)
                    {
                        list = Activator.CreateInstance(listType);
                    }
                }
                else
                {
                    list = Activator.CreateInstance(listType);
                }

                var addMethod = listType.GetMethod("Add");
                int successCount = 0;

                // Process each JSON entry
                foreach (var jsonEntry in jsonEntries)
                {
                    var element = CreateElementFromJson(elementType, jsonEntry);
                    if (element != null)
                    {
                        addMethod.Invoke(list, new object[] { element });
                        successCount++;
                    }
                }

                // Set the field value
                field.SetValue(asset, list);

                // Mark dirty and save
                EditorUtility.SetDirty(asset);
                AssetDatabase.SaveAssets();

                Debug.Log($"[SceneTaskExecutor]SetListProperty (JSON): {field.Name} = {successCount}/{jsonEntries.Count} entries ✓");
                task.result = $"Set {successCount} items in {field.Name}";
                return true;
            }
            catch (Exception ex)
            {
                task.error = $"Error parsing JSON: {ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// Parse a JSON array string into a list of dictionaries
        /// </summary>
        private List<Dictionary<string, object>> ParseJsonArray(string jsonStr)
        {
            // Always use manual parsing for complex nested JSON structures
            // JsonUtility cannot handle arrays of objects directly
            return ParseJsonArrayManual(jsonStr);
        }

        /// <summary>
        /// Manual JSON array parsing for complex nested structures
        /// </summary>
        private List<Dictionary<string, object>> ParseJsonArrayManual(string jsonStr)
        {
            var result = new List<Dictionary<string, object>>();
            jsonStr = jsonStr.Trim();

            if (!jsonStr.StartsWith("[") || !jsonStr.EndsWith("]"))
                return null;

            // Remove outer brackets
            jsonStr = jsonStr.Substring(1, jsonStr.Length - 2).Trim();

            if (string.IsNullOrEmpty(jsonStr))
                return result;

            // Split by objects (respecting nested braces)
            var objects = SplitJsonObjects(jsonStr);

            foreach (var obj in objects)
            {
                var dict = ParseJsonObject(obj);
                if (dict != null)
                    result.Add(dict);
            }

            return result;
        }

        /// <summary>
        /// Split JSON string into individual object strings
        /// </summary>
        private List<string> SplitJsonObjects(string jsonStr)
        {
            var objects = new List<string>();
            int braceCount = 0;
            int bracketCount = 0;
            int start = 0;
            bool inString = false;

            for (int i = 0; i < jsonStr.Length; i++)
            {
                char c = jsonStr[i];

                if (c == '"' && (i == 0 || jsonStr[i - 1] != '\\'))
                    inString = !inString;

                if (!inString)
                {
                    if (c == '{') braceCount++;
                    else if (c == '}') braceCount--;
                    else if (c == '[') bracketCount++;
                    else if (c == ']') bracketCount--;
                    else if (c == ',' && braceCount == 0 && bracketCount == 0)
                    {
                        objects.Add(jsonStr.Substring(start, i - start).Trim());
                        start = i + 1;
                    }
                }
            }

            // Add last object
            if (start < jsonStr.Length)
                objects.Add(jsonStr.Substring(start).Trim());

            return objects;
        }

        /// <summary>
        /// Parse a JSON object string into a dictionary
        /// </summary>
        private Dictionary<string, object> ParseJsonObject(string jsonStr)
        {
            var dict = new Dictionary<string, object>();
            jsonStr = jsonStr.Trim();

            if (!jsonStr.StartsWith("{") || !jsonStr.EndsWith("}"))
                return dict;

            // Remove outer braces
            jsonStr = jsonStr.Substring(1, jsonStr.Length - 2).Trim();
            // Parse key-value pairs
            int i = 0;
            while (i < jsonStr.Length)
            {
                // Skip whitespace
                while (i < jsonStr.Length && char.IsWhiteSpace(jsonStr[i])) i++;
                if (i >= jsonStr.Length) break;

                // Find key (quoted string)
                if (jsonStr[i] != '"') break;
                int keyStart = i + 1;
                i++;
                while (i < jsonStr.Length && jsonStr[i] != '"') i++;
                string key = jsonStr.Substring(keyStart, i - keyStart);
                i++; // Skip closing quote

                // Skip to colon
                while (i < jsonStr.Length && jsonStr[i] != ':') i++;
                i++; // Skip colon

                // Skip whitespace
                while (i < jsonStr.Length && char.IsWhiteSpace(jsonStr[i])) i++;

                // Parse value
                object value = null;
                if (jsonStr[i] == '"')
                {
                    // String value
                    int valueStart = i + 1;
                    i++;
                    while (i < jsonStr.Length && !(jsonStr[i] == '"' && jsonStr[i - 1] != '\\')) i++;
                    value = jsonStr.Substring(valueStart, i - valueStart);
                    i++; // Skip closing quote
                }
                else if (jsonStr[i] == '[')
                {
                    // Array value
                    int bracketCount = 1;
                    int valueStart = i;
                    i++;
                    while (i < jsonStr.Length && bracketCount > 0)
                    {
                        if (jsonStr[i] == '[') bracketCount++;
                        else if (jsonStr[i] == ']') bracketCount--;
                        i++;
                    }
                    string arrayStr = jsonStr.Substring(valueStart, i - valueStart);
                    value = ParseJsonStringArray(arrayStr);
                }
                else if (jsonStr[i] == '{')
                {
                    // Nested object
                    int braceCount = 1;
                    int valueStart = i;
                    i++;
                    while (i < jsonStr.Length && braceCount > 0)
                    {
                        if (jsonStr[i] == '{') braceCount++;
                        else if (jsonStr[i] == '}') braceCount--;
                        i++;
                    }
                    value = jsonStr.Substring(valueStart, i - valueStart);
                }
                else
                {
                    // Number or boolean
                    int valueStart = i;
                    while (i < jsonStr.Length && jsonStr[i] != ',' && jsonStr[i] != '}') i++;
                    value = jsonStr.Substring(valueStart, i - valueStart).Trim();
                }

                dict[key] = value;

                // Skip to next key or end
                while (i < jsonStr.Length && (jsonStr[i] == ',' || char.IsWhiteSpace(jsonStr[i]))) i++;
            }

            return dict;
        }

        /// <summary>
        /// Parse a JSON array of strings
        /// </summary>
        private List<string> ParseJsonStringArray(string jsonStr)
        {
            var result = new List<string>();
            jsonStr = jsonStr.Trim();

            if (!jsonStr.StartsWith("[") || !jsonStr.EndsWith("]"))
                return result;

            jsonStr = jsonStr.Substring(1, jsonStr.Length - 2).Trim();

            if (string.IsNullOrEmpty(jsonStr))
                return result;

            // Split by commas (respecting quotes)
            bool inString = false;
            int start = 0;

            for (int i = 0; i < jsonStr.Length; i++)
            {
                if (jsonStr[i] == '"' && (i == 0 || jsonStr[i - 1] != '\\'))
                    inString = !inString;
                else if (jsonStr[i] == ',' && !inString)
                {
                    string item = jsonStr.Substring(start, i - start).Trim().Trim('"');
                    if (!string.IsNullOrEmpty(item))
                        result.Add(item);
                    start = i + 1;
                }
            }

            // Add last item
            string lastItem = jsonStr.Substring(start).Trim().Trim('"');
            if (!string.IsNullOrEmpty(lastItem))
                result.Add(lastItem);

            return result;
        }

        /// <summary>
        /// Create an element instance from JSON dictionary using reflection
        /// </summary>
        private object CreateElementFromJson(System.Type elementType, Dictionary<string, object> jsonDict)
        {
            var element = Activator.CreateInstance(elementType);

            foreach (var kvp in jsonDict)
            {
                var field = elementType.GetField(kvp.Key,
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance);

                if (field == null)
                    continue;

                object value = ConvertJsonValue(field.FieldType, kvp.Value);
                if (value != null)
                    field.SetValue(element, value);
            }

            return element;
        }

        /// <summary>
        /// Convert a JSON value to the target type
        /// </summary>
        private object ConvertJsonValue(System.Type targetType, object jsonValue)
        {
            if (jsonValue == null)
                return null;

            // String value - could be asset path or simple string
            if (jsonValue is string strValue)
            {
                // First check for nested type with scene asset field (like SceneReference)
                // This must come BEFORE direct asset loading check
                if (targetType.IsClass && !typeof(UnityEngine.Object).IsAssignableFrom(targetType) && targetType != typeof(string))
                {
                    // Look for sceneAsset field
                    var sceneAssetField = targetType.GetField("sceneAsset",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

                    if (sceneAssetField != null && sceneAssetField.FieldType == typeof(SceneAsset))
                    {
                        var instance = Activator.CreateInstance(targetType);
                        var sceneAsset = AssetDatabase.LoadAssetAtPath<SceneAsset>(strValue);
                        if (sceneAsset != null)
                        {
                            sceneAssetField.SetValue(instance, sceneAsset);

                            // Also set cached name if exists
                            var sceneNameField = targetType.GetField("sceneName",
                                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                            if (sceneNameField != null)
                            {
                                sceneNameField.SetValue(instance, sceneAsset.name);
                            }
                        }
                        else
                        {
                            Debug.LogWarning($"[SceneTaskExecutor] Failed to load SceneAsset: {strValue}");
                        }
                        return instance;
                    }
                }

                // Check if it's an asset path for direct Unity Object types
                if (strValue.StartsWith("Assets/") && targetType != typeof(string))
                {
                    // Try to load as asset
                    if (targetType == typeof(SceneAsset))
                        return AssetDatabase.LoadAssetAtPath<SceneAsset>(strValue);
                    if (typeof(UnityEngine.Object).IsAssignableFrom(targetType))
                        return AssetDatabase.LoadAssetAtPath(strValue, targetType);
                }

                // PerSpec's manual JSON parser stores all non-string scalars (true/false/numbers)
                // as raw strings. Coerce them to the target field type here so JSON lists with
                // bool/int/float fields populate correctly. Without this, a struct field of
                // type `bool` receives "true" (string) and reflection's SetValue throws.
                if (targetType == typeof(bool) && bool.TryParse(strValue, out bool b))     return b;
                if (targetType == typeof(int) && int.TryParse(strValue, out int iv))       return iv;
                if (targetType == typeof(long) && long.TryParse(strValue, out long lv))    return lv;
                if (targetType == typeof(float)
                    && float.TryParse(strValue, System.Globalization.NumberStyles.Float,
                                      System.Globalization.CultureInfo.InvariantCulture, out float fv)) return fv;
                if (targetType == typeof(double)
                    && double.TryParse(strValue, System.Globalization.NumberStyles.Float,
                                       System.Globalization.CultureInfo.InvariantCulture, out double dv)) return dv;
                if (targetType.IsEnum)
                {
                    try { return Enum.Parse(targetType, strValue, ignoreCase: true); }
                    catch { /* fall through */ }
                }

                return strValue;
            }

            // List of strings - convert to list of assets or nested objects
            if (jsonValue is List<string> strList)
            {
                if (targetType.IsGenericType && targetType.GetGenericTypeDefinition() == typeof(List<>))
                {
                    var listElementType = targetType.GetGenericArguments()[0];
                    var list = Activator.CreateInstance(targetType);
                    var addMethod = targetType.GetMethod("Add");

                    foreach (var item in strList)
                    {
                        var convertedItem = ConvertJsonValue(listElementType, item);
                        if (convertedItem != null)
                        {
                            addMethod.Invoke(list, new object[] { convertedItem });
                        }
                    }

                    return list;
                }
            }

            // Nested JSON object string
            if (jsonValue is string objStr && objStr.StartsWith("{"))
            {
                var nestedDict = ParseJsonObject(objStr);
                return CreateElementFromJson(targetType, nestedDict);
            }

            return jsonValue;
        }

        [Serializable]
        private class JsonArrayWrapper
        {
            public string[] items;
        }

        /// <summary>
        /// Resolves common Unity asset types by name
        /// </summary>
        private System.Type ResolveAssetType(string typeName)
        {
            return typeName.ToLower() switch
            {
                "sceneasset" => typeof(SceneAsset),
                "sprite" => typeof(Sprite),
                "gameobject" => typeof(GameObject),
                "texture2d" => typeof(Texture2D),
                "texture" => typeof(Texture2D),
                "audioclip" => typeof(AudioClip),
                "audio" => typeof(AudioClip),
                "material" => typeof(Material),
                "scriptableobject" => typeof(ScriptableObject),
                "prefab" => typeof(GameObject),
                _ => ResolveType(typeName) // Try full type name resolution
            };
        }

        /// <summary>
        /// Loads an asset from path based on target type
        /// </summary>
        private UnityEngine.Object LoadAssetByType(string path, System.Type targetType)
        {
            // Handle SceneAsset specially - it's editor-only
            if (targetType == typeof(SceneAsset))
            {
                return AssetDatabase.LoadAssetAtPath<SceneAsset>(path);
            }

            // For other types, use generic load
            return AssetDatabase.LoadAssetAtPath(path, targetType);
        }

        /// <summary>
        /// Takes a screenshot of the current Game view.
        /// Parameters:
        ///   - outputDir: Directory to save screenshot (default: Assets/Screenshots)
        ///   - filename: Base filename without extension (default: screenshot)
        ///   - supersampling: Resolution multiplier (default: 1)
        /// Automatically appends timestamp to filename.
        /// Stores the full output path in task.result.
        /// </summary>
        private bool TakeScreenshot(Task task)
        {
            string outputDir = GetOptionalParam(task, "outputDir", "Assets/Screenshots");
            string filename = GetOptionalParam(task, "filename", "screenshot");
            string supersamplingStr = GetOptionalParam(task, "supersampling", "1");
            int supersampling = int.Parse(supersamplingStr);

            // Create output directory
            if (!System.IO.Directory.Exists(outputDir))
                System.IO.Directory.CreateDirectory(outputDir);

            // Generate filename with timestamp
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string fullPath = System.IO.Path.Combine(outputDir, $"{filename}_{timestamp}.png");

            // Capture screenshot
            ScreenCapture.CaptureScreenshot(fullPath, supersampling);

            task.result = fullPath;
            Debug.Log($"[SceneTaskExecutor] Captured: {fullPath} (supersampling: {supersampling}x)");
            return true;
        }

        /// <summary>
        /// Waits for a GameObject to appear in the scene, with optional component check.
        /// Parameters:
        ///   - path: GameObject name/path to find via FindInActiveContext()
        ///   - component: (optional) Fully qualified component type name to verify (e.g., "MyNamespace.MyComponent, Assembly-CSharp")
        ///   - timeout: (optional) Maximum wait time in seconds (default: 10)
        /// Returns immediately if found, otherwise polls asynchronously via EditorApplication.delayCall.
        /// Stores "Found: {path}" in task.result on success.
        /// </summary>
        private bool WaitForGameObject(Task task)
        {
            string path = GetParam(task, "path");
            string component = GetOptionalParam(task, "component");
            string timeoutStr = GetOptionalParam(task, "timeout", "10");
            float timeout = float.Parse(timeoutStr);

            // Immediate check
            var go = FindInActiveContext(path);
            if (go != null && CheckComponentOnGameObject(go, component))
            {
                task.result = $"Found: {path}";
                Debug.Log($"[SceneTaskExecutor] Found immediately: {path}");
                return true;
            }

            // Start async polling
            _lastExecuteWasAsync = true;
            double startTime = EditorApplication.timeSinceStartup;

            void Poll()
            {
                if (EditorApplication.timeSinceStartup - startTime > timeout)
                {
                    CompleteAsyncTask(task, false, $"Timeout waiting for GameObject '{path}' after {timeout}s");
                    return;
                }

                var found = FindInActiveContext(path);
                if (found != null && CheckComponentOnGameObject(found, component))
                {
                    task.result = $"Found: {path}";
                    Debug.Log($"[SceneTaskExecutor] Found: {path} (after {EditorApplication.timeSinceStartup - startTime:F1}s)");
                    CompleteAsyncTask(task, true);
                    return;
                }

                EditorApplication.delayCall += Poll;
            }

            EditorApplication.delayCall += Poll;
            Debug.Log($"[SceneTaskExecutor] Waiting for: {path} (timeout: {timeout}s)");
            return true;
        }

        /// <summary>
        /// Checks if a GameObject has a specific component type.
        /// Returns true if componentName is empty (no check needed) or if the component is found.
        /// </summary>
        private bool CheckComponentOnGameObject(GameObject go, string componentName)
        {
            if (string.IsNullOrEmpty(componentName))
                return true;

            var type = ResolveType(componentName);
            if (type == null)
                return false;

            return go.GetComponent(type) != null;
        }

        /// <summary>
        /// Calls a method on a component via reflection.
        /// Parameters:
        ///   - path: GameObject name/path to find via FindInActiveContext()
        ///   - component: Fully qualified component type name (e.g., "MyNamespace.MyComponent, Assembly-CSharp")
        ///   - method: Method name to invoke (supports public and non-public instance methods)
        /// Stores the method return value (or "void") in task.result.
        /// </summary>
        private bool CallMethod(Task task)
        {
            string path = GetParam(task, "path");
            string componentName = GetParam(task, "component");
            string methodName = GetParam(task, "method");
            string argsStr = GetOptionalParam(task, "args");

            var go = FindInActiveContext(path);
            if (go == null)
            {
                task.error = $"GameObject not found: {path}";
                return false;
            }

            var type = ResolveType(componentName);
            if (type == null)
            {
                task.error = $"Component type not found: {componentName}. Use fully qualified name: 'Namespace.Type, AssemblyName'";
                return false;
            }

            var component = go.GetComponent(type);
            if (component == null)
            {
                task.error = $"Component {componentName} not found on {path}";
                return false;
            }

            // Get all methods with the given name (handles overloads)
            var methods = type.GetMethods(
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance)
                .Where(m => m.Name == methodName).ToArray();

            if (methods.Length == 0)
            {
                task.error = $"Method '{methodName}' not found on {componentName}";
                return false;
            }

            // Parse arguments if provided
            if (!string.IsNullOrEmpty(argsStr))
            {
                // Comma-separated args matched against parameter types via ConvertValue
                var argParts = argsStr.Split(',');
                var method = methods.FirstOrDefault(m => m.GetParameters().Length == argParts.Length);
                if (method == null)
                {
                    task.error = $"No overload of '{methodName}' with {argParts.Length} parameters found on {componentName}";
                    return false;
                }

                var paramInfos = method.GetParameters();
                var args = new object[argParts.Length];
                for (int i = 0; i < argParts.Length; i++)
                {
                    args[i] = ConvertValue(paramInfos[i].ParameterType, argParts[i].Trim());
                }

                var result = method.Invoke(component, args);
                task.result = result?.ToString() ?? "void";
                EditorUtility.SetDirty(go);
                Debug.Log($"[SceneTaskExecutor] {path}/{componentName}.{methodName}({argsStr}) => {task.result}");
                return true;
            }
            else
            {
                // No args — call parameterless overload
                var method = methods.FirstOrDefault(m => m.GetParameters().Length == 0);
                if (method == null)
                {
                    task.error = $"No parameterless overload of '{methodName}' found on {componentName}. Provide 'args' parameter.";
                    return false;
                }

                var result = method.Invoke(component, null);
                task.result = result?.ToString() ?? "void";
                Debug.Log($"[SceneTaskExecutor] {path}/{componentName}.{methodName}() => {task.result}");
                return true;
            }
        }

        /// <summary>
        /// Opens a prefab in Prefab Mode for editing.
        /// Parameters:
        ///   - prefabPath: Path to the prefab asset (e.g., "Assets/Prefabs/MyPrefab.prefab")
        /// </summary>
        private bool OpenPrefab(Task task)
        {
            string prefabPath = GetParam(task, "prefabPath");

            if (!System.IO.File.Exists(prefabPath))
            {
                task.error = $"Prefab not found: {prefabPath}";
                return false;
            }

            // Idempotent re-open: if the same prefab is already opened, keep the existing stage.
            if (_openStage != null && _openPrefabPath == prefabPath && _openStage.prefabContentsRoot != null)
            {
                task.result = $"Opened prefab (already open in stage): {prefabPath}";
                return true;
            }

            // Open the prefab in Unity's visible Prefab Stage so the user can see edits land in real
            // time. PrefabStageUtility.OpenPrefab swaps the Editor view to Prefab Mode synchronously
            // and returns the stage handle. We cache the handle so subsequent tasks find their
            // targets via _openStage.prefabContentsRoot — without depending on
            // PrefabStageUtility.GetCurrentPrefabStage() which only reflects focused stages and
            // can return null on the same frame the stage opens.
            UnityEditor.SceneManagement.PrefabStage stage;
            try
            {
                stage = UnityEditor.SceneManagement.PrefabStageUtility.OpenPrefab(prefabPath);
            }
            catch (Exception ex)
            {
                task.error = $"Failed to open prefab in stage: {ex.Message}";
                return false;
            }

            if (stage == null || stage.prefabContentsRoot == null)
            {
                task.error = $"Failed to open prefab in stage: {prefabPath}";
                return false;
            }

            _openStage = stage;
            _openPrefabPath = prefabPath;

            // Detect missing scripts in the opened prefab. Unity will refuse to save a prefab
            // that contains missing-script slots, so we surface this up front as a non-fatal
            // warning. Execution continues (return true) — scenarios that don't call SavePrefab
            // are unaffected; scenarios that do call it will fail naturally on the save step.
            var missingReport = ScanForMissingScripts(stage.prefabContentsRoot, prefabPath);
            if (!string.IsNullOrEmpty(missingReport))
            {
                Debug.LogWarning($"[SceneTaskExecutor] OpenPrefab '{prefabPath}': {missingReport}");
                task.result = $"Opened prefab in stage: {prefabPath} | WARNING: {missingReport}";
            }
            else
            {
                task.result = $"Opened prefab in stage: {prefabPath}";
            }
            Debug.Log($"[SceneTaskExecutor] Opened prefab in stage: {prefabPath} (root '{stage.prefabContentsRoot.name}')");
            return true;
        }

        /// <summary>
        /// Scans an open prefab stage for GameObjects with missing script references and
        /// extracts the unresolved m_Script GUIDs from the .prefab YAML.
        /// Returns an empty string when clean; otherwise a single-line summary suitable for
        /// embedding in task.result.
        /// </summary>
        private string ScanForMissingScripts(GameObject root, string prefabPath)
        {
            // Phase 1 — runtime detection: which GameObjects in the open stage have missing slots?
            var affected = new List<string>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                var go = t.gameObject;
                int missing = UnityEditor.GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go);
                if (missing > 0)
                    affected.Add($"{GetGameObjectPath(go)} ({missing})");
            }
            if (affected.Count == 0)
                return string.Empty;

            // Phase 2 — identify which referenced scripts won't load. Walks the prefab's asset
            // dependencies (Unity's tracked references — handles nested prefabs/variants) and
            // flags MonoScripts whose class can't be resolved. No YAML parsing.
            var brokenScripts = new List<string>();
            try
            {
                var seen = new HashSet<string>();
                var deps = AssetDatabase.GetDependencies(prefabPath, recursive: true);
                foreach (var dep in deps)
                {
                    if (!dep.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                        && !dep.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!seen.Add(dep)) continue;

                    var ms = AssetDatabase.LoadAssetAtPath<MonoScript>(dep);
                    if (ms == null)
                    {
                        brokenScripts.Add($"{dep} (asset not loadable)");
                        continue;
                    }
                    if (ms.GetClass() == null)
                    {
                        var guid = AssetDatabase.AssetPathToGUID(dep);
                        brokenScripts.Add($"{dep} [class fails to load; guid: {guid}]");
                    }
                }
            }
            catch
            {
                // Dependency walk is supplementary — if it throws, the GameObject list is still
                // the primary signal that the prefab has broken slots.
            }

            var goSummary = string.Join("; ", affected);
            var brokenSummary = brokenScripts.Count > 0
                ? $" Broken script references: {string.Join("; ", brokenScripts)}."
                : string.Empty;
            return $"{affected.Count} GameObject(s) have missing scripts: {goSummary}.{brokenSummary} SavePrefab will fail until these are resolved or removed.";
        }

        /// <summary>
        /// Saves the prefab opened by OpenPrefab back to its asset path. The Prefab Stage stays
        /// open after saving so the user can visually confirm the result; the next OpenPrefab call
        /// (or manual stage close) replaces it.
        /// </summary>
        private bool SavePrefab(Task task)
        {
            if (_openStage == null || _openStage.prefabContentsRoot == null || string.IsNullOrEmpty(_openPrefabPath))
            {
                task.error = "No prefab is currently open (call OpenPrefab first)";
                return false;
            }

            // Pre-flight: Unity refuses to save a prefab containing missing-script slots. Surface
            // a structured error listing affected GameObjects and unresolved GUIDs instead of
            // letting Unity's generic save error bubble up. Use RemoveMissingScripts to clean.
            var missingReport = ScanForMissingScripts(_openStage.prefabContentsRoot, _openPrefabPath);
            if (!string.IsNullOrEmpty(missingReport))
            {
                task.error = $"Cannot save prefab: {missingReport} Run RemoveMissingScripts first to strip the broken slots.";
                return false;
            }

            try
            {
                // PrefabUtility.SaveAsPrefabAsset is the correct save call for Prefab Stage edits:
                // it writes the stage contents back to the .prefab asset while keeping the stage
                // open. EditorSceneManager.SaveScene rejects .prefab paths (only accepts .unity).
                // (The earlier "doesn't appear to save" symptom turned out to be Unity in Play
                // Mode rejecting asset writes, not an API issue.)
                PrefabUtility.SaveAsPrefabAsset(_openStage.prefabContentsRoot, _openPrefabPath);
            }
            catch (Exception ex)
            {
                task.error = $"Failed to save prefab: {ex.Message}";
                return false;
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            task.result = $"Saved prefab: {_openPrefabPath}";
            Debug.Log($"[SceneTaskExecutor] Saved prefab: {_openPrefabPath}");
            return true;
        }

        /// <summary>
        /// Closes the Prefab Stage opened by OpenPrefab and returns to the main scene view.
        /// Does NOT save — pair with SavePrefab if you want to persist edits before closing.
        /// Idempotent: no-op if no Prefab Stage is currently tracked by the executor.
        /// No parameters.
        /// </summary>
        private bool ClosePrefab(Task task)
        {
            if (_openStage == null && string.IsNullOrEmpty(_openPrefabPath))
            {
                task.result = "No prefab stage was open";
                return true;
            }

            string closedPath = _openPrefabPath;
            _openStage = null;
            _openPrefabPath = null;

            // GoToMainStage exits the current Prefab Stage and restores the scene view.
            UnityEditor.SceneManagement.StageUtility.GoToMainStage();

            task.result = $"Closed prefab stage: {closedPath}";
            Debug.Log($"[SceneTaskExecutor] Closed prefab stage: {closedPath}");
            return true;
        }

        /// <summary>
        /// Removes missing-script slots from the currently open prefab so SavePrefab can succeed.
        /// Optional `target` parameter scopes the cleanup to a sub-path under the prefab root
        /// (resolved via FindInActiveContext); without it, the entire prefab root is walked.
        /// Returns count of slots removed and list of affected GameObject paths.
        /// </summary>
        private bool RemoveMissingScripts(Task task)
        {
            if (_openStage == null || _openStage.prefabContentsRoot == null)
            {
                task.error = "No prefab is currently open (call OpenPrefab first)";
                return false;
            }

            string targetPath = GetParam(task, "target");
            GameObject root;
            if (string.IsNullOrEmpty(targetPath))
            {
                root = _openStage.prefabContentsRoot;
            }
            else
            {
                root = FindInActiveContext(targetPath);
                if (root == null)
                {
                    task.error = $"Target GameObject not found in open prefab: {targetPath}";
                    return false;
                }
            }

            int totalRemoved = 0;
            var affected = new List<string>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                var go = t.gameObject;
                int beforeCount = UnityEditor.GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go);
                if (beforeCount <= 0) continue;
                UnityEditor.GameObjectUtility.RemoveMonoBehavioursWithMissingScript(go);
                int afterCount = UnityEditor.GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go);
                int removed = beforeCount - afterCount;
                if (removed > 0)
                {
                    totalRemoved += removed;
                    affected.Add($"{GetGameObjectPath(go)} ({removed})");
                    EditorUtility.SetDirty(go);
                }
            }

            if (totalRemoved == 0)
            {
                task.result = "No missing scripts found";
                return true;
            }

            // Mark the stage dirty so the user is prompted/SavePrefab persists the cleanup.
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(_openStage.scene);

            task.result = $"Removed {totalRemoved} missing-script slot(s) from {affected.Count} GameObject(s): {string.Join("; ", affected)}";
            Debug.Log($"[SceneTaskExecutor] {task.result}");
            return true;
        }

        /// <summary>
        /// Reports missing-script details for a prefab using Unity APIs only — no YAML/text parsing.
        ///
        /// Strategy: load the prefab into a hidden editing scope via PrefabUtility.LoadPrefabContents
        /// (or use the currently open stage), walk GameObjects, and for each one with missing
        /// MonoBehaviour slots dump the GameObject's serialized representation via
        /// EditorJsonUtility.ToJson. The resulting JSON contains m_Script ObjectReference fields
        /// — including those for unresolved slots — with their GUID values exposed. We then
        /// cross-reference each extracted GUID against AssetDatabase to label each as resolved /
        /// orphan / class-fails-to-load.
        ///
        /// Parameters (all optional):
        ///   - prefabPath: load this prefab fresh (does not affect the open stage). When omitted,
        ///                 the currently open prefab stage is inspected instead.
        /// </summary>
        private bool ReportMissingScripts(Task task)
        {
            string prefabPath = GetParam(task, "prefabPath");

            GameObject root;
            bool loadedTemp = false;
            if (!string.IsNullOrEmpty(prefabPath))
            {
                if (!System.IO.File.Exists(prefabPath))
                {
                    task.error = $"Prefab not found: {prefabPath}";
                    return false;
                }
                try
                {
                    root = PrefabUtility.LoadPrefabContents(prefabPath);
                    loadedTemp = true;
                }
                catch (Exception ex)
                {
                    task.error = $"Failed to load prefab contents: {ex.Message}";
                    return false;
                }
            }
            else if (_openStage != null && _openStage.prefabContentsRoot != null)
            {
                root = _openStage.prefabContentsRoot;
                prefabPath = _openPrefabPath;
            }
            else
            {
                task.error = "No prefabPath specified and no prefab is currently open";
                return false;
            }

            try
            {
                // Match "guid": "<32 hex>" inside the JSON dump (JSON, not YAML — produced by
                // EditorJsonUtility's Unity-API serializer).
                var guidRx = new System.Text.RegularExpressions.Regex(
                    @"""guid""\s*:\s*""([a-f0-9]{32})""");

                var report = new System.Text.StringBuilder();
                int affectedCount = 0;
                var allUnresolved = new HashSet<string>();
                var allBrokenClass = new HashSet<string>();

                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    var go = t.gameObject;
                    int missingCount = UnityEditor.GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go);
                    if (missingCount <= 0) continue;
                    affectedCount++;

                    var goPath = GetGameObjectPath(go);
                    report.AppendLine($"• {goPath}  ({missingCount} missing slot(s))");

                    // Collect GUIDs that appear in the serialized dump of this GameObject and
                    // its non-null components. EditorJsonUtility includes m_Script references
                    // for unresolved slots in the GameObject-level dump, so this surfaces the
                    // orphan GUIDs Unity's dependency tracker has dropped.
                    var seenGuids = new HashSet<string>();
                    Action<string> harvest = json =>
                    {
                        if (string.IsNullOrEmpty(json)) return;
                        foreach (System.Text.RegularExpressions.Match m in guidRx.Matches(json))
                            seenGuids.Add(m.Groups[1].Value);
                    };

                    try { harvest(UnityEditor.EditorJsonUtility.ToJson(go, false)); }
                    catch { }

                    foreach (var comp in go.GetComponents<Component>())
                    {
                        if (comp == null) continue; // null = the missing slot itself; skip
                        try { harvest(UnityEditor.EditorJsonUtility.ToJson(comp, false)); }
                        catch { }
                    }


                    foreach (var guid in seenGuids)
                    {
                        var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                        if (string.IsNullOrEmpty(assetPath) || !System.IO.File.Exists(assetPath))
                        {
                            allUnresolved.Add(guid);
                            report.AppendLine($"    ► orphan GUID: {guid}  (no asset at this GUID)");
                        }
                        else if (assetPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                              || assetPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                        {
                            var ms = AssetDatabase.LoadAssetAtPath<MonoScript>(assetPath);
                            if (ms == null || ms.GetClass() == null)
                            {
                                allBrokenClass.Add($"{assetPath} (guid: {guid})");
                                report.AppendLine($"    ► class fails to load: {assetPath}  (guid: {guid})");
                            }
                        }
                    }
                }

                // Always list every GUID this prefab still references, regardless of whether
                // any slot is missing. AssetDatabase.GetDependencies returns asset paths Unity
                // can still resolve; we map each to its GUID. Orphan GUIDs (asset deleted, GUID
                // surviving only in prefab text) are not present here — Unity's tracker has
                // dropped them and no public API exposes them without text parsing.
                var dependencyGuids = new List<string>();
                try
                {
                    foreach (var dep in AssetDatabase.GetDependencies(prefabPath, recursive: true))
                    {
                        var g = AssetDatabase.AssetPathToGUID(dep);
                        if (!string.IsNullOrEmpty(g))
                            dependencyGuids.Add($"{g}  {dep}");
                    }
                }
                catch (Exception ex) { report.AppendLine($"[dep walk error: {ex.Message}]"); }

                if (affectedCount == 0)
                {
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine($"No missing scripts found in {prefabPath}");
                    sb.AppendLine($"Resolvable dependency GUIDs ({dependencyGuids.Count}):");
                    foreach (var g in dependencyGuids) sb.AppendLine($"  {g}");
                    task.result = sb.ToString();
                    return true;
                }

                if (allUnresolved.Count > 0)
                    report.AppendLine($"\nUnresolved orphan GUIDs (asset deleted, GUID survives in prefab): {string.Join(", ", allUnresolved)}");
                if (allBrokenClass.Count > 0)
                    report.AppendLine($"Scripts whose class fails to load: {string.Join("; ", allBrokenClass)}");

                report.AppendLine($"\nResolvable dependency GUIDs ({dependencyGuids.Count}) — does NOT include orphan GUIDs from the missing slots above:");
                foreach (var g in dependencyGuids) report.AppendLine($"  {g}");

                report.Insert(0, $"Missing-script report for {prefabPath} — {affectedCount} GameObject(s) affected:\n");
                task.result = report.ToString();
                Debug.Log($"[SceneTaskExecutor] {task.result}");
                return true;
            }
            finally
            {
                if (loadedTemp)
                    PrefabUtility.UnloadPrefabContents(root);
            }
        }

        /// <summary>
        /// Sets the parent of a GameObject using Transform.Find() for Prefab Mode.
        /// Parameters:
        ///   - path: GameObject name to find via FindInActiveContext()
        ///   - parentPath: Relative path from prefab root (e.g., "O2_PSC_Driver/O2_PSC/CART_BASE_FRAME/Console/sus_op_ctrl/Visuals/sus_op_mesh/XiPSC_Boom_Rotation")
        /// </summary>
        private bool SetParentByTransform(Task task)
        {
            string path = GetParam(task, "path");
            string parentPath = GetParam(task, "parentPath");

            var go = FindInActiveContext(path);
            if (go == null)
            {
                task.error = $"GameObject not found: {path}";
                return false;
            }

            // Check if we're in Prefab Mode
            var stage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
            if (stage == null)
            {
                task.error = "Not in Prefab Mode - use SetParent instead";
                return false;
            }

            // Use Transform.Find() to locate parent from prefab root
            var prefabRoot = stage.prefabContentsRoot;
            Transform parentTransform = prefabRoot.transform;

            // Split the path and traverse using Transform.Find()
            string[] pathParts = parentPath.Split('/');
            foreach (string part in pathParts)
            {
                parentTransform = parentTransform.Find(part);
                if (parentTransform == null)
                {
                    task.error = $"Parent GameObject not found via Transform.Find: {parentPath} (failed at: {part})";
                    return false;
                }
            }

            go.transform.SetParent(parentTransform);
            task.result = $"Set parent via Transform.Find: {path} -> {parentPath}";
            Debug.Log($"[SceneTaskExecutor] Set parent via Transform.Find: {path} -> {parentPath}");
            return true;
        }

        // ============================================================
        // ADDITIONAL GAMEOBJECT ACTIONS
        // ============================================================

        /// <summary>
        /// Activate or deactivate a GameObject.
        /// Parameters: path, active (true/false), recursive (optional, default false)
        /// </summary>
        private bool SetActive(Task task)
        {
            string path = GetParam(task, "path");
            string activeStr = GetParam(task, "active");
            string recursiveStr = GetOptionalParam(task, "recursive", "false");

            var go = FindInActiveContext(path);
            // Also search inactive objects if not found
            if (go == null)
            {
                var allObjects = Resources.FindObjectsOfTypeAll<GameObject>();
                foreach (var obj in allObjects)
                {
                    if (obj.hideFlags != HideFlags.None) continue;
                    if (GetGameObjectPath(obj) == path)
                    {
                        go = obj;
                        break;
                    }
                }
            }

            if (go == null)
            {
                task.error = $"GameObject not found: {path}";
                return false;
            }

            bool active = bool.Parse(activeStr);
            bool recursive = bool.Parse(recursiveStr);

            if (recursive)
                SetActiveRecursive(go, active);
            else
                go.SetActive(active);

            EditorUtility.SetDirty(go);
            Debug.Log($"[SceneTaskExecutor] SetActive: {path} = {active} ✓");
            return true;
        }

        private void SetActiveRecursive(GameObject go, bool active)
        {
            go.SetActive(active);
            foreach (Transform child in go.transform)
                SetActiveRecursive(child.gameObject, active);
        }

        private string GetGameObjectPath(GameObject go)
        {
            string path = go.name;
            Transform current = go.transform.parent;
            while (current != null)
            {
                path = current.name + "/" + path;
                current = current.parent;
            }
            return path;
        }

        /// <summary>
        /// Set the sibling index of a GameObject to control order in hierarchy.
        /// Parameters: path, index (int)
        /// </summary>
        private bool SetSiblingIndex(Task task)
        {
            string path = GetParam(task, "path");
            string indexStr = GetParam(task, "index");

            var go = FindInActiveContext(path);
            if (go == null)
            {
                task.error = $"GameObject not found: {path}";
                return false;
            }

            int index = int.Parse(indexStr);
            go.transform.SetSiblingIndex(index);
            EditorUtility.SetDirty(go);
            Debug.Log($"[SceneTaskExecutor] SetSiblingIndex: {path} = {index} ✓");
            return true;
        }

        /// <summary>
        /// Duplicate a GameObject with all components and children.
        /// Parameters: path, newName (optional, default "&lt;name&gt;_Copy")
        /// Idempotent: if a sibling with the resolved name already exists, succeeds without duplicating.
        /// </summary>
        private bool DuplicateGameObject(Task task)
        {
            string path = GetParam(task, "path");
            string newName = GetOptionalParam(task, "newName");

            var go = FindInActiveContext(path);
            if (go == null)
            {
                task.error = $"GameObject not found: {path}";
                return false;
            }

            string resolvedName = string.IsNullOrEmpty(newName) ? go.name + "_Copy" : newName;

            string parentPath = go.transform.parent != null ? GetGameObjectPath(go.transform.parent.gameObject) : "";
            string expectedPath = string.IsNullOrEmpty(parentPath) ? resolvedName : $"{parentPath}/{resolvedName}";

            if (FindInActiveContext(expectedPath) != null)
            {
                Debug.Log($"[SceneTaskExecutor] DuplicateGameObject skipped (already exists): {expectedPath}");
                return true;
            }

            var clone = UnityEngine.Object.Instantiate(go, go.transform.parent);
            clone.name = resolvedName;

            EditorUtility.SetDirty(clone);
            Debug.Log($"[SceneTaskExecutor] DuplicateGameObject: {path} → {clone.name} ✓");
            return true;
        }

        /// <summary>
        /// Move a GameObject to a different parent.
        /// Parameters: path, newParent, worldPositionStays (optional, default false).
        /// Empty newParent moves to scene root.
        /// </summary>
        private bool MoveGameObject(Task task)
        {
            string path = GetParam(task, "path");
            string newParent = GetParam(task, "newParent");
            string worldPositionStaysStr = GetOptionalParam(task, "worldPositionStays", "false");
            bool worldPositionStays = bool.Parse(worldPositionStaysStr);

            var go = FindInActiveContext(path);
            if (go == null)
            {
                task.error = $"GameObject not found: {path}";
                return false;
            }

            Transform parentTransform = null;
            if (!string.IsNullOrEmpty(newParent))
            {
                var parentGo = FindInActiveContext(newParent);
                if (parentGo == null)
                {
                    task.error = $"New parent not found: {newParent}";
                    return false;
                }
                parentTransform = parentGo.transform;
            }

            go.transform.SetParent(parentTransform, worldPositionStays);
            EditorUtility.SetDirty(go);
            Debug.Log($"[SceneTaskExecutor] MoveGameObject: {path} → {newParent ?? "root"} ✓");
            return true;
        }

        /// <summary>
        /// Insert a new GameObject as a parent — either above the target (mode="self")
        /// or as an intermediate between the target and its existing children (mode="children").
        ///
        /// mode="self":
        ///   Before:  &lt;parent&gt;/&lt;target&gt;
        ///   After:   &lt;parent&gt;/&lt;newParentName&gt;/&lt;target&gt;
        ///   The new GameObject takes the target's previous sibling index; target is re-parented under it.
        ///
        /// mode="children" (default):
        ///   Before:  &lt;target&gt;/[A, B, C]
        ///   After:   &lt;target&gt;/&lt;newParentName&gt;/[A, B, C]
        ///   Target keeps its position; its existing direct children are re-parented under the new GameObject
        ///   in their original sibling order.
        ///
        /// Parameters:
        ///   - target: path of the GameObject to wrap (or whose children to wrap).
        ///   - newParentName: name of the new GameObject.
        ///   - mode: "self" | "children" (optional, default "children").
        ///   - worldPositionStays: (optional, default "false") passed to Transform.SetParent.
        ///
        /// If the target has a RectTransform, the new GameObject is created with a stretched
        /// RectTransform (anchor 0..1, zero offsets) so layout is preserved. Otherwise a plain Transform.
        ///
        /// Idempotent:
        ///   - mode="self": succeeds if &lt;parent&gt;/&lt;newParentName&gt;/&lt;target-leaf&gt; already exists.
        ///   - mode="children": succeeds if target already has exactly one child named newParentName.
        /// </summary>
        private bool WrapWithParent(Task task)
        {
            string targetPath = GetParam(task, "target");
            string newParentName = GetParam(task, "newParentName");
            string mode = GetOptionalParam(task, "mode", "children").ToLowerInvariant();
            string wpsStr = GetOptionalParam(task, "worldPositionStays", "false");
            bool worldPositionStays = bool.Parse(wpsStr);

            if (mode != "self" && mode != "children")
            {
                task.error = $"WrapWithParent mode must be 'self' or 'children', got '{mode}'";
                return false;
            }

            var target = FindInActiveContext(targetPath);

            // Idempotency for self mode: if target path no longer resolves because it was already wrapped,
            // check whether <parent>/<newParentName>/<leaf> exists and accept that as success.
            if (target == null && mode == "self")
            {
                int lastSlash = targetPath.LastIndexOf('/');
                string parentPath = lastSlash >= 0 ? targetPath.Substring(0, lastSlash) : "";
                string leaf = lastSlash >= 0 ? targetPath.Substring(lastSlash + 1) : targetPath;
                string wrappedPath = string.IsNullOrEmpty(parentPath)
                    ? $"{newParentName}/{leaf}"
                    : $"{parentPath}/{newParentName}/{leaf}";
                if (FindInActiveContext(wrappedPath) != null)
                {
                    Debug.Log($"[SceneTaskExecutor] WrapWithParent skipped (already wrapped self at {wrappedPath})");
                    return true;
                }
            }

            if (target == null)
            {
                task.error = $"Target GameObject not found: {targetPath}";
                return false;
            }

            if (mode == "self")
            {
                var parentT = target.transform.parent;
                if (parentT != null && parentT.name == newParentName && parentT.childCount == 1)
                {
                    Debug.Log($"[SceneTaskExecutor] WrapWithParent skipped (self already wrapped): {targetPath}");
                    return true;
                }
            }
            else
            {
                var existing = target.transform.Find(newParentName);
                if (existing != null && target.transform.childCount == 1)
                {
                    Debug.Log($"[SceneTaskExecutor] WrapWithParent skipped (children already wrapped): {targetPath}/{newParentName}");
                    return true;
                }
            }

            bool targetIsUI = target.GetComponent<RectTransform>() != null;
            var newGO = new GameObject(newParentName);
            if (targetIsUI)
            {
                var rt = newGO.AddComponent<RectTransform>();
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.localScale = Vector3.one;
            }

            if (mode == "self")
            {
                var originalParent = target.transform.parent;
                int siblingIndex = target.transform.GetSiblingIndex();
                newGO.transform.SetParent(originalParent, worldPositionStays);
                newGO.transform.SetSiblingIndex(siblingIndex);
                target.transform.SetParent(newGO.transform, worldPositionStays);
            }
            else // children
            {
                // Snapshot existing children BEFORE inserting newGO so it doesn't re-parent into itself.
                var toMove = new List<Transform>();
                foreach (Transform child in target.transform)
                    toMove.Add(child);

                newGO.transform.SetParent(target.transform, worldPositionStays);
                foreach (var child in toMove)
                    child.SetParent(newGO.transform, worldPositionStays);
            }

            EditorUtility.SetDirty(newGO);
            EditorUtility.SetDirty(target);
            Debug.Log($"[SceneTaskExecutor] WrapWithParent ({mode}): {targetPath} with {newParentName} ✓");
            return true;
        }

        /// <summary>
        /// Remove a component from a GameObject.
        /// Parameters: path, component (fully qualified type name)
        /// Idempotent: if the component is already absent, succeeds.
        /// </summary>
        private bool RemoveComponent(Task task)
        {
            string path = GetParam(task, "path");
            string componentName = GetParam(task, "component");

            var go = FindInActiveContext(path);
            if (go == null)
            {
                task.error = $"GameObject not found: {path}";
                return false;
            }

            var type = ResolveType(componentName);
            if (type == null)
            {
                task.error = $"Component type not found: {componentName}";
                return false;
            }

            var component = go.GetComponent(type);
            if (component == null)
            {
                Debug.Log($"[SceneTaskExecutor] Component already removed: {componentName} on {path}");
                return true;
            }

            UnityEngine.Object.DestroyImmediate(component);
            EditorUtility.SetDirty(go);
            Debug.Log($"[SceneTaskExecutor] RemoveComponent: {componentName} from {path} ✓");
            return true;
        }

        /// <summary>
        /// Set multiple properties on one component in a single task.
        /// Parameters: path, component, properties (JSON object: {"field1":"value1","field2":"value2"})
        /// </summary>
        private bool BatchSetProperty(Task task)
        {
            string goPath = GetParam(task, "path");
            string componentName = GetParam(task, "component");
            string propertiesJson = GetParam(task, "properties");

            var go = FindInActiveContext(goPath);
            if (go == null)
            {
                task.error = $"GameObject not found: {goPath}";
                return false;
            }

            var type = ResolveType(componentName);
            if (type == null)
            {
                task.error = $"Component type not found: {componentName}";
                return false;
            }

            var component = go.GetComponent(type);
            if (component == null)
            {
                task.error = $"Component {componentName} not found on {goPath}";
                return false;
            }

            var props = ParseSimpleJsonObject(propertiesJson);
            int setCount = 0;

            foreach (var kvp in props)
            {
                string fieldName = kvp.Key;
                string valuePath = kvp.Value;

                if (fieldName == "color" && component is Graphic graphic)
                {
                    graphic.color = ParseColor(valuePath);
                    setCount++;
                    continue;
                }

                var field = type.GetField(fieldName,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var property = type.GetProperty(fieldName,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                if (field != null)
                {
                    field.SetValue(component, ConvertValue(field.FieldType, valuePath));
                    setCount++;
                }
                else if (property != null && property.CanWrite)
                {
                    property.SetValue(component, ConvertValue(property.PropertyType, valuePath));
                    setCount++;
                }
                else
                {
                    Debug.LogWarning($"[SceneTaskExecutor] BatchSetProperty: field/property '{fieldName}' not found on {componentName}");
                }
            }

            EditorUtility.SetDirty(go);
            Debug.Log($"[SceneTaskExecutor] BatchSetProperty: {goPath}/{componentName} - {setCount} properties set ✓");
            return true;
        }

        // ============================================================
        // VALUE PARSING HELPERS — colors, text alignment, simple JSON
        // ============================================================

        private Dictionary<string, string> ParseSimpleJsonObject(string json)
        {
            var result = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(json)) return result;

            json = json.Trim();
            if (json.StartsWith("{")) json = json.Substring(1);
            if (json.EndsWith("}")) json = json.Substring(0, json.Length - 1);

            // Split by commas at depth 0, respecting quoted strings
            var entries = new List<string>();
            int depth = 0;
            int start = 0;
            bool inQuote = false;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '"' && (i == 0 || json[i - 1] != '\\')) inQuote = !inQuote;
                if (!inQuote)
                {
                    if (c == '{' || c == '[') depth++;
                    if (c == '}' || c == ']') depth--;
                    if (c == ',' && depth == 0)
                    {
                        entries.Add(json.Substring(start, i - start));
                        start = i + 1;
                    }
                }
            }
            if (start < json.Length)
                entries.Add(json.Substring(start));

            foreach (var entry in entries)
            {
                var colonIdx = entry.IndexOf(':');
                if (colonIdx < 0) continue;

                string key = entry.Substring(0, colonIdx).Trim().Trim('"');
                string value = entry.Substring(colonIdx + 1).Trim().Trim('"');
                result[key] = value;
            }

            return result;
        }

        private Color ParseColor(string value)
        {
            if (string.IsNullOrEmpty(value))
                return Color.white;

            value = value.Trim();

            // Hex color: #RGB, #RGBA, #RRGGBB, #RRGGBBAA
            if (value.StartsWith("#"))
            {
                if (ColorUtility.TryParseHtmlString(value, out Color hexColor))
                    return hexColor;
            }

            // Named colors
            switch (value.ToLower())
            {
                case "red": return Color.red;
                case "green": return Color.green;
                case "blue": return Color.blue;
                case "white": return Color.white;
                case "black": return Color.black;
                case "yellow": return Color.yellow;
                case "cyan": return Color.cyan;
                case "magenta": return Color.magenta;
                case "gray": case "grey": return Color.gray;
                case "clear": case "transparent": return Color.clear;
                case "orange": return new Color(1f, 0.5f, 0f, 1f);
                case "purple": return new Color(0.5f, 0f, 0.5f, 1f);
                case "brown": return new Color(0.6f, 0.3f, 0f, 1f);
                case "pink": return new Color(1f, 0.75f, 0.8f, 1f);
                case "teal": return new Color(0f, 0.5f, 0.5f, 1f);
                case "gold": return new Color(1f, 0.84f, 0f, 1f);
                case "silver": return new Color(0.75f, 0.75f, 0.75f, 1f);
                case "navy": return new Color(0f, 0f, 0.5f, 1f);
                case "lime": return new Color(0f, 1f, 0f, 1f);
                case "olive": return new Color(0.5f, 0.5f, 0f, 1f);
                case "maroon": return new Color(0.5f, 0f, 0f, 1f);
                case "coral": return new Color(1f, 0.5f, 0.31f, 1f);
            }

            // Comma-separated RGBA floats: "0.8,0.2,0.2,1"
            var parts = value.Split(',');
            if (parts.Length >= 3)
            {
                return new Color(
                    float.Parse(parts[0].Trim()),
                    float.Parse(parts[1].Trim()),
                    float.Parse(parts[2].Trim()),
                    parts.Length > 3 ? float.Parse(parts[3].Trim()) : 1f
                );
            }

            Debug.LogWarning($"[SceneTaskExecutor] Could not parse color: {value}, defaulting to white");
            return Color.white;
        }

        private object ParseTextAlignment(string value)
        {
            // Try numeric first
            if (int.TryParse(value, out int numericValue))
                return (TMPro.TextAlignmentOptions)numericValue;

            switch (value.ToLower().Replace(" ", "").Replace("-", "").Replace("_", ""))
            {
                case "topleft": return TMPro.TextAlignmentOptions.TopLeft;
                case "top": case "topcenter": return TMPro.TextAlignmentOptions.Top;
                case "topright": return TMPro.TextAlignmentOptions.TopRight;
                case "left": case "middleleft": return TMPro.TextAlignmentOptions.Left;
                case "center": case "middle": case "middlecenter": return TMPro.TextAlignmentOptions.Center;
                case "right": case "middleright": return TMPro.TextAlignmentOptions.Right;
                case "bottomleft": return TMPro.TextAlignmentOptions.BottomLeft;
                case "bottom": case "bottomcenter": return TMPro.TextAlignmentOptions.Bottom;
                case "bottomright": return TMPro.TextAlignmentOptions.BottomRight;
                case "justified": case "topjustified": return TMPro.TextAlignmentOptions.TopJustified;
                default: return null;
            }
        }

        // ============================================================
        // GetProperty — structured single-value read via reflection
        // ============================================================

        /// <summary>
        /// Reads one field or property from a component and returns it as a structured JSON
        /// value in task.result. Fields checked first, then properties. Index properties are
        /// skipped. Unity-object references return { name, type, instanceId }.
        /// Parameters: path, component (fully-qualified type name), field.
        /// </summary>
        private bool GetProperty(Task task)
        {
            string ownerPath = GetOptionalParam(task, "owner");
            string path = GetOptionalParam(task, "path");
            string componentName = GetOptionalParam(task, "component");
            string fieldName = GetParam(task, "field");

            if (string.IsNullOrEmpty(fieldName))
            {
                task.error = "GetProperty requires 'field' parameter";
                return false;
            }

            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance;

            // ScriptableObject asset: owner=*.asset, no path/component.
            if (!string.IsNullOrEmpty(ownerPath) && ownerPath.EndsWith(".asset") &&
                string.IsNullOrEmpty(path) && string.IsNullOrEmpty(componentName))
            {
                var so = AssetDatabase.LoadAssetAtPath<ScriptableObject>(ownerPath);
                if (so == null)
                {
                    task.error = $"ScriptableObject not found: {ownerPath}";
                    return false;
                }
                if (!TryReadMember(so.GetType(), so, fieldName, flags, out object soVal, out string soType, out string soErr))
                {
                    task.error = soErr;
                    return false;
                }
                task.result = SerializeReflectedValue(soType, soVal);
                Debug.Log($"[SceneTaskExecutor] GetProperty (SO) {ownerPath}.{fieldName} → {soType} ✓");
                return true;
            }

            // Scene GameObject (legacy / current behavior): path + component + field.
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(componentName))
            {
                task.error = "GetProperty requires either 'path'+'component' (scene GO) or 'owner' (ScriptableObject asset path)";
                return false;
            }

            var go = FindInActiveContext(path);
            if (go == null)
            {
                task.error = $"GameObject not found: {path}";
                return false;
            }

            System.Type type = ResolveType(componentName);
            if (type == null)
            {
                task.error = $"Component type not found: {componentName}";
                return false;
            }

            var component = go.GetComponent(type);
            if (component == null)
            {
                task.error = $"Component {componentName} not found on {path}";
                return false;
            }

            if (!TryReadMember(type, component, fieldName, flags, out object value, out string resolvedTypeName, out string err))
            {
                task.error = err;
                return false;
            }

            task.result = SerializeReflectedValue(resolvedTypeName, value);
            Debug.Log($"[SceneTaskExecutor] GetProperty {path}/{componentName}.{fieldName} → {resolvedTypeName} ✓");
            return true;
        }

        // Shared field/property reader used by GetProperty for both scene components and SO assets.
        private static bool TryReadMember(System.Type type, object target, string fieldName,
            System.Reflection.BindingFlags flags, out object value, out string resolvedTypeName, out string error)
        {
            value = null; resolvedTypeName = null; error = null;
            var field = type.GetField(fieldName, flags);
            if (field != null)
            {
                value = field.GetValue(target);
                resolvedTypeName = field.FieldType.Name;
                return true;
            }
            var prop = type.GetProperty(fieldName, flags);
            if (prop == null || !prop.CanRead)
            {
                error = $"Field or readable property '{fieldName}' not found on {type.Name}";
                return false;
            }
            if (prop.GetIndexParameters().Length > 0)
            {
                error = $"Indexed property '{fieldName}' cannot be read via GetProperty";
                return false;
            }
            value = prop.GetValue(target);
            resolvedTypeName = prop.PropertyType.Name;
            return true;
        }

        private static string SerializeReflectedValue(string typeName, object value)
        {
            // Minimal structured JSON so scenario consumers can parse deterministically.
            if (value == null)
                return $"{{\"type\":\"{typeName}\",\"value\":null}}";

            switch (value)
            {
                case bool b:
                    return $"{{\"type\":\"{typeName}\",\"value\":{(b ? "true" : "false")}}}";
                case int i:
                    return $"{{\"type\":\"{typeName}\",\"value\":{i}}}";
                case long l:
                    return $"{{\"type\":\"{typeName}\",\"value\":{l}}}";
                case float f:
                    return $"{{\"type\":\"{typeName}\",\"value\":{f.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}";
                case double d:
                    return $"{{\"type\":\"{typeName}\",\"value\":{d.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}";
                case string s:
                    return $"{{\"type\":\"{typeName}\",\"value\":\"{EscapeJson(s)}\"}}";
                case Vector2 v2:
                    return $"{{\"type\":\"{typeName}\",\"value\":{{\"x\":{FloatStr(v2.x)},\"y\":{FloatStr(v2.y)}}}}}";
                case Vector3 v3:
                    return $"{{\"type\":\"{typeName}\",\"value\":{{\"x\":{FloatStr(v3.x)},\"y\":{FloatStr(v3.y)},\"z\":{FloatStr(v3.z)}}}}}";
                case Vector4 v4:
                    return $"{{\"type\":\"{typeName}\",\"value\":{{\"x\":{FloatStr(v4.x)},\"y\":{FloatStr(v4.y)},\"z\":{FloatStr(v4.z)},\"w\":{FloatStr(v4.w)}}}}}";
                case Color c:
                    return $"{{\"type\":\"{typeName}\",\"value\":{{\"r\":{FloatStr(c.r)},\"g\":{FloatStr(c.g)},\"b\":{FloatStr(c.b)},\"a\":{FloatStr(c.a)}}}}}";
                case Quaternion q:
                    return $"{{\"type\":\"{typeName}\",\"value\":{{\"x\":{FloatStr(q.x)},\"y\":{FloatStr(q.y)},\"z\":{FloatStr(q.z)},\"w\":{FloatStr(q.w)}}}}}";
                case Rect r:
                    return $"{{\"type\":\"{typeName}\",\"value\":{{\"x\":{FloatStr(r.x)},\"y\":{FloatStr(r.y)},\"width\":{FloatStr(r.width)},\"height\":{FloatStr(r.height)}}}}}";
                case Enum e:
                    return $"{{\"type\":\"{typeName}\",\"value\":\"{e}\",\"intValue\":{Convert.ToInt32(e)}}}";
                case UnityEngine.Object uo:
                    return uo == null
                        ? $"{{\"type\":\"{typeName}\",\"value\":null}}"
                        : $"{{\"type\":\"{typeName}\",\"value\":{{\"name\":\"{EscapeJson(uo.name)}\",\"type\":\"{uo.GetType().Name}\",\"instanceId\":{uo.GetInstanceID()}}}}}";
                default:
                    return $"{{\"type\":\"{typeName}\",\"value\":\"{EscapeJson(value.ToString())}\"}}";
            }
        }

        private static string FloatStr(float f) => f.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }

        // ============================================================
        // FindObjects — generic path/component query
        // ============================================================

        /// <summary>
        /// Returns JSON array of GameObject paths in task.result.
        /// Parameters:
        ///   - root: starting path (empty or "/" = scene root)
        ///   - withComponent: (optional) fully-qualified type name; only include GameObjects that have this component
        ///   - pathFilter: (optional) substring match on the full GameObject path
        ///   - maxDepth: (optional, default -1 unlimited) 0 = root only, N = N levels below root
        /// </summary>
        private bool FindObjects(Task task)
        {
            string rootPath = GetOptionalParam(task, "root", "");
            string withComponentName = GetOptionalParam(task, "withComponent");
            string pathFilter = GetOptionalParam(task, "pathFilter");
            int maxDepth = int.Parse(GetOptionalParam(task, "maxDepth", "-1"));

            System.Type componentType = null;
            if (!string.IsNullOrEmpty(withComponentName))
            {
                componentType = ResolveType(withComponentName);
                if (componentType == null)
                {
                    task.error = $"Component type not found: {withComponentName}";
                    return false;
                }
            }

            var matches = new List<string>();
            var roots = new List<Transform>();

            if (string.IsNullOrEmpty(rootPath) || rootPath == "/")
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                foreach (var go in scene.GetRootGameObjects())
                    roots.Add(go.transform);
            }
            else
            {
                var go = FindInActiveContext(rootPath);
                if (go == null)
                {
                    task.error = $"Root GameObject not found: {rootPath}";
                    return false;
                }
                roots.Add(go.transform);
            }

            foreach (var rootT in roots)
                WalkForFind(rootT, 0, maxDepth, componentType, pathFilter, matches);

            var sb = new System.Text.StringBuilder();
            sb.Append("{\"count\":").Append(matches.Count).Append(",\"paths\":[");
            for (int i = 0; i < matches.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(EscapeJson(matches[i])).Append('"');
            }
            sb.Append("]}");
            task.result = sb.ToString();
            Debug.Log($"[SceneTaskExecutor] FindObjects from '{(string.IsNullOrEmpty(rootPath) ? "/" : rootPath)}': {matches.Count} matches ✓");
            return true;
        }

        private static void WalkForFind(Transform t, int depth, int maxDepth, System.Type componentType, string pathFilter, List<string> matches)
        {
            string path = GetTransformPath(t);

            bool pathOk = string.IsNullOrEmpty(pathFilter) || path.Contains(pathFilter);
            bool compOk = componentType == null || t.GetComponent(componentType) != null;
            if (pathOk && compOk)
                matches.Add(path);

            if (maxDepth >= 0 && depth >= maxDepth) return;
            foreach (Transform child in t)
                WalkForFind(child, depth + 1, maxDepth, componentType, pathFilter, matches);
        }

        private static string GetTransformPath(Transform t)
        {
            var stack = new Stack<string>();
            var cur = t;
            while (cur != null)
            {
                stack.Push(cur.name);
                cur = cur.parent;
            }
            return string.Join("/", stack);
        }

        // ============================================================
        // ApplyRecipe — compose SCENE primitives via user JSON recipe
        // ============================================================

        /// <summary>
        /// Load a recipe file, substitute {{param}} tokens with provided values, and execute
        /// the inner tasks sequentially via the same executor. Nested ApplyRecipe is supported.
        /// Parameters:
        ///   - recipeFile: path to recipe JSON
        ///   - params: (optional) JSON object of param-name to value, e.g. '{"title":"Activities","color":"#80CBC4"}'
        /// Recipe JSON shape:
        ///   { "name":"wireframe-card",
        ///     "params":["parent","title","color"],
        ///     "tasks":[ { "action":"AddGameObject", "parameters":[{"key":"name","value":"Card_{{title}}"}] }, ... ] }
        /// </summary>
        private bool ApplyRecipe(Task task)
        {
            string recipeFile = GetParam(task, "recipeFile");
            string paramsJson = GetOptionalParam(task, "params", "{}");

            if (!System.IO.File.Exists(recipeFile))
            {
                task.error = $"Recipe file not found: {recipeFile}";
                return false;
            }

            string recipeJson;
            try { recipeJson = System.IO.File.ReadAllText(recipeFile); }
            catch (Exception e) { task.error = $"Recipe read error: {e.Message}"; return false; }

            Dictionary<string, string> providedParams;
            try { providedParams = ParseSimpleJsonObject(paramsJson); }
            catch (Exception e) { task.error = $"Params JSON parse error: {e.Message}"; return false; }

            // Substitute {{param}} across the recipe body.
            foreach (var kvp in providedParams)
                recipeJson = recipeJson.Replace("{{" + kvp.Key + "}}", kvp.Value);

            // Detect un-substituted tokens — likely missing required param.
            var unresolved = System.Text.RegularExpressions.Regex.Match(recipeJson, @"\{\{(\w+)\}\}");
            if (unresolved.Success)
            {
                task.error = $"Recipe has unresolved param token: {{{{{unresolved.Groups[1].Value}}}}}. Supply it in the params object.";
                return false;
            }

            // Parse the recipe JSON into a Task list using Newtonsoft (already referenced by SceneHierarchyExporter).
            List<Task> recipeTasks;
            try
            {
                var parsed = Newtonsoft.Json.Linq.JObject.Parse(recipeJson);
                var tasksToken = parsed["tasks"];
                if (tasksToken == null)
                {
                    task.error = "Recipe JSON has no 'tasks' array.";
                    return false;
                }
                recipeTasks = new List<Task>();
                foreach (var tObj in tasksToken)
                {
                    var inner = new Task
                    {
                        action = tObj["action"]?.ToString(),
                        parameters = new List<TaskParameter>()
                    };
                    var paramsToken = tObj["parameters"];
                    if (paramsToken != null)
                    {
                        foreach (var p in paramsToken)
                        {
                            inner.parameters.Add(new TaskParameter
                            {
                                key = p["key"]?.ToString(),
                                value = p["value"]?.ToString()
                            });
                        }
                    }
                    recipeTasks.Add(inner);
                }
            }
            catch (Exception e)
            {
                task.error = $"Recipe JSON parse error: {e.Message}";
                return false;
            }

            // Execute each task via this same executor (synchronous primitives only; async not supported inside a recipe for now).
            int i = 0;
            foreach (var inner in recipeTasks)
            {
                i++;
                bool ok;
                try { ok = Execute(inner); }
                catch (Exception e) { task.error = $"Recipe task #{i} ({inner.action}) threw: {e.Message}"; return false; }
                if (!ok)
                {
                    task.error = $"Recipe task #{i} ({inner.action}) failed: {inner.error}";
                    return false;
                }
            }

            task.result = $"Recipe '{System.IO.Path.GetFileName(recipeFile)}' executed {recipeTasks.Count} tasks successfully.";
            Debug.Log($"[SceneTaskExecutor] ApplyRecipe {recipeFile} — {recipeTasks.Count} tasks ✓");
            return true;
        }

        // ============================================================
        // SetPropertyOnMatching — find + set in one call (generic, no package coupling)
        // ============================================================

        /// <summary>
        /// For every GameObject under 'root' that has 'withComponent', set 'field' = 'value' on that component.
        /// Parameters: root (path or empty = scene root), withComponent (fq type), field, value,
        /// pathFilter (optional substring), maxDepth (optional, default -1 unlimited).
        /// Returns a JSON summary in task.result: {"matched":N,"set":M,"skipped":K,"paths":[...]}.
        /// </summary>
        private bool SetPropertyOnMatching(Task task)
        {
            string rootPath = GetOptionalParam(task, "root", "");
            string componentName = GetParam(task, "withComponent");
            string fieldName = GetParam(task, "field");
            string valueStr = GetParam(task, "value");
            string pathFilter = GetOptionalParam(task, "pathFilter");
            int maxDepth = int.Parse(GetOptionalParam(task, "maxDepth", "-1"));

            System.Type componentType = ResolveType(componentName);
            if (componentType == null)
            {
                task.error = $"Component type not found: {componentName}";
                return false;
            }

            var matches = new List<string>();
            var roots = new List<Transform>();

            if (string.IsNullOrEmpty(rootPath) || rootPath == "/")
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                foreach (var go in scene.GetRootGameObjects())
                    roots.Add(go.transform);
            }
            else
            {
                var go = FindInActiveContext(rootPath);
                if (go == null)
                {
                    task.error = $"Root GameObject not found: {rootPath}";
                    return false;
                }
                roots.Add(go.transform);
            }

            foreach (var rootT in roots)
                WalkForFind(rootT, 0, maxDepth, componentType, pathFilter, matches);

            int setCount = 0;
            int skipCount = 0;
            var failures = new List<string>();

            foreach (var path in matches)
            {
                var innerTask = new Task
                {
                    action = "SetProperty",
                    parameters = new List<TaskParameter>
                    {
                        new TaskParameter { key = "path", value = path },
                        new TaskParameter { key = "component", value = componentName },
                        new TaskParameter { key = "field", value = fieldName },
                        new TaskParameter { key = "value", value = valueStr },
                    }
                };
                bool ok = SetPropertyOnGameObject(innerTask, path, componentName, fieldName, valueStr);
                if (ok) setCount++;
                else { skipCount++; failures.Add($"{path}: {innerTask.error}"); }
            }

            var sb = new System.Text.StringBuilder();
            sb.Append("{\"matched\":").Append(matches.Count)
              .Append(",\"set\":").Append(setCount)
              .Append(",\"skipped\":").Append(skipCount);
            if (failures.Count > 0)
            {
                sb.Append(",\"failures\":[");
                for (int i = 0; i < failures.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(EscapeJson(failures[i])).Append('"');
                }
                sb.Append("]");
            }
            sb.Append("}");
            task.result = sb.ToString();
            Debug.Log($"[SceneTaskExecutor] SetPropertyOnMatching on {componentName}.{fieldName}: matched={matches.Count} set={setCount} skipped={skipCount} ✓");
            return skipCount == 0;
        }

        // ============================================================
        // AddComponentToMatching — add a component to every match in one call
        // ============================================================

        /// <summary>
        /// For every GameObject under 'root' that passes the withComponent + pathFilter + maxDepth filter,
        /// add 'component' to that GameObject. Idempotent per GameObject: already-present components are
        /// counted as 'alreadyHad' rather than re-added. Mirrors SetPropertyOnMatching's filter semantics.
        ///
        /// Parameters:
        ///   - root: (optional) path or "" for active-scene root.
        ///   - component: fully qualified type name of the component to add (required).
        ///   - withComponent: (optional) filter — only GameObjects that already have this component type are affected.
        ///   - pathFilter: (optional) substring filter on the GameObject's full scene path.
        ///   - maxDepth: (optional, default -1 unlimited) walk depth from each root.
        ///
        /// Returns JSON in task.result: {"matched":N,"added":M,"alreadyHad":K,"failed":F,"failures":[...]}.
        /// Task succeeds if failed == 0.
        /// </summary>
        private bool AddComponentToMatching(Task task)
        {
            string rootPath = GetOptionalParam(task, "root", "");
            string componentToAdd = GetParam(task, "component");
            string withComponentName = GetOptionalParam(task, "withComponent");
            string pathFilter = GetOptionalParam(task, "pathFilter");
            int maxDepth = int.Parse(GetOptionalParam(task, "maxDepth", "-1"));

            System.Type addType = ResolveType(componentToAdd);
            if (addType == null)
            {
                task.error = $"Component type to add not found: {componentToAdd}";
                return false;
            }

            System.Type filterType = null;
            if (!string.IsNullOrEmpty(withComponentName))
            {
                filterType = ResolveType(withComponentName);
                if (filterType == null)
                {
                    task.error = $"Filter component type not found: {withComponentName}";
                    return false;
                }
            }

            var matches = new List<string>();
            var roots = new List<Transform>();

            if (string.IsNullOrEmpty(rootPath) || rootPath == "/")
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                foreach (var go in scene.GetRootGameObjects())
                    roots.Add(go.transform);
            }
            else
            {
                var go = FindInActiveContext(rootPath);
                if (go == null)
                {
                    task.error = $"Root GameObject not found: {rootPath}";
                    return false;
                }
                roots.Add(go.transform);
            }

            foreach (var rootT in roots)
                WalkForFind(rootT, 0, maxDepth, filterType, pathFilter, matches);

            int added = 0;
            int alreadyHad = 0;
            var failures = new List<string>();

            foreach (var path in matches)
            {
                var go = FindInActiveContext(path);
                if (go == null)
                {
                    failures.Add($"{path}: vanished before add");
                    continue;
                }
                if (go.GetComponent(addType) != null)
                {
                    alreadyHad++;
                    continue;
                }
                try
                {
                    go.AddComponent(addType);
                    added++;
                    EditorUtility.SetDirty(go);
                }
                catch (Exception e)
                {
                    failures.Add($"{path}: {e.Message}");
                }
            }

            var sb = new System.Text.StringBuilder();
            sb.Append("{\"matched\":").Append(matches.Count)
              .Append(",\"added\":").Append(added)
              .Append(",\"alreadyHad\":").Append(alreadyHad)
              .Append(",\"failed\":").Append(failures.Count);
            if (failures.Count > 0)
            {
                sb.Append(",\"failures\":[");
                for (int i = 0; i < failures.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(EscapeJson(failures[i])).Append('"');
                }
                sb.Append("]");
            }
            sb.Append("}");
            task.result = sb.ToString();
            Debug.Log($"[SceneTaskExecutor] AddComponentToMatching {componentToAdd}: matched={matches.Count} added={added} alreadyHad={alreadyHad} failed={failures.Count} ✓");
            return failures.Count == 0;
        }

        // ============================================================
        // Validate(rulesFile) — framework-agnostic assertion runner
        // ============================================================

        /// <summary>
        /// Run invariant rules loaded from a user JSON file against the current scene.
        /// Parameters:
        ///   - rulesFile: path to rules JSON
        ///   - strict: (optional, default "true") if true, the task fails when any rule has violations
        /// Rules JSON shape:
        ///   {
        ///     "rules": [
        ///       {
        ///         "name": "button-text-centered",
        ///         "target": { "root": "", "pathFilter": "BtnText", "hasComponent": "TMPro.TextMeshProUGUI, Unity.TextMeshPro" },
        ///         "assert": { "component": "TMPro.TextMeshProUGUI, Unity.TextMeshPro", "field": "alignment", "equals": "Center" }
        ///       }
        ///     ]
        ///   }
        /// Supported assert primitives: equals, notEquals, isNull, in (array), regex, hasComponent.
        /// Supported composition: all (AND), any (OR), not (NOT).
        /// </summary>
        private bool Validate(Task task)
        {
            string rulesFile = GetParam(task, "rulesFile");
            bool strict = bool.Parse(GetOptionalParam(task, "strict", "true"));

            if (!System.IO.File.Exists(rulesFile))
            {
                task.error = $"Rules file not found: {rulesFile}";
                return false;
            }

            string rulesJson;
            try { rulesJson = System.IO.File.ReadAllText(rulesFile); }
            catch (Exception e) { task.error = $"Rules read error: {e.Message}"; return false; }

            Newtonsoft.Json.Linq.JObject rulesRoot;
            try { rulesRoot = Newtonsoft.Json.Linq.JObject.Parse(rulesJson); }
            catch (Exception e) { task.error = $"Rules JSON parse error: {e.Message}"; return false; }

            var rulesToken = rulesRoot["rules"];
            if (rulesToken == null)
            {
                task.error = "Rules file has no 'rules' array.";
                return false;
            }

            var perRuleResults = new List<(string name, int targeted, int violations, List<string> details)>();
            int totalRules = 0, rulesWithViolations = 0, totalViolations = 0;

            foreach (var ruleTok in rulesToken)
            {
                totalRules++;
                string ruleName = ruleTok["name"]?.ToString() ?? $"rule_{totalRules}";
                var targetTok = ruleTok["target"];
                var assertTok = ruleTok["assert"];

                if (targetTok == null || assertTok == null)
                {
                    perRuleResults.Add((ruleName, 0, 1, new List<string> { "Rule missing 'target' or 'assert'" }));
                    rulesWithViolations++;
                    totalViolations++;
                    continue;
                }

                var targets = ResolveValidationTargets(targetTok);
                int ruleViolations = 0;
                var details = new List<string>();

                foreach (var targetPath in targets)
                {
                    var go = FindInActiveContext(targetPath);
                    if (go == null) continue; // vanished between resolution and assert
                    bool ok;
                    string detail;
                    try
                    {
                        ok = EvaluateAssertion(assertTok, go, out detail);
                    }
                    catch (Exception e)
                    {
                        ok = false;
                        detail = $"assert error: {e.Message}";
                    }
                    if (!ok)
                    {
                        ruleViolations++;
                        details.Add($"{targetPath}: {detail}");
                    }
                }

                if (ruleViolations > 0)
                {
                    rulesWithViolations++;
                    totalViolations += ruleViolations;
                }

                perRuleResults.Add((ruleName, targets.Count, ruleViolations, details));
            }

            // Build structured result.
            var sb = new System.Text.StringBuilder();
            sb.Append("{\"totalRules\":").Append(totalRules)
              .Append(",\"rulesWithViolations\":").Append(rulesWithViolations)
              .Append(",\"totalViolations\":").Append(totalViolations)
              .Append(",\"rules\":[");
            for (int i = 0; i < perRuleResults.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var r = perRuleResults[i];
                sb.Append("{\"name\":\"").Append(EscapeJson(r.name)).Append("\"")
                  .Append(",\"targeted\":").Append(r.targeted)
                  .Append(",\"violations\":").Append(r.violations)
                  .Append(",\"details\":[");
                for (int j = 0; j < r.details.Count; j++)
                {
                    if (j > 0) sb.Append(',');
                    sb.Append('"').Append(EscapeJson(r.details[j])).Append('"');
                }
                sb.Append("]}");
            }
            sb.Append("]}");
            task.result = sb.ToString();

            Debug.Log($"[SceneTaskExecutor] Validate: {totalRules} rules, {rulesWithViolations} with violations, {totalViolations} total violations");

            if (strict && totalViolations > 0)
            {
                task.error = $"Validation failed: {totalViolations} violation(s) across {rulesWithViolations}/{totalRules} rule(s). See task.result for details.";
                return false;
            }
            return true;
        }

        private List<string> ResolveValidationTargets(Newtonsoft.Json.Linq.JToken targetTok)
        {
            string rootPath = targetTok["root"]?.ToString() ?? "";
            string pathFilter = targetTok["pathFilter"]?.ToString();
            string hasComponentName = targetTok["hasComponent"]?.ToString();
            int maxDepth = targetTok["maxDepth"] != null ? (int)targetTok["maxDepth"] : -1;

            System.Type componentType = null;
            if (!string.IsNullOrEmpty(hasComponentName))
                componentType = ResolveType(hasComponentName);

            var matches = new List<string>();
            var roots = new List<Transform>();

            if (string.IsNullOrEmpty(rootPath) || rootPath == "/")
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                foreach (var go in scene.GetRootGameObjects())
                    roots.Add(go.transform);
            }
            else
            {
                var go = FindInActiveContext(rootPath);
                if (go != null) roots.Add(go.transform);
            }

            foreach (var rootT in roots)
                WalkForFind(rootT, 0, maxDepth, componentType, pathFilter, matches);
            return matches;
        }

        private bool EvaluateAssertion(Newtonsoft.Json.Linq.JToken assertTok, GameObject go, out string detail)
        {
            detail = "";

            // Composition: not, all, any
            var notTok = assertTok["not"];
            if (notTok != null)
            {
                bool inner = EvaluateAssertion(notTok, go, out var innerDetail);
                if (inner)
                {
                    detail = $"NOT failed: inner asserted true ({innerDetail})";
                    return false;
                }
                return true;
            }

            var allTok = assertTok["all"] as Newtonsoft.Json.Linq.JArray;
            if (allTok != null)
            {
                foreach (var child in allTok)
                {
                    if (!EvaluateAssertion(child, go, out var innerDetail))
                    {
                        detail = $"AND failed: {innerDetail}";
                        return false;
                    }
                }
                return true;
            }

            var anyTok = assertTok["any"] as Newtonsoft.Json.Linq.JArray;
            if (anyTok != null)
            {
                foreach (var child in anyTok)
                {
                    if (EvaluateAssertion(child, go, out _)) return true;
                }
                detail = $"OR failed: no child asserted true";
                return false;
            }

            // Leaf assertion: direct component-presence check (no field required).
            // { "hasComponent": "Namespace.Type, AssemblyName" }
            var hasCompTok = assertTok["hasComponent"];
            if (hasCompTok != null)
            {
                string typeName = hasCompTok.ToString();
                var presenceType = ResolveType(typeName);
                if (presenceType == null)
                {
                    detail = $"hasComponent: type not resolvable: {typeName}";
                    return false;
                }
                if (go.GetComponent(presenceType) != null) return true;
                detail = $"hasComponent: {typeName} not present on {go.name}";
                return false;
            }

            // Leaf assertion: read a field value on a component and compare.
            string componentName = assertTok["component"]?.ToString();
            string fieldName = assertTok["field"]?.ToString();
            if (string.IsNullOrEmpty(componentName) || string.IsNullOrEmpty(fieldName))
            {
                detail = "leaf assertion missing 'component'/'field' (or 'hasComponent')";
                return false;
            }

            var type = ResolveType(componentName);
            if (type == null)
            {
                detail = $"component type not resolvable: {componentName}";
                return false;
            }

            var component = go.GetComponent(type);
            if (component == null)
            {
                detail = $"component {componentName} not on GameObject";
                return false;
            }

            const System.Reflection.BindingFlags rf =
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance;
            object actual;
            var field = type.GetField(fieldName, rf);
            if (field != null) actual = field.GetValue(component);
            else
            {
                var prop = type.GetProperty(fieldName, rf);
                if (prop == null || !prop.CanRead)
                {
                    detail = $"field/property '{fieldName}' not readable on {componentName}";
                    return false;
                }
                if (prop.GetIndexParameters().Length > 0)
                {
                    detail = $"indexed property '{fieldName}' not supported";
                    return false;
                }
                actual = prop.GetValue(component);
            }

            string actualStr = actual == null ? "null" : actual.ToString();

            // Comparisons
            var eqTok = assertTok["equals"];
            if (eqTok != null)
            {
                string expected = eqTok.ToString();
                if (actualStr == expected) return true;
                detail = $"{componentName}.{fieldName}: expected '{expected}', got '{actualStr}'";
                return false;
            }

            var neqTok = assertTok["notEquals"];
            if (neqTok != null)
            {
                string expected = neqTok.ToString();
                if (actualStr != expected) return true;
                detail = $"{componentName}.{fieldName}: expected not '{expected}', but equal";
                return false;
            }

            var isNullTok = assertTok["isNull"];
            if (isNullTok != null)
            {
                bool wantNull = bool.Parse(isNullTok.ToString());
                bool isNull = actual == null || (actual is UnityEngine.Object uo && uo == null);
                if (isNull == wantNull) return true;
                detail = $"{componentName}.{fieldName}: expected isNull={wantNull}, got isNull={isNull}";
                return false;
            }

            var inTok = assertTok["in"] as Newtonsoft.Json.Linq.JArray;
            if (inTok != null)
            {
                foreach (var opt in inTok)
                    if (actualStr == opt.ToString()) return true;
                detail = $"{componentName}.{fieldName}: value '{actualStr}' not in allowed set";
                return false;
            }

            var regexTok = assertTok["regex"];
            if (regexTok != null)
            {
                var pattern = regexTok.ToString();
                if (System.Text.RegularExpressions.Regex.IsMatch(actualStr, pattern)) return true;
                detail = $"{componentName}.{fieldName}: '{actualStr}' does not match /{pattern}/";
                return false;
            }

            detail = "leaf assertion has no comparator (equals/notEquals/isNull/in/regex)";
            return false;
        }

        // SetImporterProperty: set a field or property on an AssetImporter (TextureImporter,
        // ModelImporter, AudioImporter, etc.) and trigger SaveAndReimport() to persist + reapply.
        // Generic via reflection — works on any AssetImporter subtype's public/non-public members.
        // Use cases: change TextureImporter.spriteImportMode, TextureImporter.textureType,
        // ModelImporter.importTangents, AudioImporter.forceToMono, etc., from a scenario JSON
        // instead of writing a one-off editor script.
        private bool SetImporterProperty(Task task)
        {
            string assetPath = GetParam(task, "assetPath");
            string fieldName = GetParam(task, "field");
            string valueStr = GetParam(task, "value");
            string expectedImporterType = GetOptionalParam(task, "importerType");

            if (string.IsNullOrEmpty(assetPath))
            {
                task.error = "SetImporterProperty requires 'assetPath' parameter";
                return false;
            }
            if (string.IsNullOrEmpty(fieldName))
            {
                task.error = "SetImporterProperty requires 'field' parameter";
                return false;
            }
            if (valueStr == null)
            {
                task.error = "SetImporterProperty requires 'value' parameter";
                return false;
            }

            var importer = AssetImporter.GetAtPath(assetPath);
            if (importer == null)
            {
                task.error = $"AssetImporter not found at: {assetPath}";
                return false;
            }

            var importerType = importer.GetType();
            if (!string.IsNullOrEmpty(expectedImporterType) && importerType.Name != expectedImporterType)
            {
                task.error = $"Expected importer type '{expectedImporterType}' but got '{importerType.Name}' at: {assetPath}";
                return false;
            }

            var prop = importerType.GetProperty(fieldName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var field = importerType.GetField(fieldName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            try
            {
                if (prop != null && prop.CanWrite)
                {
                    object value = ConvertValue(prop.PropertyType, valueStr);
                    prop.SetValue(importer, value);
                }
                else if (field != null)
                {
                    object value = ConvertValue(field.FieldType, valueStr);
                    field.SetValue(importer, value);
                }
                else
                {
                    task.error = $"Field or property '{fieldName}' not found on {importerType.Name}";
                    return false;
                }
            }
            catch (Exception ex)
            {
                task.error = $"Failed to set {importerType.Name}.{fieldName} = {valueStr}: {ex.Message}";
                return false;
            }

            importer.SaveAndReimport();
            Debug.Log($"[SceneTaskExecutor] SetImporterProperty: {assetPath} / {importerType.Name}.{fieldName} = {valueStr} ✓");
            return true;
        }

        // SetPluginPlatform: configure a native plugin's platform compatibility on its
        // PluginImporter — enable/disable "Any Platform", the Editor, or a specific BuildTarget,
        // plus optional per-platform data (e.g. CPU = AnyCPU). PluginImporter exposes these only
        // through METHODS (SetCompatibleWithPlatform / SetCompatibleWithAnyPlatform /
        // SetCompatibleWithEditor / SetPlatformData), which the reflection-on-members
        // SetImporterProperty cannot reach — hence this dedicated Unity-native core primitive.
        // Typical use: enable an Objective-C++ source plugin (.mm) for StandaloneOSX so its
        // extern "C" symbols are compiled into the IL2CPP GameAssembly.
        private bool SetPluginPlatform(Task task)
        {
            string assetPath = GetParam(task, "assetPath");
            string platform = GetParam(task, "platform");
            string enableStr = GetParam(task, "enable");
            string dataKey = GetOptionalParam(task, "dataKey");
            string dataValue = GetOptionalParam(task, "dataValue");

            if (string.IsNullOrEmpty(assetPath))
            {
                task.error = "SetPluginPlatform requires 'assetPath' parameter";
                return false;
            }
            if (string.IsNullOrEmpty(platform))
            {
                task.error = "SetPluginPlatform requires 'platform' parameter (\"AnyPlatform\", \"Editor\", or a BuildTarget name like \"StandaloneOSX\")";
                return false;
            }
            if (string.IsNullOrEmpty(enableStr) || !bool.TryParse(enableStr, out bool enable))
            {
                task.error = "SetPluginPlatform requires 'enable' parameter (\"true\"/\"false\")";
                return false;
            }

            var importer = AssetImporter.GetAtPath(assetPath) as PluginImporter;
            if (importer == null)
            {
                task.error = $"PluginImporter not found at: {assetPath} (asset missing or not a plugin)";
                return false;
            }

            try
            {
                if (platform.Equals("AnyPlatform", StringComparison.OrdinalIgnoreCase))
                {
                    importer.SetCompatibleWithAnyPlatform(enable);
                }
                else if (platform.Equals("Editor", StringComparison.OrdinalIgnoreCase))
                {
                    importer.SetCompatibleWithEditor(enable);
                }
                else if (Enum.TryParse(platform, ignoreCase: true, out BuildTarget target))
                {
                    importer.SetCompatibleWithPlatform(target, enable);
                }
                else
                {
                    task.error = $"Unknown platform '{platform}' — use \"AnyPlatform\", \"Editor\", or a BuildTarget name (e.g. \"StandaloneOSX\", \"iOS\", \"Android\")";
                    return false;
                }

                if (!string.IsNullOrEmpty(dataKey))
                {
                    // Prefer the BuildTarget overload — the string overload expects Unity's
                    // internal platform names ("OSXUniversal", not "StandaloneOSX") and
                    // silently no-ops on mismatch.
                    if (Enum.TryParse(platform, ignoreCase: true, out BuildTarget dataTarget))
                        importer.SetPlatformData(dataTarget, dataKey, dataValue ?? string.Empty);
                    else
                        importer.SetPlatformData(platform, dataKey, dataValue ?? string.Empty);
                }
            }
            catch (Exception ex)
            {
                task.error = $"SetPluginPlatform failed for {assetPath} ({platform}={enable}): {ex.Message}";
                return false;
            }

            importer.SaveAndReimport();
            string dataNote = string.IsNullOrEmpty(dataKey) ? "" : $", {dataKey}={dataValue}";
            task.result = $"{platform}={enable}{dataNote}";
            Debug.Log($"[SceneTaskExecutor] SetPluginPlatform: {assetPath} → {platform}={enable}{dataNote} ✓");
            return true;
        }

        // GetPlayerPref: read a key from PlayerPrefs and return its value in task.result.
        // PlayerPrefs only stores three types — string, int, float. The 'type' parameter
        // disambiguates; defaults to "string" since that covers the common case (locale codes,
        // user IDs, saved usernames). Result is JSON for consistency with GetProperty.
        private bool GetPlayerPref(Task task)
        {
            string key = GetParam(task, "key");
            string type = (GetOptionalParam(task, "type") ?? "string").ToLowerInvariant();

            if (string.IsNullOrEmpty(key))
            {
                task.error = "GetPlayerPref requires 'key' parameter";
                return false;
            }

            if (!PlayerPrefs.HasKey(key))
            {
                task.result = "{\"hasKey\":false,\"key\":\"" + key + "\"}";
                Debug.Log($"[SceneTaskExecutor] GetPlayerPref: key '{key}' not set");
                return true;
            }

            string valueRepr;
            switch (type)
            {
                case "int":
                    valueRepr = PlayerPrefs.GetInt(key).ToString();
                    break;
                case "float":
                    valueRepr = PlayerPrefs.GetFloat(key).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "string":
                default:
                    string s = PlayerPrefs.GetString(key);
                    valueRepr = "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
                    break;
            }
            task.result = "{\"hasKey\":true,\"key\":\"" + key + "\",\"type\":\"" + type + "\",\"value\":" + valueRepr + "}";
            Debug.Log($"[SceneTaskExecutor] GetPlayerPref: {key} = {valueRepr} ({type})");
            return true;
        }

        // SetPlayerPref: write a key into PlayerPrefs. Always calls PlayerPrefs.Save() so the
        // change persists immediately (matters in the Editor where Save() isn't automatic).
        private bool SetPlayerPref(Task task)
        {
            string key = GetParam(task, "key");
            string valueStr = GetParam(task, "value");
            string type = (GetOptionalParam(task, "type") ?? "string").ToLowerInvariant();

            if (string.IsNullOrEmpty(key))
            {
                task.error = "SetPlayerPref requires 'key' parameter";
                return false;
            }
            if (valueStr == null)
            {
                task.error = "SetPlayerPref requires 'value' parameter";
                return false;
            }

            try
            {
                switch (type)
                {
                    case "int":
                        PlayerPrefs.SetInt(key, int.Parse(valueStr));
                        break;
                    case "float":
                        PlayerPrefs.SetFloat(key, float.Parse(valueStr, System.Globalization.CultureInfo.InvariantCulture));
                        break;
                    case "string":
                    default:
                        PlayerPrefs.SetString(key, valueStr);
                        break;
                }
                PlayerPrefs.Save();
            }
            catch (Exception ex)
            {
                task.error = $"Failed to parse '{valueStr}' as {type}: {ex.Message}";
                return false;
            }

            Debug.Log($"[SceneTaskExecutor] SetPlayerPref: {key} = {valueStr} ({type}) ✓");
            return true;
        }

        // DeletePlayerPref: remove a key from PlayerPrefs (or all keys when key="*"). Idempotent.
        private bool DeletePlayerPref(Task task)
        {
            string key = GetParam(task, "key");
            if (string.IsNullOrEmpty(key))
            {
                task.error = "DeletePlayerPref requires 'key' parameter (use '*' to clear all)";
                return false;
            }

            if (key == "*")
            {
                PlayerPrefs.DeleteAll();
                PlayerPrefs.Save();
                Debug.Log("[SceneTaskExecutor] DeletePlayerPref: all keys cleared ✓");
                return true;
            }

            if (PlayerPrefs.HasKey(key))
            {
                PlayerPrefs.DeleteKey(key);
                PlayerPrefs.Save();
                Debug.Log($"[SceneTaskExecutor] DeletePlayerPref: {key} ✓");
            }
            else
            {
                Debug.Log($"[SceneTaskExecutor] DeletePlayerPref: {key} not set, no-op");
            }
            return true;
        }
    }
}
