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
/// [Explicit] — never runs in CI; invoke via filter or env-tuned local runs.
/// Configure via env vars:
///   OLC_STRESS_SECONDS — wall duration of the run (default 30)
///   OLC_STRESS_NOISE   — count of CPU-saturating noise threads (default = ProcessorCount/2)
/// One ManagedPagedMMF per scenario, reused across iterations (fresh segment per iter) — avoids per-iter file I/O cost.
/// </summary>
[TestFixture]
[NonParallelizable]
[Explicit("Long-running race-stress harness for issue #297 — invoke manually")]
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
    private static readonly long[] _removeNotFoundByBranch = new long[5];
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
                    va.Dispose();
                    throw new Exception($"Add_Splits: key {i} success={r.IsSuccess} value={r.Value} expected={i * 10}");
                }
            }
            va.Dispose();
        }
        finally { em.ExitScope(setupDepth); }
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
                    va.Dispose();
                    throw new Exception($"Add_Disjoint: key {i} success={r.IsSuccess} value={r.Value} expected={i * 10}");
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
            for (int i = 1; i <= totalKeys; i++)
            {
                tree.Add(i, i * 10, ref sa);
            }
            sa.Dispose();

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
                        for (int i = 0; i < keysToRemovePerThread; i++)
                        {
                            int key = i * threadCount + tid + 1;
                            if (key <= totalKeys)
                            {
                                tree.Remove(key, out _, ref wa);
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
                throw new Exception($"Remove_Merges: EntryCount={tree.EntryCount} expected={expected}");
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
    }

    private sealed class Scenario
    {
        public readonly string Name;
        public readonly Action<ScenarioContext> Body;
        public int Iterations;
        public int DetailsCaptured;
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
                            s.Failures.Add(new FailureRecord(iter, $"HANG: scenario body did not complete within {IterationDeadline.TotalSeconds}s"));
                            WriteProgress(s.Name, iter, "HANG");
                            // Workers may still be alive — touching the MMF after Dispose() would AV.
                            // Skip cleanup; let the orphan workers churn against live (epoch-protected) memory
                            // until the process exits. Memory leak across HANG iters is bounded by the stress
                            // duration and acceptable for a diagnostic harness. See issue #297 follow-up.
                            hadHangInThisIter = true;
                            break;
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
        // Unique DB name per scenario invocation so concurrent loops don't collide on the file.
        int id = Interlocked.Increment(ref _scenarioId);
        var sc = new ServiceCollection()
            .AddLogging()
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddScopedManagedPagedMemoryMappedFile(o =>
            {
                o.DatabaseName = $"olcrs_{id:x}";
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
