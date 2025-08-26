using System;
using UnityEngine;

[AttributeUsage(AttributeTargets.Field)]
public sealed class SceneDropdownAttribute : PropertyAttribute {
    readonly string filter;
    
    public SceneDropdownAttribute(string filter = null) => this.filter = filter;
    
    public bool HasFilter => !string.IsNullOrEmpty(filter);
    public string Filter => filter ?? string.Empty;
}
