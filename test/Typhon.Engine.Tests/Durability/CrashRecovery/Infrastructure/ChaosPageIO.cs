using System;
using System.Collections.Generic;
using System.IO;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// Layer-2 data-file fault injector (P1.5). Unlike the WAL-side <see cref="ChaosWalFileIO"/> (which substitutes the WAL backend),
/// this <b>records and may throw</b> through <see cref="PagedMMF"/>'s test-only write/flush interceptors — it does NOT replace the
/// real disk I/O. That keeps reads and <c>_fileSize</c> real, so a reopened engine recovers through the genuine path
/// (cold loads, CRC-on-load, suspect-mode). After the crash, <see cref="DamagePageOnDisk"/> mutates a specific page in the real
/// data file (the engine is disposed → handle freed), simulating a torn/zeroed checkpoint write.
/// <para>
/// Design: <c>claude/design/Durability/crash-recovery-testing.md</c> §5.2 (delegate injection, OQ-1) — adapted to record-and-damage
/// rather than a Dict-backed sim disk (FPI is gone; recovery heals by re-derive (RB-01) or fails loudly (RB-04)).
/// </para>
/// </summary>
internal sealed class ChaosPageIO
{
    private readonly List<int> _writeLog = new();
    private int _flushBarrierCount;
    private int _crashAtWrite = int.MaxValue;
    private bool _hasCrashed;

    /// <summary>File page indices in physical write order, up to the crash (the crashing write is NOT recorded — it never lands).</summary>
    public IReadOnlyList<int> WrittenPages => _writeLog;

    /// <summary>Count of physical page writes that completed before the crash.</summary>
    public int TotalWriteCount => _writeLog.Count;

    /// <summary>Count of fsync barriers (<see cref="PagedMMF.FlushToDisk"/>) observed before the crash.</summary>
    public int FlushBarrierCount => _flushBarrierCount;

    /// <summary>Whether the simulated crash has fired.</summary>
    public bool HasCrashed => _hasCrashed;

    /// <summary>Configure a crash at the Nth (1-based) physical page write: writes 1..N-1 land on disk, the Nth and all after never land.</summary>
    public void SetCrashAtPageWrite(int n) => _crashAtWrite = n;

    /// <summary>Wire the interceptors onto a live <see cref="PagedMMF"/> (or <see cref="ManagedPagedMMF"/>).</summary>
    public void WireTo(PagedMMF mmf)
    {
        ArgumentNullException.ThrowIfNull(mmf);
        mmf.PageWriteInterceptor = OnPageWrite;
        mmf.FlushToDiskInterceptor = OnFlush;
    }

    /// <summary>Remove the interceptors (restore the real I/O path).</summary>
    public void Unwire(PagedMMF mmf)
    {
        if (mmf == null)
        {
            return;
        }

        mmf.PageWriteInterceptor = null;
        mmf.FlushToDiskInterceptor = null;
    }

    private void OnPageWrite(int filePageIndex)
    {
        if (_hasCrashed)
        {
            throw new ChaosSimulatedCrashException(_writeLog.Count, IoSubsystem.DataFile);
        }

        // Crash BEFORE the Nth write performs: the page never lands, the cycle aborts (CheckpointLSN does not advance → WAL window intact).
        if (_writeLog.Count + 1 >= _crashAtWrite)
        {
            _hasCrashed = true;
            throw new ChaosSimulatedCrashException(_writeLog.Count + 1, IoSubsystem.DataFile);
        }

        _writeLog.Add(filePageIndex);
    }

    private void OnFlush()
    {
        if (!_hasCrashed)
        {
            _flushBarrierCount++;
        }
    }

    /// <summary>
    /// Mutate one page in the real data file post-crash, simulating a torn/zeroed write. <see cref="PageDamageType.MissedPage"/> is a no-op
    /// (the page keeps its pre-checkpoint content). Pages beyond EOF are skipped (nothing was written there).
    /// </summary>
    public static void DamagePageOnDisk(string dataFilePath, int filePageIndex, PageDamageType damage, int pageSize)
    {
        ArgumentNullException.ThrowIfNull(dataFilePath);

        if (damage == PageDamageType.MissedPage)
        {
            return;
        }

        using var fs = new FileStream(dataFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var offset = (long)filePageIndex * pageSize;
        if (offset + pageSize > fs.Length)
        {
            return;
        }

        var buf = new byte[pageSize];
        if (damage == PageDamageType.TornPage)
        {
            // Keep the first 4 KiB, overwrite the second 4 KiB with 0xFF — the two sectors disagree, so the page CRC fails on load.
            fs.Seek(offset, SeekOrigin.Begin);
            fs.ReadExactly(buf, 0, pageSize);
            Array.Fill(buf, (byte)0xFF, pageSize / 2, pageSize - pageSize / 2);
        }
        // ZeroPage: buf stays all-zero.

        fs.Seek(offset, SeekOrigin.Begin);
        fs.Write(buf, 0, pageSize);
        fs.Flush(true);
    }
}
