using System;

namespace Typhon.Engine.Internals;

/// <summary>
/// A back-pressure round inside the checkpoint's durable-metadata hook (<see cref="PagedMMF.NoBackpressureWaitDepth"/>): the hook gives up and its
/// cycle only writes, so the checkpoint never waits for a slot only its own writes free (CK-14, #1382). Caught by
/// <c>CheckpointManager.RunHookOrDefer</c>; it never leaves the cycle.
/// </summary>
internal sealed class BackpressureCheckpointDeferral : Exception
{
}
