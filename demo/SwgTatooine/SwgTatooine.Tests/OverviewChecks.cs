using NUnit.Framework;
using SwgTatooine.Server;
using Typhon.Engine;

namespace SwgTatooine.Tests;

/// <summary>
/// <see cref="Overview"/>: a player switching to the god camera is served the god camera's view of its planet, keeps its avatar, and comes back to the
/// radius it had.
/// </summary>
/// <remarks>
/// <b>Proved with lairs.</b> A <see cref="CreatureLair"/> reaches the god profiles only (04-protocol § 5), so a player session that holds one is being served
/// a god camera's view, and one that holds none is not — an observation on the frames, not on a server-side flag.
/// </remarks>
[Category("Integration")]
[TestFixture]
[NonParallelizable]
internal sealed class OverviewChecks : ServedChecks
{
    private SimConfig Config()
    {
        var config = Worlds.Served(Dir);
        config.WorkerCount = 1;
        return config;
    }

    /// <summary>A served world with its harness.</summary>
    private TatooineSim Serve(SimConfig config)
    {
        var sim = new TatooineSim(config);
        sim.Initialize();
        Harness = new SessionHarness(sim);
        return sim;
    }

    /// <summary>Ticks until <paramref name="done"/> holds, one tick at a time.</summary>
    private void Until(System.Func<bool> done, string what) =>
        SessionHarness.Until(
            () =>
            {
                Harness.Ticks(1);
                return done();
            },
            what);

    /// <summary>On, the session holds the planet's lairs and stays on its planet with its avatar; off, the lairs are gone again.</summary>
    [Test]
    [CancelAfter(60_000)]
    public void TheGodCameraServesAPlayerTheWholePlanetAndGivesItBack()
    {
        using var sim = Serve(Config());
        var player = new PlayerProbe(Harness);
        player.UntilSettled();
        var avatar = TatooineReplication.ControlledBy(sim.Dbe, player.Session);
        Assert.That(player.Holding(nameof(CreatureLair)), Is.Zero, "the premise: a player's own profile serves no lair");

        player.AskOverview(true);
        Until(() => player.Holding(nameof(CreatureLair)) > 0, "a lair to arrive once the player is in the god camera");
        Assert.Multiple(() =>
        {
            Assert.That(player.Drain().RealmId, Is.EqualTo(0), "the god camera moved the player off its planet");
            Assert.That(TatooineReplication.ControlledBy(sim.Dbe, player.Session), Is.EqualTo(avatar), "the god camera unbound the avatar");
            Assert.That(TatooineReplication.OverviewAsks.Live, Is.EqualTo(1));
        });

        player.AskOverview(false);
        Until(() => player.Holding(nameof(CreatureLair)) == 0 && player.Holding(nameof(WorldObject)) > 0, "the player's own view to come back");
        Assert.Multiple(() =>
        {
            Assert.That(TatooineReplication.OverviewAsks.Live, Is.Zero);
            Assert.That(player.Drain().Refusals, Is.Empty);
        });
    }

    /// <summary>A player that widened its view comes back from the god camera at that radius, not at the profile's own.</summary>
    /// <remarks>
    /// The engine returns a session to its new profile's own radius on every profile change, so this is the server remembering the ask. Stood outside the
    /// widest city, as <see cref="ViewRadiusChecks"/> does, so the buildings it holds are the sphere's business and the radius shows in their count.
    /// </remarks>
    [Test]
    [CancelAfter(60_000)]
    public void TheWidenedRadiusComesBackAfterTheGodCamera()
    {
        using var sim = Serve(Config());
        var player = new PlayerProbe(Harness);
        var city = Worlds.WidestCity(sim);
        var standoff = city.Radius + (float)(TatooineReplication.CityReachM + TatooineReplication.CityLeaveM) + 32f;
        Worlds.Park(sim, player.Session, city.X + standoff, city.Z);
        player.UntilSettled();
        Harness.Ticks(10);
        var narrow = player.Holding(nameof(WorldObject));

        player.AskRadius((uint)TatooineReplication.PlayerMaxRadiusM);
        Until(() => player.Holding(nameof(WorldObject)) > narrow, "the wide radius to fill");

        player.AskOverview(true);
        Until(() => player.Holding(nameof(CreatureLair)) > 0, "the god camera");
        player.AskOverview(false);
        Until(() => player.Holding(nameof(CreatureLair)) == 0, "the player's own view");

        // The RESET that ends the overview refills the sphere from nothing, so the count is compared with the narrow one rather than the wide one.
        Until(() => player.Holding(nameof(WorldObject)) > narrow, $"the widened radius to come back (held {narrow} at the profile's own)");
        Assert.That(player.Drain().Refusals, Is.Empty);
    }

    /// <summary>Refused inside a building, and ended by the server when the avatar goes into one.</summary>
    [Test]
    [CancelAfter(60_000)]
    public void TheGodCameraIsForPlanetsOnly()
    {
        using var sim = Serve(Config());
        var player = new PlayerProbe(Harness);
        player.UntilSettled();
        var avatar = TatooineReplication.ControlledBy(sim.Dbe, player.Session);
        var interior = (ushort)sim.InteriorsPerPlanet;

        // Ended by the server: in the god camera, then through a door.
        player.AskOverview(true);
        Until(() => TatooineReplication.OverviewAsks.Live == 1, "the god camera");
        Worlds.Teleport(sim, avatar, interior, Worlds.At(0f, 1f));
        Until(() => player.Drain().RealmId == interior && TatooineReplication.OverviewAsks.Live == 0, "the overview to end inside the building");

        // Refused: asked for indoors.
        player.AskOverview(true);
        Until(() => TatooineReplication.OverviewAsks.Refused >= 1, "the indoor ask to be refused");
        Harness.Ticks(2);
        Assert.Multiple(() =>
        {
            Assert.That(player.Drain().Refusals, Has.Member(TatooineReplication.OverviewNotOnPlanet));
            Assert.That(TatooineReplication.OverviewAsks.Live, Is.Zero);
            Assert.That(player.Holding(nameof(CreatureLair)), Is.Zero, "a refused overview served the planet's lairs indoors");
        });
    }

    /// <summary>A world whose <c>--max-spectators</c> has one place, with a player in it.</summary>
    private PlayerProbe OneSpectatorPlace(out TatooineSim sim)
    {
        var config = Config();
        config.MaxSpectators = 1;
        sim = Serve(config);
        var player = new PlayerProbe(Harness);
        player.UntilSettled();
        return player;
    }

    /// <summary>Refused while a god session holds the only spectator place: an overview costs what a god session does.</summary>
    /// <remarks>One ask per case: the command's burst is two a second, and a case that toggled three times would be measuring the rate limit.</remarks>
    [Test]
    [CancelAfter(60_000)]
    public void TheGodCameraIsRefusedWhenTheSpectatorPlacesAreTaken()
    {
        var player = OneSpectatorPlace(out var sim);
        using (sim)
        {
            var god = Harness.Connect(TatooineReplication.GodKind);
            Until(() => god.Received(Typhon.Protocol.MessageTypes.Welcome), "the god session to take the place");

            player.AskOverview(true);
            Until(() => TatooineReplication.OverviewAsks.Refused >= 1, "the overview to be refused while the god session holds the place");
            Harness.Ticks(2);
            Assert.Multiple(() =>
            {
                Assert.That(player.Drain().Refusals, Has.Member(TatooineReplication.OverviewFull));
                Assert.That(TatooineReplication.OverviewAsks.Live, Is.Zero);
                Assert.That(player.Holding(nameof(CreatureLair)), Is.Zero, "a refused overview served the planet's lairs");
            });
        }
    }

    /// <summary>A god session is refused while an overview holds the only spectator place.</summary>
    [Test]
    [CancelAfter(60_000)]
    public void AGodSessionIsRefusedWhileAnOverviewTakesThePlace()
    {
        var player = OneSpectatorPlace(out var sim);
        using (sim)
        {
            player.AskOverview(true);
            Until(() => TatooineReplication.OverviewAsks.Live == 1, "the god camera");

            var god = Harness.Connect(TatooineReplication.GodKind);
            Until(() => god.Closed != null, "the god session to be refused while the overview holds the only spectator place");
            Assert.That(god.Received(Typhon.Protocol.MessageTypes.Welcome), Is.False, "the god session was admitted over the cap");
        }
    }
}
