using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Typhon.Engine.Internals;

/// <summary>
/// Engine-level MVCC override dictionary for EnabledBits.
/// Fast path: when <see cref="_overrideCount"/> == 0, the inline EntityRecord.EnabledBits is correct (zero overhead).
/// Slow path: lookup EntityKey in the dictionary, resolve at transaction TSN.
/// </summary>
internal partial class EnabledBitsOverrides
{
    /// <summary>Threshold above which a warning is logged about stale transactions blocking cleanup.</summary>
    private const int HighWaterMarkWarningThreshold = 10_000;

    private readonly ConcurrentDictionary<long, EnabledBitsHistory> _overrides = new();
    private readonly ILogger _log;

    /// <summary>Number of entities with active overrides. Zero = fast path (no dictionary lookup needed).</summary>
    internal volatile int _overrideCount;

    /// <summary>Peak override count since last prune. Reset on each successful prune.</summary>
    internal volatile int _peakOverrideCount;

    /// <summary>Whether the high-water-mark warning has already been emitted (avoid log spam).</summary>
    private volatile bool _highWaterMarkWarned;

    internal EnabledBitsOverrides(ILogger log = null) => _log = log;

    /// <summary>
    /// Resolve the EnabledBits for an entity at the given transaction TSN.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort ResolveEnabledBits(long entityKey, ushort inlineBits, long txTsn)
    {
        // Fast path: no overrides exist anywhere → use inline bits
        if (_overrideCount == 0)
        {
            return inlineBits;
        }

        // Check if this specific entity has overrides
        if (!_overrides.TryGetValue(entityKey, out var history))
        {
            return inlineBits;
        }

        return history.ResolveAt(txTsn, inlineBits);
    }

    /// <summary>
    /// Record an EnabledBits change for MVCC. Called at commit time when older transactions exist.
    /// </summary>
    public void Record(long entityKey, long changeTSN, ushort oldBits)
    {
        var history = _overrides.GetOrAdd(entityKey, _ =>
        {
            int count = Interlocked.Increment(ref _overrideCount);

            // Track peak for diagnostics
            int peak;
            do
            {
                peak = _peakOverrideCount;
                if (count <= peak)
                {
                    break;
                }
            } while (Interlocked.CompareExchange(ref _peakOverrideCount, count, peak) != peak);

            // Warn once if override count grows suspiciously large (stale transactions blocking cleanup)
            if (count >= HighWaterMarkWarningThreshold && !_highWaterMarkWarned)
            {
                _highWaterMarkWarned = true;
                LogHighOverrideCount(count);
            }

            return new EnabledBitsHistory();
        });
        history.Record(changeTSN, oldBits);
    }

    /// <summary>
    /// Prune all entries whose changeTSN is at or below minTSN. Called when MinTSN advances.
    /// </summary>
    public void Prune(long minTSN)
    {
        if (_overrideCount == 0)
        {
            return;
        }

        var toRemove = new List<long>();
        foreach (var kvp in _overrides)
        {
            if (kvp.Value.TryPrune(minTSN))
            {
                toRemove.Add(kvp.Key);
            }
        }

        foreach (var key in toRemove)
        {
            if (_overrides.TryRemove(key, out _))
            {
                // Clamp to 0 — concurrent Record() may have incremented between our TryPrune and TryRemove
                int prev;
                do
                {
                    prev = _overrideCount;
                    if (prev <= 0)
                    {
                        break;
                    }
                } while (Interlocked.CompareExchange(ref _overrideCount, prev - 1, prev) != prev);
            }
        }

        // Reset peak tracking and re-arm warning after successful prune
        if (toRemove.Count > 0)
        {
            _peakOverrideCount = _overrideCount;
            if (_overrideCount < HighWaterMarkWarningThreshold)
            {
                _highWaterMarkWarned = false;
            }
        }
    }

    [LoggerMessage(LogLevel.Warning,
        "EnabledBitsOverrides: {count} entities have active overrides — long-running transactions may be blocking cleanup. " +
        "Consider reducing transaction lifetime or Enable/Disable frequency.")]
    private partial void LogHighOverrideCount(int count);
}
