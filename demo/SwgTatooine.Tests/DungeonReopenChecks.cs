using System.IO;
using NUnit.Framework;
using Typhon.Engine;

namespace SwgTatooine.Tests;

/// <summary>
/// Reopening a persisted world that still had a dungeon open.
/// </summary>
/// <remarks>
/// <para>
/// A dungeon realm is registered at run time and persisted like any other, so a run that ends with one open leaves its catalog row <b>Live</b>. The next open
/// restores it registered — RLM-06 resolves a <i>Closing</i> realm at open, but a Live one is simply live again — while the slot counter is process state and
/// restarts at zero, walking straight into it. <c>Register</c> then threw "Realm N is already registered" from inside the Dungeon system, which aborted the
/// tick, and every tick after it: the world never advanced.
/// </para>
/// <para>
/// <b>The failure mode is what makes this worth a fixture.</b> Under <c>--serve</c> the process stays up, binds its ports, answers HTTP and accepts profiler
/// attachments while sitting at tick 0 forever — so it reads as a hang, not a crash. It cost a session twenty minutes chasing "the reopen is slow" before the
/// exception was found in the log. Nor does it need a kill: a measured run that simply ends while a dungeon is open is enough, which is the default for any
/// run shorter than <c>DungeonStayS</c>.
/// </para>
/// </remarks>
[TestFixture]
public sealed class DungeonReopenChecks
{
    /// <summary>Opens a dungeon almost at once and never closes it, so the run ends with the realm Live — the state that used to poison the next open.</summary>
    private static SimConfig WithAnOpenDungeon(string dir)
    {
        var config = Persisted.Config(dir, persist: true, ticks: 40);
        config.Dungeons = 1;
        config.DungeonIntervalS = 0.1f;
        config.DungeonStayS = 10_000f;
        return config;
    }

    [Test]
    public void AWorldReopenedWithADungeonStillOpenKeepsTicking()
    {
        var dir = Worlds.NewDirectory();
        try
        {
            int dungeonRealm;
            using (var first = Persisted.Run(WithAnOpenDungeon(dir)))
            {
                dungeonRealm = first.FirstDungeonRealm;

                // The precondition the whole test rests on. Without it the second run would reopen a world with no
                // registered dungeon and pass while exercising nothing.
                Assert.That(first.Dbe.Realms.IsRegistered(new RealmId((ushort)dungeonRealm)), Is.True,
                    "the first run must end with its dungeon still registered — otherwise this fixture proves nothing");
            }

            using var reopened = Persisted.Run(WithAnOpenDungeon(dir));

            // A system that throws writes a crash artefact beside the database, so its absence is the direct statement
            // that no tick aborted. Asserting on tick counts instead would not distinguish "ran" from "aborted 40 times".
            var artefacts = Directory.GetFileSystemEntries(dir, "*.crash-*");
            Assert.That(artefacts, Is.Empty,
                "reopening a world whose dungeon was still open must not abort a tick; artefacts: " + string.Join(", ", artefacts));

            // And the realm the previous run left behind is still there, untouched: the fix skips the slot rather than
            // reclaiming it, because reclaiming means destroying contents that include the previous party's players.
            Assert.That(reopened.Dbe.Realms.IsRegistered(new RealmId((ushort)dungeonRealm)), Is.True,
                "the inherited dungeon realm is skipped, not unregistered");
        }
        finally
        {
            Worlds.Delete(dir);
        }
    }
}
