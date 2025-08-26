using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using UnityEngine;
using com.ImmersizeFramework.Core;
using com.ImmersizeFramework.Tasks;

namespace com.ImmersizeFramework.Loader {
    
    public static class LoaderUtilities {
        
        static LoaderCore Core => FrameworkCore.Instance?.GetService<LoaderCore>();
        
        [Lore("SmartPreload", "Préchargement intelligent basé sur l'usage")]
        public static async Task<int> SmartPreload(int maxItems = 10, LoadPriority minPriority = LoadPriority.Normal) =>
            Core is null ? 0 : await Core.AllItems
                .Where(i => i.Priority >= minPriority && !i.IsLoaded && i.IsPreloadable)
                .OrderByDescending(GetItemScore)
                .Take(maxItems)
                .AsParallel()
                .Select(async item => {
                    try { return await Core.LoadAsset<UnityEngine.Object>(item.Name, LoaderConfig.Background) is not null; }
                    catch { Debug.LogWarning($"[LoaderUtilities] Failed to preload: {item.Name}"); return false; }
                })
                .WhenAll()
                .ContinueWith(t => t.Result.Count(success => success), TaskContinuationOptions.ExecuteSynchronously);
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static float GetItemScore(LoadableItem item) => 
            (float)item.Priority * 2f +
            (item.Size < 1048576).ToFloat() +
            (item.AccessCount > 5 ? item.AccessCount * 0.1f : 0f) +
            (DateTime.UtcNow - item.LastAccess).TotalDays switch {
                < 1 => 2f,
                < 7 => 1f,
                _ => 0f
            };
        
        [Lore("CreateBundle", "Crée un bundle d'assets pour chargement groupé")]
        public static LoadBundle? CreateBundle(string bundleName, params string[] itemNames) =>
            Core is null ? null : new(bundleName, itemNames.Select(name => Core[name]).Where(i => i.IsValid).ToArray());
        
        [Lore("AnalyzeDependencies", "Analyse les dépendances manquantes")]
        public static DependencyReport AnalyzeDependencies() {
            if (Core is null) return default;
            
            var (missing, circular) = Core.AllItems.Aggregate(
                (new List<(string, string[])>(), new List<string[]>()),
                (acc, item) => {
                    var deps = Core.GetDependencies(item.Name);
                    var missingDeps = deps.Where(d => !d.IsValid).Select(d => d.Name).ToArray();
                    
                    if (missingDeps.Length > 0)
                        acc.Item1.Add((item.Name, missingDeps));
                    
                    if (HasCircularDependency(item.Name, item.Dependencies.ToHashSet(), new()))
                        acc.Item2.Add(new[] { item.Name }.Concat(item.Dependencies).ToArray());
                    
                    return acc;
                }
            );
            
            return new DependencyReport(
                missing.ToArray(),
                circular.ToArray(),
                Core.TotalItems,
                Core.AllItems.Count(i => i.Dependencies.Length > 0)
            );
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static bool HasCircularDependency(string itemName, HashSet<string> dependencies, HashSet<string> visited) {
            if (!visited.Add(itemName)) return true;
            
            var result = dependencies.Any(dep => {
                var depItem = Core[dep];
                return depItem.IsValid && HasCircularDependency(dep, depItem.Dependencies.ToHashSet(), new(visited));
            });
            
            visited.Remove(itemName);
            return result;
        }
        
        [Lore("GetOptimizationRecommendations", "Génère des recommandations d'optimisation")]
        public static OptimizationRecommendations GetOptimizationRecommendations() {
            if (Core is null) return default;
            
            var allItems = Core.AllItems;
            var recommendations = new List<string>();
            
            allItems.Where(i => i.Size > 10485760).ToArray().Let(largeAssets => {
                if (largeAssets.Length > 0)
                    recommendations.Add($"Considérez compresser {largeAssets.Length} assets volumineux (>{largeAssets.Sum(a => a.Size) / 1048576f:F1} MB total)");
            });
            
            var cacheSize = Core.CacheSize;
            var lowUsageInCache = allItems.Count(i => i.IsLoaded && i.AccessCount < 2);
            if (lowUsageInCache > cacheSize * 0.3f)
                recommendations.Add($"Nettoyez le cache: {lowUsageInCache} assets peu utilisés chargés");
            
            var untaggedAssets = allItems.Count(i => i.Tags.Length == 0 && i.Roles.Length == 0);
            if (untaggedAssets > allItems.Length * 0.2f)
                recommendations.Add($"Organisez {untaggedAssets} assets sans tags/rôles pour améliorer la recherche");
            
            if (Core.CompressionRatio > 0.8f)
                recommendations.Add("Activez la compression pour réduire l'usage mémoire");
            
            if (Core.AverageLoadTime > 2f)
                recommendations.Add($"Temps de chargement élevé ({Core.AverageLoadTime:F1}s): considérez le préchargement");
            
            return new OptimizationRecommendations(
                recommendations.ToArray(),
                allItems.Length,
                cacheSize > 0 ? (float)Core.LoadedCount / cacheSize : 0f,
                Core.CompressionRatio,
                Core.AverageLoadTime
            );
        }
        
        [Lore("SmartCleanup", "Nettoyage intelligent des assets inutilisés")]
        public static Task<CleanupReport> SmartCleanup(TimeSpan maxAge, int minAccessCount = 1) {
            if (Core is null) return Task.FromResult(default(CleanupReport));
            
            var candidates = Core.AllItems
                .Where(i => i.IsLoaded && 
                           !i.IsPersistent && 
                           i.AccessCount <= minAccessCount &&
                           (DateTime.UtcNow - i.LastAccess) > maxAge)
                .ToArray();
            
            var (cleaned, freed, errors) = candidates.Aggregate(
                (new List<string>(), 0L, new List<string>()),
                (acc, item) => {
                    try {
                        acc.Item1.Add(item.Name);
                        return (acc.Item1, acc.Item2 + item.Size, acc.Item3);
                    } catch (Exception ex) {
                        acc.Item3.Add($"{item.Name}: {ex.Message}");
                        return acc;
                    }
                }
            );
            
            Debug.Log($"[LoaderUtilities] Smart cleanup: {cleaned.Count} items, {freed / 1048576f:F1} MB freed");
            
            return Task.FromResult(new CleanupReport(
                cleaned.ToArray(),
                freed,
                errors.ToArray(),
                candidates.Length
            ));
        }
    }
    
    public struct LoadBundle {
        readonly LoadableItem[] _items;
        readonly ConcurrentBag<string> _loaded;
        
        public string Name { get; }
        public LoadableItem[] Items => _items;
        public long TotalSize => _items.Sum(i => i.Size);
        public bool IsFullyLoaded => _items.All(i => i.IsLoaded);
        public float LoadProgress => _items.Length > 0 ? (float)_loaded.Count / _items.Length : 1f;
        
        public LoadBundle(string name, LoadableItem[] items) =>
            (Name, _items, _loaded) = (name, items, new());
        
        public async Task<bool> LoadAll(LoaderConfig config = default) {
            var core = FrameworkCore.Instance?.GetService<LoaderCore>();
            if (core is null) return false;
            
            var bundle = this;
            var tasks = bundle._items.Select(async item => {
                try {
                    var result = await core.LoadAsset<UnityEngine.Object>(item.Name, config);
                    if (result is not null) {
                        bundle._loaded.Add(item.Name);
                        return true;
                    }
                } catch {
                    Debug.LogError($"[LoadBundle] Failed to load: {item.Name}");
                }
                return false;
            });
            
            return (await Task.WhenAll(tasks)).All(r => r);
        }
        
        public LoadableItem this[int index] => _items[index];
        public LoadableItem this[string name] => _items.FirstOrDefault(i => i.Name == name);
    }
    
    public readonly struct DependencyReport {
        public readonly (string item, string[] missing)[] MissingDependencies;
        public readonly string[][] CircularDependencies;
        public readonly int TotalItems;
        public readonly int ItemsWithDependencies;
        
        public DependencyReport((string item, string[] missing)[] missingDependencies, string[][] circularDependencies, int totalItems, int itemsWithDependencies) =>
            (MissingDependencies, CircularDependencies, TotalItems, ItemsWithDependencies) = (missingDependencies, circularDependencies, totalItems, itemsWithDependencies);
        
        public bool HasIssues => MissingDependencies.Length > 0 || CircularDependencies.Length > 0;
        public int TotalMissingDeps => MissingDependencies.Sum(md => md.missing.Length);
    }
    
    public readonly struct OptimizationRecommendations {
        public readonly string[] Recommendations;
        public readonly int TotalAssets;
        public readonly float CacheEfficiency;
        public readonly float CompressionRatio;
        public readonly float AverageLoadTime;
        
        public OptimizationRecommendations(string[] recommendations, int totalAssets, float cacheEfficiency, float compressionRatio, float averageLoadTime) =>
            (Recommendations, TotalAssets, CacheEfficiency, CompressionRatio, AverageLoadTime) = (recommendations, totalAssets, cacheEfficiency, compressionRatio, averageLoadTime);
        
        public bool HasRecommendations => Recommendations.Length > 0;
        public OptimizationLevel Level => CacheEfficiency switch {
            < 0.5f when AverageLoadTime > 3f => OptimizationLevel.Poor,
            < 0.7f when AverageLoadTime > 1.5f => OptimizationLevel.Fair,
            < 0.9f when AverageLoadTime > 0.5f => OptimizationLevel.Good,
            _ => OptimizationLevel.Excellent
        };
    }
    
    public enum OptimizationLevel : byte { Poor, Fair, Good, Excellent }
    
    public readonly struct CleanupReport {
        public readonly string[] CleanedItems;
        public readonly long FreedMemory;
        public readonly string[] Errors;
        public readonly int TotalCandidates;
        
        public CleanupReport(string[] cleanedItems, long freedMemory, string[] errors, int totalCandidates) =>
            (CleanedItems, FreedMemory, Errors, TotalCandidates) = (cleanedItems, freedMemory, errors, totalCandidates);
        
        public bool HasErrors => Errors.Length > 0;
        public float SuccessRate => TotalCandidates > 0 ? (float)CleanedItems.Length / TotalCandidates : 0f;
        public float FreedMemoryMB => FreedMemory / 1048576f;
    }
    
    static class TaskExtensions {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static async Task<T[]> WhenAll<T>(this IEnumerable<Task<T>> tasks) =>
            await Task.WhenAll(tasks);
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float ToFloat(this bool value) => value ? 1f : 0f;
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Let<T>(this T value, Action<T> action) => action(value);
    }
}
