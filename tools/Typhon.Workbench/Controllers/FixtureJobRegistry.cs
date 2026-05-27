#if DEBUG
using System.Collections.Concurrent;
using Typhon.Workbench.Fixtures;

namespace Typhon.Workbench.Controllers;

/// <summary>
/// In-memory state of a single Dev Fixture generation job. The Workbench's create-fixture endpoint returns a
/// <c>JobId</c> immediately and spawns the actual generation on a background <c>Task</c>; the client polls
/// <c>GET /api/fixtures/jobs/{id}</c> to surface progress in the UI (state machine: <c>queued → running →
/// done | error | cancelled</c>). The <c>Result</c> + <c>Error</c> slots are populated terminally — once
/// <c>State</c> is one of the three terminal values they don't change again, so the client can stop polling.
/// </summary>
/// <param name="JobId">UUID identifying this job; the client polls + cancels by this id.</param>
/// <param name="State">"queued" | "running" | "done" | "error" | "cancelled".</param>
/// <param name="Phase">Human-readable label of the current step ("Spawning Players", …) — empty before progress lands.</param>
/// <param name="Completed">Entities processed within the current phase.</param>
/// <param name="Total">Total entities expected within the current phase. <c>0</c> = indeterminate / instant phase.</param>
/// <param name="Result">Terminal result on <c>State=="done"</c>; null otherwise.</param>
/// <param name="Error">Terminal error message on <c>State=="error"</c>; null otherwise.</param>
public sealed record FixtureJobStateDto(
    string JobId,
    string State,
    string Phase,
    int Completed,
    int Total,
    FixtureCreateResultDto Result,
    string Error);

/// <summary>Terminal result reported by a successful fixture-generation job. Mirrors the previous synchronous response.</summary>
public sealed record FixtureCreateResultDto(string TyphonFilePath, string SchemaDllPath, int TotalEntities, bool WasCreated);

/// <summary>
/// One running Dev Fixture generation. Wraps the background <c>Task</c>, the <see cref="CancellationTokenSource"/> the
/// DELETE endpoint trips, and a mutable state snapshot the GET endpoint reads under a lock. Single-process, in-memory —
/// the Workbench restarts wipe pending jobs; that's fine for a DEBUG-only dev tool.
/// </summary>
internal sealed class FixtureJob
{
    private readonly object _lock = new();
    private FixtureJobStateDto _state;

    public string JobId { get; }
    public CancellationTokenSource Cts { get; } = new();
    public Task Task { get; private set; } = Task.CompletedTask;

    public FixtureJob(string jobId)
    {
        JobId = jobId;
        _state = new FixtureJobStateDto(JobId: jobId, State: "queued", Phase: "", Completed: 0, Total: 0, Result: null, Error: null);
    }

    public FixtureJobStateDto Snapshot()
    {
        lock (_lock) return _state;
    }

    public void SetRunning()
    {
        lock (_lock) _state = _state with { State = "running" };
    }

    public void SetProgress(string phase, int completed, int total)
    {
        // Progress callback fires from the background Task's thread. The lock pairs with the GET endpoint's snapshot
        // read so the client never observes a half-updated state record.
        lock (_lock) _state = _state with { State = "running", Phase = phase, Completed = completed, Total = total };
    }

    public void SetDone(FixtureGenerationResult result)
    {
        lock (_lock) _state = _state with
        {
            State = "done",
            Result = new FixtureCreateResultDto(
                TyphonFilePath: result.TyphonFilePath,
                SchemaDllPath: result.SchemaDllPath,
                TotalEntities: result.TotalEntities,
                WasCreated: result.WasCreated),
        };
    }

    public void SetCancelled()
    {
        lock (_lock) _state = _state with { State = "cancelled" };
    }

    public void SetError(string message)
    {
        lock (_lock) _state = _state with { State = "error", Error = message };
    }

    public void AttachTask(Task task) => Task = task;
}

/// <summary>
/// Process-lifetime registry of Dev Fixture generation jobs. A static <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// because the registry's lifetime matches the application's — the same shape as <see cref="FixturesController.MockServers"/>
/// for the in-process mock profiler servers. DEBUG-only by file gating; the controller never references this type in
/// Release builds.
/// </summary>
internal static class FixtureJobRegistry
{
    private static readonly ConcurrentDictionary<string, FixtureJob> _jobs = new();

    /// <summary>Register a new job with a freshly-generated id and return it. The caller spawns the work via <see cref="FixtureJob.AttachTask"/>.</summary>
    public static FixtureJob Create()
    {
        var job = new FixtureJob(Guid.NewGuid().ToString("N"));
        _jobs[job.JobId] = job;
        return job;
    }

    /// <summary>Look up a job by id. Returns null when the id is unknown (already pruned, or never existed).</summary>
    public static FixtureJob Get(string jobId)
        => _jobs.TryGetValue(jobId, out var j) ? j : null;

    /// <summary>
    /// Cancel a job and remove it from the registry. Returns true if a job was found and signalled — the background Task
    /// may still be mid-batch; the controller's DELETE endpoint doesn't wait for it to finish unwinding.
    /// </summary>
    public static bool Cancel(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job)) return false;
        job.Cts.Cancel();
        return true;
    }

    /// <summary>Best-effort eviction of terminal jobs so the registry doesn't grow unbounded across the process lifetime.</summary>
    public static void Prune()
    {
        foreach (var (id, job) in _jobs)
        {
            var s = job.Snapshot().State;
            if (s == "done" || s == "error" || s == "cancelled")
            {
                _jobs.TryRemove(id, out _);
                job.Cts.Dispose();
            }
        }
    }
}
#endif
