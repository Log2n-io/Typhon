using JetBrains.Annotations;
using System.Runtime.CompilerServices;
using Typhon.Engine.Profiler;

namespace Typhon.Engine.Internals;

/// <summary>
/// RAII scope guard for epoch-based resource protection. Enter via <see cref="Enter"/>, exit via <see cref="Dispose"/>.
/// Supports nesting — only the outermost scope advances the global epoch.
/// </summary>
/// <remarks>
/// <para>Copy safety: depth validation in <see cref="Dispose"/> detects misuse (e.g., accidental struct copy disposing twice). A copied guard would have a stale
/// <c>_expectedDepth</c> that won't match the registry's current depth.</para>
/// <para>This is a ref struct to prevent heap allocation and boxing. Always use in a <c>using</c> statement or explicit try/finally.</para>
/// </remarks>
[PublicAPI]
internal ref struct EpochGuard
{
    private readonly EpochManager _manager;
    private readonly int _expectedDepth;
    private bool _disposed;

    /// <summary>
    /// The global epoch captured atomically at scope entry. Use this instead of <see cref="EpochManager.GlobalEpoch"/>.
    /// </summary>
    public long Epoch { get; }

    private EpochGuard(EpochManager manager, int depth, long epoch)
    {
        _manager = manager;
        _expectedDepth = depth;
        _disposed = false;
        Epoch = epoch;
    }

    /// <summary>
    /// Enter an epoch scope. Returns a guard that must be disposed to exit. The current <see cref="EpochManager.GlobalEpoch"/> is captured atomically.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EpochGuard Enter(EpochManager manager)
    {
        var depth = manager.EnterScope();
        var epoch = manager.GlobalEpoch;
        TyphonEvent.EmitConcurrencyEpochScopeEnter((uint)epoch, (byte)depth, depth == 0);
        return new EpochGuard(manager, depth, epoch);
    }

    /// <summary>
    /// Exit the epoch scope. If this is the outermost scope, advances the global epoch.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _manager.ExitScope(_expectedDepth);
            TyphonEvent.EmitConcurrencyEpochScopeExit((uint)_manager.GlobalEpoch, _expectedDepth == 0);
        }
    }
}
