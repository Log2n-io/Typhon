using System;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// #1247: a navigation — a one-shot query and an incremental view — answers at its snapshot (VIS-13). Its sources are found through the FK trees, which hold
/// the newest foreign keys and lose a destroyed entity's entries at its publish; a navigation at an older snapshot returned sources spawned after it, lost
/// ones destroyed after it and followed foreign keys retargeted after it. A view's refresh reads committed state only: it folded the refresher's own
/// unpublished write, and kept it after the rollback.
/// </summary>
[TestFixture]
[NonParallelizable]
class NavigationSnapshotTests : TestBase<NavigationSnapshotTests>
{
    private static int _cap = 5000;

    private DatabaseEngine Open()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CompGuild>();
        dbe.RegisterComponentFromAccessor<CompPlayer>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static void Commit(DatabaseEngine dbe, Action<Transaction> write)
    {
        using var tx = dbe.CreateQuickTransaction();
        write(tx);
        Assert.That(tx.Commit(), Is.True);
    }

    private static EntityId Guild(DatabaseEngine dbe, int level)
    {
        EntityId g = default;
        Commit(dbe, tx => g = tx.Spawn<CompGuildArch>(CompGuildArch.Guild.Set(new CompGuild(level, Interlocked.Increment(ref _cap)))));
        return g;
    }

    private static EntityId Player(DatabaseEngine dbe, EntityId guild)
    {
        EntityId p = default;
        Commit(dbe, tx => p = tx.Spawn<CompPlayerArch>(CompPlayerArch.Player.Set(new CompPlayer((long)guild.RawValue, true))));
        return p;
    }

    private static void SetLevel(DatabaseEngine dbe, EntityId g, int level) =>
        Commit(dbe, tx => tx.OpenMut(g).Set(CompGuildArch.Guild, new CompGuild(level, Interlocked.Increment(ref _cap))));

    private static void SetGuild(DatabaseEngine dbe, EntityId p, EntityId g) =>
        Commit(dbe, tx => tx.OpenMut(p).Set(CompPlayerArch.Player, new CompPlayer((long)g.RawValue, true)));

    private static void Kill(DatabaseEngine dbe, EntityId id) => Commit(dbe, tx => tx.Destroy(id));

    private static EntityId[] HighGuildMembers(Transaction tx) =>
        tx.Query<CompPlayerArch>().NavigateField<CompPlayer, CompGuild>(p => p.GuildId).Where((p, g) => g.Level >= 10).Execute().ToArray();

    private static ViewBase NavView(DatabaseEngine dbe)
    {
        ViewBase view;
        using (var tx = dbe.CreateQuickTransaction())
        {
            view = tx.Query<CompPlayerArch>().NavigateField<CompPlayer, CompGuild>(p => p.GuildId).Where((p, g) => g.Level >= 10).ToView();
        }

        using (var r = dbe.CreateQuickTransaction())
        {
            view.Refresh(r);   // the owed full refresh, so later entries take the incremental path
        }

        view.ClearDelta();
        return view;
    }

    private static bool Has(ViewBase v, EntityId id) => v.Contains((long)id.RawValue);

    // ── One-shot navigation ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    [VerifiesRule("VIS-13")]
    public void ANavigation_AnswersAtItsSnapshot_ThroughSpawnsDestroysAndRetargets()
    {
        using var dbe = Open();
        var high = Guild(dbe, 20);
        var low = Guild(dbe, 1);
        var stays = Player(dbe, high);
        var destroyed = Player(dbe, high);
        var leaves = Player(dbe, high);
        var joins = Player(dbe, low);
        using var reader = dbe.CreateQuickTransaction();

        Player(dbe, high);                  // spawned after the snapshot
        Kill(dbe, destroyed);               // destroyed after it
        SetGuild(dbe, leaves, low);         // retargeted away after it
        SetGuild(dbe, joins, high);         // retargeted onto it after it

        Assert.That(HighGuildMembers(reader), Is.EquivalentTo(new[] { stays, destroyed, leaves }));
        Assert.That(reader.Query<CompPlayerArch>().NavigateField<CompPlayer, CompGuild>(p => p.GuildId).Where((p, g) => p.Active == 1 && g.Level >= 10)
            .Execute(), Is.EquivalentTo(new[] { stays, destroyed, leaves }), "with a source predicate too");
    }

    [Test]
    [VerifiesRule("VIS-13")]
    public void ANavigation_SeesItsTransactionsOwnSpawnsAndDestroys_NotItsOwnRetargets()
    {
        using var dbe = Open();
        var high = Guild(dbe, 20);
        var low = Guild(dbe, 1);
        var a = Player(dbe, high);
        var b = Player(dbe, high);
        var c = Player(dbe, low);
        using var tx = dbe.CreateQuickTransaction();
        tx.Destroy(a);
        tx.OpenMut(c).Set(CompPlayerArch.Player, new CompPlayer((long)high.RawValue, true));   // its own value write: unseen
        var spawned = tx.Spawn<CompPlayerArch>(CompPlayerArch.Player.Set(new CompPlayer((long)high.RawValue, true)));

        Assert.That(HighGuildMembers(tx), Is.EquivalentTo(new[] { b, spawned }));
        Assert.That(tx.Query<CompPlayerArch>().NavigateField<CompPlayer, CompGuild>(p => p.GuildId).Where((p, g) => p.Active == 1 && g.Level >= 10).Execute(),
            Is.EquivalentTo(new[] { b, spawned }), "with a source predicate, its own spawn read on its own values");
    }

    /// <summary>
    /// A target predicate reads the target committed at the snapshot, never the transaction's own write of it — whether the transaction read the target
    /// before writing it (a cached read under the write) or not.
    /// </summary>
    [Test]
    [VerifiesRule("VIS-13")]
    public void ANavigationsTargetPredicate_ReadsCommitted_NotItsOwnWriteOfTheTarget()
    {
        using var dbe = Open();
        var high = Guild(dbe, 20);
        var low = Guild(dbe, 1);
        var a = Player(dbe, high);
        var b = Player(dbe, low);
        using var tx = dbe.CreateQuickTransaction();
        Assert.That(HighGuildMembers(tx), Is.EquivalentTo(new[] { a }), "both targets read, committed");

        tx.OpenMut(high).Set(CompGuildArch.Guild, new CompGuild(1, Interlocked.Increment(ref _cap)));   // read before: a cached read under the write
        tx.OpenMut(low).Set(CompGuildArch.Guild, new CompGuild(20, Interlocked.Increment(ref _cap)));
        Assert.That(HighGuildMembers(tx), Is.EquivalentTo(new[] { a }), "its own writes of the targets unseen");

        using var fresh = dbe.CreateQuickTransaction();
        fresh.OpenMut(low).Set(CompGuildArch.Guild, new CompGuild(20, Interlocked.Increment(ref _cap)));   // never read before
        Assert.That(HighGuildMembers(fresh), Is.EquivalentTo(new[] { a }), "an own write never read before, unseen");
        Assert.That(b, Is.Not.EqualTo(default(EntityId)));
    }

    // ── Incremental navigation view ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A target crosses into the view at or below the refresh's snapshot; after it, a source is retargeted onto that target. At S it points elsewhere.</summary>
    [Test]
    [VerifiesRule("VIS-13")]
    public void AViewsFanOut_DoesNotAdmitASourceRetargetedOntoTheTargetAfterItsSnapshot()
    {
        using var dbe = Open();
        var ga = Guild(dbe, 5);
        var gb = Guild(dbe, 5);
        var p = Player(dbe, gb);
        using var view = NavView(dbe);
        SetLevel(dbe, ga, 20);
        using var reader = dbe.CreateQuickTransaction();
        SetGuild(dbe, p, ga);
        view.Refresh(reader);
        Assert.That(Has(view, p), Is.False, "at the snapshot p points at gb, which fails");
    }

    [Test]
    [VerifiesRule("VIS-13")]
    public void AViewsFanOut_KeepsASourceRetargetedAwayAfterItsSnapshot()
    {
        using var dbe = Open();
        var ga = Guild(dbe, 5);
        var gb = Guild(dbe, 5);
        var p = Player(dbe, ga);
        using var view = NavView(dbe);
        SetLevel(dbe, ga, 20);
        using var reader = dbe.CreateQuickTransaction();
        SetGuild(dbe, p, gb);
        view.Refresh(reader);
        Assert.That(Has(view, p), Is.True, "at the snapshot p points at ga, which passes");
    }

    [Test]
    [VerifiesRule("VIS-13")]
    public void AViewsFanOut_KeepsASourceDestroyedAfterItsSnapshot()
    {
        using var dbe = Open();
        var ga = Guild(dbe, 5);
        var p = Player(dbe, ga);
        using var view = NavView(dbe);
        SetLevel(dbe, ga, 20);
        using var reader = dbe.CreateQuickTransaction();
        Kill(dbe, p);
        view.Refresh(reader);
        Assert.That(Has(view, p), Is.True, "alive at the snapshot");
    }

    [Test]
    [VerifiesRule("VIS-13")]
    public void AViewsFanOut_DoesNotAdmitASourceSpawnedAfterItsSnapshot()
    {
        using var dbe = Open();
        var ga = Guild(dbe, 5);
        using var view = NavView(dbe);
        SetLevel(dbe, ga, 20);
        using var reader = dbe.CreateQuickTransaction();
        var p = Player(dbe, ga);
        view.Refresh(reader);
        Assert.That(Has(view, p), Is.False);
    }

    /// <summary>A navigation view built from a transaction's query answers what that query answers — populated at its snapshot, as an EcsView is.</summary>
    [Test]
    [VerifiesRule("VIS-13")]
    public void ANavigationViewBuiltFromATransaction_IsPopulatedAtItsSnapshot()
    {
        using var dbe = Open();
        var g = Guild(dbe, 20);
        var died = Player(dbe, g);
        using var reader = dbe.CreateQuickTransaction();
        var born = Player(dbe, g);
        Kill(dbe, died);
        using var nav = reader.Query<CompPlayerArch>().NavigateField<CompPlayer, CompGuild>(x => x.GuildId).Where((x, gg) => gg.Level >= 10).ToView();
        Assert.That(Has(nav, born), Is.False, "spawned after the creator's snapshot");
        Assert.That(Has(nav, died), Is.True, "alive at the creator's snapshot");
    }

    /// <summary>A refresh made by a transaction holding an unpublished FK write of its own: committed state only, so a rollback leaves nothing behind.</summary>
    [Test]
    [VerifiesRule("VIS-13")]
    public void ANavigationViewsRefresh_NeverFoldsTheRefreshersOwnWrite()
    {
        using var dbe = Open();
        var ga = Guild(dbe, 5);
        var ga2 = Guild(dbe, 6);
        var gb = Guild(dbe, 20);
        var p = Player(dbe, ga);
        using var view = NavView(dbe);
        SetGuild(dbe, p, ga2);   // a committed FK change: an entry at or below the refresher's snapshot
        using (var t = dbe.CreateQuickTransaction())
        {
            t.OpenMut(p).Set(CompPlayerArch.Player, new CompPlayer((long)gb.RawValue, true));   // its own, never committed
            view.Refresh(t);
            Assert.That(Has(view, p), Is.False, "the refresher's own write is not committed state");
            t.Rollback();
        }

        using (var fresh = dbe.CreateQuickTransaction())
        {
            view.Refresh(fresh);
        }

        Assert.That(Has(view, p), Is.False, "nothing left behind by the rollback");
    }

    /// <summary>The same on an ordinary multi-field view: its refresh reads committed state, never the refresher's own write.</summary>
    [Test]
    [VerifiesRule("VIS-13")]
    public void AnEcsViewsRefresh_NeverFoldsTheRefreshersOwnWrite()
    {
        using var dbe = Open();
        var g = Guild(dbe, 20);
        EntityId p = default;
        Commit(dbe, tx => p = tx.Spawn<CompPlayerArch>(CompPlayerArch.Player.Set(new CompPlayer((long)g.RawValue, false))));
        ViewBase view;
        using (var tx = dbe.CreateQuickTransaction())
        {
            view = tx.Query<CompPlayerArch>().WhereField<CompPlayer>(x => x.Active == 1 && x.GuildId > 0).ToView();
        }

        using (var r = dbe.CreateQuickTransaction())
        {
            view.Refresh(r);
        }

        Commit(dbe, tx => tx.OpenMut(p).Set(CompPlayerArch.Player, new CompPlayer((long)g.RawValue, true)));   // Active 0 -> 1, committed
        using (var t = dbe.CreateQuickTransaction())
        {
            t.OpenMut(p).Set(CompPlayerArch.Player, new CompPlayer(0, true));   // its own, never committed: GuildId 0 fails
            view.Refresh(t);
            t.Rollback();
        }

        using (var fresh = dbe.CreateQuickTransaction())
        {
            view.Refresh(fresh);
        }

        Assert.That(Has(view, p), Is.True, "committed: Active 1, GuildId g — the rolled-back write left nothing");
        view.Dispose();
    }
}
