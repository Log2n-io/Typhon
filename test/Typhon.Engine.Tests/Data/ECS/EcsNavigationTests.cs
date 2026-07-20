using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// Tests for EcsQuery.NavigateField — FK-based navigation joins via the ECS API.
/// Uses CompPlayer (FK: GuildId → CompGuild) with CompPlayerArch (210) and CompGuildArch (209).
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
    }
}
