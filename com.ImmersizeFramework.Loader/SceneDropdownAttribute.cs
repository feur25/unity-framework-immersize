using System;
using UnityEngine;

namespace com.ImmersizeFramework.Loader {
    
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class SceneDropdownAttribute : PropertyAttribute {
        
        public readonly bool IncludeBuildSettings, IncludeAssetDatabase;
        public readonly string FilterTag;
        
        public SceneDropdownAttribute(bool buildSettings = true, bool assetDatabase = false, string filter = null) =>
            (IncludeBuildSettings, IncludeAssetDatabase, FilterTag) = (buildSettings, assetDatabase, filter ?? string.Empty);
        
        public bool HasFilter => !string.IsNullOrEmpty(FilterTag);
    }
}
