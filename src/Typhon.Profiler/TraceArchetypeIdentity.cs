using System;
using System.Collections.Generic;

namespace Typhon.Profiler;

/// <summary>
/// The single sanctioned way to resolve an archetype id read out of a trace. Built once from a trace's header + archetype table; every consumer that needs to
/// know "which archetype is this?" goes through here rather than touching <see cref="ArchetypeRecord.RoutingId"/> or, worse, comparing raw ids.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>Why this type exists.</b> A trace carries archetype identity in two incompatible <c>ushort</c> spaces (see the remarks on
/// <see cref="ArchetypeRecord"/>): the per-process <b>catalog id</b> in events and in this table, and the per-database <b>routing id</b> in the low 16 bits of
/// every <c>EntityId</c>. Writing <c>entityId &amp; 0xFFFF == spawnEvent.ArchetypeId</c> compiles, reads naturally, and is wrong for every archetype whose
/// registration order differs from its persisted routing order — while being <i>right</i> in any freshly-created test database, where the two happen to
/// coincide. Funnelling resolution through one type is what keeps that mistake from being re-invented in each new consumer.
/// </para>
/// <para>
/// <b>Names are the safe join key; routing ids are the precise one.</b> <see cref="TryGetName"/> always works. <see cref="TryGetRoutingId"/> deliberately
/// fails closed — it returns <c>false</c> when the capture observed more than one live engine (<see cref="TraceHeaderFlags.MultipleEnginesObserved"/>, design
/// D-9), when the trace never knew the id, and when the archetype is simply not in the table. A caller that cannot get a routing id should fall back to a
/// name join or omit the bridge entirely; it must never fall back to the catalog id.
/// </para>
/// </remarks>
public sealed class TraceArchetypeIdentity
{
    private readonly string[] _nameByCatalogId;
    private readonly ushort[] _routingByCatalogId;

    /// <summary>
    /// <c>false</c> when this trace carries no usable routing ids at all — either it observed multiple engines (D-9) or it was written with no engine
    /// attached. Consumers can check this once to decide whether a routing-id-based bridge is offered at all, rather than probing per archetype.
    /// </summary>
    public bool RoutingIdsAvailable { get; }

    /// <summary>Builds the resolver from a trace's header and archetype table.</summary>
    /// <param name="header">The trace header — consulted for <see cref="TraceHeaderFlags.MultipleEnginesObserved"/>.</param>
    /// <param name="archetypes">The trace's archetype table, as returned by <see cref="TraceFileReader.ReadArchetypes"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="archetypes"/> is <c>null</c>.</exception>
    public TraceArchetypeIdentity(in TraceFileHeader header, IReadOnlyList<ArchetypeRecord> archetypes)
    {
        ArgumentNullException.ThrowIfNull(archetypes);

        var multiEngine = header.MultipleEnginesObserved;

        // Catalog ids are dense and capped at 4095, so a flat array indexed by id beats a dictionary on both lookup cost and allocation. Sized to the highest
        // id actually present rather than the cap — a typical schema uses a few dozen.
        var maxId = -1;
        for (var i = 0; i < archetypes.Count; i++)
        {
            var record = archetypes[i];
            if (record != null && record.ArchetypeId > maxId)
            {
                maxId = record.ArchetypeId;
            }
        }

        _nameByCatalogId = maxId < 0 ? [] : new string[maxId + 1];
        _routingByCatalogId = maxId < 0 ? [] : new ushort[maxId + 1];
        _routingByCatalogId.AsSpan().Fill(ArchetypeRecord.UnknownRoutingId);

        var anyRouting = false;
        for (var i = 0; i < archetypes.Count; i++)
        {
            var record = archetypes[i];
            if (record == null)
            {
                continue;
            }
            _nameByCatalogId[record.ArchetypeId] = record.Name;

            // Under D-9 the on-disk ids were already patched to the sentinel at close; re-applying the rule here means an in-memory table that never touched
            // disk (a live attach, a hand-built fixture) degrades identically. The flag is the contract, not the bytes.
            if (multiEngine || record.RoutingId == ArchetypeRecord.UnknownRoutingId)
            {
                continue;
            }
            _routingByCatalogId[record.ArchetypeId] = record.RoutingId;
            anyRouting = true;
        }

        RoutingIdsAvailable = anyRouting;
    }

    /// <summary>
    /// Resolves a trace catalog id to its archetype name — the drift-tolerant join key, and the one that always works. Returns <c>false</c> for an id the
    /// trace's archetype table does not describe.
    /// </summary>
    /// <param name="catalogId">A catalog id as carried by trace events and <see cref="ArchetypeRecord.ArchetypeId"/>.</param>
    /// <param name="name">Receives the archetype name, or <c>null</c>.</param>
    public bool TryGetName(ushort catalogId, out string name)
    {
        name = catalogId < (uint)_nameByCatalogId.Length ? _nameByCatalogId[catalogId] : null;
        return name != null;
    }

    /// <summary>
    /// Resolves a trace catalog id to the database's durable routing id — the identity embedded in every <c>EntityId</c>. <b>Fails closed:</b> returns
    /// <c>false</c> whenever the answer would be a guess, including the multi-engine case where the trace deliberately carries no routing ids at all (D-9).
    /// </summary>
    /// <param name="catalogId">A catalog id as carried by trace events and <see cref="ArchetypeRecord.ArchetypeId"/>.</param>
    /// <param name="routingId">Receives the routing id, or <see cref="ArchetypeRecord.UnknownRoutingId"/>.</param>
    public bool TryGetRoutingId(ushort catalogId, out ushort routingId)
    {
        routingId = catalogId < (uint)_routingByCatalogId.Length ? _routingByCatalogId[catalogId] : ArchetypeRecord.UnknownRoutingId;
        return routingId != ArchetypeRecord.UnknownRoutingId;
    }
}
