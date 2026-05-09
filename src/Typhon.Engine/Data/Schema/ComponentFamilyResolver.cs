using System;
using System.Reflection;
using JetBrains.Annotations;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Data.Schema;

/// <summary>
/// Resolves the "component family" classification used by the Workbench Data Flow module (L2 granularity).
/// Resolution order: explicit <see cref="ComponentFamilyAttribute"/> wins; otherwise a name-based heuristic
/// classifies the component into one of the well-known families. Unmatched names fall back to <c>Misc</c>.
/// </summary>
/// <remarks>
/// Pure helper — no allocations, no caching. Callers (live or trace projection) invoke it at session attach time
/// and store the resulting map in <c>ComponentFamilyMapDto</c> for the lifetime of the session.
/// </remarks>
[PublicAPI]
public static class ComponentFamilyResolver
{
    public const string Misc = "Misc";

    /// <summary>
    /// Canonical family order surfaced to the Workbench. The Data Flow Timeline / Access Matrix renders rows
    /// in this order so users get a stable visual layout across sessions.
    /// </summary>
    public static readonly string[] CanonicalFamilyOrder =
    [
        "Spatial",
        "Combat",
        "AI",
        "Inventory",
        "Rendering",
        "Networking",
        "Input",
        Misc,
    ];

    /// <summary>
    /// Returns the family declared by an explicit <see cref="ComponentFamilyAttribute"/>, or null if absent.
    /// Available only for live-session reflection; trace sessions cannot read attributes.
    /// </summary>
    public static string ResolveByAttribute(Type componentType)
    {
        ArgumentNullException.ThrowIfNull(componentType);
        var attr = componentType.GetCustomAttribute<ComponentFamilyAttribute>(inherit: false);
        return attr?.Name;
    }

    /// <summary>
    /// Classifies a component by its name when no <see cref="ComponentFamilyAttribute"/> is available.
    /// Always returns a non-null family — unmatched names map to <see cref="Misc"/>.
    /// </summary>
    public static string ResolveByHeuristic(string componentName)
    {
        if (string.IsNullOrEmpty(componentName))
        {
            return Misc;
        }

        if (NameContainsAny(componentName, "Position", "Velocity", "Bounds", "Rotation", "Transform", "Scale", "Pose"))
        {
            return "Spatial";
        }

        if (NameContainsAny(componentName, "Health", "Damage", "Armor", "Shield", "Hp", "Hit"))
        {
            return "Combat";
        }

        if (NameContainsAny(componentName, "Behaviour", "Behavior", "Target", "Pathfind", "NavMesh", "Decision"))
        {
            return "AI";
        }

        if (NameContainsAny(componentName, "Inventory", "Equipment", "Equipped", "Ammo", "Item"))
        {
            return "Inventory";
        }

        if (NameContainsAny(componentName, "Sprite", "Animation", "Mesh", "Material", "Tint", "Render"))
        {
            return "Rendering";
        }

        if (NameContainsAny(componentName, "Network", "Replication", "Sync", "Snapshot"))
        {
            return "Networking";
        }

        if (NameContainsAny(componentName, "Input", "Command", "Action"))
        {
            return "Input";
        }

        return Misc;
    }

    /// <summary>
    /// Composite resolver — preferred entry point for live sessions. Tries the attribute first, falls back to the heuristic.
    /// </summary>
    public static string Resolve(Type componentType)
    {
        ArgumentNullException.ThrowIfNull(componentType);
        return ResolveByAttribute(componentType) ?? ResolveByHeuristic(componentType.Name);
    }

    private static bool NameContainsAny(string name, params string[] needles)
    {
        for (var i = 0; i < needles.Length; i++)
        {
            if (name.Contains(needles[i], StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }
}
