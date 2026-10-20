using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>One record as a frame walk sees it.</summary>
internal readonly record struct WalFrameRecord(long Lsn, long Tsn, bool IsTxBegin, bool IsTxCommit, bool IsFence, int Chunk);

/// <summary>One frame of a segment: its file offsets, its header's last LSN, the chunks and records it holds.</summary>
internal sealed record WalFrame(int Start, int End, long LastLsn, int Chunks, List<WalFrameRecord> Records);

/// <summary>Test-side frame walk over a segment's bytes, through the WAL's own reader and codec; and a workload that writes interesting frames.</summary>
internal static class WalFrames
{
    /// <summary>A reader over <paramref name="bytes"/> as segment <c>s.wal</c> (zeros past them, as a restored segment holds them).</summary>
    public static WalSegmentReader Open(byte[] bytes, out string path)
    {
        path = Path.Combine("archive-test", "s.wal");
        var view = WalTailView.OverPrefixes(new Dictionary<string, ReadOnlyMemory<byte>> { [path] = bytes });
        var reader = new WalSegmentReader(view);
        Assert.That(reader.OpenSegment(path), Is.True, "the segment's header is valid");
        return reader;
    }

    /// <summary>Every frame read whole, file offsets; <paramref name="truncated"/> when the walk stopped at a corruption.</summary>
    public static List<WalFrame> Read(byte[] bytes, out bool truncated)
    {
        using var reader = Open(bytes, out _);
        var frames = new List<WalFrame>();
        WalFrame current = null;
        var chunk = 0;
        while (reader.TryReadNext(out var header, out var body))
        {
            if (current == null || WalSegmentHeader.SizeInBytes + reader.FrameStart != current.Start)
            {
                current = new WalFrame(WalSegmentHeader.SizeInBytes + reader.FrameStart, WalSegmentHeader.SizeInBytes + reader.FrameEnd, reader.FrameLastLsn, 0,
                    []);
                frames.Add(current);
                chunk = 0;
            }

            current = current with { Chunks = current.Chunks + 1 };
            frames[^1] = current;
            if (header.ChunkType == (ushort)WalChunkType.Transaction)
            {
                var offset = 0;
                while (RecordCodec.TryReadRecord(body, offset, out var consumed, out var view))
                {
                    offset += consumed;
                    if (!view.IsUnknownKind)
                    {
                        current.Records.Add(new WalFrameRecord(view.Lsn, view.Tsn, view.IsTxBegin, view.IsTxCommit, view.IsFence, chunk));
                    }
                }
            }
            else if (body.Length >= 8)
            {
                current.Records.Add(new WalFrameRecord(MemoryMarshal.Read<long>(body), 0, false, false, false, chunk));
            }

            chunk++;
        }

        truncated = reader.WasTruncated;
        if (truncated && frames.Count > 0 && frames[^1].End > WalSegmentHeader.SizeInBytes + reader.LastCompleteFrameEnd)
        {
            frames.RemoveAt(frames.Count - 1);
        }

        return frames;
    }

    /// <summary>The bytes of every segment file in <paramref name="walDirectory"/>, ascending id, read beside the engine.</summary>
    public static List<(long Id, byte[] Bytes)> ReadSegments(string walDirectory)
    {
        var result = new List<(long, byte[])>();
        foreach (var path in Directory.GetFiles(walDirectory, "*.wal"))
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bytes = new byte[fs.Length];
            fs.ReadExactly(bytes);
            if (MemoryMarshal.Read<WalSegmentHeader>(bytes).Validate())
            {
                result.Add((long.Parse(Path.GetFileNameWithoutExtension(path)), bytes));
            }
        }

        result.Sort(static (a, b) => a.Item1.CompareTo(b.Item1));
        return result;
    }

    /// <summary>
    /// Writes small commits, a transaction larger than one chunk, SingleVersion updates and a multi-chunk tick fence, then more commits, and seals the
    /// segment. Returns the LSN of every commit marker the workload made durable, ascending.
    /// </summary>
    public static void Workload(DatabaseEngine dbe, int bigTransaction = 6000, int fencedEntities = 6000)
    {
        for (var i = 0; i < 5; i++)
        {
            Commit(dbe, 3, i * 10);
        }

        Commit(dbe, bigTransaction, 1_000);

        var sv = new List<EntityId>(fencedEntities);
        using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
        {
            for (var i = 0; i < fencedEntities; i++)
            {
                sv.Add(t.Spawn<SvTestArchetype>(SvTestArchetype.SvComp.Set(new CompSmSingleVersion(i))));
            }

            t.Commit();
        }

        using (var t = dbe.CreateQuickTransaction())
        {
            foreach (var id in sv)
            {
                t.OpenMut(id).Set(SvTestArchetype.SvComp, new CompSmSingleVersion(-1));
            }

            t.Commit();
        }

        dbe.WriteTickFence(1);
        for (var i = 0; i < 5; i++)
        {
            Commit(dbe, 3, 100 + i * 10);
        }

        var wal = dbe.WalManager;
        wal.RequestFlush();
        var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(10));
        wal.WaitForDurable(wal.LastPublishedLsn, ref ctx);
    }

    private static void Commit(DatabaseEngine dbe, int count, int seed)
    {
        using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
        for (var i = 0; i < count; i++)
        {
            t.Spawn<CompAArch>(CompAArch.A.Set(new CompA(seed + i)));
        }

        t.Commit();
    }
}
