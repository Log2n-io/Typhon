using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine.Tests;

/// <summary>
/// Time-bounded race-stress harness for OLC B+Tree (issue #297).
/// Loops the five flaky concurrency scenarios from <see cref="OlcBTreeTests"/> in parallel under saturating CPU noise to maximize repro density.
/// Different from <see cref="OlcBTreeStressTests"/> (high-thread single-shot scenarios) — this one is for "fail fast on a known race."
/// [Explicit] + [Category("Nightly")] — kept out of the PR gate for its wall duration, but it RUNS in the nightly tier. It used to be
/// [Explicit] with no tier, i.e. nowhere: the latch-coupled-SMO fix whose design claims "Validated By: stress tests from #117" then had
/// no running evidence in CI from February 2026 onward (#703).
/// Configure via env vars:
///   OLC_STRESS_SECONDS — wall duration of the run (default 30)
///   OLC_STRESS_NOISE   — count of CPU-saturating noise threads (default = ProcessorCount/2)
/// One ManagedPagedMMF per scenario, reused across iterations (fresh segment per iter) — avoids per-iter file I/O cost.
/// <para>
/// [NonParallelizable] for the same reason <see cref="OlcBTreeStressTests"/> carries it, and it was missing here (#738). The assembly runs
/// <c>Parallelizable(ParallelScope.Fixtures)</c> at <c>LevelOfParallelism(4)</c>, so without it this fixture — which already spawns five scenario tasks plus
/// ProcessorCount/2 noise threads of its own — is one of FOUR heavy fixtures running at once. A nightly `Category=Nightly` run in that configuration took the
/// test host down with it, alongside ChaosStressTests.UltimateStress_AllSubsystems and two others. A harness whose whole purpose is to reproduce one specific
/// race cannot do that from inside uncontrolled oversubscription: whatever it then reports is a property of the tier, not of the tree.
/// </para>
/// </summary>
[TestFixture]
[Explicit("Long-running race-stress harness for issue #297")]
[Category("Nightly")]
[NonParallelizable]
public class OlcBTreeRaceStressTests
{
    private static int _scenarioId;

    [Test]
    [CancelAfter(600_000)]  // 10 min — caller sets the actual duration via OLC_STRESS_SECONDS
    public unsafe void OlcRaceStress_FourScenariosUnderCpuNoise()
    {
        var seconds = ParseEnvInt("OLC_STRESS_SECONDS", 30);
        var noiseCount = ParseEnvInt("OLC_STRESS_NOISE", Math.Max(1, Environment.ProcessorCount / 2));
        var deadline = TimeSpan.FromSeconds(seconds);

        TestContext.WriteLine($"OLC race stress: duration={seconds}s noiseThreads={noiseCount} cores={Environment.ProcessorCount}");

        // Wire up OLC descent diagnostic capture for the duration of the harness.
        OlcDescentTrace.RecordStep = DescentTraceRecord;
        OlcDescentTrace.OnInvalidChunkId = DescentTraceOnInvalidChunkId;
        OlcDescentTrace.OnRemoveNotFound = OnRemoveNotFoundCapture;
        for (int i = 0; i < _removeNotFoundByBranch.Length; i++) { _removeNotFoundByBranch[i] = 0; }
        _removeNotFoundDetailsCaptured = 0;
        _rdNotRemoved = 0; _rdWrongValue = 0;
        while (_rdSamples.TryTake(out _)) { }
        try
        {

        using var stop = new ManualResetEventSlim(false);
        var noiseTasks = StartNoise(noiseCount, stop);

        var scenarios = new[]
        {
            new Scenario("Add_Splits",      AddSplitsBody),
            new Scenario("Add_Disjoint",    AddDisjointBody),
            new Scenario("Remove_Disjoint", RemoveDisjointBody),
            new Scenario("Remove_Merges",   RemoveMergesBody),
            new Scenario("Remove_Mixed",    RemoveMixedBody),
        };

        var sw = Stopwatch.StartNew();
        var scenarioTasks = scenarios.Select(s => Task.Factory.StartNew(() => RunScenarioLoop(s, stop), TaskCreationOptions.LongRunning)).ToArray();

        Thread.Sleep(deadline);
        stop.Set();

        Task.WaitAll(scenarioTasks);
        Task.WaitAll(noiseTasks);
        sw.Stop();

        var report = new StringBuilder();
        report.AppendLine();
        report.AppendLine($"=== OLC race stress report — wall {sw.Elapsed.TotalSeconds:F1}s ===");
        long totalIters = 0, totalFails = 0;
        foreach (var s in scenarios)
        {
            int iters = Volatile.Read(ref s.Iterations);
            int fails = s.Failures.Count;
            totalIters += iters;
            totalFails += fails;
            report.AppendLine($"  {s.Name,-18} iter={iters,6}  fail={fails,4}  rate={(iters == 0 ? 0 : (double)fails / iters):P2}");
        }
        report.AppendLine($"  {"TOTAL",-18} iter={totalIters,6}  fail={totalFails,4}");

        if (totalFails > 0)
        {
            report.AppendLine();
            report.AppendLine("=== first failure per scenario ===");
            foreach (var s in scenarios)
            {
                if (s.Failures.IsEmpty)
                {
                    continue;
                }
                var first = s.Failures.OrderBy(f => f.Iteration).First();
                report.AppendLine($"--- {s.Name} (iter {first.Iteration}) ---");
                report.AppendLine(first.Detail);
                report.AppendLine();
            }
        }
        // Append Remove NotFound branch summary BEFORE writing report to test context.
        var rnfTotal = 0L;
        for (int i = 1; i < _removeNotFoundByBranch.Length; i++) { rnfTotal += _removeNotFoundByBranch[i]; }
        if (rnfTotal > 0)
        {
            report.AppendLine();
            report.AppendLine("=== Remove NotFound branch counts ===");
            report.AppendLine($"  begin-fast-path (key < ll.firstKey)    : {_removeNotFoundByBranch[OlcDescentTrace.RemoveBranchBeginFastPathLessThanFirst]}");
            report.AppendLine($"  end-fast-path   (key > rll.lastKey)    : {_removeNotFoundByBranch[OlcDescentTrace.RemoveBranchEndFastPathGreaterThanLast]}");
            report.AppendLine($"  general path    (descend keyIndex<0)   : {_removeNotFoundByBranch[OlcDescentTrace.RemoveBranchGeneralKeyIndexNegative]}");
            report.AppendLine($"  under-lock re-find (concurrent removed): {_removeNotFoundByBranch[OlcDescentTrace.RemoveBranchUnderLockReFindNegative]}");
            report.AppendLine($"  PESS begin-fast-path (key < ll.first)  : {_removeNotFoundByBranch[OlcDescentTrace.RemoveBranchPessimisticBeginLessThanFirst]}");
            report.AppendLine($"  PESS end-fast-path   (key > rll.last)  : {_removeNotFoundByBranch[OlcDescentTrace.RemoveBranchPessimisticEndGreaterThanLast]}");
            report.AppendLine($"  TOTAL: {rnfTotal}");
        }
        if (_rdNotRemoved > 0 || _rdWrongValue > 0)
        {
            report.AppendLine();
            report.AppendLine("=== Remove_Disjoint failure split ===");
            report.AppendLine($"  not_removed (Remove returned false)    : {_rdNotRemoved}");
            report.AppendLine($"  wrong_value (Remove returned bad value): {_rdWrongValue}");
            report.AppendLine($"  Sample failures:");
            int n = 0;
            foreach (var sample in _rdSamples) { if (++n > 10) break; report.AppendLine($"    {sample}"); }
        }

        TestContext.WriteLine(report.ToString());

        Assert.That(totalFails, Is.Zero, () => $"OLC race stress observed {totalFails} failures across {totalIters} iterations.\n{report}");
        }
        finally
        {
            OlcDescentTrace.RecordStep = null;
            OlcDescentTrace.OnInvalidChunkId = null;
            OlcDescentTrace.OnRemoveNotFound = null;
        }
    }

    // === Remove NotFound branch capture ===
    private static readonly long[] _removeNotFoundByBranch = new long[OlcDescentTrace.RemoveBranchCount];
    private static int _removeNotFoundDetailsCaptured;
    private static readonly object _rnfLock = new();
    private static readonly string _rnfDetailsPath = (Environment.GetEnvironmentVariable("OLC_STRESS_LOG")
        ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "olc-race-stress.log"))
        .Replace(".log", ".rnf.log");

    private static void OnRemoveNotFoundCapture(int branch, int key, int leafChunkId, int firstOrLastKey, int leafCount)
    {
        if (branch >= 0 && branch < _removeNotFoundByBranch.Length)
        {
            Interlocked.Increment(ref _removeNotFoundByBranch[branch]);
        }
        // Cap detailed dumps to keep the log tractable.
        if (Interlocked.Increment(ref _removeNotFoundDetailsCaptured) > 30)
        {
            return;
        }
        var line = $"{DateTime.UtcNow:HH:mm:ss.fff} branch={branch} key={key} leaf={leafChunkId} pivot={firstOrLastKey} count={leafCount}\n";
        lock (_rnfLock)
        {
            try { System.IO.File.AppendAllText(_rnfDetailsPath, line); } catch { }
        }
    }

    // ====== Descent diagnostic capture ======

    [ThreadStatic] private static DescentStep[] _descentRing;
    [ThreadStatic] private static int _descentRingHead;
    private const int DescentRingSize = 64;
    private static int _descentDumpsCaptured;

    private readonly record struct DescentStep(int Op, int ParentChunkId, int ParentVersion, int ChildIndex, int ChildChunkId);

    private static void DescentTraceRecord(int op, int parentChunkId, int parentVersion, int childIndex, int childChunkId)
    {
        var ring = _descentRing ??= new DescentStep[DescentRingSize];
        ring[_descentRingHead] = new DescentStep(op, parentChunkId, parentVersion, childIndex, childChunkId);
        _descentRingHead = (_descentRingHead + 1) % DescentRingSize;
    }

    private static void DescentTraceOnInvalidChunkId(int badChunkId, string segmentMessage)
    {
        // Cap dumps so we don't drown the log under a sustained crash storm.
        if (Interlocked.Increment(ref _descentDumpsCaptured) > 5)
        {
            return;
        }
        var ring = _descentRing;
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine($"=== OLC DESCENT TRACE on invalid chunkId={badChunkId} (thread {Environment.CurrentManagedThreadId}) ===");
        sb.AppendLine(segmentMessage);
        if (ring == null)
        {
            sb.AppendLine("(no descent steps recorded on this thread — bug originated outside instrumented descent)");
        }
        else
        {
            sb.AppendLine($"Last {DescentRingSize} descent steps (oldest first; head idx={_descentRingHead}):");
            for (int i = 0; i < DescentRingSize; i++)
            {
                int idx = (_descentRingHead + i) % DescentRingSize;
                var s = ring[idx];
                if (s.ParentChunkId == 0 && s.ChildChunkId == 0)
                {
                    continue;  // unwritten slot
                }
                var opName = s.Op switch { OlcDescentTrace.OpInsert => "INS", OlcDescentTrace.OpRemove => "REM", OlcDescentTrace.OpDescend => "DSC", _ => "?" };
                sb.AppendLine($"  [{idx,2}] {opName} parentChunk={s.ParentChunkId} parentVer=0x{s.ParentVersion:x} childIdx={s.ChildIndex} childChunk={s.ChildChunkId}");
            }
        }
        WriteDescentDump(sb.ToString());
    }

    private static readonly string _descentDumpPath = (Environment.GetEnvironmentVariable("OLC_STRESS_LOG")
        ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "olc-race-stress.log"))
        .Replace(".log", ".descent.log");

    private static void WriteDescentDump(string text)
    {
        lock (_progressLogLock)
        {
            try
            {
                System.IO.File.AppendAllText(_descentDumpPath, text);
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// One line of a tree's diagnostic counters, safe to call from a thread other than the one driving the tree.
    /// </summary>
    /// <remarks>
    /// Every property here reads through <c>Interlocked.Read</c> and touches no ChunkAccessor, which is what makes it legal to call while the iteration being
    /// described is still running and still owns its accessors.
    /// </remarks>
    private static long[] DescribeCounters(IntSingleBTree<PersistentStore> tree)
        => [tree.OptimisticRestarts, tree.PessimisticFallbacks, tree.WriteLockFailures, tree.SplitCount, tree.MergeCount, tree.EntryCount];

    private static readonly string[] CounterNames = ["Restarts", "Fallbacks", "WriteLockFails", "Splits", "Merges", "Entries"];

    // Index into the arrays above. Which counter moved is the whole diagnosis, so these are named rather than spelled as literals at the comparison site.
    private const int CtrRestarts = 0;
    private const int CtrFallbacks = 1;
    private const int CtrWriteLockFails = 2;
    private const int CtrEntries = 5;

    // ====== Scenario bodies (mirror OlcBTreeTests bodies, allocate fresh segment per iter) ======

    private static unsafe void AddSplitsBody(ScenarioContext ctx)
    {
        var mpmmf = ctx.Mpmmf;
        var em = ctx.EpochManager;
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 100, sizeof(Index32Chunk));

        const int threadCount = 4;
        const int keysPerThread = 500;
        var setupDepth = em.EnterScope();
        try
        {
            var setupA = segment.CreateChunkAccessor();
            var tree = new IntSingleBTree<PersistentStore>(segment);
            ctx.CounterSnapshot = () => DescribeCounters(tree);
            setupA.Dispose();

            using var barrier = new Barrier(threadCount);
            var exceptions = new ConcurrentBag<Exception>();
            var tasks = new Task[threadCount];
            for (int t = 0; t < threadCount; t++)
            {
                int tid = t;
                tasks[t] = Task.Factory.StartNew(() =>
                {
                    var d = em.EnterScope();
                    try
                    {
                        var wa = segment.CreateChunkAccessor();
                        barrier.SignalAndWait();
                        for (int i = 0; i < keysPerThread; i++)
                        {
                            int key = i * threadCount + tid + 1;
                            tree.Add(key, key * 10, ref wa);
                        }
                        wa.CommitChanges();
                        wa.Dispose();
                    }
                    catch (Exception ex) { exceptions.Add(ex); }
                    finally { em.ExitScope(d); }
                }, TaskCreationOptions.LongRunning);
            }
            Task.WaitAll(tasks);
            ThrowIfAny(exceptions, "Add_Splits workers threw");

            int expected = threadCount * keysPerThread;
            if (tree.EntryCount != expected)
            {
                throw new Exception($"Add_Splits: EntryCount={tree.EntryCount} expected={expected}");
            }
            var va = segment.CreateChunkAccessor();
            for (int i = 1; i <= expected; i++)
            {
                var r = tree.TryGet(i, ref va);
                if (!r.IsSuccess || r.Value != i * 10)
                {
                    var where = DescribeLostKey(tree, i, ref va);
                    va.Dispose();
                    throw new Exception($"Add_Splits: key {i} success={r.IsSuccess} value={r.Value} expected={i * 10} | {where}");
                }
            }
            va.Dispose();
        }
        finally { em.ExitScope(setupDepth); }
    }

    /// <summary>
    /// Answers the one question that splits #297/#679's candidate causes in half: is a key that <c>TryGet</c> cannot find still PRESENT in the leaf-level
    /// linked list?
    /// </summary>
    /// <remarks>
    /// <c>TryGet</c> reaches a leaf by descending through separators; <c>EnumerateLeaves</c> reaches it by walking the sibling chain. The two disagreeing is not
    /// a detail, it is the diagnosis:
    /// <list type="bullet">
    /// <item>present in the chain, absent by descent — the key landed in a real leaf and the PARENT cannot route to it. A separator or child pointer is wrong;
    /// the insert itself was fine.</item>
    /// <item>absent from both — the insert never took effect anywhere reachable, even though <c>EntryCount</c> was incremented for it. A write into a node that
    /// is in neither structure.</item>
    /// </list>
    /// Reported at the point of failure because the tree is torn down per iteration: without this the message carries only "not found", which is compatible
    /// with every hypothesis in #297 and therefore separates none of them.
    /// </remarks>
    private static string DescribeLostKey(IntSingleBTree<PersistentStore> tree, int key, ref ChunkAccessor<PersistentStore> accessor)
    {
        int chainCount = 0;
        bool inChain = false;
        int predecessor = int.MinValue;
        int successor = int.MaxValue;
        bool chainOrdered = true;
        int previousKey = int.MinValue;
        try
        {
            foreach (var kv in tree.EnumerateLeaves())
            {
                chainCount++;
                if (kv.Key == key)
                {
                    inChain = true;
                }
                else if (kv.Key < key && kv.Key > predecessor)
                {
                    predecessor = kv.Key;
                }
                else if (kv.Key > key && kv.Key < successor)
                {
                    successor = kv.Key;
                }
                if (chainCount > 1 && kv.Key <= previousKey)
                {
                    chainOrdered = false;
                }
                previousKey = kv.Key;
            }
        }
        catch (Exception ex)
        {
            return $"leaf-chain walk threw {ex.GetType().Name}: {ex.Message}";
        }

        string consistency;
        try
        {
            tree.CheckConsistency(ref accessor);
            consistency = "ok";
        }
        catch (Exception ex)
        {
            consistency = ex.Message;
        }

        // No separate separator/HighKey/depth dumps here any more: CheckConsistency asserts all three itself (#679), so `consistency` already carries the
        // detail and carries it in priority order instead of printing four reports of which three are usually "all agree".
        return $"inLeafChain={inChain} chainCount={chainCount} chainOrdered={chainOrdered} entryCount={tree.EntryCount} height={tree.Height} "
             + $"emptyInitRacesLost={tree.EmptyInitRacesLost} "
             + $"chainNeighbours=({(predecessor == int.MinValue ? "-" : predecessor.ToString())},"
             + $"{(successor == int.MaxValue ? "-" : successor.ToString())}) consistency=[{consistency}]";
    }

    private static unsafe void AddDisjointBody(ScenarioContext ctx)
    {
        var mpmmf = ctx.Mpmmf;
        var em = ctx.EpochManager;
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 50, sizeof(Index32Chunk));

        const int threadCount = 4;
        const int keysPerThread = 200;
        var setupDepth = em.EnterScope();
        try
        {
            var setupA = segment.CreateChunkAccessor();
            var tree = new IntSingleBTree<PersistentStore>(segment);
            ctx.CounterSnapshot = () => DescribeCounters(tree);
            setupA.Dispose();

            using var barrier = new Barrier(threadCount);
            var exceptions = new ConcurrentBag<Exception>();
            var tasks = new Task[threadCount];
            for (int t = 0; t < threadCount; t++)
            {
                int tid = t;
                tasks[t] = Task.Factory.StartNew(() =>
                {
                    var d = em.EnterScope();
                    try
                    {
                        var wa = segment.CreateChunkAccessor();
                        barrier.SignalAndWait();
                        int start = tid * keysPerThread + 1;
                        for (int i = start; i < start + keysPerThread; i++)
                        {
                            tree.Add(i, i * 10, ref wa);
                        }
                        wa.CommitChanges();
                        wa.Dispose();
                    }
                    catch (Exception ex) { exceptions.Add(ex); }
                    finally { em.ExitScope(d); }
                }, TaskCreationOptions.LongRunning);
            }
            Task.WaitAll(tasks);
            ThrowIfAny(exceptions, "Add_Disjoint workers threw");

            int expected = threadCount * keysPerThread;
            if (tree.EntryCount != expected)
            {
                throw new Exception($"Add_Disjoint: EntryCount={tree.EntryCount} expected={expected}");
            }
            var va = segment.CreateChunkAccessor();
            for (int i = 1; i <= expected; i++)
            {
                var r = tree.TryGet(i, ref va);
                if (!r.IsSuccess || r.Value != i * 10)
                {
                    var where = DescribeLostKey(tree, i, ref va);
                    va.Dispose();
                    throw new Exception($"Add_Disjoint: key {i} success={r.IsSuccess} value={r.Value} expected={i * 10} | {where}");
                }
            }
            va.Dispose();
        }
        finally { em.ExitScope(setupDepth); }
    }

    // Issue #297 follow-up: distinguish "Remove returned false" from "Remove returned true with wrong value"
    private static int _rdNotRemoved;
    private static int _rdWrongValue;
    private static readonly System.Collections.Concurrent.ConcurrentBag<string> _rdSamples = new();

    private static unsafe void RemoveDisjointBody(ScenarioContext ctx)
    {
        var mpmmf = ctx.Mpmmf;
        var em = ctx.EpochManager;
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 50, sizeof(Index32Chunk));

        const int threadCount = 4;
        const int keysPerThread = 100;
        const int totalKeys = threadCount * keysPerThread;
        var setupDepth = em.EnterScope();
        try
        {
            var sa = segment.CreateChunkAccessor();
            var tree = new IntSingleBTree<PersistentStore>(segment);
            ctx.CounterSnapshot = () => DescribeCounters(tree);
            for (int i = 1; i <= totalKeys; i++)
            {
                tree.Add(i, i * 10, ref sa);
            }
            sa.Dispose();

            using var barrier = new Barrier(threadCount);
            var exceptions = new ConcurrentBag<Exception>();
            int errors = 0;
            var tasks = new Task[threadCount];
            for (int t = 0; t < threadCount; t++)
            {
                int tid = t;
                tasks[t] = Task.Factory.StartNew(() =>
                {
                    var d = em.EnterScope();
                    try
                    {
                        var wa = segment.CreateChunkAccessor();
                        barrier.SignalAndWait();
                        int start = tid * keysPerThread + 1;
                        for (int i = start; i < start + keysPerThread; i++)
                        {
                            bool removed = tree.Remove(i, out var v, ref wa);
                            if (!removed)
                            {
                                Interlocked.Increment(ref errors);
                                Interlocked.Increment(ref _rdNotRemoved);
                                if (_rdSamples.Count < 30) { _rdSamples.Add($"NOT_REMOVED key={i}"); }
                            }
                            else if (v != i * 10)
                            {
                                Interlocked.Increment(ref errors);
                                Interlocked.Increment(ref _rdWrongValue);
                                if (_rdSamples.Count < 30) { _rdSamples.Add($"WRONG_VALUE key={i} got={v} expected={i * 10}"); }
                            }
                        }
                        wa.CommitChanges();
                        wa.Dispose();
                    }
                    catch (Exception ex) { exceptions.Add(ex); }
                    finally { em.ExitScope(d); }
                }, TaskCreationOptions.LongRunning);
            }
            Task.WaitAll(tasks);
            ThrowIfAny(exceptions, "Remove_Disjoint workers threw");
            if (errors != 0)
            {
                throw new Exception($"Remove_Disjoint: errors={errors}");
            }
            if (tree.EntryCount != 0)
            {
                throw new Exception($"Remove_Disjoint: EntryCount={tree.EntryCount} expected=0");
            }
        }
        finally { em.ExitScope(setupDepth); }
    }

    private static unsafe void RemoveMergesBody(ScenarioContext ctx)
    {
        var mpmmf = ctx.Mpmmf;
        var em = ctx.EpochManager;
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 100, sizeof(Index32Chunk));

        const int threadCount = 4;
        const int totalKeys = 2000;
        const int keysToRemovePerThread = 200;
        var setupDepth = em.EnterScope();
        try
        {
            var sa = segment.CreateChunkAccessor();
            var tree = new IntSingleBTree<PersistentStore>(segment);
            ctx.CounterSnapshot = () => DescribeCounters(tree);
            for (int i = 1; i <= totalKeys; i++)
            {
                tree.Add(i, i * 10, ref sa);
            }
            sa.Dispose();

            using var barrier = new Barrier(threadCount);
            var exceptions = new ConcurrentBag<Exception>();
            var tasks = new Task[threadCount];
            // Keys are disjoint per thread, so no remove can lose a race with another remove for the same key. That makes the two candidate explanations for a
            // wrong EntryCount cleanly separable, and the return value — discarded until now — is half the evidence.
            var removeReturnedFalse = new int[1];
            for (int t = 0; t < threadCount; t++)
            {
                int tid = t;
                tasks[t] = Task.Factory.StartNew(() =>
                {
                    var d = em.EnterScope();
                    try
                    {
                        var wa = segment.CreateChunkAccessor();
                        barrier.SignalAndWait();
                        for (int i = 0; i < keysToRemovePerThread; i++)
                        {
                            int key = i * threadCount + tid + 1;
                            if (key <= totalKeys)
                            {
                                if (!tree.Remove(key, out _, ref wa))
                                {
                                    Interlocked.Increment(ref removeReturnedFalse[0]);
                                }
                            }
                        }
                        wa.CommitChanges();
                        wa.Dispose();
                    }
                    catch (Exception ex) { exceptions.Add(ex); }
                    finally { em.ExitScope(d); }
                }, TaskCreationOptions.LongRunning);
            }
            Task.WaitAll(tasks);
            ThrowIfAny(exceptions, "Remove_Merges workers threw");

            int expected = totalKeys - threadCount * keysToRemovePerThread;
            if (tree.EntryCount != expected)
            {
                // chainCount is the ground truth (what the tree actually holds); EntryCount is the counter. Which of the two is wrong names the defect:
                // chain == expected means a DecCount was lost, chain == EntryCount means a key really survived its Remove.
                int chainCount = 0;
                var leftover = new System.Collections.Generic.List<int>();
                foreach (var kv in tree.EnumerateLeaves())
                {
                    chainCount++;
                    // The removed set is exactly 1..threadCount*keysToRemovePerThread (i*threadCount + tid + 1 covers it densely), so anything at or below
                    // that bound still in the chain is a key whose Remove did not take.
                    if (kv.Key <= threadCount * keysToRemovePerThread && leftover.Count < 8)
                    {
                        leftover.Add(kv.Key);
                    }
                }
                throw new Exception($"Remove_Merges: EntryCount={tree.EntryCount} expected={expected} chainCount={chainCount} "
                                  + $"removeReturnedFalse={removeReturnedFalse[0]} survivingRemovedKeys=[{string.Join(",", leftover)}]");
            }
            var va = segment.CreateChunkAccessor();
            tree.CheckConsistency(ref va);
            va.Dispose();
        }
        finally { em.ExitScope(setupDepth); }
    }

    private static unsafe void RemoveMixedBody(ScenarioContext ctx)
    {
        var mpmmf = ctx.Mpmmf;
        var em = ctx.EpochManager;
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 100, sizeof(Index32Chunk));

        const int initialEntries = 500;
        const int writerCount = 2;
        const int removerCount = 2;
        const int insertsPerWriter = 300;
        const int removesPerRemover = 100;
        var setupDepth = em.EnterScope();
        try
        {
            var sa = segment.CreateChunkAccessor();
            var tree = new IntSingleBTree<PersistentStore>(segment);
            ctx.CounterSnapshot = () => DescribeCounters(tree);
            for (int i = 1; i <= initialEntries; i++)
            {
                tree.Add(i, i * 10, ref sa);
            }
            sa.Dispose();

            using var startSignal = new ManualResetEventSlim(false);
            var exceptions = new ConcurrentBag<Exception>();

            var insertTasks = new Task[writerCount];
            for (int w = 0; w < writerCount; w++)
            {
                int wid = w;
                insertTasks[w] = Task.Factory.StartNew(() =>
                {
                    var d = em.EnterScope();
                    try
                    {
                        var wa = segment.CreateChunkAccessor();
                        startSignal.Wait();
                        int start = 10_000 + wid * insertsPerWriter;
                        for (int i = start; i < start + insertsPerWriter; i++)
                        {
                            tree.Add(i, i * 10, ref wa);
                        }
                        wa.CommitChanges();
                        wa.Dispose();
                    }
                    catch (Exception ex) { exceptions.Add(ex); }
                    finally { em.ExitScope(d); }
                }, TaskCreationOptions.LongRunning);
            }
            var removeTasks = new Task[removerCount];
            for (int r = 0; r < removerCount; r++)
            {
                int rid = r;
                removeTasks[r] = Task.Factory.StartNew(() =>
                {
                    var d = em.EnterScope();
                    try
                    {
                        var wa = segment.CreateChunkAccessor();
                        startSignal.Wait();
                        int start = rid * removesPerRemover + 1;
                        for (int i = start; i < start + removesPerRemover; i++)
                        {
                            tree.Remove(i, out _, ref wa);
                        }
                        wa.CommitChanges();
                        wa.Dispose();
                    }
                    catch (Exception ex) { exceptions.Add(ex); }
                    finally { em.ExitScope(d); }
                }, TaskCreationOptions.LongRunning);
            }
            startSignal.Set();
            Task.WaitAll(insertTasks.Concat(removeTasks).ToArray());
            ThrowIfAny(exceptions, "Remove_Mixed workers threw");

            int expected = initialEntries + writerCount * insertsPerWriter - removerCount * removesPerRemover;
            if (tree.EntryCount != expected)
            {
                throw new Exception($"Remove_Mixed: EntryCount={tree.EntryCount} expected={expected}");
            }
            var va = segment.CreateChunkAccessor();
            tree.CheckConsistency(ref va);
            va.Dispose();
        }
        finally { em.ExitScope(setupDepth); }
    }

    // ====== Plumbing ======

    private sealed class ScenarioContext
    {
        public ManagedPagedMMF Mpmmf;
        public EpochManager EpochManager;

        /// <summary>
        /// Published by the scenario body once it owns a tree, so the deadline handler can sample the tree's counters from OUTSIDE the stuck iteration.
        /// </summary>
        /// <remarks>
        /// This is what turns "DEADLINE" from a label into a diagnosis. A wall clock expiring cannot tell a livelock from a slow loop, and reading the old
        /// "HANG" as evidence of a lock cycle is what sent five #738 hypotheses to be refuted one at a time. Sampling the counters twice does distinguish them:
        /// restarts still climbing means the iteration is making attempts and is merely slow, restarts frozen means it is not attempting anything.
        /// <para>
        /// Read from a different thread than the one that writes it, and only over <c>Interlocked.Read</c> counters — it must never touch a ChunkAccessor,
        /// because the iteration it is describing still owns one.
        /// </para>
        /// </remarks>
        public volatile Func<long[]> CounterSnapshot;
    }

    private sealed class Scenario
    {
        public readonly string Name;
        public readonly Action<ScenarioContext> Body;
        public int Iterations;
        public int DetailsCaptured;
        public int Deadlines;
        public readonly ConcurrentBag<FailureRecord> Failures = new();

        public Scenario(string name, Action<ScenarioContext> body)
        {
            Name = name;
            Body = body;
        }
    }

    private readonly record struct FailureRecord(int Iteration, string Detail);

    private static void RunScenarioLoop(Scenario s, ManualResetEventSlim stop)
    {
        // Fresh service provider + MMF per iteration — the MMF tracks BTree indexes
        // in a per-segment directory capped at 20 entries, so reusing the MMF runs out fast.
        // Per-iter rebuild is also closer to what the regular test fixtures do (matches CI).
        while (!stop.IsSet)
        {
            int iter = Interlocked.Increment(ref s.Iterations);
            var sp = BuildScenarioProvider();
            bool hadHangInThisIter = false;
            try
            {
                var mpmmf = sp.GetRequiredService<ManagedPagedMMF>();
                var em = sp.GetRequiredService<EpochManager>();
                var ctx = new ScenarioContext { Mpmmf = mpmmf, EpochManager = em };
                try
                {
                    // File-based progress: written BEFORE iteration. If process crashes mid-iter,
                    // we know which scenario+iteration was running. Crucial because OLC bugs may
                    // produce AccessViolation that bypasses managed try/catch.
                    WriteProgress(s.Name, iter, "start");
                    try
                    {
                        // Run on a worker thread so we can put a wall-clock deadline on it.
                        // If the iteration deadlocks (livelock in OLC retry loops, etc.), we
                        // record a "hang" outcome and break — otherwise the whole harness wedges.
                        var iterTask = Task.Factory.StartNew(() => s.Body(ctx), TaskCreationOptions.LongRunning);
                        if (!iterTask.Wait(IterationDeadline))
                        {
                            // A wall clock expiring reports that one iteration took too long while OLC_STRESS_NOISE CPU-saturating threads run alongside five
                            // scenario loops. On its own that says NOTHING about the tree: a bounded-but-glacial retry loop (the pessimistic paths allow
                            // MaxPessimisticRestarts = 10,000, measured at 2m34s single-threaded in #740) trips it exactly as a genuine deadlock would.
                            // Reading the old "HANG" label as evidence of a lock cycle is what sent five #738 hypotheses to be refuted one at a time.
                            // So do not report the label — report the measurement that separates the two. See ScenarioContext.CounterSnapshot.
                            s.Failures.Add(new FailureRecord(iter, DiagnoseDeadline(s, iter, ctx)));
                            // Workers may still be alive — touching the MMF after Dispose() would AV. Skip cleanup and let the orphan workers churn against
                            // live (epoch-protected) memory until the process exits.
                            hadHangInThisIter = true;

                            // Do NOT break. The old code retired the scenario for the rest of the run on its first deadline, so a 30-second harness that hit
                            // one slow iteration at second 2 spent the remaining 28 seconds not testing that scenario — and reported a per-scenario failure
                            // RATE computed over the handful of iterations it managed before quitting. The leak is what the break was really protecting, so
                            // bound the leak instead: a fresh provider is built per iteration, so tolerating a few costs a few MMFs, not unbounded growth.
                            if (Interlocked.Increment(ref s.Deadlines) >= MaxDeadlinesPerScenario)
                            {
                                s.Failures.Add(new FailureRecord(iter, $"RETIRED: {MaxDeadlinesPerScenario} deadlines in this scenario; stopping its loop to "
                                                                      + "bound the leaked MMFs. Everything after this point is untested for this scenario."));
                                WriteProgress(s.Name, iter, "RETIRED");
                                break;
                            }
                            continue;
                        }
                        if (iterTask.IsFaulted)
                        {
                            throw iterTask.Exception?.InnerException ?? iterTask.Exception ?? new Exception("Unknown fault");
                        }
                        WriteProgress(s.Name, iter, "ok");
                    }
                    catch (Exception ex)
                    {
                        // Unwrap the AggregateException Wait() introduced.
                        var inner = ex is AggregateException agg ? (agg.InnerException ?? ex) : ex;
                        s.Failures.Add(new FailureRecord(iter, inner.ToString()));
                        // First line of message is enough to spot patterns in the live log.
                        var msg = inner.Message?.Split('\n')[0] ?? "";
                        WriteProgress(s.Name, iter, $"fail: {inner.GetType().Name}: {msg}");
                        // Capture full detail for the first 3 failures per scenario — survives test-abort.
                        if (Interlocked.Increment(ref s.DetailsCaptured) <= 3)
                        {
                            WriteDetails(s.Name, iter, inner);
                        }
                    }
                }
                finally
                {
                    if (!hadHangInThisIter)
                    {
                        em.Dispose();
                        mpmmf.Dispose();
                    }
                    // On HANG: skip Dispose() — orphan workers continue to run safely against the live MMF.
                }
            }
            finally
            {
                if (!hadHangInThisIter)
                {
                    (sp as IDisposable)?.Dispose();
                }
                // On HANG: skip SP.Dispose() too — it would tear down the scoped MMF and AV the orphan workers.
                // Acceptable leak (bounded by stress duration).
            }
        }
    }

    private static readonly TimeSpan IterationDeadline = TimeSpan.FromSeconds(ParseEnvInt("OLC_STRESS_ITER_DEADLINE_SECONDS", 10));

    // A deadlined iteration leaks its MMF by design (its workers may still be running), so the count of them has to be bounded somewhere. It used to be
    // bounded at one, by quitting the scenario — which bought a small leak at the price of not testing that scenario again for the rest of the run.
    private const int MaxDeadlinesPerScenario = 3;

    // How long to wait between the two counter samples. Long enough that a working-but-slow iteration demonstrably moves a counter, short enough not to eat the
    // harness budget. A Thread.Sleep is the measurement here, not a synchronisation shortcut: there is no event to wait on, the question IS "what changed over
    // an interval".
    private static readonly TimeSpan DeadlineSampleWindow = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Samples the stuck iteration's tree counters twice and reports whether it is progressing, so the record says what happened instead of that a clock expired.
    /// </summary>
    private static string DiagnoseDeadline(Scenario s, int iter, ScenarioContext ctx)
    {
        var snapshot = ctx.CounterSnapshot;
        if (snapshot == null)
        {
            WriteProgress(s.Name, iter, "DEADLINE (no tree published — stuck before setup)");
            return $"DEADLINE after {IterationDeadline.TotalSeconds}s with no tree published: the body did not get as far as constructing its tree, so this is "
                 + "segment allocation or epoch setup, not the B+Tree.";
        }

        long[] first, second;
        try
        {
            first = snapshot();
            Thread.Sleep(DeadlineSampleWindow);
            second = snapshot();
        }
        catch (Exception ex)
        {
            return $"DEADLINE after {IterationDeadline.TotalSeconds}s; counter sampling threw {ex.GetType().Name}: {ex.Message}";
        }

        var deltas = new long[first.Length];
        bool anyMoved = false;
        for (int i = 0; i < first.Length; i++)
        {
            deltas[i] = second[i] - first[i];
            anyMoved |= deltas[i] != 0;
        }

        // WHICH counter moved is the diagnosis. These three are materially different defects and the old single "HANG" label collapsed them into one, which is
        // how #738 accumulated five hypotheses that all assumed a lock cycle.
        string label, verdict;
        if (!anyMoved)
        {
            label = "STUCK";
            verdict = "not one counter moved in the sample window. Nothing is being attempted — this is the shape a lock cycle or a wait-forever has, and the "
                    + "only thing that will tell you more is a stack dump (`--blame-hang --blame-hang-timeout <n>`).";
        }
        else if (deltas[CtrWriteLockFails] > 0 && deltas[CtrRestarts] == 0 && deltas[CtrFallbacks] == 0 && deltas[CtrEntries] == 0)
        {
            label = "SPINNING";
            verdict = $"only WriteLockFailures moved, by {deltas[CtrWriteLockFails]:N0} in {DeadlineSampleWindow.TotalSeconds}s. The operation is not restarting "
                    + "and not completing: it is inside a write-lock spin that never acquires. That is lock-acquisition livelock, NOT a restart storm — the "
                    + "restart-bound story does not apply and MaxPessimisticRestarts will never fire here.";
        }
        else if (deltas[CtrRestarts] > 0 || deltas[CtrFallbacks] > 0)
        {
            label = "RESTARTING";
            verdict = $"restarts moved by {deltas[CtrRestarts]:N0} and fallbacks by {deltas[CtrFallbacks]:N0}. The operation keeps re-attempting and losing — a "
                    + "restart storm. Find what invalidates the version between read and lock; a guard stronger than the invariant it protects does exactly this "
                    + "(#740).";
        }
        else
        {
            label = "SLOW";
            verdict = "the tree is still changing, so the iteration is progressing and merely exceeded a wall clock set while CPU-saturating noise threads run. "
                    + "Suspect the budget before the tree.";
        }

        var sb = new StringBuilder();
        sb.Append($"DEADLINE after {IterationDeadline.TotalSeconds}s — {label}: {verdict}\n  deltas over {DeadlineSampleWindow.TotalSeconds}s:");
        for (int i = 0; i < deltas.Length; i++)
        {
            sb.Append($" {CounterNames[i]}={first[i]:N0}{(deltas[i] == 0 ? "" : $"(+{deltas[i]:N0})")}");
        }

        WriteProgress(s.Name, iter, $"DEADLINE/{label}");
        return sb.ToString();
    }

    private static readonly string _progressLogPath = Environment.GetEnvironmentVariable("OLC_STRESS_LOG")
        ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "olc-race-stress.log");

    private static readonly object _progressLogLock = new();

    private static void WriteProgress(string scenario, int iter, string state)
    {
        lock (_progressLogLock)
        {
            try
            {
                System.IO.File.AppendAllText(_progressLogPath, $"{DateTime.UtcNow:HH:mm:ss.fff} {scenario,-18} iter={iter,6} {state}\n");
            }
            catch { /* best-effort */ }
        }
    }

    private static readonly string _detailsLogPath = (Environment.GetEnvironmentVariable("OLC_STRESS_LOG")
        ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "olc-race-stress.log"))
        .Replace(".log", ".details.log");

    private static void WriteDetails(string scenario, int iter, Exception ex)
    {
        lock (_progressLogLock)
        {
            try
            {
                System.IO.File.AppendAllText(_detailsLogPath,
                    $"\n=== {DateTime.UtcNow:HH:mm:ss.fff} {scenario} iter={iter} ===\n{ex}\n");
            }
            catch { /* best-effort */ }
        }
    }

    private static IServiceProvider BuildScenarioProvider()
    {
        // Unique DB name per scenario invocation so concurrent loops don't collide on the file — and unique per PROCESS, which it was not (#738). `_scenarioId`
        // restarts at 0 in every test host, so run N+1 walked the exact same name sequence as run N. That only matters because the HANG path in
        // RunScenarioLoop deliberately skips cleanup and leaks the MMF with its file open, so a single deadline expiry left a locked `olcrs_<id>` behind that
        // every subsequent run then tripped over at the same iteration — `EnsureFileDeleted` cannot remove a file another process still holds, and `LoadFile`
        // throws IOException. Measured: three consecutive isolated runs failed identically at `olcrs_2397`, and 33,088 leaked database directories had
        // accumulated under the test bin. Including the process id makes the leak self-contained instead of contagious.
        int id = Interlocked.Increment(ref _scenarioId);
        var sc = new ServiceCollection()
            .AddLogging()
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddScopedManagedPagedMemoryMappedFile(o =>
            {
                o.DatabaseName = $"olcrs_{Environment.ProcessId:x}_{id:x}";
                // Generously sized — many segments per scenario over a long run.
                o.DatabaseCacheSize = (ulong)(PagedMMF.MinimumMemPageCount * PagedMMF.PageSize);
                o.PagesDebugPattern = true;
            });
        var sp = sc.BuildServiceProvider();
        sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
        return sp;
    }

    private static Task[] StartNoise(int count, ManualResetEventSlim stop)
    {
        var noise = new Task[count];
        for (int i = 0; i < count; i++)
        {
            noise[i] = Task.Factory.StartNew(() =>
            {
                // CPU-bound spinner — keeps cores hot so OLC workers face frequent preemption.
                ulong acc = 1;
                while (!stop.IsSet)
                {
                    for (int k = 0; k < 4096; k++)
                    {
                        acc = acc * 6364136223846793005UL + 1442695040888963407UL;
                    }
                }
                GC.KeepAlive(acc);
            }, TaskCreationOptions.LongRunning);
        }
        return noise;
    }

    private static int ParseEnvInt(string name, int fallback)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return int.TryParse(v, out var n) ? n : fallback;
    }

    private static void ThrowIfAny(ConcurrentBag<Exception> exceptions, string label)
    {
        if (exceptions.IsEmpty)
        {
            return;
        }
        var first = exceptions.First();
        throw new Exception($"{label} ({exceptions.Count} total); first:\n{first}");
    }
}
