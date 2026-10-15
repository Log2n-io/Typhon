using System;

namespace Typhon.Engine.Internals;

/// <summary>
/// A back-pressure round inside an entity open (<see cref="PagedMMF.RestartableOpenDepth"/>): the open unwinds to its start, moves its epoch there and
/// runs again (#1230). Caught by <c>Transaction.ResolveEntity</c>; it never leaves the open.
/// </summary>
internal sealed class BackpressureOpenRestart : Exception
{
}
