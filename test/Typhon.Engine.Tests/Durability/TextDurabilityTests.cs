using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.IO;
using System.Runtime.InteropServices;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// #1094 — String1024 crash-durability. A text lives in a chunk of the text segment; the component holds the chunk id. The id never reaches the log
// (LOG-06): the text rides behind every Slot record as a CollectionDelta Clear + one Append, and recovery writes it into a fresh chunk (DC-01, TXT-03).
//   TdVersionedArch — Versioned: the value is a revision's content chunk, logged per commit.
//   TdSingleArch    — SingleVersion: the value is the cluster SoA, logged per commit under the Commit discipline, per fence under TickFence.
// ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>A Versioned component with a text.</summary>
[Component("Typhon.Schema.UnitTest.TdVersioned", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct TdVersioned
{
    /// <summary>A scalar control.</summary>
    [Field] public int Seq;

    /// <summary>The text under test.</summary>
    [Field] public String1024 Text;
}

/// <summary>A SingleVersion component with a text.</summary>
[Component("Typhon.Schema.UnitTest.TdSingle", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct TdSingle
{
    /// <summary>A scalar control.</summary>
    [Field] public int Seq;

    /// <summary>The text under test.</summary>
    [Field] public String1024 Text;
}

[Archetype]
internal class TdVersionedArch : Archetype<TdVersionedArch>
{
    public static readonly Comp<TdVersioned> C = Register<TdVersioned>();
}

[Archetype]
internal class TdSingleArch : Archetype<TdSingleArch>
{
    public static readonly Comp<TdSingle> C = Register<TdSingle>();
}

[TestFixture]
internal sealed class TextDurabilityTests
{
    private const int EntityCount = 8;

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
            const string prefix = "Txd_";
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
        var root = Path.Combine(TestStorage.Root, nameof(TextDurabilityTests));
        _dbDir = Path.Combine(root, CurrentDatabaseName, "db");
        _walDir = Path.Combine(root, CurrentDatabaseName, "wal");
        Directory.CreateDirectory(_dbDir);
        Directory.CreateDirectory(_walDir);

        var services = new ServiceCollection();
        services
            .AddLogging(b =>
            {
                b.AddSimpleConsole();
                b.SetMinimumLevel(LogLevel.Warning);
            })
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

    // A text whose length varies per entity: a defect that handed every entity the same chunk fails on the content, not only on a count.
    private static string TextOf(int i, int round) => $"r{round}-e{i}-" + new string((char)('a' + (i % 26)), 10 + (i * 37 % 900));

    private static void Register(DatabaseEngine dbe)
    {
        dbe.RegisterComponentFromAccessor<TdVersioned>();
        dbe.RegisterComponentFromAccessor<TdSingle>();
        dbe.InitializeArchetypes();
    }

    private static (EntityId[] Versioned, EntityId[] Single) Spawn(DatabaseEngine dbe)
    {
        var versioned = new EntityId[EntityCount];
        var single = new EntityId[EntityCount];
        using var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate);
        using (var tx = uow.CreateTransaction(CommitDiscipline.Commit))
        {
            for (var i = 0; i < EntityCount; i++)
            {
                versioned[i] = tx.Spawn<TdVersionedArch>(TdVersionedArch.C.Set(new TdVersioned { Seq = i }));
                single[i] = tx.Spawn<TdSingleArch>(TdSingleArch.C.Set(new TdSingle { Seq = i }));
            }

            Assert.That(tx.Commit(), Is.True);
        }

        uow.Flush();
        return (versioned, single);
    }

    /// <summary>Sets every entity's text in one Commit-discipline transaction, durable at its flush.</summary>
    private static void SetTexts(DatabaseEngine dbe, EntityId[] versioned, EntityId[] single, int round, CommitDiscipline discipline)
    {
        using var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate);
        using (var tx = uow.CreateTransaction(discipline))
        {
            for (var i = 0; i < EntityCount; i++)
            {
                var ev = tx.OpenMut(versioned[i]);
                var v = ev.Read(TdVersionedArch.C);
                ev.SetText(TdVersionedArch.C, ref v, ref v.Text, TextOf(i, round));

                var es = tx.OpenMut(single[i]);
                var s = es.Read(TdSingleArch.C);
                es.SetText(TdSingleArch.C, ref s, ref s.Text, TextOf(i, round));
            }

            Assert.That(tx.Commit(), Is.True);
        }

        uow.Flush();
    }

    private static void AssertTexts(DatabaseEngine dbe, EntityId[] versioned, EntityId[] single, Func<int, string> expected)
    {
        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < EntityCount; i++)
        {
            var ev = tx.Open(versioned[i]);
            var v = ev.Read(TdVersionedArch.C);
            Assert.That(v.Seq, Is.EqualTo(i), $"versioned {i} scalar");
            Assert.That(ev.ReadText(TdVersionedArch.C, in v.Text), Is.EqualTo(expected(i)), $"versioned {i} text");

            var es = tx.Open(single[i]);
            var s = es.Read(TdSingleArch.C);
            Assert.That(s.Seq, Is.EqualTo(i), $"single {i} scalar");
            Assert.That(es.ReadText(TdSingleArch.C, in s.Text), Is.EqualTo(expected(i)), $"single {i} text");
        }
    }

    [Test]
    [CancelAfter(20_000)]
    public void CommittedTexts_SurviveAHardCrash()
    {
        EntityId[] versioned, single;
        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = scope1.ServiceProvider.GetRequiredService<DatabaseEngine>();
            Register(dbe);
            (versioned, single) = Spawn(dbe);
            SetTexts(dbe, versioned, single, 1, CommitDiscipline.Commit);
            SetTexts(dbe, versioned, single, 2, CommitDiscipline.Commit);
            dbe.SimulateHardCrash();
        }

        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
            Register(dbe);
            Assert.That(dbe.LastOpenRanCrashRecovery, Is.True);
            AssertTexts(dbe, versioned, single, i => TextOf(i, 2));
        }
    }

    [Test]
    [CancelAfter(20_000)]
    public void TickFenceTexts_SurviveAHardCrash()
    {
        EntityId[] versioned, single;
        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = scope1.ServiceProvider.GetRequiredService<DatabaseEngine>();
            Register(dbe);
            (versioned, single) = Spawn(dbe);
            SetTexts(dbe, versioned, single, 1, CommitDiscipline.TickFence);
            dbe.WriteTickFence(1);
            dbe.SimulateHardCrash();
        }

        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
            Register(dbe);
            AssertTexts(dbe, versioned, single, i => TextOf(i, 1));
        }
    }

    [Test]
    [CancelAfter(20_000)]
    public void ClearedTexts_StayClearedAcrossAHardCrash()
    {
        EntityId[] versioned, single;
        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = scope1.ServiceProvider.GetRequiredService<DatabaseEngine>();
            Register(dbe);
            (versioned, single) = Spawn(dbe);
            SetTexts(dbe, versioned, single, 1, CommitDiscipline.Commit);
            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                using (var tx = uow.CreateTransaction(CommitDiscipline.Commit))
                {
                    for (var i = 0; i < EntityCount; i += 2)
                    {
                        var ev = tx.OpenMut(versioned[i]);
                        var v = ev.Read(TdVersionedArch.C);
                        ev.ClearText(TdVersionedArch.C, ref v, ref v.Text);

                        var es = tx.OpenMut(single[i]);
                        var s = es.Read(TdSingleArch.C);
                        es.ClearText(TdSingleArch.C, ref s, ref s.Text);
                    }

                    Assert.That(tx.Commit(), Is.True);
                }

                uow.Flush();
            }

            dbe.SimulateHardCrash();
        }

        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
            Register(dbe);
            AssertTexts(dbe, versioned, single, i => i % 2 == 0 ? string.Empty : TextOf(i, 1));
        }
    }

    /// <summary>
    /// Texts a checkpoint consolidated survive a crash, the texts written after it are replayed into fresh chunks, the chunks those rows held before the
    /// replay are swept, and nothing recovered shares a chunk with a text written after the open (TXT-03).
    /// </summary>
    [Test]
    [VerifiesRule("TXT-03")]
    [CancelAfter(30_000)]
    public void CheckpointedTexts_SurviveAHardCrash_AndAreNotReissued()
    {
        EntityId[] versioned, single;
        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = scope1.ServiceProvider.GetRequiredService<DatabaseEngine>();
            Register(dbe);
            (versioned, single) = Spawn(dbe);
            SetTexts(dbe, versioned, single, 1, CommitDiscipline.Commit);
            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True);

            // Half the entities change after the checkpoint: their rows are replayed, the other half come back from the data file as they are.
            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                using (var tx = uow.CreateTransaction(CommitDiscipline.Commit))
                {
                    for (var i = 1; i < EntityCount; i += 2)
                    {
                        var es = tx.OpenMut(single[i]);
                        var s = es.Read(TdSingleArch.C);
                        es.SetText(TdSingleArch.C, ref s, ref s.Text, TextOf(i, 2));
                    }

                    Assert.That(tx.Commit(), Is.True);
                }

                uow.Flush();
            }

            dbe.SimulateHardCrash();
        }

        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
            Register(dbe);
            Func<int, string> expectedSingle = i => i % 2 == 1 ? TextOf(i, 2) : TextOf(i, 1);
            AssertSingle(dbe, single, expectedSingle);
            Assert.That(dbe.TextOrphansSweptAtOpen, Is.GreaterThan(0), "the replayed rows' pre-crash chunks are named by nothing and swept");

            // The collision probe: new texts after the open must not land in a chunk a recovered entity holds.
            EntityId[] probeV, probeS;
            (probeV, probeS) = Spawn(dbe);
            SetTexts(dbe, probeV, probeS, 9, CommitDiscipline.Commit);
            AssertSingle(dbe, single, expectedSingle);
            using (var tx = dbe.CreateQuickTransaction())
            {
                for (var i = 0; i < EntityCount; i++)
                {
                    var ev = tx.Open(versioned[i]);
                    var v = ev.Read(TdVersionedArch.C);
                    Assert.That(ev.ReadText(TdVersionedArch.C, in v.Text), Is.EqualTo(TextOf(i, 1)), $"versioned {i}");
                }
            }
        }
    }

    [Test]
    [CancelAfter(20_000)]
    public void Texts_SurviveACleanReopen()
    {
        EntityId[] versioned, single;
        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = scope1.ServiceProvider.GetRequiredService<DatabaseEngine>();
            Register(dbe);
            (versioned, single) = Spawn(dbe);
            SetTexts(dbe, versioned, single, 1, CommitDiscipline.Commit);
            SetTexts(dbe, versioned, single, 2, CommitDiscipline.TickFence);
            dbe.WriteTickFence(1);
        }

        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
            Register(dbe);
            Assert.That(dbe.LastOpenRanCrashRecovery, Is.False);
            AssertTexts(dbe, versioned, single, i => TextOf(i, 2));
            var segment = dbe.GetComponentTable<TdSingle>().TextSegment;
            Assert.That(segment, Is.SameAs(dbe.GetComponentTable<TdVersioned>().TextSegment), "one text segment per database");
        }
    }

    /// <summary>
    /// A crash leaves the values in the WAL in the old layout: the migrating open replays them through the same widening, so each gets a text chunk too
    /// (MigrateWalPayload, #1469), saved before the seal's flip commits the migration.
    /// </summary>
    [Test]
    public void ACrashOpenThatWidens_ReplaysOldLayoutValuesIntoTexts()
    {
        var ids = new EntityId[EntityCount];
        using (var scope = _serviceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<TxmSvWidenV1>();
            dbe.InitializeArchetypes();
            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                using (var t = uow.CreateTransaction(CommitDiscipline.Commit))
                {
                    for (var i = 0; i < EntityCount; i++)
                    {
                        ids[i] = t.Spawn<TxmSvWidenArch>(TxmSvWidenArch.Comp.Set(new TxmSvWidenV1 { Name = (String64)$"w{i}", After = i }));
                    }

                    Assert.That(t.Commit(), Is.True);
                }

                uow.Flush();
            }

            dbe.SimulateHardCrash();
        }

        using (var scope = _serviceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<TxmSvWidenV2>();
            dbe.InitializeArchetypes();
            Assert.That(dbe.LastOpenRanCrashRecovery, Is.True);
            using var t = dbe.CreateQuickTransaction();
            for (var i = 0; i < EntityCount; i++)
            {
                var e = t.Open(ids[i]);
                var v = e.Read(TxmSvWidenV2Arch.Comp);
                Assert.That(v.After, Is.EqualTo(i));
                Assert.That(e.ReadText(TxmSvWidenV2Arch.Comp, in v.Name), Is.EqualTo($"w{i}"), $"{i}: replayed and widened");
            }

            var segment = dbe.TextSegment;
            Assert.That(segment.AllocatedChunkCount - segment.QuarantinedFreeCount - 1, Is.EqualTo(EntityCount), "each replayed value has one text");
        }

        // And the migrated texts are durable past the seal.
        using (var scope = _serviceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<TxmSvWidenV2>();
            dbe.InitializeArchetypes();
            using var t = dbe.CreateQuickTransaction();
            var e = t.Open(ids[3]);
            var v = e.Read(TxmSvWidenV2Arch.Comp);
            Assert.That(e.ReadText(TxmSvWidenV2Arch.Comp, in v.Name), Is.EqualTo("w3"));
        }
    }

    private static void AssertSingle(DatabaseEngine dbe, EntityId[] single, Func<int, string> expected)
    {
        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < EntityCount; i++)
        {
            var es = tx.Open(single[i]);
            var s = es.Read(TdSingleArch.C);
            Assert.That(es.ReadText(TdSingleArch.C, in s.Text), Is.EqualTo(expected(i)), $"single {i}");
        }
    }
}
