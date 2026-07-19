using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Threading;
using Typhon.Schema.Definition;
// `PendingArchetype` lives in this file; no extra using needed.

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

    // These four maps are WRITTEN only under RegistrationLock but READ lock-free on hot-ish paths (GetComponentTypeId during ComponentInfo creation,
    // GetMetadata<T> during query build). A plain Dictionary tears under concurrent read+write — parallel test fixtures / Workbench multi-ALC engines
    // register on one thread while another reads, which surfaced (via Phase-2's GetSlot validation) as a rare flaky "component id 65535 not in archetype".
    // ConcurrentDictionary makes the lock-free reads safe; the compound read-modify-write sequences stay serialized under RegistrationLock. (#514 Phase 5.)

    /// <summary>CLR Type → globally unique ComponentTypeId (deduplication across archetypes).</summary>
    private static readonly ConcurrentDictionary<Type, int> ComponentTypeIds = new();

    /// <summary>Component schema name → ComponentTypeId (dedup across V1/V2 CLR types sharing the same schema name).</summary>
    private static readonly ConcurrentDictionary<string, int> ComponentTypeIdsBySchemaName = new();

    /// <summary>ComponentTypeId → CLR Type (reverse lookup for slot-to-type mapping).</summary>
    private static readonly ConcurrentDictionary<int, Type> ComponentTypeById = new();

    private static int NextComponentTypeId;

    /// <summary>Indexed by ArchetypeId. Max 4096 slots (12-bit ArchetypeId).</summary>
    private static readonly ArchetypeMetadata[] Archetypes = new ArchetypeMetadata[4096];

    private static int RegisteredCount;
    private static int MaxRegisteredArchetypeId;

    /// <summary>Highest ArchetypeId registered so far. Used to size ArchetypeMaskLarge.</summary>
    internal static int MaxArchetypeId => MaxRegisteredArchetypeId;

    // #514 D1 — the per-process catalog id is engine-ASSIGNED (dense, by archetype identity), not author-set. Identity is the CLR type full name (the class name
    // doubles as the archetype name). ArchetypeIdByName dedups so the same identity — including the same schema re-loaded into a fresh ALC — reuses its catalog id
    // (string keys never pin an ALC). The counter starts at 1: id 0 is reserved (default(ushort) null/sentinel).
    private static ushort NextArchetypeId;
    private static readonly Dictionary<string, ushort> ArchetypeIdByName = new();

    private static bool Frozen;

    /// <summary>Get-or-assign the dense, per-process catalog id for an archetype identity (its CLR type full name). #514 D1.</summary>
    private static ushort GetOrAssignCatalogId(string identity)
    {
        if (ArchetypeIdByName.TryGetValue(identity, out var id))
        {
            return id;
        }
        var next = (ushort)(NextArchetypeId + 1);
        if (next > 4095)
        {
            throw new InvalidOperationException($"Archetype catalog exceeded its 4095-entry capacity while registering '{identity}'.");
        }
        NextArchetypeId = next;
        ArchetypeIdByName[identity] = next;
        return next;
    }

    /// <summary>
    /// Accumulates <see cref="DeclareComponent{TArchetype,T}"/> calls before <see cref="FinalizeArchetypeInternal"/>.
    /// Backed by a <see cref="ConditionalWeakTable{TKey,TValue}"/> so entries hold a WEAK reference to the archetype <see cref="Type"/> — necessary because
    /// the static constructor that populates each entry runs at most ONCE per Type per process. If we used a strong-keyed dictionary we'd face the choice:
    /// clear it on <see cref="UnregisterEngineUse"/> (and lose the data, so the next engine can't re-finalize because the ctor won't re-run) OR keep it
    /// (and pin every Type forever, defeating ALC unload). With <see cref="ConditionalWeakTable{TKey,TValue}"/> the data persists exactly as long as the Type
    /// does — so a re-finalize within the same ALC sees the cached components, and an ALC unload reclaims the Type + the entry together.
    /// </summary>
    private static readonly ConditionalWeakTable<Type, PendingArchetype> PendingRegistrations = new();

    /// <summary>CLR Type → ArchetypeMetadata for generic lookup cache. Read lock-free by <see cref="GetMetadata{TArchetype}"/>; see the ConcurrentDictionary
    /// note above.</summary>
    private static readonly ConcurrentDictionary<Type, ArchetypeMetadata> MetadataByType = new();

    // ─── Lifecycle refcounts ────────────────────────────────────────────────────────────────────────────────
    //
    // Each `DatabaseEngine` increments the refcount for every archetype + component Type it registered when `InitializeArchetypes` runs, and decrements them
    // on `Dispose`. When a refcount reaches zero the registry entry is removed wholesale — releasing the `Type` reference + downstream `ArchetypeMetadata` so
    // the owning `AssemblyLoadContext` can be GC'd. Without this, the registry pins the first ALC's Type instances for the lifetime of the process and any
    // later session that loads the same DLL into a fresh collectible ALC gets a stale view of the registry (cross-ALC type-identity bug).
    //
    // Refcount semantics matter when two engines coexist (e.g. the Workbench's session engine + the Dev Fixture's short-lived engine in the same process):
    // each holds an independent reference; disposing one must not clear entries the other still uses. Strict counting closes that hole.
    private static readonly Dictionary<Type, int> ArchetypeRefcount = new();
    private static readonly Dictionary<Type, int> ComponentRefcount = new();

    // ─── Registration lock (#514 Phase 3) ──────────────────────────────────────────────────────────────────────
    // A single coarse, REENTRANT mutex (System.Threading.Lock) serialising every mutation of the process-global registry: component/archetype registration
    // (DeclareComponent / EnsureFinalized), the build-once Freeze() (subtree + cascade graph), and refcount lifecycle (Register/UnregisterEngineUse). This is
    // what makes concurrent DatabaseEngine.InitializeArchetypes calls safe (parallel test fixtures, Workbench multi-ALC): the shared catalog + cascade graph
    // are built atomically and exactly once, closing the flaky-test race (Face A sizing was removed in Phase 1; Face B — a per-engine cascade rebuild
    // double-adding CascadeTargets — is closed here by building the cascade inside the locked, idempotent Freeze). Reentrancy is required: EnsureFinalized
    // recurses up the parent chain (and RunClassConstructor may re-enter via a static field initializer). Runtime READS stay lock-free — the registry is
    // immutable once frozen. Init/Dispose are off the hot path, so a coarse lock is correct + simple.
    private static readonly Lock RegistrationLock = new();

    // ═══════════════════════════════════════════════════════════════════════
    // Registration API (called during static initialization)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Declare a component for an archetype. Called from <see cref="Archetype{TSelf}.Register{T}"/>.
    /// Assigns or reuses a ComponentTypeId and records the pending slot.
    /// </summary>
    internal static Comp<T> DeclareComponent<TArchetype, T>() where T : unmanaged
    {
        // Serialise concurrent registrations (parallel test fixtures / Workbench ALCs declare components from separate threads). Reentrant.
        using var scopeLock = RegistrationLock.EnterScope();

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

        // Get or create pending registration for this archetype. ConditionalWeakTable has no indexer-set, so use `Add` — this branch only runs on the first
        // DeclareComponent for a given archetype Type, so the duplicate-add path is impossible.
        if (!PendingRegistrations.TryGetValue(archetypeType, out var pending))
        {
            pending = new PendingArchetype { ArchetypeType = archetypeType };
            PendingRegistrations.Add(archetypeType, pending);
        }

        // Record the component
        pending!.Components.Add((typeof(T), componentTypeId));

        return new Comp<T>(componentTypeId);
    }

    /// <summary>
    /// Ensure an archetype type is finalized. Triggers static initialization (field initializers) then calls FinalizeArchetypeInternal if not already done.
    /// </summary>
    /// <param name="archetypeType">The archetype CLR type to finalize.</param>
    /// <param name="fromBarrier">
    /// True when called from the generated <c>[ModuleInitializer]</c> barrier (feature #514 Phase 5). In that mode a cross-ALC collision — the same archetype
    /// <see cref="Type.FullName"/> already registered from a DIFFERENT <see cref="AssemblyLoadContext"/> — is a tolerated no-op rather than a throw: the barrier
    /// registers a whole assembly eagerly at load, and a host that both references a schema (default ALC) and loads it collectibly would otherwise poison the
    /// module initializer (which fails ALL registrations in that assembly). The archetype is already registered by the other ALC, so there is nothing to do; the
    /// engine's on-demand path (EngineLifecycle / InitializeArchetypes) remains the authority. Genuine authoring errors (duplicate id, &gt;16 components) still
    /// throw. Non-barrier callers keep the original strict diagnostic (a real cross-ALC bug is surfaced clearly).
    /// </param>
    internal static void EnsureFinalized(Type archetypeType, bool fromBarrier = false)
    {
        // Serialise with concurrent registration/freeze. Reentrant: FinalizeArchetypeInternal recurses here for the parent chain, and RunClassConstructor
        // below may re-enter via a static field initializer (DeclareComponent).
        using var scopeLock = RegistrationLock.EnterScope();

        // First ensure static field initializers have run (DeclareComponent calls)
        RuntimeHelpers.RunClassConstructor(archetypeType.TypeHandle);

        var attr = archetypeType.GetCustomAttribute<ArchetypeAttribute>();
        if (attr == null)
        {
            return;
        }

        // Already finalized? Identity is the CLR type full name (#514 D1 — no author-set id). If this identity already holds a slot filled by a DIFFERENT CLR
        // type, it's a cross-ALC collision: the same schema loaded into two AssemblyLoadContexts (e.g. the Workbench referencing a schema in the default ALC
        // while also loading it into a collectible per-session ALC). The two CLR types share their FullName but not identity; the registry can hold only one CLR
        // type per archetype identity. The barrier tolerates it (the archetype is already registered by the other ALC — nothing to do; throwing would poison the
        // [ModuleInitializer] and fail every registration in the assembly); strict callers get a clear, actionable diagnostic.
        var identity = archetypeType.FullName;
        if (ArchetypeIdByName.TryGetValue(identity!, out var existingId) && Archetypes[existingId] != null)
        {
            var existing = Archetypes[existingId];
            if (existing.ArchetypeType != archetypeType)
            {
                if (fromBarrier)
                {
                    return;
                }
                throw new InvalidOperationException(
                    $"Archetype '{identity}' is loaded in two different AssemblyLoadContexts. The engine's static archetype registry can hold only one CLR type "
                    + "per archetype identity. This usually happens when an embedding host (e.g. the Workbench) loads a schema DLL into a collectible ALC for one "
                    + "operation while another code path references the same archetype from the default ALC. Close any open session that loaded this schema, or "
                    + "restart the host process, then retry.");
            }
            return;
        }

        FinalizeArchetypeInternal(archetypeType);
    }

    private static void FinalizeArchetypeInternal(Type archetypeType)
    {
        // Read [Archetype] attribute
        var attr = archetypeType.GetCustomAttribute<ArchetypeAttribute>();
        if (attr == null)
        {
            throw new InvalidOperationException($"Archetype type {archetypeType.Name} is missing [Archetype] attribute");
        }

        // Per-process catalog id is engine-assigned, dense, keyed by identity (#514 D1). Reused if this identity already has one (re-finalize within the same
        // ALC, or a same-name reload into a fresh ALC after the prior slot was cleared by UnregisterEngineUse).
        var identity = archetypeType.FullName;
        var archetypeId = GetOrAssignCatalogId(identity);

        // Already finalized (re-entrant via the parent chain, or a same-identity reload). A slot held by a DIFFERENT CLR type is a cross-ALC collision — surfaced
        // clearly in EnsureFinalized; reachable here only via parent-chain recursion, where we simply keep the existing registration.
        if (Archetypes[archetypeId] != null)
        {
            return;
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

            // Parent's catalog id comes from its finalized metadata (#514 D1 — no author-set id to read).
            if (!MetadataByType.TryGetValue(parentType, out var parentMeta))
            {
                throw new InvalidOperationException(
                    $"Parent archetype '{parentType.FullName}' failed to finalize before child '{archetypeType.FullName}' — is it missing [Archetype]?");
            }

            parentArchetypeId = parentMeta.ArchetypeId;

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

        // Re-hydrate the component-id maps if a previous `UnregisterEngineUse` cleared them. The static ctor of the component types ran AT MOST ONCE per
        // process, so a same-ALC sequential open (engine1.Dispose → engine2 in the same ALC) can't rely on `DeclareComponent` to repopulate. We use the cached
        // `(Type, ComponentTypeId)` tuples from `PendingArchetype.Components` to restore the exact same IDs the user's static `Comp<T>` fields captured at
        // first registration — preserves API contract that `MixedUnit.Field.ComponentTypeId` is stable for the type's lifetime.
        foreach (var (compType, compTypeId) in ownComponents)
        {
            if (compType == null)
            {
                continue;
            }
            if (!ComponentTypeIds.ContainsKey(compType))
            {
                ComponentTypeIds[compType] = compTypeId;
                ComponentTypeById[compTypeId] = compType;
                var schemaName = compType.GetCustomAttribute<ComponentAttribute>()?.Name;
                if (schemaName != null && !ComponentTypeIdsBySchemaName.ContainsKey(schemaName))
                {
                    ComponentTypeIdsBySchemaName[schemaName] = compTypeId;
                }
            }
        }

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
            // Durable identity (#514 D4): the schema name defaults to the CLR type's simple name — preserving the pre-D4 on-disk name for existing databases — but
            // an [Archetype(Name=...)] override decouples it from the C# type so the class can be renamed freely. PreviousName is the reopen rename hint.
            Name = attr.Name ?? archetypeType.Name,
            PreviousName = attr.PreviousName,
            Alias = attr.Alias,
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

    /// <summary>
    /// Build an <see cref="ArchetypeMaskLarge"/> with bits set for all archetypes that declare a component with the given type ID.
    /// Used when <see cref="MaxArchetypeId"/> &gt; 255.
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
    /// Increment the engine-use refcount for each provided archetype + component Type. Called by <see cref="DatabaseEngine.InitializeArchetypes"/> with the
    /// snapshot of Types that engine consumed. Paired with <see cref="UnregisterEngineUse"/> on engine dispose; balanced calls release the registry's
    /// references so the owning <see cref="System.Runtime.Loader.AssemblyLoadContext"/> can be GC'd.
    /// </summary>
    internal static void RegisterEngineUse(IReadOnlyCollection<Type> archetypeTypes, IReadOnlyCollection<Type> componentTypes)
    {
        ArgumentNullException.ThrowIfNull(archetypeTypes);
        ArgumentNullException.ThrowIfNull(componentTypes);

        lock (RegistrationLock)
        {
            foreach (var t in archetypeTypes)
            {
                ArchetypeRefcount.TryGetValue(t, out var c);
                ArchetypeRefcount[t] = c + 1;
            }
            foreach (var t in componentTypes)
            {
                ComponentRefcount.TryGetValue(t, out var c);
                ComponentRefcount[t] = c + 1;
            }
        }
    }

    /// <summary>
    /// Decrement the engine-use refcount for each provided archetype + component Type; when the refcount reaches zero the corresponding entry is removed from
    /// every registry table that holds a <see cref="Type"/> reference (<see cref="Archetypes"/>, <see cref="MetadataByType"/>,
    /// <see cref="PendingRegistrations"/>, <see cref="ComponentTypeIds"/>, <see cref="ComponentTypeById"/>,
    /// <see cref="ComponentTypeIdsBySchemaName"/>).
    ///
    /// Idempotent: a Type that isn't currently refcounted is silently skipped (so a duplicate <c>UnregisterEngineUse</c> call from a double-dispose is a
    /// no-op, not a crash).
    ///
    /// <see cref="NextComponentTypeId"/> is intentionally NOT decremented — IDs stay monotonically increasing for the process lifetime so a new engine can
    /// never collide with a freshly-removed (but still-referenced elsewhere, e.g. on disk) component ID.
    ///
    /// <see cref="MaxRegisteredArchetypeId"/> is similarly left as a high-water mark — it sizes
    /// <see cref="ArchetypeMaskLarge"/> and bumping it back down would invalidate any cached masks the next engine builds; the few wasted bits cost nothing.
    /// </summary>
    internal static void UnregisterEngineUse(IReadOnlyCollection<Type> archetypeTypes, IReadOnlyCollection<Type> componentTypes)
    {
        ArgumentNullException.ThrowIfNull(archetypeTypes);
        ArgumentNullException.ThrowIfNull(componentTypes);

        lock (RegistrationLock)
        {
            foreach (var t in archetypeTypes)
            {
                if (!ArchetypeRefcount.TryGetValue(t, out var c))
                {
                    continue; // idempotent — already unregistered or never registered through the engine path
                }
                if (c > 1)
                {
                    ArchetypeRefcount[t] = c - 1;
                    continue;
                }
                ArchetypeRefcount.Remove(t);
                
                // ─── ALC-collectibility gate ─────────────────────────────────────────────────────────────
                // Only clear registry entries for Types that came from a COLLECTIBLE AssemblyLoadContext (i.e., user schema DLLs loaded by the Workbench's
                // per-session ALC). Default-ALC Types (the engine's own assemblies, tests' compiled-in archetypes) are kept in the registry — they live for
                // the process lifetime anyway, and existing tests use [OneTimeSetUp] to `Touch()` archetypes ONCE per fixture, expecting registry persistence
                // across the fixture's individual tests. Aggressively clearing default-ALC entries breaks that assumption and produces a "registry empty after
                // first dispose" pollution failure mode.
                //
                // Workbench session lifecycle: schema loaded into collectible ALC → entries cleared here → ALC.Unload reclaims Types → next session loads fresh
                // ALC + fresh entries. That's the bug we're fixing. Default-ALC behaviour is unchanged from pre-fix.
                if (!IsCollectibleAlcType(t))
                {
                    continue; // refcount went to 0 but we leave the registry intact for default-ALC types
                }
                // Remove from every static table that holds this Type — identity check on Archetypes[id] guards against a stale slot that was overwritten via
                // a different code path (defensive).
                if (MetadataByType.Remove(t, out var meta))
                {
                    if (meta.ArchetypeId < Archetypes.Length && Archetypes[meta.ArchetypeId] == meta)
                    {
                        Archetypes[meta.ArchetypeId] = null;
                        RegisteredCount--;
                    }
                }
                // Note: PendingRegistrations is intentionally NOT cleared here. It's a ConditionalWeakTable keyed on Type; entries self-clean when the Type
                // is GC'd (ALC unload). Within the same ALC, keeping the entry lets a re-finalize succeed (the static ctor that populated it runs at most
                // once per Type per process — without the cached entry, FinalizeArchetypeInternal would see an empty components list and produce a broken
                // metadata). Clear the per-generic-instantiation `Archetype<T>._metadata` static field. This is critical: the static cache lives on the generic
                // instantiation `Archetype<TSelf>` and is NOT released by removing entries from the registry's dictionaries — `_metadata` still holds the
                // orphaned ArchetypeMetadata, so a subsequent `Archetype<TSelf>.Touch()` returns the stale reference without re-finalizing (the class
                // constructor has already run; the cached value short-circuits `EnsureFinalized`). Net effect without this clear: the next engine that touches
                // the same TSelf type sees `Metadata != null` but `Archetypes[id] == null`, and `InitializeArchetypes` never re-populates the per-engine
                // state → `Spawn` fails its EntityMap assertion.
                ClearArchetypeMetadataField(t);
            }

            foreach (var t in componentTypes)
            {
                if (!ComponentRefcount.TryGetValue(t, out var c))
                {
                    continue;
                }
                if (c > 1)
                {
                    ComponentRefcount[t] = c - 1;
                    continue;
                }
                ComponentRefcount.Remove(t);
                // Same ALC-collectibility gate as for archetypes — see commentary above.
                if (!IsCollectibleAlcType(t))
                {
                    continue;
                }
                if (ComponentTypeIds.Remove(t, out var id))
                {
                    // Reverse-mapping discipline: drop it only when this Type is still the one stored there, AND restore it to any OTHER Type that still claims
                    // the same id afterward.
                    //
                    // Schema-name dedup in `DeclareComponent` makes this important: a default-ALC component can register first
                    // (ComponentTypeById[id] = default-ALC Type), then a collectible-ALC copy of the same component overrides via the schema-name path
                    // (ComponentTypeById[id] = ALC Type). When the ALC unloads and we remove its forward mapping, the default-ALC version is still in
                    // ComponentTypeIds — we must point the reverse mapping back at it so the engine's slot→Type lookups keep working for the surviving
                    // default-ALC archetypes. O(N) walk over ComponentTypeIds; bounded by the small component-type count (≤ a few hundred in any realistic
                    // schema).
                    if (ComponentTypeById.TryGetValue(id, out var current) && current == t)
                    {
                        Type fallback = null;
                        foreach (var (otherType, otherId) in ComponentTypeIds)
                        {
                            if (otherId == id && otherType != t)
                            {
                                fallback = otherType;
                                break;
                            }
                        }
                        if (fallback != null)
                        {
                            ComponentTypeById[id] = fallback;
                        }
                        else
                        {
                            ComponentTypeById.TryRemove(id, out _);
                        }
                    }
                    // Same fallback discipline for the schema-name → id mapping.
                    var schemaName = t.GetCustomAttribute<ComponentAttribute>()?.Name;
                    if (schemaName != null && ComponentTypeIdsBySchemaName.TryGetValue(schemaName, out var schemaId) && schemaId == id)
                    {
                        // Check whether any other Type still claims this id — if yes, leave the schema-name
                        // mapping intact (it still resolves to a valid Type); if no, drop it.
                        var anyOther = false;
                        foreach (var (otherType, otherId) in ComponentTypeIds)
                        {
                            if (otherId == id && otherType != t)
                            {
                                anyOther = true;
                                break;
                            }
                        }
                        if (!anyOther)
                        {
                            ComponentTypeIdsBySchemaName.TryRemove(schemaName, out _);
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Whether a <see cref="Type"/> was loaded into a collectible <see cref="AssemblyLoadContext"/>. Used by <see cref="UnregisterEngineUse"/> to discriminate
    /// user-schema Types (which need cleanup so the ALC can unload) from default-ALC Types (which the engine's tests assume persist for the process lifetime,
    /// and which can never unload anyway since the default ALC isn't collectible).
    ///
    /// Falls back to <c>false</c> when <see cref="AssemblyLoadContext.GetLoadContext"/> returns null (defensive — should never happen for a real Type but keeps
    /// the registry intact if it does).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsCollectibleAlcType(Type t)
    {
        var alc = AssemblyLoadContext.GetLoadContext(t.Assembly);
        return alc != null && alc.IsCollectible;
    }

    /// <summary>
    /// Reflectively null out the <c>Archetype&lt;archetypeType&gt;._metadata</c> static field. Used by <see cref="UnregisterEngineUse"/> to release the cached
    /// metadata on the generic instantiation so a later <c>Archetype&lt;TSelf&gt;.Touch()</c> re-runs <c>EnsureFinalized</c> from a clean baseline.
    ///
    /// <para>Reflection is the only viable approach: the field is private to the generic, and the generic instantiation can be any user-supplied <c>TSelf</c>.
    /// The work happens once per disposal — not on any hot path — so the reflection cost is irrelevant. Caches the FieldInfo per generic instantiation in
    /// <see cref="MetadataFieldCache"/> to amortise repeated unregister/re-register cycles within the same process (e.g. a test suite that opens + closes
    /// engines hundreds of times).</para>
    /// </summary>
    private static void ClearArchetypeMetadataField(Type archetypeType)
    {
        if (!MetadataFieldCache.TryGetValue(archetypeType, out var fi))
        {
            // Walk the base-type chain to find the concrete `Archetype<TSelf>` declaration that carries the `_metadata` field. CRTP guarantees the field lives
            // somewhere on the chain — either on the user archetype's direct base (root archetype) or on a parent archetype's `Archetype<TParent>` base
            // (inheritance chain). MakeGenericType(archetypeType) on the open `Archetype<>` definition is the canonical lookup.
            var closedArchetype = typeof(Archetype<>).MakeGenericType(archetypeType);
            fi = closedArchetype.GetField("_metadata", BindingFlags.NonPublic | BindingFlags.Static);
            if (fi == null)
            {
                return; // not an archetype shape we can clean up — leave it; caller is best-effort
            }
            // ConditionalWeakTable has no indexer-set; `Add` throws on duplicate, but we're in the no-cache-hit branch so a duplicate is impossible.
            // `AddOrUpdate` would also work — chose `Add` for the explicit assumption-as-assertion semantic.
            MetadataFieldCache.Add(archetypeType, fi);
        }
        fi!.SetValue(null, null);
    }

    /// <summary>
    /// Cached reflection handles for <see cref="ClearArchetypeMetadataField"/>; amortised across runs.
    ///
    /// <para><b>Must be <see cref="ConditionalWeakTable{TKey,TValue}"/></b>, not a strong-keyed <c>Dictionary</c>: the cache key is the user's archetype
    /// <see cref="Type"/>, and a strong reference would pin that Type (and its <see cref="AssemblyLoadContext"/>) forever — defeating the entire lifecycle fix.
    /// With a CWT, the entry self-cleans when the Type is GC'd (i.e. when the ALC unloads). The cost: re-resolving the FieldInfo for a new (re-loaded) Type,
    /// which is a one-time hit per registration.</para>
    /// </summary>
    private static readonly ConditionalWeakTable<Type, FieldInfo> MetadataFieldCache = new();

    /// <summary>
    /// Freeze the registry: build SubtreeArchetypeIds for each archetype, prevent further registration. Called by DatabaseEngine before the first transaction.
    /// </summary>
    public static void Freeze()
    {
        // Lock first, THEN check Frozen — so the check + build + set is atomic and the global derived data (subtree + cascade graph) is built EXACTLY ONCE
        // across concurrent DatabaseEngine.InitializeArchetypes calls. A late DeclareComponent flips Frozen back to false, so a subsequent Freeze rebuilds.
        using var scopeLock = RegistrationLock.EnterScope();
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

        // Build + validate the cascade-delete graph here (once, under the lock) rather than per-engine. The per-engine rebuild was Face B of the flaky race:
        // two engines concurrently clearing + re-adding CascadeTargets on the shared metadata produced a spurious "cascade diamond". #514 Phase 3.
        BuildAndValidateCascadeGraph();

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

                    // Register the cascade target on the PARENT (target) archetype, resolved by IDENTITY (name) → the single authoritative catalog slot
                    // (#514 D1). A type-keyed lookup is fragile across ALCs: when the EntityLink's type argument is a cross-ALC twin, its own finalization may
                    // have been skipped by the cross-ALC guard, so it isn't in MetadataByType — yet the destroy path resolves the entity's archetype through the
                    // name-assigned catalog slot. Registering here on that same Archetypes[catalogId] instance keeps the cascade edge and the destroy lookup on
                    // one metadata object.
                    var targetName = targetArchetypeType.FullName;
                    if (targetName == null || !ArchetypeIdByName.TryGetValue(targetName, out var targetCatalogId) || Archetypes[targetCatalogId] == null)
                    {
                        continue;
                    }
                    var parentMeta = Archetypes[targetCatalogId];

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
