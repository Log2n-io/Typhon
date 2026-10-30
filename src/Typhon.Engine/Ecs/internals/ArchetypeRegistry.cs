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
/// runtime (array indexed by ArchetypeId). Process-wide, with one archetype per name per load context: a collectible context's entries leave with it.
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

    /// <summary>
    /// Component schema name, within its load context (<see cref="ScopedName"/>) → ComponentTypeId: the dedup across V1/V2 CLR types sharing one schema name.
    /// Two load contexts' copies of a component are two components, as they are two types.
    /// </summary>
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
    // doubles as the archetype name) WITHIN ITS LOAD CONTEXT (#554): two contexts' copies of one schema are two archetypes, each with its own slot and metadata,
    // which is what lets a host open two databases whose schemas share archetype names. ArchetypeIdByName dedups a re-finalize within a context; its string keys
    // never pin an ALC. A collectible context's ids go back to FreeCatalogIds with it, since a Workbench reopening a database gets a new context every time. The
    // counter starts at 1: id 0 is reserved (default(ushort) null/sentinel).
    private static ushort NextArchetypeId;
    private static readonly Dictionary<string, ushort> ArchetypeIdByName = new();
    private static readonly Queue<ushort> FreeCatalogIds = new();

    private static bool Frozen;

    /// <summary>A number per non-default load context, for <see cref="ScopedName"/>. Weakly keyed, so it pins no context.</summary>
    private static readonly ConditionalWeakTable<AssemblyLoadContext, object> ContextKeys = new();

    private static int NextContextKey;

    /// <summary><paramref name="name"/> qualified by the load context of <paramref name="type"/>. The default context's names stand for themselves.</summary>
    private static string ScopedName(Type type, string name)
    {
        var context = AssemblyLoadContext.GetLoadContext(type.Assembly);
        if (context == null || context == AssemblyLoadContext.Default)
        {
            return name;
        }

        return $"{ContextKeys.GetValue(context, TrackContext)}/{name}";
    }

    /// <summary>First sight of a context: a number for its names, and, for a collectible one, the release of what it registered when it goes.</summary>
    private static object TrackContext(AssemblyLoadContext context)
    {
        if (context.IsCollectible)
        {
            context.Unloading += ReleaseContext;
        }

        return ++NextContextKey;
    }

    /// <summary>
    /// A collectible context is going: releases what it registered and no engine uses, which is what a schema loaded but never opened leaves behind. An
    /// engine that still uses an entry releases it at its own dispose (<see cref="UnregisterEngineUse"/>).
    /// </summary>
    private static void ReleaseContext(AssemblyLoadContext context)
    {
        lock (RegistrationLock)
        {
            for (var i = 0; i < Archetypes.Length; i++)
            {
                var meta = Archetypes[i];
                if (meta != null && meta.LoadContext == context && !ArchetypeRefcount.ContainsKey(meta.ArchetypeType))
                {
                    ReleaseArchetype(meta.ArchetypeType);
                }
            }

            foreach (var (type, _) in ComponentTypeIds)
            {
                if (AssemblyLoadContext.GetLoadContext(type.Assembly) == context && !ComponentRefcount.ContainsKey(type))
                {
                    ReleaseComponent(type);
                }
            }
        }
    }

    /// <summary>Get-or-assign the dense, per-process catalog id for an archetype identity (<see cref="ScopedName"/> of its CLR type full name). #514 D1.</summary>
    private static ushort GetOrAssignCatalogId(string identity)
    {
        if (ArchetypeIdByName.TryGetValue(identity, out var id))
        {
            return id;
        }

        // First released, first reused: ids released in order come back in order, so a reopened schema keeps the ids it had, as a "#<id>" in a saved query expects.
        if (FreeCatalogIds.TryDequeue(out id))
        {
            ArchetypeIdByName[identity] = id;
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
    // stable once frozen for as long as an engine uses them. Init/Dispose are off the hot path, so a coarse lock is correct + simple.
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
            var schemaName = SchemaKeyOf(typeof(T));
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
    /// True when called from the generated <c>[ModuleInitializer]</c> barrier (feature #514 Phase 5). In that mode two assemblies of one load context declaring
    /// the same archetype <see cref="Type.FullName"/> is a tolerated no-op rather than a throw: the barrier registers a whole assembly eagerly at load, and a
    /// throw there fails ALL registrations in that assembly. Genuine authoring errors (&gt;16 components) still throw. Non-barrier callers get the diagnostic.
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

        // Already finalized? Identity is the CLR type full name within its load context (#514 D1, #554). A slot held by a DIFFERENT CLR type means two assemblies
        // of one context declare the same name: the barrier tolerates it, since a throw would poison the [ModuleInitializer] and fail every registration in the
        // assembly, and strict callers get the diagnostic.
        var identity = ScopedName(archetypeType, archetypeType.FullName);
        if (ArchetypeIdByName.TryGetValue(identity, out var existingId) && Archetypes[existingId] != null)
        {
            var existing = Archetypes[existingId];
            if (existing.ArchetypeType != archetypeType)
            {
                if (fromBarrier)
                {
                    return;
                }
                throw new InvalidOperationException(
                    $"Archetype '{archetypeType.FullName}' is declared by two assemblies of one load context ('{existing.ArchetypeType.Assembly.GetName().Name}' and "
                    + $"'{archetypeType.Assembly.GetName().Name}'). A load context holds one archetype per name: rename one, or load them in separate contexts.");
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

        // Already finalized (re-entrant via the parent chain). A slot held by a DIFFERENT CLR type is two assemblies declaring one name — surfaced clearly in
        // EnsureFinalized; reachable here only via parent-chain recursion, where we simply keep the existing registration.
        var identity = ScopedName(archetypeType, archetypeType.FullName);
        if (ArchetypeIdByName.TryGetValue(identity, out var knownId) && Archetypes[knownId] != null)
        {
            return;
        }

        // Get pending registration (may be empty if archetype has no own components)
        PendingRegistrations.TryGetValue(archetypeType, out var pending);
        var ownComponents = pending?.Components ?? [];

        // Walk parent chain to collect inherited components (parent-first ordering)
        var allComponents = new List<(Type ComponentType, int ComponentTypeId)>();
        ushort parentArchetypeId = ArchetypeMetadata.NoParent;
        ArchetypeMetadata parentMeta = null;

        var parentType = FindParentArchetypeType(archetypeType);
        if (parentType != null)
        {
            // Ensure parent is finalized first (recursive — handles multi-level chains)
            EnsureFinalized(parentType);

            // Parent's catalog id comes from its finalized metadata (#514 D1 — no author-set id to read).
            if (!MetadataByType.TryGetValue(parentType, out parentMeta))
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
                var schemaName = SchemaKeyOf(compType);
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

        // Per-process catalog id is engine-assigned, dense, keyed by identity (#514 D1). Past the throw above, so an archetype that cannot finalize holds no id: a
        // scoped identity never recurs, and each reload would lose one.
        var archetypeId = GetOrAssignCatalogId(identity);
        parentMeta?.ChildArchetypeIds.Add(archetypeId);

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
            // #568: declared per-archetype durability window for SingleVersion cluster values. Not inherited from the parent archetype — a child owns its own
            // cluster segment and its own fence work item, so the setting is meaningful (and independently choosable) per concrete archetype.
            ClusterDurability = attr.ClusterDurability,
            ComponentCount = totalComponentCount,
            ParentArchetypeId = parentArchetypeId,
            ArchetypeType = archetypeType,
            LoadContext = AssemblyLoadContext.GetLoadContext(archetypeType.Assembly),
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
            // One read: a session closing can null the slot between two.
            var meta = Archetypes[i];
            if (meta != null)
            {
                yield return meta;
            }
        }
    }

    /// <summary>
    /// The archetypes of one load context, and their ancestors wherever those live: what an engine of that context consumes (#554). A schema's parent can sit in a
    /// dependency the host resolved from the default context. The rest of the registry is another schema's.
    /// </summary>
    internal static ArchetypeMetadata[] GetArchetypesFor(AssemblyLoadContext context)
    {
        var own = new List<ArchetypeMetadata>();
        HashSet<ArchetypeMetadata> ancestors = null;
        for (var i = 0; i < Archetypes.Length; i++)
        {
            var meta = Archetypes[i];
            if (meta == null || meta.LoadContext != context)
            {
                continue;
            }

            own.Add(meta);
            for (var parentId = meta.ParentArchetypeId; parentId != ArchetypeMetadata.NoParent;)
            {
                var parent = Archetypes[parentId];
                if (parent == null || parent.LoadContext == context)
                {
                    break;
                }

                (ancestors ??= []).Add(parent);
                parentId = parent.ParentArchetypeId;
            }
        }

        if (ancestors != null)
        {
            own.AddRange(ancestors);
        }

        return own.ToArray();
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

    // ── Live-engine accounting (#614 D-9) ────────────────────────────────────────────────────────────────────────────────────────────────────────────────
    //
    // TyphonProfiler is a process-global static: two DatabaseEngine instances alive at once do NOT produce two traces, they produce ONE trace with interleaved
    // events from two colliding archetype routing-id spaces. The profiler needs to know when that happened so it can withhold the routing ids rather than
    // write ambiguous ones.
    //
    // This count is kept SEPARATE from the per-Type refcounts below, rather than riding along inside Register/UnregisterEngineUse, because the two have
    // different pairing requirements. The Type refcounts are deliberately forgiving — UnregisterEngineUse skips Types it doesn't know, so an engine that
    // registered components and then died before InitializeArchetypes does no harm. An engine COUNT cannot be forgiving in the same way: one unpaired
    // decrement makes the process under-report, and under-reporting is the dangerous direction — it writes ambiguous routing ids while believing they are
    // sound. Explicit one-shot calls from the engine's own init/dispose make the pairing exact by construction.

    /// <summary>Engines currently registered with the process-global registry. Written only under <c>RegistrationLock</c>.</summary>
    private static int LiveEngineCount;

    /// <summary>High-water mark of <see cref="LiveEngineCount"/> since the last <see cref="ResetLiveEngineHighWater"/>.</summary>
    private static int MaxLiveEngineCountSinceReset;

    /// <summary>Number of engines live right now. Read at capture start to warn when a capture is mixed from its very first event.</summary>
    internal static int CurrentLiveEngineCount
    {
        get
        {
            lock (RegistrationLock)
            {
                return LiveEngineCount;
            }
        }
    }

    /// <summary>
    /// Highest number of simultaneously-live engines observed since the last reset. Read at capture close: a value &gt; 1 means this trace is mixed and its
    /// routing ids are ambiguous.
    /// </summary>
    /// <remarks>
    /// A high-water mark rather than a snapshot, because the case that matters is a second engine that both appears <i>and disappears</i> inside the capture
    /// window — sampling the live count at close would report 1 and hand out ids from two id spaces as if they were one.
    /// </remarks>
    internal static int MaxLiveEngineCount
    {
        get
        {
            lock (RegistrationLock)
            {
                return MaxLiveEngineCountSinceReset;
            }
        }
    }

    /// <summary>
    /// Rebases the high-water mark to the current live count. Called by the profiler when a capture starts, so the mark measures that capture's window and not
    /// the whole process lifetime. Exact for this purpose because profiler sessions are process-global and serialized.
    /// </summary>
    internal static void ResetLiveEngineHighWater()
    {
        lock (RegistrationLock)
        {
            MaxLiveEngineCountSinceReset = LiveEngineCount;
        }
    }

    /// <summary>Records that an engine has come online. Called once per engine from <see cref="DatabaseEngine.InitializeArchetypes"/>, guarded there.</summary>
    internal static void RegisterLiveEngine()
    {
        lock (RegistrationLock)
        {
            LiveEngineCount++;
            if (LiveEngineCount > MaxLiveEngineCountSinceReset)
            {
                MaxLiveEngineCountSinceReset = LiveEngineCount;
            }
        }
    }

    /// <summary>
    /// Records that an engine has gone away. Paired one-for-one with <see cref="RegisterLiveEngine"/> by the engine's dispose guard; the floor at zero is
    /// belt-and-braces so a hypothetical unpaired call degrades to under-counting by one rather than to a permanently negative counter.
    /// </summary>
    internal static void UnregisterLiveEngine()
    {
        lock (RegistrationLock)
        {
            if (LiveEngineCount > 0)
            {
                LiveEngineCount--;
            }
        }
    }

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
                ReleaseArchetype(t);
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
                ReleaseComponent(t);
            }
        }
    }

    /// <summary>Removes an archetype of a collectible context from every static table that holds its Type, and gives its catalog id back. Under the lock.</summary>
    private static void ReleaseArchetype(Type t)
    {
        // Identity check on Archetypes[id] guards against a stale slot that was overwritten via a different code path (defensive).
        if (MetadataByType.Remove(t, out var meta) && meta.ArchetypeId < Archetypes.Length && Archetypes[meta.ArchetypeId] == meta)
        {
            Archetypes[meta.ArchetypeId] = null;
            RegisteredCount--;

            // A default-context parent stays registered: it must not go on naming a child whose id the next schema gets. The derived graphs rebuild at the next
            // Freeze.
            if (meta.ParentArchetypeId != ArchetypeMetadata.NoParent)
            {
                Archetypes[meta.ParentArchetypeId]?.ChildArchetypeIds.Remove(meta.ArchetypeId);
            }

            ArchetypeIdByName.Remove(ScopedName(t, t.FullName));
            FreeCatalogIds.Enqueue(meta.ArchetypeId);
            Frozen = false;
        }

        // Note: PendingRegistrations is intentionally NOT cleared here. It's a ConditionalWeakTable keyed on Type; entries self-clean when the Type is GC'd (ALC
        // unload). Within the same ALC, keeping the entry lets a re-finalize succeed (the static ctor that populated it runs at most once per Type per process —
        // without the cached entry, FinalizeArchetypeInternal would see an empty components list and produce a broken metadata). Clear the per-generic-instantiation
        // `Archetype<T>._metadata` static field. This is critical: the static cache lives on the generic instantiation `Archetype<TSelf>` and is NOT released by
        // removing entries from the registry's dictionaries — `_metadata` still holds the orphaned ArchetypeMetadata, so a subsequent `Archetype<TSelf>.Touch()`
        // returns the stale reference without re-finalizing (the class constructor has already run; the cached value short-circuits `EnsureFinalized`). Net effect
        // without this clear: the next engine that touches the same TSelf type sees `Metadata != null` but `Archetypes[id] == null`, and `InitializeArchetypes` never
        // re-populates the per-engine state → `Spawn` fails its EntityMap assertion.
        ClearArchetypeMetadataField(t);
    }

    /// <summary>Removes a component of a collectible context from every static table that holds its Type. Under the lock.</summary>
    private static void ReleaseComponent(Type t)
    {
        if (!ComponentTypeIds.Remove(t, out var id))
        {
            return;
        }

        // Reverse-mapping discipline: drop it only when this Type is still the one stored there, AND restore it to any OTHER Type that still claims the same id
        // afterward. V1/V2 dedup in `DeclareComponent` makes this important: V1 registers first (ComponentTypeById[id] = V1), then V2 overrides via the schema-name
        // path. Removing V2's forward mapping leaves V1 in ComponentTypeIds, and the reverse mapping must point back at it so the slot→Type lookups keep working.
        // O(N) walk over ComponentTypeIds; bounded by the small component-type count (≤ a few hundred in any realistic schema).
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
        var schemaName = SchemaKeyOf(t);
        if (schemaName != null && ComponentTypeIdsBySchemaName.TryGetValue(schemaName, out var schemaId) && schemaId == id)
        {
            // Check whether any other Type still claims this id — if yes, leave the schema-name mapping intact (it still resolves to a valid Type); if no, drop it.
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

    /// <summary>The key of <see cref="ComponentTypeIdsBySchemaName"/> for a component, or <c>null</c> when it declares no schema name.</summary>
    private static string SchemaKeyOf(Type componentType)
    {
        var schemaName = componentType.GetCustomAttribute<ComponentAttribute>()?.Name;
        return schemaName == null ? null : ScopedName(componentType, schemaName);
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

    /// <summary>The <see cref="FreezeGeneration"/> each load context last passed validation at. Weakly keyed, so it pins no context.</summary>
    private static readonly ConditionalWeakTable<AssemblyLoadContext, StrongBox<int>> ValidatedAt = new();

    /// <summary>Counts the rebuilds of the derived data, so that a context is validated once per rebuild and not once per engine.</summary>
    private static int FreezeGeneration;

    /// <summary>
    /// Freeze the registry and validate one load context's schema: build SubtreeArchetypeIds and the cascade graph, prevent further registration. Called by
    /// DatabaseEngine before the first transaction.
    /// </summary>
    /// <param name="context">The context whose schema the caller consumes; null means the default one.</param>
    /// <remarks>
    /// The derived data is process-wide and built once, but validation is per context (#554): a schema that fails it refuses the engines that consume it, and
    /// cannot refuse the open of a database in another context.
    /// </remarks>
    public static void Freeze(AssemblyLoadContext context = null)
    {
        context ??= AssemblyLoadContext.Default;

        // Lock first, THEN check Frozen — so the check + build + set is atomic and the global derived data (subtree + cascade graph) is built EXACTLY ONCE
        // across concurrent DatabaseEngine.InitializeArchetypes calls. A late DeclareComponent flips Frozen back to false, so a subsequent Freeze rebuilds.
        using var scopeLock = RegistrationLock.EnterScope();
        if (!Frozen)
        {
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

            // Build the cascade-delete graph here (once, under the lock) rather than per-engine. The per-engine rebuild was Face B of the flaky race: two engines
            // concurrently clearing + re-adding CascadeTargets on the shared metadata produced a spurious "cascade diamond". #514 Phase 3.
            BuildCascadeGraphs();

            FreezeGeneration++;
            Frozen = true;
        }

        if (ValidatedAt.TryGetValue(context, out var validated) && validated.Value == FreezeGeneration)
        {
            return;
        }

        var archetypes = GetArchetypesFor(context);
        ValidateCascadeGraph(archetypes);

        // #678 step 1. Validated HERE and not in FinalizeArchetypeInternal on purpose: finalization runs inside the generated [ModuleInitializer] barrier, and
        // a throw there fails EVERY registration in the assembly (see EnsureFinalized's remarks) rather than the one schema at fault.
        ValidateComponentDeclarations(archetypes);

        ValidatedAt.AddOrUpdate(context, new StrongBox<int>(FreezeGeneration));
    }

    /// <summary>
    /// Rejects a schema whose component declarations make a unique <c>[Index]</c> unenforceable, or that wastes a component slot on a duplicate declaration.
    /// The open-world backstop for the build-time <c>TPH1003</c> / <c>TPH1004</c> diagnostics (#678 step 1, design §2.1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two rules, both about where a component is declared.</b> A unique index is scoped to the declaring archetype's subtree, so a component carrying one
    /// may be declared at most once <i>per archetype tree</i>. And a component may be declared once per inheritance chain: re-declaring an inherited one
    /// appends a second slot for the same component type, which <see cref="ArchetypeMetadata.TryGetSlot"/> can never return, while it still consumes one of
    /// the 16 slots and a full cluster column.
    /// </para>
    /// <para>
    /// <b>Per tree, because that is where the index is stored.</b> There is one B+Tree per (archetype, indexed field), so a unique field gets one tree per
    /// archetype tree and the duplicate check is a single descent. Two declarers <i>inside one tree</i> would put two trees under one root with nothing
    /// spanning them, so enforcement would mean probing every sibling tree on each insert — O(K) descents, and racy, since probe and insert are not atomic.
    /// Two declarers in <i>unrelated</i> trees already have their own trees: two independent constraints over disjoint populations, nothing to span, nothing
    /// to coordinate — and no query can compare them either, since no subtree contains two roots. The consequence is worth stating: the same key value may
    /// exist once per tree, so "unique" means unique within a tree, not database-wide.
    /// </para>
    /// <para>
    /// <b>The second rule is what makes the first one's reasoning valid.</b> Counting declarers per tree relies on no declarer being an ancestor of another —
    /// otherwise the two would be in one chain and the count would be meaningless. That held only by author discipline until now. The duplicate check runs
    /// first for the same reason: a re-declaration would otherwise surface as the less actionable "two declarers" message.
    /// </para>
    /// <para>
    /// Takes the archetype list explicitly so it can be exercised without registering a schema that would poison the module-init barrier for a whole assembly.
    /// </para>
    /// </remarks>
    /// <param name="archetypes">Every archetype to validate together — normally one load context's.</param>
    /// <exception cref="InvalidOperationException">
    /// A component is declared twice in one chain, or a unique-indexed component has more than one declarer in a single tree.
    /// </exception>
    internal static void ValidateComponentDeclarations(IEnumerable<ArchetypeMetadata> archetypes)
    {
        // Keyed by (componentTypeId, tree root) — the rule is per TREE, not per catalog. Component identity is the type id rather than the CLR type, because
        // that is what the index home is keyed by: two CLR structs sharing one [Component("name")] (the V1/V2 evolution pattern) are ONE component here.
        var declarers = new Dictionary<(int TypeId, ushort Root), List<ArchetypeMetadata>>();

        foreach (var meta in archetypes)
        {
            if (meta?._componentTypeIds == null)
            {
                continue;
            }

            var root = RootArchetypeIdOf(meta);

            // Slots [0, inheritedCount) are copied from the parent; everything after is what THIS archetype declares (FinalizeArchetypeInternal appends own
            // components after the parent's).
            var inheritedCount = meta.ParentArchetypeId != ArchetypeMetadata.NoParent && Archetypes[meta.ParentArchetypeId] != null
                ? Archetypes[meta.ParentArchetypeId].ComponentCount : 0;

            for (var slot = inheritedCount; slot < meta.ComponentCount; slot++)
            {
                var typeId = meta._componentTypeIds[slot];

                for (var prior = 0; prior < slot; prior++)
                {
                    if (meta._componentTypeIds[prior] != typeId)
                    {
                        continue;
                    }

                    var compName = meta._slotToComponentType?[slot]?.FullName ?? $"componentTypeId {typeId}";

                    // Two shapes reach here and they need different advice: the component came from an ancestor, or the archetype simply called Register<T>()
                    // twice for it. Both burn a second slot nothing can address; only the first has an inherited slot to point at.
                    var where = prior < inheritedCount
                        ? $"which it already inherits from '{DeclaringAncestorName(meta, typeId)}'. Remove the Register call; the inherited slot is "
                          + "already there"
                        : "twice. Remove the duplicate Register call";

                    throw new InvalidOperationException(
                        $"Archetype '{meta.ArchetypeType?.FullName}' declares component '{compName}' {where}. A component may be declared once per inheritance "
                        + "chain — the re-declaration consumes a second of the 16 component slots, and a whole cluster column, that nothing can address.");
                }

                var key = (typeId, root);
                if (!declarers.TryGetValue(key, out var list))
                {
                    list = [];
                    declarers[key] = list;
                }
                list.Add(meta);
            }
        }

        foreach (var (key, list) in declarers)
        {
            if (list.Count < 2)
            {
                continue;
            }

            var compType = list[0]._slotToComponentType?[list[0]._typeIdToSlot[key.TypeId]];
            var uniqueField = UniqueIndexFieldOf(compType);
            if (uniqueField == null)
            {
                continue;   // no unique index — any number of declarers is fine, anywhere, and sharing a component is ordinary composition
            }

            var names = new List<string>(list.Count);
            foreach (var m in list)
            {
                names.Add(m.ArchetypeType?.FullName);
            }
            names.Sort(StringComparer.Ordinal);

            var rootName = Archetypes[key.Root]?.ArchetypeType?.FullName;
            throw new InvalidOperationException(
                $"Component '{compType?.FullName}' declares a unique [Index] on field '{uniqueField}', but {list.Count} archetypes in the same tree (rooted at "
                + $"'{rootName}') declare it: {string.Join(", ", names)}. The index is stored per archetype, so each of these owns a separate B+Tree and "
                + "enforcing uniqueness between them would mean probing every sibling tree on each insert. Declare the component on their common ancestor "
                + "instead, so one tree covers the whole subtree, or use [Index(AllowMultiple = true)]. Archetypes in UNRELATED trees may each declare it — "
                + "each already has its own tree, so their constraints are independent and cost nothing.");
        }
    }

    /// <summary>
    /// Root of <paramref name="meta"/>'s archetype tree — itself when it has no parent. Two archetypes share a common ancestor if and only if they share
    /// this, which is the whole test behind the per-tree scope of a unique <c>[Index]</c>.
    /// </summary>
    private static ushort RootArchetypeIdOf(ArchetypeMetadata meta)
    {
        var current = meta;
        while (current.ParentArchetypeId != ArchetypeMetadata.NoParent && Archetypes[current.ParentArchetypeId] != null)
        {
            current = Archetypes[current.ParentArchetypeId];
        }

        return current.ArchetypeId;
    }

    /// <summary>Nearest ancestor of <paramref name="meta"/> declaring <paramref name="componentTypeId"/>, for the duplicate diagnostic.</summary>
    private static string DeclaringAncestorName(ArchetypeMetadata meta, int componentTypeId)
    {
        var parentId = meta.ParentArchetypeId;
        while (parentId != ArchetypeMetadata.NoParent && Archetypes[parentId] != null)
        {
            var parent = Archetypes[parentId];
            if (parent.TryGetSlot(componentTypeId, out _))
            {
                return parent.ArchetypeType?.FullName;
            }

            parentId = parent.ParentArchetypeId;
        }

        return "an ancestor";
    }

    /// <summary>
    /// Name of the first field carrying a UNIQUE <c>[Index]</c> (<c>AllowMultiple</c> false), or <see langword="null"/> when the component has none.
    /// <c>[SpatialIndex]</c> is a different attribute and never matches. Same reflection shape as <see cref="BuildCascadeGraph"/>.
    /// </summary>
    private static string UniqueIndexFieldOf(Type compType)
    {
        if (compType == null)
        {
            return null;
        }

        foreach (var field in compType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            var indexAttr = field.GetCustomAttribute<IndexAttribute>();
            if (indexAttr != null && !indexAttr.AllowMultiple)
            {
                return field.Name;
            }
        }

        return null;
    }

    /// <summary>
    /// Build the cascade delete graph. Called by <see cref="Freeze"/> once all archetypes have their SlotToComponentType populated.
    /// Safe to call multiple times (rebuilds each time).
    /// </summary>
    private static void BuildCascadeGraphs()
    {
        // Built aside and swapped in per archetype: a commit running meanwhile reads either graph, never one cleared to nothing (CASC-02).
        var targets = new Dictionary<ArchetypeMetadata, List<CascadeTarget>>();
        var fks = new Dictionary<ArchetypeMetadata, List<(byte Slot, int FieldOffset)>>();
        BuildCascadeGraph(targets, fks);
        foreach (var meta in GetAllArchetypes())
        {
            meta._cascadeTargets = targets.GetValueOrDefault(meta);
            meta._cascadeFks = fks.TryGetValue(meta, out var own) ? own.ToArray() : null;
        }

        IndexCascadeFkComponents();
    }

    /// <summary>
    /// Scan all registered archetypes' component fields for [Index(OnParentDelete = Delete)] on EntityLink fields.
    /// Build CascadeTargets on parent archetypes.
    /// </summary>
    private static void BuildCascadeGraph(Dictionary<ArchetypeMetadata, List<CascadeTarget>> targets,
        Dictionary<ArchetypeMetadata, List<(byte Slot, int FieldOffset)>> fks)
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

                    // Register the cascade target on the PARENT (target) archetype, resolved by IDENTITY → the single authoritative catalog slot (#514 D1). The
                    // destroy path resolves the entity's archetype through that same slot, so the cascade edge and the destroy lookup stay on one metadata object.
                    var targetName = targetArchetypeType.FullName;
                    if (targetName == null || !ArchetypeIdByName.TryGetValue(ScopedName(targetArchetypeType, targetName), out var targetCatalogId)
                        || Archetypes[targetCatalogId] == null)
                    {
                        continue;
                    }
                    var parentMeta = Archetypes[targetCatalogId];

                    var fieldOffset = (int)Marshal.OffsetOf(compType, field.Name);
                    if (!targets.TryGetValue(parentMeta, out var parentTargets))
                    {
                        targets[parentMeta] = parentTargets = [];
                    }

                    parentTargets.Add(new CascadeTarget
                    {
                        ChildArchetypeId = meta.ArchetypeId,
                        ChildArchetypeType = meta.ArchetypeType,
                        FkSlotIndex = slot,
                        FkFieldOffset = fieldOffset,
                    });
                    if (!fks.TryGetValue(meta, out var childFks))
                    {
                        fks[meta] = childFks = [];
                    }

                    childFks.Add((slot, fieldOffset));
                }
            }
        }
    }

    /// <summary>
    /// The component types some archetype declares a cascade foreign key in, by component type id; null when no archetype declares one. A commit's
    /// referential check (CASC-02) visits only these components' writes.
    /// </summary>
    internal static volatile bool[] CascadeFkComponentTypes;

    private static void IndexCascadeFkComponents()
    {
        bool[] types = null;
        foreach (var meta in GetAllArchetypes())
        {
            if (meta._cascadeFks == null)
            {
                continue;
            }

            foreach (var (slot, _) in meta._cascadeFks)
            {
                var typeId = meta._componentTypeIds[slot];
                if (typeId < 0)
                {
                    continue;
                }

                if (types == null || typeId >= types.Length)
                {
                    Array.Resize(ref types, Math.Max(typeId + 1, (types?.Length ?? 0) * 2));
                }

                types[typeId] = true;
            }
        }

        CascadeFkComponentTypes = types;
    }

    /// <summary>
    /// Validate the cascade graph: no cycles, no diamonds. DFS from each root.
    /// </summary>
    private static void ValidateCascadeGraph(ArchetypeMetadata[] archetypes)
    {
        // Collect all archetypes that have cascade targets (potential roots)
        var visited = new HashSet<ushort>();
        var inStack = new HashSet<ushort>();

        foreach (var meta in archetypes)
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
