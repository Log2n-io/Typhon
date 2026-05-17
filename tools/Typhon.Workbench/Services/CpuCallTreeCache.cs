using System.Collections.Generic;
using Typhon.Workbench.Dtos.Profiler;

namespace Typhon.Workbench.Services;

/// <summary>
/// Per-session memo of folded call trees (#351 — H2). A call-tree fold is a server-side scan; without a cache, every panel interaction — a scope change,
/// a panel remount, an identical re-request — re-folds from scratch. The scope space a session actually exercises is small (a handful of systems / span
/// kinds × a few view modes), so a flat bounded map with clear-all eviction is enough — no LRU bookkeeping.
/// </summary>
public sealed class CpuCallTreeCache
{
    /// <summary>Past this many entries the whole map is flushed — the scope space is tiny, so a full clear is cheaper than tracking eviction order.</summary>
    private const int MaxEntries = 64;

    private readonly object _lock = new();
    private readonly Dictionary<string, CallTreeResponseDto> _entries = [];

    /// <summary>The canonical cache key for a request — every scope axis that changes the fold result, and nothing else.</summary>
    public static string KeyFor(CallTreeRequestDto r) =>
        $"{r.SpanKind}|{r.SystemIndex}|{r.Phase}|{r.StartUs}|{r.EndUs}|{r.FrameRoot}|{r.ViewMode}";

    /// <summary>Returns a previously folded tree for <paramref name="key"/>, if cached.</summary>
    public bool TryGet(string key, out CallTreeResponseDto value)
    {
        lock (_lock)
        {
            return _entries.TryGetValue(key, out value);
        }
    }

    /// <summary>Stores a folded tree under <paramref name="key"/>, flushing the map first if it has reached <see cref="MaxEntries"/>.</summary>
    public void Put(string key, CallTreeResponseDto value)
    {
        lock (_lock)
        {
            if (_entries.Count >= MaxEntries)
            {
                _entries.Clear();
            }
            _entries[key] = value;
        }
    }
}
