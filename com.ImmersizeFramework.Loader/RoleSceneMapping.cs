using System;
using UnityEngine;
using System.Runtime.CompilerServices;
using com.ImmersizeFramework.Save;
using System.Threading.Tasks;

namespace com.ImmersizeFramework.Loader {
    
    [Serializable]
    public struct RoleSceneMapping {
        [SerializeField] public string Role;
        [SerializeField] public string SceneName;
        [SerializeField] public string DownloadUrl;
        
        public RoleSceneMapping(string role, string sceneName, string downloadUrl = "") {
            Role = role ?? "guest";
            SceneName = sceneName ?? string.Empty;
            DownloadUrl = downloadUrl ?? string.Empty;
        }
        
        public bool IsValid => !string.IsNullOrEmpty(Role) && !string.IsNullOrEmpty(SceneName);
        public bool HasDownloadUrl => !string.IsNullOrEmpty(DownloadUrl);
        
        public override string ToString() => HasDownloadUrl ? $"{Role} → {SceneName} [{DownloadUrl}]" : $"{Role} → {SceneName}";
        
        public async Task<bool> DownloadAssetsAsync() {
            if (!HasDownloadUrl) return true;
            
            var role = Role;
            var url = DownloadUrl;
            using var downloader = new GameAssetDownloader();

            return await downloader.CheckAndDownloadAssetsAsync(role, _ => Task.FromResult(url));
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator (string role, string scene)(RoleSceneMapping mapping) =>
            (mapping.Role, mapping.SceneName);
            
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator RoleSceneMapping((string role, string scene) tuple) =>
            new(tuple.role, tuple.scene);
            
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator RoleSceneMapping((string role, string scene, string url) tuple) =>
            new(tuple.role, tuple.scene, tuple.url);
    }
}
