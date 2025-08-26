#if UNITY_EDITOR
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using UnityEditor;
using UnityEngine;

namespace com.ImmersizeFramework.Loader.Editor {
    
    internal static class SceneRegistry {
        
        static readonly ConcurrentDictionary<string, SceneManifest> _manifests = new();
        static readonly object _lock = new();
        static volatile bool _isInitialized;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SceneManifest GetManifest(SceneDropdownAttribute attribute) =>
            _manifests.GetOrAdd(GenerateKey(attribute), _ => BuildManifest(attribute));

        public static void InvalidateCache() {
            lock (_lock) {
                _manifests.Clear();
                _isInitialized = false;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static SceneManifest BuildManifest(SceneDropdownAttribute attribute) =>
            new SceneManifestBuilder(attribute).Build();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static string GenerateKey(SceneDropdownAttribute attribute) =>
            $"{attribute.IncludeBuildSettings}_{attribute.IncludeAssetDatabase}_{attribute.FilterTag}";

        [InitializeOnLoadMethod]
        static void Initialize() {
            if (_isInitialized) return;
            
            EditorApplication.projectChanged += InvalidateCache;
            _isInitialized = true;
        }
    }

    internal readonly struct SceneManifest {
        readonly SceneEntry[] _entries;

        public SceneManifest(SceneEntry[] entries) => _entries = entries ?? Array.Empty<SceneEntry>();

        public ReadOnlySpan<SceneEntry> Entries => _entries.AsSpan();
        public string[] DisplayNames => _entries.Select(e => e.DisplayName).ToArray();
        public int Count => _entries.Length;

        public SceneEntry this[int index] => 
            index >= 0 && index < _entries.Length ? _entries[index] : default;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int FindIndex(string sceneName) =>
            Array.FindIndex(_entries, entry => entry.Name.Equals(sceneName, StringComparison.Ordinal));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetEntry(string sceneName, out SceneEntry entry) {
            var index = FindIndex(sceneName);
            entry = index >= 0 ? _entries[index] : default;
            return index >= 0;
        }
    }

    internal readonly struct SceneEntry : IEquatable<SceneEntry> {
        public string Name { get; }
        public string Path { get; }
        public SceneSource Source { get; }

        public SceneEntry(string name, string path, SceneSource source) =>
            (Name, Path, Source) = (name ?? string.Empty, path ?? string.Empty, source);

        public string DisplayName => Source switch {
            SceneSource.BuildSettings => Name,
            SceneSource.AssetDatabase => $"{Name} (Asset)",
            SceneSource.None => "(None)",
            _ => Name
        };

        public bool IsValid => !string.IsNullOrEmpty(Name) && Source != SceneSource.None;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Equals(SceneEntry other) =>
            Name == other.Name && Path == other.Path && Source == other.Source;

        public override bool Equals(object obj) => obj is SceneEntry other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Name, Path, Source);
        public override string ToString() => $"{DisplayName} ({Source})";
    }

    internal enum SceneSource : byte {
        None,
        BuildSettings,
        AssetDatabase
    }

    internal sealed class SceneManifestBuilder {
        readonly SceneDropdownAttribute _attribute;
        readonly List<SceneEntry> _entries = new List<SceneEntry>();
        readonly HashSet<string> _processedNames = new HashSet<string>(StringComparer.Ordinal);

        public SceneManifestBuilder(SceneDropdownAttribute attribute) => _attribute = attribute;

        public SceneManifest Build() {
            AddNoneEntry();
            
            if (_attribute.IncludeBuildSettings) AddBuildSettingsEntries();
            if (_attribute.IncludeAssetDatabase) AddAssetDatabaseEntries();
            
            ApplyFiltering();
            SortEntries();
            
            return new SceneManifest(_entries.ToArray());
        }

        void AddNoneEntry() => (_entries, _processedNames).Let(static t => {
            t._entries.Add(new("(None)", string.Empty, SceneSource.None));
            t._processedNames.Add("(None)");
        });

        void AddBuildSettingsEntries() =>
            EditorBuildSettings.scenes
                .Where(scene => scene.enabled)
                .Select(scene => new SceneEntry(
                    Path.GetFileNameWithoutExtension(scene.path),
                    scene.path,
                    SceneSource.BuildSettings))
                .Where(entry => _processedNames.Add(entry.Name))
                .ForEach(_entries.Add);

        void AddAssetDatabaseEntries() =>
            AssetDatabase.FindAssets("t:Scene")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => !string.IsNullOrEmpty(path))
                .Select(path => new SceneEntry(
                    Path.GetFileNameWithoutExtension(path),
                    path,
                    SceneSource.AssetDatabase))
                .Where(entry => _processedNames.Add(entry.Name))
                .ForEach(_entries.Add);

        void ApplyFiltering() {
            if (!_attribute.HasFilter) return;
            
            _entries.Where(entry => 
                entry.Source == SceneSource.None || 
                entry.Name.Contains(_attribute.FilterTag, StringComparison.OrdinalIgnoreCase))
                .ToList()
                .Let(filtered => {
                    _entries.Clear();
                    _entries.AddRange(filtered);
                });
        }

        void SortEntries() => _entries.First(e => e.Source == SceneSource.None).Let(noneEntry => {
            _entries.Skip(1)
                .OrderBy(e => e.Source)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .Prepend(noneEntry)
                .ToList()
                .Let(sortedEntries => {
                    _entries.Clear();
                    _entries.AddRange(sortedEntries);
                });
        });
    }

    internal static class CollectionExtensions {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ForEach<T>(this IEnumerable<T> source, Action<T> action) {
            foreach (var item in source) action(item);
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Let<T>(this T value, Action<T> action) => action(value);
    }
}
#endif
