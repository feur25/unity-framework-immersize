#if UNITY_EDITOR
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEditor;
using UnityEngine;

namespace com.ImmersizeFramework.Loader.Editor {
    
    [InitializeOnLoad]
    internal static class SceneDropdownValidator {
        
        static readonly ConcurrentDictionary<Type, FieldInfo[]> _fieldCache = new ConcurrentDictionary<Type, FieldInfo[]>();
        static readonly HashSet<string> _validatedObjects = new HashSet<string>();

        static SceneDropdownValidator() => Selection.selectionChanged += ValidateSelection;

        static void ValidateSelection() {
            if (Selection.activeGameObject == null) return;

            Selection.activeGameObject.GetComponents<MonoBehaviour>()
                .Where(c => c != null)
                .AsParallel()
                .ForAll(ValidateComponent);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void ValidateComponent(MonoBehaviour component) {
            var objectId = $"{component.GetInstanceID()}_{component.GetType().Name}";
            if (_validatedObjects.Contains(objectId)) return;

            var fields = GetSceneDropdownFields(component.GetType());
            var issues = new List<ValidationIssue>();

            foreach (var field in fields) {
                var value = field.GetValue(component) as string;
                var attribute = field.GetCustomAttribute<SceneDropdownAttribute>();
                
                if (ValidateSceneField(value, attribute, field.Name, out var issue))
                    continue;
                    
                issues.Add(issue);
            }

            if (issues.Count > 0)
                LogValidationIssues(component, issues);

            _validatedObjects.Add(objectId);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static FieldInfo[] GetSceneDropdownFields(Type type) =>
            _fieldCache.GetOrAdd(type, t => t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .Where(f => f.GetCustomAttribute<SceneDropdownAttribute>() != null)
                .ToArray());

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static bool ValidateSceneField(string sceneName, SceneDropdownAttribute attribute, 
            string fieldName, out ValidationIssue issue) {
            issue = default;

            if (string.IsNullOrEmpty(sceneName) || sceneName == "(None)")
                return true;

            var manifest = SceneRegistry.GetManifest(attribute);
            if (manifest.TryGetEntry(sceneName, out var entry) && entry.IsValid)
                return true;

            issue = new ValidationIssue(
                fieldName,
                sceneName,
                ValidationSeverity.Warning,
                $"Scene '{sceneName}' not found in project"
            );

            return false;
        }

        static void LogValidationIssues(MonoBehaviour component, List<ValidationIssue> issues) {
            var context = component.gameObject;
            var componentName = component.GetType().Name;

            issues.AsParallel().ForAll(issue => {
                var message = $"[{componentName}] {issue.Message} in field '{issue.FieldName}'";

                switch (issue.Severity) {
                    case ValidationSeverity.Error:
                        Debug.LogError(message, context);
                        break;
                    case ValidationSeverity.Warning:
                        Debug.LogWarning(message, context);
                        break;
                    case ValidationSeverity.Info:
                        Debug.Log(message, context);
                        break;
                }
            });
        }

        [MenuItem("Tools/ImmersizeFramework/Validate Scene References")]
        static void ValidateAllSceneReferences() {
            var components = Resources.FindObjectsOfTypeAll<MonoBehaviour>()
                .Where(c => c != null && GetSceneDropdownFields(c.GetType()).Length > 0);

            var totalIssues = 0;
            _validatedObjects.Clear();

            foreach (var component in components) {
                ValidateComponent(component);
                totalIssues++;
            }

            Debug.Log($"[SceneDropdownValidator] Validated {totalIssues} components with SceneDropdown fields");
        }

        readonly struct ValidationIssue {
            public readonly string FieldName;
            public readonly string SceneName;
            public readonly ValidationSeverity Severity;
            public readonly string Message;

            public ValidationIssue(string fieldName, string sceneName, ValidationSeverity severity, string message) {
                FieldName = fieldName;
                SceneName = sceneName;
                Severity = severity;
                Message = message;
            }
        }

        enum ValidationSeverity : byte {
            Info,
            Warning,
            Error
        }
    }

    internal static class DictionaryExtensions {
        public static TValue GetOrAdd<TKey, TValue>(this Dictionary<TKey, TValue> dictionary, TKey key, Func<TKey, TValue> valueFactory) {
            if (dictionary.TryGetValue(key, out var value)) return value;

            value = valueFactory(key);
            dictionary[key] = value;

            return value;
        }
    }
}
#endif
