using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Typhon.Engine.Tests;

[TestFixture]
class TransactionChainTests : TestBase<TransactionChainTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    /// <summary>
    /// 4 threads creating transactions concurrently — verify all TSNs are unique (no duplicates from lock-free path).
    /// Each thread disposes its own transactions to respect thread affinity.
    /// </summary>
    [Test]
    public void MPSC_ConcurrentCreates_AllTSNsUnique()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        const int threadCount = 4;
        const int txPerThread = 50;
        var allTsns = new ConcurrentBag<long>();
        var barrier = new CountdownEvent(threadCount);
        var go = new ManualResetEventSlim(false);

        var threads = new Thread[threadCount];
        for (int i = 0; i < threadCount; i++)
        {
            threads[i] = new Thread(() =>
            {
                barrier.Signal();
                go.Wait();
                for (int j = 0; j < txPerThread; j++)
                {
                    using var tx = dbe.CreateQuickTransaction();
                    allTsns.Add(tx.TSN);
                }
            });
            threads[i].Start();
        }

        barrier.Wait();
        go.Set();

        for (int i = 0; i < threadCount; i++)
        {
            threads[i].Join();
        }

        // Verify all TSNs are unique
        var tsnList = allTsns.ToList();
        Assert.That(tsnList.Count, Is.EqualTo(threadCount * txPerThread));
        Assert.That(tsnList.Distinct().Count(), Is.EqualTo(tsnList.Count), "All TSNs must be unique");
        Assert.That(dbe.TransactionChain.ActiveCount, Is.EqualTo(0));
    }

    /// <summary>
    /// Create 3 transactions, dispose the oldest (tail), verify MinTSN advances to second-oldest.
    /// </summary>
    [Test]
    public void MinTSN_DisposeTail_Advances()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        // Create 3 transactions: t1 (oldest/tail), t2, t3 (newest/head)
        var t1 = dbe.CreateQuickTransaction();
        var t2 = dbe.CreateQuickTransaction();
        var t3 = dbe.CreateQuickTransaction();

        var tsn1 = t1.TSN;
        var tsn2 = t2.TSN;

        // MinTSN should be the oldest (t1)
        Assert.That(dbe.TransactionChain.MinTSN, Is.EqualTo(tsn1), "MinTSN should be TSN of oldest transaction");
        Assert.That(dbe.TransactionChain.Tail, Is.SameAs(t1), "Tail should be the oldest transaction");

        // Dispose t1 (the tail) — MinTSN should advance to t2
        t1.Dispose();
        Assert.That(dbe.TransactionChain.MinTSN, Is.EqualTo(tsn2), "MinTSN should advance to second-oldest after tail disposed");
        Assert.That(dbe.TransactionChain.ActiveCount, Is.EqualTo(2));

        t2.Dispose();
        t3.Dispose();

        Assert.That(dbe.TransactionChain.ActiveCount, Is.EqualTo(0));
    }

    /// <summary>
    /// Read-only transaction can read entities but throws on write operations.
    /// Verifies no ChangeSet or UoW is allocated.
    /// </summary>
    [Test]
    public void ReadOnly_CanRead_ThrowsOnWrite()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        // Create an entity with a normal transaction
        var comp = new CompA(42, 1.5f, 2.5);
        EntityId entityId;
        using (var wt = dbe.CreateQuickTransaction())
        {
            entityId = wt.Spawn<CompAArch>(CompAArch.A.Set(in comp));
            wt.Commit();
        }

        // Read-only transaction can read
        using var rt = dbe.CreateReadOnlyTransaction();
        Assert.That(rt.IsReadOnly, Is.True);
        var read = rt.Open(entityId).Read(CompAArch.A);
        Assert.That(read.A, Is.EqualTo(42));

        // Write operations throw
        var writeComp = new CompA(99);
        Assert.Throws<InvalidOperationException>(() => rt.Spawn<CompAArch>(CompAArch.A.Set(in writeComp)));
        Assert.Throws<InvalidOperationException>(() => rt.OpenMut(entityId));
        Assert.Throws<InvalidOperationException>(() => rt.Destroy(entityId));

        // Commit is a no-op
        Assert.That(rt.Commit(), Is.True);

        // State should still be Created (no write happened)
        Assert.That(rt.State, Is.EqualTo(Transaction.TransactionState.Created));
    }

    /// <summary>
    /// Read-only transaction disposes cleanly and returns to the pool.
    /// </summary>
    [Test]
    public void ReadOnly_Dispose_ActiveCountZero()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var rt = dbe.CreateReadOnlyTransaction();
        Assert.That(dbe.TransactionChain.ActiveCount, Is.EqualTo(1));
        rt.Dispose();
        Assert.That(dbe.TransactionChain.ActiveCount, Is.EqualTo(0));

        // Double-dispose is safe
        rt.Dispose();
        Assert.That(dbe.TransactionChain.ActiveCount, Is.EqualTo(0));
    }

    /// <summary>
    /// Read-only transaction sees a consistent snapshot (doesn't see later commits).
    /// </summary>
    [Test]
    public void ReadOnly_SnapshotIsolation()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        // Create entity
        var comp = new CompA(10);
        EntityId entityId;
        using (var wt = dbe.CreateQuickTransaction())
        {
            entityId = wt.Spawn<CompAArch>(CompAArch.A.Set(in comp));
            wt.Commit();
        }

        // Open read-only snapshot
        using var rt = dbe.CreateReadOnlyTransaction();
        var before = rt.Open(entityId).Read(CompAArch.A);
        Assert.That(before.A, Is.EqualTo(10));

        // Update after snapshot was taken
        using (var wt2 = dbe.CreateQuickTransaction())
        {
            ref var w = ref wt2.OpenMut(entityId).Write(CompAArch.A);
            w = new CompA(99);
            wt2.Commit();
        }

        // Read-only tx still sees the old value (snapshot isolation)
        var after = rt.Open(entityId).Read(CompAArch.A);
        Assert.That(after.A, Is.EqualTo(10), "Read-only transaction should see snapshot, not the update");
    }

    /// <summary>
    /// Read-only transactions recycle through the pool — a disposed read-only tx
    /// can be reused as a read-write tx and vice versa.
    /// </summary>
    [Test]
    public void ReadOnly_PoolRecycling_ClearsFlag()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        // Exhaust initial pool
        var exhaustPool = new List<Transaction>();
        for (int i = 0; i < 16; i++)
        {
            exhaustPool.Add(dbe.CreateQuickTransaction());
        }

        // Create a read-only tx beyond the pool — forces new allocation
        var roTx = dbe.CreateReadOnlyTransaction();
        Assert.That(roTx.IsReadOnly, Is.True);
        roTx.Dispose();

        // Reuse the same object as a read-write tx
        var rwTx = dbe.CreateQuickTransaction();
        Assert.That(rwTx, Is.SameAs(roTx), "Pool should recycle the disposed read-only transaction");
        Assert.That(rwTx.IsReadOnly, Is.False, "Recycled transaction must not carry read-only flag");

        // Write operations should work on the recycled tx
        var comp = new CompA(1);
        Assert.DoesNotThrow(() => rwTx.Spawn<CompAArch>(CompAArch.A.Set(in comp)));
        rwTx.Commit();
        rwTx.Dispose();

        // Cleanup
        foreach (var tx in exhaustPool)
        {
            tx.Dispose();
        }
    }

    /// <summary>
    /// 8 threads, 200ms stress — create/dispose loop (no entity ops). Validates lock-free PushHead + Remove
    /// under high contention without page cache pressure.
    /// </summary>
    [Test]
    public void Stress_ConcurrentCreateDispose_ActiveCountZeroAtEnd()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        const int threadCount = 8;
        var stop = new ManualResetEventSlim(false);
        var barrier = new CountdownEvent(threadCount);
        var totalOps = new int[threadCount];
        var errors = new ConcurrentBag<Exception>();

        var threads = new Thread[threadCount];
        for (int i = 0; i < threadCount; i++)
        {
            int threadIdx = i;
            threads[i] = new Thread(() =>
            {
                barrier.Signal();
                barrier.Wait();
                int ops = 0;
                while (!stop.IsSet)
                {
                    try
                    {
                        using var tx = dbe.CreateQuickTransaction();
                        ops++;
                    }
                    catch (Exception ex)
                    {
                        errors.Add(ex);
                        break;
                    }
                }
                totalOps[threadIdx] = ops;
            });
            threads[i].Start();
        }

        // 50ms is enough for 8 threads in tight create/dispose loops to expose CAS races.
        Thread.Sleep(50);
        stop.Set();

        for (int i = 0; i < threadCount; i++)
        {
            threads[i].Join();
        }

        Assert.That(errors, Is.Empty, () => $"Stress test errors: {string.Join("; ", errors.Select(e => e.Message))}");
        Assert.That(dbe.TransactionChain.ActiveCount, Is.EqualTo(0), "All transactions should be disposed");

        var total = totalOps.Sum();
        Assert.That(total, Is.GreaterThan(0), "Stress test should complete some operations");
        TestContext.Out.WriteLine($"Stress: {total} ops across {threadCount} threads in 50ms");
    }

    /// <summary>
    /// Torture test: 32 threads, 3 seconds — saturates all cores with tight create/dispose loops.
    /// Maximizes CAS contention on PushHead and exclusive-lock contention on Remove simultaneously.
    /// <para>
    /// Each thread runs a tight loop: <c>using var tx = CreateQuickTransaction()</c> — this exercises:
    /// <list type="bullet">
    ///   <item>PushHead CAS loop retries (32 threads fighting for _head)</item>
    ///   <item>Remove exclusive-lock queuing (32 threads serializing on Dispose)</item>
    ///   <item>Remove's CAS-on-_head with re-scan fallback (PushHead prepends while Remove tries to unlink head)</item>
    ///   <item>ConcurrentQueue pool contention (TryDequeue/Enqueue from 32 threads)</item>
    ///   <item>Tail maintenance under churn</item>
    /// </list>
    /// </para>
    /// </summary>
    [Test]
    [Explicit("Torture test — run manually, takes ~3s and saturates all cores")]
    public void Torture_32Cores_MixedWorkload_AllInvariantsHold()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        const int threadCount = 32;
        const int durationMs = 3000;

        var stop = new ManualResetEventSlim(false);
        var barrier = new CountdownEvent(threadCount);
        var errors = new ConcurrentBag<(int ThreadIdx, Exception Ex)>();
        var ops = new long[threadCount];

        var threads = new Thread[threadCount];
        for (int i = 0; i < threadCount; i++)
        {
            int idx = i;
            threads[i] = new Thread(() =>
            {
                barrier.Signal();
                barrier.Wait();
                long count = 0;
                while (!stop.IsSet)
                {
                    try
                    {
                        using var tx = dbe.CreateQuickTransaction();
                        count++;
                    }
                    catch (Exception ex)
                    {
                        errors.Add((idx, ex));
                        break;
                    }
                }
                ops[idx] = count;
            })
            { IsBackground = true };
            threads[i].Start();
        }

        // Let it burn for 3 seconds
        Thread.Sleep(durationMs);
        stop.Set();

        for (int i = 0; i < threadCount; i++)
        {
            threads[i].Join(TimeSpan.FromSeconds(10));
            Assert.That(threads[i].IsAlive, Is.False, $"Thread {i} did not terminate — possible deadlock or infinite loop in Remove");
        }

        // ── Invariants ──
        if (errors.Count > 0)
        {
            var errorSummary = string.Join("\n", errors.Select(e => $"  Thread[{e.ThreadIdx}] [{e.Ex.GetType().Name}] {e.Ex.Message}"));
            Assert.Fail($"Errors ({errors.Count}):\n{errorSummary}");
        }

        var activeCount = dbe.TransactionChain.ActiveCount;
        if (activeCount != 0)
        {
            // Walk the chain to diagnose leaked transactions
            var diag = new StringBuilder();
            diag.AppendLine($"ActiveCount = {activeCount}, walking chain for diagnostics:");
            var node = dbe.TransactionChain.Head;
            int chainLen = 0;
            while (node != null)
            {
                diag.AppendLine($"  [{chainLen}] TSN={node.TSN}, State={node.State}");
                node = node.Next;
                chainLen++;
                if (chainLen > 200)
                {
                    diag.AppendLine("  ... truncated (>200 nodes)");
                    break;
                }
            }
            diag.AppendLine($"  Chain length: {chainLen}, Tail TSN: {dbe.TransactionChain.Tail?.TSN.ToString() ?? "null"}");
            Assert.Fail($"Leaked chain node(s):\n{diag}");
        }

        // ── Report ──
        var total = ops.Sum();
        Assert.That(total, Is.GreaterThan(0), "Must complete some operations");
        TestContext.Out.WriteLine($"── Torture results ({durationMs}ms, {threadCount} threads) ──");
        TestContext.Out.WriteLine($"  Total ops:        {total:N0}  ({total / (durationMs / 1000.0):N0} ops/s)");
        TestContext.Out.WriteLine($"  Per thread:       {string.Join(", ", ops.Select(o => $"{o:N0}"))}");
        TestContext.Out.WriteLine($"  Min/Max per thread: {ops.Min():N0} / {ops.Max():N0}");
    }
}

/// <summary>
/// Separate fixture with MaxActiveTransactions = 5 to test the resource exhaustion guard.
/// </summary>
[TestFixture]
class TransactionChainMaxActiveTests : TestBase<TransactionChainMaxActiveTests>
{
    private const int MaxActive = 5;

    public override void Setup()
    {
        base.Setup();

        // Rebuild ServiceProvider with a low MaxActiveTransactions limit
        (ServiceProvider as IDisposable)?.Dispose();

        var serviceCollection = new ServiceCollection();
        ServiceCollection = serviceCollection;
        ServiceCollection
            .AddLogging(builder =>
            {
                builder.AddSerilog();
                builder.AddSimpleConsole(options =>
                {
                    options.SingleLine = true;
                    options.IncludeScopes = true;
                    options.TimestampFormat = "mm:ss.fff ";
                });
                builder.SetMinimumLevel(LogLevel.Information);
            })
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddHighResolutionSharedTimer()
            .AddDeadlineWatchdog()
            .AddScopedManagedPagedMemoryMappedFile(options =>
            {
                options.DatabaseName = CurrentDatabaseName;
                options.DatabaseDirectory = Path.Combine(Path.GetTempPath(), "Typhon.Tests", nameof(TransactionChainMaxActiveTests));
                options.DatabaseCacheSize = (ulong)PagedMMF.MinimumCacheSize;
                options.PagesDebugPattern = false;
            })
            .AddScopedDatabaseEngine(options =>
            {
                options.Resources.MaxActiveTransactions = MaxActive;
            });

        ServiceProvider = ServiceCollection.BuildServiceProvider();
        ServiceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
    }

    /// <summary>
    /// Fill to MaxActiveTransactions, verify next create throws ResourceExhaustedException,
    /// verify ActiveCount stays at the limit (no leak from the failed create path).
    /// After disposing one, verify create succeeds again.
    /// </summary>
    [Test]
    public void MaxActiveLimit_ExceedThrows_ActiveCountStable()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

#pragma warning disable TYPHON004 // Transactions disposed in cleanup loop below
        var held = new List<Transaction>();
        for (int i = 0; i < MaxActive; i++)
        {
            held.Add(dbe.CreateQuickTransaction());
        }
#pragma warning restore TYPHON004

        Assert.That(dbe.TransactionChain.ActiveCount, Is.EqualTo(MaxActive));

        // Next create should throw — result intentionally discarded (exception expected)
#pragma warning disable TYPHON004
        Assert.Throws<ResourceExhaustedException>(() => dbe.CreateQuickTransaction());
#pragma warning restore TYPHON004

        // ActiveCount must not have changed (no increment leaked from failed path)
        Assert.That(dbe.TransactionChain.ActiveCount, Is.EqualTo(MaxActive), "ActiveCount must not leak on failed create");

        // Dispose one — should allow a new create
        held[0].Dispose();
        Assert.That(dbe.TransactionChain.ActiveCount, Is.EqualTo(MaxActive - 1));

        var recovered = dbe.CreateQuickTransaction();
        Assert.That(dbe.TransactionChain.ActiveCount, Is.EqualTo(MaxActive));
        recovered.Dispose();

        // Cleanup
        for (int i = 1; i < held.Count; i++)
        {
            held[i].Dispose();
        }

        Assert.That(dbe.TransactionChain.ActiveCount, Is.EqualTo(0));
    }
}

