using System;
using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Trigger volumes for one spatial component type: define regions, then ask each which entities entered, left, or stayed since the last evaluation.
/// </summary>
/// <remarks>
/// <para>Obtained from <see cref="SpatialObserverExtensions.SpatialTriggers{T}"/>. Public since #872 step 13, which moved the system onto the per-cell
/// cluster index: a subsystem reachable only from tests could not be told apart from one that had stopped working. Its sibling, the observer set, was
/// superseded by engine-owned subscriptions and deleted (design/Subscriptions/09 § 15, F4).</para>
/// <para><b>Evaluation is a set diff, not a bitmap XOR.</b> Occupancy is tracked by entity id against the per-cell cluster index; the component-chunk-id
/// bitmap the old entity-level path used could not represent cluster storage, whose chunk ids live in a different namespace.</para>
/// </remarks>
[PublicAPI]
public readonly struct SpatialTriggerVolumes
{
    private readonly SpatialTriggerSystem _system;

    internal SpatialTriggerVolumes(SpatialTriggerSystem system) => _system = system;

    /// <summary><c>false</c> for a <c>default</c>-constructed value, which every other member rejects.</summary>
    /// <remarks>
    /// A public <c>struct</c> can always be default-constructed, and this one is a façade over engine state it cannot invent. Without the check every member
    /// would throw <see cref="NullReferenceException"/> — the one exception that tells a caller nothing about what they did wrong.
    /// </remarks>
    public bool IsValid => _system != null;

    /// <summary>How many regions are currently defined.</summary>
    public int ActiveRegionCount => Checked().ActiveRegionCount;

    private SpatialTriggerSystem Checked() => _system
        ?? throw new InvalidOperationException("This SpatialTriggerVolumes was default-constructed. Obtain one from DatabaseEngine.SpatialTriggers<T>().");

    /// <summary>
    /// Define a trigger region over <paramref name="bounds"/>.
    /// </summary>
    /// <param name="bounds">
    /// <c>[minX, minY, maxX, maxY]</c> for a 2D component, <c>[minX, minY, minZ, maxX, maxY, maxZ]</c> for a 3D one.
    /// </param>
    /// <param name="categoryMask">Category bits the region reacts to; <c>0</c> means "no filter".</param>
    /// <param name="evaluationFrequency">Minimum ticks between real evaluations; calls in between return <see cref="SpatialTriggerResult.Skipped"/>.</param>
    /// <param name="realm">The realm the region is in: it reports only that realm's entities. Realm 0 by default; must be registered.</param>
    /// <exception cref="InvalidOperationException"><paramref name="realm"/> is not registered.</exception>
    public SpatialRegionHandle CreateRegion(ReadOnlySpan<double> bounds, uint categoryMask = 0, byte evaluationFrequency = 1, RealmId realm = default)
        => Checked().CreateRegion(bounds, categoryMask, evaluationFrequency, realm);

    /// <summary>Remove a region. The handle is invalid afterwards.</summary>
    public void DestroyRegion(SpatialRegionHandle handle) => Checked().DestroyRegion(handle);

    /// <summary>Move or resize a region. Its occupant set is kept, so the next evaluation reports the difference as enters and leaves.</summary>
    public void UpdateRegionBounds(SpatialRegionHandle handle, ReadOnlySpan<double> newBounds) => Checked().UpdateRegionBounds(handle, newBounds);

    /// <summary>Change which categories a region reacts to.</summary>
    public void UpdateRegionCategoryMask(SpatialRegionHandle handle, uint newMask) => Checked().UpdateRegionCategoryMask(handle, newMask);

    /// <summary>
    /// Which entities entered, left, or stayed in the region since its previous evaluation.
    /// </summary>
    /// <remarks>
    /// The returned spans are valid only until the next <see cref="EvaluateRegion"/> call on this system — they are shared result buffers, not per-region.
    /// </remarks>
    public SpatialTriggerResult EvaluateRegion(SpatialRegionHandle handle, int currentTick) => Checked().EvaluateRegion(handle, currentTick);
}

/// <summary>Entry point for the trigger volumes layered on the spatial index.</summary>
[PublicAPI]
public static class SpatialObserverExtensions
{
    /// <summary>
    /// Trigger volumes for component <typeparamref name="T"/>, which must carry a <c>[SpatialIndex]</c> field.
    /// </summary>
    /// <remarks>
    /// The underlying state is created on first use and lives as long as the component's <c>ComponentTable</c> does — which is the engine's lifetime in
    /// ordinary use, but NOT across a schema migration that reconstructs the table. Obtain the façade again after one rather than holding it across.
    /// </remarks>
    public static SpatialTriggerVolumes SpatialTriggers<T>(this DatabaseEngine engine) where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(engine);
        var table = engine.GetComponentTable<T>();
        if (table?.SpatialIndex == null)
        {
            throw new InvalidOperationException($"Component {typeof(T).Name} has no [SpatialIndex] field, so it has no trigger volumes.");
        }

        return new SpatialTriggerVolumes(table.SpatialIndex.GetOrCreateTriggerSystem(table));
    }
}
