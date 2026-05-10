using System;

namespace Typhon.Engine.Internals;

/// <summary>
/// Tracks state for a backpressure wait loop. Each bounded resource creates one at entry and passes it by ref through retry iterations.
/// </summary>
internal struct BackpressureContext
{
    public readonly string ResourcePath;
    public WaitContext WaitContext;
    public int RetryCount;

    public BackpressureContext(string resourcePath, TimeSpan timeout)
    {
        ResourcePath = resourcePath;
        WaitContext = WaitContext.FromTimeout(timeout);
        RetryCount = 0;
    }

    public bool ShouldGiveUp => WaitContext.ShouldStop;

    public void RecordRetry() => RetryCount++;
}
