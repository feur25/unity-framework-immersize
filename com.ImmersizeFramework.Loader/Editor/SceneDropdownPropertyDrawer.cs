#if UNITY_EDITOR
using System.Runtime.CompilerServices;
using UnityEditor;
using UnityEngine;

namespace com.ImmersizeFramework.Loader.Editor {
    
    [CustomPropertyDrawer(typeof(SceneDropdownAttribute))]
    internal sealed class SceneDropdownPropertyDrawer : PropertyDrawer {
        
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label) {
            if (property.propertyType != SerializedPropertyType.String) {
                EditorGUI.LabelField(position, label.text, "Use SceneDropdown with string fields only.");
                return;
            }

            var sceneAttribute = (SceneDropdownAttribute)attribute;
            var manifest = SceneRegistry.GetManifest(sceneAttribute);
            
            EditorGUI.BeginProperty(position, label, property);

            
            var currentIndex = manifest.FindIndex(property.stringValue);
            if (currentIndex < 0) currentIndex = 0;
            
            var newIndex = EditorGUI.Popup(position, label.text, currentIndex, manifest.DisplayNames);
            
            if (newIndex != currentIndex && newIndex >= 0 && newIndex < manifest.Count) {
                var selectedEntry = manifest[newIndex];
                property.stringValue = selectedEntry.Source == SceneSource.None ? string.Empty : selectedEntry.Name;
            }
            
            EditorGUI.EndProperty();
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override float GetPropertyHeight(SerializedProperty property, GUIContent label) =>
            EditorGUIUtility.singleLineHeight;
    }
}
#endif
