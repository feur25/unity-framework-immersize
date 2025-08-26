using UnityEngine;
using com.ImmersizeFramework.Core;
using com.ImmersizeFramework.Loader;

namespace com.ImmersizeFramework.Loader {
    public static class LoaderInitializer {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Initialize() {
            var core = FrameworkCore.Instance;
            if (core == null) {
                Debug.LogWarning("[LoaderInitializer] FrameworkCore not found");
                return;
            }

            try {
                var loader = core.GetService<LoaderCore>() ?? new LoaderCore();
                if (!core.HasService<LoaderCore>()) core.RegisterService<LoaderCore>(loader);
                
                Debug.Log("[LoaderInitializer] LoaderCore registered successfully");
            } catch (System.Exception ex) {
                Debug.LogError($"[LoaderInitializer] Failed to register LoaderCore: {ex.Message}");
            }
        }
    }
}
