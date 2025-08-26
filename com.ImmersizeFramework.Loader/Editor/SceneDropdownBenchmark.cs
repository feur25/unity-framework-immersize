#if UNITY_EDITOR
using System;
using System.Diagnostics;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace com.ImmersizeFramework.Loader.Editor {
    internal static class SceneDropdownBenchmark {
        [MenuItem("Tools/ImmersizeFramework/Benchmark Scene Dropdown")]
        public static void RunBenchmark() {
            const int iterations = 1000;
            var attribute = new SceneDropdownAttribute();

            var stopwatch = Stopwatch.StartNew();
            
            for (int i = 0; i < iterations; i++) {
                var testManifest = SceneRegistry.GetManifest(attribute);
                _ = testManifest.FindIndex("TestScene");
            }
            
            stopwatch.Stop();
            
            var avgTime = stopwatch.ElapsedMilliseconds / (double)iterations;
            var manifest = SceneRegistry.GetManifest(attribute);
            
            UnityEngine.Debug.Log($"[Benchmark] Scene Dropdown Performance:\n" +
                $"• Total scenes: {manifest.Count}\n" +
                $"• Iterations: {iterations:N0}\n" +
                $"• Total time: {stopwatch.ElapsedMilliseconds}ms\n" +
                $"• Average per operation: {avgTime:F3}ms\n" +
                $"• Operations per second: {1000 / avgTime:F0}");
        }

        [MenuItem("Tools/ImmersizeFramework/Clear Scene Cache")]
        public static void ClearCache() {
            SceneRegistry.InvalidateCache();
            UnityEngine.Debug.Log("[SceneDropdown] Cache cleared successfully");
        }

        [MenuItem("Tools/ImmersizeFramework/Scene Dropdown Info")]
        public static void ShowInfo() {
            var attributes = new[] {
                new SceneDropdownAttribute(true, false),
                new SceneDropdownAttribute(false, true),
                new SceneDropdownAttribute(true, true)
            };

            foreach (var attr in attributes) {
                var manifest = SceneRegistry.GetManifest(attr);
                UnityEngine.Debug.Log($"[SceneDropdown] {attr}: {manifest.Count} scenes");
            }
        }
    }

    [InitializeOnLoad]
    internal static class SceneDropdownProfiler {
        private static readonly Stopwatch _stopwatch = new();
        private static int _operationCount;

        static SceneDropdownProfiler() {
            EditorApplication.update += TrackPerformance;
        }

        private static void TrackPerformance() {
            if (_operationCount > 0 && _stopwatch.ElapsedMilliseconds > 5000) {
                var avgTime = _stopwatch.ElapsedMilliseconds / (double)_operationCount;
                
                if (avgTime > 1.0) {
                    UnityEngine.Debug.LogWarning($"[SceneDropdown] Performance alert: " +
                        $"Average operation time: {avgTime:F2}ms ({_operationCount} ops)");
                }
                
                Reset();
            }
        }

        public static void RecordOperation() {
            if (_operationCount == 0)
                _stopwatch.Restart();
                
            _operationCount++;
        }

        private static void Reset() {
            _stopwatch.Reset();
            _operationCount = 0;
        }
    }
}
#endif
