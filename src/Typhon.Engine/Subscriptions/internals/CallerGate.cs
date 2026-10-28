using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// The quiesce-before-free gate of an object whose native memory threads it does not own reach: a count of the callers inside, and a latch in its sign bit.
/// </summary>
/// <remarks>
/// <para>
/// A caller takes a reference with <see cref="TryEnter"/> for the length of one call and drops it with <see cref="Exit"/>. <see cref="LatchAndDrain"/>
/// closes the gate to new callers and waits for the ones already inside to leave; only then may the owner free what they were reading. A call that arrives
/// after the latch is refused rather than let in, and answers as it would for something that is gone. Freeing on a "not disposed yet" flag read instead is a
/// check-then-use: a caller parked between the check and the read reads the memory after it is freed.
/// </para>
/// <para>
/// One cache line of its own: transport threads move it on every call, and it must not share a line with what the owner's tick writes. A mutable struct held
/// in a field — never copy it.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Explicit, Size = 64)]
internal struct CallerGate
{
    /// <summary>The latch, in the sign bit: set once, never cleared.</summary>
    private const int LatchedBit = unchecked((int)0x8000_0000);

    [FieldOffset(0)]
    private int _callers;

    /// <summary>Whether <see cref="LatchAndDrain"/> has closed the gate.</summary>
    public bool IsLatched => Volatile.Read(ref _callers) < 0;

    /// <summary>Takes a reference for the duration of one call.</summary>
    /// <returns><see langword="false"/> once the gate has latched; the caller must touch nothing it guards.</returns>
    public bool TryEnter()
    {
        while (true)
        {
            var observed = Volatile.Read(ref _callers);
            if (observed < 0)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _callers, observed + 1, observed) == observed)
            {
                return true;
            }
        }
    }

    /// <summary>Drops the reference taken by <see cref="TryEnter"/>. The count lives in the low bits, so this never borrows into the latch.</summary>
    public void Exit() => Interlocked.Decrement(ref _callers);

    /// <summary>Closes the gate to new callers and waits until every caller already inside has left.</summary>
    /// <returns><see langword="false"/> when the gate had already latched: a second disposal, which must do nothing.</returns>
    public bool LatchAndDrain()
    {
        while (true)
        {
            var observed = Volatile.Read(ref _callers);
            if (observed < 0)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _callers, observed | LatchedBit, observed) == observed)
            {
                break;
            }
        }

        var spin = new SpinWait();
        while ((Volatile.Read(ref _callers) & ~LatchedBit) != 0)
        {
            spin.SpinOnce();
        }

        return true;
    }
}
