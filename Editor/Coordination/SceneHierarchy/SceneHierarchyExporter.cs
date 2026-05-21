using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PerSpec.Editor.Coordination
{
    /// <summary>
    /// Exports Unity scene hierarchy to JSON format with full component details
    /// </summary>
    public static class SceneHierarchyExporter
    {
        private class GameObjectData
        {
            public string name;
            public string path;
            public bool active;
            public TransformData transform;
            public List<ComponentData> components;
            public List<GameObjectData> children;
        }

        private class TransformData
        {
            public float[] position;
            public float[] rotation;
            public float[] scale;
        }

        private class ComponentData
        {
            public string type;
            public bool enabled;
            public Dictionary<string, object> properties;
        }

        private class SceneHierarchyData
        {
            public string exportTime;
            public string sceneName;
            public string scenePath;
            public int totalGameObjects;
            public int totalComponents;
            public List<GameObjectData> rootObjects;
        }

        public static string ExportFullHierarchy(bool includeInactive = true, bool includeComponents = true)
        {
            try
            {
                var scene = SceneManager.GetActiveScene();
                if (!scene.IsValid())
                {
                    throw new InvalidOperationException("No active scene found");
                }

                var hierarchyData = new SceneHierarchyData
                {
                    exportTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    sceneName = scene.name,
                    scenePath = scene.path,
                    rootObjects = new List<GameObjectData>()
                };

                int totalGameObjects = 0;
                int totalComponents = 0;

                // Get all root GameObjects
                var rootGameObjects = scene.GetRootGameObjects();
                foreach (var rootGO in rootGameObjects)
                {
                    if (!includeInactive && !rootGO.activeInHierarchy)
                        continue;

                    var gameObjectData = ExportGameObject(rootGO, includeInactive, includeComponents, ref totalGameObjects, ref totalComponents);
                    hierarchyData.rootObjects.Add(gameObjectData);
                }

                hierarchyData.totalGameObjects = totalGameObjects;
                hierarchyData.totalComponents = totalComponents;

                // Serialize to JSON with formatting
                var settings = new JsonSerializerSettings
                {
                    Formatting = Formatting.Indented,
                    NullValueHandling = NullValueHandling.Ignore,
                    ReferenceLoopHandling = ReferenceLoopHandling.Ignore
                };

                return JsonConvert.SerializeObject(hierarchyData, settings);
            }
            catch (Exception e)
            {
                Debug.LogError($"[SceneHierarchyExporter] Failed to export hierarchy: {e.Message}");
                throw;
            }
        }

        public static string ExportSingleGameObject(string targetPath, bool includeInactive = true, bool includeComponents = true)
        {
            try
            {
                GameObject targetGO = null;

                // Try to find the GameObject by path
                if (targetPath.StartsWith("/"))
                {
                    targetGO = GameObject.Find(targetPath.Substring(1));
                }
                else
                {
                    targetGO = GameObject.Find(targetPath);
                }

                // If not found by path, try to find by name
                if (targetGO == null)
                {
#if UNITY_2022_2_OR_NEWER
                    // Modern API (Unity 2022.2+) - faster with explicit control over inactive objects and sorting
                    var allObjects = GameObject.FindObjectsByType<GameObject>(
                        includeInactive ? FindObjectsInactive.Include : FindObjectsInactive.Exclude,
                        FindObjectsSortMode.None);
#else
                    // Legacy API for Unity 2021.3 and earlier
                    // Note: FindObjectsByType was introduced in Unity 2021.3.18, but Unity's preprocessor
                    // directives only support major.minor versions, so we use 2022.2 as the cutoff
                    var allObjects = GameObject.FindObjectsOfType<GameObject>(includeInactive);
#endif
                    targetGO = allObjects.FirstOrDefault(go => go.name == targetPath || GetGameObjectPath(go) == targetPath);
                }

                if (targetGO == null)
                {
                    throw new InvalidOperationException($"GameObject not found: {targetPath}");
                }

                int totalGameObjects = 0;
                int totalComponents = 0;

                var gameObjectData = ExportGameObject(targetGO, includeInactive, includeComponents, ref totalGameObjects, ref totalComponents);

                var exportData = new
                {
                    exportTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    sceneName = SceneManager.GetActiveScene().name,
                    targetPath = GetGameObjectPath(targetGO),
                    totalGameObjects = totalGameObjects,
                    totalComponents = totalComponents,
                    gameObject = gameObjectData
                };

                // Serialize to JSON with formatting
                var settings = new JsonSerializerSettings
                {
                    Formatting = Formatting.Indented,
                    NullValueHandling = NullValueHandling.Ignore,
                    ReferenceLoopHandling = ReferenceLoopHandling.Ignore
                };

                return JsonConvert.SerializeObject(exportData, settings);
            }
            catch (Exception e)
            {
                Debug.LogError($"[SceneHierarchyExporter] Failed to export GameObject: {e.Message}");
                throw;
            }
        }

        private static GameObjectData ExportGameObject(GameObject go, bool includeInactive, bool includeComponents, ref int totalGameObjects, ref int totalComponents)
        {
            totalGameObjects++;

            var data = new GameObjectData
            {
                name = go.name,
                path = GetGameObjectPath(go),
                active = go.activeSelf,
                transform = ExportTransform(go.transform),
                components = new List<ComponentData>(),
                children = new List<GameObjectData>()
            };

            // Export components if requested
            if (includeComponents)
            {
                var components = go.GetComponents<Component>();
                foreach (var component in components)
                {
                    if (component == null) continue;

                    // Skip vanilla Transform (data already in the `transform` block).
                    // Do NOT skip RectTransform — its anchorMin/Max, sizeDelta, pivot,
                    // offsetMin/Max are only accessible via its SerializedProperty iterator,
                    // not via the simple transform block.
                    if (component is Transform && !(component is RectTransform)) continue;

                    try
                    {
                        var componentData = ExportComponent(component);
                        if (componentData != null)
                        {
                            data.components.Add(componentData);
                            totalComponents++;
                        }
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[SceneHierarchyExporter] Failed to export component {component.GetType().Name} on {go.name}: {e.Message}");
                    }
                }
            }

            // Export children
            foreach (Transform child in go.transform)
            {
                if (!includeInactive && !child.gameObject.activeInHierarchy)
                    continue;

                var childData = ExportGameObject(child.gameObject, includeInactive, includeComponents, ref totalGameObjects, ref totalComponents);
                data.children.Add(childData);
            }

            return data;
        }

        private static TransformData ExportTransform(Transform transform)
        {
            return new TransformData
            {
                position = new[] { transform.localPosition.x, transform.localPosition.y, transform.localPosition.z },
                rotation = new[] { transform.localRotation.x, transform.localRotation.y, transform.localRotation.z, transform.localRotation.w },
                scale = new[] { transform.localScale.x, transform.localScale.y, transform.localScale.z }
            };
        }

        private static ComponentData ExportComponent(Component component)
        {
            var componentType = component.GetType();
            var data = new ComponentData
            {
                type = componentType.Name,
                enabled = true,
                properties = new Dictionary<string, object>()
            };

            // Check if component has enabled property
            var enabledProp = componentType.GetProperty("enabled");
            if (enabledProp != null && enabledProp.CanRead)
            {
                try
                {
                    data.enabled = (bool)enabledProp.GetValue(component);
                }
                catch { }
            }

            // Use SerializedObject for better property extraction
            var serializedObject = new SerializedObject(component);
            var iterator = serializedObject.GetIterator();

            // Skip script property for MonoBehaviours
            if (iterator.NextVisible(true))
            {
                do
                {
                    // Skip certain properties
                    if (iterator.name == "m_Script" ||
                        iterator.name == "m_ObjectHideFlags" ||
                        iterator.name == "m_PrefabInstance" ||
                        iterator.name == "m_PrefabAsset")
                        continue;

                    try
                    {
                        var value = GetSerializedPropertyValue(iterator);
                        if (value != null)
                        {
                            // Clean up property name (remove m_ prefix if present)
                            var propertyName = iterator.name;
                            if (propertyName.StartsWith("m_"))
                                propertyName = propertyName.Substring(2);

                            data.properties[propertyName] = value;
                        }
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[SceneHierarchyExporter] Failed to export property {iterator.name}: {e.Message}");
                    }
                }
                while (iterator.NextVisible(false));
            }

            return data;
        }

        private static object GetSerializedPropertyValue(SerializedProperty property)
        {
            switch (property.propertyType)
            {
                case SerializedPropertyType.Integer:
                    return property.intValue;
                case SerializedPropertyType.Boolean:
                    return property.boolValue;
                case SerializedPropertyType.Float:
                    return property.floatValue;
                case SerializedPropertyType.String:
                    return property.stringValue;
                case SerializedPropertyType.Color:
                    var color = property.colorValue;
                    return new { r = color.r, g = color.g, b = color.b, a = color.a };
                case SerializedPropertyType.Vector2:
                    var v2 = property.vector2Value;
                    return new { x = v2.x, y = v2.y };
                case SerializedPropertyType.Vector3:
                    var v3 = property.vector3Value;
                    return new { x = v3.x, y = v3.y, z = v3.z };
                case SerializedPropertyType.Vector4:
                    var v4 = property.vector4Value;
                    return new { x = v4.x, y = v4.y, z = v4.z, w = v4.w };
                case SerializedPropertyType.Rect:
                    var rect = property.rectValue;
                    return new { x = rect.x, y = rect.y, width = rect.width, height = rect.height };
                case SerializedPropertyType.Bounds:
                    var bounds = property.boundsValue;
                    return new
                    {
                        center = new { x = bounds.center.x, y = bounds.center.y, z = bounds.center.z },
                        size = new { x = bounds.size.x, y = bounds.size.y, z = bounds.size.z }
                    };
                case SerializedPropertyType.Quaternion:
                    var q = property.quaternionValue;
                    return new { x = q.x, y = q.y, z = q.z, w = q.w };
                case SerializedPropertyType.Enum:
                    return property.enumDisplayNames.Length > property.enumValueIndex ?
                           property.enumDisplayNames[property.enumValueIndex] :
                           property.enumValueIndex.ToString();
                case SerializedPropertyType.ObjectReference:
                    if (property.objectReferenceValue != null)
                    {
                        var obj = property.objectReferenceValue;
                        return new
                        {
                            type = obj.GetType().Name,
                            name = obj.name,
                            instanceId = obj.GetInstanceID()
                        };
                    }
                    return null;
                case SerializedPropertyType.LayerMask:
                    return property.intValue;
                case SerializedPropertyType.ArraySize:
                    return property.intValue;
                default:
                    // For arrays and other complex types, just return the display name
                    if (property.isArray && property.propertyType != SerializedPropertyType.String)
                    {
                        var array = new List<object>();
                        for (int i = 0; i < property.arraySize && i < 100; i++) // Limit to 100 items
                        {
                            var element = property.GetArrayElementAtIndex(i);
                            var value = GetSerializedPropertyValue(element);
                            if (value != null)
                                array.Add(value);
                        }
                        return array;
                    }
                    return null;
            }
        }

        private static string GetGameObjectPath(GameObject go)
        {
            var path = "/" + go.name;
            var parent = go.transform.parent;

            while (parent != null)
            {
                path = "/" + parent.name + path;
                parent = parent.parent;
            }

            return path;
        }

        public static void CleanOutputDirectory()
        {
            try
            {
                string projectPath = Directory.GetParent(Application.dataPath).FullName;
                string outputDir = Path.Combine(projectPath, "PerSpec", "SceneHierarchy");

                if (Directory.Exists(outputDir))
                {
                    var files = Directory.GetFiles(outputDir, "*.json");
                    foreach (var file in files)
                    {
                        File.Delete(file);
                    }
                    Debug.Log($"[SceneHierarchyExporter] Cleaned {files.Length} files from output directory");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[SceneHierarchyExporter] Failed to clean output directory: {e.Message}");
            }
        }
    }
}