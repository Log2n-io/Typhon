using JetBrains.Annotations;
using System;

namespace Typhon.Engine;

/// <summary>
/// A lock acquisition (shared or exclusive) exceeded its deadline.
/// Always transient — the resource is presumably available later.
/// </summary>
[PublicAPI]
public class LockTimeoutException : TyphonTimeoutException
{
    /// <summary>
    /// Creates a new <see cref="LockTimeoutException"/> for the specified resource.
    /// </summary>
    /// <param name="resourceName">Name or path of the resource that could not be locked.</param>
    /// <param name="waitDuration">How long the caller waited before the timeout fired.</param>
    public LockTimeoutException(string resourceName, TimeSpan waitDuration)
        : base(TyphonErrorCode.LockTimeout, $"Lock timeout on '{resourceName}' after {waitDuration.TotalMilliseconds:F0}ms", waitDuration)
    {
        ResourceName = resourceName;
    }

    /// <summary>Name or path of the resource that could not be locked.</summary>
    public string ResourceName { get; }
}
