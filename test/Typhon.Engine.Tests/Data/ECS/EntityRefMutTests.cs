using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// #997: read-only-ness is a property of the handle's TYPE. <see cref="EntityRef"/> (from <c>Open</c> / <c>TryOpen</c>) has no write member;
/// <see cref="EntityRefMut"/> (from <c>OpenMut</c> / <c>TryOpenMut</c>) has them and converts to <see cref="EntityRef"/>. Every accessor exposes the same
/// <c>Open</c> / <c>OpenMut</c> / <c>TryOpen</c> / <c>TryOpenMut</c> / <c>IsAlive</c> contract.
/// </summary>
/// <remarks>Reuses the <c>SvUnit</c> / <c>VUnit</c> / <c>MixedUnit</c> archetypes declared by <see cref="ArchetypeAccessorTests"/>.</remarks>
[NonParallelizable]
class EntityRefMutTests : TestBase<EntityRefMutTests>
{
    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<SvPosition>();
        dbe.RegisterComponentFromAccessor<SvVelocity>();
        dbe.RegisterComponentFromAccessor<VStats>();
        dbe.RegisterComponentFromAccessor<MixedSvPos>();
        dbe.RegisterComponentFromAccessor<MixedVData>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static EntityId SpawnSv(DatabaseEngine dbe, float x)
    {
        using var tx = dbe.CreateQuickTransaction();
        var pos = new SvPosition(x, 0);
        var vel = new SvVelocity(1, 2);
        var id = tx.Spawn<SvUnit>(SvUnit.Position.Set(in pos), SvUnit.Velocity.Set(in vel));
        tx.Commit();
        return id;
    }

    private static EntityId SpawnV(DatabaseEngine dbe, int hp)
    {
        using var tx = dbe.CreateQuickTransaction();
        var stats = new VStats(hp, 100);
        var id = tx.Spawn<VUnit>(VUnit.Stats.Set(in stats));
        tx.Commit();
        return id;
    }

    private static float ReadX(DatabaseEngine dbe, EntityId id)
    {
        using var tx = dbe.CreateQuickTransaction();
        return tx.Open(id).Read(SvUnit.Position).X;
    }

    /// <summary>An id no engine here can resolve: routing id 150 names no archetype in this fixture.</summary>
    private static readonly EntityId Missing = new(99999, 150);

    // ═══════════════════════════════════════════════════════════════════════
    // The type split
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>The compile-time guarantee, projected as an assertion: the read-only handle has no member that mutates.</summary>
    [Test]
    [VerifiesRule("ACCESS-01")]
    public void EntityRef_ExposesNoWriteMember_EntityRefMutDoes()
    {
        var roMembers = typeof(EntityRef).GetMembers(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.MemberType is MemberTypes.Method or MemberTypes.Property).Select(m => m.Name).ToHashSet();
        var mutMembers = typeof(EntityRefMut).GetMembers(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.MemberType is MemberTypes.Method or MemberTypes.Property).Select(m => m.Name).ToHashSet();

        // An ALLOWLIST, so a mutator added under any name (WriteRaw, SetLink...) fails here instead of slipping past a denylist.
        string[] readSurface =
        [
            "Id", "get_Id", "ArchetypeId", "get_ArchetypeId", "IsValid", "get_IsValid", "ComponentCount", "get_ComponentCount", "Read", "TryRead",
            "IsEnabled", "GetComponentName", "ReadRaw", "Equals", "GetHashCode", "ToString", "GetType", "Realm", "get_Realm",
        ];
        Assert.That(roMembers.Except(readSurface), Is.Empty, "EntityRef's public surface is read-only; review any new member here first");
        Assert.That(mutMembers, Is.SupersetOf(new[] { "Write", "Enable", "Disable" }));
        Assert.That(mutMembers, Does.Not.Contain("IsWritable"));

        // An EntityRefMut comes only from a writable open: no public constructor, and no conversion INTO it from EntityRef.
        Assert.That(typeof(EntityRefMut).GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);
        var intoMut = typeof(EntityRef).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Concat(typeof(EntityRefMut).GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(m => m.Name is "op_Implicit" or "op_Explicit" && m.ReturnType == typeof(EntityRefMut));
        Assert.That(intoMut, Is.Empty, "a conversion EntityRef -> EntityRefMut would reopen the misuse #997 closed");

        // Every read member of EntityRef is also on EntityRefMut, so a writable handle reads without converting.
        Assert.That(roMembers.Except(mutMembers), Is.Empty);

        // The runtime flag is gone: nothing about access is decided at run time any more.
        var fields = typeof(EntityRef).GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance).Select(f => f.Name);
        Assert.That(fields, Does.Not.Contain("_writable"));
    }

    [Test]
    public void ImplicitConversion_ReadOnlyViewSeesTheWrittenValue()
    {
        using var dbe = SetupEngine();
        var id = SpawnSv(dbe, 1);

        using var tx = dbe.CreateQuickTransaction();
        var mut = tx.OpenMut(id);
        mut.Write(SvUnit.Position).X = 42;
        EntityRef ro = mut;

        Assert.That(ro.IsValid, Is.True);
        Assert.That(ro.Id, Is.EqualTo(id));
        Assert.That(ReadPosX(ro), Is.EqualTo(42f), "a helper taking EntityRef accepts an OpenMut result");
    }

    private static float ReadPosX(EntityRef entity) => entity.Read(SvUnit.Position).X;

    /// <summary>
    /// No defensive copy, ever: calling a non-<c>readonly</c> member on a read-only receiver (an <c>in</c> parameter, a <c>foreach</c> variable) makes
    /// the compiler copy the whole ~200-byte handle first. So every member that does not write is <c>readonly</c> — the read path included, whose
    /// Versioned memo writes through <c>Unsafe.AsRef(in this)</c>. <c>Write</c> / <c>Enable</c> / <c>Disable</c> are the only exceptions: they
    /// mutate by design.
    /// </summary>
    [Test]
    [VerifiesRule("ACCESS-01")]
    public void EveryNonWritingMember_IsReadonly()
    {
        string[] mutators = ["Write", "Enable", "Disable"];
        string[] objectMembers = ["Equals", "GetHashCode", "ToString", "GetType"];
        foreach (var type in new[] { typeof(EntityRef), typeof(EntityRefMut) })
        {
            var notReadonly = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => !mutators.Contains(m.Name) && !objectMembers.Contains(m.Name))
                .Where(m => !m.IsDefined(typeof(IsReadOnlyAttribute), false))
                .Select(m => m.Name)
                .ToArray();
            Assert.That(notReadonly, Is.Empty, $"{type.Name}: a non-readonly member costs a defensive copy on every read-only receiver");
        }

        var conversion = typeof(EntityRefMut).GetMethod("op_Implicit", BindingFlags.Public | BindingFlags.Static);
        Assert.That(conversion, Is.Not.Null);
        Assert.That(conversion.GetParameters()[0].IsIn, Is.True, "the conversion takes its operand by `in`, not by a ~200-byte copy");
    }

    /// <summary>
    /// The Versioned read memo through read-only receivers: a <c>foreach</c> variable and an <c>in</c> parameter read the committed value, twice, and
    /// see a write made through the same transaction.
    /// </summary>
    [Test]
    public void VersionedRead_ThroughReadonlyReceivers_IsCorrect()
    {
        using var dbe = SetupEngine();
        var a = SpawnV(dbe, 11);
        var b = SpawnV(dbe, 22);

        using var tx = dbe.CreateQuickTransaction();
        var seen = 0;
        foreach (var e in tx.Query<VUnit>())
        {
            var first = e.Read(VUnit.Stats).Health;
            Assert.That(e.Read(VUnit.Stats).Health, Is.EqualTo(first), "the memoized second read agrees with the first");
            Assert.That(ReadHealth(in e), Is.EqualTo(first));
            seen += first;
        }
        Assert.That(seen, Is.EqualTo(33));

        tx.OpenMut(a).Write(VUnit.Stats).Health = 99;
        var ro = tx.Open(a);
        Assert.That(ReadHealth(in ro), Is.EqualTo(99), "read-your-own-write through an `in` receiver");
        Assert.That(ReadHealth(in ro), Is.EqualTo(99));
        Assert.That(tx.Open(b).Read(VUnit.Stats).Health, Is.EqualTo(22));
    }

    private static int ReadHealth(in EntityRef entity) => entity.Read(VUnit.Stats).Health;

    /// <summary>
    /// The Versioned read memo lands on the handle itself when the read goes through a read-only receiver. A point-in-time worker defers the chain walk
    /// (the transaction resolves eagerly, so it would not exercise this): the first read through an <c>in</c> parameter must clear the pending state on
    /// the caller's handle. A non-readonly <c>Read</c> would walk on a defensive copy and leave it pending.
    /// </summary>
    [Test]
    public void VersionedMemo_LandsOnTheHandle_ThroughAnInReceiver()
    {
        using var dbe = SetupEngine();
        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos = new MixedSvPos(1, 1);
            var data = new MixedVData(10);
            id = tx.Spawn<MixedUnit>(MixedUnit.Position.Set(in pos), MixedUnit.Data.Set(in data));
            tx.Commit();
        }

        using var pta = PointInTimeAccessor.Create(dbe);
        var e = pta.GetWorkerAccessor(0).Open(id);
        Assert.That(e.HasPendingVersionedSlots, Is.True, "premise: this path defers the Versioned walk");
        Assert.That(ReadScore(in e), Is.EqualTo(10));
        Assert.That(e.HasPendingVersionedSlots, Is.False, "the memo landed on the handle, not on a copy");
        Assert.That(ReadScore(in e), Is.EqualTo(10), "and the memoized location reads the same value");
    }

    private static int ReadScore(in EntityRef entity) => entity.Read(MixedUnit.Data).Score;

    /// <summary>
    /// The resolvers turn a resolved <see cref="EntityRef"/> into an <see cref="EntityRefMut"/> with <c>Unsafe.BitCast</c>, which throws at run time -
    /// on every writable open - once the two sizes differ. A field added to <see cref="EntityRefMut"/> would still compile; this is what catches it.
    /// </summary>
    [Test]
    [VerifiesRule("ACCESS-01")]
    public void EntityRefMut_IsExactlyOneEntityRef()
    {
        Assert.That(Unsafe.SizeOf<EntityRefMut>(), Is.EqualTo(Unsafe.SizeOf<EntityRef>()));
        var fields = typeof(EntityRefMut).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.That(fields.Select(f => (f.Name, f.FieldType)), Is.EqualTo(new[] { ("_ref", typeof(EntityRef)) }));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Transaction / EntityAccessor
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void TryOpenMut_Existing_WritesAndCommits()
    {
        using var dbe = SetupEngine();
        var id = SpawnSv(dbe, 1);

        using (var tx = dbe.CreateQuickTransaction())
        {
            Assert.That(tx.TryOpenMut(id, out var entity), Is.True);
            Assert.That(entity.IsValid, Is.True);
            entity.Write(SvUnit.Position).X = 7;
            tx.Commit();
        }

        Assert.That(ReadX(dbe, id), Is.EqualTo(7f));
    }

    [Test]
    public void TryOpenMut_Missing_ReturnsFalse()
    {
        using var dbe = SetupEngine();
        var sv = SpawnSv(dbe, 1);
        using var tx = dbe.CreateQuickTransaction();

        // A real EntityMap miss (right archetype, absent key), then an id no archetype routes.
        var absent = new EntityId(sv.EntityKey + 100_000, sv.ArchetypeId);
        Assert.That(tx.TryOpenMut(absent, out var miss), Is.False);
        Assert.That(miss.IsValid, Is.False);
        Assert.That(tx.IsAlive(absent), Is.False);
        Assert.That(tx.TryOpenMut(Missing, out var entity), Is.False);
        Assert.That(entity.IsValid, Is.False);
        Assert.That(tx.TryOpenMut(EntityId.Null, out _), Is.False);
    }

    [Test]
    public void TryOpenMut_OwnSpawn_ReturnsTrue_PendingDestroy_ReturnsFalse()
    {
        using var dbe = SetupEngine();
        var committed = SpawnSv(dbe, 1);

        using var tx = dbe.CreateQuickTransaction();
        var pos = new SvPosition(5, 0);
        var vel = new SvVelocity(0, 0);
        var spawned = tx.Spawn<SvUnit>(SvUnit.Position.Set(in pos), SvUnit.Velocity.Set(in vel));
        Assert.That(tx.TryOpenMut(spawned, out var own), Is.True, "an own spawn is writable before commit");
        own.Write(SvUnit.Position).X = 6;

        tx.Destroy(committed);
        Assert.That(tx.TryOpenMut(committed, out _), Is.False, "a pending destroy is not openable");
        Assert.That(tx.IsAlive(committed), Is.False);

        var doomed = tx.Spawn<SvUnit>(SvUnit.Position.Set(in pos), SvUnit.Velocity.Set(in vel));
        Assert.That(tx.IsAlive(doomed), Is.True, "premise: an own spawn is alive");
        tx.Destroy(doomed);
        Assert.That(tx.TryOpenMut(doomed, out _), Is.False, "an own spawn destroyed in the same transaction is not openable");
        Assert.That(tx.IsAlive(doomed), Is.False);
        tx.Commit();

        Assert.That(ReadX(dbe, spawned), Is.EqualTo(6f));
    }

    [Test]
    public void TryOpenMut_MixedArchetype_WritesBothStorageModes()
    {
        using var dbe = SetupEngine();
        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos = new MixedSvPos(1, 1);
            var data = new MixedVData(10);
            id = tx.Spawn<MixedUnit>(MixedUnit.Position.Set(in pos), MixedUnit.Data.Set(in data));
            tx.Commit();
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            Assert.That(tx.TryOpenMut(id, out var e), Is.True);
            e.Write(MixedUnit.Position).X = 5;
            e.Write(MixedUnit.Data).Score = 20;
            tx.Commit();
        }

        using var check = dbe.CreateQuickTransaction();
        var r = check.Open(id);
        Assert.That(r.Read(MixedUnit.Position).X, Is.EqualTo(5f));
        Assert.That(r.Read(MixedUnit.Data).Score, Is.EqualTo(20));
    }

    [Test]
    public void TryOpenMut_Versioned_WritesAndCommits()
    {
        using var dbe = SetupEngine();
        var id = SpawnV(dbe, 10);

        using (var tx = dbe.CreateQuickTransaction())
        {
            Assert.That(tx.TryOpenMut(id, out var entity), Is.True);
            entity.Write(VUnit.Stats).Health = 33;
            Assert.That(entity.Read(VUnit.Stats).Health, Is.EqualTo(33), "read-your-own-write through the same handle");
            tx.Commit();
        }

        using var check = dbe.CreateQuickTransaction();
        Assert.That(check.Open(id).Read(VUnit.Stats).Health, Is.EqualTo(33));
    }

    /// <summary>
    /// The mutation prep runs on every writable open, found or not: a read-only transaction refuses <c>TryOpenMut</c> exactly as it refuses
    /// <c>OpenMut</c>, and a writable open moves the transaction to InProgress.
    /// </summary>
    [Test]
    [VerifiesRule("ACCESS-01")]
    public void WritableOpens_RunTheMutationPrep()
    {
        using var dbe = SetupEngine();
        var id = SpawnSv(dbe, 1);

        using (var ro = dbe.CreateReadOnlyTransaction())
        {
            Assert.Throws<InvalidOperationException>(() => ro.TryOpenMut(id, out _));
            Assert.Throws<InvalidOperationException>(() => ro.TryOpenMut(Missing, out _), "the prep runs before the lookup");
            Assert.Throws<InvalidOperationException>(() => ro.OpenMut(id));
            Assert.That(ro.TryOpen(id, out _), Is.True, "reads stay available");
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            Assert.That(tx.State, Is.EqualTo(Transaction.TransactionState.Created));
            Assert.That(tx.TryOpenMut(Missing, out _), Is.False);
            Assert.That(tx.State, Is.EqualTo(Transaction.TransactionState.InProgress));
        }

        // A finished transaction refuses too: the prep checks the state, not only read-only-ness.
        using (var done = dbe.CreateQuickTransaction())
        {
            // A write first: Commit() on an EMPTY transaction returns true but leaves it in Created, still usable.
            done.OpenMut(id).Write(SvUnit.Position).X = 2;
            done.Commit();
            Assert.Throws<InvalidOperationException>(() => done.TryOpenMut(id, out _));
            Assert.Throws<InvalidOperationException>(() => done.OpenMut(id));
        }

        var absent = new EntityId(id.EntityKey + 100_000, id.ArchetypeId);
        using (var ro = dbe.CreateReadOnlyTransaction())
        {
            var accessor = ro.For<SvUnit>();
            try
            {
                Assert.That(ArchetypeOpenThrows(ref accessor, id, tryForm: true), Is.True, "ArchetypeAccessor.TryOpenMut runs the same prep");
                Assert.That(ArchetypeOpenThrows(ref accessor, absent, tryForm: true), Is.True, "...before the lookup");
                Assert.That(ArchetypeOpenThrows(ref accessor, id, tryForm: false), Is.True, "ArchetypeAccessor.OpenMut too");
            }
            finally
            {
                accessor.Dispose();
            }
        }

        // The prep runs on EVERY writable open of an ArchetypeAccessor, not once: one taken before commit is refused after it.
        using (var tx = dbe.CreateQuickTransaction())
        {
            var accessor = tx.For<SvUnit>();
            try
            {
                Assert.That(accessor.TryOpenMut(id, out _), Is.True, "premise: the first writable open succeeds");
                tx.Commit();
                Assert.That(ArchetypeOpenThrows(ref accessor, id, tryForm: false), Is.True, "a writable open after commit must be refused");
                Assert.That(ArchetypeOpenThrows(ref accessor, id, tryForm: true), Is.True);
            }
            finally
            {
                accessor.Dispose();
            }
        }
    }

    /// <summary>An ArchetypeAccessor is a ref struct, so <c>Assert.Throws</c>' lambda cannot capture it.</summary>
    private static bool ArchetypeOpenThrows(ref ArchetypeAccessor<SvUnit> accessor, EntityId id, bool tryForm)
    {
        try
        {
            if (tryForm)
            {
                accessor.TryOpenMut(id, out _);
            }
            else
            {
                accessor.OpenMut(id);
            }
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    [Test]
    public void OpenMut_Missing_Throws()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();
        Assert.Throws<InvalidOperationException>(() => tx.OpenMut(Missing));
        Assert.Throws<InvalidOperationException>(() => tx.Open(Missing));
    }

    [Test]
    public void PointInTimeWorker_IsAlive_And_TryOpen()
    {
        using var dbe = SetupEngine();
        var id = SpawnSv(dbe, 1);

        using var pta = PointInTimeAccessor.Create(dbe);
        var wa = pta.GetWorkerAccessor(0);
        Assert.That(wa.IsAlive(id), Is.True);
        Assert.That(wa.IsAlive(Missing), Is.False);
        Assert.That(wa.IsAlive(EntityId.Null), Is.False);
        Assert.That(wa.TryOpen(id, out var e), Is.True);
        Assert.That(e.Read(SvUnit.Position).X, Is.EqualTo(1f));
    }

    [Test]
    public void EntityAccessor_IsAlive_IsSnapshotBound()
    {
        using var dbe = SetupEngine();
        using var pta = PointInTimeAccessor.Create(dbe);
        var wa = pta.GetWorkerAccessor(0);
        var later = SpawnSv(dbe, 1);

        Assert.That(wa.IsAlive(later), Is.False, "spawned after the snapshot's TSN");

        using var fresh = PointInTimeAccessor.Create(dbe);
        Assert.That(fresh.GetWorkerAccessor(0).IsAlive(later), Is.True, "premise: a snapshot taken after the spawn sees it");
    }

    /// <summary>The base <see cref="EntityAccessor"/> - a point-in-time worker - has the same writable try-open, with a no-op mutation prep.</summary>
    [Test]
    public void PointInTimeWorker_TryOpenMut()
    {
        using var dbe = SetupEngine();
        var id = SpawnSv(dbe, 1);

        using var pta = PointInTimeAccessor.Create(dbe);
        var wa = pta.GetWorkerAccessor(0);
        Assert.That(wa.TryOpenMut(id, out var e), Is.True);
        Assert.That(e.Read(SvUnit.Position).X, Is.EqualTo(1f));
        Assert.That(wa.TryOpenMut(new EntityId(id.EntityKey + 100_000, id.ArchetypeId), out var miss), Is.False);
        Assert.That(miss.IsValid, Is.False);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // ArchetypeAccessor — same contract
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void ArchetypeAccessor_Missing_OpenThrows_TryOpenFalse()
    {
        using var dbe = SetupEngine();
        var sv = SpawnSv(dbe, 1);
        using var tx = dbe.CreateQuickTransaction();
        // Right archetype, absent key — the lookup miss, not the routing check.
        var absent = new EntityId(sv.EntityKey + 100_000, sv.ArchetypeId);
        var accessor = tx.For<SvUnit>();
        try
        {
            Assert.That(accessor.TryOpen(absent, out var ro), Is.False);
            Assert.That(ro.IsValid, Is.False);
            Assert.That(accessor.TryOpenMut(absent, out var mut), Is.False);
            Assert.That(mut.IsValid, Is.False);
            Assert.That(accessor.IsAlive(absent), Is.False);
            Assert.That(accessor.TryOpen(EntityId.Null, out _), Is.False);
            Assert.That(accessor.IsAlive(EntityId.Null), Is.False);

            var openThrew = false;
            try
            {
                accessor.Open(absent);
            }
            catch (InvalidOperationException)
            {
                openThrew = true;
            }
            var openMutThrew = false;
            try
            {
                accessor.OpenMut(absent);
            }
            catch (InvalidOperationException)
            {
                openMutThrew = true;
            }
            Assert.That(openThrew, Is.True);
            Assert.That(openMutThrew, Is.True);
        }
        finally
        {
            accessor.Dispose();
        }
    }

    [Test]
    public void ArchetypeAccessor_TryOpenMut_WritesAndCommits()
    {
        using var dbe = SetupEngine();
        var id = SpawnSv(dbe, 1);

        using (var tx = dbe.CreateQuickTransaction())
        {
            var accessor = tx.For<SvUnit>();
            try
            {
                Assert.That(accessor.IsAlive(id), Is.True);
                Assert.That(accessor.TryOpenMut(id, out var entity), Is.True);
                entity.Write(SvUnit.Position).X = 9;
                Assert.That(accessor.TryOpen(id, out var ro), Is.True);
                Assert.That(ro.Read(SvUnit.Position).X, Is.EqualTo(9f));
            }
            finally
            {
                accessor.Dispose();
            }
            tx.Commit();
        }

        Assert.That(ReadX(dbe, id), Is.EqualTo(9f));
    }

    /// <summary>An id of another archetype is not an entity of this one, even when its key exists in this archetype's map.</summary>
    [Test]
    public void ArchetypeAccessor_ForeignArchetypeId_IsNotFound()
    {
        using var dbe = SetupEngine();
        var sv = SpawnSv(dbe, 1);
        var v = SpawnV(dbe, 10);
        // Same key, other archetype: the collision the routing check exists for.
        var foreign = new EntityId(sv.EntityKey, v.ArchetypeId);

        using var tx = dbe.CreateQuickTransaction();
        var accessor = tx.For<SvUnit>();
        try
        {
            Assert.That(accessor.IsAlive(sv), Is.True, "premise: the key resolves under its own archetype");
            Assert.That(accessor.TryOpen(foreign, out _), Is.False);
            Assert.That(accessor.TryOpen(v, out _), Is.False);
            Assert.That(accessor.IsAlive(foreign), Is.False);
        }
        finally
        {
            accessor.Dispose();
        }
    }

    /// <summary>
    /// The routing check compares ROUTING ids (SCHEMA-03). An id built with the archetype's catalog id instead must not resolve — which this test can
    /// only show when the two differ, so that is asserted as its premise.
    /// </summary>
    [Test]
    public void ArchetypeAccessor_CatalogIdInPlaceOfRoutingId_IsNotFound()
    {
        using var dbe = SetupEngine();
        var sv = SpawnSv(dbe, 1);
        var catalogId = Archetype<SvUnit>.Metadata.ArchetypeId;
        Assume.That(sv.ArchetypeId, Is.Not.EqualTo(catalogId), "premise: routing and catalog ids diverge in this process, or the test proves nothing");
        var wrong = new EntityId(sv.EntityKey, catalogId);

        using var tx = dbe.CreateQuickTransaction();
        var accessor = tx.For<SvUnit>();
        try
        {
            Assert.That(accessor.TryOpen(sv, out _), Is.True);
            Assert.That(accessor.TryOpen(wrong, out _), Is.False);
            Assert.That(accessor.IsAlive(wrong), Is.False);
        }
        finally
        {
            accessor.Dispose();
        }
    }

    /// <summary>A destroy committed after the accessor's snapshot does not hide the entity from it: it was alive at that TSN.</summary>
    [Test]
    public void ArchetypeAccessor_EntityDestroyedAfterTsn_IsStillFound()
    {
        using var dbe = SetupEngine();
        var id = SpawnSv(dbe, 3);
        using var old = dbe.CreateQuickTransaction();
        using (var d = dbe.CreateQuickTransaction())
        {
            d.Destroy(id);
            d.Commit();
        }

        var accessor = old.For<SvUnit>();
        try
        {
            Assert.That(accessor.IsAlive(id), Is.True);
            Assert.That(accessor.TryOpen(id, out var e), Is.True);
            Assert.That(e.Read(SvUnit.Position).X, Is.EqualTo(3f));
        }
        finally
        {
            accessor.Dispose();
        }

        using var fresh = dbe.CreateQuickTransaction();
        Assert.That(fresh.IsAlive(id), Is.False, "premise: the destroy is committed");
    }

    [Test]
    public void ArchetypeAccessor_TryOpenMut_Versioned_WritesAndCommits()
    {
        using var dbe = SetupEngine();
        var id = SpawnV(dbe, 10);

        using (var tx = dbe.CreateQuickTransaction())
        {
            var accessor = tx.For<VUnit>();
            try
            {
                Assert.That(accessor.TryOpenMut(id, out var entity), Is.True);
                entity.Write(VUnit.Stats).Health = 44;
                Assert.That(entity.Read(VUnit.Stats).Health, Is.EqualTo(44));
            }
            finally
            {
                accessor.Dispose();
            }
            tx.Commit();
        }

        using var check = dbe.CreateQuickTransaction();
        Assert.That(check.Open(id).Read(VUnit.Stats).Health, Is.EqualTo(44));
    }

    /// <summary>The accessor reads at its transaction's snapshot: an entity committed after that TSN does not resolve.</summary>
    [Test]
    public void ArchetypeAccessor_EntityNotVisibleAtTsn_IsNotFound()
    {
        using var dbe = SetupEngine();
        using var old = dbe.CreateQuickTransaction();
        var later = SpawnSv(dbe, 1);

        var accessor = old.For<SvUnit>();
        try
        {
            Assert.That(accessor.TryOpen(later, out _), Is.False);
            Assert.That(accessor.IsAlive(later), Is.False);
        }
        finally
        {
            accessor.Dispose();
        }

        using var fresh = dbe.CreateQuickTransaction();
        var freshAccessor = fresh.For<SvUnit>();
        try
        {
            Assert.That(freshAccessor.TryOpen(later, out _), Is.True, "premise: visible to a snapshot taken after the commit");
        }
        finally
        {
            freshAccessor.Dispose();
        }
    }

    /// <summary>
    /// Documented: the archetype accessor does not see its transaction's own spawns (not in the EntityMap until commit), but it does honour its pending
    /// destroys — an entity the transaction destroyed is a miss, as on the transaction, so it cannot be written after its destroy.
    /// </summary>
    [Test]
    public void ArchetypeAccessor_OwnSpawnNotSeen_PendingDestroyIsMiss()
    {
        using var dbe = SetupEngine();
        var committed = SpawnSv(dbe, 2);
        using var tx = dbe.CreateQuickTransaction();
        var pos = new SvPosition(1, 0);
        var vel = new SvVelocity(0, 0);
        var spawned = tx.Spawn<SvUnit>(SvUnit.Position.Set(in pos), SvUnit.Velocity.Set(in vel));
        tx.Destroy(committed);

        var accessor = tx.For<SvUnit>();
        try
        {
            Assert.That(accessor.TryOpen(spawned, out _), Is.False);
            Assert.That(tx.TryOpen(spawned, out _), Is.True, "the transaction itself does see it");

            Assert.That(accessor.IsAlive(committed), Is.False, "a pending destroy is a miss");
            Assert.That(accessor.TryOpen(committed, out _), Is.False);
            Assert.That(accessor.TryOpenMut(committed, out _), Is.False, "…so it cannot be written after its destroy");
        }
        finally
        {
            accessor.Dispose();
        }
    }
}
