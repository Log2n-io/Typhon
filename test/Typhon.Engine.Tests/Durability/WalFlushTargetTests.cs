using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.Diagnostics;
using System.IO;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>
/// #937 / WP-16 — an LSN that was allocated but never carried by a WAL frame must not be a durability wait target. Two paths
/// consume LSNs without producing a frame: an append that throws after <c>TryClaim</c> (<c>AbandonClaim</c> publishes a skip frame,
/// whose <c>LastLsn</c> is 0, so draining it advances nothing), and an over-capacity claim whose producer times out in the
/// back-pressure park (<c>TryClaim</c> takes bytes AND LSNs in one XADD before it knows the claim fits, and <c>PerformSwap</c> folds
/// the consumed offsets into the base either way — no <c>AbandonClaim</c> is involved). When such a gap lands at the TAIL, a wait
/// for <c>LastAppendedLsn</c> (<c>NextLsn - 1</c>, which counts it) can only be satisfied by a LATER commit's frame draining past
/// it. An idle engine has no later commit, so the wait runs to its deadline: 30 s and a <c>WalBackPressureTimeoutException</c>
/// thrown out of <c>Transaction.Dispose</c>.
/// <para>
/// The fix is to wait for <c>LastPublishedLsn</c>, which only a real <c>Publish</c> advances. All three were red before it —
/// measured: the fixture took 1 m 42 s and every case failed.
/// </para>
/// </summary>
[TestFixture]
[NonParallelizable]
internal sealed class WalFlushTargetTests
{
    /// <summary>Distinctive substring of this rule's rejection messages, which its mutant must trip.</summary>
    private const string Wp16Marker = "WP-16 violated";

    /// <summary>
    /// The budget a stalled flush actually spends, and the threshold a healthy one must beat. Short, because the mutant
    /// deliberately stalls and would otherwise cost the suite the production default on every run.
    /// </summary>
    /// <remarks>
    /// It is <c>DefaultUowTimeout</c> that governs here, not <c>DefaultCommitTimeout</c>: a quick transaction's UoW is created with
    /// a BOUNDED deadline (<c>CreateUnitOfWork</c> falls back to <c>DefaultUowTimeout</c>), so <c>FlushAsync</c> takes its
    /// <c>FromDeadline</c> branch and never reads the commit timeout at all. Both defaults are 30 s, which is why the issue's
    /// measured stall was attributed to the wrong one; overriding only the commit timeout left the mutant costing a full 30 s.
    /// </remarks>
    private static readonly TimeSpan ShortCommitTimeout = TimeSpan.FromSeconds(1);

    private const int BarrierTimeoutMs = 2000;

    private string _dbDir;
    private string _walDir;
    private ServiceProvider _serviceProvider;
    private TimeSpan _savedCommitTimeout;
    private TimeSpan _savedUowTimeout;

    private static string CurrentDatabaseName
    {
        get
        {
            var name = TestContext.CurrentContext.Test.Name;
            foreach (var c in new[] { '(', ')', ',', ' ', '"' })
            {
                name = name.Replace(c, '_');
            }

            const int max = 63;
            const string prefix = "Wft_";
            if (prefix.Length + name.Length > max)
            {
                name = name[^(max - prefix.Length)..];
            }
            return prefix + name;
        }
    }

    [SetUp]
    public void Setup()
    {
        var root = Path.Combine(Path.GetTempPath(), "Typhon.Tests", nameof(WalFlushTargetTests));
        _dbDir = Path.Combine(root, CurrentDatabaseName, "db");
        _walDir = Path.Combine(root, CurrentDatabaseName, "wal");
        Directory.CreateDirectory(_dbDir);
        Directory.CreateDirectory(_walDir);

        var services = new ServiceCollection();
        services
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddHighResolutionSharedTimer()
            .AddDeadlineWatchdog()
            .AddScopedManagedPagedMemoryMappedFile(opts =>
            {
                opts.DatabaseName = CurrentDatabaseName;
                opts.DatabaseDirectory = _dbDir;
                opts.DatabaseCacheSize = (ulong)PagedMMF.MinimumCacheSize * 4;
            })
            .AddScopedDatabaseEngine(opts =>
            {
                opts.Wal = new WalWriterOptions
                {
                    WalDirectory = _walDir,
                    GroupCommitIntervalMs = 5,
                    UseFUA = false,
                    SegmentSize = 4 * 1024 * 1024,
                    PreAllocateSegments = 1,
                };
                // Bound the barrier with the test's own budget, so a regression costs 2 s per cycle rather than the 30 s default.
                opts.Resources = new ResourceOptions { CheckpointBarrierTimeoutMs = BarrierTimeoutMs };
            });

        _serviceProvider = services.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
    }

    [TearDown]
    public void TearDown()
    {
        if (_savedCommitTimeout != default)
        {
            TimeoutOptions.Current.DefaultCommitTimeout = _savedCommitTimeout;
            TimeoutOptions.Current.DefaultUowTimeout = _savedUowTimeout;
            _savedCommitTimeout = default;
            _savedUowTimeout = default;
        }

        _serviceProvider?.Dispose();
        _serviceProvider = null;

        var testRoot = Directory.GetParent(_dbDir)?.FullName;
        try
        {
            if (testRoot != null && Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }

    /// <summary>
    /// Disposing a transaction whose append failed after its claim, with no later commit, returns promptly and does not throw. The
    /// throw matters as much as the stall: <c>Dispose</c> runs inside the caller's <c>using</c>, so an exception raised there
    /// REPLACES the one in flight — which is usually the commit failure that abandoned the claim in the first place.
    /// </summary>
    [Test]
    [CancelAfter(20_000)]
    [VerifiesRule("WP-16")]
    public void AbandonedTailClaim_DisposingTheFailedTransaction_DoesNotWaitForAnLsnNoFrameOwns() =>
        AbandonedTailDisposeScenario(targetTheAllocationFrontier: false);

    /// <summary>
    /// The <see cref="RuleMutantAttribute"/> companion: point the flush target back at the allocation frontier — the pre-#937
    /// behaviour — and the dispose must go back to spending its whole budget and throwing, which the verifier must reject.
    /// </summary>
    [Test]
    [CancelAfter(20_000)]
    [RuleMutant("WP-16")]
    public void AbandonedTailClaim_AFlushTargetingTheAllocationFrontier_IsRejected() =>
        RuleMutants.AssertDetects("WP-16", Wp16Marker, () => AbandonedTailDisposeScenario(targetTheAllocationFrontier: true));

    private void AbandonedTailDisposeScenario(bool targetTheAllocationFrontier)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbe = PrepareEngine(scope, out var id);

        var wal = dbe.WalManager;
        if (targetTheAllocationFrontier)
        {
            wal.FlushTargetProbe = () => wal.LastAppendedLsn;
        }

        var failed = FailAnAppendAfterItsClaim(dbe, id);

        Exception fromDispose = null;
        var elapsed = Stopwatch.StartNew();
        try
        {
            failed.Dispose();
        }
        catch (Exception e)
        {
            fromDispose = e;
        }
        elapsed.Stop();

        var report = $"elapsed={elapsed.ElapsedMilliseconds}ms, DurableLsn={dbe.DurabilityLog.DurableLsn}, "
            + $"LastPublishedLsn={wal.CommitBuffer.LastPublishedLsn}, LastAppendedLsn={wal.LastAppendedLsn}, "
            + $"threw={fromDispose?.GetType().Name ?? "<none>"}";

        // Cleared before the assertions: a mutant that leaves the seam armed would hold it across the scope's teardown flush.
        wal.FlushTargetProbe = null;

        Assert.That(fromDispose, Is.Null, $"{Wp16Marker}: disposing the failed transaction threw ({report})");
        Assert.That(elapsed.Elapsed, Is.LessThan(ShortCommitTimeout), $"{Wp16Marker}: the dispose waited for an LSN no frame owns ({report})");
    }

    /// <summary>
    /// A checkpoint cycle after such a failure completes without a later commit. The barrier (CK-01/CK-02) and the CK-02 second
    /// flush wait on the same frontier the UoW flush does, so on an idle engine every cycle — the shutdown cycle included — failed
    /// transiently until an unrelated commit drained past the gap.
    /// </summary>
    [Test]
    [CancelAfter(20_000)]
    [VerifiesRule("WP-16")]
    public void AbandonedTailClaim_ACheckpointCycle_CompletesWithoutALaterCommit()
    {
        using var scope = _serviceProvider.CreateScope();
        var dbe = PrepareEngine(scope, out var id);

        FailAnAppendAfterItsClaim(dbe, id).Dispose();

        var cm = dbe.CheckpointManager;
        var cyclesBefore = cm.TotalCheckpoints;

        var elapsed = Stopwatch.StartNew();
        var completed = cm.ForceCheckpointAndWait(TimeSpan.FromSeconds(6));
        elapsed.Stop();

        var report = $"elapsed={elapsed.ElapsedMilliseconds}ms, completed={completed}, cycles {cyclesBefore}->{cm.TotalCheckpoints}, "
            + $"health={cm.Health}, CheckpointLsn={cm.CheckpointLsn}, DurableLsn={dbe.DurabilityLog.DurableLsn}, "
            + $"LastPublishedLsn={dbe.DurabilityLog.LastPublishedLsn}, LastAppendedLsn={dbe.DurabilityLog.LastAppendedLsn}";

        Assert.That(completed, Is.True, $"the checkpoint stalled on an LSN no frame owns ({report})");
        Assert.That(elapsed.Elapsed, Is.LessThan(TimeSpan.FromMilliseconds(BarrierTimeoutMs)), $"the cycle took a barrier timeout to get there ({report})");
    }

    /// <summary>
    /// The quiescent state the fix legalises: the allocation frontier sits ABOVE the published one and nothing is owed. This is the
    /// half a liveness test cannot see — that the target itself never names a gap, rather than that some later commit covered it.
    /// </summary>
    [Test]
    [CancelAfter(20_000)]
    [VerifiesRule("WP-16")]
    public void AbandonedTailClaim_TheFlushTarget_NeverNamesAnLsnNoFrameOwns()
    {
        using var scope = _serviceProvider.CreateScope();
        var dbe = PrepareEngine(scope, out var id);

        FailAnAppendAfterItsClaim(dbe, id).Dispose();

        var wal = dbe.WalManager;
        var allocated = wal.LastAppendedLsn;
        var published = wal.LastPublishedLsn;
        var durable = wal.DurableLsn;
        var report = $"allocated={allocated}, published={published}, durable={durable}";

        Assert.That(allocated, Is.GreaterThan(durable), $"the abandoned claim left no gap to test ({report})");
        Assert.That(published, Is.LessThan(allocated), $"the published frontier must stay below the abandoned claim ({report})");
        Assert.That(published, Is.EqualTo(durable), $"every published frame has drained, so the two frontiers must meet ({report})");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════════

    private DatabaseEngine PrepareEngine(IServiceScope scope, out EntityId id)
    {
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CmPosition>();
        dbe.RegisterComponentFromAccessor<CmWallet>();
        dbe.InitializeArchetypes();

        // After the engine's own initialization, which installs the singleton this overrides.
        _savedCommitTimeout = TimeoutOptions.Current.DefaultCommitTimeout;
        _savedUowTimeout = TimeoutOptions.Current.DefaultUowTimeout;
        TimeoutOptions.Current.DefaultCommitTimeout = ShortCommitTimeout;
        TimeoutOptions.Current.DefaultUowTimeout = ShortCommitTimeout;

        using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
        {
            id = tx.Spawn<CmEntity>(CmEntity.Position.Set(new CmPosition(1, 1)), CmEntity.Wallet.Set(new CmWallet(50)));
            tx.Commit();
        }

        return dbe;
    }

    /// <summary>
    /// Drives a commit whose <c>DurabilityLog.Append</c> throws after its claim, through the CK-13 test seam. Returns the failed
    /// transaction UNDISPOSED, with the abandoned claim at the tail of the LSN sequence and nothing after it — the state #937 is
    /// about. The caller disposes.
    /// </summary>
    private static Transaction FailAnAppendAfterItsClaim(DatabaseEngine dbe, EntityId id)
    {
        var log = (DurabilityLog)dbe.DurabilityLog;
        var failed = dbe.CreateQuickTransaction(DurabilityMode.Immediate, CommitDiscipline.Commit);
        failed.OpenMut(id).Write(CmEntity.Position) = new CmPosition(99, 88);

        var committerThread = Environment.CurrentManagedThreadId;
        log.AfterFloorProbe = () =>
        {
            if (Environment.CurrentManagedThreadId != committerThread)
            {
                return;
            }
            throw new InvalidOperationException("injected: the append fails after its claim");
        };

        try
        {
            failed.Commit();
            Assert.Fail("the injected append failure did not reach the caller");
        }
        catch (InvalidOperationException)
        {
            // expected
        }
        finally
        {
            log.AfterFloorProbe = null;
        }

        return failed;
    }
}
