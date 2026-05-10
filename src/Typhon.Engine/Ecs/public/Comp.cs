using System.Runtime.CompilerServices;
using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Typed component handle — identifies a component type within the ECS system.
/// Stored as <c>static readonly</c> fields on archetype classes to declare schema.
/// </summary>
/// <remarks>
/// <para>ComponentTypeId: global type registry ID (shared when the same component type appears in multiple archetypes).</para>
/// <para>Slot resolution happens at runtime via <see cref="ArchetypeMetadata.GetSlot"/> — avoids static initialization ordering issues with inheritance.</para>
/// </remarks>
/// <typeparam name="T">The unmanaged component data type.</typeparam>
[PublicAPI]
public readonly struct Comp<T> where T : unmanaged
{
    internal readonly int _componentTypeId;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Comp(int componentTypeId) => _componentTypeId = componentTypeId;

    /// <summary>Create a <see cref="ComponentValue"/> with the given data for use during entity spawning.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ComponentValue Set(in T value) => ComponentValue.Create(_componentTypeId, in value);

    /// <summary>Create a zero-initialized <see cref="ComponentValue"/> for use during entity spawning.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ComponentValue Default() => ComponentValue.Create<T>(_componentTypeId, default);
}
