using System;
using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Interest management for one spatial component type: register observers over a region, then ask each what changed since it last looked.
/// </summary>
/// <remarks>
/// <para>Obtained from <see cref="SpatialObserverExtensions.SpatialObservers{T}"/>. The handle is a thin façade over per-component state the engine owns, so
/// it is cheap to obtain and holds nothing that needs disposing; observers themselves live until unregistered.</para>
/// <para><b>Delta, with a full-sync fallback.</b> <see cref="GetSpatialChanges"/> walks the entities that actually moved since the observer's last
/// consumption tick and tests each against its region — work proportional to CHANGE, not to region population. An observer that falls further behind than the
/// dirty ring is long gets a full sync instead, flagged by <see cref="SpatialChangeResult.IsFullSync"/>, because the ring can no longer say what it missed.
/// </para>
/// <para><b>Why this is public.</b> Before #872 step 13 the interest and trigger systems had no production entry point at all: they were reachable only from
/// tests and benchmarks, and read an entity-level R-Tree that had had no writer since #666 — so the half that was reachable was querying an empty index.
/// Step 13 removed that tree, moved both systems onto the per-cell cluster index, and had to resolve which of the two outcomes the design allowed: port them,
/// or delete them together with their <c>rules/spatial.md</c> modules. They are ported, and this surface is what makes that a fact a test can check rather
/// than a claim.</para>
/// </remarks>
[PublicAPI]
public readonly struct SpatialObserverSet
{
    private readonly SpatialInterestSystem _system;

    internal SpatialObserverSet(SpatialInterestSystem system) => _system = system;

    /// <summary>
    /// <c>false</c> for a <c>default</c>-constructed value, which every other member rejects.
    /// </summary>
    /// <remarks>
    /// A public <c>struct</c> can always be default-constructed, and this one is a façade over engine state it cannot invent. Without the check every member
    /// would throw <see cref="NullReferenceException"/> — the one exception that tells a caller nothing about what they did wrong.
    /// </remarks>
    public bool IsValid => _system != null;

    /// <summary>How many observers are currently registered.</summary>
    public int ActiveObserverCount => Checked().ActiveObserverCount;

    private SpatialInterestSystem Checked() => _system
        ?? throw new InvalidOperationException("This SpatialObserverSet was default-constructed. Obtain one from DatabaseEngine.SpatialObservers<T>().");

    /// <summary>
    /// Register an observer watching <paramref name="bounds"/>.
    /// </summary>
    /// <param name="bounds">
    /// <c>[minX, minY, maxX, maxY]</c> for a 2D component, <c>[minX, minY, minZ, maxX, maxY, maxZ]</c> for a 3D one.
    /// </param>
    /// <param name="categoryMask">Category bits the observer cares about; <c>0</c> means "no filter".</param>
    /// <param name="initialTick">The tick the observer is considered to have already consumed. Pass the current tick to start from "nothing new".</param>
    public SpatialObserverHandle RegisterObserver(ReadOnlySpan<double> bounds, uint categoryMask = 0, long initialTick = 0)
        => Checked().RegisterObserver(bounds, categoryMask, initialTick);

    /// <summary>Release an observer and its buffers. The handle is invalid afterwards.</summary>
    public void UnregisterObserver(SpatialObserverHandle handle) => Checked().UnregisterObserver(handle);

    /// <summary>Move or resize an observer's region. Its consumption tick is unaffected.</summary>
    public void UpdateObserverBounds(SpatialObserverHandle handle, ReadOnlySpan<double> newBounds) => Checked().UpdateObserverBounds(handle, newBounds);

    /// <summary>
    /// Entities whose spatial position changed inside the observer's region since it last consumed.
    /// </summary>
    /// <remarks>
    /// The returned spans point into the observer's own buffers and are valid only until this observer's next call.
    /// </remarks>
    public SpatialChangeResult GetSpatialChanges(SpatialObserverHandle handle, long currentTick) => Checked().GetSpatialChanges(handle, currentTick);
}

/// <summary>
/// Trigger volumes for one spatial component type: define regions, then ask each which entities entered, left, or stayed since the last evaluation.
/// </summary>
/// <remarks>
/// <para>Obtained from <see cref="SpatialObserverExtensions.SpatialTriggers{T}"/>. See <see cref="SpatialObserverSet"/> for why both surfaces became public in
/// #872 step 13.</para>
/// <para><b>Evaluation is a set diff, not a bitmap XOR.</b> Occupancy is tracked by entity id against the per-cell cluster index; the component-chunk-id
/// bitmap the old entity-level path used could not represent cluster storage, whose chunk ids live in a different namespace.</para>
/// </remarks>
[PublicAPI]
public readonly struct SpatialTriggerVolumes
{
    private readonly SpatialTriggerSystem _system;

    internal SpatialTriggerVolumes(SpatialTriggerSystem system) => _system = system;

    /// <inheritdoc cref="SpatialObserverSet.IsValid"/>
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
    public SpatialRegionHandle CreateRegion(ReadOnlySpan<double> bounds, uint categoryMask = 0, byte evaluationFrequency = 1)
        => Checked().CreateRegion(bounds, categoryMask, evaluationFrequency);

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

/// <summary>Entry points for the two systems layered on the spatial index.</summary>
[PublicAPI]
public static class SpatialObserverExtensions
{
    /// <summary>
    /// Interest management for component <typeparamref name="T"/>, which must carry a <c>[SpatialIndex]</c> field.
    /// </summary>
    /// <remarks>
    /// The underlying state is created on first use and lives as long as the component's <c>ComponentTable</c> does — which is the engine's lifetime in
    /// ordinary use, but NOT across a schema migration that reconstructs the table. Obtain the façade again after one rather than holding it across.
    /// </remarks>
    public static SpatialObserverSet SpatialObservers<T>(this DatabaseEngine engine) where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(engine);
        var table = engine.GetComponentTable<T>();
        if (table?.SpatialIndex == null)
        {
            throw new InvalidOperationException($"Component {typeof(T).Name} has no [SpatialIndex] field, so it has no interest management.");
        }

        return new SpatialObserverSet(table.SpatialIndex.GetOrCreateInterestSystem(table));
    }

    /// <summary>
    /// Trigger volumes for component <typeparamref name="T"/>, which must carry a <c>[SpatialIndex]</c> field.
    /// </summary>
    /// <inheritdoc cref="SpatialObservers{T}" path="/remarks"/>
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
