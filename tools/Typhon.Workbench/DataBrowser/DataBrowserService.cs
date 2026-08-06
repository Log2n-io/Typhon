using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Typhon.Engine;
using Typhon.Workbench.Dtos.Data;
using Typhon.Workbench.Dtos.Profiler;
using Typhon.Workbench.Schema;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.DataBrowser;

/// <summary>
/// Read-only entity-data surface for the Data Browser (v1). Resolves an Open session's live <see cref="DatabaseEngine"/> and,
/// for a given archetype, enumerates entities (paged) and decodes a single entity's components into <see cref="EntityDetailDto"/>.
/// Stateless apart from a per-(session,archetype) snapshot cache of entity ids — safe because an Open session is static at HEAD.
/// Mirrors <see cref="SchemaService"/>'s session-resolution + exception model.
/// </summary>
public sealed class DataBrowserService
{
    private const int DefaultLimit = 200;
    private const int MaxLimit = 1000;

    private readonly SessionManager _sessions;

    // Per-session, per-archetype snapshot: the enumerated entity ids + the revision (TSN) they were read at. Keyed on the
    // OpenSession instance so the cache evicts automatically when the session is collected — no SessionManager coupling. The
    // value is a Lazy so the enumeration runs exactly once per (session, archetype): ConcurrentDictionary.GetOrAdd may invoke
    // its factory concurrently for the same key, and the Data Browser fires several identical page requests on mount (id-only,
    // then each preview-column change). Two concurrent EnumerateArchetypeEntities over the same EntityMap race on cold
    // page-cache loads of its chunks; serialising via Lazy.Value makes the scan single-threaded and cached thereafter.
    private readonly ConditionalWeakTable<OpenSession, ConcurrentDictionary<ushort, Lazy<ArchetypeSnapshot>>> _snapshots = new();

    public DataBrowserService(SessionManager sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        _sessions = sessions;
    }

    /// <summary>
    /// Drops every cached snapshot for a session. <b>Must be called whenever the session's engine is replaced</b> — i.e. on pause and on resume (#621).
    /// </summary>
    /// <remarks>
    /// <para>This is a correctness requirement, not housekeeping. <see cref="_snapshots"/> is keyed on the <see cref="OpenSession"/>, and the whole point of
    /// pausing is that the session object <i>outlives</i> the engine. Without this call the entries stay reachable, so after the application has written to
    /// the database and the Workbench has resumed, the Data Browser keeps serving the pre-pause entity list stamped with the pre-pause
    /// <c>Revision</c> — silently wrong data that looks entirely plausible, which is the single worst outcome this feature could produce.</para>
    /// <para><c>StorageMapService</c> needs no equivalent: its cache is keyed on the <c>DatabaseEngine</c> itself, so replacing the engine invalidates it by
    /// construction.</para>
    /// </remarks>
    public void InvalidateSession(OpenSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _snapshots.Remove(session);
    }

    /// <summary>
    /// Whether this session currently has cached snapshots. Test-only introspection for the pause path (#621).
    /// </summary>
    /// <remarks>
    /// The behaviour that matters — "a resumed session re-reads the database instead of replaying a pre-pause list" — cannot be observed from outside the
    /// service: a surviving cache would answer without ever touching the new engine, which looks identical to a correct answer unless the data changed
    /// underneath. Writing entities from a test would require the schema's static archetype types, which live in a collectible ALC precisely so they do not
    /// leak into the default one. So the mechanism is pinned here directly, and the end-to-end version — browse, let the application write, resume, see the
    /// new rows — is part of the live walk rather than pretended at in a unit test.
    /// </remarks>
    internal bool HasCachedSnapshots(OpenSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return _snapshots.TryGetValue(session, out var perSession) && !perSession.IsEmpty;
    }

    private sealed record ArchetypeSnapshot(EntityId[] Ids, long Revision);

    private readonly record struct PreviewSpec(string TypeName, int FieldId);

    /// <summary>
    /// Returns a page of the archetype's entities, sliced from the cached snapshot. When <paramref name="preview"/> names columns
    /// (comma-separated <c>typeName:fieldId</c>), each row carries those decoded field values in request order.
    /// </summary>
    public EntityPageDto GetEntityPage(Guid sessionId, string archetypeId, int offset, int limit, string preview = null)
    {
        var (open, archId) = ResolveArchetype(sessionId, archetypeId);
        var snapshot = GetSnapshot(open, archId);

        var total = snapshot.Ids.Length;
        var start = Math.Clamp(offset, 0, total);
        var take = Math.Clamp(limit <= 0 ? DefaultLimit : limit, 1, MaxLimit);
        var count = Math.Min(take, total - start);

        var specs = ParsePreview(preview);
        var rows = specs.Length == 0
            ? BuildIdOnlyRows(snapshot.Ids, start, count)
            : BuildPreviewRows(open.Engine.Engine, snapshot.Ids, start, count, specs);

        return new EntityPageDto(archetypeId, snapshot.Revision, total, start, rows, start + count < total);
    }

    private static EntityRowDto[] BuildIdOnlyRows(EntityId[] ids, int start, int count)
    {
        var rows = new EntityRowDto[count];
        for (var i = 0; i < count; i++)
        {
            rows[i] = new EntityRowDto(ids[start + i].RawValue.ToString(), []);
        }
        return rows;
    }

    // Opens each page row and decodes the requested preview fields. The slot→name map and component definitions are constant
    // for the archetype, so they're resolved once (from the first opened row) and reused across the page.
    private static EntityRowDto[] BuildPreviewRows(DatabaseEngine engine, EntityId[] ids, int start, int count, PreviewSpec[] specs)
    {
        var rows = new EntityRowDto[count];
        var definitions = new Dictionary<string, DBComponentDefinition>(StringComparer.Ordinal);
        foreach (var table in engine.GetAllComponentTables())
        {
            definitions[table.Definition.Name] = table.Definition;
        }

        using var tx = engine.CreateReadOnlyTransaction();
        Dictionary<string, int> slotByName = null;

        for (var i = 0; i < count; i++)
        {
            var id = ids[start + i];
            if (!tx.TryOpen(id, out var entity))
            {
                rows[i] = new EntityRowDto(id.RawValue.ToString(), []);
                continue;
            }

            if (slotByName == null)
            {
                slotByName = new Dictionary<string, int>(entity.ComponentCount, StringComparer.Ordinal);
                for (var slot = 0; slot < entity.ComponentCount; slot++)
                {
                    slotByName[entity.GetComponentName(slot)] = slot;
                }
            }

            var values = new ComponentValueDto[specs.Length];
            for (var s = 0; s < specs.Length; s++)
            {
                var spec = specs[s];
                if (slotByName.TryGetValue(spec.TypeName, out var slot)
                    && definitions.TryGetValue(spec.TypeName, out var def)
                    && spec.FieldId >= 0 && spec.FieldId < def.MaxFieldId && def[spec.FieldId] is { } field)
                {
                    values[s] = ComponentValueDecoder.Decode(field, entity.ReadRaw(slot));
                }
                else
                {
                    values[s] = new ComponentValueDto(spec.FieldId, null, "");
                }
            }
            rows[i] = new EntityRowDto(id.RawValue.ToString(), values);
        }
        return rows;
    }

    // Parses "typeName:fieldId,typeName:fieldId" — component names never contain ':' or ',', so a simple split is unambiguous.
    private static PreviewSpec[] ParsePreview(string preview)
    {
        if (string.IsNullOrWhiteSpace(preview))
        {
            return [];
        }

        var parts = preview.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var specs = new List<PreviewSpec>(parts.Length);
        foreach (var part in parts)
        {
            var sep = part.LastIndexOf(':');
            if (sep <= 0 || sep == part.Length - 1)
            {
                continue;
            }
            if (int.TryParse(part[(sep + 1)..], out var fieldId))
            {
                specs.Add(new PreviewSpec(part[..sep], fieldId));
            }
        }
        return specs.ToArray();
    }

    /// <summary>Returns the full component-card detail for one entity.</summary>
    public EntityDetailDto GetEntityDetail(Guid sessionId, string archetypeId, string entityId)
    {
        var (open, _) = ResolveArchetype(sessionId, archetypeId);
        var engine = open.Engine.Engine;

        if (!ulong.TryParse(entityId, out var rawId))
        {
            throw new KeyNotFoundException($"Malformed entity id '{entityId}'.");
        }
        var eid = EntityId.FromRaw(unchecked((long)rawId));

        // name → definition for field-layout decode (component instances report their name; the field offsets/types come from the table).
        var definitions = new Dictionary<string, DBComponentDefinition>(StringComparer.Ordinal);
        foreach (var table in engine.GetAllComponentTables())
        {
            definitions[table.Definition.Name] = table.Definition;
        }

        using var tx = engine.CreateReadOnlyTransaction();
        if (!tx.TryOpen(eid, out var entity))
        {
            throw new KeyNotFoundException($"Entity '{entityId}' not found or not visible.");
        }

        var components = new ComponentInstanceDto[entity.ComponentCount];
        for (var slot = 0; slot < entity.ComponentCount; slot++)
        {
            var name = entity.GetComponentName(slot);
            var enabled = entity.IsEnabled((byte)slot);
            var raw = entity.ReadRaw(slot);

            ComponentValueDto[] fields;
            if (definitions.TryGetValue(name, out var def))
            {
                var ordered = def.FieldsByName.Values.OrderBy(f => f.OffsetInComponentStorage).ToArray();
                fields = new ComponentValueDto[ordered.Length];
                for (var f = 0; f < ordered.Length; f++)
                {
                    fields[f] = ComponentValueDecoder.Decode(ordered[f], raw);
                }
            }
            else
            {
                fields = [];
            }

            components[slot] = new ComponentInstanceDto(name, enabled, fields);
        }

        return new EntityDetailDto(entityId, archetypeId, tx.TSN, components);
    }

    // ── Cohort resolution (#620, design §4.4) ──────────────────────────────

    /// <summary>
    /// Partitions a spawn cohort into the entities the database still holds and those it does not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is safe at all.</b> <c>EntityId</c> is monotonic and never recycled, so an id taken from an old capture either finds the same entity or
    /// finds nothing — it can never resolve to a <i>different</i> one. That single property is what makes the entity bridge shippable where the archetype
    /// and component bridges needed name joins and rename chains.
    /// </para>
    /// <para>
    /// <b>The routing-id cross-check.</b> Every entity id embeds its archetype's durable routing id in the low 16 bits. Before answering, the archetype's
    /// routing id is compared against the ids actually submitted; a mismatch means the caller is asking about a different archetype's entities — the exact
    /// shape of design §5.3's landmine — and is rejected rather than answered with a confident "none of these are alive".
    /// </para>
    /// <para>
    /// Membership is tested against the cached archetype snapshot, so a 200,000-id cohort costs one enumeration (already paid by the entity list) plus one
    /// hash lookup per id — not one <c>TryOpen</c> per id, and certainly not one request per id.
    /// </para>
    /// </remarks>
    /// <param name="sessionId">An Open (file) session.</param>
    /// <param name="archetypeId">Catalog archetype id, as the schema DTO reports it.</param>
    /// <param name="entityIds">Raw entity ids as decimal strings.</param>
    public CohortResolutionDto ResolveCohort(Guid sessionId, string archetypeId, IReadOnlyList<string> entityIds)
    {
        var (open, archId) = ResolveArchetype(sessionId, archetypeId);
        var expectedRouting = open.Engine.Engine.RoutingIdForCatalog(archId);
        var snapshot = GetSnapshot(open, archId);

        var live = new HashSet<long>(snapshot.Ids.Length);
        foreach (var id in snapshot.Ids)
        {
            live.Add((long)id.RawValue);
        }

        var alive = new List<string>();
        var missing = new List<string>();
        var foreignRouting = 0;

        foreach (var raw in entityIds ?? [])
        {
            if (!ulong.TryParse(raw, out var parsed))
            {
                missing.Add(raw);
                continue;
            }

            var value = unchecked((long)parsed);
            if (EntityId.FromRaw(value).ArchetypeId != expectedRouting)
            {
                // Not "destroyed" — not this archetype's entity at all. Counting it as missing would let a mis-joined cohort read as a mass extinction.
                foreignRouting++;
                continue;
            }

            (live.Contains(value) ? alive : missing).Add(raw);
        }

        return new CohortResolutionDto(
            ArchetypeId: archetypeId,
            RoutingId: expectedRouting,
            Revision: snapshot.Revision,
            AliveIds: [.. alive],
            MissingIds: [.. missing],
            ForeignRoutingCount: foreignRouting);
    }

    /// <summary>
    /// Returns a page of rows for an explicit list of entity ids, in the order given, using the same preview decoding as
    /// <see cref="GetEntityPage"/>.
    /// </summary>
    /// <remarks>
    /// The Data Browser's normal page is a window onto the archetype's whole snapshot; this one is a window onto a caller-supplied set, which is what makes
    /// "open these 830 in the Data Browser" a real navigation rather than a filter the user has to reconstruct by hand.
    /// </remarks>
    public EntityPageDto GetEntityPageForIds(Guid sessionId, string archetypeId, IReadOnlyList<string> entityIds, int offset, int limit, string preview = null)
    {
        var (open, archId) = ResolveArchetype(sessionId, archetypeId);
        var snapshot = GetSnapshot(open, archId);

        var parsed = new List<EntityId>(entityIds?.Count ?? 0);
        foreach (var raw in entityIds ?? [])
        {
            if (ulong.TryParse(raw, out var value))
            {
                parsed.Add(EntityId.FromRaw(unchecked((long)value)));
            }
        }

        var total = parsed.Count;
        var start = Math.Clamp(offset, 0, total);
        var take = Math.Clamp(limit <= 0 ? DefaultLimit : limit, 1, MaxLimit);
        var count = Math.Min(take, total - start);

        var window = parsed.GetRange(start, count).ToArray();
        var specs = ParsePreview(preview);
        var rows = specs.Length == 0
            ? BuildIdOnlyRows(window, 0, count)
            : BuildPreviewRows(open.Engine.Engine, window, 0, count, specs);

        // The snapshot revision travels with the page so the caller can say which HEAD the rows were read against — a cohort is drawn from a past capture,
        // and the values in it are emphatically current.
        return new EntityPageDto(archetypeId, snapshot.Revision, total, start, rows, start + count < total);
    }

    // ── Resolution + snapshot ──────────────────────────────────────────────

    private (OpenSession Open, ushort ArchetypeId) ResolveArchetype(Guid sessionId, string archetypeId)
    {
        var open = RequireOpenEngine(sessionId);
        if (!ushort.TryParse(archetypeId, out var archId) || ArchetypeRegistry.GetMetadata(archId) == null)
        {
            throw new KeyNotFoundException($"Archetype '{archetypeId}' is not registered.");
        }
        return (open, archId);
    }

    private OpenSession RequireOpenEngine(Guid sessionId)
    {
        if (!_sessions.TryGet(sessionId, out var session))
        {
            throw new SessionNotFoundException(sessionId);
        }
        if (session is not OpenSession open)
        {
            // Trace / live-Attach sessions have no in-process engine to read entities from.
            throw new DataUnavailableException(sessionId, session.Kind.ToString());
        }
        if (open.IsPaused)
        {
            // Paused (#621): the engine is gone, so there are no entities to read. 409 rather than 400 — this resolves itself when the holder exits, and the
            // client should retry rather than treat it as a permanent wrong-session-kind error.
            throw new WorkbenchException(409, "database_paused",
                "The Data Browser needs the database, which this session has released"
                + (open.PausedBy is { } h ? $" to {h.Describe()}" : "") + ".");
        }
        if (open.Engine.State != SchemaCompatibility.State.Ready)
        {
            throw new SchemaIncompatibleException(sessionId, open.Engine.State.ToString());
        }
        return open;
    }

    private ArchetypeSnapshot GetSnapshot(OpenSession open, ushort archetypeId)
    {
        var perSession = _snapshots.GetValue(open, static _ => new ConcurrentDictionary<ushort, Lazy<ArchetypeSnapshot>>());
        // The GetOrAdd factory only constructs the (cheap) Lazy; even if it runs more than once concurrently, a single Lazy
        // wins the slot and its Value (the actual enumeration) executes exactly once under LazyThreadSafetyMode default.
        var lazy = perSession.GetOrAdd(archetypeId, static (id, o) => new Lazy<ArchetypeSnapshot>(() => Enumerate(o.Engine.Engine, id)), open);
        return lazy.Value;
    }

    private static ArchetypeSnapshot Enumerate(DatabaseEngine engine, ushort archetypeId)
    {
        using var tx = engine.CreateReadOnlyTransaction();
        // archetypeId here is the catalog id (from the schema DTO); EnumerateArchetypeEntities expects the per-DB routing id.
        var ids = tx.EnumerateArchetypeEntities(engine.RoutingIdForCatalog(archetypeId));
        return new ArchetypeSnapshot(ids.ToArray(), tx.TSN);
    }
}

/// <summary>The session exists but is not an Open (file) session, so it has no in-process engine to browse. Controller → 404.</summary>
public sealed class DataUnavailableException(Guid sessionId, string sessionKind)
    : Exception($"Session {sessionId} ({sessionKind}) has no entity data — only Open (file) sessions support the Data Browser.")
{
    public Guid SessionId { get; } = sessionId;
    public string SessionKind { get; } = sessionKind;
}

/// <summary>The Open session's loaded schema is incompatible with the database file, so decoding would be unsafe. Controller → 409.</summary>
public sealed class SchemaIncompatibleException(Guid sessionId, string state)
    : Exception($"Session {sessionId} schema state is '{state}' — resolve the schema mismatch before browsing data.")
{
    public Guid SessionId { get; } = sessionId;
    public string State { get; } = state;
}
