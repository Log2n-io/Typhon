using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Typhon.Profiler;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// The cache's thread table (v19): every thread the capture named, wherever its <c>ThreadInfo</c> record fell, so the Workbench labels every lane
/// without having loaded the chunk that holds the name; and a start-up that overflows the pre-tick buffer no longer drops the names.
/// </summary>
[TestFixture]
public class ThreadTableCacheTests
{
    [Test]
    public void The_table_round_trips_and_a_long_name_is_cut_on_a_character_boundary()
    {
        var path = Path.Combine(Path.GetTempPath(), $"thread-table-{Guid.NewGuid():N}.typhon-trace-cache");
        try
        {
            var longName = new string('é', 200); // 400 UTF-8 bytes, past the short string's 255
            using (var sink = FileCacheSink.Create(path))
            {
                WriteEmptyTrailer(sink, [new ThreadTableEntry(3, ThreadKind.Worker, "Typhon.Worker-2"), new ThreadTableEntry(48, ThreadKind.Pool, longName)]);
            }

            using var reader = new TraceFileCacheReader(File.OpenRead(path));
            Assert.That(reader.Threads, Has.Count.EqualTo(2));
            Assert.That(reader.Threads[0], Is.EqualTo(new ThreadTableEntry(3, ThreadKind.Worker, "Typhon.Worker-2")));
            Assert.Multiple(() =>
            {
                Assert.That((reader.Threads[1].Slot, reader.Threads[1].Kind), Is.EqualTo(((byte)48, ThreadKind.Pool)));
                Assert.That(Encoding.UTF8.GetByteCount(reader.Threads[1].Name), Is.LessThanOrEqualTo(255));
                Assert.That(longName, Does.StartWith(reader.Threads[1].Name), "cut between characters, not inside one");
            });
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void Every_thread_is_named_even_past_the_pre_tick_cap_and_after_the_first_tick()
    {
        var sink = new CapturingSink();
        using (var builder = new IncrementalCacheBuilder(sink, ownsSink: false, new ProfilerHeader { Version = 13, TimestampFrequency = 10_000_000 },
            new byte[32], new Dictionary<int, string>(), new Dictionary<ushort, string>()))
        {
            // A start-up that writes more than the pre-tick buffer holds, then names a thread: the name must survive the cap.
            // Header-only records, so the buffer fills to the byte and no room is left for even a small record past it.
            var filler = new byte[(IncrementalCacheBuilder.PreTickBufferCap / TraceRecordHeader.CommonHeaderSize + 10) * TraceRecordHeader.CommonHeaderSize];
            for (var pos = 0; pos < filler.Length; pos += TraceRecordHeader.CommonHeaderSize)
            {
                TraceRecordHeader.WriteCommonHeader(filler.AsSpan(pos), TraceRecordHeader.CommonHeaderSize, TraceEventKind.GcStart, 1, 1);
            }

            builder.FeedRawRecords(filler);

            builder.FeedRawRecords(ThreadInfo(slot: 9, ts: 2, "Typhon.Worker-8", ThreadKind.Worker));
            builder.FeedRawRecords(Record(TraceEventKind.TickStart, slot: 0, ts: 3, payload: 0));
            // A thread that first records something mid-run, its name in a later chunk.
            builder.FeedRawRecords(ThreadInfo(slot: 48, ts: 4, ".NET TP Worker", ThreadKind.Pool));
            builder.FeedRawRecords(Record(TraceEventKind.TickStart, slot: 0, ts: 5, payload: 0));

            Assert.That(builder.PreTickDroppedBytes, Is.GreaterThan(0), "the filler did overflow the cap");
        }

        Assert.That(sink.Threads.Select(t => (t.Slot, t.Name, t.Kind)), Is.EqualTo(new[]
        {
            ((byte)9, "Typhon.Worker-8", ThreadKind.Worker), ((byte)48, ".NET TP Worker", ThreadKind.Pool),
        }));
        Assert.That(KindsIn(sink.Chunks[0]), Does.Contain(TraceEventKind.ThreadInfo), "the pre-tick ThreadInfo still reaches the tick-0 chunk");
    }

    private static byte[] Record(TraceEventKind kind, byte slot, long ts, int payload)
    {
        var r = new byte[TraceRecordHeader.CommonHeaderSize + payload];
        TraceRecordHeader.WriteCommonHeader(r, (ushort)r.Length, kind, slot, ts);
        return r;
    }

    private static byte[] ThreadInfo(byte slot, long ts, string name, ThreadKind kind)
    {
        var nameUtf8 = Encoding.UTF8.GetBytes(name);
        var r = new byte[ThreadInfoEventCodec.ComputeSize(nameUtf8.Length)];
        ThreadInfoEventCodec.WriteThreadInfo(r, slot, ts, managedThreadId: 7, nameUtf8, kind, out _);
        return r;
    }

    private static List<TraceEventKind> KindsIn(byte[] chunk)
    {
        var kinds = new List<TraceEventKind>();
        for (var pos = 0; pos + 3 <= chunk.Length;)
        {
            var size = BinaryPrimitives.ReadUInt16LittleEndian(chunk.AsSpan(pos));
            if (size < 3) break;
            kinds.Add((TraceEventKind)chunk[pos + 2]);
            pos += size;
        }
        return kinds;
    }

    private static void WriteEmptyTrailer(ICacheChunkSink sink, IReadOnlyList<ThreadTableEntry> threads)
    {
        var header = new CacheHeader
        {
            Magic = CacheHeader.MagicValue, Version = CacheHeader.CurrentVersion, ChunkerVersion = TraceFileCacheConstants.CurrentChunkerVersion,
        };
        CacheHeader.SetIdentifier(ref header, new byte[32]);
        sink.WriteTrailer([], new GlobalMetricsFixed(), [], [], new Dictionary<int, string>(), default, header, [], [], [], new Dictionary<ushort, string>(), [], [],
            threads);
    }

    private sealed class CapturingSink : ICacheChunkSink
    {
        private long _offset;

        public bool SupportsTrailer => true;

        public List<byte[]> Chunks { get; } = [];

        public IReadOnlyList<ThreadTableEntry> Threads { get; private set; } = [];

        public (long CacheOffset, uint CompressedLength, uint UncompressedLength) AppendChunk(ReadOnlySpan<byte> uncompressedRecords)
        {
            Chunks.Add(uncompressedRecords.ToArray());
            var off = _offset;
            _offset += uncompressedRecords.Length;
            return (off, (uint)uncompressedRecords.Length, (uint)uncompressedRecords.Length);
        }

        public void WriteTrailer(IReadOnlyList<TickSummary> tickSummaries, in GlobalMetricsFixed globalMetrics,
            IReadOnlyList<SystemAggregateDuration> systemAggregates, IReadOnlyList<ChunkManifestEntry> chunkManifest, IReadOnlyDictionary<int, string> spanNames,
            ReadOnlySpan<byte> sourceMetadataBytes, in CacheHeader headerTemplate, IReadOnlyList<SystemTickSummary> systemTickSummaries,
            IReadOnlyList<QueueTickSummary> queueTickSummaries, IReadOnlyList<PostTickSummary> postTickSummaries, IReadOnlyDictionary<ushort, string> queueIdToName,
            IReadOnlyList<SystemArchetypeTouchSummary> systemArchetypeTouches, IReadOnlyList<EntityLifecycleRun> entityLifecycleRuns,
            IReadOnlyList<ThreadTableEntry> threads) => Threads = threads;

        public void Dispose() { }
    }
}
