using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine.Internals;

internal partial struct AccessControl
{
    [StructLayout(LayoutKind.Sequential)]
    private ref struct LockData
    {
        /// <summary>
        /// Constructor for blocking operations that may need to wait.
        /// </summary>
        /// <param name="data">Reference to the lock's state data.</param>
        /// <param name="ctx">Reference to WaitContext. Use <c>ref WaitContext.Null</c> for infinite wait.</param>
        public LockData(ref ulong data, ref WaitContext ctx)
        {
            _data = ref data;
            _initial = _staging = _data;
            _ctx = ref ctx;
            _isNullRef = Unsafe.IsNullRef(ref ctx);
        }

        /// <summary>
        /// Constructor for non-blocking operations (exit, demote, try-enter).
        /// </summary>
        /// <param name="data">Reference to the lock's state data.</param>
        public LockData(ref ulong data)
        {
            _data = ref data;
            _initial = _staging = _data;
            _ctx = ref Unsafe.NullRef<WaitContext>();
            _isNullRef = true;
        }

        private ref ulong _data;
        private ulong _initial;
        private ulong _staging;
        private readonly ref WaitContext _ctx;
        private readonly bool _isNullRef;

        /// <summary>
        /// Returns true if the wait should stop due to timeout or cancellation.
        /// Returns false for infinite wait (NullRef pattern).
        /// </summary>
        public bool ShouldStop
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => !_isNullRef && _ctx.ShouldStop;
        }

        public int SharedCounter
        {
            get => (int)(_staging & SharedCounterMask);
            set
            {
                Debug.Assert(value is >= 0 and <= byte.MaxValue);
                _staging = (_staging & ~SharedCounterMask) | (uint)value;
            }
        }

        public int SharedWaiters
        {
            get => (int)((_staging & SharedWaitersMask) >> SharedWaitersShift);
            set
            {
                Debug.Assert(value is >= 0 and <= byte.MaxValue);
                _staging = (_staging & ~SharedWaitersMask) | (ulong)value << SharedWaitersShift;
            }
        }

        public int ExclusiveWaiters
        {
            get => (int)((_staging & ExclusiveWaitersMask) >> ExclusiveWaitersShift);
            set
            {
                Debug.Assert(value is >= 0 and <= byte.MaxValue);
                _staging = (_staging & ~ExclusiveWaitersMask) | (ulong)value << ExclusiveWaitersShift;
            }
        }

        public int PromoterWaiters
        {
            get => (int)((_staging & PromoterWaitersMask) >> PromoterWaitersShift);
            set
            {
                Debug.Assert(value is >= 0 and <= byte.MaxValue);
                _staging = (_staging & ~PromoterWaitersMask) | (ulong)value << PromoterWaitersShift;
            }
        }

        public int ThreadId
        {
            get => (int)((_staging & ThreadIdMask) >> ThreadIdShift);
            set => _staging = (_staging & ~ThreadIdMask) | ((ulong)value << ThreadIdShift) & ThreadIdMask;
        }

        public ulong State
        {
            get => _staging & StateMask;
            set => _staging = (_staging & ~StateMask) | value;
        }

        public ulong Staging
        {
            get => _staging;
            set => _staging = value;
        }

        public bool TryUpdate()
        {
            var succeed = Interlocked.CompareExchange(ref _data, _staging, _initial) == _initial;

            if (!succeed)
            {
                Fetch();
            }

            return succeed;
        }

        internal void Fetch()
        {
            _initial = _staging = _data;
        }

        public bool CanShareStart => (_staging & (PromoterWaitersMask | ExclusiveWaitersMask)) == 0;
        public bool CanExclusiveStart => (_staging & PromoterWaitersMask) == 0;
        public bool CanPromoteToExclusive => (_initial & SharedCounterMask) == 1;

        public bool WaitForSharedCanStart()
        {
            var res = false;

            var sw = new SpinWait();
            var maxWaitCounter = 100;
            while (!ShouldStop && (--maxWaitCounter > 0))
            {
                var cur = _data;

                var state = (cur & StateMask);

                // A concurrent change of state may occur and if that's the case, we need to exit the wait and reassess
                if (state == ExclusiveState)
                {
                    res = true;
                    break;
                }

                // Can't be exclusive, without exclusive or promoters waiters (they have the priority)
                if (((cur & (ExclusiveWaitersMask | PromoterWaitersMask)) == 0))
                {
                    res = true;
                    break;
                }
                sw.SpinOnce();
//                Console.WriteLine($"[Thread {Environment.CurrentManagedThreadId}] {LogData(cur)}");
            }

            return (maxWaitCounter == 0) || res;
        }

        internal enum WaitFor
        {
            Shared,
            Exclusive,
            Promote
        }

        public bool WaitForIdleState(WaitFor waitFor)
        {
            // Log the operation start and set the value to increment as a waiter
            int waitIncValue;
            bool overflow;
            switch (waitFor)
            {
                case WaitFor.Shared:
                    // TOFIX
                    // AddWaitOperation(OperationType.SharedStartWait);
                    overflow = SharedWaiters == byte.MaxValue;
                    waitIncValue = 1 << SharedWaitersShift;
                    break;
                case WaitFor.Exclusive:
                    // TOFIX
                    // AddWaitOperation(OperationType.ExclusiveStartWait); 
                    overflow = ExclusiveWaiters == byte.MaxValue;
                    waitIncValue = 1 << ExclusiveWaitersShift;
                    break;
                case WaitFor.Promote:
                    // TOFIX
                    // AddWaitOperation(OperationType.PromoteStartWait);
                    overflow = PromoterWaiters == byte.MaxValue;
                    waitIncValue = 1 << PromoterWaitersShift;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(waitFor), waitFor, null);
            }

            if (!overflow)
            {
                // Increment the appropriate waiter
                Staging += (ulong)waitIncValue;

                // Make the update, two possibilities
                if (!TryUpdate())
                {
                    // Concurrent update came in between, return true to signal "hey, let's try again"
                    return true;
                }
                // Keep going on as expected
            }

            // Set contention flag (sticky, atomic) - we had to wait, so contention occurred
            Interlocked.Or(ref Unsafe.As<ulong, long>(ref _data), (long)ContentionFlagMask);
            TyphonEvent.EmitConcurrencyAccessControlContention();

            // Enter the wait loop where we fetch the lock data and check for idle state
            var sw = new SpinWait();
            var maxWaitCounter = 1000;
            var res = false;
            while (!ShouldStop && (--maxWaitCounter > 0))
            {
                var data = _data;
                var threadId = (data & ThreadIdMask) >> ThreadIdShift;

                // Idle ?
                if ((data & StateMask) == IdleState)
                {
                    // Set result to true to signal to reassess
                    res = true;
                    break;
                }

                sw.SpinOnce();
            }

            // Update res to be either true (try again/reassess) or false (timeout or cancellation)
            res = (maxWaitCounter == 0) || res;

            if (!overflow)
            {
                // Decrement the waiter counter, can only be made through a 64-bits CAS, we only care about decrement the value we initially added,
                // Keep trying if we fail.
                while (true)
                {
                    var oldData = _data;
                    var newData = oldData - (ulong)waitIncValue;
                    if (Interlocked.CompareExchange(ref _data, newData, oldData) == oldData)
                    {
                        break;
                    }
                }
            }

            // Return false if we timed out or were canceled
            if (!res)
            {
                // TOFIX
                // AddTimedOutOrCanceledOperation();
                return false;
            }

            return true;
        }
    }
}