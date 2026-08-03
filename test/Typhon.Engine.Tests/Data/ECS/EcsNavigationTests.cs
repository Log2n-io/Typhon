using System;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ── SingleVersion navigation fixtures (issue #623) ──────────────────────
// The shared CompGuild / CompPlayer types omit StorageMode, and the attribute defaults to Versioned — so every
// pre-existing navigation test exercised the one mode that worked. These declare the mode explicitly so the
// SingleVersion FK path is reachable from a test at all.
[Component("Typhon.Test.Nav.SvGuild", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct SvNavGuild
{
    [Index(AllowMultiple = true)] public int Level;
    [Index(AllowMultiple = true)] public int MemberCap;

    public SvNavGuild(int level, int memberCap)
    {
        Level = level;
        MemberCap = memberCap;
    }
}

[Component("Typhon.Test.Nav.SvPlayer", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct SvNavPlayer
{
    [Index(AllowMultiple = true), ForeignKey(typeof(SvNavGuild))]
    public long GuildId;
    [Index(AllowMultiple = true)] public int Score;

    public SvNavPlayer(long guildId, int score)
    {
        GuildId = guildId;
        Score = score;
    }
}

[Archetype]
class SvNavGuildArch : Archetype<SvNavGuildArch>
{
    public static readonly Comp<SvNavGuild> Guild = Register<SvNavGuild>();
}

[Archetype]
class SvNavPlayerArch : Archetype<SvNavPlayerArch>
{
    public static readonly Comp<SvNavPlayer> Player = Register<SvNavPlayer>();
}

/// <summary>
/// Tests for EcsQuery.NavigateField — FK-based navigation joins via the ECS API.
/// Uses CompPlayer (FK: GuildId → CompGuild) with CompPlayerArch (210) and CompGuildArch (209),
/// plus SvNavPlayer / SvNavGuild for the SingleVersion path (issue #623).
/// </summary>
class EcsNavigationTests : TestBase<EcsNavigationTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    protected override void RegisterComponents(DatabaseEngine dbe)
    {
        base.RegisterComponents(dbe);
        dbe.RegisterComponentFromAccessor<CompGuild>();
        dbe.RegisterComponentFromAccessor<CompPlayer>();
        dbe.RegisterComponentFromAccessor<SvNavGuild>();
        dbe.RegisterComponentFromAccessor<SvNavPlayer>();
    }

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();
        return dbe;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Execute — one-shot navigation query
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void NavigateField_Execute_FindsPlayersInHighLevelGuild()
    {
        using var dbe = SetupEngine();

        // Create guilds
        EntityId guild1, guild2;
        using (var tx = dbe.CreateQuickTransaction())
        {
            guild1 = tx.Spawn<CompGuildArch>(CompGuildArch.Guild.Set(new CompGuild(10, 50)));  // Level=10
            guild2 = tx.Spawn<CompGuildArch>(CompGuildArch.Guild.Set(new CompGuild(50, 100))); // Level=50
            tx.Commit();
        }

        // Create players referencing guilds
        EntityId player1, player2, player3;
        using (var tx = dbe.CreateQuickTransaction())
        {
            player1 = tx.Spawn<CompPlayerArch>(CompPlayerArch.Player.Set(new CompPlayer((long)guild1.RawValue, true)));
            player2 = tx.Spawn<CompPlayerArch>(CompPlayerArch.Player.Set(new CompPlayer((long)guild2.RawValue, true)));
            player3 = tx.Spawn<CompPlayerArch>(CompPlayerArch.Player.Set(new CompPlayer((long)guild2.RawValue, false)));
            tx.Commit();
        }

        // Navigate: players whose guild has Level >= 30
        using var txQ = dbe.CreateQuickTransaction();
        var result = txQ.Query<CompPlayerArch>()
            .NavigateField<CompPlayer, CompGuild>(p => p.GuildId)
            .Where((p, g) => g.Level >= 30)
            .Execute();

        Assert.That(result.Count, Is.EqualTo(2), "player2 + player3 in guild2 (Level=50)");
        Assert.That(result.Contains(player2), Is.True);
        Assert.That(result.Contains(player3), Is.True);
    }

    [Test]
    public void NavigateField_Execute_CombinesSourceAndTargetPredicates()
    {
        using var dbe = SetupEngine();

        EntityId guild;
        using (var tx = dbe.CreateQuickTransaction())
        {
            guild = tx.Spawn<CompGuildArch>(CompGuildArch.Guild.Set(new CompGuild(50, 100)));
            tx.Commit();
        }

        EntityId activePlayer, inactivePlayer;
        using (var tx = dbe.CreateQuickTransaction())
        {
            activePlayer = tx.Spawn<CompPlayerArch>(CompPlayerArch.Player.Set(new CompPlayer((long)guild.RawValue, true)));
            inactivePlayer = tx.Spawn<CompPlayerArch>(CompPlayerArch.Player.Set(new CompPlayer((long)guild.RawValue, false)));
            tx.Commit();
        }

        // Navigate: active players in guilds with Level >= 30
        using var txQ = dbe.CreateQuickTransaction();
        var result = txQ.Query<CompPlayerArch>()
            .NavigateField<CompPlayer, CompGuild>(p => p.GuildId)
            .Where((p, g) => p.Active == 1 && g.Level >= 30)
            .Execute();

        Assert.That(result.Count, Is.EqualTo(1), "Only the active player");
        Assert.That(result.Contains(activePlayer), Is.True);
    }

    [Test]
    public void NavigateField_Count()
    {
        using var dbe = SetupEngine();

        EntityId guild;
        using (var tx = dbe.CreateQuickTransaction())
        {
            guild = tx.Spawn<CompGuildArch>(CompGuildArch.Guild.Set(new CompGuild(50, 100)));
            tx.Commit();
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 5; i++)
            {
                tx.Spawn<CompPlayerArch>(CompPlayerArch.Player.Set(new CompPlayer((long)guild.RawValue, true)));
            }
            tx.Commit();
        }

        using var txQ = dbe.CreateQuickTransaction();
        var count = txQ.Query<CompPlayerArch>()
            .NavigateField<CompPlayer, CompGuild>(p => p.GuildId)
            .Where((p, g) => g.Level >= 30)
            .Count();

        Assert.That(count, Is.EqualTo(5));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // ToView — incremental navigation view
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void NavigateField_ToView_IncrementalRefresh()
    {
        using var dbe = SetupEngine();

        EntityId guild;
        using (var tx = dbe.CreateQuickTransaction())
        {
            guild = tx.Spawn<CompGuildArch>(CompGuildArch.Guild.Set(new CompGuild(50, 100)));
            tx.Commit();
        }

        EntityId player1;
        using (var tx = dbe.CreateQuickTransaction())
        {
            player1 = tx.Spawn<CompPlayerArch>(CompPlayerArch.Player.Set(new CompPlayer((long)guild.RawValue, true)));
            tx.Commit();
        }

        // Create navigation view
        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<CompPlayerArch>()
            .NavigateField<CompPlayer, CompGuild>(p => p.GuildId)
            .Where((p, g) => p.Active == 1 && g.Level >= 30)
            .ToView();

        Assert.That(view.Count, Is.EqualTo(1));
        Assert.That(view.Contains((long)player1.RawValue), Is.True);

        // Add another active player → should enter the view
        EntityId player2;
        using (var tx = dbe.CreateQuickTransaction())
        {
            player2 = tx.Spawn<CompPlayerArch>(CompPlayerArch.Player.Set(new CompPlayer((long)guild.RawValue, true)));
            tx.Commit();
        }

        using var txR = dbe.CreateQuickTransaction();
        view.Refresh(txR);

        Assert.That(view.Count, Is.EqualTo(2));
        // #660: assert the IDENTITY, not just the count. A delta published in the wrong PK space still bumps Count — it inserts a
        // key that no reverse lookup can ever match or evict — so a Count-only assertion passes while the view is quietly corrupt.
        Assert.That(view.Contains((long)player2.RawValue), Is.True, "the incremental entry must be keyed on the full EntityId");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Issues #623 / #662 — SingleVersion source components
    //
    // Two defects stacked here. A SingleVersion component has no CompRevTableSegment (ComponentTable allocates it only for
    // Versioned), so the FK index value addresses a component chunk rather than a CompRev chunk — navigation used to
    // dereference the CompRev segment unconditionally and died with a bare NullReferenceException (#623, fixed by
    // FkSourcePkResolver). Underneath that: a pure-SingleVersion archetype is CLUSTER-BACKED, so its field indexes live on
    // the archetype and the ComponentTable tree navigation scanned was empty. These tests stayed [Ignore]d until
    // FkReverseLookup gave the reverse lookup both index homes (#662).
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void NavigateField_Execute_SingleVersionSource_FindsPlayersInHighLevelGuild()
    {
        using var dbe = SetupEngine();

        EntityId guild1, guild2;
        using (var tx = dbe.CreateQuickTransaction())
        {
            guild1 = tx.Spawn<SvNavGuildArch>(SvNavGuildArch.Guild.Set(new SvNavGuild(10, 50)));   // Level=10
            guild2 = tx.Spawn<SvNavGuildArch>(SvNavGuildArch.Guild.Set(new SvNavGuild(50, 100)));  // Level=50
            tx.Commit();
        }

        EntityId player1, player2, player3;
        using (var tx = dbe.CreateQuickTransaction())
        {
            player1 = tx.Spawn<SvNavPlayerArch>(SvNavPlayerArch.Player.Set(new SvNavPlayer((long)guild1.RawValue, 5)));
            player2 = tx.Spawn<SvNavPlayerArch>(SvNavPlayerArch.Player.Set(new SvNavPlayer((long)guild2.RawValue, 7)));
            player3 = tx.Spawn<SvNavPlayerArch>(SvNavPlayerArch.Player.Set(new SvNavPlayer((long)guild2.RawValue, 9)));
            tx.Commit();
        }

        using var txQ = dbe.CreateQuickTransaction();
        var result = txQ.Query<SvNavPlayerArch>()
            .NavigateField<SvNavPlayer, SvNavGuild>(p => p.GuildId)
            .Where((p, g) => g.Level >= 30)
            .Execute();

        Assert.That(result.Count, Is.EqualTo(2), "player2 + player3 are in guild2 (Level=50)");
        Assert.That(result.Contains(player2), Is.True);
        Assert.That(result.Contains(player3), Is.True);
        Assert.That(result.Contains(player1), Is.False, "player1's guild is Level=10");
    }

    [Test]
    public void NavigateField_Execute_SingleVersionSource_CombinesSourceAndTargetPredicates()
    {
        using var dbe = SetupEngine();

        EntityId guild;
        using (var tx = dbe.CreateQuickTransaction())
        {
            guild = tx.Spawn<SvNavGuildArch>(SvNavGuildArch.Guild.Set(new SvNavGuild(50, 100)));
            tx.Commit();
        }

        EntityId highScore;
        using (var tx = dbe.CreateQuickTransaction())
        {
            highScore = tx.Spawn<SvNavPlayerArch>(SvNavPlayerArch.Player.Set(new SvNavPlayer((long)guild.RawValue, 90)));
            tx.Spawn<SvNavPlayerArch>(SvNavPlayerArch.Player.Set(new SvNavPlayer((long)guild.RawValue, 10)));
            tx.Commit();
        }

        using var txQ = dbe.CreateQuickTransaction();
        var result = txQ.Query<SvNavPlayerArch>()
            .NavigateField<SvNavPlayer, SvNavGuild>(p => p.GuildId)
            .Where((p, g) => p.Score >= 50 && g.Level >= 30)
            .Execute();

        Assert.That(result.Count, Is.EqualTo(1), "only the high-scoring player in a high-level guild");
        Assert.That(result.Contains(highScore), Is.True);
    }

    [Test]
    public void NavigateField_ToView_SingleVersionSource_RefreshesOnSourceAndTargetChanges()
    {
        using var dbe = SetupEngine();

        EntityId guild;
        using (var tx = dbe.CreateQuickTransaction())
        {
            guild = tx.Spawn<SvNavGuildArch>(SvNavGuildArch.Guild.Set(new SvNavGuild(50, 100)));
            tx.Commit();
        }

        EntityId player1;
        using (var tx = dbe.CreateQuickTransaction())
        {
            player1 = tx.Spawn<SvNavPlayerArch>(SvNavPlayerArch.Player.Set(new SvNavPlayer((long)guild.RawValue, 90)));
            tx.Commit();
        }

        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<SvNavPlayerArch>()
            .NavigateField<SvNavPlayer, SvNavGuild>(p => p.GuildId)
            .Where((p, g) => p.Score >= 50 && g.Level >= 30)
            .ToView();

        Assert.That(view.Count, Is.EqualTo(1));
        Assert.That(view.Contains((long)player1.RawValue), Is.True);

        // Source-side change: a second qualifying player enters the view.
        EntityId player2;
        using (var tx = dbe.CreateQuickTransaction())
        {
            player2 = tx.Spawn<SvNavPlayerArch>(SvNavPlayerArch.Player.Set(new SvNavPlayer((long)guild.RawValue, 80)));
            tx.Commit();
        }

        using (var txR = dbe.CreateQuickTransaction())
        {
            view.Refresh(txR);
        }

        Assert.That(view.Count, Is.EqualTo(2), "player2 also qualifies");

        // Target-side change: dropping the guild below the threshold must evict BOTH sources. This is the fan-out
        // path (NavigationView.ReverseLookupAndUpdate) — the second of the two sites that dereferenced CompRev.
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(guild).Write(SvNavGuildArch.Guild).Level = 5;
            tx.Commit();
        }
        // SingleVersion components are mutated IN PLACE, so the old key is parked in a shadow buffer and both the index update and the view delta are
        // emitted at the tick fence, not at commit. Without this the target-side delta never reaches the view.
        dbe.WriteTickFence(1);

        using (var txR = dbe.CreateQuickTransaction())
        {
            view.Refresh(txR);
        }

        Assert.That(view.Count, Is.EqualTo(0), "guild dropped below Level 30 — both players evicted via reverse lookup");
        Assert.That(view.Contains((long)player2.RawValue), Is.False);
    }
}
