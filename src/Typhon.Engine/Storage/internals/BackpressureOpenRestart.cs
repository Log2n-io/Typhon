using System;

namespace Typhon.Engine.Internals;

/// <summary>
/// A wait inside an entity open, or inside a first write before it claims its revision (<see cref="PagedMMF.RestartableOpenDepth"/>), that the
/// operation gives up instead of making with its pin: a back-pressure round (#1230), or a contended revision-chain lock (#1382). The operation
/// unwinds to its start, moves its epoch there and runs again. Caught by <c>Transaction.ResolveEntity</c> and
/// <c>Transaction.EcsVersionedCopyOnWrite</c>; it never leaves them.
/// </summary>
internal sealed class BackpressureOpenRestart : Exception
{
    internal BackpressureOpenRestart(bool lockWait = false) => LockWait = lockWait;

    /// <summary>A contended chain lock, not a page: the retry waits briefly instead of for a page.</summary>
    internal bool LockWait { get; }
}
