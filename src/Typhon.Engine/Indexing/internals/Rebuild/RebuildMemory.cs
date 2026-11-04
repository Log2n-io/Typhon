namespace Typhon.Engine.Internals;

/// <summary>
/// Every native buffer of one archetype's index rebuild, allocated and freed through one place so the budget accounting cannot miss one (RB-09, #1588):
/// it knows the bytes live now and the most ever live, which the stats report and the tests hold against the budget.
/// </summary>
internal sealed class RebuildMemory
{
    private readonly BufferOwner _owner;
    private readonly IndexRebuildStats _stats;
    private static long _allocations;

    public RebuildMemory(long budgetBytes, BufferOwner owner, IndexRebuildStats stats)
    {
        BudgetBytes = budgetBytes;
        _owner = owner;
        _stats = stats;
    }

    /// <summary>The budget every phase plans against.</summary>
    public long BudgetBytes { get; }

    /// <summary>The native bytes allocated through this and not freed yet.</summary>
    public long Live { get; private set; }

    /// <summary>What is left of the budget.</summary>
    public long Left => BudgetBytes - Live;

    /// <summary>Allocates <paramref name="bytes"/> (at least 1) of native memory named <paramref name="id"/>.</summary>
    public NativeBuffer Allocate(long bytes, string id)
    {
        if (bytes > int.MaxValue)
        {
            ThrowHelper.ThrowInvalidOp($"Index rebuild: a {bytes}-byte buffer is over the 2 GiB a native block can hold.");
        }

        // Resource ids are unique among a node's children: several buffers share a name, so each takes a number.
        var number = System.Threading.Interlocked.Increment(ref _allocations);
        var buffer = NativeBuffer.Allocate((int)System.Math.Max(1, bytes), _owner, $"{id}#{number}");
        Live += buffer.Length;
        if (Live > _stats.PeakNativeBytes)
        {
            _stats.PeakNativeBytes = Live;
        }

        return buffer;
    }

    /// <summary>Frees <paramref name="buffer"/>, if any, and clears the reference.</summary>
    public void Free(ref NativeBuffer buffer)
    {
        if (buffer == null)
        {
            return;
        }

        Live -= buffer.Length;
        buffer.Free();
        buffer = null;
    }
}
