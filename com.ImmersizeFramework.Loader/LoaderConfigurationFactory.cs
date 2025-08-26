using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using UnityEngine;
using com.ImmersizeFramework.Core;

namespace com.ImmersizeFramework.Loader {
    
    public readonly struct LoaderConfigurationBuilder {
        
        readonly LoaderConfiguration _config;
        readonly ConfigurationCache _cache;
        
        public LoaderConfigurationBuilder(LoaderConfiguration config) =>
            (_config, _cache) = (config, new());
        
        public LoaderConfigurationBuilder WithRole(string name, string pattern, LoadableType? type = null, string description = null) {
            _cache.AddRole(new RoleConfiguration(name, pattern, type, description));
            return this;
        }
        
        public LoaderConfigurationBuilder WithAssetRule(string pattern, LoadableType? type, LoadPriority priority, 
            LoadableFlags flags, string[] tags = null, string description = null) {
            _cache.AddRule(new AssetRule(pattern, type, priority, flags, tags ?? Array.Empty<string>(), description));
            return this;
        }
        
        public LoaderConfigurationBuilder WithPreloadRule(string pattern, LoadableType? type, 
            LoadPriority minPriority, long maxSize, string description = null) {
            _cache.AddPreloadRule(new PreloadRule(pattern, type, minPriority, maxSize, description));
            return this;
        }
        
        public LoaderConfigurationBuilder WithPerformance(int maxConcurrent = 3, float timeout = 30f, 
            bool smartCaching = true, int maxCacheSize = 100) {
            _cache.SetPerformance(maxConcurrent, timeout, smartCaching, maxCacheSize);
            return this;
        }
        
        public LoaderConfiguration Build() {
            _cache.ApplyTo(_config);
            return _config;
        }
        
        sealed class ConfigurationCache {
            readonly List<RoleConfiguration> _roles = new List<RoleConfiguration>();
            readonly List<AssetRule> _rules = new List<AssetRule>();
            readonly List<PreloadRule> _preloadRules = new List<PreloadRule>();
            (int maxConcurrent, float timeout, bool smartCaching, int maxCacheSize) _performance = (3, 30f, true, 100);
            
            public void AddRole(RoleConfiguration role) => _roles.Add(role);
            public void AddRule(AssetRule rule) => _rules.Add(rule);
            public void AddPreloadRule(PreloadRule rule) => _preloadRules.Add(rule);
            public void SetPerformance(int maxConcurrent, float timeout, bool smartCaching, int maxCacheSize) =>
                _performance = (maxConcurrent, timeout, smartCaching, maxCacheSize);
            
            public void ApplyTo(LoaderConfiguration config) {
                var field = typeof(LoaderConfiguration).GetField("roleConfigurations", 
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                field?.SetValue(config, _roles.ToArray());
                
                var assetField = typeof(LoaderConfiguration).GetField("assetRules", 
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                assetField?.SetValue(config, _rules.ToArray());
                
                var preloadField = typeof(LoaderConfiguration).GetField("preloadRules", 
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                preloadField?.SetValue(config, _preloadRules.ToArray());
            }
        }
    }
    
    public static class LoaderConfigurationFactory {
        
        static readonly ConcurrentDictionary<string, Func<LoaderConfiguration>> _presets = new();
        static readonly ThreadLocal<LoaderConfigurationBuilder> _builderCache = new();
        
        static LoaderConfigurationFactory() => RegisterDefaultPresets();
        
        public static LoaderConfiguration Create(string name = "LoaderConfiguration") =>
            ScriptableObject.CreateInstance<LoaderConfiguration>().With(config => config.name = name);
        
        public static LoaderConfiguration CreateFromPreset(string presetName) =>
            _presets.TryGetValue(presetName, out var factory) ? 
                factory() : 
                throw new ArgumentException($"Preset '{presetName}' not found");
        
        public static LoaderConfigurationBuilder Configure(LoaderConfiguration config = null) =>
            new(config ?? Create());
        
        public static void RegisterPreset(string name, Func<LoaderConfiguration> factory) =>
            _presets.TryAdd(name, factory);
        
        static void RegisterDefaultPresets() {
            RegisterPreset("Gaming", () => Configure()
                .WithRole("player", "*Player*", LoadableType.Prefab, "Player-related assets")
                .WithRole("enemy", "*Enemy*", LoadableType.Prefab, "Enemy-related assets")
                .WithAssetRule("*UI*", LoadableType.Prefab, LoadPriority.High, LoadableFlags.Preload, 
                    new[] { "ui", "interface" }, "UI elements")
                .WithAssetRule("*Audio*", LoadableType.Audio, LoadPriority.Normal, LoadableFlags.Compressed,
                    new[] { "audio", "sound" }, "Audio assets")
                .WithPreloadRule("*Critical*", LoadableType.Prefab, LoadPriority.High, 1048576, "Critical assets")
                .WithPerformance(5, 15f, true, 150)
                .Build());
            
            RegisterPreset("Education", () => Configure()
                .WithRole("teacher", "*", null, "Full access for teachers")
                .WithRole("student", "*Lesson*", LoadableType.Scene, "Student lesson access")
                .WithAssetRule("*Lesson*", LoadableType.Scene, LoadPriority.High, LoadableFlags.Preload,
                    new[] { "lesson", "education" }, "Educational content")
                .WithAssetRule("*Resource*", LoadableType.Texture, LoadPriority.Normal, LoadableFlags.Compressed,
                    new[] { "resource", "material" }, "Educational resources")
                .WithPreloadRule("*Essential*", null, LoadPriority.High, 2097152, "Essential educational content")
                .WithPerformance(3, 30f, true, 100)
                .Build());
            
            RegisterPreset("Production", () => Configure()
                .WithRole("admin", "*", null, "Administrative access")
                .WithRole("user", "*Public*", null, "Public content access")
                .WithAssetRule("*", null, LoadPriority.Normal, LoadableFlags.Compressed,
                    Array.Empty<string>(), "All assets compressed")
                .WithPreloadRule("*Critical*", null, LoadPriority.High, 524288, "Critical small assets only")
                .WithPerformance(2, 60f, true, 50)
                .Build());
        }
        
        public static string[] AvailablePresets => _presets.Keys.ToArray();
    }
    
    public static class ObjectExtensions {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T With<T>(this T obj, Action<T> action) {
            action(obj);
            return obj;
        }
    }
}
