// unset

using JetBrains.Annotations;
using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Typhon.Engine.Profiler;

namespace Typhon.Engine.Internals;

/// <summary>
/// A 32-bit synchronization primitive for resource lifecycle management with 3 modes:
/// ACCESSING (multiple concurrent), MODIFY (single holder, compatible with ACCESSING), and DESTROY (terminal, exclusive).
/// </summary>
/// <remarks>
/// <para><b>Key difference from RW locks</b>: MODIFY is compatible with ACCESSING. Modifiers can execute while accessors are active
/// (for append-only/extend-only operations). Only DESTROY is truly exclusive.</para>
///
/// <para><b>Bit layout (32 bits)</b>:</para>
/// <list type="bullet">
/// <item>Bits 0-7: ACCESSING count (0-255)</item>
/// <item>Bits 8-23: MODIFY holder ThreadId (16 bits, 0 = not held)</item>
/// <item>Bit 24: MODIFY_PENDING flag (fairness)</item>
/// <item>Bit 25: DESTROY flag (terminal, never cleared)</item>
/// <item>Bit 26: CONTENTION flag (sticky, cleared by Reset)</item>
/// <item>Bits 27-31: Reserved (5 bits)</item>
/// </list>
///
/// <para><b>Compatibility matrix</b>:</para>
/// <code>
///              ACCESSING   MODIFY   DESTROY
/// ACCESSING       ✓          ✓         ✗
/// MODIFY          ✓          ✗         ✗
/// DESTROY         ✗          ✗         ✗
/// </code>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
[PublicAPI]
[DebuggerDisplay("{DebuggerDisplay,nq}")]
internal struct ResourceAccessControl
{
    // ═══════════════════════════════════════════════════════════════════════
    // Bit Layout Constants
    // ═══════════════════════════════════════════════════════════════════════

    private const int AccessingCountMask  = 0x0000_00FF;  // Bits 0-7
    private const int ThreadIdMask        = 0x00FF_FF00;  // Bits 8-23
    private const int ModifyPendingFlag   = 0x0100_0000;  // Bit 24
    private const int DestroyFlag         = 0x0200_0000;  // Bit 25
    private const int ContentionFlag      = 0x0400_0000;  // Bit 26

    private const int ThreadIdShift       = 8;
    private const int MaxAccessingCount   = 255;
    private const int ThreadIdBitsMask    = 0xFFFF;       // 16 bits for thread ID

    private int _state;

    // ═══════════════════════════════════════════════════════════════════════
    // Helper Methods
    // ═══════════════════════════════════════════════════════════════════════

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetAccessingCount(int state) => state & AccessingCountMask;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetThreadId(int state) => (state & ThreadIdMask) >> ThreadIdShift;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsModifyHeld(int state) => GetThreadId(state) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool HasModifyPending(int state) => (state & ModifyPendingFlag) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool HasDestroyFlag(int state) => (state & DestroyFlag) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool HasPendingOrDestroy(int state) => (state & (ModifyPendingFlag | DestroyFlag)) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetCurrentThreadIdBits() => Environment.CurrentManagedThreadId & ThreadIdBitsMask;

    /// <summary>Computes elapsed time in microseconds from a Stopwatch start tick.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ComputeElapsedUs(long startTicks)
    {
        var elapsed = Stopwatch.GetTimestamp() - startTicks;
        return (elapsed * 1_000_000) / Stopwatch.Frequency;
    }

    /// <summary>Cap an elapsed-us value to <see cref="ushort.MaxValue"/> for trace event payloads.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort ElapsedUsCapped(long startTicks)
    {
        var us = ComputeElapsedUs(startTicks);
        return us >= ushort.MaxValue ? ushort.MaxValue : (ushort)us;
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowInvalidOperation(string message) => throw new InvalidOperationException(message);

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowTimeout() => ThrowHelper.ThrowLockTimeout("ResourceAccessControl", TimeSpan.Zero);

    /// <summary>Set the contention flag if not already set; emit a Contention trace event the first time it transitions.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetContentionFlagAndEmit()
    {
        var prior = Interlocked.Or(ref _state, ContentionFlag);
        if ((prior & ContentionFlag) == 0)
        {
            TyphonEvent.EmitConcurrencyResourceContention();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // ACCESSING Mode
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Attempts to enter ACCESSING mode without blocking.</summary>
    public bool TryEnterAccessing()
    {
        int state = _state;

        if (HasPendingOrDestroy(state))
        {
            return false;
        }

        int count = GetAccessingCount(state);
        if (count >= MaxAccessingCount)
        {
            ThrowInvalidOperation("Max ACCESSING count (1023) exceeded.");
        }

        int newState = state + 1;

        if (Interlocked.CompareExchange(ref _state, newState, state) != state)
        {
            return false;
        }

        TyphonEvent.EmitConcurrencyResourceAccessing(true, (byte)GetAccessingCount(newState), 0);
        return true;
    }

    /// <summary>Enters ACCESSING mode, spinning if necessary.</summary>
    public bool EnterAccessing(ref WaitContext ctx)
    {
        bool isNullRef = Unsafe.IsNullRef(ref ctx);
        SpinWait spin = default;
        long waitStartTicks = 0;
        bool hadToWait = false;

        while (true)
        {
            if (!isNullRef && ctx.ShouldStop)
            {
                TyphonEvent.EmitConcurrencyResourceAccessing(false, 0,
                    hadToWait ? ElapsedUsCapped(waitStartTicks) : (ushort)0);
                return false;
            }

            int state = _state;

            if (HasPendingOrDestroy(state))
            {
                if (!hadToWait)
                {
                    hadToWait = true;
                    waitStartTicks = Stopwatch.GetTimestamp();
                    SetContentionFlagAndEmit();
                }
                spin.SpinOnce();
                continue;
            }

            int count = GetAccessingCount(state);
            if (count >= MaxAccessingCount)
            {
                ThrowInvalidOperation("Max ACCESSING count (1023) exceeded.");
            }

            int newState = state + 1;

            if (Interlocked.CompareExchange(ref _state, newState, state) == state)
            {
                var elapsedUs = hadToWait ? ElapsedUsCapped(waitStartTicks) : (ushort)0;
                TyphonEvent.EmitConcurrencyResourceAccessing(true, (byte)GetAccessingCount(newState), elapsedUs);
                return true;
            }

            spin.SpinOnce();
        }
    }

    /// <summary>Exits ACCESSING mode.</summary>
    public void ExitAccessing()
    {
        SpinWait spin = default;

        while (true)
        {
            int state = _state;

            if (GetAccessingCount(state) == 0)
            {
                ThrowInvalidOperation("ExitAccessing called without matching EnterAccessing.");
            }

            int newState = state - 1;

            if (Interlocked.CompareExchange(ref _state, newState, state) == state)
            {
                // Exit is modelled as an Accessing event with success=false to indicate "released, count went down".
                // Operationally distinct: the consumer correlates by (slot, timestamp) order to pair acquires with releases.
                // No separate "Released" kind to keep wire-kind count small; the new accessingCount payload byte = post-release count.
                TyphonEvent.EmitConcurrencyResourceAccessing(false, (byte)GetAccessingCount(newState), 0);
                return;
            }

            spin.SpinOnce();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // MODIFY Mode
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Attempts to enter MODIFY mode without blocking.</summary>
    public bool TryEnterModify()
    {
        int state = _state;

        if (HasDestroyFlag(state) || IsModifyHeld(state) || GetAccessingCount(state) > 0)
        {
            return false;
        }

        int threadId = GetCurrentThreadIdBits();
        int newState = (state & ~ThreadIdMask) | (threadId << ThreadIdShift);

        if (Interlocked.CompareExchange(ref _state, newState, state) != state)
        {
            return false;
        }

        TyphonEvent.EmitConcurrencyResourceModify(true, (ushort)threadId, 0);
        return true;
    }

    /// <summary>Enters MODIFY mode.</summary>
    public bool EnterModify(ref WaitContext ctx)
    {
        bool isNullRef = Unsafe.IsNullRef(ref ctx);
        SpinWait spin = default;
        int threadId = GetCurrentThreadIdBits();
        long waitStartTicks = 0;
        bool hadToWait = false;
        bool weSetPending = false;

        while (true)
        {
            if (!isNullRef && ctx.ShouldStop)
            {
                if (weSetPending)
                {
                    TryClearModifyPending();
                }
                TyphonEvent.EmitConcurrencyResourceModify(false, (ushort)threadId, hadToWait ? ElapsedUsCapped(waitStartTicks) : (ushort)0);
                return false;
            }

            int state = _state;

            if (HasDestroyFlag(state))
            {
                if (!hadToWait)
                {
                    hadToWait = true;
                    waitStartTicks = Stopwatch.GetTimestamp();
                    SetContentionFlagAndEmit();
                }
                spin.SpinOnce();
                continue;
            }

            if (IsModifyHeld(state))
            {
                if (!hadToWait)
                {
                    hadToWait = true;
                    waitStartTicks = Stopwatch.GetTimestamp();
                    SetContentionFlagAndEmit();
                }
                spin.SpinOnce();
                continue;
            }

            if (GetAccessingCount(state) == 0)
            {
                int newState = (state & ~(ModifyPendingFlag | ThreadIdMask)) | (threadId << ThreadIdShift);

                if (Interlocked.CompareExchange(ref _state, newState, state) == state)
                {
                    var elapsedUs = hadToWait ? ElapsedUsCapped(waitStartTicks) : (ushort)0;
                    TyphonEvent.EmitConcurrencyResourceModify(true, (ushort)threadId, elapsedUs);
                    return true;
                }

                spin.SpinOnce();
                continue;
            }

            // ACCESSING holders exist - set MODIFY_PENDING to block new ones
            if (!hadToWait)
            {
                hadToWait = true;
                waitStartTicks = Stopwatch.GetTimestamp();
                SetContentionFlagAndEmit();
            }

            if (!HasModifyPending(state))
            {
                int newState = state | ModifyPendingFlag;
                if (Interlocked.CompareExchange(ref _state, newState, state) == state)
                {
                    weSetPending = true;
                }
            }

            spin.SpinOnce();
        }
    }

    /// <summary>Exits MODIFY mode.</summary>
    public void ExitModify()
    {
        SpinWait spin = default;
        int expectedThreadId = GetCurrentThreadIdBits();

        while (true)
        {
            int state = _state;

            if (GetThreadId(state) != expectedThreadId)
            {
                ThrowInvalidOperation("ExitModify called by thread that doesn't hold MODIFY.");
            }

            int newState = state & ~ThreadIdMask;

            if (Interlocked.CompareExchange(ref _state, newState, state) == state)
            {
                TyphonEvent.EmitConcurrencyResourceModify(false, (ushort)expectedThreadId, 0);
                return;
            }

            spin.SpinOnce();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Promotion/Demotion
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Attempts to promote from ACCESSING to MODIFY.</summary>
    public bool TryPromoteToModify(ref WaitContext ctx)
    {
        bool isNullRef = Unsafe.IsNullRef(ref ctx);
        SpinWait spin = default;
        int threadId = GetCurrentThreadIdBits();
        long waitStartTicks = 0;
        bool hadToWait = false;
        bool weSetPending = false;

        while (true)
        {
            if (!isNullRef && ctx.ShouldStop)
            {
                if (weSetPending)
                {
                    TryClearModifyPending();
                }
                if (hadToWait)
                {
                    TyphonEvent.EmitConcurrencyResourceModifyPromotion(ElapsedUsCapped(waitStartTicks));
                }
                return false;
            }

            int state = _state;
            int count = GetAccessingCount(state);

            if (count == 0)
            {
                ThrowInvalidOperation("TryPromoteToModify called without holding ACCESSING.");
            }

            if (HasDestroyFlag(state))
            {
                if (weSetPending)
                {
                    TryClearModifyPending();
                }
                return false;
            }

            if (IsModifyHeld(state))
            {
                if (weSetPending)
                {
                    TryClearModifyPending();
                }
                return false;
            }

            if (count == 1)
            {
                int newState = (state - 1) & ~(ModifyPendingFlag | ThreadIdMask) | (threadId << ThreadIdShift);

                if (Interlocked.CompareExchange(ref _state, newState, state) == state)
                {
                    var elapsedUs = hadToWait ? ElapsedUsCapped(waitStartTicks) : (ushort)0;
                    // Successful promotion fires Modify acquire event (the destination state).
                    TyphonEvent.EmitConcurrencyResourceModify(true, (ushort)threadId, elapsedUs);
                    return true;
                }

                spin.SpinOnce();
                continue;
            }

            if (!hadToWait)
            {
                hadToWait = true;
                waitStartTicks = Stopwatch.GetTimestamp();
                SetContentionFlagAndEmit();
                TyphonEvent.EmitConcurrencyResourceModifyPromotion(0);
            }

            if (!HasModifyPending(state))
            {
                int newState = state | ModifyPendingFlag;
                if (Interlocked.CompareExchange(ref _state, newState, state) == state)
                {
                    weSetPending = true;
                }
            }

            spin.SpinOnce();
        }
    }

    /// <summary>Attempts to clear the MODIFY_PENDING flag (best-effort).</summary>
    private void TryClearModifyPending()
    {
        SpinWait spin = default;
        for (int i = 0; i < 10; i++)
        {
            int state = _state;
            if (!HasModifyPending(state))
            {
                return;
            }

            int newState = state & ~ModifyPendingFlag;
            if (Interlocked.CompareExchange(ref _state, newState, state) == state)
            {
                return;
            }
            spin.SpinOnce();
        }
    }

    /// <summary>Demotes from MODIFY back to ACCESSING.</summary>
    public void DemoteFromModify()
    {
        SpinWait spin = default;
        int expectedThreadId = GetCurrentThreadIdBits();

        while (true)
        {
            int state = _state;

            if (GetThreadId(state) != expectedThreadId)
            {
                ThrowInvalidOperation("DemoteFromModify called by thread that doesn't hold MODIFY.");
            }

            int newState = (state & ~ThreadIdMask) + 1;

            if (Interlocked.CompareExchange(ref _state, newState, state) == state)
            {
                // Demote = ModifyExit + AccessingAcquire by same thread.
                TyphonEvent.EmitConcurrencyResourceModify(false, (ushort)expectedThreadId, 0);
                TyphonEvent.EmitConcurrencyResourceAccessing(true, (byte)GetAccessingCount(newState), 0);
                return;
            }

            spin.SpinOnce();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // DESTROY Mode
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Enters DESTROY mode (terminal).</summary>
    public bool EnterDestroy(ref WaitContext ctx)
    {
        bool isNullRef = Unsafe.IsNullRef(ref ctx);
        SpinWait spin = default;
        long waitStartTicks = 0;
        bool hadToWait = false;
        bool destroyFlagSet = false;

        // Phase 1: Set DESTROY flag
        while (!destroyFlagSet)
        {
            if (!isNullRef && ctx.ShouldStop)
            {
                TyphonEvent.EmitConcurrencyResourceDestroy(false, hadToWait ? ElapsedUsCapped(waitStartTicks) : (ushort)0);
                return false;
            }

            int state = _state;

            if (HasDestroyFlag(state))
            {
                break;
            }

            if (!hadToWait)
            {
                hadToWait = true;
                waitStartTicks = Stopwatch.GetTimestamp();
                SetContentionFlagAndEmit();
            }

            int newState = state | DestroyFlag;

            if (Interlocked.CompareExchange(ref _state, newState, state) == state)
            {
                destroyFlagSet = true;
            }
            else
            {
                spin.SpinOnce();
            }
        }

        // Phase 2: Wait for ACCESSING=0 and MODIFY not held
        spin = default;

        while (true)
        {
            if (!isNullRef && ctx.ShouldStop)
            {
                TyphonEvent.EmitConcurrencyResourceDestroy(false, ElapsedUsCapped(waitStartTicks));
                return false;
            }

            int state = _state;

            if (GetAccessingCount(state) == 0 && !IsModifyHeld(state))
            {
                var elapsedUs = ElapsedUsCapped(waitStartTicks);
                TyphonEvent.EmitConcurrencyResourceDestroy(true, elapsedUs);
                return true;
            }

            spin.SpinOnce();
        }
    }

    // No ExitDestroy - destruction is final

    // ═══════════════════════════════════════════════════════════════════════
    // Scoped Guards
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Enters ACCESSING and returns a disposable guard.</summary>
    public unsafe AccessingGuard EnterAccessingScoped(ref WaitContext ctx)
    {
        if (!EnterAccessing(ref ctx))
        {
            ThrowTimeout();
        }
        fixed (int* ptr = &_state)
        {
            return new AccessingGuard(ptr);
        }
    }

    /// <summary>Enters MODIFY and returns a disposable guard.</summary>
    public unsafe ModifyGuard EnterModifyScoped(ref WaitContext ctx)
    {
        if (!EnterModify(ref ctx))
        {
            ThrowTimeout();
        }
        fixed (int* ptr = &_state)
        {
            return new ModifyGuard(ptr);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Lifecycle
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Resets the primitive to initial state.</summary>
    public void Reset() => _state = 0;

    // ═══════════════════════════════════════════════════════════════════════
    // Diagnostic Properties
    // ═══════════════════════════════════════════════════════════════════════

    public bool IsModifyHeldByCurrentThread
    {
        get
        {
            int threadId = GetThreadId(_state);
            return threadId != 0 && threadId == GetCurrentThreadIdBits();
        }
    }

    public int ModifyHolderThreadId => GetThreadId(_state);
    public int AccessingCount => GetAccessingCount(_state);
    public bool IsModifyPending => HasModifyPending(_state);
    public bool IsDestroyed => HasDestroyFlag(_state);
    public bool WasContended => (_state & ContentionFlag) != 0;

    public ResourceAccessControlState GetDiagnosticState()
    {
        int state = _state;
        return new ResourceAccessControlState
        {
            AccessingCount = GetAccessingCount(state),
            ModifyHolderThreadId = GetThreadId(state),
            ModifyPending = HasModifyPending(state),
            Destroyed = HasDestroyFlag(state),
            RawState = state
        };
    }

    private string DebuggerDisplay
    {
        get
        {
            var state = GetDiagnosticState();
            var contention = WasContended ? ", CONTENDED" : "";
            return $"Accessing={state.AccessingCount}, ModifyHolder={state.ModifyHolderThreadId}, " +
                   $"Pending={state.ModifyPending}, Destroyed={state.Destroyed}{contention}";
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // State Snapshot (test infrastructure)
    // ═══════════════════════════════════════════════════════════════════════

    internal readonly struct StateSnapshot(int state)
    {
        internal readonly int State = state;
    }

    internal StateSnapshot SnapshotInternalState() => new(_state & ~ContentionFlag);

    internal bool CheckInternalState(in StateSnapshot snapshot) => (_state & ~ContentionFlag) == snapshot.State;

    // ═══════════════════════════════════════════════════════════════════════
    // Scoped Guard Structs
    // ═══════════════════════════════════════════════════════════════════════

    [PublicAPI]
    public readonly unsafe ref struct AccessingGuard
    {
        private readonly int* _statePtr;

        internal AccessingGuard(int* state)
        {
            _statePtr = state;
        }

        public void Dispose()
        {
            if (_statePtr == null)
            {
                return;
            }

            SpinWait spin = default;

            while (true)
            {
                int state = *_statePtr;

                if (GetAccessingCount(state) == 0)
                {
                    ThrowInvalidOperation("AccessingGuard.Dispose called without matching EnterAccessing.");
                }

                int newState = state - 1;

                if (Interlocked.CompareExchange(ref *_statePtr, newState, state) == state)
                {
                    TyphonEvent.EmitConcurrencyResourceAccessing(false, (byte)GetAccessingCount(newState), 0);
                    return;
                }

                spin.SpinOnce();
            }
        }
    }

    [PublicAPI]
    public readonly unsafe ref struct ModifyGuard
    {
        private readonly int* _statePtr;

        internal ModifyGuard(int* state)
        {
            _statePtr = state;
        }

        public void Dispose()
        {
            if (_statePtr == null)
            {
                return;
            }

            SpinWait spin = default;
            int expectedThreadId = GetCurrentThreadIdBits();

            while (true)
            {
                int state = *_statePtr;

                if (GetThreadId(state) != expectedThreadId)
                {
                    ThrowInvalidOperation("ModifyGuard.Dispose called by thread that doesn't hold MODIFY.");
                }

                int newState = state & ~ThreadIdMask;

                if (Interlocked.CompareExchange(ref *_statePtr, newState, state) == state)
                {
                    TyphonEvent.EmitConcurrencyResourceModify(false, (ushort)expectedThreadId, 0);
                    return;
                }

                spin.SpinOnce();
            }
        }
    }
}

/// <summary>Diagnostic snapshot of a <see cref="ResourceAccessControl"/>'s state.</summary>
[PublicAPI]
internal readonly struct ResourceAccessControlState
{
    public int AccessingCount { get; init; }
    public int ModifyHolderThreadId { get; init; }
    public bool ModifyPending { get; init; }
    public bool Destroyed { get; init; }
    public int RawState { get; init; }

    public override string ToString() =>
        $"Accessing={AccessingCount}, ModifyHolder={ModifyHolderThreadId}, " +
        $"ModifyPending={ModifyPending}, Destroyed={Destroyed}";
}
