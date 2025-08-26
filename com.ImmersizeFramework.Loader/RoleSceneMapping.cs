using System;
using UnityEngine;
using System.Runtime.CompilerServices;

namespace com.ImmersizeFramework.Loader {
    
    [Serializable]
    public struct RoleSceneMapping {
        [SerializeField] public string Role;
        [SerializeField] public string SceneName;
        
        public RoleSceneMapping(string role, string sceneName) {
            Role = role ?? "guest";
            SceneName = sceneName ?? string.Empty;
        }
        
        public bool IsValid => !string.IsNullOrEmpty(Role) && !string.IsNullOrEmpty(SceneName);
        
        public override string ToString() => $"{Role} → {SceneName}";
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator (string role, string scene)(RoleSceneMapping mapping) =>
            (mapping.Role, mapping.SceneName);
            
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator RoleSceneMapping((string role, string scene) tuple) =>
            new(tuple.role, tuple.scene);
    }
}
