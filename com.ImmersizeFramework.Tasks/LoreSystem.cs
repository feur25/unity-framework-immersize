using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using System.Runtime.CompilerServices;

namespace com.ImmersizeFramework.Tasks {
    [AttributeUsage(AttributeTargets.Method)]
    public class LoreAttribute : Attribute {
        public string Name { get; }
        public string Description { get; }

        public LoreAttribute(string name, string description = "") {
            Name = name;
            Description = description;
        }
    }

    public static class LoreSystem {
        private static readonly Dictionary<string, LoreAttribute> _methodLoreMap = new();
        private static bool _initialized = false;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        public static void Initialize() {
            if (_initialized) return;
            _initialized = true;
            ScanAssemblies();
        }

        private static void ScanAssemblies() {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) {
                try {
                    foreach (var type in assembly.GetTypes()) {
                        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)) {
                            var loreAttr = method.GetCustomAttribute<LoreAttribute>();
                            if (loreAttr != null) {
                                var key = $"{type.Name}.{method.Name}";
                                _methodLoreMap[key] = loreAttr;
                            }
                        }
                    }
                } catch { }
            }
        }

        public static void LogExecution([CallerMemberName] string methodName = "", [CallerFilePath] string filePath = "") {
            var className = System.IO.Path.GetFileNameWithoutExtension(filePath);
            var key = $"{className}.{methodName}";
            
            if (_methodLoreMap.TryGetValue(key, out var loreAttr)) {
                var description = string.IsNullOrEmpty(loreAttr.Description) ? "" : $" ({loreAttr.Description})";
                Debug.Log($"[{className}] : la function {loreAttr.Name} à été exécuté{description}");
            }
        }

        public static void LogExecution(object instance, [CallerMemberName] string methodName = "") {
            if (instance == null) return;
            
            var className = instance.GetType().Name;
            var key = $"{className}.{methodName}";
            
            if (_methodLoreMap.TryGetValue(key, out var loreAttr)) {
                var description = string.IsNullOrEmpty(loreAttr.Description) ? "" : $" ({loreAttr.Description})";
                Debug.Log($"[{className}] : la function {loreAttr.Name} à été exécuté{description}");
            }
        }
    }

    public abstract class LoreMonoBehaviour : MonoBehaviour {
        protected virtual void Awake() => LoreSystem.LogExecution(this);
        protected virtual void Start() => LoreSystem.LogExecution(this);
        protected virtual void Update() => LoreSystem.LogExecution(this);
        protected virtual void FixedUpdate() => LoreSystem.LogExecution(this);
        protected virtual void LateUpdate() => LoreSystem.LogExecution(this);
        protected virtual void OnEnable() => LoreSystem.LogExecution(this);
        protected virtual void OnDisable() => LoreSystem.LogExecution(this);
        protected virtual void OnDestroy() => LoreSystem.LogExecution(this);

        protected void LogLore([CallerMemberName] string methodName = "") =>
            LoreSystem.LogExecution(this, methodName);
    }
}
