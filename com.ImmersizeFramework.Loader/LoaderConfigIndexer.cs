using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

using com.ImmersizeFramework.Core;

namespace com.ImmersizeFramework.Loader {
    public sealed class LoaderConfigIndexer : IDisposable {
        
        readonly ConcurrentDictionary<ConfigKey, AssetRule> _ruleCache = new();
        readonly ConcurrentDictionary<string, RoleConfiguration> _roleIndex = new();
        readonly ConcurrentDictionary<LoadableType, AssetRule[]> _typeIndex = new();
        readonly ConcurrentDictionary<string, AssetRule[]> _patternIndex = new();
        readonly LoaderConfiguration _config;
        readonly object _lock = new();
        
        volatile bool _disposed;
        
        public LoaderConfigIndexer(LoaderConfiguration config) {
            _config = config;
            new IndexBuilder(this, _config).Execute();
        }
        
        public RoleConfiguration this[string roleName] =>
            _roleIndex.TryGetValue(roleName, out var role) ? role : default;
        
        public AssetRule[] this[LoadableType type] =>
            _typeIndex.TryGetValue(type, out var rules) ? rules : EmptyAssetRules;
        
        static readonly AssetRule[] EmptyAssetRules = Array.Empty<AssetRule>();
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void CacheRule(in ConfigKey key, in AssetRule value) => _ruleCache[key] = value;
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public AssetRule GetCachedRule(in ConfigKey key) =>
            _ruleCache.TryGetValue(key, out var rule) ? rule : default;
        
        public AssetRule this[string pattern, LoadableType type] {
            get {
                var key = new ConfigKey(pattern, type);
                var cached = GetCachedRule(in key);

                return !cached.Equals(default) ? cached : CacheAndReturn(in key, pattern, type);
            }
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        AssetRule CacheAndReturn(in ConfigKey key, string pattern, LoadableType type) {
            var rule = FindOptimalRule(pattern, type);
            CacheRule(in key, in rule);

            return rule;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        AssetRule FindOptimalRule(string pattern, LoadableType type) {
            var rules = _config.GetAssetRules();
            for (int i = 0; i < rules.Length; i++) {
                ref readonly var rule = ref rules[i];

                if (rule.AssetType == type && PatternMatcher.Matches(pattern, rule.AssetPattern))
                    return rule;
            }
            return default;
        }
        
        static class PatternMatcher {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool Matches(string item, string pattern) => pattern switch {
                null or "" => false,
                "*" => true,
                _ when !pattern.Contains('*') => item.Equals(pattern, StringComparison.OrdinalIgnoreCase),
                _ => MatchesWildcard(item, pattern)
            };
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static bool MatchesWildcard(string item, string pattern) {
                var parts = pattern.Split('*', StringSplitOptions.RemoveEmptyEntries);
                var index = 0;
                
                for (int i = 0; i < parts.Length; i++) {
                    var part = parts[i];
                    var found = item.IndexOf(part, index, StringComparison.OrdinalIgnoreCase);

                    if (found == -1) return false;

                    index = found + part.Length;
                }
                return true;
            }
        }
        
        readonly struct IndexBuilder {
            readonly LoaderConfigIndexer _indexer;
            readonly LoaderConfiguration _config;
            
            public IndexBuilder(LoaderConfigIndexer indexer, LoaderConfiguration config) =>
                (_indexer, _config) = (indexer, config);
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Execute() {
                ProcessRoles();
                ProcessTypes();
                ProcessPatterns();
            }
            
            void ProcessRoles() {
                var roles = _config.GetRoleConfigurations();
                var roleIndex = _indexer._roleIndex;

                for (int i = 0; i < roles.Length; i++)
                    roleIndex.TryAdd(roles[i].RoleName, roles[i]);
            }
            
            void ProcessTypes() {
                var rules = _config.GetAssetRules();
                var typeIndex = _indexer._typeIndex;
                var groups = new Dictionary<LoadableType, List<AssetRule>>();
                
                for (int i = 0; i < rules.Length; i++) {
                    ref readonly var rule = ref rules[i];

                    if (!rule.AssetType.HasValue) continue;
                    
                    var type = rule.AssetType.Value;
                    if (!groups.TryGetValue(type, out var list)) {
                        list = new List<AssetRule>();
                        groups[type] = list;
                    }
                    list.Add(rule);
                }
                
                foreach (var (type, ruleList) in groups)
                    typeIndex.TryAdd(type, ruleList.ToArray());
            }
            
            void ProcessPatterns() {
                var rules = _config.GetAssetRules();
                var patternIndex = _indexer._patternIndex;
                var groups = new Dictionary<string, List<AssetRule>>();
                
                for (int i = 0; i < rules.Length; i++) {
                    ref readonly var rule = ref rules[i];
                    var pattern = rule.AssetPattern;
                    
                    if (!groups.TryGetValue(pattern, out var list)) {
                        list = new List<AssetRule>();
                        groups[pattern] = list;
                    }
                    list.Add(rule);
                }
                
                foreach (var (pattern, ruleList) in groups)
                    patternIndex.TryAdd(pattern, ruleList.ToArray());
            }
        }
        
        readonly struct ResourceDisposal {
            readonly LoaderConfigIndexer _indexer;
            
            public ResourceDisposal(LoaderConfigIndexer indexer) => _indexer = indexer;
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Execute() {
                var indexer = _indexer;
                indexer._ruleCache.Clear();
                indexer._roleIndex.Clear();
                indexer._typeIndex.Clear();
                indexer._patternIndex.Clear();
            }
        }
        
        public void Dispose() {
            if (_disposed) return;
            
            lock (_lock)
            {
                if (_disposed) return;

                new ResourceDisposal(this).Execute();
                _disposed = true;
            }
        }
    }
    
    public readonly struct ConfigKey : IEquatable<ConfigKey> {
        public readonly string Pattern;
        public readonly LoadableType Type;
        readonly int _precomputedHash;
        
        public ConfigKey(string pattern, LoadableType type) =>
            (Pattern, Type, _precomputedHash) = (pattern, type, HashCode.Combine(pattern?.GetHashCode() ?? 0, type));
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override int GetHashCode() => _precomputedHash;
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Equals(ConfigKey other) => 
            Type == other.Type && string.Equals(Pattern, other.Pattern, StringComparison.Ordinal);
        
        public override bool Equals(object obj) => obj is ConfigKey other && Equals(other);
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator ==(ConfigKey left, ConfigKey right) => left.Equals(right);
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator !=(ConfigKey left, ConfigKey right) => !left.Equals(right);
    }
    
    public static class LoaderConfigurationExtensions {
        
        static readonly ConcurrentDictionary<LoaderConfiguration, LoaderConfigIndexer> _indexerCache = new();
        static readonly FieldInfo _assetRulesField = typeof(LoaderConfiguration).GetField("assetRules", 
            BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo _roleConfigField = typeof(LoaderConfiguration).GetField("roleConfigurations", 
            BindingFlags.NonPublic | BindingFlags.Instance);
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static LoaderConfigIndexer GetIndexer(this LoaderConfiguration config) =>
            _indexerCache.GetOrAdd(config, static cfg => new LoaderConfigIndexer(cfg));
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static AssetRule[] GetAssetRules(this LoaderConfiguration config) =>
            _assetRulesField?.GetValue(config) as AssetRule[] ?? Array.Empty<AssetRule>();
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static RoleConfiguration[] GetRoleConfigurations(this LoaderConfiguration config) =>
            _roleConfigField?.GetValue(config) as RoleConfiguration[] ?? Array.Empty<RoleConfiguration>();
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static RoleConfiguration GetRole(this LoaderConfiguration config, string roleName) =>
            config.GetIndexer()[roleName];
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static AssetRule[] GetRulesForType(this LoaderConfiguration config, LoadableType type) =>
            config.GetIndexer()[type];
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static AssetRule GetRule(this LoaderConfiguration config, string pattern, LoadableType type) =>
            config.GetIndexer()[pattern, type];
    }
}
