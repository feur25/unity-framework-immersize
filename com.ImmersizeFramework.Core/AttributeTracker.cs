using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace com.ImmersizeFramework.Core {
    public interface ITrackableAttribute {
        void Execute(object instance, MethodInfo method);
    }

    public sealed class AttributeTracker<T> where T : Attribute, ITrackableAttribute {
        private static readonly Dictionary<string, T> _cache = new();
        private static bool _initialized;
        private static readonly AttributeTracker<T> _instance = new();

        public static AttributeTracker<T> Instance => _instance;
        public T this[string key] => _cache.TryGetValue(key, out var attr) ? attr : null;
        public IReadOnlyDictionary<string, T> Cache => _cache;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        public static void Initialize() {
            if (_initialized) return;
            _initialized = true;
            
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) {
                try {
                    foreach (var type in assembly.GetTypes()) {
                        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)) {
                            if (Attribute.GetCustomAttribute(method, typeof(T)) is T attr) {
                                _cache[$"{type.Name}.{method.Name}"] = attr;
                            }
                        }
                    }
                } catch { }
            }
        }

        public static void Track(object instance, [CallerMemberName] string methodName = "") {
            if (instance == null) return;
            
            var key = $"{instance.GetType().Name}.{methodName}";
            if (_cache.TryGetValue(key, out var attr)) {
                attr.Execute(instance, instance.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
            }
        }
    }
}
