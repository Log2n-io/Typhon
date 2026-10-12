using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

[Component("Typhon.Test.PostAppendFailure.Wallet", 1)]
[StructLayout(LayoutKind.Sequential)]
struct PaWallet
{
    public long Credits;
    public long Pad;
}

[Archetype]
class PaWalletArch : Archetype<PaWalletArch>
{
    public static readonly Comp<PaWallet> Wallet = Register<PaWallet>();
}

/// <summary>A SingleVersion component under a unique index (<c>[Index]</c> without AllowMultiple), written under the Commit discipline.</summary>
[Component("Typhon.Test.PostAppendFailure.SvKey", 1, StorageMode = StorageMode.SingleVersion, DefaultDiscipline = CommitDiscipline.Commit)]
[StructLayout(LayoutKind.Sequential)]
struct PaSvKey
{
    [Index] public int Key;
    public int Pad;
}

/// <summary>A Versioned component under a unique index.</summary>
[Component("Typhon.Test.PostAppendFailure.VKey", 1)]
[StructLayout(LayoutKind.Sequential)]
struct PaVKey
{
    [Index] public int Key;
    public int Pad;
}

/// <summary>A Versioned spatial component: a spawn can leave it unset, and then has no cell to be placed in.</summary>
[Component("Typhon.Test.PostAppendFailure.Pos", 1)]
[StructLayout(LayoutKind.Sequential)]
struct PaPos
{
    [Field]
    [SpatialIndex]
    public AABB3F Bounds;
}

[Archetype]
class PaSpatialArch : Archetype<PaSpatialArch>
{
    public static readonly Comp<PaPos> Pos = Register<PaPos>();
    public static readonly Comp<PaWallet> Wallet = Register<PaWallet>();
}

/// <summary>A SingleVersion float under a unique index: -0 and +0 are one key to it.</summary>
[Component("Typhon.Test.PostAppendFailure.FloatKey", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct PaFloatKey
{
    [Index] public float Key;
    public int Pad;
}

[Archetype]
class PaFloatKeyArch : Archetype<PaFloatKeyArch>
{
    public static readonly Comp<PaFloatKey> Key = Register<PaFloatKey>();
}

/// <summary>A Versioned String64 under a unique index (the only storage mode a String64 index allows): its keys live in the archetype's String64
/// index segment.</summary>
[Component("Typhon.Test.PostAppendFailure.NameKey", 1)]
[StructLayout(LayoutKind.Sequential)]
struct PaNameKey
{
    [Index] public String64 Name;
}

[Archetype]
class PaNameKeyArch : Archetype<PaNameKeyArch>
{
    public static readonly Comp<PaNameKey> Key = Register<PaNameKey>();
}

/// <summary>Two unique fields in one SingleVersion component.</summary>
[Component("Typhon.Test.PostAppendFailure.TwoKeys", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct PaTwoKeys
{
    [Index] public int A;
    [Index] public int B;
}

[Archetype]
class PaTwoKeysArch : Archetype<PaTwoKeysArch>
{
    public static readonly Comp<PaTwoKeys> Keys = Register<PaTwoKeys>();
}

/// <summary>A SingleVersion counter under the default (tick-fence) discipline: its writes are logged by the tick fence.</summary>
[Component("Typhon.Test.PostAppendFailure.Counter", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct PaCounter
{
    public long Value;
}

[Archetype]
class PaCounterArch : Archetype<PaCounterArch>
{
    public static readonly Comp<PaCounter> Counter = Register<PaCounter>();
}

[Archetype]
class PaSvKeyArch : Archetype<PaSvKeyArch>
{
    public static readonly Comp<PaSvKey> Key = Register<PaSvKey>();
}

[Archetype]
class PaVKeyArch : Archetype<PaVKeyArch>
{
    public static readonly Comp<PaVKey> Key = Register<PaVKey>();
}

/// <summary>
/// Rules <b>AP-02</b> / <b>AP-03</b> (<c>rules/durability.md</c>, #396): a commit whose records are in the WAL is never rolled back. When something throws
/// after the append, before its publish completed, the transaction reports Committed, the engine stops committing — its memory holds a partial publish —
/// and the checkpoint stays below the commit's records, so a restart replays them and the commit comes back whole.
/// </summary>
[TestFixture]
[NonParallelizable] // The back-pressure tests shorten the static TimeoutOptions.Current, which every engine reads.
internal sealed class PostAppendFailureTests
{
    private const string Marker = "AP-03 violated";
    private string _dbDir;
    private string _walDir;
    private ServiceProvider _serviceProvider;

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
            const string prefix = "Paf_";
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
        var root = Path.Combine(Path.GetTempPath(), "Typhon.Tests", nameof(PostAppendFailureTests));
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
            });

        _serviceProvider = services.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
    }

    [TearDown]
    public void TearDown()
    {
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

    private static DatabaseEngine Open(IServiceScope scope)
    {
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<PaWallet>();
        dbe.RegisterComponentFromAccessor<PaSvKey>();
        dbe.RegisterComponentFromAccessor<PaVKey>();
        dbe.RegisterComponentFromAccessor<PaPos>();
        dbe.RegisterComponentFromAccessor<PaFloatKey>();
        dbe.RegisterComponentFromAccessor<PaTwoKeys>();
        dbe.RegisterComponentFromAccessor<PaNameKey>();
        dbe.RegisterComponentFromAccessor<PaCounter>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(worldMin: new Vector2(-1_000, -1_000), worldMax: new Vector2(1_000, 1_000), cellSize: 100f));
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>Shortens the page-cache back-pressure timeouts for one test; set after the engine is built, as its constructor installs its own.</summary>
    private sealed class BackpressureTimeouts : IDisposable
    {
        private readonly TimeoutOptions _saved = TimeoutOptions.Current;

        public BackpressureTimeouts(TimeSpan allocation, TimeSpan publish)
        {
            var timeouts = _saved.Clone();
            timeouts.PageCacheBackpressureTimeout = allocation;
            timeouts.CommitPublishBackpressureTimeout = publish;
            TimeoutOptions.Current = timeouts;
        }

        public void Dispose() => TimeoutOptions.Current = _saved;
    }

    /// <summary>
    /// Back-pressure on the engine's page cache, for the committing thread only and from its append on: each of its allocation rounds finds no slot
    /// while <c>holdWhile</c> is true. Other threads — the checkpoint's — allocate as usual. Counts the rounds it forced, so a test can show its publish
    /// did allocate.
    /// </summary>
    private sealed class PublishBackpressure : IDisposable
    {
        private readonly DatabaseEngine _dbe;
        private volatile int _publisher;
        private int _forced;

        public PublishBackpressure(DatabaseEngine dbe, Func<bool> holdWhile)
        {
            _dbe = dbe;
            dbe.CommitAfterAppendProbe = () => _publisher = Environment.CurrentManagedThreadId;
            dbe.MMF.ForceBackpressureForTest = () =>
            {
                if (_publisher != Environment.CurrentManagedThreadId || !holdWhile())
                {
                    return false;
                }

                Interlocked.Increment(ref _forced);
                return true;
            };
        }

        public int ForcedRounds => Volatile.Read(ref _forced);

        public void Dispose()
        {
            _dbe.CommitAfterAppendProbe = null;
            _dbe.MMF.ForceBackpressureForTest = null;
        }
    }

    /// <summary>
    /// Enough spawns that the publish grows the archetype's B+Tree, so it allocates pages. A spawn's placement — the cluster segment's growth — is PREPARE's
    /// since VIS-08; its index entries are still the publish's.
    /// </summary>
    private const int SpawnCount = 20_000;

    private static EntityId[] SpawnWallets(Transaction tx)
    {
        var ids = new EntityId[SpawnCount];
        for (var i = 0; i < ids.Length; i++)
        {
            ids[i] = tx.Spawn<PaVKeyArch>(PaVKeyArch.Key.Set(new PaVKey { Key = 1_000 + i }));
        }

        return ids;
    }

    private static void AssertWallets(DatabaseEngine dbe, EntityId[] ids, string what)
    {
        using var tx = dbe.CreateReadOnlyTransaction();
        for (var i = 0; i < ids.Length; i++)
        {
            Assert.That(tx.TryOpen(ids[i], out var e), Is.True, $"{Marker}: {what}: spawn {i} of {ids.Length} is missing");
            Assert.That(e.Read(PaVKeyArch.Key).Key, Is.EqualTo(1_000 + i), $"{Marker}: {what}: spawn {i} is incomplete");
        }
    }

    /// <summary>
    /// Back-pressure during a spawn publish, past the append, outlasting <see cref="TimeoutOptions.PageCacheBackpressureTimeout"/>: the publish waits it out
    /// (AP-03), and the commit completes once it clears — every entity visible and complete, the engine committing on. Thrown, it would have stopped the
    /// engine over a partial publish.
    /// </summary>
    [Test]
    [VerifiesRule("AP-03")]
    [CancelAfter(30_000)]
    public void BackpressureDuringTheSpawnPublish_IsWaitedOut_AndTheCommitCompletes() => RunBackpressureWaitedOut(giveUp: false);

    /// <summary>The <see cref="RuleMutantAttribute"/> companion: a publish that gives up on back-pressure after the ordinary allocation timeout.</summary>
    [Test]
    [RuleMutant("AP-03")]
    [CancelAfter(30_000)]
    public void APublishThatGivesUpOnBackpressure_StopsTheEngine() =>
        RuleMutants.AssertDetects("AP-03", Marker, () => RunBackpressureWaitedOut(giveUp: true));

    private void RunBackpressureWaitedOut(bool giveUp)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbe = Open(scope);
        using var timeouts = new BackpressureTimeouts(allocation: TimeSpan.FromMilliseconds(40), publish: TimeSpan.FromSeconds(20));
        dbe.PublishGivesUpOnBackpressureForTest = giveUp;
        var extensionsBefore = dbe.MMF.PublishBackpressureExtensions;

        EntityId[] ids;
        Exception thrown = null;
        using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
        using (var pressure = new PublishBackpressure(dbe, holdWhile: () => dbe.MMF.PublishBackpressureExtensions == extensionsBefore))
        {
            ids = SpawnWallets(tx);
            try
            {
                tx.Commit();
            }
            catch (Exception e)
            {
                thrown = e;
            }

            Assert.That(pressure.ForcedRounds, Is.GreaterThan(0), "premise: the publish allocated pages under back-pressure");
        }

        Assert.That(thrown, Is.Null, $"{Marker}: back-pressure during the publish failed the commit: {thrown?.GetType().Name}");
        Assert.That(dbe.MMF.PublishBackpressureExtensions, Is.GreaterThan(extensionsBefore),
            "premise: the pressure outlasted the allocation timeout, so the publish went on waiting instead of giving up");
        Assert.That(dbe.CommitPipelineFailure, Is.Null, $"{Marker}: back-pressure during the publish stopped the engine");
        AssertWallets(dbe, ids, "after the waited-out publish");

        using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
        {
            tx.Spawn<PaWalletArch>(PaWalletArch.Wallet.Set(new PaWallet { Credits = 1 }));
            Assert.That(tx.Commit(), Is.True, "the engine goes on committing");
        }
    }

    /// <summary>
    /// Back-pressure that outlasts <see cref="TimeoutOptions.CommitPublishBackpressureTimeout"/> as well: the publish gives up, partly done. The commit is
    /// Committed and its records durable, the engine stops, and a restart brings every spawn back whole.
    /// </summary>
    [Test]
    [VerifiesRule("AP-03")]
    [CancelAfter(30_000)]
    public void BackpressureOutlastingThePublishLimit_StopsTheEngine_AndARestartRecoversTheSpawns()
    {
        EntityId[] ids;
        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = Open(scope1);
            using var timeouts = new BackpressureTimeouts(allocation: TimeSpan.FromMilliseconds(30), publish: TimeSpan.FromMilliseconds(150));
            Exception thrown = null;
            // Bounded, so a publish that never gives up fails the test instead of hanging it.
            var held = Stopwatch.StartNew();
            using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            using (var pressure = new PublishBackpressure(dbe, holdWhile: () => held.Elapsed < TimeSpan.FromSeconds(3)))
            {
                ids = SpawnWallets(tx);
                try
                {
                    tx.Commit();
                }
                catch (Exception e)
                {
                    thrown = e;
                }

                Assert.That(pressure.ForcedRounds, Is.GreaterThan(0), "premise: the publish allocated pages under back-pressure");
                Assert.That(tx.State, Is.EqualTo(Transaction.TransactionState.Committed), $"{Marker}: the appended commit is not Committed (AP-02)");
            }

            Assert.That(thrown, Is.InstanceOf<CommitPublishIncompleteException>(), $"{Marker}: the publish's give-up surfaced as {thrown?.GetType().Name}");
            Assert.That(thrown!.InnerException, Is.InstanceOf<PageCacheBackpressureTimeoutException>(), "the give-up is the back-pressure's");
            Assert.That(((CommitPublishIncompleteException)thrown).Durable, Is.True, "the commit's records are confirmed durable");
            Assert.That(dbe.CommitPipelineFailure, Is.Not.Null, $"{Marker}: the engine goes on committing over a partial publish");
        }

        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = Open(scope2);
            AssertWallets(dbe, ids, "after the restart");
        }
    }

    /// <summary>
    /// A spawn that leaves unset a Versioned component a unique index covers is rejected before the append (AP-03): the publish would index it under
    /// whatever bytes its claimed cluster slot holds, and two such spawns collide there, past the append.
    /// </summary>
    [Test]
    [VerifiesRule("AP-03")]
    [CancelAfter(30_000)]
    public void ASpawnWithoutItsUniqueIndexedComponent_IsRejectedBeforeTheAppend()
    {
        using var scope = _serviceProvider.CreateScope();
        var dbe = Open(scope);
        var lsnBefore = dbe.DurabilityLog.LastAppendedLsn;
        using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
        {
            tx.Spawn<PaVKeyArch>();
            var thrown = Assert.Throws<InvalidOperationException>(() => tx.Commit(),
                $"{Marker}: the spawn without its unique-indexed component is not rejected");
            Assert.That(thrown!.Message, Does.Contain("unique index"));
        }

        Assert.That(dbe.DurabilityLog.LastAppendedLsn, Is.EqualTo(lsnBefore), $"{Marker}: the rejected commit appended records");
        Assert.That(dbe.CommitPipelineFailure, Is.Null, $"{Marker}: a rejected spawn stopped the engine");
    }

    /// <summary>How a Commit-discipline write takes a key the index holds when the commit starts, but not when the write publishes.</summary>
    public enum FreedKey
    {
        /// <summary>The same commit destroys the holder: its entry is removed before the staged writes publish.</summary>
        HolderDestroyed,

        /// <summary>A staged write visited earlier moves the holder's key away.</summary>
        HolderMovedFirst,
    }

    /// <summary>
    /// A Commit-discipline write onto a key that is free by the time it publishes commits: AP-03's check follows the publish order, in which a destroy of
    /// the holder, or a write visited earlier that moves its key away, runs first.
    /// </summary>
    [Test]
    [VerifiesRule("AP-03")]
    [CancelAfter(30_000)]
    public void AStagedWrite_OntoAKeyFreedEarlierInThePublish_Commits([Values] FreedKey freed)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbe = Open(scope);
        EntityId a, b;
        using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
        {
            a = tx.Spawn<PaSvKeyArch>(PaSvKeyArch.Key.Set(new PaSvKey { Key = 1 }));
            b = tx.Spawn<PaSvKeyArch>(PaSvKeyArch.Key.Set(new PaSvKey { Key = 2 }));
            tx.Commit();
        }

        using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate, CommitDiscipline.Commit))
        {
            if (freed == FreedKey.HolderDestroyed)
            {
                tx.Destroy(b);
            }
            else
            {
                tx.OpenMut(b).Set(PaSvKeyArch.Key, new PaSvKey { Key = 3 });
            }

            tx.OpenMut(a).Set(PaSvKeyArch.Key, new PaSvKey { Key = 2 });
            Assert.That(tx.Commit(), Is.True, $"{Marker}: a write onto a key freed earlier in the publish is rejected");
        }

        Assert.That(dbe.CommitPipelineFailure, Is.Null);
        using (var r = dbe.CreateReadOnlyTransaction())
        {
            Assert.That(r.Open(a).Read(PaSvKeyArch.Key).Key, Is.EqualTo(2));
            Assert.That(r.TryOpen(b, out var e) ? e.Read(PaSvKeyArch.Key).Key : -1, Is.EqualTo(freed == FreedKey.HolderDestroyed ? -1 : 3));
        }
    }

    /// <summary>
    /// A key moved by two staged writes in the wrong order: A takes B's key before B's write, visited later, moves it away. The publish would reject A's
    /// move, so the check does, before the append.
    /// </summary>
    [Test]
    [VerifiesRule("AP-03")]
    [CancelAfter(30_000)]
    public void AStagedWrite_OntoAKeyMovedAwayLaterInThePublish_IsRejectedBeforeTheAppend()
    {
        using var scope = _serviceProvider.CreateScope();
        var dbe = Open(scope);
        EntityId a, b;
        using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
        {
            a = tx.Spawn<PaSvKeyArch>(PaSvKeyArch.Key.Set(new PaSvKey { Key = 1 }));
            b = tx.Spawn<PaSvKeyArch>(PaSvKeyArch.Key.Set(new PaSvKey { Key = 2 }));
            tx.Commit();
        }

        var lsnBefore = dbe.DurabilityLog.LastAppendedLsn;
        using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate, CommitDiscipline.Commit))
        {
            tx.OpenMut(a).Set(PaSvKeyArch.Key, new PaSvKey { Key = 2 });
            tx.OpenMut(b).Set(PaSvKeyArch.Key, new PaSvKey { Key = 3 });
            Assert.Throws<UniqueConstraintViolationException>(() => tx.Commit(), $"{Marker}: a move onto a key held until later in the publish is accepted");
        }

        Assert.That(dbe.DurabilityLog.LastAppendedLsn, Is.EqualTo(lsnBefore), $"{Marker}: the rejected commit appended records");
        Assert.That(dbe.CommitPipelineFailure, Is.Null, $"{Marker}: the rejection stopped the engine");
    }

    /// <summary>How a commit puts one key twice by the index's own comparison, though not byte for byte, or puts a key a second unique field holds.</summary>
    public enum KeyShape
    {
        /// <summary>Two spawns, one with +0f and one with -0f: one key to a float index.</summary>
        PlusAndMinusZero,

        /// <summary>A component with two unique fields, the second one's key already committed.</summary>
        SecondUniqueField,

        /// <summary>A String64 key already committed: checked in the archetype's String64 index segment.</summary>
        String64Key,
    }

    [Test]
    [VerifiesRule("AP-03")]
    [CancelAfter(30_000)]
    public void AKeyTakenByTheIndexsOwnComparison_IsRejectedBeforeTheAppend([Values] KeyShape shape)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbe = Open(scope);
        if (shape == KeyShape.SecondUniqueField)
        {
            using var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            tx.Spawn<PaTwoKeysArch>(PaTwoKeysArch.Keys.Set(new PaTwoKeys { A = 1, B = 5 }));
            tx.Commit();
        }
        else if (shape == KeyShape.String64Key)
        {
            using var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            tx.Spawn<PaNameKeyArch>(PaNameKeyArch.Key.Set(new PaNameKey { Name = "taken" }));
            tx.Spawn<PaNameKeyArch>(PaNameKeyArch.Key.Set(new PaNameKey { Name = "other" }));
            tx.Commit();
        }

        var lsnBefore = dbe.DurabilityLog.LastAppendedLsn;
        using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
        {
            if (shape == KeyShape.PlusAndMinusZero)
            {
                tx.Spawn<PaFloatKeyArch>(PaFloatKeyArch.Key.Set(new PaFloatKey { Key = 0f }));
                tx.Spawn<PaFloatKeyArch>(PaFloatKeyArch.Key.Set(new PaFloatKey { Key = -0f }));
            }
            else if (shape == KeyShape.SecondUniqueField)
            {
                tx.Spawn<PaTwoKeysArch>(PaTwoKeysArch.Keys.Set(new PaTwoKeys { A = 2, B = 5 }));
            }
            else
            {
                tx.Spawn<PaNameKeyArch>(PaNameKeyArch.Key.Set(new PaNameKey { Name = "free" }));
                tx.Spawn<PaNameKeyArch>(PaNameKeyArch.Key.Set(new PaNameKey { Name = "taken" }));
            }

            Assert.Throws<UniqueConstraintViolationException>(() => tx.Commit(), $"{Marker}: the taken key is not rejected before the append");
        }

        Assert.That(dbe.DurabilityLog.LastAppendedLsn, Is.EqualTo(lsnBefore), $"{Marker}: the rejected commit appended records");
        Assert.That(dbe.CommitPipelineFailure, Is.Null, $"{Marker}: the rejection stopped the engine");
    }

    /// <summary>
    /// The engine stops while a commit is in PREPARE (here, inside its conflict handler): that commit still rolls back clean — checked again just before
    /// its append, it appends nothing over the partial publish.
    /// </summary>
    [Test]
    [VerifiesRule("AP-03")]
    [CancelAfter(30_000)]
    public void AnEngineThatStopsDuringACommitsPrepare_RefusesItsAppend()
    {
        using var scope = _serviceProvider.CreateScope();
        var dbe = Open(scope);
        EntityId id;
        using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
        {
            id = tx.Spawn<PaWalletArch>(PaWalletArch.Wallet.Set(new PaWallet { Credits = 1 }));
            tx.Commit();
        }

        var handlerRan = false;
        using (var late = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
        {
            late.OpenMut(id).Set(PaWalletArch.Wallet, new PaWallet { Credits = 3 });
            using (var first = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                first.OpenMut(id).Set(PaWalletArch.Wallet, new PaWallet { Credits = 2 });
                first.Commit();
            }

            var lsnBefore = dbe.DurabilityLog.LastAppendedLsn;
            var thrown = Assert.Throws<DurabilityException>(() => late.Commit((ref ConcurrencyConflictSolver _) =>
            {
                handlerRan = true;
                dbe.LatchCommitPipelineFailure(0, new InvalidOperationException("another commit's publish failed"));
            }), $"{Marker}: a commit appended over an engine that stopped while it prepared");
            Assert.That(handlerRan, Is.True, "premise: the conflict handler ran inside the commit's PREPARE");
            Assert.That(thrown!.ErrorCode, Is.EqualTo(TyphonErrorCode.CommitPipelineStopped));
            Assert.That(dbe.DurabilityLog.LastAppendedLsn, Is.EqualTo(lsnBefore), $"{Marker}: the refused commit appended records");
        }
    }

    /// <summary>
    /// A conflict-handler commit holds each entity's chain lock from its PREPARE. A throw under that lock, after the append, must release it once and
    /// surface as <see cref="CommitPublishIncompleteException"/>: released by the publish and again by the abort path, the second release's
    /// InvalidOperationException took its place, and with it the "do not run it again".
    /// </summary>
    [Test]
    [VerifiesRule("AP-03")]
    [CancelAfter(30_000)]
    public void AThrowUnderAHandlerCommitsChainLock_ReleasesItOnce_AndSurfacesAsIncomplete()
    {
        using var scope = _serviceProvider.CreateScope();
        var dbe = Open(scope);
        EntityId id;
        using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
        {
            id = tx.Spawn<PaWalletArch>(PaWalletArch.Wallet.Set(new PaWallet { Credits = 1 }));
            tx.Commit();
        }

        Exception thrown = null;
        using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
        {
            tx.OpenMut(id).Set(PaWalletArch.Wallet, new PaWallet { Credits = 2 });
            dbe.PublishUnderLockProbe = () =>
            {
                dbe.PublishUnderLockProbe = null;
                throw new TimeoutException("injected under the chain lock");
            };
            try
            {
                tx.Commit((ref ConcurrencyConflictSolver _) => { });
            }
            catch (Exception e)
            {
                thrown = e;
            }
            finally
            {
                dbe.PublishUnderLockProbe = null;
            }
        }

        Assert.That(thrown, Is.InstanceOf<CommitPublishIncompleteException>(),
            $"{Marker}: a throw under a retained chain lock surfaced as {thrown?.GetType().Name ?? "nothing"}: {thrown?.Message}");
        Assert.That(dbe.CommitPipelineFailure, Is.Not.Null, "the engine stopped");
    }

    /// <summary>A stopped engine's tick fence appends nothing over the partial publish (AP-03): it returns without logging the tick's writes.</summary>
    [Test]
    [VerifiesRule("AP-03")]
    [CancelAfter(30_000)]
    public void AStoppedEnginesTickFence_AppendsNothing()
    {
        using var scope = _serviceProvider.CreateScope();
        var dbe = Open(scope);
        EntityId id;
        using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
        {
            id = tx.Spawn<PaCounterArch>(PaCounterArch.Counter.Set(new PaCounter { Value = 1 }));
            tx.Commit();
        }

        WriteCounter(dbe, id, 2);
        Assert.That(dbe.WriteTickFence(1), Is.GreaterThan(0), "premise: a tick-fence write is logged by the fence");

        dbe.LatchCommitPipelineFailure(0, new InvalidOperationException("a commit's publish failed"));

        // A tick-fence write lands in place when it is made; its commit, which logs nothing itself, is refused like any other on a stopped engine.
        Assert.Throws<DurabilityException>(() => WriteCounter(dbe, id, 3), "a stopped engine refuses the commit");
        var lsnBefore = dbe.DurabilityLog.LastAppendedLsn;
        Assert.That(dbe.WriteTickFence(2), Is.Zero, $"{Marker}: a stopped engine's fence logged the tick's writes");
        Assert.That(dbe.DurabilityLog.LastAppendedLsn, Is.EqualTo(lsnBefore), $"{Marker}: a stopped engine's fence appended records");
    }

    /// <summary>
    /// A Commit-discipline write from -0f to +0f: the bytes change, so the publish moves the key, and the index finds the new key held — by the entity
    /// itself (one key to the index). The check accepts it, as the publish does, and the commit goes through.
    /// </summary>
    [Test]
    [VerifiesRule("AP-03")]
    [CancelAfter(30_000)]
    public void AStagedWrite_OntoTheKeyItsEntityHolds_Commits()
    {
        using var scope = _serviceProvider.CreateScope();
        var dbe = Open(scope);
        EntityId id;
        using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
        {
            id = tx.Spawn<PaFloatKeyArch>(PaFloatKeyArch.Key.Set(new PaFloatKey { Key = -0f }));
            tx.Commit();
        }

        using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate, CommitDiscipline.Commit))
        {
            tx.OpenMut(id).Set(PaFloatKeyArch.Key, new PaFloatKey { Key = 0f, Pad = 1 });
            Assert.That(tx.Commit(), Is.True, $"{Marker}: a write onto the key its own entity holds is rejected");
        }

        Assert.That(dbe.CommitPipelineFailure, Is.Null, $"{Marker}: the write stopped the engine");
        using (var r = dbe.CreateReadOnlyTransaction())
        {
            Assert.That(r.Open(id).Read(PaFloatKeyArch.Key).Pad, Is.EqualTo(1));
        }
    }

    /// <summary>
    /// The runtime's parallel fence — the default — appends nothing on a stopped engine (AP-03): its table and archetype appends do not pass through
    /// <see cref="DatabaseEngine.WriteTickFence"/>. A tick-fence write is pending when the runtime starts; without the stop, its first fence logs it.
    /// </summary>
    [Test]
    [VerifiesRule("AP-03")]
    [CancelAfter(30_000)]
    public void AStoppedEnginesParallelFence_AppendsNothing([Values] bool stopped)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbe = Open(scope);
        EntityId id;
        using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
        {
            id = tx.Spawn<PaCounterArch>(PaCounterArch.Counter.Set(new PaCounter { Value = 1 }));
            tx.Commit();
        }

        if (stopped)
        {
            dbe.LatchCommitPipelineFailure(0, new InvalidOperationException("a commit's publish failed"));
            Assert.Throws<DurabilityException>(() => WriteCounter(dbe, id, 2), "a stopped engine refuses the commit");
        }
        else
        {
            WriteCounter(dbe, id, 2);
        }

        var lsnBefore = dbe.DurabilityLog.LastAppendedLsn;
        var ticks = 0;
        Exception unhandled = null;
        using (var runtime = TyphonRuntime.Create(dbe, schedule =>
               {
                   schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Count", _ => Interlocked.Increment(ref ticks));
               }, new RuntimeOptions { WorkerCount = 2, BaseTickRate = 100, EnableParallelFence = true }))
        {
            runtime.Scheduler.UnhandledExceptionCallback = (_, _, ex) => Interlocked.CompareExchange(ref unhandled, ex, null);
            runtime.Start();
            SpinWait.SpinUntil(() => Volatile.Read(ref ticks) >= 3 && runtime.CurrentTickNumber >= 3, TimeSpan.FromSeconds(15));
            runtime.Shutdown();
        }

        Assert.That(unhandled, Is.Null, $"the fence must not throw on a stopped engine: {unhandled}");
        Assert.That(ticks, Is.GreaterThanOrEqualTo(3), "premise: the runtime ticked");
        if (stopped)
        {
            Assert.That(dbe.DurabilityLog.LastAppendedLsn, Is.EqualTo(lsnBefore), $"{Marker}: a stopped engine's parallel fence appended records");
        }
        else
        {
            Assert.That(dbe.DurabilityLog.LastAppendedLsn, Is.GreaterThan(lsnBefore), "premise: the parallel fence logs a pending tick-fence write");
        }
    }

    private static void WriteCounter(DatabaseEngine dbe, EntityId id, long value)
    {
        using var tx = dbe.CreateQuickTransaction();
        tx.OpenMut(id).Set(PaCounterArch.Counter, new PaCounter { Value = value });
        tx.Commit();
    }

    /// <summary>
    /// A spawn of a spatial archetype that leaves its (Versioned) spatial component unset is rejected before the append (AP-03): the publish could not place
    /// it in a cell, and found there, past the append, the failure could only stop the engine.
    /// </summary>
    [Test]
    [VerifiesRule("AP-03")]
    [CancelAfter(30_000)]
    public void ASpatialSpawnWithoutItsSpatialComponent_IsRejectedBeforeTheAppend()
    {
        using var scope = _serviceProvider.CreateScope();
        var dbe = Open(scope);
        var lsnBefore = dbe.DurabilityLog.LastAppendedLsn;
        EntityId rejected;
        using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
        {
            rejected = tx.Spawn<PaSpatialArch>(PaSpatialArch.Wallet.Set(new PaWallet { Credits = 5 }));
            var thrown = Assert.Throws<InvalidOperationException>(() => tx.Commit(), $"{Marker}: the spawn without its spatial component is not rejected");
            Assert.That(thrown!.Message, Does.Contain("spatial component"));
        }

        Assert.That(dbe.DurabilityLog.LastAppendedLsn, Is.EqualTo(lsnBefore), $"{Marker}: the rejected commit appended records");
        Assert.That(dbe.CommitPipelineFailure, Is.Null, $"{Marker}: a rejected spawn stopped the engine");
        EntityId placed;
        using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
        {
            Assert.That(tx.TryOpen(rejected, out _), Is.False, "the rejected spawn is not visible");
            placed = tx.Spawn<PaSpatialArch>(
                PaSpatialArch.Pos.Set(new PaPos { Bounds = new AABB3F { MinX = 1, MinY = 1, MinZ = 0, MaxX = 2, MaxY = 2, MaxZ = 1 } }),
                PaSpatialArch.Wallet.Set(new PaWallet { Credits = 6 }));
            Assert.That(tx.Commit(), Is.True, "a spatial spawn with its spatial component commits");
        }

        using (var tx = dbe.CreateReadOnlyTransaction())
        {
            Assert.That(tx.Open(placed).Read(PaSpatialArch.Wallet).Credits, Is.EqualTo(6));
        }
    }

    /// <summary>How a commit puts a key that a unique index already holds.</summary>
    public enum Collision
    {
        /// <summary>A spawn of a SingleVersion component, the key committed by another entity.</summary>
        SvSpawnOverCommitted,

        /// <summary>A spawn of a Versioned component, the key committed by another entity.</summary>
        VersionedSpawnOverCommitted,

        /// <summary>Two spawns of one transaction, one key.</summary>
        TwoSpawnsOneKey,

        /// <summary>A Commit-discipline write that moves an entity's key onto one another entity holds.</summary>
        StagedWriteOverCommitted,
    }

    /// <summary>
    /// A unique key already taken is rejected before the append (AP-03): the commit throws <see cref="UniqueConstraintViolationException"/>, appends no
    /// record, rolls back clean, and the engine goes on committing. Found at publish instead, past the append, it could only stop the engine.
    /// </summary>
    [Test]
    [VerifiesRule("AP-03")]
    [CancelAfter(30_000)]
    public void AUniqueKeyAlreadyTaken_IsRejectedBeforeTheAppend([Values] Collision collision)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbe = Open(scope);
        EntityId holder, mover = default;
        using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
        {
            holder = collision == Collision.VersionedSpawnOverCommitted
                ? tx.Spawn<PaVKeyArch>(PaVKeyArch.Key.Set(new PaVKey { Key = 7 }))
                : tx.Spawn<PaSvKeyArch>(PaSvKeyArch.Key.Set(new PaSvKey { Key = 7 }));
            if (collision == Collision.StagedWriteOverCommitted)
            {
                mover = tx.Spawn<PaSvKeyArch>(PaSvKeyArch.Key.Set(new PaSvKey { Key = 8 }));
            }

            tx.Commit();
        }

        var lsnBefore = dbe.DurabilityLog.LastAppendedLsn;
        EntityId rejected = default;
        using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate, CommitDiscipline.Commit))
        {
            switch (collision)
            {
                case Collision.SvSpawnOverCommitted:
                    rejected = tx.Spawn<PaSvKeyArch>(PaSvKeyArch.Key.Set(new PaSvKey { Key = 7 }));
                    break;
                case Collision.VersionedSpawnOverCommitted:
                    rejected = tx.Spawn<PaVKeyArch>(PaVKeyArch.Key.Set(new PaVKey { Key = 7 }));
                    break;
                case Collision.TwoSpawnsOneKey:
                    rejected = tx.Spawn<PaSvKeyArch>(PaSvKeyArch.Key.Set(new PaSvKey { Key = 9 }));
                    tx.Spawn<PaSvKeyArch>(PaSvKeyArch.Key.Set(new PaSvKey { Key = 9 }));
                    break;
                case Collision.StagedWriteOverCommitted:
                    tx.OpenMut(mover).Set(PaSvKeyArch.Key, new PaSvKey { Key = 7 });
                    break;
            }

            Assert.Throws<UniqueConstraintViolationException>(() => tx.Commit(), $"{Marker}: the taken key is not rejected as a unique violation");
        }

        Assert.That(dbe.DurabilityLog.LastAppendedLsn, Is.EqualTo(lsnBefore), $"{Marker}: the rejected commit appended records");
        Assert.That(dbe.CommitPipelineFailure, Is.Null, $"{Marker}: a rejected key stopped the engine");
        using (var r = dbe.CreateReadOnlyTransaction())
        {
            if (rejected != default)
            {
                Assert.That(r.TryOpen(rejected, out _), Is.False, "the rejected spawn is not visible");
            }

            if (collision == Collision.StagedWriteOverCommitted)
            {
                Assert.That(r.Open(mover).Read(PaSvKeyArch.Key).Key, Is.EqualTo(8), "the rejected write is not visible");
            }
        }

        using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
        {
            tx.Spawn<PaSvKeyArch>(PaSvKeyArch.Key.Set(new PaSvKey { Key = 100 }));
            Assert.That(tx.Commit(), Is.True, "the engine goes on committing");
        }
    }

    /// <summary>
    /// #1271: once AP-03 stopped the engine, a SingleVersion write under the tick-fence discipline still lands in place — and no fence logs it any more.
    /// The checkpoint went on, and persisted it: the reopen read a value no WAL record holds, beside the log's pre-stop state. A stopped engine's
    /// checkpointer writes nothing more to the data file.
    /// </summary>
    [Test]
    [VerifiesRule("AP-03")]
    [CancelAfter(30_000)]
    public void AWriteAfterTheEngineStopped_NeverReachesTheDataFile()
    {
        EntityId counter;
        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = Open(scope1);
            EntityId wallet;
            using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                counter = tx.Spawn<PaCounterArch>(PaCounterArch.Counter.Set(new PaCounter { Value = 1 }));
                wallet = tx.Spawn<PaWalletArch>(PaWalletArch.Wallet.Set(new PaWallet { Credits = 50 }));
                tx.Commit();
            }

            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(5)), Is.True, "premise: the spawns are checkpointed");

            dbe.CommitAfterAppendProbe = () => throw new InvalidOperationException("injected after the append");
            using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                tx.OpenMut(wallet).Set(PaWalletArch.Wallet, new PaWallet { Credits = 99 });
                Assert.Catch(() => tx.Commit(), "premise: the commit failed past its append");
            }

            dbe.CommitAfterAppendProbe = null;
            Assert.That(dbe.CommitPipelineFailure, Is.Not.Null, "premise: the engine stopped");

            // In place, under the tick fence: no fence logs it once stopped (FenceMustNotAppend), whatever the commit does.
            using (var tx = dbe.CreateQuickTransaction())
            {
                tx.OpenMut(counter).Set(PaCounterArch.Counter, new PaCounter { Value = 42 });
                try
                {
                    tx.Commit();
                }
                catch (DurabilityException)
                {
                    // refused at its start: the write already landed
                }
            }

            dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(5));
            dbe.SimulateHardCrash();
        }

        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = Open(scope2);
            using var tx = dbe.CreateReadOnlyTransaction();
            Assert.That(tx.Open(counter).Read(PaCounterArch.Counter).Value, Is.EqualTo(1),
                $"{Marker}: a write made after the stop, with no WAL record, came back");
        }
    }

    /// <summary>
    /// #1271, review: a stop latched while a checkpoint cycle is writing stops the cycle before its next write wave. The cycle that began before the stop
    /// passed its first check and went on writing every page it had collected, post-stop writes among them; it stops now, uncovered, CheckpointLSN where it
    /// was. Staged with one-page waves, the stop latched by the first page a wave writes after the metadata hook.
    /// </summary>
    [Test]
    [VerifiesRule("AP-03")]
    [CancelAfter(30_000)]
    public void AStopLatchedMidCycle_StopsTheCycleBeforeItsNextWave()
    {
        using var scope = _serviceProvider.CreateScope();
        var dbe = Open(scope);
        using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
        {
            for (var i = 0; i < 2_000; i++)
            {
                tx.Spawn<PaWalletArch>(PaWalletArch.Wallet.Set(new PaWallet { Credits = i }));
            }

            tx.Commit();
        }

        var cm = dbe.CheckpointManager;
        var before = cm.CheckpointLsn;
        cm.WriteBatchMaxPagesForTest = 1;
        cm.WriteSubBatchPagesForTest = 1;
        // The hook saves its own pages: the waves are what follows it.
        var hook = cm.PersistDurableMetadataHook;
        var hookDone = false;
        cm.PersistDurableMetadataHook = () =>
        {
            hook?.Invoke();
            hookDone = true;
        };
        var written = 0;
        dbe.MMF.PageWriteInterceptor = _ =>
        {
            if (hookDone && Interlocked.Increment(ref written) == 1)
            {
                dbe.LatchCommitPipelineFailure(0, new InvalidOperationException("staged: a commit's publish failed past its append"));
            }
        };
        try
        {
            // On this thread, so the cycle has ended when the counts are read: the latch wakes a forced waiter at once.
            cm.RunCheckpointCycle(dbe.WalManager.DurableLsn, Typhon.Profiler.CheckpointReason.Forced);
        }
        finally
        {
            dbe.MMF.PageWriteInterceptor = null;
            cm.PersistDurableMetadataHook = hook;
        }

        Assert.That(dbe.CommitPipelineFailure, Is.Not.Null, "premise: the stop latched inside the cycle's waves");
        Assert.That((written, cm.CheckpointLsn), Is.EqualTo((1, before)), $"{Marker}: the cycle went on writing after the stop");
    }

    /// <param name="hardCrash">The engine is then torn down as a power cut; otherwise it is disposed, and must close as a crash on its own.</param>
    [Test]
    [VerifiesRule("AP-03")]
    [VerifiesRule("CK-13")]
    [CancelAfter(30_000)]
    public void AThrowAfterTheAppend_KeepsTheCommit_StopsTheEngine_AndARestartRecoversIt([Values] bool hardCrash)
    {
        EntityId id;
        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = Open(scope1);
            using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                id = tx.Spawn<PaWalletArch>(PaWalletArch.Wallet.Set(new PaWallet { Credits = 50 }));
                tx.Commit();
            }

            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(5)), Is.True, "premise: the spawn is checkpointed");

            dbe.CommitAfterAppendProbe = () => throw new InvalidOperationException("injected after the append");
            Exception thrown = null;
            long recordLsn;
            using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                tx.OpenMut(id).Set(PaWalletArch.Wallet, new PaWallet { Credits = 99 });
                try
                {
                    tx.Commit();
                }
                catch (Exception e)
                {
                    thrown = e;
                }
                finally
                {
                    dbe.CommitAfterAppendProbe = null;
                }

                recordLsn = dbe.DurabilityLog.LastAppendedLsn;
                Assert.That(thrown, Is.InstanceOf<CommitPublishIncompleteException>(),
                    $"{Marker}: a throw after the append surfaced as {thrown?.GetType().Name ?? "nothing"}");
                Assert.That(tx.State, Is.EqualTo(Transaction.TransactionState.Committed), $"{Marker}: the appended commit is not Committed (AP-02)");
            }

            Assert.That(dbe.CommitPipelineFailure, Is.Not.Null, $"{Marker}: the engine goes on committing over a partial publish");
            using (var later = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                later.OpenMut(id).Set(PaWalletArch.Wallet, new PaWallet { Credits = 7 });
                Assert.Throws<DurabilityException>(() => later.Commit(), $"{Marker}: a later commit is accepted by a stopped engine");
            }

            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(5)), Is.False,
                "the stopped engine's checkpointer writes no more (#1271)");
            Assert.That(dbe.CheckpointManager.CheckpointLsn, Is.LessThan(recordLsn),
                $"{Marker}: the checkpoint passed the records of a commit whose publish did not complete");

            if (hardCrash)
            {
                dbe.SimulateHardCrash();
            }
        }

        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = Open(scope2);
            Assert.That(dbe.CommitPipelineFailure, Is.Null, "a reopened engine commits again");
            using var tx = dbe.CreateReadOnlyTransaction();
            Assert.That(tx.Open(id).Read(PaWalletArch.Wallet).Credits, Is.EqualTo(99), $"{Marker}: the appended commit did not come back after the restart");
        }
    }
}
