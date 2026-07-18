// unset

using JetBrains.Annotations;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Synchronization type that allows multiple concurrent shared access or one exclusive.
/// Doesn't allow re-entrant calls, burn CPU cycle on wait, using <see cref="SpinWait"/>
/// Costs 4 bytes of data.
/// </summary>
/// <remarks>
/// <para>This is the compact version of <see cref="AccessControl"/> (4 bytes vs 8 bytes).
/// Use this when memory is constrained and you don't need waiter tracking.</para>
/// <para>For blocking operations, pass <c>ref WaitContext</c> to control timeout and cancellation.
/// Use <c>ref WaitContext.Null</c> for infinite wait with zero overhead.</para>
/// <para><b>Thread limit: 32,767 simultaneously-live threads.</b> The thread id occupies bits 16-31 of a
/// signed <see cref="int"/>, so a managed thread id of 32,768 or above would set the sign bit and
/// <see cref="LockedByThreadId"/> would sign-extend on read. This is half the 65,535 that
/// <see cref="AccessControl"/> supports, and it is an accepted bound: the runtime allocates managed thread
/// ids lowest-available-first (ids are recycled when threads die), so the ceiling applies to threads alive
/// at the same instant, not to threads created over the process lifetime. 32K concurrent threads is far
/// beyond any workload this engine targets.</para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
[PublicAPI]
internal struct AccessControlSmall
{
    // ═══════════════════════════════════════════════════════════════════════
    // Bit Layout Constants
    // ═══════════════════════════════════════════════════════════════════════
    // Bit layout, from least to most significant:
    // 15 Shared counter        (bits 0-14, max 32,767)
    //  1 Contention flag       (bit 15)
    // 16 Thread Id             (bits 16-31, max 32,767 — signed backing field, see <remarks>)

    private const int ThreadIdShift = 16;
    private const int SharedUsedCounterMask = 0x0000_7FFF;  // Bits 0-14 (15 bits, max 32,767)
    private const int ContentionFlagMask    = 0x0000_8000;  // Bit 15

    /// <summary>Resets the lock to initial state.</summary>
    public void Reset() => _data = 0;

    private int _data;

    /// <summary>True if the current thread holds exclusive access.</summary>
    public bool IsLockedByCurrentThread => Environment.CurrentManagedThreadId == LockedByThreadId;

    /// <summary>Thread ID holding exclusive access, or 0 if not held.</summary>
    public int LockedByThreadId => _data >> ThreadIdShift;

    /// <summary>Current shared access count.</summary>
    public int SharedUsedCounter => _data & SharedUsedCounterMask;

    /// <summary>
    /// Returns true if this lock has ever experienced contention (a thread had to wait).
    /// This flag is sticky - once set, it remains set until <see cref="Reset"/> is called.
    /// </summary>
    public bool WasContended => (_data & ContentionFlagMask) != 0;

    // ═══════════════════════════════════════════════════════════════════════
    // Helper Methods
    // ═══════════════════════════════════════════════════════════════════════

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowInvalidOperationException(string msg) => throw new InvalidOperationException(msg);

    // ═══════════════════════════════════════════════════════════════════════
    // AtomicChange Helper
    // ═══════════════════════════════════════════════════════════════════════

    private ref struct AtomicChange
    {
        /// <summary>Constructor for blocking operations that may need to wait (Enter, Promote).</summary>
        public AtomicChange(ref int source, ref WaitContext ctx)
        {
            _source = ref source;
            _spinWait = new SpinWait();
            _ctx = ref ctx;
            _isNullRef = Unsafe.IsNullRef(ref ctx);
            Fetch();
        }

        /// <summary>Constructor for non-blocking operations (Exit, ForceCommit) that don't need WaitContext.</summary>
        public AtomicChange(ref int source)
        {
            _source = ref source;
            _spinWait = new SpinWait();
            _ctx = ref Unsafe.NullRef<WaitContext>();
            _isNullRef = true;
            Fetch();
        }

        public int Initial;
        public int NewValue;

        private readonly ref int _source;
        private SpinWait _spinWait;
        private readonly ref WaitContext _ctx;
        private readonly bool _isNullRef;

        public bool ShouldStop
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => !_isNullRef && _ctx.ShouldStop;
        }

        public bool Commit() => Interlocked.CompareExchange(ref _source, NewValue, Initial) == Initial;

        public void Fetch() => Initial = _source;

        public bool Wait()
        {
            if (ShouldStop)
            {
                return false;
            }

            _spinWait.SpinOnce();
            Fetch();
            return true;
        }

        public bool WaitFor(Func<int, bool> predicate)
        {
            Fetch();
            while (true)
            {
                if (predicate(Initial))
                {
                    return true;
                }

                if (!Wait())
                {
                    return false;
                }
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Shared Access
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Enters shared (reader) access. Multiple threads can hold shared access simultaneously.
    /// </summary>
    /// <param name="ctx">Reference to WaitContext for timeout/cancellation. Use <c>ref WaitContext.Null</c> for infinite wait.</param>
    /// <returns>True if access was acquired; false if timed out or cancelled.</returns>
    public bool EnterSharedAccess(ref WaitContext ctx)
    {
        var ac = new AtomicChange(ref _data, ref ctx);
        bool hadToWait = false;

        while (true)
        {
            // Wait for no exclusive holder (ThreadId == 0)
            if ((ac.Initial >> ThreadIdShift) != 0)
            {
                if (!hadToWait)
                {
                    hadToWait = true;

                    // Set contention flag (sticky, atomic) - we had to wait
                    Interlocked.Or(ref _data, ContentionFlagMask);
                    TyphonEvent.EmitConcurrencyAccessControlSmallContention();
                }

                if (!ac.WaitFor(d => (d >> ThreadIdShift) == 0))
                {
                    return false;
                }
            }

            if ((ac.Initial & SharedUsedCounterMask) >= SharedUsedCounterMask)
            {
                ThrowInvalidOperationException("Too many concurrent shared accesses");
            }

            ac.NewValue = ac.Initial + 1;
            if (ac.Commit())
            {
                TyphonEvent.EmitConcurrencyAccessControlSmallSharedAcquire((ushort)Environment.CurrentManagedThreadId);
                return true;
            }

            // CAS failed, re-fetch and retry
            ac.Fetch();
        }
    }

    /// <summary>Exits shared (reader) access.</summary>
    public void ExitSharedAccess()
    {
        // Hand-rolled CAS retry (no delegate) — see #486: a ForceCommit(lambda) here allocated the delegate on this hot path.
        while (true)
        {
            int initial = _data;
            if ((initial & SharedUsedCounterMask) == 0)
            {
                ThrowInvalidOperationException("Exiting shared access without entering it first");
            }

            int newValue = initial - 1;
            if (Interlocked.CompareExchange(ref _data, newValue, initial) == initial)
            {
                break;
            }
        }

        TyphonEvent.EmitConcurrencyAccessControlSmallSharedRelease((ushort)Environment.CurrentManagedThreadId);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Exclusive Access
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Enters exclusive (writer) access. Only one thread can hold exclusive access.</summary>
    /// <param name="ctx">Reference to WaitContext for timeout/cancellation.</param>
    /// <returns>True if access was acquired; false if timed out or cancelled.</returns>
    public bool EnterExclusiveAccess(ref WaitContext ctx)
    {
        var ct = Environment.CurrentManagedThreadId << ThreadIdShift;
        var ac = new AtomicChange(ref _data, ref ctx);

        if ((ac.Initial & ~SharedUsedCounterMask) == ct)
        {
            ThrowInvalidOperationException("Cannot enter exclusive access while already holding it");
        }

        // Fast path - try immediate acquisition (idle state is 0 or just contention flag)
        var initialMasked = ac.Initial & ~ContentionFlagMask;
        if (initialMasked == 0)
        {
            ac.NewValue = ct | (ac.Initial & ContentionFlagMask);  // Preserve contention flag
            if (ac.Commit())
            {
                TyphonEvent.EmitConcurrencyAccessControlSmallExclusiveAcquire((ushort)Environment.CurrentManagedThreadId);
                return true;
            }
        }

        // Slow path - need to wait
        Interlocked.Or(ref _data, ContentionFlagMask);
        TyphonEvent.EmitConcurrencyAccessControlSmallContention();

        while (true)
        {
            // Wait for idle state (ignoring contention flag)
            if (!ac.WaitFor(d => (d & ~ContentionFlagMask) == 0))
            {
                return false;
            }

            ac.NewValue = ct | (ac.Initial & ContentionFlagMask);  // Preserve contention flag
            if (ac.Commit())
            {
                TyphonEvent.EmitConcurrencyAccessControlSmallExclusiveAcquire((ushort)Environment.CurrentManagedThreadId);
                return true;
            }
        }
    }

    /// <summary>Tries to enter exclusive (writer) access without waiting.</summary>
    /// <returns>True if access was acquired; false if lock is not available.</returns>
    public bool TryEnterExclusiveAccess()
    {
        var ct = Environment.CurrentManagedThreadId << ThreadIdShift;
        var ac = new AtomicChange(ref _data);

        // Check for idle state (ignoring contention flag)
        if ((ac.Initial & ~ContentionFlagMask) != 0)
        {
            return false;
        }

        ac.NewValue = ct | (ac.Initial & ContentionFlagMask);  // Preserve contention flag
        if (!ac.Commit())
        {
            return false;
        }

        TyphonEvent.EmitConcurrencyAccessControlSmallExclusiveAcquire((ushort)Environment.CurrentManagedThreadId);
        return true;
    }

    /// <summary>Exits exclusive (writer) access.</summary>
    public void ExitExclusiveAccess()
    {
        var expectedThread = Environment.CurrentManagedThreadId << ThreadIdShift;

        // Hand-rolled CAS retry (no delegate) — see #486: passing a lambda capturing `expectedThread` to ForceCommit
        // allocated a display-class instance (24 B) on every exclusive release, on the lock embedded in every page header.
        while (true)
        {
            int initial = _data;
            if ((initial & ~SharedUsedCounterMask & ~ContentionFlagMask) != expectedThread)
            {
                ThrowInvalidOperationException("ExitExclusiveAccess called by a thread that doesn't own the lock");
            }

            int newValue = initial & ContentionFlagMask;  // Preserve only contention flag
            if (Interlocked.CompareExchange(ref _data, newValue, initial) == initial)
            {
                break;
            }
        }

        TyphonEvent.EmitConcurrencyAccessControlSmallExclusiveRelease((ushort)Environment.CurrentManagedThreadId);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Promotion
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Tries to promote from shared to exclusive access. Caller must already hold shared access.
    /// </summary>
    /// <param name="ctx">Reference to WaitContext for timeout/cancellation.</param>
    /// <returns>True if promotion succeeded; false if timed out, cancelled, or other shared holders exist.</returns>
    public bool TryPromoteToExclusiveAccess(ref WaitContext ctx)
    {
        bool hadToWait = false;

        var ct = Environment.CurrentManagedThreadId << ThreadIdShift;
        var ac = new AtomicChange(ref _data, ref ctx);

        while (true)
        {
            var counter = ac.Initial & SharedUsedCounterMask;

            // Must be in shared mode to promote (counter > 0)
            if (counter == 0)
            {
                ThrowInvalidOperationException("Cannot promote to exclusive without holding shared access");
            }

            // We can only promote if we are the only user (counter == 1)
            if (counter != 1)
            {
                // Other shared holders exist - cannot promote
                return false;
            }

            ac.NewValue = ct | (ac.Initial & ContentionFlagMask);  // Preserve contention flag
            if (ac.Commit())
            {
                // Promotion success on AccessControlSmall is modelled as ExclusiveAcquire (no separate Promotion kind).
                TyphonEvent.EmitConcurrencyAccessControlSmallExclusiveAcquire((ushort)Environment.CurrentManagedThreadId);
                return true;
            }

            if (!hadToWait)
            {
                hadToWait = true;

                // Set contention flag (sticky, atomic) - we had to wait
                Interlocked.Or(ref _data, ContentionFlagMask);
                TyphonEvent.EmitConcurrencyAccessControlSmallContention();
            }

            if (!ac.Wait())
            {
                return false;
            }
        }
    }

    /// <summary>Demotes from exclusive to shared access. Caller must hold exclusive access.</summary>
    public void DemoteFromExclusiveAccess()
    {
        var expectedThread = Environment.CurrentManagedThreadId << ThreadIdShift;

        // Hand-rolled CAS retry (no delegate) — see #486 (same capturing-lambda allocation as ExitExclusiveAccess).
        while (true)
        {
            int initial = _data;
            if ((initial & ~SharedUsedCounterMask) != expectedThread)
            {
                ThrowInvalidOperationException("DemoteFromExclusiveAccess called by a thread that doesn't own the lock");
            }

            // Clear thread ID and set shared counter to 1
            if (Interlocked.CompareExchange(ref _data, 1, initial) == initial)
            {
                break;
            }
        }

        // Demote = ExclusiveRelease + SharedAcquire by same thread (atomic on the lock, two events on the wire).
        var threadId = (ushort)Environment.CurrentManagedThreadId;
        TyphonEvent.EmitConcurrencyAccessControlSmallExclusiveRelease(threadId);
        TyphonEvent.EmitConcurrencyAccessControlSmallSharedAcquire(threadId);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Convenience Methods
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Enters either shared or exclusive access based on the parameter.</summary>
    public bool Enter(bool exclusive, ref WaitContext ctx)
        => exclusive ? EnterExclusiveAccess(ref ctx) : EnterSharedAccess(ref ctx);

    /// <summary>Exits either shared or exclusive access based on the parameter.</summary>
    public void Exit(bool exclusive)
    {
        if (exclusive)
        {
            ExitExclusiveAccess();
        }
        else
        {
            ExitSharedAccess();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // State Snapshot (test infrastructure)
    // ═══════════════════════════════════════════════════════════════════════

    internal readonly struct StateSnapshot(int data)
    {
        internal readonly int Data = data;
    }

    internal StateSnapshot SnapshotInternalState() => new(_data & ~ContentionFlagMask);

    internal bool CheckInternalState(in StateSnapshot snapshot) => (_data & ~ContentionFlagMask) == snapshot.Data;
}
