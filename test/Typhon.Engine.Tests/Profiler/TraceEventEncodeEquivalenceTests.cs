using System;
using NUnit.Framework;
using Typhon.Engine.Profiler;
using Typhon.Profiler;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// Per-kind equivalence tests gating the TyphonEvent emission refactor (#294).
///
/// For every event ref struct, we assert: the bytes produced by <c>struct.EncodeTo(...)</c> match the bytes produced by calling the underlying
/// codec's <c>Encode</c> method directly with the same field values. This catches argument-order swaps and field-thread-through bugs in the
/// struct layer, which the existing codec round-trip tests do not exercise (they call codecs directly, bypassing the ref struct).
///
/// During Phase 1 (TraceSpanHeader migration) and Phase 2 (source generator), the codec layer is untouched, so codec-equivalence is the right
/// gate. After Phase 3 (codec consolidation) some codec methods are removed; those kinds will be retested against frozen golden bytes captured
/// from a Phase-2 baseline.
///
/// All test methods below are produced by <c>./gen_equivalence_tests.py</c>. Re-run the script after Phase 1/2/3 changes via:
///   <c>python3 test/Typhon.Engine.Tests/Profiler/gen_equivalence_tests.py</c>
/// and replace the section between <c>// ── GENERATED-BEGIN ──</c> and <c>// ── GENERATED-END ──</c>.
/// </summary>
[TestFixture]
public class TraceEventEncodeEquivalenceTests
{
    // Deterministic field values, shared across all kinds. Sentinel values chosen to be non-zero, distinct, and easy to spot in a hex dump.
    private const byte ThreadSlot = 7;
    private const long StartTs = 0x1111_2222_3333_4444L;
    private const long EndTs = 0x1111_2222_3333_5555L;
    private const ulong SpanId = 0xAAAA_BBBB_CCCC_DDDDUL;
    private const ulong ParentSpanId = 0x1234_5678_9ABC_DEF0UL;
    private const ulong TraceIdHi = 0x0F0F_0F0F_0F0F_0F0FUL;
    private const ulong TraceIdLo = 0xF0F0_F0F0_F0F0_F0F0UL;

    // ── GENERATED-BEGIN ──
    [Test]
    public void BTreeInsertEvent_StructEncode_MatchesCodec()
    {
        var ev = new BTreeInsertEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3500280744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F0");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void BTreeDeleteEvent_StructEncode_MatchesCodec()
    {
        var ev = new BTreeDeleteEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3500290744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F0");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void BTreeNodeSplitEvent_StructEncode_MatchesCodec()
    {
        var ev = new BTreeNodeSplitEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("35002A0744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F0");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void BTreeNodeMergeEvent_StructEncode_MatchesCodec()
    {
        var ev = new BTreeNodeMergeEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("35002B0744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F0");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void CheckpointCycleEvent_StructEncode_MatchesCodec()
    {
        var ev = new CheckpointCycleEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            TargetLsn = 1001L,
            Reason = 0x12,
            DirtyPageCount = 303,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("4300530744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F0E90300000000000012012F010000");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void CheckpointCollectEvent_StructEncode_MatchesCodec()
    {
        var ev = new CheckpointCollectEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3500540744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F0");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void CheckpointWriteEvent_StructEncode_MatchesCodec()
    {
        var ev = new CheckpointWriteEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            WrittenCount = 101,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3A00550744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F00165000000");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void CheckpointFsyncEvent_StructEncode_MatchesCodec()
    {
        var ev = new CheckpointFsyncEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3500560744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F0");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void CheckpointTransitionEvent_StructEncode_MatchesCodec()
    {
        var ev = new CheckpointTransitionEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            TransitionedCount = 101,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3A00570744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F00165000000");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void CheckpointRecycleEvent_StructEncode_MatchesCodec()
    {
        var ev = new CheckpointRecycleEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            RecycledCount = 101,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3A00580744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F00165000000");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void ClusterMigrationEvent_StructEncode_MatchesCodec()
    {
        var ev = new ClusterMigrationEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            ArchetypeId = 0x1200,
            MigrationCount = 202,
            ComponentCount = 303,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3F003C0744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F00012CA0000002F010000");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void DataTransactionInitEvent_StructEncode_MatchesCodec()
    {
        var ev = new DataTransactionInitEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            Tsn = 1001L,
            UowId = 0x1300,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3F00AD0744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F0E9030000000000000013");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void DataTransactionPrepareEvent_StructEncode_MatchesCodec()
    {
        var ev = new DataTransactionPrepareEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            Tsn = 1001L,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3D00AE0744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F0E903000000000000");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void DataTransactionValidateEvent_StructEncode_MatchesCodec()
    {
        var ev = new DataTransactionValidateEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            Tsn = 1001L,
            EntryCount = 202,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("4100AF0744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F0E903000000000000CA000000");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void DataTransactionCleanupEvent_StructEncode_MatchesCodec()
    {
        var ev = new DataTransactionCleanupEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            Tsn = 1001L,
            EntityCount = 202,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("4100B10744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F0E903000000000000CA000000");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void DataMvccVersionCleanupEvent_StructEncode_MatchesCodec()
    {
        var ev = new DataMvccVersionCleanupEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            Pk = 1001L,
            EntriesFreed = 0x1300,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3F00B30744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F0E9030000000000000013");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void DataIndexBTreeRangeScanEvent_StructEncode_MatchesCodec()
    {
        var ev = new DataIndexBTreeRangeScanEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            ResultCount = 101,
            RestartCount = 0x12,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3A00B50744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F06500000012");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void DataIndexBTreeBulkInsertEvent_StructEncode_MatchesCodec()
    {
        var ev = new DataIndexBTreeBulkInsertEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            BufferId = 101,
            EntryCount = 202,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3D00B80744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F065000000CA000000");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void DurabilityWalQueueDrainEvent_StructEncode_MatchesCodec()
    {
        var ev = new DurabilityWalQueueDrainEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            BytesAligned = 101,
            FrameCount = 202,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3D00D60744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F065000000CA000000");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void DurabilityWalOsWriteEvent_StructEncode_MatchesCodec()
    {
        var ev = new DurabilityWalOsWriteEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            BytesAligned = 101,
            FrameCount = 202,
            HighLsn = 3003L,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("4500D70744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F065000000CA000000BB0B000000000000");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void DurabilityWalSignalEvent_StructEncode_MatchesCodec()
    {
        var ev = new DurabilityWalSignalEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            HighLsn = 1001L,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3D00D80744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F0E903000000000000");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void DurabilityWalBufferEvent_StructEncode_MatchesCodec()
    {
        var ev = new DurabilityWalBufferEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            BytesAligned = 101,
            Pad = 202,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3D00DB0744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F065000000CA000000");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void DurabilityWalBackpressureEvent_StructEncode_MatchesCodec()
    {
        var ev = new DurabilityWalBackpressureEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            WaitUs = 1001u,
            ProducerThread = 202,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3D00DD0744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F0E9030000CA000000");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void DurabilityCheckpointWriteBatchEvent_StructEncode_MatchesCodec()
    {
        var ev = new DurabilityCheckpointWriteBatchEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            WriteBatchSize = 101,
            StagingAllocated = 202,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3D00DE0744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F065000000CA000000");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void DurabilityCheckpointBackpressureEvent_StructEncode_MatchesCodec()
    {
        var ev = new DurabilityCheckpointBackpressureEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            WaitMs = 1001u,
            Exhausted = 0x12,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3A00DF0744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F0E903000012");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void DurabilityCheckpointSleepEvent_StructEncode_MatchesCodec()
    {
        var ev = new DurabilityCheckpointSleepEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            SleepMs = 1001u,
            WakeReason = 0x12,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3A00E00744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F0E903000012");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void DurabilityRecoveryDiscoverEvent_StructEncode_MatchesCodec()
    {
        var ev = new DurabilityRecoveryDiscoverEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            SegCount = 101,
            TotalBytes = 2002L,
            FirstSegId = 303,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("4500E20744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F065000000D2070000000000002F010000");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void DurabilityRecoverySegmentEvent_StructEncode_MatchesCodec()
    {
        var ev = new DurabilityRecoverySegmentEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            SegId = 101,
            RecCount = 202,
            Bytes = 3003L,
            Truncated = 0x14,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("4600E30744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F065000000CA000000BB0B00000000000014");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void DurabilityRecoveryRedoEvent_StructEncode_MatchesCodec()
    {
        var ev = new DurabilityRecoveryRedoEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            RecordsReplayed = 101,
            UowsReplayed = 202,
            DurUs = 3003u,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("4100E60744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F065000000CA000000BB0B0000");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void DurabilityRecoveryUndoEvent_StructEncode_MatchesCodec()
    {
        var ev = new DurabilityRecoveryUndoEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            VoidedUowCount = 101,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3900E70744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F065000000");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void DurabilityRecoveryTickFenceEvent_StructEncode_MatchesCodec()
    {
        var ev = new DurabilityRecoveryTickFenceEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            TickFenceCount = 101,
            Entries = 202,
            TickNumber = 3003L,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("4500E80744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F065000000CA000000BB0B000000000000");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void EcsSpawnEvent_StructEncode_MatchesCodec()
    {
        var ev = new EcsSpawnEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            ArchetypeId = 0x1200,
            EntityId = 0xAA020202UL,
            Tsn = 3003L,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("48001E0744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F0001203020202AA00000000BB0B000000000000");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void EcsDestroyEvent_StructEncode_MatchesCodec()
    {
        var ev = new EcsDestroyEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            EntityId = 0xAA010101UL,
            CascadeCount = 202,
            Tsn = 3003L,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("4A001F0744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F0010101AA0000000003CA000000BB0B000000000000");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void EcsViewRefreshEvent_StructEncode_MatchesCodec()
    {
        var ev = new EcsViewRefreshEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            ArchetypeTypeId = 0x1200,
            Mode = (EcsViewRefreshMode)2,
            ResultCount = 303,
            DeltaCount = 404,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("4100230744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F0001207022F01000094010000");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void EcsQueryExecuteEvent_StructEncode_MatchesCodec()
    {
        var ev = new EcsQueryExecuteEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            ArchetypeTypeId = 0x1200,
            ResultCount = 202,
            ScanMode = (EcsQueryScanMode)3,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3D00200744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F0001203CA00000003");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void EcsQueryCountEvent_StructEncode_MatchesCodec()
    {
        var ev = new EcsQueryCountEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            ArchetypeTypeId = 0x1200,
            ResultCount = 202,
            ScanMode = (EcsQueryScanMode)3,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3D00210744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F0001203CA00000003");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void EcsQueryAnyEvent_StructEncode_MatchesCodec()
    {
        var ev = new EcsQueryAnyEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            ArchetypeTypeId = 0x1200,
            Found = true,
            ScanMode = (EcsQueryScanMode)3,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3D00220744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F00012060100000003");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void PageCacheFetchEvent_StructEncode_MatchesCodec()
    {
        var ev = new PageCacheFetchEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            FilePageIndex = 101,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        // Generator drops the trailing optMask byte (no [Optional] fields → no mask). Wire is 41 bytes.
        var golden = Convert.FromHexString("3900320744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F065000000");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void PageCacheDiskReadEvent_StructEncode_MatchesCodec()
    {
        var ev = new PageCacheDiskReadEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            FilePageIndex = 101,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3900330744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F065000000");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void PageCacheDiskWriteEvent_StructEncode_MatchesCodec()
    {
        var ev = new PageCacheDiskWriteEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            FilePageIndex = 101,
            PageCount = 202,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3E00340744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F06500000001CA000000");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void PageCacheAllocatePageEvent_StructEncode_MatchesCodec()
    {
        var ev = new PageCacheAllocatePageEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            FilePageIndex = 101,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3900350744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F065000000");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void PageCacheFlushEvent_StructEncode_MatchesCodec()
    {
        var ev = new PageCacheFlushEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            PageCount = 101,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3900360744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F065000000");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void PageCacheBackpressureEvent_StructEncode_MatchesCodec()
    {
        var ev = new PageCacheBackpressureEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            RetryCount = 101,
            DirtyCount = 202,
            EpochCount = 303,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("41003B0744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F065000000CA0000002F010000");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void QueryParseEvent_StructEncode_MatchesCodec()
    {
        var ev = new QueryParseEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            PredicateCount = 0x1200,
            BranchCount = 0x12,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3800BB0744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F0001212");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void QueryParseDnfEvent_StructEncode_MatchesCodec()
    {
        var ev = new QueryParseDnfEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            InBranches = 0x1200,
            OutBranches = 0x1300,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3900BC0744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F000120013");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void QueryPlanEvent_StructEncode_MatchesCodec()
    {
        var ev = new QueryPlanEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            EvaluatorCount = 0x11,
            IndexFieldIdx = 0x1300,
            RangeMin = 3003L,
            RangeMax = 4004L,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        // v9 (#342): trailing optMask byte = 0 because none of the new [Optional] fields are set,
        // so the encoded size grows by 1 byte. The new size prefix is 0x49 = 73 (vs. v8's 72).
        var golden = Convert.FromHexString("4900BD0744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F0110013BB0B000000000000A40F00000000000000");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void QueryEstimateEvent_StructEncode_MatchesCodec()
    {
        var ev = new QueryEstimateEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            FieldIdx = 0x1200,
            Cardinality = 2002L,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3F00BE0744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F00012D207000000000000");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void QueryPlanSortEvent_StructEncode_MatchesCodec()
    {
        var ev = new QueryPlanSortEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            EvaluatorCount = 0x11,
            SortNs = 2002u,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3A00C00744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F011D2070000");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void QueryExecuteIndexScanEvent_StructEncode_MatchesCodec()
    {
        var ev = new QueryExecuteIndexScanEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            PrimaryFieldIdx = 0x1200,
            Mode = 0x12,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3800C10744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F0001212");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void QueryExecuteIterateEvent_StructEncode_MatchesCodec()
    {
        var ev = new QueryExecuteIterateEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            ChunkCount = 101,
            EntryCount = 202,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3D00C20744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F065000000CA000000");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void QueryExecuteFilterEvent_StructEncode_MatchesCodec()
    {
        var ev = new QueryExecuteFilterEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            FilterCount = 0x11,
            RejectedCount = 202,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3A00C30744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F011CA000000");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void QueryExecutePaginationEvent_StructEncode_MatchesCodec()
    {
        var ev = new QueryExecutePaginationEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            Skip = 101,
            Take = 202,
            EarlyTerm = 0x13,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3E00C40744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F065000000CA00000013");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void QueryCountEvent_StructEncode_MatchesCodec()
    {
        var ev = new QueryCountEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            ResultCount = 101,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3900C60744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F065000000");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void EcsQueryConstructEvent_StructEncode_MatchesCodec()
    {
        var ev = new EcsQueryConstructEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            TargetArchId = 0x1200,
            Polymorphic = 0x12,
            MaskSize = 0x13,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3900C70744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F000121213");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void EcsQuerySubtreeExpandEvent_StructEncode_MatchesCodec()
    {
        var ev = new EcsQuerySubtreeExpandEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            SubtreeCount = 0x1200,
            RootId = 0x1300,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3900C90744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F000120013");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void EcsViewRefreshPullEvent_StructEncode_MatchesCodec()
    {
        var ev = new EcsViewRefreshPullEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            QueryNs = 1001u,
            ArchetypeMaskBits = 0x1300,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3B00CC0744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F0E90300000013");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void EcsViewIncrementalDrainEvent_StructEncode_MatchesCodec()
    {
        var ev = new EcsViewIncrementalDrainEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            DeltaCount = 101,
            Overflow = 0x12,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3A00CD0744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F06500000012");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void EcsViewRefreshFullEvent_StructEncode_MatchesCodec()
    {
        var ev = new EcsViewRefreshFullEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            OldCount = 101,
            NewCount = 202,
            RequeryNs = 3003u,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("4100D10744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F065000000CA000000BB0B0000");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void EcsViewRefreshFullOrEvent_StructEncode_MatchesCodec()
    {
        var ev = new EcsViewRefreshFullOrEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            OldCount = 101,
            NewCount = 202,
            BranchCount = 0x13,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3E00D20744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F065000000CA00000013");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void RuntimePhaseSpanEvent_StructEncode_MatchesCodec()
    {
        var ev = new RuntimePhaseSpanEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            Phase = 0x11,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3600F30744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F011");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void RuntimeTransactionLifecycleEvent_StructEncode_MatchesCodec()
    {
        var ev = new RuntimeTransactionLifecycleEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            SysIdx = 0x1200,
            TxDurUs = 2002u,
            Success = 0x13,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3C00A30744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F00012D207000013");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void RuntimeSubscriptionOutputExecuteEvent_StructEncode_MatchesCodec()
    {
        var ev = new RuntimeSubscriptionOutputExecuteEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            Tick = 1001L,
            Level = 0x12,
            ClientCount = 0x1400,
            ViewsRefreshed = 0x1500,
            DeltasPushed = 5005u,
            OverflowCount = 0x1700,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("4800A40744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F0E90300000000000012001400158D1300000017");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void SchedulerChunkEvent_StructEncode_MatchesCodec()
    {
        var ev = new SchedulerChunkEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            SystemIndex = 0x1200,
            ChunkIndex = 0x1300,
            TotalChunks = 0x1400,
            EntitiesProcessed = 404,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3F000A0744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F000120013001494010000");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void SchedulerSystemSingleThreadedEvent_StructEncode_MatchesCodec()
    {
        var ev = new SchedulerSystemSingleThreadedEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            SysIdx = 0x1200,
            IsParallelQuery = 0x12,
            ChunkCount = 0x1400,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3A00950744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F00012120014");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void SchedulerWorkerIdleEvent_StructEncode_MatchesCodec()
    {
        var ev = new SchedulerWorkerIdleEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            WorkerId = 0x11,
            SpinCount = 0x1300,
            IdleUs = 3003u,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3C00960744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F0110013BB0B0000");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void SchedulerWorkerBetweenTickEvent_StructEncode_MatchesCodec()
    {
        var ev = new SchedulerWorkerBetweenTickEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            WorkerId = 0x11,
            WaitUs = 2002u,
            WakeReason = 0x13,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3B00980744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F011D207000013");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void SchedulerDependencyFanOutEvent_StructEncode_MatchesCodec()
    {
        var ev = new SchedulerDependencyFanOutEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            CompletingSysIdx = 0x1200,
            SuccCount = 0x1300,
            SkippedCount = 0x1400,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3B009B0744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F0001200130014");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void SchedulerGraphBuildEvent_StructEncode_MatchesCodec()
    {
        var ev = new SchedulerGraphBuildEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            SysCount = 0x1200,
            EdgeCount = 0x1300,
            TopoLen = 0x1400,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3B009F0744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F0001200130014");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void SchedulerGraphRebuildEvent_StructEncode_MatchesCodec()
    {
        var ev = new SchedulerGraphRebuildEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            OldSysCount = 0x1200,
            NewSysCount = 0x1300,
            Reason = 0x13,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3A00A00744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F00012001313");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void SpatialMaintainInsertEvent_StructEncode_MatchesCodec()
    {
        var ev = new SpatialMaintainInsertEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            EntityPK = 1001L,
            ComponentTypeId = 0x1300,
            DidDegenerate = 0x13,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("40008A0744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F0E903000000000000001313");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void SpatialMaintainUpdateSlowPathEvent_StructEncode_MatchesCodec()
    {
        var ev = new SpatialMaintainUpdateSlowPathEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            EntityPK = 1001L,
            ComponentTypeId = 0x1300,
            EscapeDistSq = 3.5f,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("43008B0744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F0E903000000000000001300006040");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void SpatialTierIndexRebuildEvent_StructEncode_MatchesCodec()
    {
        var ev = new SpatialTierIndexRebuildEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            ArchetypeId = 0x1200,
            ClusterCount = 202,
            OldVersion = 303,
            NewVersion = 404,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("4300880744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F00012CA0000002F01000094010000");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void SpatialTriggerEvalEvent_StructEncode_MatchesCodec()
    {
        var ev = new SpatialTriggerEvalEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            RegionId = 0x1200,
            OccupantCount = 0x1300,
            EnterCount = 0x1400,
            LeaveCount = 0x1500,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3D008F0744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F00012001300140015");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void SpatialQueryAabbEvent_StructEncode_MatchesCodec()
    {
        var ev = new SpatialQueryAabbEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            NodesVisited = 0x1200,
            LeavesEntered = 0x1300,
            ResultCount = 0x1400,
            RestartCount = 0x14,
            CategoryMask = 5005u,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("4000750744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F0001200130014148D130000");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void SpatialQueryRadiusEvent_StructEncode_MatchesCodec()
    {
        var ev = new SpatialQueryRadiusEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            NodesVisited = 0x1200,
            ResultCount = 0x1300,
            Radius = 3.5f,
            RestartCount = 0x14,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3E00760744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F0001200130000604014");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void SpatialQueryRayEvent_StructEncode_MatchesCodec()
    {
        var ev = new SpatialQueryRayEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            NodesVisited = 0x1200,
            ResultCount = 0x1300,
            MaxDist = 3.5f,
            RestartCount = 0x14,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3E00770744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F0001200130000604014");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void SpatialQueryFrustumEvent_StructEncode_MatchesCodec()
    {
        var ev = new SpatialQueryFrustumEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            NodesVisited = 0x1200,
            ResultCount = 0x1300,
            PlaneCount = 0x13,
            RestartCount = 0x14,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3B00780744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F0001200131314");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void SpatialQueryKnnEvent_StructEncode_MatchesCodec()
    {
        var ev = new SpatialQueryKnnEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            K = 0x1200,
            IterCount = 0x12,
            FinalRadius = 3.5f,
            ResultCount = 0x1500,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3E00790744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F0001212000060400015");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void SpatialQueryCountEvent_StructEncode_MatchesCodec()
    {
        var ev = new SpatialQueryCountEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            Variant = 0x11,
            NodesVisited = 0x1300,
            ResultCount = 303,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3C007A0744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F01100132F010000");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void SpatialRTreeInsertEvent_StructEncode_MatchesCodec()
    {
        var ev = new SpatialRTreeInsertEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            EntityId = 1001L,
            Depth = 0x12,
            DidSplit = 0x13,
            RestartCount = 0x14,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("40007B0744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F0E903000000000000121314");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void SpatialRTreeRemoveEvent_StructEncode_MatchesCodec()
    {
        var ev = new SpatialRTreeRemoveEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            EntityId = 1001L,
            LeafCollapse = 0x12,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3E007C0744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F0E90300000000000012");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void SpatialRTreeNodeSplitEvent_StructEncode_MatchesCodec()
    {
        var ev = new SpatialRTreeNodeSplitEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            Depth = 0x11,
            SplitAxis = 0x12,
            LeftCount = 0x13,
            RightCount = 0x14,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("39007D0744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F011121314");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void SpatialRTreeBulkLoadEvent_StructEncode_MatchesCodec()
    {
        var ev = new SpatialRTreeBulkLoadEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            EntityCount = 101,
            LeafCount = 202,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3D007E0744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F065000000CA000000");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void StatisticsRebuildEvent_StructEncode_MatchesCodec()
    {
        var ev = new StatisticsRebuildEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            EntityCount = 101,
            MutationCount = 202,
            SamplingInterval = 303,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("4100590744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F065000000CA0000002F010000");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void StoragePageCacheDirtyWalkEvent_StructEncode_MatchesCodec()
    {
        var ev = new StoragePageCacheDirtyWalkEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            RangeStart = 101,
            RangeLen = 202,
            DirtyMs = 303,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("4100A50744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F065000000CA0000002F010000");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void RuntimeSubscriptionSubscriberEvent_StructEncode_MatchesCodec()
    {
        var ev = new RuntimeSubscriptionSubscriberEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            SubscriberId = 1001u,
            ViewId = 0x1300,
            DeltaCount = 303,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3F00EB0744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F0E903000000132F010000");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void RuntimeSubscriptionDeltaBuildEvent_StructEncode_MatchesCodec()
    {
        var ev = new RuntimeSubscriptionDeltaBuildEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            ViewId = 0x1200,
            Added = 202,
            Removed = 303,
            Modified = 404,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("4300EC0744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F00012CA0000002F01000094010000");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void RuntimeSubscriptionDeltaSerializeEvent_StructEncode_MatchesCodec()
    {
        var ev = new RuntimeSubscriptionDeltaSerializeEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            ClientId = 1001u,
            ViewId = 0x1300,
            Bytes = 303,
            Format = 0x14,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("4000ED0744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F0E903000000132F01000014");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void RuntimeSubscriptionTransitionBeginSyncEvent_StructEncode_MatchesCodec()
    {
        var ev = new RuntimeSubscriptionTransitionBeginSyncEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            ClientId = 1001u,
            ViewId = 0x1300,
            EntitySnapshot = 303,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3F00EE0744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F0E903000000132F010000");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void RuntimeSubscriptionOutputCleanupEvent_StructEncode_MatchesCodec()
    {
        var ev = new RuntimeSubscriptionOutputCleanupEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            DeadCount = 101,
            DeregCount = 202,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3D00EF0744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F065000000CA000000");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void RuntimeSubscriptionDeltaDirtyBitmapSupplementEvent_StructEncode_MatchesCodec()
    {
        var ev = new RuntimeSubscriptionDeltaDirtyBitmapSupplementEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            ModifiedFromRing = 101,
            SupplementCount = 202,
            UnionSize = 303,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("4100F00744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F065000000CA0000002F010000");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void TransactionCommitEvent_StructEncode_MatchesCodec()
    {
        var ev = new TransactionCommitEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            Tsn = 1001L,
            ComponentCount = 202,
            ConflictDetected = true,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("4300140744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F0E90300000000000003CA00000001");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void TransactionRollbackEvent_StructEncode_MatchesCodec()
    {
        var ev = new TransactionRollbackEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            Tsn = 1001L,
            ComponentCount = 202,
            Reason = (TransactionRollbackReason)3,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("4300150744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F0E90300000000000005CA00000003");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void TransactionCommitComponentEvent_StructEncode_MatchesCodec()
    {
        var ev = new TransactionCommitComponentEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            Tsn = 1001L,
            ComponentTypeId = 202,
            RowCount = 303,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("4600160744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F0E903000000000000CA000000082F010000");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void TransactionPersistEvent_StructEncode_MatchesCodec()
    {
        var ev = new TransactionPersistEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            Tsn = 1001L,
            WalLsn = 2002L,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("4600170744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F0E90300000000000001D207000000000000");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void WalFlushEvent_StructEncode_MatchesCodec()
    {
        var ev = new WalFlushEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            BatchByteCount = 101,
            FrameCount = 202,
            HighLsn = 3003L,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("4500500744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F065000000CA000000BB0B000000000000");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void WalSegmentRotateEvent_StructEncode_MatchesCodec()
    {
        var ev = new WalSegmentRotateEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            NewSegmentIndex = 101,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3900510744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F065000000");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }

    [Test]
    public void WalWaitEvent_StructEncode_MatchesCodec()
    {
        var ev = new WalWaitEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = TraceIdHi,
                TraceIdLo = TraceIdLo,
            },
            TargetLsn = 1001L,
        };

        Span<byte> bufStruct = stackalloc byte[256];
        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);

        var golden = Convert.FromHexString("3D00520744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F0E903000000000000");
        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);
    }
    // ── GENERATED-END ──

    private static void AssertSpanEqualsGolden(ReadOnlySpan<byte> actual, int actualLen, ReadOnlySpan<byte> golden)
    {
        Assert.That(actualLen, Is.EqualTo(golden.Length),
            $"Encoded length mismatch: actual={actualLen}, golden={golden.Length}");
        for (int i = 0; i < actualLen; i++)
        {
            if (actual[i] != golden[i])
            {
                Assert.Fail($"Byte mismatch at offset {i}: actual=0x{actual[i]:X2}, golden=0x{golden[i]:X2}\n" +
                            $"actual[0..{actualLen}] = {Convert.ToHexString(actual[..actualLen])}\n" +
                            $"golden[0..{golden.Length}] = {Convert.ToHexString(golden)}");
            }
        }
    }
}
