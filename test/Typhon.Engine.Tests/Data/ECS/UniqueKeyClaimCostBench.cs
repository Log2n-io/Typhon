using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine.Tests;

/// <summary>
/// What the engine-wide unique-key claim costs a commit that writes a unique index (#1390), measured against the same binary with the claim switched off.
/// </summary>
/// <remarks>
/// <para>
/// <b>[Explicit]: a measurement, not a check.</b> It asserts nothing about a duration — a number that fails the suite on a loaded machine teaches nothing — and
/// prints what it measured. Run it deliberately:
/// </para>
/// <code>
/// dotnet test test/Typhon.Engine.Tests/Typhon.Engine.Tests.csproj -c Release --filter "FullyQualifiedName~UniqueKeyClaimCostBench"
/// </code>
/// <para>
/// <b>One binary, one flag, interleaved pairs.</b> Two builds cannot be compared: the second moves the JIT's inlining decisions and the layout of everything
/// around the code under test, so a 2 % difference could be either. <c>UniqueKeyClaims.SkipClaimsForTest</c> flips the behaviour inside one process and the arms
/// alternate A,B,A,B…, because a machine that gets slower during the run (thermal, another process) biases a block design entirely into one arm and cancels out
/// of an interleaved one.
/// </para>
/// <para>
/// <b>Counted before it is timed.</b> Each arm reports the claims it actually took, so the per-commit cost has a denominator and an arm that silently claimed
/// nothing cannot be read as "claiming is free".
/// </para>
/// <para>
/// Two cases, and the second is the one worth running. Uncontended, a claim is an uncontended monitor and a hash insert, and the question is just how much.
/// <b>Contended is where the design could be wrong</b>: every unique-index write in the engine goes through ONE lock, so eight threads spawning eight DIFFERENT
/// keys — which never conflict and never need to see each other — still queue behind it. If that is expensive, the answer is a striped table or a per-index one,
/// and this is the measurement that would say so.
/// </para>
/// </remarks>
[TestFixture]
[Explicit("A measurement. Run it by name; it asserts no timing.")]
// Manual, not Nightly, and the linter is right to demand the choice. A nightly run would publish a number taken on whatever else that machine was
// doing, and this fixture's whole subject is a 50 ns difference inside a microsecond-scale operation — the per-commit arms already proved that a
// loaded box turns it into noise with the sign flipped. It is run deliberately, beside a change to the claim path, and read with its spread.
[Category("Manual")]
[NonParallelizable]
class UniqueKeyClaimCostBench : TestBase<UniqueKeyClaimCostBench>
{
    private const int Pairs = 8;
    private const int CommitsPerArm = 2_000;
    private const int Threads = 8;
    private const int BatchSpawns = 500;
    private const int BatchCommits = 20;

    /// <summary>
    /// 256 MiB. The contended arm spawns tens of thousands of entities per pair and the default 8 MiB cache cannot hold eight threads' working sets: the
    /// first attempt died on <c>PageCacheBackpressureTimeout</c> after 5 s with 985 epoch-protected pages, which measures the cache and not the claim.
    /// </summary>
    private const long BenchCacheSize = 256L * 1024 * 1024;

    /// <summary>Fewer commits per thread than the serial arm, for the same reason: eight threads multiply the working set by eight.</summary>
    private const int ConcurrentBatchCommits = 5;

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>One arm's result: what it did, and how long it took to do it.</summary>
    private readonly record struct Arm(double Nanos, long Claims, int Commits);

    private static Arm RunSerial(DatabaseEngine dbe, bool claims, ref int nextKey)
    {
        dbe.UniqueKeys.SkipClaimsForTest = !claims;
        dbe.UniqueKeys.ResetCountersForTest();

        var start = Stopwatch.GetTimestamp();
        for (var i = 0; i < CommitsPerArm; i++)
        {
            using var tx = dbe.CreateQuickTransaction();
            tx.Spawn<CompDArch>(CompDArch.D.Set(new CompD(1f, nextKey++, 1.0)));
            tx.Commit();
        }

        var elapsed = Stopwatch.GetElapsedTime(start).TotalNanoseconds;
        return new Arm(elapsed / CommitsPerArm, dbe.UniqueKeys.ClaimsTaken, CommitsPerArm);
    }

    /// <summary>
    /// Eight threads, each committing batches of spawns with disjoint keys: what ONE global claim lock costs writes that never conflict.
    /// </summary>
    /// <remarks>
    /// Batched for the reason <see cref="OneCommitManyKeys"/> gives — one key per commit buries the lock under a WAL append — and this is the arm that
    /// would condemn the design if it were expensive, because every unique-index write in the engine passes through one monitor. The keys are carved into
    /// disjoint per-thread ranges so no two threads ever want the same one: any cost here is the lock's own, paid by writes with no reason to meet.
    /// </remarks>
    private static Arm RunConcurrentBatch(DatabaseEngine dbe, bool claims, ref int nextKey)
    {
        dbe.UniqueKeys.SkipClaimsForTest = !claims;
        dbe.UniqueKeys.ResetCountersForTest();

        var bases = new int[Threads];
        for (var t = 0; t < Threads; t++)
        {
            bases[t] = nextKey;
            nextKey += ConcurrentBatchCommits * BatchSpawns;
        }

        using var gate = new Barrier(Threads);
        var tasks = new Task[Threads];
        var start = 0L;

        for (var t = 0; t < Threads; t++)
        {
            var slot = t;
            tasks[slot] = Task.Factory.StartNew(
                () =>
                {
                    gate.SignalAndWait();
                    if (slot == 0)
                    {
                        Volatile.Write(ref start, Stopwatch.GetTimestamp());
                    }

                    var key = bases[slot];
                    for (var c = 0; c < ConcurrentBatchCommits; c++)
                    {
                        using var tx = dbe.CreateQuickTransaction();
                        for (var i = 0; i < BatchSpawns; i++)
                        {
                            tx.Spawn<CompDArch>(CompDArch.D.Set(new CompD(slot, key++, 1.0)));
                        }

                        tx.Commit();
                    }
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        Task.WaitAll(tasks);
        var keys = Threads * ConcurrentBatchCommits * BatchSpawns;
        var elapsed = Stopwatch.GetElapsedTime(Volatile.Read(ref start)).TotalNanoseconds;
        return new Arm(elapsed / keys, dbe.UniqueKeys.ClaimsTaken, keys);
    }

    private static Arm RunConcurrent(DatabaseEngine dbe, bool claims, ref int nextKey)
    {
        dbe.UniqueKeys.SkipClaimsForTest = !claims;
        dbe.UniqueKeys.ResetCountersForTest();

        var perThread = CommitsPerArm / Threads;
        // Keys carved into disjoint ranges per thread, so nothing CONFLICTS: the point is the lock's own cost on writes that have no
        // reason to meet, which is exactly what a single global claim table makes them do.
        var bases = new int[Threads];
        for (var t = 0; t < Threads; t++)
        {
            bases[t] = nextKey;
            nextKey += perThread;
        }

        using var gate = new Barrier(Threads);
        var tasks = new Task[Threads];
        var start = 0L;

        for (var t = 0; t < Threads; t++)
        {
            var slot = t;
            tasks[slot] = Task.Factory.StartNew(
                () =>
                {
                    gate.SignalAndWait();
                    if (slot == 0)
                    {
                        Volatile.Write(ref start, Stopwatch.GetTimestamp());
                    }

                    for (var i = 0; i < perThread; i++)
                    {
                        using var tx = dbe.CreateQuickTransaction();
                        tx.Spawn<CompDArch>(CompDArch.D.Set(new CompD(slot, bases[slot] + i, 1.0)));
                        tx.Commit();
                    }
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        Task.WaitAll(tasks);
        var elapsed = Stopwatch.GetElapsedTime(Volatile.Read(ref start)).TotalNanoseconds;
        return new Arm(elapsed / (perThread * Threads), dbe.UniqueKeys.ClaimsTaken, perThread * Threads);
    }

    private static void Report(string what, List<Arm> on, List<Arm> off)
    {
        var deltas = new List<double>();
        for (var i = 0; i < on.Count; i++)
        {
            deltas.Add(on[i].Nanos - off[i].Nanos);
        }

        var onMed = Median(on.ConvertAll(a => a.Nanos));
        var offMed = Median(off.ConvertAll(a => a.Nanos));
        var deltaMed = Median(deltas);

        TestContext.Out.WriteLine($"  {what}");
        TestContext.Out.WriteLine($"    claims ON   {onMed,8:F0} ns/commit   ({on[0].Claims} claims over {on[0].Commits} commits in pair 0)");
        TestContext.Out.WriteLine($"    claims OFF  {offMed,8:F0} ns/commit   ({off[0].Claims} claims — zero is the premise)");
        var sorted = new List<double>(deltas);
        sorted.Sort();
        TestContext.Out.WriteLine($"    delta       {deltaMed,8:F0} ns          {(offMed <= 0 ? 0 : deltaMed / offMed * 100),6:F1} %   (median of {deltas.Count} interleaved pairs)");
        // The spread, because a median alone cannot say whether a measurement resolved anything. A range that straddles zero means it did not,
        // whatever the median reads — and a NEGATIVE median is the loud version of the same thing, since a lock does not make work faster.
        TestContext.Out.WriteLine($"    spread      {sorted[0],8:F0} .. {sorted[^1]:F0} ns   {(sorted[0] < 0 && sorted[^1] > 0 ? "STRADDLES ZERO — unresolved" : "one-signed")}");
    }

    private static double Median(List<double> xs)
    {
        xs.Sort();
        var n = xs.Count;
        return n % 2 == 1 ? xs[n / 2] : (xs[(n / 2) - 1] + xs[n / 2]) / 2;
    }

    /// <summary>
    /// The claim's own cost, resolved by putting many keys in ONE commit instead of one key in many.
    /// </summary>
    /// <remarks>
    /// <b>Written because the per-commit arms could not resolve it, and one of them came out NEGATIVE.</b> A commit costs 25-38 us here — a WAL append and
    /// a durability wait — and the claim is a couple of hundred nanoseconds, so the signal is well under 1 % of a noisy quantity and the eight-thread arm
    /// measured the claim as making commits FASTER, which is not a thing a lock does. A lock cannot be measured by timing something a thousand times its
    /// size; the ratio has to change. One commit spawning <see cref="BatchSpawns"/> entities takes that many claims against one append and one wait, so the
    /// claim is most of what differs between the arms rather than a rounding error in it.
    /// </remarks>
    [Test]
    [Property("CacheSize", BenchCacheSize)]
    public void OneCommitManyKeys()
    {
        using var dbe = SetupEngine();
        var key = 100_000_000;

        RunBatch(dbe, claims: true, ref key);
        RunBatch(dbe, claims: false, ref key);

        var on = new List<Arm>();
        var off = new List<Arm>();
        for (var p = 0; p < Pairs; p++)
        {
            on.Add(RunBatch(dbe, claims: true, ref key));
            off.Add(RunBatch(dbe, claims: false, ref key));
        }

        Report($"one commit of {BatchSpawns} spawns, {BatchCommits} commits per arm — per KEY, not per commit", on, off);
    }

    /// <summary>One commit, many spawns: the arm reports per-KEY rather than per-commit, since that is what the claim is paid per.</summary>
    private static Arm RunBatch(DatabaseEngine dbe, bool claims, ref int nextKey)
    {
        dbe.UniqueKeys.SkipClaimsForTest = !claims;
        dbe.UniqueKeys.ResetCountersForTest();

        var start = Stopwatch.GetTimestamp();
        for (var c = 0; c < BatchCommits; c++)
        {
            using var tx = dbe.CreateQuickTransaction();
            for (var i = 0; i < BatchSpawns; i++)
            {
                tx.Spawn<CompDArch>(CompDArch.D.Set(new CompD(1f, nextKey++, 1.0)));
            }

            tx.Commit();
        }

        var keys = BatchCommits * BatchSpawns;
        var elapsed = Stopwatch.GetElapsedTime(start).TotalNanoseconds;
        return new Arm(elapsed / keys, dbe.UniqueKeys.ClaimsTaken, keys);
    }
    /// <summary>
    /// The claim table on its own: <c>TryClaim</c> + <c>Release</c> over the engine's real unique index, one thread and then eight on disjoint keys.
    /// </summary>
    /// <remarks>
    /// <b>Here because every arm that timed a COMMIT failed to resolve the claim, and said so.</b> Per-commit and per-key alike, the median delta came out
    /// negative with a spread straddling zero — a commit is thousands of nanoseconds of WAL and page cache whose run-to-run swing is larger than the whole
    /// quantity under test, and a lock does not make work faster. The rule that applies is the one about two failed timings: stop timing something a
    /// thousand times the size of the thing and measure the thing.
    /// <para>
    /// So this calls the table directly, with a real <c>IBTreeIndex</c> taken from the archetype's own cluster state and a key on the stack — the engine's
    /// pointer rule allows a stack address, and this is what the commit path passes anyway. What it answers is the design question: eight threads claiming
    /// keys that never collide still queue on ONE monitor, and if that serialises them the table needs striping or a per-index lock.
    /// </para>
    /// </remarks>
    [Test]
    public unsafe void TheClaimTableItself()
    {
        using var dbe = SetupEngine();

        // One commit, so the archetype's cluster state and its index exist to be borrowed below.
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<CompDArch>(CompDArch.D.Set(new CompD(1f, -1, 1.0)));
            tx.Commit();
        }

        var index = UniqueIndexOf(dbe);
        Assert.That(index, Is.Not.Null, "premise: CompD.B is a unique index on this archetype");

        const int Ops = 200_000;
        var table = dbe.UniqueKeys;

        var serial = TimeClaims(table, index, Ops, threads: 1);
        var parallel = TimeClaims(table, index, Ops, threads: Threads);

        TestContext.Out.WriteLine($"  the claim table alone, {Ops} claim+release pairs per thread");
        TestContext.Out.WriteLine($"    1 thread    {serial,8:F1} ns per claim+release");
        TestContext.Out.WriteLine($"    {Threads} threads   {parallel,8:F1} ns per claim+release   {parallel / serial,5:F1}x the single-thread cost");
        TestContext.Out.WriteLine($"    a commit is 3-12 us, so one claim is {serial / 8000 * 100,5:F2}% of an 8 us commit at this cost");
    }

    /// <summary>Median of three runs of <paramref name="threads"/> threads each doing <paramref name="ops"/> claim+release pairs on their own keys.</summary>
    private static unsafe double TimeClaims(UniqueKeyClaims table, IBTreeIndex index, int ops, int threads)
    {
        var runs = new List<double>();
        for (var r = 0; r < 4; r++)
        {
            using var gate = new Barrier(threads);
            var tasks = new Task[threads];
            var start = 0L;
            for (var t = 0; t < threads; t++)
            {
                var slot = t;
                tasks[slot] = Task.Factory.StartNew(
                    () =>
                    {
                        // Disjoint per thread: the cost measured is the lock's, not a conflict's.
                        var key = slot * ops;
                        gate.SignalAndWait();
                        if (slot == 0)
                        {
                            Volatile.Write(ref start, Stopwatch.GetTimestamp());
                        }

                        for (var i = 0; i < ops; i++)
                        {
                            var k = key + i;
                            // On the stack, which the engine's pointer rule allows and which is what the commit path hands it.
                            var claimed = table.TryClaim(index, (byte*)&k);
                            if (claimed)
                            {
                                table.Release(new UniqueKeyRef(index, (byte*)&k));
                            }
                        }
                    },
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
            }

            Task.WaitAll(tasks);
            if (r > 0)
            {
                runs.Add(Stopwatch.GetElapsedTime(Volatile.Read(ref start)).TotalNanoseconds / ((double)ops * threads));
            }
        }

        return Median(runs);
    }

    /// <summary>The archetype's first unique index, borrowed from its cluster state the way the commit path reaches it.</summary>
    private static IBTreeIndex UniqueIndexOf(DatabaseEngine dbe)
    {
        foreach (var meta in Typhon.Engine.Internals.ArchetypeRegistry.GetAllArchetypes())
        {
            if (meta.ArchetypeType != typeof(CompDArch))
            {
                continue;
            }

            // The engine's own routing, the way the commit path resolves an archetype's cluster state.
            var cs = dbe._stateByRouting[dbe.RoutingIdOf(meta)]?.ClusterState;
            if (cs == null)
            {
                continue;
            }

            foreach (ref var slot in cs.IndexSlots.AsSpan())
            {
                foreach (ref var field in slot.Fields.AsSpan())
                {
                    if (!field.AllowMultiple)
                    {
                        return field.Index;
                    }
                }
            }
        }

        return null;
    }


    [Test]
    public void OneThread()
    {
        using var dbe = SetupEngine();
        var key = 1;

        // Discarded: the first arm pays for the JIT, the cluster's growth and the index's first pages.
        RunSerial(dbe, claims: true, ref key);
        RunSerial(dbe, claims: false, ref key);

        var on = new List<Arm>();
        var off = new List<Arm>();
        for (var p = 0; p < Pairs; p++)
        {
            on.Add(RunSerial(dbe, claims: true, ref key));
            off.Add(RunSerial(dbe, claims: false, ref key));
        }

        Report($"one thread, {CommitsPerArm} commits per arm, one unique key each", on, off);
    }

    [Test]
    [Property("CacheSize", BenchCacheSize)]
    public void EightThreadsDistinctKeys()
    {
        using var dbe = SetupEngine();
        var key = 10_000_000;

        RunConcurrentBatch(dbe, claims: true, ref key);
        RunConcurrentBatch(dbe, claims: false, ref key);

        var on = new List<Arm>();
        var off = new List<Arm>();
        for (var p = 0; p < Pairs; p++)
        {
            on.Add(RunConcurrentBatch(dbe, claims: true, ref key));
            off.Add(RunConcurrentBatch(dbe, claims: false, ref key));
        }

        Report($"{Threads} threads x {ConcurrentBatchCommits} commits x {BatchSpawns} spawns, disjoint keys — the single global lock's own cost", on, off);
    }
}
