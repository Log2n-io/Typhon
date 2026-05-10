using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Internals;

/// <summary>
/// Pending component declaration accumulated before archetype finalization.
/// </summary>
internal class PendingArchetype
{
    public Type ArchetypeType;
    public readonly List<(Type ComponentType, int ComponentTypeId)> Components = [];
}

/// <summary>
/// Global archetype registration manager. Static class — registration at startup (serialized by CLR static constructors), lock-free reads at
/// runtime (array indexed by ArchetypeId).
/// </summary>
internal static class ArchetypeRegistry
{
    // ═══════════════════════════════════════════════════════════════════════
    // Static state
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>CLR Type → globally unique ComponentTypeId (deduplication across archetypes).</summary>
    private static readonly Dictionary<Type, int> ComponentTypeIds = new();

    /// <summary>Component schema name → ComponentTypeId (dedup across V1/V2 CLR types sharing the same schema name).</summary>
    private static readonly Dictionary<string, int> ComponentTypeIdsBySchemaName = new();

    /// <summary>ComponentTypeId → CLR Type (reverse lookup for slot-to-type mapping).</summary>
    private static readonly Dictionary<int, Type> ComponentTypeById = new();

    private static int NextComponentTypeId;

    /// <summary>Indexed by ArchetypeId. Max 4096 slots (12-bit ArchetypeId).</summary>
    private static readonly ArchetypeMetadata[] Archetypes = new ArchetypeMetadata[4096];

    private static int RegisteredCount;
    private static int MaxRegisteredArchetypeId;

    /// <summary>Highest ArchetypeId registered so far. Used to size ArchetypeMaskLarge.</summary>
    internal static int MaxArchetypeId => MaxRegisteredArchetypeId;

    private static bool Frozen;

    /// <summary>Accumulates DeclareComponent calls before FinalizeArchetype.</summary>
    private static readonly Dictionary<Type, PendingArchetype> PendingRegistrations = new();

    /// <summary>CLR Type → ArchetypeMetadata for generic lookup cache.</summary>
    private static readonly Dictionary<Type, ArchetypeMetadata> MetadataByType = new();

    // ═══════════════════════════════════════════════════════════════════════
    // Registration API (called during static initialization)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Declare a component for an archetype. Called from <see cref="Archetype{TSelf}.Register{T}"/>.
    /// Assigns or reuses a ComponentTypeId and records the pending slot.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Comp<T> DeclareComponent<TArchetype, T>() where T : unmanaged
    {
        // Allow late registration — static constructors may fire after Freeze() if new archetype types are accessed for the first time (e.g., in test
        // fixtures loaded after engine init). Freeze() will need to be called again to rebuild subtree IDs.
        if (Frozen)
        {
            Frozen = false; // Unfreeze to allow registration, Freeze() will be called again
        }

        var archetypeType = typeof(TArchetype);

        // Get or create ComponentTypeId (dedup by schema name — V1/V2 CLR types sharing the same [Component("SchemaName")] get the same ID so archetype
        // slot mappings work across schema evolution)
        if (!ComponentTypeIds.TryGetValue(typeof(T), out int componentTypeId))
        {
            var schemaName = typeof(T).GetCustomAttribute<ComponentAttribute>()?.Name;
            if (schemaName != null && ComponentTypeIdsBySchemaName.TryGetValue(schemaName, out componentTypeId))
            {
                // Same schema name as another CLR type (schema evolution) — reuse the ID
                ComponentTypeIds[typeof(T)] = componentTypeId;
                ComponentTypeById[componentTypeId] = typeof(T); // update reverse mapping to latest type
            }
            else
            {
                componentTypeId = NextComponentTypeId++;
                ComponentTypeIds[typeof(T)] = componentTypeId;
                ComponentTypeById[componentTypeId] = typeof(T);
                if (schemaName != null)
                {
                    ComponentTypeIdsBySchemaName[schemaName] = componentTypeId;
                }
            }
        }

        // Get or create pending registration for this archetype
        if (!PendingRegistrations.TryGetValue(archetypeType, out var pending))
        {
            pending = new PendingArchetype { ArchetypeType = archetypeType };
            PendingRegistrations[archetypeType] = pending;
        }

        // Record the component
        pending.Components.Add((typeof(T), componentTypeId));

        return new Comp<T>(componentTypeId);
    }

    /// <summary>
    /// Ensure an archetype type is finalized. Triggers static initialization (field initializers) then calls FinalizeArchetypeInternal if not already done.
    /// </summary>
    internal static void EnsureFinalized(Type archetypeType)
    {
        // First ensure static field initializers have run (DeclareComponent calls)
        RuntimeHelpers.RunClassConstructor(archetypeType.TypeHandle);

        var attr = archetypeType.GetCustomAttribute<ArchetypeAttribute>();
        if (attr == null)
        {
            return;
        }

        // Already finalized?
        if (Archetypes[attr.Id] != null)
        {
            return;
        }

        FinalizeArchetypeInternal(archetypeType);
    }

    private static void FinalizeArchetypeInternal(Type archetypeType)
    {
        // Read [Archetype(Id = N)] attribute
        var attr = archetypeType.GetCustomAttribute<ArchetypeAttribute>();
        if (attr == null)
        {
            throw new InvalidOperationException($"Archetype type {archetypeType.Name} is missing [Archetype(Id = N)] attribute");
        }

        var archetypeId = attr.Id;
        if (archetypeId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(archetypeId), $"ArchetypeId 0 is reserved (default(ushort) ambiguity). Use IDs 1-4095.");
        }
        if (archetypeId > 4095)
        {
            throw new ArgumentOutOfRangeException(nameof(archetypeId), $"ArchetypeId {archetypeId} exceeds max 4095 (12-bit)");
        }

        // Already finalized (e.g., re-entrant via parent chain)
        if (Archetypes[archetypeId] != null)
        {
            // Duplicate check: different type registering same Id
            if (Archetypes[archetypeId].ArchetypeType != archetypeType)
            {
                throw new InvalidOperationException(
                    $"Duplicate ArchetypeId {archetypeId}: already registered by {Archetypes[archetypeId].ArchetypeType.Name}, " +
                    $"cannot register {archetypeType.Name}");
            }
            return; // already finalized
        }

        // Get pending registration (may be empty if archetype has no own components)
        PendingRegistrations.TryGetValue(archetypeType, out var pending);
        var ownComponents = pending?.Components ?? [];

        // Walk parent chain to collect inherited components (parent-first ordering)
        var allComponents = new List<(Type ComponentType, int ComponentTypeId)>();
        ushort parentArchetypeId = ArchetypeMetadata.NoParent;

        var parentType = FindParentArchetypeType(archetypeType);
        if (parentType != null)
        {
            // Ensure parent is finalized first (recursive — handles multi-level chains)
            EnsureFinalized(parentType);

            var parentAttr = parentType.GetCustomAttribute<ArchetypeAttribute>();
            if (parentAttr == null)
            {
                throw new InvalidOperationException($"Parent type {parentType.Name} is missing [Archetype] attribute");
            }

            parentArchetypeId = parentAttr.Id;
            var parentMeta = Archetypes[parentAttr.Id];

            // Copy parent's component slots (inherited, parent-first ordering preserved)
            for (int i = 0; i < parentMeta.ComponentCount; i++)
            {
                allComponents.Add((null, parentMeta._componentTypeIds[i])); // Type not needed, just the ID
            }

            // Register as child of parent
            parentMeta.ChildArchetypeIds.Add(archetypeId);
        }

        // Append own components
        allComponents.AddRange(ownComponents);

        byte totalComponentCount = (byte)allComponents.Count;
        if (totalComponentCount > 16)
        {
            throw new InvalidOperationException($"Archetype {archetypeType.Name} has {totalComponentCount} components (max 16). " +
                                                $"Inherited: {allComponents.Count - ownComponents.Count}, Own: {ownComponents.Count}");
        }

        // Build slot mapping — flat array indexed by componentTypeId (0xFF = not present)
        var componentTypeIds = new int[totalComponentCount];
        int maxTypeId = 0;
        for (int i = 0; i < totalComponentCount; i++)
        {
            int typeId = allComponents[i].ComponentTypeId;
            componentTypeIds[i] = typeId;
            if (typeId > maxTypeId)
            {
                maxTypeId = typeId;
            }
        }

        var typeIdToSlot = new byte[maxTypeId + 1];
        Array.Fill(typeIdToSlot, (byte)0xFF);
        for (int i = 0; i < totalComponentCount; i++)
        {
            typeIdToSlot[componentTypeIds[i]] = (byte)i;
        }

        // Build slot-to-type array for DatabaseEngine initialization
        var slotToComponentType = new Type[totalComponentCount];
        for (int i = 0; i < totalComponentCount; i++)
        {
            slotToComponentType[i] = ComponentTypeById[componentTypeIds[i]];
        }

        // Create and store metadata
        var metadata = new ArchetypeMetadata
        {
            ArchetypeId = archetypeId,
            Revision = attr.Revision,
            ComponentCount = totalComponentCount,
            ParentArchetypeId = parentArchetypeId,
            ArchetypeType = archetypeType,
            _componentTypeIds = componentTypeIds,
            _typeIdToSlot = typeIdToSlot,
            _slotToComponentType = slotToComponentType,
            _entityRecordSize = EntityRecordAccessor.RecordSize(totalComponentCount),
        };

        Archetypes[archetypeId] = metadata;
        MetadataByType[archetypeType] = metadata;
        RegisteredCount++;
        if (archetypeId > MaxRegisteredArchetypeId)
        {
            MaxRegisteredArchetypeId = archetypeId;
        }

    }

    /// <summary>
    /// Rebuild every archetype's slot→Type cache from the current <c>ComponentTypeById</c> map. Call this after loading a schema DLL into a fresh
    /// AssemblyLoadContext on top of a registry that was already populated by a prior ALC:
    /// <c>EnsureFinalized</c> short-circuits on already-registered archetype ids and leaves <c>_slotToComponentType</c> pointing at the first
    /// ALC's <see cref="Type"/> instances. <c>DeclareComponent</c> already refreshes <c>ComponentTypeById</c> via the schema-name dedup path, so the up-to-date
    /// Type is available — this method just propagates that into every affected archetype. Only needed by the Workbench (per-session collectible ALCs);
    /// production hosts with a single ALC can ignore it.
    ///
    /// If a slot's component type is no longer registered in the current registry (e.g., the new schema DLL dropped that component), the slot is set
    /// to <c>null</c> rather than leaving the stale ALC Type behind — downstream code like <see cref="DatabaseEngine.InitializeArchetypes"/> already
    /// null-checks each slot and falls back to schema-name matching, so a null is a fail-fast signal rather than a silent wrong answer.
    /// </summary>
    public static void RefreshSlotTypes()
    {
        for (int i = 0; i < Archetypes.Length; i++)
        {
            var meta = Archetypes[i];
            if (meta == null || meta._slotToComponentType == null)
            {
                continue;
            }
            var ids = meta._componentTypeIds;
            var slots = meta._slotToComponentType;
            for (int s = 0; s < slots.Length; s++)
            {
                slots[s] = ComponentTypeById.GetValueOrDefault(ids[s]);
            }
        }
    }

    /// <summary>
    /// Walk the base type chain to find the direct parent archetype type.
    /// Returns null if this is a root archetype (inherits directly from Archetype&lt;TSelf&gt;).
    /// </summary>
    private static Type FindParentArchetypeType(Type archetypeType)
    {
        var baseType = archetypeType.BaseType;
        if (baseType == null || !baseType.IsGenericType)
        {
            return null;
        }

        var genDef = baseType.GetGenericTypeDefinition();

        // Archetype<TSelf, TParent> — the TParent is the parent archetype
        if (genDef.GetGenericArguments().Length == 2)
        {
            return baseType.GetGenericArguments()[1];
        }

        // Archetype<TSelf> — root archetype, no parent
        return null;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Query API (lock-free reads at runtime)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Get metadata by ArchetypeId. O(1) array index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ArchetypeMetadata GetMetadata(ushort archetypeId) => Archetypes[archetypeId];

    /// <summary>Get metadata by archetype CLR type.</summary>
    internal static ArchetypeMetadata GetMetadata<TArchetype>()
    {
        MetadataByType.TryGetValue(typeof(TArchetype), out var meta);
        return meta;
    }

    /// <summary>Number of registered archetypes.</summary>
    public static int Count => RegisteredCount;

    /// <summary>Whether the registry is frozen (no more registrations allowed).</summary>
    public static bool IsFrozen => Frozen;

    /// <summary>Enumerate all registered archetype metadata (non-null entries). The Workbench Schema Inspector accesses this via <c>InternalsVisibleTo</c> —
    /// promoting to public would cascade to <see cref="ArchetypeMetadata"/> and its 50+ fields. The registry freezes after bootstrap, so the result is stable
    /// once the engine is initialized.</summary>
    internal static IEnumerable<ArchetypeMetadata> GetAllArchetypes()
    {
        for (int i = 0; i < Archetypes.Length; i++)
        {
            if (Archetypes[i] != null)
            {
                yield return Archetypes[i];
            }
        }
    }

    /// <summary>
    /// Build an ArchetypeMask256 with bits set for all archetypes that declare a component with the given type ID.
    /// O(K) where K = registered archetypes. Called at query construction time, not in hot path.
    /// </summary>
    internal static ArchetypeMask256 GetComponentMask(int componentTypeId)
    {
        var mask = new ArchetypeMask256();
        for (int i = 0; i < Archetypes.Length; i++)
        {
            var meta = Archetypes[i];
            if (meta != null && meta.HasComponent(componentTypeId))
            {
                mask.Set(meta.ArchetypeId);
            }
        }
        return mask;
    }

    /// <summary>
    /// Build an ArchetypeMaskLarge with bits set for all archetypes that declare a component with the given type ID.
    /// <summary>
    /// Find the first archetype that contains a component with the given type ID.
    /// Used by Shell CLI for dynamic archetype discovery when creating entities by component name.
    /// Returns null if no archetype contains this component type.
    /// </summary>
    internal static ArchetypeMetadata FindArchetypeForComponent(int componentTypeId)
    {
        for (int i = 0; i <= MaxRegisteredArchetypeId; i++)
        {
            var meta = Archetypes[i];
            if (meta != null && meta.HasComponent(componentTypeId))
            {
                return meta;
            }
        }
        return null;
    }

    /// Used when <see cref="MaxArchetypeId"/> > 255.
    /// </summary>
    internal static ArchetypeMaskLarge GetComponentMaskLarge(int componentTypeId)
    {
        var mask = new ArchetypeMaskLarge(MaxRegisteredArchetypeId);
        for (int i = 0; i < Archetypes.Length; i++)
        {
            var meta = Archetypes[i];
            if (meta != null && meta.HasComponent(componentTypeId))
            {
                mask.Set(meta.ArchetypeId);
            }
        }
        return mask;
    }

    /// <summary>True if all registered archetypes have IDs ≤ 255 (ArchetypeMask256 can be used).</summary>
    internal static bool UseSmallMask => MaxRegisteredArchetypeId < 256;

    /// <summary>
    /// Get the global ComponentTypeId for a CLR component type. Returns -1 if not registered.
    /// </summary>
    public static int GetComponentTypeId<T>() where T : unmanaged => ComponentTypeIds.GetValueOrDefault(typeof(T), -1);

    /// <summary>
    /// Get the global ComponentTypeId for a CLR component type. Returns -1 if not registered.
    /// </summary>
    public static int GetComponentTypeId(Type type) => ComponentTypeIds.GetValueOrDefault(type, -1);

    // ═══════════════════════════════════════════════════════════════════════
    // Lifecycle
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Freeze the registry: build SubtreeArchetypeIds for each archetype, prevent further registration.
    /// Called by DatabaseEngine before the first transaction.
    /// </summary>
    public static void Freeze()
    {
        if (Frozen)
        {
            return;
        }

        // Build SubtreeArchetypeIds for each registered archetype
        for (int i = 0; i < Archetypes.Length; i++)
        {
            var meta = Archetypes[i];
            if (meta == null)
            {
                continue;
            }

            var subtree = new List<ushort>();
            CollectSubtree(meta.ArchetypeId, subtree);
            meta.SubtreeArchetypeIds = subtree.ToArray();
        }

        Frozen = true;
    }

    /// <summary>
    /// Build and validate the cascade delete graph. Must be called after Freeze() and after all archetypes have their SlotToComponentType populated.
    /// Safe to call multiple times (rebuilds each time).
    /// </summary>
    public static void BuildAndValidateCascadeGraph()
    {
        // Clear any previous cascade targets (needed for test isolation)
        foreach (var meta in GetAllArchetypes())
        {
            meta._cascadeTargets = null;
        }

        BuildCascadeGraph();
        ValidateCascadeGraph();
    }

    /// <summary>
    /// Scan all registered archetypes' component fields for [Index(OnParentDelete = Delete)] on EntityLink fields.
    /// Build CascadeTargets on parent archetypes.
    /// </summary>
    private static void BuildCascadeGraph()
    {
        foreach (var meta in GetAllArchetypes())
        {
            if (meta._slotToComponentType == null)
            {
                continue;
            }

            for (byte slot = 0; slot < meta.ComponentCount; slot++)
            {
                var compType = meta._slotToComponentType[slot];
                if (compType == null)
                {
                    continue;
                }

                // Scan fields for EntityLink<T> with [Index(OnParentDelete = Delete)]
                foreach (var field in compType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    var indexAttr = field.GetCustomAttribute<IndexAttribute>();
                    if (indexAttr == null || indexAttr.OnParentDelete == CascadeAction.None)
                    {
                        continue;
                    }

                    // Check that field type is EntityLink<T>
                    if (!field.FieldType.IsGenericType || field.FieldType.GetGenericTypeDefinition() != typeof(EntityLink<>))
                    {
                        continue;
                    }

                    // Extract target archetype type from EntityLink<T>
                    var targetArchetypeType = field.FieldType.GetGenericArguments()[0];
                    var targetAttr = targetArchetypeType.GetCustomAttribute<ArchetypeAttribute>();
                    if (targetAttr == null)
                    {
                        continue;
                    }

                    // Register cascade target on the PARENT archetype
                    var parentMeta = Archetypes[targetAttr.Id];
                    if (parentMeta == null)
                    {
                        continue;
                    }

                    parentMeta._cascadeTargets ??= [];
                    parentMeta._cascadeTargets.Add(new CascadeTarget
                    {
                        ChildArchetypeId = meta.ArchetypeId,
                        ChildArchetypeType = meta.ArchetypeType,
                        FkSlotIndex = slot,
                        FkFieldOffset = (int)Marshal.OffsetOf(compType, field.Name),
                    });
                }
            }
        }
    }

    /// <summary>
    /// Validate the cascade graph: no cycles, no diamonds. DFS from each root.
    /// </summary>
    private static void ValidateCascadeGraph()
    {
        // Collect all archetypes that have cascade targets (potential roots)
        var visited = new HashSet<ushort>();
        var inStack = new HashSet<ushort>();

        foreach (var meta in GetAllArchetypes())
        {
            if (meta._cascadeTargets == null || meta._cascadeTargets.Count == 0)
            {
                continue;
            }

            visited.Clear();
            inStack.Clear();
            ValidateCascadeDfs(meta.ArchetypeId, visited, inStack);
        }
    }

    private static void ValidateCascadeDfs(ushort archetypeId, HashSet<ushort> visited, HashSet<ushort> inStack)
    {
        if (inStack.Contains(archetypeId))
        {
            var meta = Archetypes[archetypeId];
            throw new InvalidOperationException($"Cascade delete cycle detected involving archetype '{meta?.ArchetypeType.Name}' (Id={archetypeId}). " +
                                                $"Cycles in cascade graphs are forbidden.");
        }

        if (!visited.Add(archetypeId))
        {
            // Already visited from a different path — diamond detected
            var meta = Archetypes[archetypeId];
            throw new InvalidOperationException($"Cascade delete diamond detected: archetype '{meta?.ArchetypeType.Name}' (Id={archetypeId}) " +
                                                $"is reachable via multiple cascade paths. Diamond cascade graphs are forbidden.");
        }

        var archMeta = Archetypes[archetypeId];
        if (archMeta?._cascadeTargets == null)
        {
            return;
        }

        inStack.Add(archetypeId);
        foreach (var target in archMeta._cascadeTargets)
        {
            ValidateCascadeDfs(target.ChildArchetypeId, visited, inStack);
        }
        inStack.Remove(archetypeId);
    }

    private static void CollectSubtree(ushort archetypeId, List<ushort> result)
    {
        result.Add(archetypeId);
        var meta = Archetypes[archetypeId];
        if (meta?.ChildArchetypeIds == null)
        {
            return;
        }

        foreach (var childId in meta.ChildArchetypeIds)
        {
            CollectSubtree(childId, result);
        }
    }

}
