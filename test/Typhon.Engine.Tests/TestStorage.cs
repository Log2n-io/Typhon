using System;
using System.IO;

namespace Typhon.Engine.Tests;

/// <summary>
/// Where this test RUN puts its databases: one directory per process, under the shared <c>Typhon.Tests</c> container.
/// </summary>
/// <remarks>
/// <para>
/// <b>Per-fixture isolation is not enough, because a fixture name is not unique across processes.</b> Every path was built as
/// <c>%TEMP%/Typhon.Tests/&lt;FixtureName&gt;</c>, which separates fixtures running side by side inside one test host — the contention
/// <see cref="AllocatorTestBase.TestDatabaseDir"/> was written to avoid — and separates nothing at all between two test hosts. Two of
/// them running the same fixture open the same file, and the second gets
/// <c>"The process cannot access the file … because it is being used by another process"</c>.
/// </para>
/// <para>
/// <b>That is the normal way this repository is used, not an edge case.</b> Work happens in several git worktrees at once, the nightly
/// runs in its own, and <c>bench/aws/shard.py run</c> starts eight test hosts deliberately. Measured on 2026-10-05: a sharded engine
/// run reported nine failures in eight unrelated fixtures, every one of them this lock error, while two other worktrees were running
/// the same suite. The failures survived both of the script's retries — the retry cannot win a lock the first attempt still holds —
/// and the run reported <c>OVERALL=FAIL</c> with "likely a real regression", which it was not. A gate result that depends on what else
/// happens to be running is worse than no gate result.
/// </para>
/// <para>
/// <b>The token carries the process id AND a random suffix.</b> The id alone is what you want when a directory outlives its run and you
/// are trying to work out whose it was; it is not enough on its own, because the OS reuses ids, and a new process inheriting a dead
/// one's directory inherits its locked and half-written files. The suffix removes that. The two together are also what let the sweep
/// below be safe.
/// </para>
/// <para>
/// This mirrors what <c>Typhon.Workbench.Tests</c> already does — a <see cref="Guid"/> per fixture — rather than inventing a
/// convention.
/// </para>
/// </remarks>
internal static class TestStorage
{
    /// <summary>The shared container every run's directory sits in, so one sweep can find them all.</summary>
    private static readonly string Container = Path.Combine(Path.GetTempPath(), "Typhon.Tests");

    /// <summary>
    /// How old a run directory must be before this process will delete somebody else's.
    /// </summary>
    /// <remarks>
    /// A day, which is far longer than any run: the engine suite is about a minute, the sharded run about fifty seconds, and the
    /// longest stress fixture is minutes. Deleting a LIVE run's database would be much worse than leaving a dead one's behind, so the
    /// threshold is set where nothing live can be mistaken for dead rather than where temp would be tidiest.
    /// </remarks>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(24);

    /// <summary>This run's root. Created before any test opens a database.</summary>
    public static string Root { get; }

    static TestStorage()
    {
        Root = Path.Combine(Container, $"run-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);

        // Best effort, both of them: a run that cannot tidy up is still a run that must produce a result.
        SweepStaleRuns();
        AppDomain.CurrentDomain.ProcessExit += static (_, _) => TryDelete(Root);
    }

    /// <summary>This run's directory for one fixture.</summary>
    /// <param name="fixture">The fixture's name.</param>
    /// <returns>The directory, not yet created.</returns>
    public static string ForFixture(string fixture) => Path.Combine(Root, fixture);

    /// <summary>
    /// Deletes run directories left behind by processes that are long gone.
    /// </summary>
    /// <remarks>
    /// Without this, a killed run leaks its databases for ever and <c>%TEMP%</c> grows without bound — which is a real cost here,
    /// because a fixture's cache is megabytes and a crash-recovery fixture keeps several bundles. Only <c>run-*</c> directories are
    /// considered, so a pre-existing per-fixture directory from before this change is left alone rather than deleted out from under a
    /// run that has not been rebuilt.
    /// </remarks>
    private static void SweepStaleRuns()
    {
        string[] candidates;
        try
        {
            candidates = Directory.GetDirectories(Container, "run-*");
        }
        catch (Exception)
        {
            return;
        }

        var cutoff = DateTime.UtcNow - StaleAfter;
        foreach (var candidate in candidates)
        {
            if (string.Equals(candidate, Root, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                if (Directory.GetLastWriteTimeUtc(candidate) < cutoff)
                {
                    TryDelete(candidate);
                }
            }
            catch (Exception)
            {
                // Vanished, or not ours to read. Either way the next run will reconsider it.
            }
        }
    }

    /// <summary>Removes a directory and everything under it, ignoring every failure.</summary>
    /// <param name="directory">The directory.</param>
    /// <remarks>
    /// A locked file is the expected failure and is not worth a message: it means a process still owns that directory, which is
    /// exactly when it must not be removed.
    /// </remarks>
    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception)
        {
            // Best effort by design.
        }
    }
}
