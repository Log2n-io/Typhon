using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// ECS-aware navigation query builder. Handles FK-based joins between archetype entities.
/// Created via <see cref="EcsQuery{TArchetype}.NavigateField{TSource,TTarget}"/>.
/// </summary>
[PublicAPI]
#pragma warning disable TYPHON005 // Builder borrows Transaction, doesn't own it
public unsafe class EcsNavigationQueryBuilder<TSourceArch, TSource, TTarget> where TSourceArch : class where TSource : unmanaged where TTarget : unmanaged
{
    private readonly NavigationQueryBuilder<TSource, TTarget> _inner;
    private readonly EcsQuery<TSourceArch> _query;
    private readonly Transaction _tx;

    internal EcsNavigationQueryBuilder(EcsQuery<TSourceArch> query, Transaction tx, string fkFieldName)
    {
        _query = query;
        _tx = tx;
        _inner = new NavigationQueryBuilder<TSource, TTarget>(tx.DBE, fkFieldName);
    }

    /// <summary>
    /// Filter by source and target predicates. Source parameters come first, target second.
    /// Only indexed fields are supported (same constraint as navigation views).
    /// </summary>
    public EcsNavigationQueryBuilder<TSourceArch, TSource, TTarget> Where(Expression<Func<TSource, TTarget, bool>> predicate)
    {
        _inner.Where(predicate);
        return this;
    }

    /// <summary>Create an incremental navigation view. Registers with both source and target ViewRegistries.</summary>
    public ViewBase ToView(int bufferCapacity = ViewDeltaRingBuffer.DefaultCapacity) => _inner.ToView(bufferCapacity);

    /// <summary>Execute the navigation query and return matching source entity IDs.</summary>
    public HashSet<EntityId> Execute() => ExecuteViaEntityMap();    // All entities use EntityMap-based enumeration (PK B+Tree removed)

    private HashSet<EntityId> ExecuteViaEntityMap()
    {
        var (sourceCT, _, fkField, sourceEvals, targetEvals) = _inner.ResolveForExternalExecution();
        var result = new HashSet<EntityId>();

        // Find the FK index on the source table
        var fkIndexInfo = PipelineExecutor.FindFKIndex(sourceCT, fkField.OffsetInComponentStorage);
        var fkIndex = (BTree<long, PersistentStore>)fkIndexInfo.Index;

        // Collect target entity PKs by scanning all archetype EntityMaps that include TTarget.
        // Use a separate scope for the EntityMap scan (avoids nested epoch guard issues with FK lookup).
        var targetPKs = new List<long>();
        {
            var targetTypeId = ArchetypeRegistry.GetComponentTypeId<TTarget>();
            foreach (var meta in ArchetypeRegistry.GetAllArchetypes())
            {
                if (!meta.TryGetSlot(targetTypeId, out _))
                {
                    continue;
                }

                var engineState = _tx.DBE._archetypeStates[meta.ArchetypeId];
                if (engineState?.EntityMap == null)
                {
                    continue;
                }

                using var scanGuard = EpochGuard.Enter(_tx.DBE.EpochManager);
                var accessor = engineState.EntityMap.Segment.CreateChunkAccessor();
                var collector = new TargetCollector
                {
                    ArchetypeId = meta.ArchetypeId,
                    TargetPKs = targetPKs,
                    TSN = _tx.TSN,
                };
                engineState.EntityMap.ForEachEntry(ref accessor, ref collector);
                accessor.Dispose();
            }
        }

        // FK reverse lookup on collected target PKs
        using var guard = EpochGuard.Enter(_tx.DBE.EpochManager);
        var compRevAccessor = sourceCT.CompRevTableSegment.CreateChunkAccessor();

        try
        {
            foreach (var targetPK in targetPKs)
            {
                // Evaluate target predicates
                if (targetEvals.Length > 0 && !PipelineExecutor.EvaluateFilters<TTarget>(targetEvals, _tx, targetPK))
                {
                    continue;
                }

                // Reverse lookup: find source entities that have FK == targetPK
                var enumerator = fkIndex.EnumerateRangeMultiple(targetPK, targetPK);
                try
                {
                    while (enumerator.MoveNextKey())
                    {
                        do
                        {
                            var values = enumerator.CurrentValues;
                            for (var j = 0; j < values.Length; j++)
                            {
                                ref var header = ref compRevAccessor.GetChunk<CompRevStorageHeader>(values[j]);
                                var sourcePK = header.EntityPK;
                                var sourceEntityId = EntityId.FromRaw(sourcePK);

                                // Archetype mask filter
                                if (!_query.MaskTestPublic(sourceEntityId.ArchetypeId))
                                {
                                    continue;
                                }

                                // Evaluate source predicates
                                if (sourceEvals.Length > 0 && !PipelineExecutor.EvaluateFilters<TSource>(sourceEvals, _tx, sourcePK))
                                {
                                    continue;
                                }

                                result.Add(sourceEntityId);
                            }
                        } while (enumerator.NextChunk());
                    }
                }
                finally
                {
                    enumerator.Dispose();
                }
            }
        }
        finally
        {
            compRevAccessor.Dispose();
        }

        return result;
    }

    /// <summary>Collects visible entity PKs from EntityMap enumeration.</summary>
    private struct TargetCollector : RawValuePagedHashMap<long, PersistentStore>.IEntryAction<long>
    {
        public ushort ArchetypeId;
        public List<long> TargetPKs;
        public long TSN;

        public bool Process(long key, byte* value)
        {
            ref var header = ref EntityRecordAccessor.GetHeader(value);
            bool visible = header.IsVisibleAt(TSN);
            if (visible)
            {
                var entityId = new EntityId(key, ArchetypeId);
                TargetPKs.Add((long)entityId.RawValue);
            }
            return true; // continue enumeration (ForEachEntry convention: true=continue, false=stop)
        }
    }

    /// <summary>Count matching source entities.</summary>
    public int Count() => Execute().Count;

    /// <summary>Test if any source entity matches.</summary>
    public bool Any() => Execute().Count > 0;
}
