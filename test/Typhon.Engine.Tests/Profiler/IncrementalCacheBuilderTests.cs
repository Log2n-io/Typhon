using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Typhon.Profiler;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// Phase 1 (#289) — verifies <see cref="IncrementalCacheBuilder"/> produces byte-identical sidecar output to the original
/// <see cref="TraceFileCacheBuilder"/> static implementation, including when records are fed in arbitrary span splits. Also
/// covers builder events, force-flush mid-tick, idempotent <c>FlushTrailingTick</c>, and the pre-tick buffer cap.
/// </summary>
[TestFixture]
public class IncrementalCacheBuilderTests
{
    private const int CommonHeaderSize = 12;

    /// <summary>
    /// Build the same source file via the static façade and via an <see cref="IncrementalCacheBuilder"/> driven manually with
    /// arbitrary feed splits. Both must produce byte-identical sidecar caches.
    /// </summary>
    [TestCase(64)]
    [TestCase(256)]
    [TestCase(4096)]
    [TestCase(64 * 1024)]
    public void Parity_ManualSplitFeed_ProducesIdenticalSidecar(int feedSplitBytes)
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"icb-parity-src-{Guid.NewGuid():N}.typhon-trace");
        var cachePathA = Path.Combine(Path.GetTempPath(), $"icb-parity-A-{Guid.NewGuid():N}.typhon-trace-cache");
        var cachePathB = Path.Combine(Path.GetTempPath(), $"icb-parity-B-{Guid.NewGuid():N}.typhon-trace-cache");
        try
        {
            // 5 ticks × 200 events each — exercises tick boundaries without hitting the chunk cap.
            WriteSyntheticTrace(sourcePath, ticks: 5, eventsPerTick: 200);

            // A: built via the static façade (which now delegates to IncrementalCacheBuilder.Build).
            var resultA = TraceFileCacheBuilder.Build(sourcePath, cachePathA);
            Assert.That(resultA.TickCount, Is.EqualTo(5));

            // B: build manually by feeding raw record bytes in chunks of feedSplitBytes.
            var fingerprint = new byte[32];
            TraceFileCacheReader.ComputeSourceFingerprint(sourcePath, fingerprint);
            using (var srcStream = File.OpenRead(sourcePath))
            using (var reader = new TraceFileReader(srcStream))
            {
                var fileHeader = reader.ReadHeader();
                reader.ReadSystemDefinitions();
                reader.ReadArchetypes();
                reader.ReadComponentTypes();
                reader.ReadPhases();

                var sink = FileCacheSink.Create(cachePathB);
                var profilerHeader = new ProfilerHeader { Version = fileHeader.Version, TimestampFrequency = fileHeader.TimestampFrequency };
                using var builder = new IncrementalCacheBuilder(sink, ownsSink: true, profilerHeader, fingerprint, reader.SpanNames);
                while (reader.ReadNextBlock(out var recordBytes, out _))
                {
                    var span = recordBytes.Span;
                    var pos = 0;
                    while (pos < span.Length)
                    {
                        // Cut the feed on a record boundary closest to feedSplitBytes (caller contract: complete records only).
                        var endPos = Math.Min(span.Length, pos + feedSplitBytes);
                        // Walk back to nearest record boundary.
                        var feedEnd = pos;
                        while (feedEnd < endPos)
                        {
                            var size = BinaryPrimitives.ReadUInt16LittleEndian(span[feedEnd..]);
                            if (size < CommonHeaderSize || feedEnd + size > span.Length)
                            {
                                feedEnd = span.Length;
                                break;
                            }
                            if (feedEnd + size > endPos)
                            {
                                if (feedEnd == pos)
                                {
                                    // Single record larger than feedSplitBytes — must include it whole or we can't make progress.
                                    feedEnd += size;
                                }
                                break;
                            }
                            feedEnd += size;
                        }
                        builder.FeedRawRecords(span.Slice(pos, feedEnd - pos));
                        pos = feedEnd;
                    }
                }
            }

            // Compare bytes — the only field that legitimately differs is CreatedUtcTicks. Mask that off (offset 88..96 in CacheHeader).
            CompareCachesIgnoringCreationTime(cachePathA, cachePathB);
        }
        finally
        {
            if (File.Exists(sourcePath)) File.Delete(sourcePath);
            if (File.Exists(cachePathA)) File.Delete(cachePathA);
            if (File.Exists(cachePathB)) File.Delete(cachePathB);
        }
    }

    /// <summary>
    /// Driving the builder produces matching event counts on its <c>TickFinalized</c> / <c>ChunkFlushed</c> stream and its
    /// <c>TickSummaries</c> / <c>ChunkManifest</c> lists.
    /// </summary>
    [Test]
    public void Events_TickFinalizedAndChunkFlushed_MatchListCounts()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"icb-events-{Guid.NewGuid():N}.typhon-trace");
        var cachePath = Path.Combine(Path.GetTempPath(), $"icb-events-{Guid.NewGuid():N}.typhon-trace-cache");
        try
        {
            WriteSyntheticTrace(sourcePath, ticks: 3, eventsPerTick: 100);

            var fingerprint = new byte[32];
            TraceFileCacheReader.ComputeSourceFingerprint(sourcePath, fingerprint);
            var tickEvents = 0;
            var chunkEvents = 0;
            using (var srcStream = File.OpenRead(sourcePath))
            using (var reader = new TraceFileReader(srcStream))
            {
                var fileHeader = reader.ReadHeader();
                reader.ReadSystemDefinitions();
                reader.ReadArchetypes();
                reader.ReadComponentTypes();
                reader.ReadPhases();

                var sink = FileCacheSink.Create(cachePath);
                var profilerHeader = new ProfilerHeader { Version = fileHeader.Version, TimestampFrequency = fileHeader.TimestampFrequency };
                using var builder = new IncrementalCacheBuilder(sink, ownsSink: true, profilerHeader, fingerprint, reader.SpanNames);
                builder.TickFinalized += _ => tickEvents++;
                builder.ChunkFlushed += _ => chunkEvents++;

                while (reader.ReadNextBlock(out var recordBytes, out _))
                {
                    builder.FeedRawRecords(recordBytes.Span);
                }
                // Builder.Dispose() emits the final tick + final chunk events.
                builder.Dispose();

                Assert.That(tickEvents, Is.EqualTo(builder.TickSummaries.Count), "TickFinalized event count == TickSummaries.Count");
                Assert.That(chunkEvents, Is.EqualTo(builder.ChunkManifest.Count), "ChunkFlushed event count == ChunkManifest.Count");
            }
        }
        finally
        {
            if (File.Exists(sourcePath)) File.Delete(sourcePath);
            if (File.Exists(cachePath)) File.Delete(cachePath);
        }
    }

    /// <summary>
    /// Feeding half a tick, calling <c>FlushCurrentChunk</c>, feeding the rest: the chunk that contains the second half must
    /// be marked as a continuation of the first.
    /// </summary>
    [Test]
    public void FlushCurrentChunk_MidTick_NextChunkMarkedAsContinuation()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"icb-flushmid-{Guid.NewGuid():N}.typhon-trace");
        var cachePath = Path.Combine(Path.GetTempPath(), $"icb-flushmid-{Guid.NewGuid():N}.typhon-trace-cache");
        try
        {
            // Single tick with 1000 events — well under the chunk caps so it'd normally be a single chunk.
            WriteSyntheticTrace(sourcePath, ticks: 1, eventsPerTick: 1000);

            var fingerprint = new byte[32];
            TraceFileCacheReader.ComputeSourceFingerprint(sourcePath, fingerprint);
            using (var srcStream = File.OpenRead(sourcePath))
            using (var reader = new TraceFileReader(srcStream))
            {
                var fileHeader = reader.ReadHeader();
                reader.ReadSystemDefinitions();
                reader.ReadArchetypes();
                reader.ReadComponentTypes();
                reader.ReadPhases();

                var sink = FileCacheSink.Create(cachePath);
                var profilerHeader = new ProfilerHeader { Version = fileHeader.Version, TimestampFrequency = fileHeader.TimestampFrequency };
                using var builder = new IncrementalCacheBuilder(sink, ownsSink: true, profilerHeader, fingerprint, reader.SpanNames);

                reader.ReadNextBlock(out var recordBytes, out _);
                var span = recordBytes.Span;

                // Feed first half (round to record boundary).
                var halfBytes = span.Length / 2;
                var feedEnd = 0;
                while (feedEnd < halfBytes)
                {
                    var size = BinaryPrimitives.ReadUInt16LittleEndian(span[feedEnd..]);
                    feedEnd += size;
                }
                builder.FeedRawRecords(span[..feedEnd]);

                // Mid-tick force-flush.
                Assert.That(builder.FlushCurrentChunk(), Is.True, "FlushCurrentChunk should report it produced a chunk");
                Assert.That(builder.ChunkManifest.Count, Is.EqualTo(1));
                Assert.That(builder.ChunkManifest[0].Flags & TraceFileCacheConstants.FlagIsContinuation, Is.EqualTo(0u),
                    "first chunk must NOT have continuation flag set");

                // Feed remaining bytes.
                builder.FeedRawRecords(span[feedEnd..]);
                builder.Dispose();

                Assert.That(builder.ChunkManifest.Count, Is.EqualTo(2), "two chunks total — original + continuation");
                Assert.That(builder.ChunkManifest[1].Flags & TraceFileCacheConstants.FlagIsContinuation, Is.EqualTo(TraceFileCacheConstants.FlagIsContinuation),
                    "second chunk must have continuation flag set");
                Assert.That(builder.ChunkManifest[0].FromTick, Is.EqualTo(1u));
                Assert.That(builder.ChunkManifest[1].FromTick, Is.EqualTo(1u));
                Assert.That(builder.ChunkManifest[1].ToTick, Is.EqualTo(2u));
            }
        }
        finally
        {
            if (File.Exists(sourcePath)) File.Delete(sourcePath);
            if (File.Exists(cachePath)) File.Delete(cachePath);
        }
    }

    /// <summary>
    /// Calling <c>FlushTrailingTick</c> twice produces no extra tick summary the second time.
    /// </summary>
    [Test]
    public void FlushTrailingTick_IsIdempotent()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"icb-trail-{Guid.NewGuid():N}.typhon-trace");
        var cachePath = Path.Combine(Path.GetTempPath(), $"icb-trail-{Guid.NewGuid():N}.typhon-trace-cache");
        try
        {
            WriteSyntheticTrace(sourcePath, ticks: 2, eventsPerTick: 50);

            var fingerprint = new byte[32];
            TraceFileCacheReader.ComputeSourceFingerprint(sourcePath, fingerprint);
            using (var srcStream = File.OpenRead(sourcePath))
            using (var reader = new TraceFileReader(srcStream))
            {
                var fileHeader = reader.ReadHeader();
                reader.ReadSystemDefinitions();
                reader.ReadArchetypes();
                reader.ReadComponentTypes();
                reader.ReadPhases();

                var sink = FileCacheSink.Create(cachePath);
                var profilerHeader = new ProfilerHeader { Version = fileHeader.Version, TimestampFrequency = fileHeader.TimestampFrequency };
                using var builder = new IncrementalCacheBuilder(sink, ownsSink: true, profilerHeader, fingerprint, reader.SpanNames);
                while (reader.ReadNextBlock(out var recordBytes, out _))
                {
                    builder.FeedRawRecords(recordBytes.Span);
                }
                var beforeCount = builder.TickSummaries.Count;
                builder.FlushTrailingTick();
                var afterFirst = builder.TickSummaries.Count;
                builder.FlushTrailingTick();
                var afterSecond = builder.TickSummaries.Count;

                Assert.That(afterFirst, Is.EqualTo(beforeCount + 1), "first call finalizes the trailing tick");
                Assert.That(afterSecond, Is.EqualTo(afterFirst), "second call is a no-op");
            }
        }
        finally
        {
            if (File.Exists(sourcePath)) File.Delete(sourcePath);
            if (File.Exists(cachePath)) File.Delete(cachePath);
        }
    }

    /// <summary>
    /// #289 — pre-tick records of ANY kind get buffered (not just the old whitelist) and emitted as a synthetic
    /// tick 0. AntHill spawns 200K entities before the runtime's first TickStart; those EcsSpawn records used to be
    /// silently dropped. The fix: buffer every pre-tick record up to <see cref="IncrementalCacheBuilder.PreTickBufferCap"/>,
    /// flush as chunk 0 [FromTick=0, ToTick=1) with IsContinuation=true, synthesize a TickSummary for tick 0.
    /// </summary>
    [Test]
    public void PreTickRecords_OfArbitraryKind_AreBufferedNotDropped()
    {
        // 100 fake EcsSpawn records pre-tick + 1 TickStart + 5 in-tick records.
        const int recordSize = CommonHeaderSize;
        const int preTickCount = 100;
        const int inTickCount = 5;
        var totalRecords = preTickCount + 1 + inTickCount;
        var buffer = new byte[totalRecords * recordSize];
        long ts = 100;
        var idx = 0;
        for (var i = 0; i < preTickCount; i++, idx++)
        {
            var offset = idx * recordSize;
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset, 2), (ushort)recordSize);
            buffer[offset + 2] = (byte)TraceEventKind.EcsSpawn;
            buffer[offset + 3] = 0;
            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(offset + 4, 8), ts++);
        }
        // TickStart(1)
        {
            var offset = idx++ * recordSize;
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset, 2), (ushort)recordSize);
            buffer[offset + 2] = (byte)TraceEventKind.TickStart;
            buffer[offset + 3] = 0;
            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(offset + 4, 8), ts++);
        }
        for (var i = 0; i < inTickCount; i++, idx++)
        {
            var offset = idx * recordSize;
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset, 2), (ushort)recordSize);
            buffer[offset + 2] = (byte)TraceEventKind.Instant;
            buffer[offset + 3] = 0;
            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(offset + 4, 8), ts++);
        }

        var fingerprint = new byte[32];
        var profilerHeader = new ProfilerHeader { Version = TraceFileHeader.CurrentVersion, TimestampFrequency = 10_000_000 };
        var sink = new MemorySink();
        var builder = new IncrementalCacheBuilder(sink, ownsSink: true, profilerHeader, fingerprint, new Dictionary<int, string>());
        builder.FeedRawRecords(buffer);
        builder.Dispose();   // flushes the trailing tick-1 chunk

        // Two chunks: tick 0 (pre-tick records) + tick 1 (TickStart + in-tick records).
        Assert.That(sink.Chunks.Count, Is.EqualTo(2), "tick 0 (pre-tick) chunk + tick 1 chunk");
        Assert.That(CountRecords(sink.Chunks[0]), Is.EqualTo(preTickCount),
            "tick-0 chunk holds all pre-tick records");
        Assert.That(CountRecords(sink.Chunks[1]), Is.EqualTo(1 + inTickCount),
            "tick-1 chunk holds the TickStart + in-tick records");
        Assert.That(builder.TickSummaries.Count, Is.EqualTo(2), "two summaries: synthetic tick 0 + real tick 1");
        Assert.That(builder.TickSummaries[0].TickNumber, Is.EqualTo(0u), "first summary is tick 0");
        Assert.That(builder.TickSummaries[0].EventCount, Is.EqualTo((uint)preTickCount), "tick 0 holds pre-tick events");
        Assert.That(builder.TickSummaries[1].TickNumber, Is.EqualTo(1u), "second summary is tick 1");
        var preTickEntry = builder.ChunkManifest[0];
        Assert.That(preTickEntry.FromTick, Is.EqualTo(0u));
        Assert.That(preTickEntry.ToTick, Is.EqualTo(1u));
        Assert.That(preTickEntry.Flags & TraceFileCacheConstants.FlagIsContinuation, Is.EqualTo(TraceFileCacheConstants.FlagIsContinuation),
            "tick-0 chunk uses IsContinuation so the client decoder seeds currentTick=0");
        var tickOneEntry = builder.ChunkManifest[1];
        Assert.That(tickOneEntry.FromTick, Is.EqualTo(1u));
        Assert.That(tickOneEntry.Flags & TraceFileCacheConstants.FlagIsContinuation, Is.EqualTo(0u),
            "tick-1 chunk is a normal (non-continuation) chunk");
    }

    private static int CountRecords(byte[] chunkBytes)
    {
        var pos = 0;
        var recordCount = 0;
        while (pos + CommonHeaderSize <= chunkBytes.Length)
        {
            var sz = BinaryPrimitives.ReadUInt16LittleEndian(chunkBytes.AsSpan(pos));
            if (sz < CommonHeaderSize) break;
            recordCount++;
            pos += sz;
        }
        return recordCount;
    }

    /// <summary>
    /// Pre-tick records overflow the cap → the excess is counted in <c>PreTickDroppedBytes</c>.
    /// </summary>
    [Test]
    public void PreTickBuffer_CapsAtConfiguredLimit()
    {
        // Synthesize enough pre-tick records (ThreadInfo) to overflow PreTickBufferCap with no TickStart at all.
        const int recordSize = CommonHeaderSize;
        var bigRecordCount = (IncrementalCacheBuilder.PreTickBufferCap / recordSize) + 5_000;
        var buffer = new byte[bigRecordCount * recordSize];
        for (var i = 0; i < bigRecordCount; i++)
        {
            var offset = i * recordSize;
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset, 2), (ushort)recordSize);
            buffer[offset + 2] = (byte)TraceEventKind.ThreadInfo;
            buffer[offset + 3] = 0; // threadSlot
            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(offset + 4, 8), 100 + i);
        }

        var fingerprint = new byte[32];
        var profilerHeader = new ProfilerHeader { Version = TraceFileHeader.CurrentVersion, TimestampFrequency = 10_000_000 };
        var sink = new MemorySink();
        using var builder = new IncrementalCacheBuilder(sink, ownsSink: true, profilerHeader, fingerprint, new Dictionary<int, string>());
        builder.FeedRawRecords(buffer);

        Assert.That(builder.PreTickDroppedBytes, Is.GreaterThan(0L), "expected drops once the buffer hits its cap");
        Assert.That(builder.TickSummaries.Count, Is.EqualTo(0), "no TickStart was emitted, so no tick summary should be finalized");
    }

    /// <summary>
    /// In-memory <see cref="ICacheChunkSink"/> for tests that don't care about file output.
    /// </summary>
    private sealed class MemorySink : ICacheChunkSink
    {
        public bool SupportsTrailer => false;
        public List<byte[]> Chunks { get; } = new();
        private long _offset;

        public (long CacheOffset, uint CompressedLength, uint UncompressedLength) AppendChunk(ReadOnlySpan<byte> uncompressedRecords)
        {
            var copy = uncompressedRecords.ToArray();
            Chunks.Add(copy);
            var off = _offset;
            _offset += copy.Length;
            return (off, (uint)copy.Length, (uint)copy.Length);
        }
        public void WriteTrailer(IReadOnlyList<TickSummary> ts, in GlobalMetricsFixed gm, IReadOnlyList<SystemAggregateDuration> sa,
            IReadOnlyList<ChunkManifestEntry> cm, IReadOnlyDictionary<int, string> sn, ReadOnlySpan<byte> sourceMetadataBytes, in CacheHeader h,
            IReadOnlyList<SystemTickSummary> sts, IReadOnlyList<QueueTickSummary> qts, IReadOnlyList<PostTickSummary> pts,
            IReadOnlyDictionary<ushort, string> qIdToName)
            => throw new NotSupportedException("MemorySink does not support trailer.");
        public void Dispose() { }
    }

    private static void CompareCachesIgnoringCreationTime(string pathA, string pathB)
    {
        var a = File.ReadAllBytes(pathA);
        var b = File.ReadAllBytes(pathB);
        Assert.That(a.Length, Is.EqualTo(b.Length), "cache file sizes must match exactly");

        // CacheHeader.CreatedUtcTicks is at offset 64 (Magic 0..4, Version 4..6, Flags 6..8, SourceFingerprint 8..40,
        // SourceVersion 40..42, ChunkerVersion 42..44, SectionTableOffset 44..52, SectionTableLength 52..60, CreatedUtcTicks 60..68).
        // Wait: layout is Pack=1 sequential — let's compute it:
        //   Magic u32 @0, Version u16 @4, Flags u16 @6, SourceFingerprint[32] @8, SourceVersion u16 @40, ChunkerVersion u16 @42,
        //   SectionTableOffset i64 @44, SectionTableLength i64 @52, CreatedUtcTicks i64 @60.
        const int CreatedUtcTicksOffset = 60;
        const int CreatedUtcTicksLength = 8;
        for (var i = 0; i < a.Length; i++)
        {
            if (i >= CreatedUtcTicksOffset && i < CreatedUtcTicksOffset + CreatedUtcTicksLength)
            {
                continue; // skip CreatedUtcTicks — legitimate diff.
            }
            if (a[i] != b[i])
            {
                Assert.Fail($"byte mismatch at offset {i}: A=0x{a[i]:X2} B=0x{b[i]:X2}");
            }
        }
    }

    /// <summary>
    /// Writes a minimal <c>.typhon-trace</c> v3 file with the given tick + event distribution. Each record is 12 bytes
    /// (common header only); first record per tick is <see cref="TraceEventKind.TickStart"/>, the rest are
    /// <see cref="TraceEventKind.Instant"/>.
    /// </summary>
    private static void WriteSyntheticTrace(string path, int ticks, int eventsPerTick)
    {
        using var fs = File.Create(path);
        using var writer = new TraceFileWriter(fs);

        var header = new TraceFileHeader
        {
            Magic = TraceFileHeader.MagicValue,
            Version = TraceFileHeader.CurrentVersion,
            Flags = 0,
            TimestampFrequency = 10_000_000,
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

        const int maxRecordsPerBlock = TraceFileWriter.MaxBlockBytes / CommonHeaderSize;
        var blockBuf = new byte[maxRecordsPerBlock * CommonHeaderSize];

        var totalRecords = ticks * eventsPerTick;
        var written = 0;
        var perTickIdx = 0;
        long ts = 100;
        while (written < totalRecords)
        {
            var recordsThisBlock = Math.Min(maxRecordsPerBlock, totalRecords - written);
            for (var r = 0; r < recordsThisBlock; r++)
            {
                var isTickStart = perTickIdx == 0;
                var kind = isTickStart ? (byte)TraceEventKind.TickStart : (byte)TraceEventKind.Instant;
                var offset = r * CommonHeaderSize;
                BinaryPrimitives.WriteUInt16LittleEndian(blockBuf.AsSpan(offset, 2), CommonHeaderSize);
                blockBuf[offset + 2] = kind;
                blockBuf[offset + 3] = 0;
                BinaryPrimitives.WriteInt64LittleEndian(blockBuf.AsSpan(offset + 4, 8), ts);
                ts++;
                written++;
                perTickIdx++;
                if (perTickIdx >= eventsPerTick)
                {
                    perTickIdx = 0;
                }
            }
            writer.WriteRecords(blockBuf.AsSpan(0, recordsThisBlock * CommonHeaderSize), recordsThisBlock);
        }

        writer.WriteSpanNames(new Dictionary<int, string>());
        writer.Flush();
    }
}
