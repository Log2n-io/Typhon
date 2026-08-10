using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// #389 — ComponentCollection crash-safety. Test plan 08 row A1.7.
//
// Two archetypes, chosen so the four storage homes a collection can live in are all reachable:
//   CcVersionedArch — pure Versioned  ⇒ FLAT: the value lives in a content chunk hung off a revision chain.
//   CcSingleArch    — pure SV         ⇒ CLUSTER-eligible: the value lives in the cluster's SoA, and the tick fence emits it columnarly.
// ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>A Versioned component carrying a collection — the flat, revision-chain storage home.</summary>
[Component("Typhon.Schema.UnitTest.CcVersioned", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct CcVersioned
{
    /// <summary>The variable-size payload under test.</summary>
    [Field]
    public ComponentCollection<int> Items;

    /// <summary>A plain scalar beside it — a control that must survive whatever the collection does.</summary>
    public int Seq;
}

/// <summary>A SingleVersion component carrying a collection — the cluster-SoA storage home.</summary>
[Component("Typhon.Schema.UnitTest.CcSingle", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct CcSingle
{
    /// <summary>The variable-size payload under test.</summary>
    [Field]
    public ComponentCollection<int> Items;

    /// <summary>A plain scalar beside it — a control that must survive whatever the collection does.</summary>
    public int Seq;
}

[Archetype]
internal class CcVersionedArch : Archetype<CcVersionedArch>
{
    public static readonly Comp<CcVersioned> C = Register<CcVersioned>();
}

[Archetype]
internal class CcSingleArch : Archetype<CcSingleArch>
{
    public static readonly Comp<CcSingle> C = Register<CcSingle>();
}

/// <summary>
/// Crash-durability of <c>ComponentCollection</c> content (#389), and the LOG-06 guarantee its fix depends on.
/// </summary>
/// <remarks>
/// <para>
/// A real on-disk WAL, not <c>InMemoryWalFileIO</c>: the rule under test constrains what the EMITTER writes, so the assertions read the bytes back off disk
/// through the engine's own reader (<see cref="WalScanner"/>). That is the distinction that retired LOG-06's previous coverage — see the rule's
/// <c>verified:</c> note and #703.
/// </para>
/// <para>
/// <b>Element count varies per entity</b> (<c>(i % 4) + 1</c>), matching <c>ComponentCollectionMatrixTests</c> and <c>PayloadPayloadWorkload</c>: a constant
/// length lets a defect that hands every entity the SAME buffer read as correct, whereas a varying length fails on the count — earlier, and localised.
/// </para>
/// </remarks>
[TestFixture]
internal sealed class CollectionDurabilityTests
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
            const string prefix = "Ccd_";
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
        var root = Path.Combine(Path.GetTempPath(), "Typhon.Tests", nameof(CollectionDurabilityTests));
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

    // ── the per-entity model ────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static int ElementCountOf(int i) => (i % 4) + 1;

    private static int ElementValue(int i, int element) => (i * 100) + element + 1;

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // Phase 1 — LOG-06: no bufferId reaches the log
    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A real commit of collection-bearing components must put no <c>bufferId</c> on the wire (LOG-06).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rule's own <c>verified:</c> field spells out what a genuine verifier has to do — "drive the real emit path
    /// (CommitBatchBuilder/RecordCodec via a live commit) and assert no page index / chunk id / bufferId / chain topology appears in the emitted bytes" — because
    /// the two fixtures that claimed the rule until #703 hand-built their records and so could never observe the emitter. This is that verifier.
    /// </para>
    /// <para>
    /// It covers both emitters. <c>BuildCommitBatch</c> writes per-entity Slot records and passes the table's packed handle ranges to the codec; the tick
    /// fence's columnar path (#559) bulk-copies component columns straight out of the cluster page, where a handle survives exactly as the scalar beside it
    /// does. Zeroing the columnar path is not merely hygiene — the fence's expansion is the LATEST value recovery sees for the entity, so an unzeroed handle
    /// would be written back into the recovered row and dangle.
    /// </para>
    /// </remarks>
    [Test]
    [CancelAfter(20_000)]
    [VerifiesRule("LOG-06")]
    public void Commit_WithCollections_PutsNoBufferIdOnTheWire()
    {
        List<HandleWindow> windows;
        using (var scope = _serviceProvider.CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            RegisterBoth(dbe);
            var (versioned, single) = SeedBoth(dbe);

            // Drive the COLUMNAR emitter too. A spawn alone does not: the fence emits only for entity slots its own tick marked dirty, and an archetype whose
            // FenceWrittenSlots union is empty short-circuits before any block is built. So touch the SingleVersion entities under the default TickFence
            // discipline first — that is the write whose fence record carries the SoA bytes, collection handle included.
            TouchSingleVersionEntities(dbe, single);
            dbe.WriteTickFence(1);

            // The precondition that keeps the assertion from being vacuous: a collection with no elements allocates no buffer, so its handle is legitimately
            // zero and "all handles are zero" would hold on a database where nothing was ever stored.
            AssertLiveCollectionsAreNonEmpty(dbe, versioned);
            windows = HandleWindowsOf(dbe, versioned[0].ArchetypeId, single[0].ArchetypeId);
        }

        AssertNoBufferIdOnTheWire(WalScanner.ScanAll(_walDir), windows, requireBothEmitters: true);
    }

    /// <summary>
    /// The genuineness proof for <see cref="Commit_WithCollections_PutsNoBufferIdOnTheWire"/>: a non-zero handle in the emitted bytes must be REJECTED.
    /// </summary>
    /// <remarks>
    /// It drives the verifier's own assertion — <see cref="AssertNoBufferIdOnTheWire"/> — with a record whose handle window holds a live-looking bufferId,
    /// which is exactly the pre-fix production shape (`bufferId bytes IN THE WAL = 1`). Without this, the verifier could assert something weaker than the rule
    /// and nobody would know, which is the failure #703 catalogued.
    /// </remarks>
    [Test]
    [CancelAfter(15_000)]
    [RuleMutant("LOG-06")]
    public void Log06Verifier_RejectsABufferIdOnTheWire() =>
        RuleMutants.AssertDetects("LOG-06", BufferIdOnTheWireMarker, static () =>
        {
            // A Slot payload laid out like CcVersioned: handle at offset 0, scalar at offset 4. The handle holds bufferId 7 — a dangling reference in waiting.
            var payload = new byte[8];
            BitConverter.TryWriteBytes(payload.AsSpan(0), 7);
            BitConverter.TryWriteBytes(payload.AsSpan(4), 0x11223344);

            var record = new WalScanner.Record
            {
                Lsn = 1, Kind = RecordKind.Slot, Op = (byte)SlotOp.Upsert, EntityId = 0x10001, SlotIndex = 0, Payload = payload,
            };

            AssertNoBufferIdOnTheWire([record], [new HandleWindow(RoutingId: 1, SlotIndex: 0, Offset: 0, Length: 4, TableName: "MutantTable")]);
        });

    /// <summary>
    /// Where one collection field's handle sits inside the value bytes a Slot record carries, for one <c>(routingId, slot)</c> component identity.
    /// </summary>
    /// <remarks>
    /// Keyed on the routing id as well as the slot, because two archetypes routinely put their collection at the same slot — both fixtures here do — and a
    /// slot-only match would attribute one table's record to the other. That is the same <c>(routingId, slot)</c> pair LOG-06 makes the durable identity, and
    /// resolving it the way the engine does keeps the verifier honest about which table it just inspected.
    /// </remarks>
    private readonly record struct HandleWindow(ushort RoutingId, ushort SlotIndex, int Offset, int Length, string TableName);

    /// <summary>The marker that identifies the verifier's own rejection — see <see cref="RuleMutants.AssertDetects"/> on why it must be distinctive.</summary>
    private const string BufferIdOnTheWireMarker = "bufferId on the wire";

    /// <summary>
    /// Asserts that every Slot record's collection-handle bytes are zero, and that the assertion was not vacuous.
    /// </summary>
    /// <remarks>
    /// Shared by the verifier and its mutant, deliberately: a mutant that exercised a different assertion would prove nothing about the one that ships.
    /// </remarks>
    private static void AssertNoBufferIdOnTheWire(IReadOnlyList<WalScanner.Record> records, IReadOnlyList<HandleWindow> windows, bool requireBothEmitters = false)
    {
        var fromCommitBatch = 0;
        var fromFenceBlock = 0;
        foreach (var r in records)
        {
            if (r.Kind != RecordKind.Slot)
            {
                continue;
            }

            foreach (var w in windows)
            {
                // (routingId, slot) is the wire identity — the routing id lives in the EntityId's low 16 bits, exactly as RecoveryApplier resolves it.
                if ((ushort)r.EntityId != w.RoutingId || r.SlotIndex != w.SlotIndex || r.Payload.Length < w.Offset + w.Length)
                {
                    continue;
                }

                if (r.FromFenceBlock)
                {
                    fromFenceBlock++;
                }
                else
                {
                    fromCommitBatch++;
                }

                var handle = BitConverter.ToInt32(r.Payload, w.Offset);
                if (handle != 0)
                {
                    Assert.Fail(
                        $"LOG-06: {BufferIdOnTheWireMarker} — record at LSN {r.Lsn} for entity 0x{r.EntityId:X} ({w.TableName}, slot {w.SlotIndex}) carries "
                        + $"bufferId {handle} at payload offset {w.Offset}. A handle and the buffer it points at sit on different durability timelines, so a "
                        + $"logged handle is a dangling reference after any crash the buffer did not survive (#389, DC-01). Record: {r}");
                }
            }
        }

        Assert.That(fromCommitBatch + fromFenceBlock, Is.GreaterThan(0),
            "no Slot record carrying a collection field was found — the LOG-06 assertion inspected nothing, so its green result would mean nothing");

        // Both emitters or neither. The two zero handles by different mechanisms — the per-entity path hands packed ranges to the codec, the columnar path
        // rewrites the copied SoA bytes — so a run that only observed one would report confidence in a mechanism it never exercised.
        if (requireBothEmitters)
        {
            Assert.That(fromCommitBatch, Is.GreaterThan(0), "no per-entity Slot record was inspected — CommitBatchBuilder's handle zeroing went unverified");
            Assert.That(fromFenceBlock, Is.GreaterThan(0),
                "no FenceBlock-expanded record was inspected — the columnar emitter's handle zeroing went unverified. That path bulk-copies component columns "
                + "straight out of the cluster page, so it is the one that most easily carries a handle through.");
        }
    }

    // ── shared engine scaffolding ───────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static void RegisterBoth(DatabaseEngine dbe)
    {
        dbe.RegisterComponentFromAccessor<CcVersioned>();
        dbe.RegisterComponentFromAccessor<CcSingle>();
        dbe.InitializeArchetypes();
    }

    private static (EntityId[] Versioned, EntityId[] Single) SeedBoth(DatabaseEngine dbe)
    {
        var versioned = new EntityId[EntityCount];
        var single = new EntityId[EntityCount];

        using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
        {
            using (var tx = uow.CreateTransaction(CommitDiscipline.Commit))
            {
                for (var i = 0; i < EntityCount; i++)
                {
                    var v = new CcVersioned { Seq = i };
                    FillCollection(tx, ref v.Items, i);
                    versioned[i] = tx.Spawn<CcVersionedArch>(CcVersionedArch.C.Set(in v));

                    var s = new CcSingle { Seq = i };
                    FillCollection(tx, ref s.Items, i);
                    single[i] = tx.Spawn<CcSingleArch>(CcSingleArch.C.Set(in s));
                }

                tx.Commit();
            }

            uow.Flush();
        }

        return (versioned, single);
    }

    /// <summary>
    /// Fills a collection on the LOCAL struct, before <c>Spawn</c>.
    /// </summary>
    /// <remarks>
    /// The idiom <c>ClusterComponentCollectionTests</c> and <c>PayloadPayloadWorkload</c> both use. Spawning first and then writing through
    /// <c>OpenMut(id).Write(...)</c> in the same transaction NREs inside <c>Transaction.BuildCommitBatch</c>'s Commit-staged path — see #713.
    /// </remarks>
    private static void FillCollection(Transaction tx, ref ComponentCollection<int> field, int i)
    {
        using var cca = tx.CreateComponentCollectionAccessor(ref field);
        for (var el = 0; el < ElementCountOf(i); el++)
        {
            cca.Add(ElementValue(i, el));
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // Phase 3 — Option B: a commit emits the collection's full content
    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Decoding a real commit's CollectionDelta records must reproduce every entity's collection exactly (AC 3.1).
    /// </summary>
    /// <remarks>
    /// Driven through a live transaction rather than a hand-built batch, for the same reason the LOG-06 verifier is: the property under test is what the
    /// EMITTER produces. <c>CommitBatchBuilder.AddCollectionDelta</c> had zero production callers before this — the record kind, its codec and its wire layout
    /// all existed and had property tests, and nothing ever wrote one.
    /// </remarks>
    [Test]
    [CancelAfter(20_000)]
    public void Commit_WithCollections_EmitsTheFullContent()
    {
        EntityId[] versioned;
        EntityId[] single;
        using (var scope = _serviceProvider.CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            RegisterBoth(dbe);
            (versioned, single) = SeedBoth(dbe);
        }

        var records = WalScanner.ScanAll(_walDir);
        AssertFoldsToTheModel(records, versioned, "Versioned (flat)");
        AssertFoldsToTheModel(records, single, "SingleVersion (cluster)");
    }

    /// <summary>
    /// The tick fence must emit collection content too — the columnar path is a separate emitter with its own hole.
    /// </summary>
    /// <remarks>
    /// The fence's record carries the cluster's SoA bytes with every collection handle zeroed, so on its own it RESTORES a collection as empty. That is worse
    /// than the bug it replaces for one specific shape: content that was already checkpointed, and therefore already safe, gets erased by replaying a window
    /// that only touched a scalar. The content has to ride alongside, which is why the fence emits its own CollectionDelta batch.
    /// </remarks>
    [Test]
    [CancelAfter(20_000)]
    public void Fence_WithCollections_EmitsTheFullContent()
    {
        EntityId[] single;
        int beforeFence;
        using (var scope = _serviceProvider.CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            RegisterBoth(dbe);
            (_, single) = SeedBoth(dbe);
            beforeFence = WalScanner.ScanAll(_walDir).Count(static r => r.Kind == RecordKind.CollectionDelta);

            TouchSingleVersionEntities(dbe, single);
            dbe.WriteTickFence(1);
        }

        var records = WalScanner.ScanAll(_walDir);
        var fenceDeltas = records.Where(r => r.Kind == RecordKind.CollectionDelta && r.IsFence).ToList();
        Assert.That(fenceDeltas, Is.Not.Empty,
            "the tick fence emitted no CollectionDelta — its zeroed handles would restore every one of these collections as empty");
        Assert.That(records.Count(static r => r.Kind == RecordKind.CollectionDelta), Is.GreaterThan(beforeFence),
            "the fence must add content records of its own, not merely reuse the spawn commit's");

        // The fold over the WHOLE window — spawn commit plus fence — still has to land on the model: a later Clear discards the earlier content and re-appends
        // the same elements, so repetition converges rather than accumulating.
        AssertFoldsToTheModel(records, single, "SingleVersion (cluster, after fence)");
    }

    /// <summary>
    /// A component with NO collection field must emit byte-identical records to before the change (AC 3.3).
    /// </summary>
    /// <remarks>
    /// The common case must not pay for the rare one. Stated as "not one CollectionDelta record exists" rather than as a byte comparison against a golden
    /// file: the emitters are shared, so the only way a collection-free component could change shape is by emitting a record kind it never emitted, and that
    /// is both the precise claim and the one that stays readable when an unrelated field is added to the fixture component.
    /// </remarks>
    [Test]
    [CancelAfter(20_000)]
    public void Commit_WithoutCollections_EmitsNoCollectionDelta()
    {
        using (var scope = _serviceProvider.CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompA>();
            dbe.InitializeArchetypes();

            using var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate);
            using (var tx = uow.CreateTransaction(CommitDiscipline.Commit))
            {
                for (var i = 0; i < EntityCount; i++)
                {
                    tx.Spawn<CompAArch>(CompAArch.A.Set(new CompA(i)));
                }

                tx.Commit();
            }

            uow.Flush();
            dbe.WriteTickFence(1);
        }

        var records = WalScanner.ScanAll(_walDir);
        Assert.That(records, Is.Not.Empty, "the commit must have reached the log, or this assertion is vacuous");
        Assert.That(records.FindAll(static r => r.Kind == RecordKind.CollectionDelta), Is.Empty,
            "a component with no collection field must emit no CollectionDelta record — the common case does not pay for the rare one");
    }

    /// <summary>
    /// LOG-07: a CollectionDelta must never precede the Spawn of the entity it belongs to (AC 3.2).
    /// </summary>
    /// <remarks>
    /// <c>AddCollectionDelta</c> already buckets as <c>1</c>, the same bucket as <c>AddSlot</c>, so the builder places it after all Spawns by construction.
    /// This verifies that rather than rebuilding it — the emission added a caller to a bucketing scheme that until now had none, which is exactly when an
    /// assumed invariant is worth checking against the bytes.
    /// </remarks>
    [Test]
    [CancelAfter(20_000)]
    public void CollectionDeltas_FollowTheirSpawn_InBatchOrder()
    {
        using (var scope = _serviceProvider.CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            RegisterBoth(dbe);
            SeedBoth(dbe);
        }

        var records = WalScanner.ScanAll(_walDir);
        var spawnLsn = new Dictionary<long, long>();
        foreach (var r in records)
        {
            if (r.Kind == RecordKind.Lifecycle && r.Op == (byte)LifecycleOp.Spawn)
            {
                spawnLsn[r.EntityId] = r.Lsn;
            }
        }

        var checkedDeltas = 0;
        foreach (var r in records)
        {
            if (r.Kind != RecordKind.CollectionDelta || !spawnLsn.TryGetValue(r.EntityId, out var born))
            {
                continue;
            }

            checkedDeltas++;
            Assert.That(r.Lsn, Is.GreaterThan(born),
                $"LOG-07: CollectionDelta at LSN {r.Lsn} precedes entity 0x{r.EntityId:X}'s Spawn at LSN {born} — apply would fold content for an entity "
                + "that does not exist yet");
        }

        Assert.That(checkedDeltas, Is.GreaterThan(0), "no CollectionDelta was matched to a Spawn — the ordering assertion inspected nothing");
    }

    /// <summary>
    /// Folds the emitted CollectionDelta records the way recovery does and requires the result to equal the per-entity model.
    /// </summary>
    /// <remarks>
    /// This is the test-side twin of <c>RecoveryDriver</c>'s fold, and it is deliberately a re-implementation rather than a call into it: it has to be
    /// possible for the two to disagree, or the round-trip proves only that one piece of code is self-consistent.
    /// </remarks>
    private static void AssertFoldsToTheModel(IReadOnlyList<WalScanner.Record> records, IReadOnlyList<EntityId> ids, string shape)
    {
        var folded = new Dictionary<(long Entity, ushort Slot, ushort Field), List<int>>();
        foreach (var r in records)
        {
            if (r.Kind != RecordKind.CollectionDelta)
            {
                continue;
            }

            var key = (r.EntityId, r.SlotIndex, r.FieldId);
            if (!folded.TryGetValue(key, out var elements))
            {
                folded[key] = elements = [];
            }

            switch ((CollectionOp)r.Op)
            {
                case CollectionOp.Clear:
                    elements.Clear();
                    break;
                case CollectionOp.Append:
                    Assert.That(r.Payload, Has.Length.EqualTo(sizeof(int)), $"{shape}: an Append element must be one int wide");
                    elements.Add(BitConverter.ToInt32(r.Payload));
                    break;
                default:
                    Assert.Fail($"{shape}: Option B emits only Clear and Append, got {(CollectionOp)r.Op}");
                    break;
            }
        }

        for (var i = 0; i < ids.Count; i++)
        {
            var match = folded.Where(kv => kv.Key.Entity == (long)ids[i].RawValue).ToList();
            Assert.That(match, Has.Count.EqualTo(1), $"{shape}: entity {ids[i]} must have exactly one folded collection field, got {match.Count}");

            var expected = new int[ElementCountOf(i)];
            for (var el = 0; el < expected.Length; el++)
            {
                expected[el] = ElementValue(i, el);
            }

            Assert.That(match[0].Value, Is.EqualTo(expected).AsCollection,
                $"{shape}: entity {ids[i]}'s folded content must equal what was committed — the emitted records ARE the collection after a crash");
        }
    }

    /// <summary>Writes a scalar on every SingleVersion entity under the TickFence discipline, so the next fence has dirty slots to emit columnarly.</summary>
    private static void TouchSingleVersionEntities(DatabaseEngine dbe, IReadOnlyList<EntityId> single)
    {
        using var tx = dbe.CreateQuickTransaction();
        foreach (var id in single)
        {
            tx.OpenMut(id).Write(CcSingleArch.C).Seq += 1000;
        }

        Assert.That(tx.Commit(), Is.True, "the tick-fence-discipline update must commit");
    }

    /// <summary>
    /// The handle windows of both fixtures' collection fields, read out of the live schema rather than hardcoded.
    /// </summary>
    /// <remarks>
    /// The routing ids come from actual spawned <see cref="EntityId"/>s: an <c>EntityId</c> carries the per-DB ROUTING id in its low bits, which is the space
    /// a Slot record's identity is resolved in — <c>ArchetypeMetadata.ArchetypeId</c> is the per-process CATALOG id and is a different space that happens to
    /// coincide often enough to hide the mistake.
    /// </remarks>
    private static List<HandleWindow> HandleWindowsOf(DatabaseEngine dbe, ushort versionedRoutingId, ushort singleRoutingId)
    {
        var windows = new List<HandleWindow>();
        AddWindows(dbe.GetComponentTable<CcVersioned>(), versionedRoutingId, nameof(CcVersioned));
        AddWindows(dbe.GetComponentTable<CcSingle>(), singleRoutingId, nameof(CcSingle));
        return windows;

        void AddWindows(ComponentTable table, ushort routingId, string name)
        {
            var slot = (ushort)dbe.GetMetaByRouting(routingId).GetSlot(ArchetypeRegistry.GetComponentTypeId(table.Definition.POCOType));
            foreach (var f in table.CollectionFields)
            {
                windows.Add(new HandleWindow(routingId, slot, f.OffsetInComponentStorage, f.HandleSize, name));
            }
        }
    }

    private static void AssertLiveCollectionsAreNonEmpty(DatabaseEngine dbe, IReadOnlyList<EntityId> versioned)
    {
        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < versioned.Count; i++)
        {
            var v = tx.Open(versioned[i]).Read(CcVersionedArch.C);
            using var cca = tx.CreateComponentCollectionAccessor(ref v.Items);
            Assert.That(cca.ElementCount, Is.EqualTo(ElementCountOf(i)),
                $"{versioned[i]}: the live collection must hold its elements, or a zeroed handle on the wire proves nothing");
        }
    }
}
