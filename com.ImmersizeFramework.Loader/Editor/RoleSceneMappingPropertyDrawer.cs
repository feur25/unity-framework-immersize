#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using System.Linq;
using System.Runtime.CompilerServices;

namespace com.ImmersizeFramework.Loader.Editor {
    [CustomPropertyDrawer(typeof(RoleSceneMapping))]
    internal sealed class RoleSceneMappingPropertyDrawer : PropertyDrawer {
        
        const float SPACING = 2f;
        const float LINE_HEIGHT = 18f;
        
        static string[] _sceneNames;
        static string[] _scenePaths;
        
        static RoleSceneMappingPropertyDrawer() => RefreshSceneList();
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void RefreshSceneList() {
            var buildScenes = EditorBuildSettings.scenes
                .Where(scene => scene.enabled && !string.IsNullOrEmpty(scene.path))
                .ToArray();
            
            _scenePaths = new string[buildScenes.Length + 1];
            _sceneNames = new string[buildScenes.Length + 1];
            
            _scenePaths[0] = string.Empty;
            _sceneNames[0] = "None";
            
            for (int i = 0; i < buildScenes.Length; i++) {
                _scenePaths[i + 1] = buildScenes[i].path;
                _sceneNames[i + 1] = System.IO.Path.GetFileNameWithoutExtension(buildScenes[i].path);
            }
        }
        
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label) {
            if (_sceneNames == null || _sceneNames.Length == 0) RefreshSceneList();
            
            EditorGUI.BeginProperty(position, label, property);
            
            var roleProperty = property.FindPropertyRelative("Role");
            var sceneProperty = property.FindPropertyRelative("SceneName");
            
            var roleRect = new Rect(position.x, position.y, position.width * 0.4f - SPACING, LINE_HEIGHT);
            var sceneRect = new Rect(position.x + position.width * 0.4f, position.y, position.width * 0.6f, LINE_HEIGHT);
            
            EditorGUI.PropertyField(roleRect, roleProperty, GUIContent.none);
            
            var currentSceneName = sceneProperty.stringValue;
            var currentIndex = System.Array.FindIndex(_sceneNames, name => name == currentSceneName);
            if (currentIndex < 0) currentIndex = 0;
            
            var newIndex = EditorGUI.Popup(sceneRect, currentIndex, _sceneNames);
            
            if (newIndex != currentIndex && newIndex >= 0 && newIndex < _sceneNames.Length) {
                sceneProperty.stringValue = newIndex == 0 ? string.Empty : _sceneNames[newIndex];
            }
            
            EditorGUI.EndProperty();
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override float GetPropertyHeight(SerializedProperty property, GUIContent label) =>
            LINE_HEIGHT + SPACING;
            
        [InitializeOnLoadMethod]
        static void Initialize() {
            EditorBuildSettings.sceneListChanged += RefreshSceneList;
        }
    }
}
#endif
