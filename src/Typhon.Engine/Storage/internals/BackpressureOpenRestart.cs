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

/// <summary>
/// A back-pressure round inside a commit's publish, in a B+Tree write that has taken no latch, chunk reservation or node yet
/// (<see cref="PagedMMF.BeginRestartableIndexWrite"/>, #1382): the write gives the wait up instead of making it with its pin. Caught at the write's entry
/// (<c>BTree.Add</c>, <c>Remove</c>, <c>RemoveValue</c>, <c>Move</c>, <c>MoveValue</c>), which moves the epoch, waits for a page and runs the write again;
/// it never leaves them.
/// </summary>
internal sealed class BackpressureIndexRestart : Exception
{
    internal BackpressureIndexRestart(PagedMMF cache) => Cache = cache;

    /// <summary>The cache the round found no slot in: the one whose epoch the write moves and whose page it waits for.</summary>
    internal PagedMMF Cache { get; }
}
