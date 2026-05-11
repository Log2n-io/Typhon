using Typhon.Profiler;
using Typhon.Workbench.Dtos.Profiler;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Services;

/// <summary>
/// Per-session lazy facade around <see cref="QueryCatalogBuilder"/>. Constructs the catalog on first
/// access and caches it for the session lifetime — subsequent requests are O(1) Dictionary lookups
/// against the pre-built indexes carried on <see cref="QueryCatalogData"/>.
/// </summary>
/// <remarks>
/// Thread-safe: <see cref="GetCatalogAsync"/> uses a single <see cref="TaskCompletionSource{T}"/>
/// (initialized lazily under a lock) so concurrent first-callers all observe the same build task.
/// The build runs on a thread-pool thread untied to any specific caller's CancellationToken — that
/// way a cancelling caller cannot poison the cache for unrelated subsequent callers. Each caller
/// honors its own CT non-destructively via <see cref="Task.WaitAsync(CancellationToken)"/>.
/// </remarks>
public sealed class QueryCatalogService
{
    /// <summary>Page-size hard ceiling enforced at the service layer. Larger requests are rejected with <see cref="ArgumentOutOfRangeException"/>.</summary>
    public const int MaxPageSize = 500;

    private readonly IChunkProvider _provider;
    private readonly Func<ProfilerMetadataDto> _metadataAccessor;
    private readonly Func<string[]> _sourceStringsAccessor;
    private readonly object _initLock = new();
    private TaskCompletionSource<QueryCatalogData> _tcs;

    /// <summary>
    /// Construct a catalog service for a session. The accessors are deferred so we don't capture stale
    /// state if the build is triggered before the session's metadata is ready (we'll re-read at build
    /// time).
    /// </summary>
    public QueryCatalogService(IChunkProvider provider, Func<ProfilerMetadataDto> metadataAccessor, Func<string[]> sourceStringsAccessor)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _metadataAccessor = metadataAccessor ?? throw new ArgumentNullException(nameof(metadataAccessor));
        _sourceStringsAccessor = sourceStringsAccessor ?? throw new ArgumentNullException(nameof(sourceStringsAccessor));
    }

    /// <summary>
    /// Returns the catalog data. First call triggers the build; subsequent calls return the same cached Task.
    /// The build is detached from any caller's CT — passing a cancelled <paramref name="ct"/> only abandons
    /// the await; it does not abort the build or poison the cache.
    /// </summary>
    public Task<QueryCatalogData> GetCatalogAsync(CancellationToken ct = default)
    {
        Task<QueryCatalogData> task;
        lock (_initLock)
        {
            if (_tcs == null)
            {
                _tcs = new TaskCompletionSource<QueryCatalogData>(TaskCreationOptions.RunContinuationsAsynchronously);
                // CancellationToken.None — the build is session-scoped, not request-scoped. If we passed
                // the first caller's CT, their cancellation would set _tcs to Canceled forever (the
                // sticky state of a TaskCompletionSource), and every subsequent caller would observe
                // TaskCanceledException despite never having cancelled themselves.
                _ = Task.Run(() => BuildOnceAsync(CancellationToken.None), CancellationToken.None);
            }
            task = _tcs.Task;
        }
        // Honor the caller's CT non-destructively — WaitAsync surfaces their cancellation as a per-await
        // OperationCanceledException without affecting the shared task.
        return ct.CanBeCanceled ? task.WaitAsync(ct) : task;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Public lookup methods (mirror the 4 endpoint shapes)
    // ─────────────────────────────────────────────────────────────────────────

    public async Task<QueryDefinitionDto[]> GetAllDefinitionsAsync(CancellationToken ct = default)
    {
        var data = await GetCatalogAsync(ct);
        return data.Definitions;
    }

    public async Task<QueryDefinitionDto> GetDefinitionAsync(byte kind, uint localId, CancellationToken ct = default)
    {
        var data = await GetCatalogAsync(ct);
        var key = ((ulong)kind << 32) | localId;
        return data.DefinitionsByKey.TryGetValue(key, out var dto) ? dto : null;
    }

    public async Task<QueryExecutionDto[]> GetExecutionsAsync(
        byte kind,
        uint localId,
        long? fromTick,
        long? toTick,
        int? systemId,
        int pageSize,
        int pageOffset,
        CancellationToken ct = default)
    {
        if (pageSize <= 0 || pageSize > MaxPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), pageSize, $"pageSize must be in 1..{MaxPageSize}.");
        }
        if (pageOffset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageOffset), pageOffset, "pageOffset must be >= 0.");
        }

        var data = await GetCatalogAsync(ct);
        var key = ((ulong)kind << 32) | localId;
        if (!data.ExecutionsByDefKey.TryGetValue(key, out var bucket) || bucket.Length == 0)
        {
            return [];
        }

        // Bucket is already scoped to one (kind, localId); per-row predicate eliminates rows that fail
        // the optional tick-range / system filters. Iteration is O(bucket size) — typically tens to
        // hundreds of executions for a single definition.
        var filtered = new List<QueryExecutionDto>(Math.Min(bucket.Length, pageSize));
        for (var i = 0; i < bucket.Length; i++)
        {
            var e = bucket[i];
            if (fromTick.HasValue && e.TickIndex < fromTick.Value) continue;
            if (toTick.HasValue && e.TickIndex > toTick.Value) continue;
            if (systemId.HasValue && e.SystemId != systemId.Value) continue;
            filtered.Add(e);
        }

        if (pageOffset >= filtered.Count) return [];
        var take = Math.Min(pageSize, filtered.Count - pageOffset);
        var result = new QueryExecutionDto[take];
        filtered.CopyTo(pageOffset, result, 0, take);
        return result;
    }

    public async Task<QueryExecutionDto> GetExecutionBySpanIdAsync(long spanId, CancellationToken ct = default)
    {
        var data = await GetCatalogAsync(ct);
        return data.ExecutionsBySpanId.TryGetValue(spanId, out var exec) ? exec : null;
    }

    /// <summary>
    /// Returns the executions whose parent span id matches <paramref name="parentSpanId"/>. The profiler
    /// detail pane uses this to round-trip from a selected system span to the matching query execution(s)
    /// — pull-mode views' per-tick QueryPlan spans are parented under the system's
    /// <c>Scheduler:System:SingleThreaded</c> span. Empty array when no matches.
    /// </summary>
    public async Task<QueryExecutionDto[]> GetExecutionsByParentSpanIdAsync(long parentSpanId, CancellationToken ct = default)
    {
        var data = await GetCatalogAsync(ct);
        return data.ExecutionsByParentSpanId.TryGetValue(parentSpanId, out var execs) ? execs : [];
    }

    /// <summary>
    /// Returns the executions for a given (systemIdx, tickIndex) pair. Used by the profiler detail pane
    /// in multi-threaded mode where parent-span linkage is unreliable — chunks carry the system index
    /// natively, and the runtime-emitted QueryPlan spans carry <c>OwnerSystemIdx</c> in their wire format.
    /// Typically returns at most one execution per (system, tick) pair when the system owns a single view.
    /// </summary>
    public async Task<QueryExecutionDto[]> GetExecutionsBySystemTickAsync(int systemIdx, long tickIndex, CancellationToken ct = default)
    {
        var data = await GetCatalogAsync(ct);
        var matches = new List<QueryExecutionDto>(2);
        foreach (var exec in data.Executions)
        {
            if (exec.SystemId == systemIdx && exec.TickIndex == tickIndex)
            {
                matches.Add(exec);
            }
        }
        return matches.ToArray();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Internals
    // ─────────────────────────────────────────────────────────────────────────

    private async Task BuildOnceAsync(CancellationToken ct)
    {
        try
        {
            var metadata = _metadataAccessor();
            var sources = _sourceStringsAccessor();
            if (metadata == null)
            {
                _tcs.TrySetResult(EmptyCatalog());
                return;
            }
            var data = await QueryCatalogBuilder.BuildAsync(_provider, metadata, sources ?? [], ct);
            _tcs.TrySetResult(data);
        }
        catch (OperationCanceledException)
        {
            // ct is CancellationToken.None here — this path is for the build itself being abandoned by
            // an inner IO operation (rare). We surface as a generic cancellation; downstream callers'
            // WaitAsync will observe their own CT independently.
            _tcs.TrySetCanceled();
        }
        catch (Exception ex)
        {
            _tcs.TrySetException(ex);
        }
    }

    private static QueryCatalogData EmptyCatalog()
    {
        var emptyDefs = new Dictionary<ulong, QueryDefinitionDto>();
        var emptyExecsByDef = new Dictionary<ulong, QueryExecutionDto[]>();
        var emptyExecsBySpan = new Dictionary<long, QueryExecutionDto>();
        var emptyExecsByParent = new Dictionary<long, QueryExecutionDto[]>();
        return new QueryCatalogData([], [], emptyDefs, emptyExecsByDef, emptyExecsBySpan, emptyExecsByParent);
    }
}
