using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Typhon.Profiler;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// Tests for the intra-tick chunk-splitting behavior added in chunker v8. When a single tick's event count or byte count
/// exceeds the IntraTick caps, <see cref="TraceFileCacheBuilder"/> must flush mid-tick and open a continuation chunk
/// marked with <see cref="TraceFileCacheConstants.FlagIsContinuation"/>. These tests synthesize a trace source with
/// controlled record counts and verify the manifest matches the expected split pattern.
/// </summary>
[TestFixture]
public class TraceFileCacheBuilderSplitTests
{
    /// <summary>Matches the layout constants at the top of TraceFileCacheBuilder. Kept local to avoid leaking internals.</summary>
    private const int CommonHeaderSize = 12;

    /// <summary>
    /// Dense single-tick: 1 TickStart + enough Instant records (kind = 6) to exceed <see cref="TraceFileCacheConstants.IntraTickEventCap"/>.
    /// Expected: ≥2 chunks emitted; the first has Flags = 0, later chunks have <see cref="TraceFileCacheConstants.FlagIsContinuation"/> set.
    /// All chunks share the same <c>[FromTick, ToTick)</c> range. Event counts across chunks sum to the total written.
    /// </summary>
    [Test]
    public void Build_DenseSingleTickExceedingEventCap_SplitsIntoContinuationChunks()
    {
        // Write slightly more than IntraTickEventCap events so at least one split is forced. TickStart itself counts as 1, so
        // write (cap + 100) additional events to be comfortably over the threshold — one split is guaranteed, maybe two.
        var extraEvents = TraceFileCacheConstants.IntraTickEventCap + 100;
        var totalRecords = 1 + extraEvents;   // 1 TickStart + extras

        var sourcePath = Path.Combine(Path.GetTempPath(), $"split-src-{Guid.NewGuid():N}.typhon-trace");
        var cachePath = TraceFileCacheBuilder.GetCachePathFor(sourcePath);
        try
        {
            WriteSyntheticTrace(sourcePath, totalRecords, recordsPerTick: totalRecords);

            var result = TraceFileCacheBuilder.Build(sourcePath, cachePath);
            Assert.That(result.TickCount, Is.EqualTo(1), "exactly one tick in the synthetic trace");
            Assert.That(result.EventCount, Is.EqualTo(totalRecords));

            using var reader = new TraceFileCacheReader(File.OpenRead(cachePath));
            var manifest = reader.ChunkManifest;

            Assert.That(manifest.Count, Is.GreaterThanOrEqualTo(2),
                "event count exceeded IntraTickEventCap, at least one mid-tick split must have fired");

            // First chunk: normal (non-continuation), Flags == 0.
            Assert.That(manifest[0].Flags & TraceFileCacheConstants.FlagIsContinuation, Is.EqualTo(0u),
                "the first chunk in a tick always starts with a TickStart — never a continuation");

            // Every subsequent chunk must be marked as a continuation (since this is a single tick split across multiple chunks).
            for (var i = 1; i < manifest.Count; i++)
            {
                Assert.That(manifest[i].Flags & TraceFileCacheConstants.FlagIsContinuation, Is.EqualTo(TraceFileCacheConstants.FlagIsContinuation),
                    $"chunk #{i} is continuation of the same tick — must have FlagIsContinuation set");
            }

            // All chunks share the same tick range — the split tick is [1, 2) in every entry.
            foreach (var entry in manifest)
            {
                Assert.That(entry.FromTick, Is.EqualTo(1u), "single tick in the trace is tick 1");
                Assert.That(entry.ToTick, Is.EqualTo(2u), "exclusive ToTick for tick 1 is 2");
            }

            // Sum of per-chunk event counts equals the total records the builder saw. This verifies no records were dropped
            // or double-counted by the split logic.
            var sumEvents = 0L;
            foreach (var entry in manifest) sumEvents += entry.EventCount;
            Assert.That(sumEvents, Is.EqualTo(totalRecords),
                "per-chunk event counts must sum to the full per-tick total; split must neither drop nor duplicate records");
        }
        finally
        {
            if (File.Exists(sourcePath)) File.Delete(sourcePath);
            if (File.Exists(cachePath)) File.Delete(cachePath);
        }
    }

    /// <summary>
    /// Well-sized single tick (under both caps): exactly ONE chunk must be emitted with Flags = 0. Sanity check that the
    /// intra-tick check doesn't fire on normal workloads — Option C's "merge is rare" promise.
    /// </summary>
    [Test]
    public void Build_WellSizedTick_EmitsSingleNonContinuationChunk()
    {
        var totalRecords = 1 + 1_000;   // 1 TickStart + 1000 events, well under the cap

        var sourcePath = Path.Combine(Path.GetTempPath(), $"split-src-normal-{Guid.NewGuid():N}.typhon-trace");
        var cachePath = TraceFileCacheBuilder.GetCachePathFor(sourcePath);
        try
        {
            WriteSyntheticTrace(sourcePath, totalRecords, recordsPerTick: totalRecords);

            TraceFileCacheBuilder.Build(sourcePath, cachePath);
            using var reader = new TraceFileCacheReader(File.OpenRead(cachePath));
            var manifest = reader.ChunkManifest;

            Assert.That(manifest.Count, Is.EqualTo(1), "well-sized tick fits in one chunk — no split");
            Assert.That(manifest[0].Flags & TraceFileCacheConstants.FlagIsContinuation, Is.EqualTo(0u),
                "the sole chunk is a normal chunk, not a continuation");
            Assert.That(manifest[0].EventCount, Is.EqualTo((uint)totalRecords));
        }
        finally
        {
            if (File.Exists(sourcePath)) File.Delete(sourcePath);
            if (File.Exists(cachePath)) File.Delete(cachePath);
        }
    }

    /// <summary>
    /// Writes a minimal <c>.typhon-trace</c> v3 file with the given record stream. Records are 12-byte common-header-only instants
    /// (the first is TickStart, subsequent are generic Instant kind 6). All records belong to a single tick with <c>tickNumber=1</c>.
    /// </summary>
    private static void WriteSyntheticTrace(string path, int totalRecords, int recordsPerTick)
    {
        using var fs = File.Create(path);
        using var writer = new TraceFileWriter(fs);

        var header = new TraceFileHeader
        {
            Magic = TraceFileHeader.MagicValue,
            Version = TraceFileHeader.CurrentVersion,
            Flags = 0,
            TimestampFrequency = 10_000_000,   // Stopwatch-like 10 MHz
            BaseTickRate = 1_000,
            WorkerCount = 1,
            SystemCount = 0,
            ArchetypeCount = 0,
            ComponentTypeCount = 0,
            CreatedUtcTicks = DateTime.UtcNow.Ticks,
            SamplingSessionStartQpc = 0,
        };
        writer.WriteHeader(in header);
        writer.WriteSystemDefinitions(ReadOnlySpan<SystemDefinitionRecord>.Empty);
        writer.WriteArchetypes(ReadOnlySpan<ArchetypeRecord>.Empty);
        writer.WriteComponentTypes(ReadOnlySpan<ComponentTypeRecord>.Empty);
        writer.WritePhases(ReadOnlySpan<string>.Empty);
        writer.WriteEmptyStaticStructures();

        // Build record bytes in blocks no larger than TraceFileWriter.MaxBlockBytes (256 KiB). At 12 B per record that's up to
        // ~21K records per block — chunk the emission into pages.
        const int maxRecordsPerBlock = TraceFileWriter.MaxBlockBytes / CommonHeaderSize;
        var blockBuf = new byte[maxRecordsPerBlock * CommonHeaderSize];

        var written = 0;
        long ts = 100;   // non-zero start timestamp (FinalizeTick rejects firstTs <= 0)
        while (written < totalRecords)
        {
            var recordsThisBlock = Math.Min(maxRecordsPerBlock, totalRecords - written);
            for (var r = 0; r < recordsThisBlock; r++)
            {
                var kind = (written == 0)
                    ? (byte)TraceEventKind.TickStart
                    : (byte)TraceEventKind.Instant;
                var offset = r * CommonHeaderSize;
                BinaryPrimitives.WriteUInt16LittleEndian(blockBuf.AsSpan(offset, 2), CommonHeaderSize);       // size
                blockBuf[offset + 2] = kind;                                                                     // kind
                blockBuf[offset + 3] = 0;                                                                        // threadSlot
                BinaryPrimitives.WriteInt64LittleEndian(blockBuf.AsSpan(offset + 4, 8), ts);                     // startTs
                ts++;
                written++;
            }
            writer.WriteRecords(blockBuf.AsSpan(0, recordsThisBlock * CommonHeaderSize), recordsThisBlock);
        }

        writer.WriteSpanNames(new Dictionary<int, string>());
        writer.Flush();
    }
}
