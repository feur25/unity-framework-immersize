using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using UnityEngine;
using com.ImmersizeFramework.Core;
using com.ImmersizeFramework.Tasks;
using Debug = UnityEngine.Debug;

namespace com.ImmersizeFramework.Loader {
    
    public static class LoaderValidator {
        
        static LoaderCore Core => FrameworkCore.Instance?.GetService<LoaderCore>();
        
        [Lore("ValidateFramework", "Validation complète du framework")]
        public static ValidationReport ValidateFramework() {
            if (Core is null) return ValidationReport.Failed("LoaderCore not available");
            
            var sw = Stopwatch.StartNew();
            var issues = new ConcurrentBag<ValidationIssue>();
            
            var validators = new Func<ValidationIssue[]>[] {
                ValidateRegistry,
                ValidateDependencies,
                ValidatePerformance,
                ValidateMemoryUsage,
                ValidateConfiguration
            };
            
            Parallel.ForEach(validators, validator => {
                try {
                    validator().ForEach(issues.Add);
                } catch (Exception ex) {
                    issues.Add(new($"Validator exception: {ex.Message}", ValidationSeverity.Critical));
                }
            });
            
            sw.Stop();
            
            var allIssues = issues.ToArray();
            var criticalCount = allIssues.Count(i => i.Severity == ValidationSeverity.Critical);
            var warningCount = allIssues.Count(i => i.Severity == ValidationSeverity.Warning);
            
            return new ValidationReport(
                criticalCount == 0,
                allIssues,
                sw.Elapsed,
                $"Validation completed: {criticalCount} critical, {warningCount} warnings"
            );
        }
        
        static ValidationIssue[] ValidateRegistry() {
            var issues = new List<ValidationIssue>();
            
            if (Core.TotalItems == 0)
                issues.Add(new("Registry is empty", ValidationSeverity.Warning));
            
            Core.AllItems
                .GroupBy(i => i.Name)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ForEach(name => issues.Add(new($"Duplicate item name: {name}", ValidationSeverity.Critical)));
            
            Core.AllItems
                .Where(i => string.IsNullOrEmpty(i.Path))
                .Select(i => i.Name)
                .ForEach(name => issues.Add(new($"Invalid path for item: {name}", ValidationSeverity.Critical)));
            
            Core.AllItems
                .Where(i => i.Size > 100 * 1024 * 1024)
                .Select(i => i.Name)
                .ForEach(name => issues.Add(new($"Oversized item (>100MB): {name}", ValidationSeverity.Warning)));
            
            return issues.ToArray();
        }
        
        static ValidationIssue[] ValidateDependencies() {
            var issues = new List<ValidationIssue>();
            var dependencyReport = LoaderUtilities.AnalyzeDependencies();
            
            dependencyReport.MissingDependencies.ForEach(dep => 
                issues.Add(new($"Missing dependencies for {dep.item}: {string.Join(", ", dep.missing)}", 
                    ValidationSeverity.Critical)));
            
            dependencyReport.CircularDependencies.ForEach(circular => 
                issues.Add(new($"Circular dependency: {string.Join(" → ", circular)}", 
                    ValidationSeverity.Critical)));
            
            return issues.ToArray();
        }
        
        static ValidationIssue[] ValidatePerformance() {
            var items = Core.AllItems;
            var total = Core.TotalItems;
            var slowLoads = items.Count(i => i.LoadTime > 5f);
            var failedLoads = items.Count(i => i.IsFailed);
            var lowCompression = items.Count(i => i.CompressionRatio > 0.9f);

            return new[] {
                slowLoads > total * 0.2f ? new ValidationIssue($"Many items with slow load time (>5s): {slowLoads}/{total}", ValidationSeverity.Warning) : default,
                failedLoads > total * 0.1f ? new ValidationIssue($"High failure rate: {failedLoads}/{total}", ValidationSeverity.Critical) : default,
                lowCompression > total * 0.3f ? new ValidationIssue($"Many items with low compression efficiency: {lowCompression}/{total}", ValidationSeverity.Warning) : default
            }.Where(issue => issue.Message != null).ToArray();
        }
        
        static ValidationIssue[] ValidateMemoryUsage() {
            var totalMemoryMB = Core.TotalSize / (1024f * 1024f);
            var issues = new List<ValidationIssue>();
            
            if (totalMemoryMB > 1000f)
                issues.Add(new($"High total memory usage: {totalMemoryMB:F1} MB", ValidationSeverity.Warning));
            
            if (Core.CacheSize > 500)
                issues.Add(new($"Large cache size: {Core.CacheSize} items", ValidationSeverity.Warning));
            
            return issues.ToArray();
        }
        
        static ValidationIssue[] ValidateConfiguration() {
            var items = Core.AllItems;
            var total = Core.TotalItems;
            var untagged = items.Count(i => i.Tags.Length == 0);
            var unroled = items.Count(i => i.Roles.Length == 0);

            return new[] {
                untagged > total * 0.5f ? new ValidationIssue($"Many items without tags: {untagged}/{total}", ValidationSeverity.Warning) : default,
                unroled > total * 0.3f ? new ValidationIssue($"Many items without roles: {unroled}/{total}", ValidationSeverity.Warning) : default
            }.Where(issue => issue.Message != null).ToArray();
        }
    }
    
    public static class LoaderBenchmark {
        
        static LoaderCore Core => FrameworkCore.Instance?.GetService<LoaderCore>();
        
        [Lore("RunBenchmark", "Benchmark complet du système")]
        public static async Task<BenchmarkReport> RunBenchmark(int iterations = 10) {
            if (Core is null) return BenchmarkReport.Failed("LoaderCore not available");
            
            var sw = Stopwatch.StartNew();
            var results = new ConcurrentBag<BenchmarkResult>();
            
            var testCases = new (string name, Func<int, Task<(TimeSpan averageTime, bool success, string details)>> benchmark)[] {
                ("Scene Loading", BenchmarkSceneLoading),
                ("Asset Loading", BenchmarkAssetLoading),
                ("Cache Performance", BenchmarkCachePerformance),
                ("Search Performance", BenchmarkSearchPerformance),
                ("Registry Operations", BenchmarkRegistryOperations)
            };
            
            foreach (var (name, benchmark) in testCases) {
                try {
                    var result = await benchmark(iterations);
                    results.Add(new(name, result.averageTime, result.success, result.details));
                } catch (Exception ex) {
                    results.Add(new(name, TimeSpan.MaxValue, false, ex.Message));
                }
            }
            
            sw.Stop();
            
            var allResults = results.ToArray();
            var successRate = allResults.Count(r => r.Success) / (float)allResults.Length;
            
            return new BenchmarkReport(
                allResults,
                sw.Elapsed,
                successRate,
                $"Benchmark completed: {successRate:P0} success rate in {sw.Elapsed.TotalSeconds:F2}s"
            );
        }
        
        static async Task<(TimeSpan averageTime, bool success, string details)> BenchmarkSceneLoading(int iterations) {
            var scenes = Core.GetScenes().Take(Math.Min(iterations, 5)).ToArray();
            if (scenes.Length == 0) return (TimeSpan.Zero, false, "No scenes available");
            
            var (times, successCount) = await scenes.Aggregate(
                Task.FromResult((new List<TimeSpan>(), 0)),
                async (acc, scene) => {
                    var (prevTimes, prevSuccess) = await acc;
                    var sw = Stopwatch.StartNew();
                    try {
                        var result = await Core.LoadScene(scene, config: LoaderConfig.Fast);
                        sw.Stop();
                        
                        if (result) {
                            prevTimes.Add(sw.Elapsed);
                            return (prevTimes, prevSuccess + 1);
                        }
                    } catch {
                        sw.Stop();
                    }
                    return (prevTimes, prevSuccess);
                }
            );
            
            var avgTime = times.Count > 0 ? TimeSpan.FromTicks(times.Sum(t => t.Ticks) / times.Count) : TimeSpan.Zero;
            return (avgTime, successCount == scenes.Length, $"Loaded {successCount}/{scenes.Length} scenes");
        }
        
        static async Task<(TimeSpan averageTime, bool success, string details)> BenchmarkAssetLoading(int iterations) {
            var assets = Core.AllItems
                .Where(i => i.Type != LoadableType.Scene)
                .Take(Math.Min(iterations, 20))
                .ToArray();
            
            if (assets.Length == 0) return (TimeSpan.Zero, false, "No assets available");
            
            var times = new List<TimeSpan>();
            var successCount = 0;
            
            foreach (var asset in assets) {
                var sw = Stopwatch.StartNew();
                try {
                    var result = await Core.LoadAsset<UnityEngine.Object>(asset.Name, LoaderConfig.Fast);
                    sw.Stop();
                    
                    if (result is not null) {
                        times.Add(sw.Elapsed);
                        successCount++;
                    }
                } catch {
                    sw.Stop();
                }
            }
            
            var avgTime = times.Count > 0 ? TimeSpan.FromTicks(times.Sum(t => t.Ticks) / times.Count) : TimeSpan.Zero;
            return (avgTime, successCount > assets.Length * 0.8f, $"Loaded {successCount}/{assets.Length} assets");
        }
        
        static Task<(TimeSpan averageTime, bool success, string details)> BenchmarkCachePerformance(int iterations) {
            var sw = Stopwatch.StartNew();
            var hits = Core.AllItems.Take(iterations).Count(item => Core[item.Name].IsLoaded);
            sw.Stop();
            
            var hitRate = iterations > 0 ? hits / (float)iterations : 0f;
            return Task.FromResult((sw.Elapsed, hitRate > 0.5f, $"Cache hit rate: {hitRate:P0}"));
        }
        
        static Task<(TimeSpan averageTime, bool success, string details)> BenchmarkSearchPerformance(int iterations) {
            var sw = Stopwatch.StartNew();
            var searchTerms = new[] { "test", "main", "ui", "audio", "texture" };
            var totalResults = searchTerms.Take(iterations).Sum(term => Core.Search(term).Length);
            sw.Stop();
            
            var avgResultsPerSearch = iterations > 0 ? totalResults / (float)iterations : 0f;
            return Task.FromResult((sw.Elapsed, totalResults > 0, $"Avg results per search: {avgResultsPerSearch:F1}"));
        }
        
        static Task<(TimeSpan averageTime, bool success, string details)> BenchmarkRegistryOperations(int iterations) {
            var sw = Stopwatch.StartNew();
            var operationsCount = Core.GetStatistics().Count + Core.GetStateStatistics().Count + Core.GetPreloadable().Length;
            sw.Stop();
            
            return Task.FromResult((sw.Elapsed, operationsCount > 0, $"Registry operations: {operationsCount}"));
        }
    }
    
    public readonly struct ValidationReport {
        public readonly bool IsValid;
        public readonly ValidationIssue[] Issues;
        public readonly TimeSpan ValidationTime;
        public readonly string Summary;
        
        public ValidationReport(bool isValid, ValidationIssue[] issues, TimeSpan validationTime, string summary) =>
            (IsValid, Issues, ValidationTime, Summary) = (isValid, issues, validationTime, summary);
        
        public ValidationIssue[] CriticalIssues => Issues.Where(i => i.Severity == ValidationSeverity.Critical).ToArray();
        public ValidationIssue[] Warnings => Issues.Where(i => i.Severity == ValidationSeverity.Warning).ToArray();
        
        public static ValidationReport Failed(string message) => new(
            false,
            new[] { new ValidationIssue(message, ValidationSeverity.Critical) },
            TimeSpan.Zero,
            message
        );
    }
    
    public readonly struct ValidationIssue {
        public readonly string Message;
        public readonly ValidationSeverity Severity;
        
        public ValidationIssue(string message, ValidationSeverity severity) =>
            (Message, Severity) = (message, severity);
    }
    
    public enum ValidationSeverity : byte { Info, Warning, Critical }
    
    public readonly struct BenchmarkReport {
        public readonly BenchmarkResult[] Results;
        public readonly TimeSpan TotalTime;
        public readonly float SuccessRate;
        public readonly string Summary;
        
        public BenchmarkReport(BenchmarkResult[] results, TimeSpan totalTime, float successRate, string summary) =>
            (Results, TotalTime, SuccessRate, Summary) = (results, totalTime, successRate, summary);
        
        public BenchmarkResult[] FailedTests => Results.Where(r => !r.Success).ToArray();
        public TimeSpan AverageTime => Results.Length > 0 ? 
            TimeSpan.FromTicks(Results.Sum(r => r.Time.Ticks) / Results.Length) : TimeSpan.Zero;
        
        public static BenchmarkReport Failed(string message) => new(
            Array.Empty<BenchmarkResult>(),
            TimeSpan.Zero,
            0f,
            message
        );
    }
    
    public readonly struct BenchmarkResult {
        public readonly string TestName;
        public readonly TimeSpan Time;
        public readonly bool Success;
        public readonly string Details;
        
        public BenchmarkResult(string testName, TimeSpan time, bool success, string details) =>
            (TestName, Time, Success, Details) = (testName, time, success, details);
    }
    
    static class CollectionExtensions {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ForEach<T>(this IEnumerable<T> source, Action<T> action) {
            foreach (var item in source) action(item);
        }
    }
}
