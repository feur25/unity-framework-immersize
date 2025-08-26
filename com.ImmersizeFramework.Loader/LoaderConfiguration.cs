using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using UnityEngine;
using com.ImmersizeFramework.Core;
using com.ImmersizeFramework.Tasks;

namespace com.ImmersizeFramework.Loader {
    
    [CreateAssetMenu(fileName = "LoaderConfiguration", menuName = "ImmersizeFramework/Loader Configuration")]
    public sealed class LoaderConfiguration : ScriptableObject {
        
        [SerializeField] bool enableAutoScan = true;
        [SerializeField] bool enablePreloading = true;
        [SerializeField] bool enableCompression = false;
        [SerializeField] int maxConcurrentLoads = 3;
        [SerializeField] float defaultTimeout = 30f;
        [SerializeField] bool enableSmartCaching = true;
        [SerializeField] int maxCacheSize = 100;
        [SerializeField] float cacheOptimizationInterval = 300f;
        [SerializeField] bool enablePerformanceMonitoring = true;
        [SerializeField] RoleConfiguration[] roleConfigurations = Array.Empty<RoleConfiguration>();
        [SerializeField] AssetRule[] assetRules = Array.Empty<AssetRule>();
        [SerializeField] PreloadRule[] preloadRules = Array.Empty<PreloadRule>();
        
        public ref readonly bool EnableAutoScan => ref enableAutoScan;
        public ref readonly bool EnablePreloading => ref enablePreloading;
        public ref readonly bool EnableCompression => ref enableCompression;
        public ref readonly int MaxConcurrentLoads => ref maxConcurrentLoads;
        public ref readonly float DefaultTimeout => ref defaultTimeout;
        public ref readonly bool EnableSmartCaching => ref enableSmartCaching;
        public ref readonly int MaxCacheSize => ref maxCacheSize;
        public ref readonly float CacheOptimizationInterval => ref cacheOptimizationInterval;
        public ref readonly bool EnablePerformanceMonitoring => ref enablePerformanceMonitoring;
        
        LoaderCore Core => FrameworkCore.Instance?.GetService<LoaderCore>();
        
        readonly struct ConfigurationProcessor {
            readonly LoaderCore _core;
            readonly LoaderConfiguration _config;
            
            public ConfigurationProcessor(LoaderCore core, LoaderConfiguration config) => 
                (_core, _config) = (core, config);
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void ProcessAll() {
                ProcessRoles(_config.roleConfigurations);
                ProcessRules(_config.assetRules);
                if (_config.enablePreloading) ProcessPreload(_config.preloadRules);
            }
            
            void ProcessRoles(ReadOnlySpan<RoleConfiguration> roles) {
                foreach (var role in roles) {
                    var items = GetItemsByPattern(role.AssetPattern, role.AssetType);
                    if (items.Length > 0) _core.Configure(role.RoleName, items);
                }
            }
            
            void ProcessRules(ReadOnlySpan<AssetRule> rules) {
                foreach (var rule in rules) {
                    var items = GetItemsByPattern(rule.AssetPattern, rule.AssetType);
                    for (int i = 0; i < items.Length; i++) {
                        var item = _core[items[i]];
                        if (!item.IsValid) continue;
                        
                        _core.RegisterItem(
                            item.Name, item.Path, item.Type, rule.Priority, rule.Flags,
                            item.Roles, MergeTags(rule.Tags, item.Tags),
                            item.Dependencies, rule.Description ?? item.Description
                        );
                    }
                }
            }
            
            void ProcessPreload(ReadOnlySpan<PreloadRule> rules) {
                foreach (var rule in rules) {
                    var items = GetItemsByPattern(rule.AssetPattern, rule.AssetType);
                    for (int i = 0; i < items.Length; i++) {
                        var item = _core[items[i]];
                        if (!item.IsValid || !ShouldPreload(item, rule)) continue;
                        
                        var newFlags = item.Flags | LoadableFlags.Preload;
                        _core.RegisterItem(
                            item.Name, item.Path, item.Type, item.Priority, newFlags,
                            item.Roles, item.Tags, item.Dependencies, item.Description
                        );
                    }
                }
            }
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static string[] MergeTags(string[] tags1, string[] tags2) => 
                tags1.Concat(tags2).Distinct().ToArray();
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static bool ShouldPreload(LoadableItem item, PreloadRule rule) =>
                item.Size <= rule.MaxSize && item.Priority >= rule.MinPriority;
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            string[] GetItemsByPattern(string pattern, LoadableType? type) => pattern switch {
                null or "" => Array.Empty<string>(),
                "*" => GetByType(type),
                _ => GetByType(type).Where(item => MatchesPattern(item, pattern)).ToArray()
            };
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            string[] GetByType(LoadableType? type) => type switch {
                LoadableType.Scene => _core.GetScenes(),
                LoadableType.Prefab => _core.GetPrefabs(),
                LoadableType.Texture => _core.GetTextures(),
                LoadableType.Audio => _core.GetAudio(),
                LoadableType.Material => _core.GetMaterials(),
                LoadableType.Mesh => _core.GetMeshes(),
                LoadableType.Animation => _core.GetAnimations(),
                LoadableType.Video => _core.GetVideos(),
                LoadableType.Data => _core.GetData(),
                LoadableType.Script => _core.GetScripts(),
                _ => _core.Available
            };
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static bool MatchesPattern(string item, string pattern) {
                if (!pattern.Contains('*')) 
                    return item.Contains(pattern, StringComparison.OrdinalIgnoreCase);
                
                var parts = pattern.Split('*');
                var index = 0;
                
                foreach (var part in parts) {
                    if (string.IsNullOrEmpty(part)) continue;
                    var found = item.IndexOf(part, index, StringComparison.OrdinalIgnoreCase);
                    if (found == -1) return false;
                    index = found + part.Length;
                }
                return true;
            }
        }
        
        [Lore("ApplyConfiguration", "Applique la configuration au système")]
        public void ApplyConfiguration() {
            if (Core is null) {
                Debug.LogError("[LoaderConfiguration] LoaderCore not found");
                return;
            }
            
            new ConfigurationProcessor(Core, this).ProcessAll();
            Debug.Log($"[LoaderConfiguration] Applied: {roleConfigurations.Length} roles, {assetRules.Length} rules");
        }
        
        readonly struct ValidationEngine {
            readonly LoaderConfiguration _config;
            
            public ValidationEngine(LoaderConfiguration config) => _config = config;
            
            public ValidationResult Execute() {
                var (errors, warnings) = (new List<string>(), new List<string>());
                
                CheckRoleDuplicates(errors);
                CheckEmptyPatterns(warnings);
                CheckInvalidSizes(warnings);
                CheckInvalidParameters(errors);
                
                return new ValidationResult(errors.Count == 0, errors.ToArray(), warnings.ToArray());
            }
            
            void CheckRoleDuplicates(List<string> errors) {
                var duplicates = _config.roleConfigurations
                    .GroupBy(r => r.RoleName)
                    .Where(g => g.Count() > 1)
                    .Select(g => g.Key)
                    .ToList();
                
                duplicates.ForEach(duplicate => errors.Add($"Duplicate role: {duplicate}"));
            }
            
            void CheckEmptyPatterns(List<string> warnings) {
                var emptyRules = _config.assetRules
                    .Where(rule => string.IsNullOrEmpty(rule.AssetPattern))
                    .Count();
                
                if (emptyRules > 0) warnings.Add($"{emptyRules} rules with empty patterns");
            }
            
            void CheckInvalidSizes(List<string> warnings) {
                var invalidRules = _config.preloadRules
                    .Where(rule => rule.MaxSize <= 0)
                    .ToList();
                
                invalidRules.ForEach(rule => warnings.Add($"Invalid preload size: {rule.MaxSize}"));
            }
            
            void CheckInvalidParameters(List<string> errors) {
                if (_config.maxConcurrentLoads <= 0)
                    errors.Add("Max concurrent loads must be > 0");
                if (_config.defaultTimeout <= 0)
                    errors.Add("Default timeout must be > 0");
            }
        }
        
        [Lore("ValidateConfiguration", "Valide la configuration")]
        public ValidationResult ValidateConfiguration() => new ValidationEngine(this).Execute();
        
        readonly struct DefaultConfigBuilder {
            readonly LoaderConfiguration _config;
            
            public DefaultConfigBuilder(LoaderConfiguration config) => _config = config;
            
            public void Build() {
                BuildRoles();
                BuildRules();
                BuildPreload();
                Debug.Log("[LoaderConfiguration] Default configuration created");
            }
            
            void BuildRoles() => _config.roleConfigurations = new[] {
                new RoleConfiguration("admin", "*", null, "Full access"),
                new RoleConfiguration("user", "*", LoadableType.Scene, "User scenes"),
                new RoleConfiguration("guest", "Demo*", LoadableType.Scene, "Demo only")
            };
            
            void BuildRules() => _config.assetRules = new[] {
                new AssetRule("*UI*", LoadableType.Prefab, LoadPriority.High, 
                    LoadableFlags.Preload, new[] { "ui" }, "UI elements"),
                new AssetRule("*Audio*", LoadableType.Audio, LoadPriority.Normal, 
                    LoadableFlags.Compressed, new[] { "audio" }, "Audio assets")
            };
            
            void BuildPreload() => _config.preloadRules = new[] {
                new PreloadRule("*Critical*", LoadableType.Prefab, LoadPriority.High, 1048576, "Critical prefabs")
            };
        }
        
        [ContextMenu("Create Default Configuration")]
        public void CreateDefaultConfiguration() => new DefaultConfigBuilder(this).Build();
    }
    
    [System.Serializable]
    public readonly struct RoleConfiguration {
        public string RoleName { get; }
        public string AssetPattern { get; }
        public LoadableType? AssetType { get; }
        public string Description { get; }
        
        public RoleConfiguration(string roleName, string assetPattern, LoadableType? assetType, string description) =>
            (RoleName, AssetPattern, AssetType, Description) = (roleName, assetPattern, assetType, description);
    }
    
    [System.Serializable]
    public readonly struct AssetRule {
        public string AssetPattern { get; }
        public LoadableType? AssetType { get; }
        public LoadPriority Priority { get; }
        public LoadableFlags Flags { get; }
        public string[] Tags { get; }
        public string Description { get; }
        
        public AssetRule(string assetPattern, LoadableType? assetType, LoadPriority priority, 
                        LoadableFlags flags, string[] tags, string description) =>
            (AssetPattern, AssetType, Priority, Flags, Tags, Description) = 
            (assetPattern, assetType, priority, flags, tags, description);
    }
    
    public readonly struct PreloadRule {
        public string AssetPattern { get; }
        public LoadableType? AssetType { get; }
        public LoadPriority MinPriority { get; }
        public long MaxSize { get; }
        public string Description { get; }
        
        public PreloadRule(string assetPattern, LoadableType? assetType, LoadPriority minPriority, 
                          long maxSize, string description) =>
            (AssetPattern, AssetType, MinPriority, MaxSize, Description) = 
            (assetPattern, assetType, minPriority, maxSize, description);
    }
    
    public readonly struct ValidationResult {
        public bool IsValid { get; }
        public string[] Errors { get; }
        public string[] Warnings { get; }
        
        public ValidationResult(bool isValid, string[] errors, string[] warnings) =>
            (IsValid, Errors, Warnings) = (isValid, errors, warnings);
        
        public bool HasWarnings => Warnings.Length > 0;
        public string Summary => IsValid ? 
            $"Valid ({Warnings.Length} warnings)" : 
            $"Invalid ({Errors.Length} errors, {Warnings.Length} warnings)";
    }
    
    public static class ListExtensions {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ForEach<T>(this List<T> source, Action<T> action) {
            for (int i = 0; i < source.Count; i++) action(source[i]);
        }
    }
}
