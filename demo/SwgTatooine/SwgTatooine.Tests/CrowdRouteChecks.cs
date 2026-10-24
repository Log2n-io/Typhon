using System;
using NUnit.Framework;
using SwgTatooine.Server;
using Typhon.Engine;

namespace SwgTatooine.Tests;

/// <summary>
/// The scripted crowd walks round a city's buildings on the bots' own search, rather than into them (Loïc, 2026-10-24).
/// </summary>
/// <remarks>
/// <para>
/// <b>Found on the served demo.</b> Steered in a straight line and collided since 2026-10-22, the crowd stopped against whatever building stood between it
/// and its destination: round one shuttleport 34 of 35 bodies stood still, several on the same coordinates, in its corners.
/// </para>
/// <para>
/// <b>The hardest building there is, from the side it cannot be crossed.</b> A scripted player is put on one side of a shuttleport and sent to the other,
/// so its straight line runs through the ring wall, the pad and the tower; it must arrive. Placed before the server ticks, as the dead-target case does.
/// </para>
/// </remarks>
[Category("Integration")]
[TestFixture]
[NonParallelizable]
public sealed class CrowdRouteChecks
{
    [Test]
    public void AScriptedPlayerSentThroughAShuttleportWalksRoundIt()
    {
        var dir = Worlds.NewDirectory();
        try
        {
            var config = Causality.LongEnough(dir, 600);
            config.WorkerCount = 1;
            using var sim = new TatooineSim(config);
            sim.Initialize();

            var (portX, portZ) = sim.Index.Shuttleports[0];
            var terrain = SwgTatooine.Server.Terrain.TerrainField.Shared(config.ContentScale);
            var (fromX, fromZ) = (portX - 45f, portZ + 3f);
            var (goalX, goalZ) = (portX + 45f, portZ - 3f);
            var walker = sim.Index.Players[0];
            using (var tx = sim.Dbe.CreateQuickTransaction())
            {
                var player = tx.OpenMut(walker);
                var state = player.Read(Player.State);
                state.Activity = PlayerActivity.Travelling;
                state.ActivityTicks = int.MaxValue / 2;
                player.Set(Player.State, state);
                var move = player.Read(Player.Move);
                move.DestX = goalX;
                move.DestZ = goalZ;
                move.SpeedMps = TatooineData.PlayerRunSpeedMps;
                move.VelX = 0f;
                move.VelZ = 0f;
                player.Set(Player.Move, move);
                var at = default(PlayerPlacement);
                at.SetAt(fromX, fromZ, terrain.GroundAt(fromX, fromZ), player.Read(Player.Bounds).HalfExtent);
                tx.Teleport(walker, Player.Bounds, RealmId.Default, in at);
                tx.Commit();
            }

            sim.Run();

            float x, z;
            using (var tx = sim.Dbe.CreateQuickTransaction())
            {
                var p = tx.Open(walker).Read(Player.Bounds);
                x = p.X;
                z = p.Z;
            }

            var left = MathF.Sqrt(((goalX - x) * (goalX - x)) + ((goalZ - z) * (goalZ - z)));
            var (planned, failed) = sim.RouteTotals;
            TestContext.Out.WriteLine($"port ({portX:F0}, {portZ:F0}); ended ({x:F1}, {z:F1}), {left:F1} m from the goal; routes {planned}, failed {failed}");
            Assert.Multiple(() =>
            {
                Assert.That(planned, Is.GreaterThan(0), "no route was planned: the crowd still walks straight");
                Assert.That(left, Is.LessThan(5f), "the walker did not get round the shuttleport");
            });
        }
        finally
        {
            Worlds.Delete(dir);
        }
    }
}
