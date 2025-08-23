using System;
using System.Reflection;
using UnityEngine;
using System.Runtime.CompilerServices;
using com.ImmersizeFramework.Core;

namespace com.ImmersizeFramework.Tasks {
    [AttributeUsage(AttributeTargets.Method)]
    public class LoreAttribute : Attribute, ITrackableAttribute {
        public string Name { get; }
        public string Description { get; }

        public LoreAttribute(string name, string description = "") => (Name, Description) = (name, description);

        public void Execute(object instance, MethodInfo method) {
            var className = instance?.GetType().Name ?? "Static";
            var description = string.IsNullOrEmpty(Description) ? "" : $" ({Description})";
            Debug.Log($"[{className}] : la function {Name} à été exécuté{description}");
        }
    }

    public static class LoreTracker {
        public static void Log(object instance, [CallerMemberName] string methodName = "") => 
            AttributeTracker<LoreAttribute>.Track(instance, methodName);
    }

    public abstract class LoreMonoBehaviour : MonoBehaviour {
        protected virtual void Awake() => LoreTracker.Log(this);
        protected virtual void Start() => LoreTracker.Log(this);
        protected virtual void OnEnable() => LoreTracker.Log(this);
        protected virtual void OnDisable() => LoreTracker.Log(this);
        protected virtual void OnDestroy() => LoreTracker.Log(this);

        protected void LogLore([CallerMemberName] string methodName = "") => LoreTracker.Log(this, methodName);
    }
}
