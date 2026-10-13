using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// Rule <b>AP-06</b> (<c>rules/durability.md</c>): an entity's index entries follow its newest committed revision, whatever the order of two
/// last-writer-wins updates (#1263).
/// </summary>
/// <remarks>
/// Without a conflict handler two transactions may update one entity concurrently, each from the revision it read. Each publish moved the entity's index
/// entries from the READ value. The first moved them off it, so the second found nothing to move and changed nothing: the entity read the second writer's
/// value while its unique key stayed the first writer's — unreachable by its value, and the first writer's key held by an entity that no longer had it.
/// </remarks>
[TestFixture]
class LastWriterWinsIndexTests : TestBase<LastWriterWinsIndexTests>
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private DatabaseEngine OpenEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CompD>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static EntityId Seed(DatabaseEngine dbe, CompD value)
    {
        using var tx = dbe.CreateQuickTransaction();
        var id = tx.Spawn<CompDArch>(CompDArch.D.Set(value));
        tx.Commit();
        return id;
    }

    private static void Write(Transaction tx, EntityId id, CompD value) => tx.OpenMut(id).Set(CompDArch.D, value);

    private static List<long> UniqueHolders(DatabaseEngine dbe, int b)
    {
        using var tx = dbe.CreateQuickTransaction();
        var found = new List<long>();
        using var e = tx.EnumerateIndex<CompD, int>(dbe.GetIndexRef<CompD, int>(x => x.B), b, b);
        while (e.MoveNext())
        {
            found.Add(e.Current.EntityPK);
        }

        return found;
    }

    private static List<long> MultiHolders(DatabaseEngine dbe, double c)
    {
        using var tx = dbe.CreateQuickTransaction();
        var found = new List<long>();
        using var e = tx.EnumerateIndex<CompD, double>(dbe.GetIndexRef<CompD, double>(x => x.C), c, c);
        while (e.MoveNext())
        {
            found.Add(e.Current.EntityPK);
        }

        return found;
    }

    /// <summary>Both read B=60; T1 commits 30, then T2 commits 40. The entity reads 40, and the unique index holds it under 40 alone.</summary>
    [Test]
    [VerifiesRule("AP-06")]
    public void TwoUpdatesFromOneRead_LeaveTheUniqueIndexOnTheLastWritersKey()
    {
        using var dbe = OpenEngine();
        var x = Seed(dbe, new CompD(1f, 60, 1.0));
        var pk = (long)x.RawValue;
        using (var t1 = dbe.CreateQuickTransaction())
        using (var t2 = dbe.CreateQuickTransaction())
        {
            Write(t1, x, new CompD(1f, 30, 1.0));
            Write(t2, x, new CompD(1f, 40, 1.0));
            Assert.That(t1.Commit() && t2.Commit(), Is.True, "premise: without a handler both commit, the last writer winning");
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            Assert.That(tx.Open(x).Read(CompDArch.D).B, Is.EqualTo(40), "premise: the entity reads the last writer's value");
        }

        Assert.That(UniqueHolders(dbe, 40), Is.EqualTo(new[] { pk }), "the entity is not indexed under the value it reads");
        Assert.That(UniqueHolders(dbe, 30), Is.Empty, "the first writer's key is still held by an entity that no longer has it");
        Assert.That(UniqueHolders(dbe, 60), Is.Empty, "the read key is still held");
    }

    /// <summary>The same on an AllowMultiple index: both read C=6; T1 commits 3, then T2 commits 4. The entity is under 4 alone.</summary>
    [Test]
    [VerifiesRule("AP-06")]
    public void TwoUpdatesFromOneRead_LeaveTheMultiValueIndexOnTheLastWritersKey()
    {
        using var dbe = OpenEngine();
        var x = Seed(dbe, new CompD(1f, 60, 6.0));
        var pk = (long)x.RawValue;
        using (var t1 = dbe.CreateQuickTransaction())
        using (var t2 = dbe.CreateQuickTransaction())
        {
            Write(t1, x, new CompD(1f, 60, 3.0));
            Write(t2, x, new CompD(1f, 60, 4.0));
            Assert.That(t1.Commit() && t2.Commit(), Is.True, "premise: without a handler both commit, the last writer winning");
        }

        Assert.That(MultiHolders(dbe, 4.0), Is.EqualTo(new[] { pk }), "the entity is not indexed under the value it reads");
        Assert.That(MultiHolders(dbe, 3.0), Is.Empty, "the first writer's key still names the entity");
        Assert.That(MultiHolders(dbe, 6.0), Is.Empty, "the read key still names the entity");
    }

    /// <summary>
    /// Both read B=60; T1 commits 30, then T2 commits its fields as it read them. The keys T2 writes are its read ones, so its PREPARE owes no index move —
    /// but the newest committed key is T1's 30, and the entity now reads 60.
    /// </summary>
    [Test]
    [VerifiesRule("AP-06")]
    public void AnUpdateThatKeepsItsReadKey_MovesTheEntryBackFromAnInterveningCommit()
    {
        using var dbe = OpenEngine();
        var x = Seed(dbe, new CompD(1f, 60, 1.0));
        var pk = (long)x.RawValue;
        using (var t1 = dbe.CreateQuickTransaction())
        using (var t2 = dbe.CreateQuickTransaction())
        {
            Write(t1, x, new CompD(1f, 30, 1.0));
            Write(t2, x, new CompD(1f, 60, 1.0));
            Assert.That(t1.Commit() && t2.Commit(), Is.True, "premise: without a handler both commit, the last writer winning");
        }

        Assert.That(UniqueHolders(dbe, 60), Is.EqualTo(new[] { pk }), "the entity is not indexed under the value it reads");
        Assert.That(UniqueHolders(dbe, 30), Is.Empty, "the intervening commit's key is still held");
    }

    /// <summary>
    /// X moves from 60, the largest key, to 30; a spawn then takes 60. The tree's last-key cache still said 60, the append read it as a duplicate past the
    /// commit's append, and the engine stopped (IXW-11).
    /// </summary>
    [Test]
    [VerifiesRule("IXW-11")]
    public void ASpawnOntoTheLargestKeyAnUpdateFreed_Commits()
    {
        using var dbe = OpenEngine();
        var x = Seed(dbe, new CompD(1f, 60, 1.0));
        using (var tx = dbe.CreateQuickTransaction())
        {
            Write(tx, x, new CompD(1f, 30, 1.0));
            Assert.That(tx.Commit(), Is.True, "premise: X moves to 30");
        }

        var y = Seed(dbe, new CompD(1f, 60, 1.0));
        Assert.That(dbe.CommitPipelineFailure, Is.Null, "the engine stopped committing");
        Assert.That(UniqueHolders(dbe, 60), Is.EqualTo(new[] { (long)y.RawValue }), "Y is not under its key");
        Assert.That(UniqueHolders(dbe, 30), Is.EqualTo(new[] { (long)x.RawValue }), "X is not under its key");
    }

    /// <summary>
    /// T2 reads X at B=60 and writes it unchanged; T1 then moves X to 30 and Y takes 60. T2's commit moves X back to 60, which Y holds: it is rejected before
    /// its append, as any write onto a held key is, and the engine goes on. Measured against its read, T2 moved no key, nothing checked 60, and the publish's
    /// backstop threw past the point of no return, which stops the engine (AP-03).
    /// </summary>
    [Test]
    [VerifiesRule("AP-06")]
    public void AnUpdateBackOntoAKeyTakenSinceItsRead_IsRejectedBeforeTheAppend()
    {
        using var dbe = OpenEngine();
        var x = Seed(dbe, new CompD(1f, 60, 1.0));
        EntityId y;
        using (var t2 = dbe.CreateQuickTransaction())
        {
            Write(t2, x, new CompD(1f, 60, 1.0));
            using (var t1 = dbe.CreateQuickTransaction())
            {
                Write(t1, x, new CompD(1f, 30, 1.0));
                Assert.That(t1.Commit(), Is.True, "premise: T1 moves X to 30");
            }

            y = Seed(dbe, new CompD(1f, 60, 1.0));
            Assert.Throws<UniqueConstraintViolationException>(() => t2.Commit(), "a write back onto a key taken since its read is not rejected before the append");
        }

        Assert.That(dbe.CommitPipelineFailure, Is.Null, "the engine stopped committing");
        Assert.That(UniqueHolders(dbe, 30), Is.EqualTo(new[] { (long)x.RawValue }), "X is not under the key it reads");
        Assert.That(UniqueHolders(dbe, 60), Is.EqualTo(new[] { (long)y.RawValue }), "Y is not under its key");
    }

    /// <summary>
    /// The older update — the lower stamp — is held at its publish while the newer one commits. The entity reads the newer value; the older publish, last,
    /// must move nothing.
    /// </summary>
    [Test]
    [VerifiesRule("AP-06")]
    public void AnOlderUpdatePublishedLast_LeavesTheIndexOnTheNewerKey()
    {
        using var dbe = OpenEngine();
        var x = Seed(dbe, new CompD(1f, 60, 1.0));
        var pk = (long)x.RawValue;
        var table = dbe.GetComponentTable<CompD>();
        using var olderAtPublish = new ManualResetEventSlim();
        using var releaseOlder = new ManualResetEventSlim();
        var publishes = 0;
        dbe.PublishComponentProbe = (t, _) =>
        {
            if (ReferenceEquals(t, table) && Interlocked.Increment(ref publishes) == 1)
            {
                olderAtPublish.Set();
                releaseOlder.Wait(Patience);
            }
        };

        Exception failure = null;
        var older = new Thread(() => Run(30)) { IsBackground = true };
        var newer = new Thread(() => Run(40)) { IsBackground = true };
        try
        {
            older.Start();
            Assert.That(olderAtPublish.Wait(Patience), Is.True, "premise: the older update reaches its publish");
            newer.Start();
            Assert.That(newer.Join(Patience), Is.True, "premise: the newer update commits while the older one is held at its publish");
        }
        finally
        {
            releaseOlder.Set();
        }

        Assert.That(older.Join(Patience), Is.True, "the older update finishes its publish once released");
        dbe.PublishComponentProbe = null;
        Assert.That(failure, Is.Null);

        using (var tx = dbe.CreateQuickTransaction())
        {
            Assert.That(tx.Open(x).Read(CompDArch.D).B, Is.EqualTo(40), "premise: the newer update's value is the newest committed");
        }

        Assert.That(UniqueHolders(dbe, 40), Is.EqualTo(new[] { pk }), "the entity is not indexed under the value it reads");
        Assert.That(UniqueHolders(dbe, 30), Is.Empty, "the older publish moved the entry back to its own key");

        void Run(int b)
        {
            try
            {
                using var tx = dbe.CreateQuickTransaction();
                Write(tx, x, new CompD(1f, b, 1.0));
                tx.Commit();
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref failure, ex, null);
            }
        }
    }
}
