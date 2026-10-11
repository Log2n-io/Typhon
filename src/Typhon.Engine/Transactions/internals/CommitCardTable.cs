using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// The commit cards of option C (design <c>Revision/05</c> §3.2, VIS-03): a committer holds one from before its PREPARE until its last effect is stamped,
/// and a reader that meets an effect still flagged with its owner's TSN finds the owner's commit stamp here. One <c>{Owner, Stamp}</c> pair per 64-byte card,
/// so no two committers share a cache line; a commit's window is the <see cref="WindowSlots"/> cards from <c>Owner &amp; (Size − 1)</c>.
/// </summary>
/// <remarks>
/// <para>
/// A card's <c>Owner</c> is 0 when free, <c>−TSN</c> while reserved, <c>TSN</c> once registered at the commit point. <c>Stamp</c> is 0 until the stamp is
/// drawn. A reader matches a positive owner only, and reads <c>(Owner, Stamp, Owner)</c>: TSNs are unique and a reservation resets <c>Stamp</c> before the
/// owner can be its new one, so a stamp read between two equal owner reads is that owner's.
/// </para>
/// <para>
/// A pinned block from the engine's allocator, freed with the engine: the cards are addressed by pointer (CLAUDE.md, no pointers over GC memory).
/// </para>
/// </remarks>
internal sealed unsafe class CommitCardTable : IDisposable
{
    /// <summary>Cards in the table: 4 MiB padded.</summary>
    internal const int DefaultSize = 1 << 16;

    /// <summary>Cards in one owner's window: a commit takes the first free one, a reader looks for its owner in all of them.</summary>
    internal const int WindowSlots = 4;

    private const int CardBytes = 64;
    private const int StampOffset = 8;

    private readonly TransactionChain _chain;
    private readonly int _mask;
    private PinnedMemoryBlock _block;
    private byte* _cards;

    internal CommitCardTable(TransactionChain chain, IMemoryAllocator allocator, IResource parent, int size = DefaultSize)
    {
        ArgumentNullException.ThrowIfNull(chain);
        ArgumentNullException.ThrowIfNull(allocator);
        if (size < WindowSlots || (size & (size - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, "A power of two, at least the window.");
        }

        _chain = chain;
        _mask = size - 1;
        _block = allocator.AllocatePinned("CommitCards", parent, size * CardBytes, zeroed: true, alignment: CardBytes);
        _cards = _block.DataAsPointer;
    }

    /// <summary>Cards in the table.</summary>
    internal int Size => _mask + 1;

    /// <summary>Reservations that found their window full and waited (diagnostics and tests).</summary>
    internal static long FullWindowWaits;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref long OwnerAt(int slot) => ref Unsafe.AsRef<long>(_cards + ((long)slot * CardBytes));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref long StampAt(int slot) => ref Unsafe.AsRef<long>(_cards + ((long)slot * CardBytes) + StampOffset);

    /// <summary>The first card of <paramref name="owner"/>'s window.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int WindowStart(long owner) => (int)(owner & _mask);

    /// <summary>
    /// Step 0 of VIS-03: takes a free card of <paramref name="tsn"/>'s window, its owner <c>−TSN</c> and its stamp reset. False when the window is full: the
    /// caller waits and retries, holding no chain lock and no WAL claim.
    /// </summary>
    internal bool TryReserve(long tsn, out int slot)
    {
        var start = WindowStart(tsn);
        for (var i = 0; i < WindowSlots; i++)
        {
            var s = (start + i) & _mask;
            if (Volatile.Read(ref OwnerAt(s)) == 0 && Interlocked.CompareExchange(ref OwnerAt(s), -tsn, 0) == 0)
            {
                // Plain: ordered before the owner's step-1 store by its release, and no reader matches a negative owner.
                StampAt(s) = 0;
                slot = s;
                return true;
            }
        }

        slot = -1;
        return false;
    }

    /// <summary>
    /// Steps 1–3 of VIS-03, at the commit point (nothing after it fails): the card registered with <paramref name="tsn"/>, the stamp drawn from the TSN
    /// counter, stored. Registered before the draw: a reader whose snapshot is above the stamp drew it after this store, by its own RMW on the counter, and
    /// finds the card.
    /// </summary>
    internal long Register(int slot, long tsn)
    {
        Volatile.Write(ref OwnerAt(slot), tsn);
        var stamp = _chain.DrawCommitStamp();
        Volatile.Write(ref StampAt(slot), stamp);
        return stamp;
    }

    /// <summary>Gives the card back (step 6, or any exit before the commit point): free for the window's next committer.</summary>
    internal void Release(int slot) => Volatile.Write(ref OwnerAt(slot), 0);

    /// <summary>
    /// The lookup of VIS-01: <paramref name="owner"/>'s commit stamp when it is registered, spinning through the three-instruction window between its
    /// register and its stamp store. False when no card of its window holds it: the owner is not committing.
    /// </summary>
    internal bool TryGetStamp(long owner, out long stamp)
    {
        var start = WindowStart(owner);
        var spinner = new SpinWait();
        while (true)
        {
            var found = false;
            for (var i = 0; i < WindowSlots; i++)
            {
                var s = (start + i) & _mask;
                if (Volatile.Read(ref OwnerAt(s)) != owner)
                {
                    continue;
                }

                var st = Volatile.Read(ref StampAt(s));
                if (Volatile.Read(ref OwnerAt(s)) != owner)
                {
                    continue;
                }

                if (st != 0)
                {
                    stamp = st;
                    return true;
                }

                found = true;
            }

            if (!found)
            {
                stamp = 0;
                return false;
            }

            spinner.SpinOnce(sleep1Threshold: -1);
        }
    }

    /// <summary>The owner word of <paramref name="slot"/>, for tests: 0 free, <c>−TSN</c> reserved, <c>TSN</c> registered.</summary>
    internal long OwnerOfSlot(int slot) => Volatile.Read(ref OwnerAt(slot));

    /// <summary>Cards not free (tests: a leaked reservation shows here).</summary>
    internal int CountHeld()
    {
        var n = 0;
        for (var s = 0; s <= _mask; s++)
        {
            if (Volatile.Read(ref OwnerAt(s)) != 0)
            {
                n++;
            }
        }

        return n;
    }

    public void Dispose()
    {
        _cards = null;
        _block?.Dispose();
        _block = null;
    }
}
