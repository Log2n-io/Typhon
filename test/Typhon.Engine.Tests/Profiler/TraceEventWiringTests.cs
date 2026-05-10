using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Typhon.Engine.Profiler;
using Typhon.Profiler;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// Build-time guards on the trace-event encode/decode contract.
/// <para>
/// 1. <see cref="NoTwoTraceEventKindsShareSameNumericValue"/> catches enum-value collisions in
///    <see cref="TraceEventKind"/>. The 2026-05-10 fix to <see cref="TraceEventKind.NamedSpan"/> (was 200,
///    collided with <see cref="TraceEventKind.EcsQueryMaskAnd"/>) is the regression this guards against.
/// </para>
/// <para>
/// 2. <see cref="EveryEncodedKindHasTypedDecoderOrAcknowledgedGap"/> catches encoder/decoder drift: every
///    <c>[TraceEvent(TraceEventKind.X, ...)]</c> annotation on a payload struct in <c>Typhon.Engine</c> must
///    either (a) be wired to a typed decoder in <see cref="RecordDecoder"/>'s switch, or (b) be explicitly
///    acknowledged in <see cref="KnownEncoderOnlyGaps"/> below as a kind that intentionally falls through to
///    the generic-span fallback. Adding a new event without doing one or the other fails the build.
/// </para>
/// </summary>
/// <remarks>
/// See <c>claude/research/PublicVsInternalApiClassification.md</c> §8 for the audit that motivated this fixture.
/// </remarks>
[TestFixture]
public sealed class TraceEventWiringTests
{
    [Test]
    public void NoTwoTraceEventKindsShareSameNumericValue()
    {
        var collisions = Enum.GetValues<TraceEventKind>()
            .GroupBy(v => (int)v)
            .Where(g => g.Count() > 1)
            .Select(g => $"  value {(int)g.Key}: {string.Join(", ", g.Select(v => v.ToString()))}")
            .ToList();

        Assert.That(collisions, Is.Empty,
            "TraceEventKind has duplicate numeric value(s) — wire-format ambiguity. "
            + "Pick a fresh value (next free is one above the highest assigned). "
            + "Bump TraceFileHeader.CurrentVersion and TraceFileCache.CurrentChunkerVersion if the change "
            + "affects the on-disk format.\n"
            + string.Join("\n", collisions));
    }

    [Test]
    public void EveryEncodedKindHasTypedDecoderOrAcknowledgedGap()
    {
        var encoderKinds = ReflectEncoderKinds();

        // Sanity guard: a regression in the reflection pass (e.g., generator-emitted attribute split across
        // assemblies, namespace rename) would silently zero this out and make the test useless. The engine
        // has dozens of [TraceEvent] payloads today; refuse to pass if reflection finds suspiciously few.
        Assert.That(encoderKinds.Count, Is.GreaterThan(50),
            $"Reflection found only {encoderKinds.Count} [TraceEvent] kinds in Typhon.Engine — the encoder "
            + "audit logic in this test is likely broken (attribute type identity mismatch between assemblies?). "
            + "Investigate ReflectEncoderKinds() before trusting any pass result here.");

        var unexpected = encoderKinds
            .Except(TypedDecodedKinds)
            .Except(KnownEncoderOnlyGaps)
            .OrderBy(k => (int)k)
            .ToList();

        if (unexpected.Count == 0)
        {
            return;
        }

        Assert.Fail(
            "Found [TraceEvent(...)] kind(s) in Typhon.Engine without a matching typed decoder in "
            + "src/Typhon.Profiler/RecordDecoder.cs:\n"
            + string.Join("\n", unexpected.Select(k => $"  {k} = {(int)k}"))
            + "\n\nResolve by either:\n"
            + "  (a) Adding a switch arm in RecordDecoder.DecodeRecord (or DecodeInstant for instant kinds)\n"
            + "      that calls a typed decoder, then appending the kind to TypedDecodedKinds in this fixture.\n"
            + "  (b) Acknowledging the gap by adding the kind to KnownEncoderOnlyGaps in this fixture, with a\n"
            + "      one-line comment naming the audit reference or the reason the typed payload is not needed.\n"
            + "\nAudit reference: claude/research/PublicVsInternalApiClassification.md §8.");
    }

    /// <summary>
    /// Reflect every <c>[TraceEvent(TraceEventKind.X, ...)]</c> annotation on a payload struct in the
    /// <c>Typhon.Engine</c> assembly and return the set of declared kinds.
    /// <para>
    /// Uses <see cref="MemberInfo.CustomAttributes"/> (metadata-level) rather than
    /// <see cref="MemberInfo.GetCustomAttribute(Type)"/> because <c>TraceEventAttribute</c> is emitted by a
    /// source generator into every consuming assembly: the engine has its own copy of the attribute, the test
    /// project has its own copy, and they are distinct <see cref="Type"/> instances even though the FullName
    /// matches. Type-identity-based matching silently misses every annotation in this setup; metadata-level
    /// matching by FullName works.
    /// </para>
    /// </summary>
    private static IReadOnlyCollection<TraceEventKind> ReflectEncoderKinds()
    {
        const string attrFullName = "Typhon.Engine.Profiler.TraceEventAttribute";
        var engineAssembly = typeof(TyphonProfiler).Assembly;
        var kinds = new HashSet<TraceEventKind>();

        foreach (var t in engineAssembly.GetTypes())
        {
            foreach (var attrData in t.CustomAttributes)
            {
                if (attrData.AttributeType.FullName != attrFullName)
                {
                    continue;
                }
                if (attrData.ConstructorArguments.Count == 0)
                {
                    continue;
                }
                // First ctor arg is the TraceEventKind. The metadata layer surfaces enum values as their
                // underlying integral type, so cast through int32.
                var raw = attrData.ConstructorArguments[0].Value;
                if (raw is null)
                {
                    continue;
                }
                kinds.Add((TraceEventKind)Convert.ToInt32(raw));
            }
        }

        return kinds;
    }

    /// <summary>
    /// Mirror of the typed dispatch in <c>src/Typhon.Profiler/RecordDecoder.cs</c>. Update when adding or
    /// removing a switch arm in <c>DecodeRecord</c> / <c>DecodeInstant</c>.
    /// <para>
    /// This list is hand-maintained — there is no programmatic introspection of switch expressions in C#.
    /// The cost is one line per new event when wiring a decoder; the benefit is a build-time reminder that
    /// every encoder needs a decoder.
    /// </para>
    /// </summary>
    private static readonly HashSet<TraceEventKind> TypedDecodedKinds =
    [
        // DecodeInstant — instant kinds with typed decoders
        TraceEventKind.TickStart,
        TraceEventKind.TickEnd,
        TraceEventKind.PhaseStart,
        TraceEventKind.PhaseEnd,
        TraceEventKind.SystemReady,
        TraceEventKind.SystemSkipped,
        TraceEventKind.MemoryAllocEvent,
        TraceEventKind.PerTickSnapshot,
        TraceEventKind.GcStart,
        TraceEventKind.GcEnd,
        TraceEventKind.ThreadInfo,

        // DecodeRecord — span kinds with typed decoders
        TraceEventKind.SchedulerChunk,
        TraceEventKind.SchedulerSystemArchetype,

        TraceEventKind.BTreeInsert,
        TraceEventKind.BTreeDelete,
        TraceEventKind.BTreeNodeSplit,
        TraceEventKind.BTreeNodeMerge,

        TraceEventKind.TransactionCommit,
        TraceEventKind.TransactionRollback,
        TraceEventKind.TransactionCommitComponent,
        TraceEventKind.TransactionPersist,

        TraceEventKind.EcsSpawn,
        TraceEventKind.EcsDestroy,
        TraceEventKind.EcsQueryExecute,
        TraceEventKind.EcsQueryCount,
        TraceEventKind.EcsQueryAny,
        TraceEventKind.EcsViewRefresh,

        TraceEventKind.PageCacheFetch,
        TraceEventKind.PageCacheDiskRead,
        TraceEventKind.PageCacheDiskWrite,
        TraceEventKind.PageCacheAllocatePage,
        TraceEventKind.PageCacheFlush,
        TraceEventKind.PageEvicted,
        TraceEventKind.PageCacheDiskReadCompleted,
        TraceEventKind.PageCacheDiskWriteCompleted,
        TraceEventKind.PageCacheFlushCompleted,
        TraceEventKind.PageCacheBackpressure,

        TraceEventKind.ClusterMigration,
        TraceEventKind.RuntimePhaseSpan,

        TraceEventKind.WalFlush,
        TraceEventKind.WalSegmentRotate,
        TraceEventKind.WalWait,

        TraceEventKind.CheckpointCycle,
        TraceEventKind.CheckpointCollect,
        TraceEventKind.CheckpointWrite,
        TraceEventKind.CheckpointFsync,
        TraceEventKind.CheckpointTransition,
        TraceEventKind.CheckpointRecycle,

        TraceEventKind.StatisticsRebuild,
    ];

    /// <summary>
    /// Kinds with <c>[TraceEvent]</c> annotations that have no typed decoder today and fall through to
    /// <c>RecordDecoder.DecodeGenericSpan</c> (header only, no typed payload). Each entry should eventually
    /// move to <see cref="TypedDecodedKinds"/> when a typed decoder is added, or be removed if the encoder
    /// is dropped.
    /// <para>
    /// Snapshot 2026-05-10: 66 entries (the existing gap surface at the time the audit was committed).
    /// Audit reference: <c>claude/research/PublicVsInternalApiClassification.md</c> §8.1.B / §8.2 action plan items 3 and 5–7.
    /// </para>
    /// </summary>
    private static readonly HashSet<TraceEventKind> KnownEncoderOnlyGaps =
    [
        // Spatial query — only header decoded today; payloads (target tree, AABB, radius, etc.) lost.
        TraceEventKind.SpatialQueryAabb,
        TraceEventKind.SpatialQueryRadius,
        TraceEventKind.SpatialQueryRay,
        TraceEventKind.SpatialQueryFrustum,
        TraceEventKind.SpatialQueryKnn,
        TraceEventKind.SpatialQueryCount,

        // Spatial R-tree — operation kind decoded, but node/AABB/parent payload lost.
        TraceEventKind.SpatialRTreeInsert,
        TraceEventKind.SpatialRTreeRemove,
        TraceEventKind.SpatialRTreeNodeSplit,
        TraceEventKind.SpatialRTreeBulkLoad,

        // Spatial maintenance + trigger — header only.
        TraceEventKind.SpatialTierIndexRebuild,
        TraceEventKind.SpatialMaintainInsert,
        TraceEventKind.SpatialMaintainUpdateSlowPath,
        TraceEventKind.SpatialTriggerEval,

        // Scheduler spans (Phase 4) — most decoders missing.
        TraceEventKind.SchedulerSystemSingleThreaded,
        TraceEventKind.SchedulerWorkerIdle,
        TraceEventKind.SchedulerWorkerBetweenTick,
        TraceEventKind.SchedulerDependencyFanOut,
        TraceEventKind.SchedulerGraphBuild,
        TraceEventKind.SchedulerGraphRebuild,

        // Runtime spans — phase + storage walk.
        TraceEventKind.RuntimeTransactionLifecycle,
        TraceEventKind.RuntimeSubscriptionOutputExecute,
        TraceEventKind.StoragePageCacheDirtyWalk,

        // Data transactions — encoders emit, decoder layer covers only kind 176 (Conflict).
        TraceEventKind.DataTransactionInit,
        TraceEventKind.DataTransactionPrepare,
        TraceEventKind.DataTransactionValidate,
        TraceEventKind.DataTransactionCleanup,

        // Data MVCC — decoder covers only 178 (ChainWalk).
        TraceEventKind.DataMvccVersionCleanup,

        // Data index — decoder covers neither range-scan nor bulk-insert today.
        TraceEventKind.DataIndexBTreeRangeScan,
        TraceEventKind.DataIndexBTreeBulkInsert,

        // Query pipeline — parse / plan / execute spans, header only.
        TraceEventKind.QueryParse,
        TraceEventKind.QueryParseDnf,
        TraceEventKind.QueryPlan,
        TraceEventKind.QueryEstimate,
        TraceEventKind.QueryPlanSort,
        TraceEventKind.QueryExecuteIndexScan,
        TraceEventKind.QueryExecuteIterate,
        TraceEventKind.QueryExecuteFilter,
        TraceEventKind.QueryExecutePagination,
        TraceEventKind.QueryCount,

        // ECS query construction — header only.
        TraceEventKind.EcsQueryConstruct,
        TraceEventKind.EcsQuerySubtreeExpand,

        // ECS view refresh path — header only.
        TraceEventKind.EcsViewRefreshPull,
        TraceEventKind.EcsViewIncrementalDrain,
        TraceEventKind.EcsViewRefreshFull,
        TraceEventKind.EcsViewRefreshFullOr,

        // Durability WAL spans — encoders emit, no typed decoders for queue/buffer/signal/backpressure.
        TraceEventKind.DurabilityWalQueueDrain,
        TraceEventKind.DurabilityWalOsWrite,
        TraceEventKind.DurabilityWalSignal,
        TraceEventKind.DurabilityWalBuffer,
        TraceEventKind.DurabilityWalBackpressure,

        // Durability checkpoint spans (write batch / backpressure / sleep).
        TraceEventKind.DurabilityCheckpointWriteBatch,
        TraceEventKind.DurabilityCheckpointBackpressure,
        TraceEventKind.DurabilityCheckpointSleep,

        // Durability recovery spans — Phase 8 producer wired ahead of decoder layer.
        TraceEventKind.DurabilityRecoveryDiscover,
        TraceEventKind.DurabilityRecoverySegment,
        TraceEventKind.DurabilityRecoveryFpi,
        TraceEventKind.DurabilityRecoveryRedo,
        TraceEventKind.DurabilityRecoveryUndo,
        TraceEventKind.DurabilityRecoveryTickFence,

        // Subscription dispatch (Phase 9) — encoders defined; RuntimeSubscriptionEventCodec is a stub
        // per Phase 9 deferral. See audit §8.1.B.
        TraceEventKind.RuntimeSubscriptionSubscriber,
        TraceEventKind.RuntimeSubscriptionDeltaBuild,
        TraceEventKind.RuntimeSubscriptionDeltaSerialize,
        TraceEventKind.RuntimeSubscriptionTransitionBeginSync,
        TraceEventKind.RuntimeSubscriptionOutputCleanup,
        TraceEventKind.RuntimeSubscriptionDeltaDirtyBitmapSupplement,
    ];
}
