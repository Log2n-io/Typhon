using System;
using System.Threading.Tasks;
using AsteroidField.Bot;
using AsteroidField.Client;
using AsteroidField.Server;
using NUnit.Framework;

namespace AsteroidField.Tests;

/// <summary>
/// The Asteroid Field demo end to end, in one process: the server over real TCP, and pilots reading it through <see cref="BeltLink"/> — the client
/// core the Unity project compiles. What a Unity pilot relies on is asserted here, so a change that breaks the demo breaks a test before it breaks
/// the Editor.
/// </summary>
[TestFixture]
[NonParallelizable]
public class AsteroidFieldTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    private static AsteroidServer StartServer(int holdCapacity = AsteroidServer.DefaultHoldCapacity) => AsteroidServer.Start(new ServerOptions
    {
        Port = 0,
        Asteroids = 2000,
        Drones = 2,
        HoldCapacity = holdCapacity,
        PilotSpawnRadiusM = 20f,
    });

    [Test]
    public async Task APilotSeesTheAsteroidsAroundItsShipAndNothingBeyondTheView()
    {
        using var server = StartServer();
        await using var link = new BeltLink("127.0.0.1", server.Port);
        await link.ConnectAsync();
        var brain = new PilotBrain(link);

        var world = await Until(brain, w => w.SelfNetId != 0 && w.TryGetShip(w.SelfNetId, out _) && w.Asteroids.Count > 0);

        Assert.That(world.TryGetShip(world.SelfNetId, out var self), Is.True);
        self.PositionAt(world.Tick, out var sx, out var sy);
        foreach (var a in world.Asteroids)
        {
            a.PositionAt(world.Tick, out var x, out var y);
            var distance = Math.Sqrt(((x - sx) * (x - sx)) + ((y - sy) * (y - sy)));
            // The view's slack: the anchor moves only past R/48, and a drifting rock may have left since its last segment.
            Assert.That(distance, Is.LessThan(AsteroidServer.ViewRadiusM * 1.1), $"asteroid {a.NetId} is beyond the view");
            Assert.That(a.Size, Is.InRange(1, 4), "the onEnter size arrived");
            Assert.That(a.Ore, Is.GreaterThan(0));
        }

        Assert.That(world.Cargo, Is.Zero, "a fresh hold");
    }

    [Test]
    public async Task MiningFillsTheHoldTheServerSaysSoOnceAndSellingPays()
    {
        const int hold = 20;
        using var server = StartServer(hold);
        await using var link = new BeltLink("127.0.0.1", server.Port);
        await link.ConnectAsync();
        var brain = new PilotBrain(link);

        // The brain flies to the nearest rock, mines it, and sells on the reliable CargoFull: the credits are the proof the whole loop ran.
        var world = await Until(brain, w => brain.HoldsFull >= 1 && w.Credits > 0);

        Assert.Multiple(() =>
        {
            Assert.That(brain.HoldsFull, Is.EqualTo(1), "one full hold, told once");
            Assert.That(world.Credits, Is.EqualTo(hold * AsteroidServer.CreditsPerOre), "the hold, sold");
        });
    }

    [Test]
    public async Task AnotherPilotSeesTheShipAndItsBeam()
    {
        using var server = StartServer();
        await using var first = new BeltLink("127.0.0.1", server.Port);
        await using var second = new BeltLink("127.0.0.1", server.Port);
        await first.ConnectAsync();
        await second.ConnectAsync();
        var miner = new PilotBrain(first);
        var watcher = new PilotBrain(second);

        // The miner picks a rock and turns its beam on; the watcher, spawned within 20 m of it, must see the beam's target on the miner's ship.
        var minerWorld = await Until(miner, w => w.TryGetShip(w.SelfNetId, out var s) && s.Target != 0);
        minerWorld.TryGetShip(minerWorld.SelfNetId, out var minerShip);
        var seen = await Until(watcher, w => w.TryGetShip(minerShip.NetId, out var s) && s.Target != 0, stepOther: miner, steer: false);

        Assert.That(seen.TryGetShip(minerShip.NetId, out var fromOutside), Is.True);
        Assert.That(fromOutside.Target, Is.Not.Zero, "the beam's target travels as the asteroid's netId, to every pilot in range");
    }

    [Test]
    public async Task AnAsteroidThisPilotWasNeverShownCannotBeMined()
    {
        using var server = StartServer();
        await using var link = new BeltLink("127.0.0.1", server.Port);
        await link.ConnectAsync();
        var watcher = new PilotBrain(link);
        await Until(watcher, w => w.TryGetShip(w.SelfNetId, out _) && w.Asteroids.Count > 0, steer: false);

        // A netId far past any this session holds: TryResolve refuses it, so the server leaves the beam off.
        link.Mine(0x00FF_FFFF);
        var start = watcher.World.Tick;
        var world = await Until(watcher, w => w.Tick > start + 10, steer: false);
        world.TryGetShip(world.SelfNetId, out var self);
        Assert.That(self.Target, Is.Zero, "a target the client was never shown is refused");

        // The positive control: one it holds is accepted.
        var shown = world.Asteroids[0].NetId;
        link.Mine(shown);
        world = await Until(watcher, w => w.TryGetShip(w.SelfNetId, out var s) && s.Target == shown, steer: false);
        Assert.That(world.TryGetShip(world.SelfNetId, out self) && self.Target == shown, Is.True);
    }

    // Steps the brain (and another, so both sessions keep their commands flowing) until the condition holds on its world.
    private static async Task<BeltSnapshot> Until(PilotBrain brain, Func<BeltSnapshot, bool> condition, PilotBrain stepOther = null, bool steer = true)
    {
        var until = DateTime.UtcNow + Deadline;
        while (DateTime.UtcNow < until)
        {
            if (steer)
            {
                brain.Step();
            }
            else
            {
                brain.Observe();
            }

            stepOther?.Step();
            if (brain.World != null && condition(brain.World))
            {
                return brain.World;
            }

            await Task.Delay(25);
        }

        Assert.Fail($"timed out after {Deadline.TotalSeconds} s; last frame {brain.World?.Tick}");
        return null;
    }
}
