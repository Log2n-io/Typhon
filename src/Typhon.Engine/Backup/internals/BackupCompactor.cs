using System;
using System.Diagnostics;
using System.IO;

namespace Typhon.Engine.Internals;

/// <summary>
/// Turns point X into a base without touching the database (05 §2, BKP-13): <c>{X}-C.tpack</c>, X's header with <c>Kind = Compacted</c>, holding
/// every page of X's image — each the newest version along X's chain, its stored frame copied byte for byte after one decode against its
/// <c>PageCrc</c>, a hole as a zero entry — X's suspects where they came from, and X's tail. Published only once a full verify of the sealed temp
/// passed. Nothing is deleted: that is retention's job.
/// </summary>
/// <remarks>
/// Runs under the caller's <c>store.lock</c> (a maintenance session, or the engine's capture session). Memory is the cursor's read windows (within
/// <see cref="BackupLifecycleOptions.ReadBudgetBytes"/>) and the writer's two buffers, all native; one decoded page on the stack; nothing managed per page.
/// </remarks>
internal static class BackupCompactor
{
    /// <summary>Cancellation and progress every this many pages (4 MiB of image).</summary>
    private const int CheckEveryPages = 512;

    /// <summary>
    /// Compacts <paramref name="pointId"/>. A point that is already a base (full, or compacted before) is a no-op. Throws
    /// <see cref="BackupChainBrokenException"/> when it does not resolve, <see cref="BackupPackCorruptException"/> when a page of its chain or the
    /// compacted pack fails a check, <see cref="IOException"/> when the volume is short of the space a whole image needs (DL-12). On any failure
    /// nothing is visible and the temp is gone.
    /// </summary>
    public static BackupCompactionResult Compact(BackupStore session, uint pointId, BackupLifecycleOptions options)
    {
        options ??= new BackupLifecycleOptions();
        var clock = Stopwatch.StartNew();
        var view = session.View;
        var chain = ChainResolver.Resolve(view, pointId);
        if (chain.Point.IsBase)
        {
            return new BackupCompactionResult(BackupOperations.Summarize(chain.Point), true, chain.Depth, chain.Point.ImagePages, 0, 0, 0, clock.Elapsed);
        }

        var fs = session.FileSystem;
        var chainBytes = ChainBytes(chain);
        if (fs.TryGetAvailableFreeSpace(view.Paths.Packs, out var free) && free < chainBytes)
        {
            throw new IOException($"Compacting point #{pointId} needs up to {chainBytes} bytes on the store's volume ({free} free): nothing was written.");
        }

        var packs = PackChainPageSource.OpenPacks(fs, view, chain);
        ChainPageCursor cursor = null;
        PackWriter writer = null;
        try
        {
            var suspects = new uint[packs.Length][];
            for (var k = 0; k < packs.Length; k++)
            {
                suspects[k] = packs[k].LoadSuspects();
            }

            var point = packs[^1];
            writer = session.BeginCompaction(chain.Point, options.Cancellation);
            writer.SetImage(point.Header.ImagePages, point.Header.CheckpointLsn, point.Header.FreezeUtcTicks);
            cursor = new ChainPageCursor(packs, options.ReadBudgetBytes);
            Span<byte> page = stackalloc byte[PackFormat.PageSize];
            var holes = 0;
            while (cursor.MoveNext(out var p, out var k, out var entry))
            {
                if (k < 0)
                {
                    writer.AddZeroPage(p, suspect: false);
                    holes++;
                }
                else
                {
                    // The suspect set follows the page: suspect in X-C iff suspect in the pack its newest version came from.
                    var suspect = suspects[k].Length > 0 && Array.BinarySearch(suspects[k], (uint)p) >= 0;
                    if (entry.StoredLength == 0)
                    {
                        writer.AddZeroPage(p, suspect);
                    }
                    else
                    {
                        if (!cursor.Frames(k).TryReadFrame(entry, out var frame, out var error) || !PackReader.DecodePage(entry, frame, page, out error))
                        {
                            PackFormat.ThrowCorrupt(cursor.Pack(k).Path, "Page", p, error);
                        }

                        writer.AddStoredPage(p, page, frame[PackFormat.FramePrefixSize..], entry.PageCrc, suspect);
                    }
                }

                if ((p + 1) % CheckEveryPages == 0)
                {
                    options.Cancellation.ThrowIfCancellationRequested();
                    options.Progress?.Invoke(BytesRead(packs), chainBytes);
                }
            }

            cursor.Complete();
            var tail = point.LoadTailDirectory();
            for (var i = 0; i < tail.Segments.Length; i++)
            {
                point.CopyTailSegment(tail, i, writer);
            }

            options.Cancellation.ThrowIfCancellationRequested();
            var info = writer.Complete(point.Footer.PointLsn, point.Footer.TailEndUtcTicks);
            var bytesRead = BytesRead(packs);
            options.Progress?.Invoke(bytesRead, chainBytes);
            BackupLog.PointCompacted(session.Log, pointId, packs.Length, info.ImagePages, holes, bytesRead, info.PackBytes, clock.ElapsedMilliseconds);
            return new BackupCompactionResult(BackupOperations.Summarize(session.View.Find(pointId).Compacted), false, packs.Length, info.ImagePages,
                holes, bytesRead, info.PackBytes, clock.Elapsed);
        }
        catch
        {
            writer?.Abort();
            throw;
        }
        finally
        {
            cursor?.Dispose();
            foreach (var p in packs)
            {
                p.Dispose();
            }
        }
    }

    /// <summary>The bytes of a chain's packs: what a compaction reads, and the bound of what it writes (each page once, plus its index).</summary>
    internal static long ChainBytes(ResolvedChain chain)
    {
        long bytes = 0;
        foreach (var p in chain.Packs)
        {
            bytes += p.Size;
        }

        return bytes;
    }

    private static long BytesRead(PackReader[] packs)
    {
        long bytes = 0;
        foreach (var p in packs)
        {
            bytes += p.BytesRead;
        }

        return bytes;
    }
}
