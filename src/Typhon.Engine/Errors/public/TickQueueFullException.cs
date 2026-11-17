using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// <see cref="TyphonRuntime.WriteInTick"/> / <see cref="TyphonRuntime.InTick{T}"/> refused the work: the in-tick queue already holds
/// <see cref="RuntimeOptions.InTickQueueCapacity"/> items. Transient: the queue drains a bounded number per tick, so retry after a tick, or shed the request
/// (an HTTP endpoint maps it to 503).
/// </summary>
[PublicAPI]
public sealed class TickQueueFullException : TyphonException
{
    /// <summary>Creates the exception for a queue holding <paramref name="capacity"/> items.</summary>
    /// <param name="capacity">The queue's capacity.</param>
    public TickQueueFullException(int capacity) : base(TyphonErrorCode.TickQueueFull,
        $"The runtime's in-tick queue is full ({capacity} items): retry after a tick, or shed the request.")
    {
        Capacity = capacity;
    }

    /// <summary>The queue's capacity when it refused.</summary>
    public int Capacity { get; }

    /// <inheritdoc />
    public override bool IsTransient => true;
}
