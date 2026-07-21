using NUnit.Framework;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// Tests for the once-per-session "describe each query identity exactly once" semantics (#342, P2).
/// Producer-side dedup: <see cref="QueryDefinitionDescribeTracker"/>.TryMarkAndCheck returns <c>true</c> the first time
/// per (kind, localId) per session, <c>false</c> thereafter — exactly the "emit-on-first-sight" property required by
/// the design (claude/design/Profiler/11-query-definition-export.md §4.6).
/// </summary>
[TestFixture]
[NonParallelizable] // mutates the process-static QueryDefinitionDescribeTracker; a parallel fixture pollutes its dedup state
public class QueryDefinitionDescribeOnceTests
{
    [SetUp]
    public void Setup() => QueryDefinitionDescribeTracker.Reset();

    [Test]
    public void EmittedOnce_AcrossMultipleAttempts_ForSameIdentity()
    {
        // Simulate N executions of the same logical query: only the first one's TryMarkAndCheck returns true.
        const int attempts = 100;
        int emittedCount = 0;

        for (var i = 0; i < attempts; i++)
        {
            if (QueryDefinitionDescribeTracker.TryMarkAndCheck(kind: 1, localId: 42))
            {
                emittedCount++;
            }
        }

        Assert.That(emittedCount, Is.EqualTo(1));
    }

    [Test]
    public void EmittedOncePerDistinctIdentity_AcrossMultipleQueries()
    {
        // 3 distinct (kind, localId) identities, each executed 10 times.
        // Total emissions across all should be exactly 3 (one per identity).
        int emittedCount = 0;
        var identities = new (byte kind, uint localId)[]
        {
            (0, 1),  // View, ViewId=1
            (0, 2),  // View, ViewId=2
            (1, 1),  // EcsQuery, EcsQueryId=1  ← different from (0,1) because kind differs
        };

        for (var pass = 0; pass < 10; pass++)
        {
            foreach (var (kind, localId) in identities)
            {
                if (QueryDefinitionDescribeTracker.TryMarkAndCheck(kind, localId))
                {
                    emittedCount++;
                }
            }
        }

        Assert.That(emittedCount, Is.EqualTo(3));
    }

    [Test]
    public void ResetClearsAcrossSession_AllowsNewEmissions()
    {
        // Mid-session state: identity (1, 7) already described.
        Assert.That(QueryDefinitionDescribeTracker.TryMarkAndCheck(1, 7), Is.True);
        Assert.That(QueryDefinitionDescribeTracker.TryMarkAndCheck(1, 7), Is.False);

        // Simulate new session start: Reset clears state.
        QueryDefinitionDescribeTracker.Reset();

        // Same identity in the new session: describes again.
        Assert.That(QueryDefinitionDescribeTracker.TryMarkAndCheck(1, 7), Is.True);
    }
}
