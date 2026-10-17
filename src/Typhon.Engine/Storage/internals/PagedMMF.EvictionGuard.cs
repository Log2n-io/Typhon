// CS1591: PagedMMF is public in the internal namespace (see PagedMMF.cs); not part of the published API reference.
#pragma warning disable 1591

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

public partial class PagedMMF
{
    /// <summary>
    /// Test-only (#1382): how many evicted slots sit out a quarantine before their reuse, their memory inaccessible; 0 for none. Set once per process,
    /// before any engine exists, as <see cref="PoisonCacheForProcess"/> is. A stress run sets it to make a page address kept past its page's protection
    /// fault at the access, with its stack, instead of reading or writing whatever page the slot holds next — a use-after-free that otherwise shows
    /// only as corruption found later, if at all. Windows and Linux; the fault ends the process, so never in a suite run.
    /// </summary>
    internal static int EvictionGuardDepthForProcess;

    private EvictionGuard _evictionGuard;

    /// <summary>Test seam: starts the quarantine on this cache only, from now on.</summary>
    internal unsafe void EnableEvictionGuardForTests(int depth) => _evictionGuard ??= new EvictionGuard(_memPagesAddr, MemPagesCount, depth);

    /// <summary>Test seam: the slots the quarantine holds now, oldest first.</summary>
    internal int[] QuarantinedSlotsForTests => _evictionGuard?.Held() ?? [];

    /// <summary>
    /// The quarantine of <see cref="EvictionGuardDepthForProcess"/>: a slot reclaimed for a new page is made inaccessible and queued, and the slot handed
    /// out in its place is the oldest queued one, made accessible again. Its slots stay Allocating, out of the directory, so nothing reclaims them.
    /// </summary>
    private sealed unsafe class EvictionGuard
    {
        private readonly byte* _base;
        private readonly int _depth;
        private readonly Queue<int> _held = new();
        private bool _released;   // under _held: set by ReleaseAll, after which nothing is quarantined

        internal EvictionGuard(byte* memPagesAddr, int pageCount, int depth)
        {
            if (((nint)memPagesAddr & (Environment.SystemPageSize - 1)) != 0 || PageSize % Environment.SystemPageSize != 0)
            {
                throw new InvalidOperationException("The eviction guard needs cache pages aligned on the system's pages.");
            }

            _base = memPagesAddr;
            // Leaves the cache most of its slots; and on Linux each protected region splits a mapping, which vm.max_map_count (65,530 by default)
            // bounds, so never more than MaxDepth.
            _depth = Math.Clamp(depth, 1, Math.Max(1, Math.Min(pageCount / 8, MaxDepth)));
        }

        /// <summary>
        /// Quarantines the reclaimed slot <paramref name="memPageIndex"/> and replaces it by the oldest quarantined one. False while the quarantine
        /// is filling: it kept the slot and has none to give back, so the caller looks for another.
        /// </summary>
        internal bool Exchange(ref int memPageIndex)
        {
            Protect(memPageIndex, false);
            lock (_held)
            {
                if (_released)
                {
                    // The cache is being disposed: the slot is used as it is, never left inaccessible over a block about to be freed.
                    Protect(memPageIndex, true);
                    return true;
                }

                _held.Enqueue(memPageIndex);
                if (_held.Count <= _depth)
                {
                    return false;
                }

                memPageIndex = _held.Dequeue();
            }

            Protect(memPageIndex, true);
            return true;
        }

        internal int[] Held()
        {
            lock (_held)
            {
                return _held.ToArray();
            }
        }

        /// <summary>Makes every quarantined slot accessible again: before the cache block is freed.</summary>
        internal void ReleaseAll()
        {
            lock (_held)
            {
                _released = true;
                while (_held.Count > 0)
                {
                    Protect(_held.Dequeue(), true);
                }
            }
        }

        private void Protect(int memPageIndex, bool accessible)
        {
            var address = _base + memPageIndex * (long)PageSize;
            bool done;
            if (OperatingSystem.IsWindows())
            {
                done = VirtualProtect(address, PageSize, accessible ? PageReadWrite : PageNoAccess, out _);
            }
            else
            {
                done = mprotect(address, PageSize, accessible ? ProtReadWrite : ProtNone) == 0;
            }

            if (!done)
            {
                throw new InvalidOperationException($"The eviction guard could not protect slot {memPageIndex} (error {Marshal.GetLastPInvokeError()}).");
            }
        }

        private const int MaxDepth = 4096;
        private const uint PageNoAccess = 0x01;
        private const uint PageReadWrite = 0x04;
        private const int ProtNone = 0;
        private const int ProtReadWrite = 3;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualProtect(void* address, nuint size, uint newProtect, out uint oldProtect);

        [DllImport("libc", SetLastError = true)]
        private static extern int mprotect(void* address, nuint size, int protection);
    }
}
