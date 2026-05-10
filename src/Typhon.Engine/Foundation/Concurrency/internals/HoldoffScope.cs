using JetBrains.Annotations;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// RAII scope guard for holdoff regions. Enter via <see cref="UnitOfWorkContext.EnterHoldoff"/>,
/// exit via <see cref="Dispose"/>. While in holdoff, <see cref="UnitOfWorkContext.ThrowIfCancelled"/>
/// is a no-op — cancellation is deferred until the holdoff exits.
/// Supports nesting — inner holdoffs keep the outer holdoff active.
/// </summary>
/// <remarks>
/// <para>Uses a <c>ref</c> field (C# 11+) to mutate the caller's <see cref="UnitOfWorkContext"/>
/// on the stack. This requires .NET 7+ / C# 11+, which Typhon targets (.NET 10 / C# 13).</para>
/// <para>This is a <c>ref struct</c> to prevent heap allocation and boxing.
/// Always use in a <c>using</c> statement or explicit try/finally.</para>
/// </remarks>
[PublicAPI]
internal ref struct HoldoffScope
{
    private ref UnitOfWorkContext _ctx;
    private bool _disposed;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal HoldoffScope(ref UnitOfWorkContext ctx)
    {
        _ctx = ref ctx;
        _disposed = false;
        _ctx._holdoffCount++;
    }

    /// <summary>
    /// Exit the holdoff region. Decrements the holdoff counter on the associated context.
    /// Double-dispose is safe (no-op).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            Debug.Assert(_ctx._holdoffCount > 0, "HoldoffScope.Dispose: holdoff count underflow");
            _ctx._holdoffCount--;
        }
    }
}
