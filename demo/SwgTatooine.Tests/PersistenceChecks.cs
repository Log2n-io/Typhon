using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Typhon.Engine;

namespace SwgTatooine.Tests;

/// <summary>
/// Shared helpers for the P-1/P-2/P-3 fixtures: building a world, letting it run, and reading back what survived.
/// </summary>
internal static class Persisted
{
    /// <summary>A world small enough to build and reopen several times in one fixture, on a named database in <paramref name="dir"/>.</summary>
    public static SimConfig Config(string dir, bool persist, int ticks = 20)
    {
        var config = Worlds.Small(dir);
        config.Persist = persist;
        config.WarmTicks = 0;
        config.MeasuredTicks = ticks;
        config.Unpaced = true;
        config.WorkerCount = 4;
        return config;
    }

    /// <summary>Builds or reopens a world, runs it, and hands the instance back for inspection. The caller disposes.</summary>
    public static TatooineSim Run(SimConfig config)
    {
        var sim = new TatooineSim(config);
        sim.Initialize();
        sim.Run();
        return sim;
    }

    /// <summary>Entries in <paramref name="dir"/> that belong to a database of this name — the database itself is a DIRECTORY, not a file.</summary>
    public static string[] DatabaseEntries(string dir, string name) => Directory.GetFileSystemEntries(dir, name + ".*");

    /// <summary>A copy of the index lists a rebuild has to reproduce.</summary>
    public static (List<(float X, float Z)> Ports, List<(float X, float Z)> Doors) Geometry(TatooineSim sim) =>
        (new List<(float, float)>(sim.Index.Shuttleports), new List<(float, float)>(sim.Index.Portals));

    /// <summary>
    /// The largest distance between two coordinate lists, or <see cref="float.MaxValue"/> when they are not the same length.
    /// </summary>
    /// <remarks>
    /// <b>A distance and not an equality, because a coordinate does not round-trip bit for bit and should not be expected to.</b> The build records the raw
    /// <c>x</c> the generator drew; what is stored is the entity's AABB, so what a reader gets back is <c>(MinX + MaxX) / 2</c> — the same point to within float
    /// rounding of the half-extent, and not the same bits. Measured: 617 doors agreed to about a micrometre and an exact comparison failed on most of them.
    /// <para>
    /// The tolerance is a millimetre, which is four to five orders of magnitude below what the claim is about: a list re-derived from a different seed differs
    /// by hundreds of metres, so nothing that matters can hide under it.
    /// </para>
    /// </remarks>
    public static float MaxDistance(List<(float X, float Z)> a, List<(float X, float Z)> b)
    {
        if (a.Count != b.Count)
        {
            return float.MaxValue;
        }

        var worst = 0f;
        for (var i = 0; i < a.Count; i++)
        {
            var dx = a[i].X - b[i].X;
            var dz = a[i].Z - b[i].Z;
            worst = MathF.Max(worst, MathF.Sqrt((dx * dx) + (dz * dz)));
        }

        return worst;
    }

    /// <summary>A millimetre — see <see cref="MaxDistance"/>.</summary>
    public const float CoordinateTolerance = 0.001f;
}

/// <summary>
/// P-1: one persistence surface — one flag, one default, one name, one deletion decision.
/// </summary>
/// <remarks>
/// The two source items (SWG-04 and CLI-05) each proposed a flag for this with opposite senses and opposite defaults, which is what made merging them the first
/// thing WP-3's scoping did. What is asserted here is the settled answer: <c>--persist</c> opts in, there is no <c>--fresh</c>, and the process id is out of the
/// name unconditionally.
/// </remarks>
[TestFixture]
[NonParallelizable]
public sealed class PersistenceSurfaceTests
{
    private string _dir;

    [SetUp]
    public void NewDirectory() => _dir = Worlds.NewDirectory();

    [TearDown]
    public void Cleanup() => Worlds.Delete(_dir);

    [Test]
    public void TheDatabaseNameCarriesNoProcessId_SoAKilledRunLeavesOne()
    {
        // The 154 GB incident, made structural. The name used to be SwgTatooine_{Environment.ProcessId}, so every run — and every run killed before it could
        // delete its own file — left a database nothing would ever clean up, because the only process that knew the name was gone.
        var config = Persisted.Config(_dir, persist: true, ticks: 5);
        using (var sim = Persisted.Run(config))
        {
            Assert.That(sim.Reopened, Is.False, "precondition: the first run builds");
        }

        var entries = Persisted.DatabaseEntries(_dir, SimConfig.DefaultDatabaseName);
        Assert.Multiple(() =>
        {
            Assert.That(config.DatabaseName, Is.EqualTo(SimConfig.DefaultDatabaseName), "the default name is the bare one");
            Assert.That(entries, Has.Length.EqualTo(1), $"one database, not one per run: found {string.Join(", ", entries)}");
            Assert.That(Path.GetFileName(entries[0]), Does.Not.Contain(Environment.ProcessId.ToString()),
                "the process id is back in the database name, so every killed run will leave its own file again");
        });
    }

    [Test]
    public void WithoutPersist_TheDatabaseIsDeletedAtStart_AndTheWorldIsFresh()
    {
        // A marker no generator would produce: a player parked at a coordinate the world build never chooses. If the second run finds it, the database was
        // reused; if it does not, the file was deleted — which is the assertion, and it is checked on world CONTENT rather than on a file timestamp.
        EntityId marked;
        using (var sim = Persisted.Run(Persisted.Config(_dir, persist: true, ticks: 5)))
        {
            marked = sim.Index.Players[0];
            using var tx = sim.Dbe.CreateQuickTransaction();
            var at = default(PlayerPlacement);
            at.SetAt(Marker, Marker, 0f, tx.Open(marked).Read(Player.Bounds).HalfExtent);
            tx.Teleport(marked, Player.Bounds, RealmId.Default, in at);
            tx.Commit();
        }

        using (var sim = Persisted.Run(Persisted.Config(_dir, persist: false, ticks: 5)))
        {
            Assert.That(sim.Reopened, Is.False, "without --persist the run must build, whatever was on disk");
            using var tx = sim.Dbe.CreateQuickTransaction();
            var found = false;
            foreach (var id in sim.Index.Players)
            {
                var at = tx.Open(id).Read(Player.Bounds);
                found |= MathF.Abs(at.X - Marker) < 1f && MathF.Abs(at.Z - Marker) < 1f;
            }

            Assert.That(found, Is.False, "a player from the previous run survived, so the database was not deleted");
        }
    }

    [Test]
    public void WithPersist_TheWorldIsReopened_AndAMutationSurvives()
    {
        EntityId marked;
        using (var sim = Persisted.Run(Persisted.Config(_dir, persist: true, ticks: 5)))
        {
            marked = sim.Index.Players[0];
            using var tx = sim.Dbe.CreateQuickTransaction();

            // Idle and pinned, so the simulation does not walk it away from the marker before the second run can look. The VELOCITY has to go too: PlayerThink
            // leaves an idle player alone, but PlayerMove integrates whatever velocity it is carrying, and a reopened player still running at 12 m/s covers
            // several metres in the five ticks this test runs — which failed the assertion for a reason that had nothing to do with persistence.
            var player = tx.OpenMut(marked);
            ref var state = ref player.Write(Player.State);
            state.Activity = PlayerActivity.Idle;
            state.ActivityTicks = int.MaxValue / 2;
            ref var move = ref player.Write(Player.Move);
            move.VelX = 0f;
            move.VelZ = 0f;
            move.DestX = Marker;
            move.DestZ = Marker;
            var at = default(PlayerPlacement);
            at.SetAt(Marker, Marker, 0f, player.Read(Player.Bounds).HalfExtent);
            tx.Teleport(marked, Player.Bounds, RealmId.Default, in at);
            tx.Commit();
        }

        using (var sim = Persisted.Run(Persisted.Config(_dir, persist: true, ticks: 5)))
        {
            Assert.That(sim.Reopened, Is.True, "the second --persist run must reopen rather than build");
            using var tx = sim.Dbe.CreateQuickTransaction();
            var at = tx.Open(marked).Read(Player.Bounds);
            Assert.That(MathF.Abs(at.X - Marker), Is.LessThan(1f), "the mutation did not survive the close: the world was not persisted");
            Assert.That(MathF.Abs(at.Z - Marker), Is.LessThan(1f));
        }
    }

    [Test]
    public void APersistedDatabaseReopensHealthy_WhichIsWhatTheFinalCheckpointIsFor()
    {
        using (var sim = Persisted.Run(Persisted.Config(_dir, persist: true, ticks: 30)))
        {
            Assert.That(sim.Reopened, Is.False);
        }

        using var reopened = Persisted.Run(Persisted.Config(_dir, persist: true, ticks: 5));
        var report = reopened.Dbe.RunStorageIntegrityCheck();

        Assert.Multiple(() =>
        {
            Assert.That(reopened.Reopened, Is.True);
            Assert.That(report.IsHealthy, Is.True, $"integrity issues after a clean close and reopen: {Describe(report)}");
            Assert.That(report.OrphanPageCount, Is.Zero);
            Assert.That(report.PhantomPageCount, Is.Zero);

            // A pass over nothing is not a pass. This distinguishes "the audit found no problem" from "the audit looked at no clusters".
            Assert.That(report.VisibilitySummaryClustersChecked, Is.GreaterThan(0), "the audit examined no clusters, so its verdict says nothing");
        });
    }

    private static string Describe(StorageIntegrityReport report)
    {
        var lines = new List<string>();
        foreach (var issue in report.Issues)
        {
            lines.Add(issue.ToString());
        }

        return lines.Count == 0 ? "(none)" : string.Join(" | ", lines);
    }

    /// <summary>A coordinate the world build never chooses — well inside the world, and nowhere near a city or a spawn region.</summary>
    private const float Marker = 137.5f;
}

/// <summary>
/// P-2: a reopened world's <see cref="WorldIndex"/> is derived from the entities on disk, and its census matches the run that built it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The assertion that carries the item is the seed one.</b> Cities and points of interest come from the map and are the same in every world built from one
/// configuration, so matching them proves nothing. Shuttleports and doors are RNG coordinates: which building in a city is its port, and which buildings have
/// doors, exist nowhere but on the entities. Reopening with a DIFFERENT seed and finding the original coordinates is what separates "read from disk" from
/// "re-derived from the generator" — under re-derivation the new seed would produce new coordinates.
/// </para>
/// <para>It is also the #1054 watch: a spatial query on a reopened world must answer.</para>
/// </remarks>
[TestFixture]
[NonParallelizable]
public sealed class WorldIndexRebuildTests
{
    private string _dir;
    private WorldCensus _built;
    private (List<(float X, float Z)> Ports, List<(float X, float Z)> Doors) _geometry;
    private int _realmsAtBuild;

    [SetUp]
    public void BuildAWorld()
    {
        _dir = Worlds.NewDirectory();
        using var sim = Persisted.Run(Persisted.Config(_dir, persist: true, ticks: 30));
        _built = sim.Census;
        _geometry = Persisted.Geometry(sim);
        _realmsAtBuild = sim.Dbe.Realms.Counts.Active + sim.Dbe.Realms.Counts.Simulated + sim.Dbe.Realms.Counts.Dormant;
        Assert.That(sim.Reopened, Is.False, "precondition: this run builds");
        Assert.That(_geometry.Ports, Is.Not.Empty, "precondition: the built world has shuttleports");
    }

    [TearDown]
    public void Cleanup() => Worlds.Delete(_dir);

    [Test]
    public void AReopenedWorld_HasTheSameCensus()
    {
        using var sim = Persisted.Run(Persisted.Config(_dir, persist: true, ticks: 5));

        Assert.Multiple(() =>
        {
            Assert.That(sim.Reopened, Is.True);
            Assert.That(sim.Census.StaticObjects, Is.EqualTo(_built.StaticObjects), "static objects");
            Assert.That(sim.Census.PlayerStructures, Is.EqualTo(_built.PlayerStructures), "player structures");
            Assert.That(sim.Census.Lairs, Is.EqualTo(_built.Lairs), "lairs");
            Assert.That(sim.Census.Creatures, Is.EqualTo(_built.Creatures), "creatures");
            Assert.That(sim.Census.CityNpcs, Is.EqualTo(_built.CityNpcs), "city NPCs");
            Assert.That(sim.Census.Players, Is.EqualTo(_built.Players), "players");

            // The composition, not only the size (S0-5). It also proves CreatureBrain.Template survived the round trip, which is the one census field the
            // rebuild has to read per entity rather than count.
            Assert.That(sim.Census.CreaturesByTemplate, Is.EqualTo(_built.CreaturesByTemplate), "creature composition by template");
        });
    }

    [Test]
    public void AReopenedWorldsIndexComesFromTheEntities_NotFromTheGenerator()
    {
        // A DIFFERENT SEED. Under a rebuild that re-ran the generator, every shuttleport and every door would move; under one that reads the entities, the seed
        // is irrelevant because the coordinates are already on disk. That is the whole of the distinction this item is about.
        var config = Persisted.Config(_dir, persist: true, ticks: 5);
        config.Seed += 1;
        using var sim = Persisted.Run(config);

        Assert.Multiple(() =>
        {
            Assert.That(sim.Reopened, Is.True);
            Assert.That(Persisted.MaxDistance(sim.Index.Shuttleports, _geometry.Ports), Is.LessThan(Persisted.CoordinateTolerance),
                "the shuttleports moved when the seed changed, so they were re-derived from the generator rather than read from the entities");
            Assert.That(Persisted.MaxDistance(sim.Index.Portals, _geometry.Doors), Is.LessThan(Persisted.CoordinateTolerance),
                $"the doors moved when the seed changed (built {_geometry.Doors.Count}, rebuilt {sim.Index.Portals.Count})");
        });
    }

    [Test]
    public void AReopenedWorld_KeepsItsRealms_AndItsSpatialIndexStillAnswers()
    {
        using var sim = Persisted.Run(Persisted.Config(_dir, persist: true, ticks: 10));
        var realms = sim.Dbe.Realms.Counts;

        // The transaction is what supplies the epoch scope the query's chunk accessors are rented under; without it the enumerator asserts
        // "ChunkAccessor must be created inside an epoch scope" rather than answering.
        HashSet<EntityId> players;
        using (var tx = sim.Dbe.CreateQuickTransaction())
        {
            players = Worlds.PlayersIn(sim.Dbe, 0, Worlds.Everywhere);
            tx.Commit();
        }

        Assert.Multiple(() =>
        {
            Assert.That(realms.Active + realms.Simulated + realms.Dormant, Is.EqualTo(_realmsAtBuild), "the realm registrations were not re-issued");

            // The #1054 watch. A reopened world whose spatial queries answer zero is the most valuable bug WP-3 could find; this is where it would show.
            Assert.That(players, Is.Not.Empty, "#1054: the reopened world's spatial index answered nothing");
            Assert.That(players.Count, Is.EqualTo(_built.Players), "the index answered, but not for every player");
        });
    }

    [Test]
    public void AReopenedWorldsTravellers_HeadForRealPlaces_NotForTheOrigin()
    {
        // The failure this guards is a rebuilt index full of default values: (0, 0) is the centre of the planet and a plausible-looking coordinate, so a
        // traveller walking there looks like a traveller rather than like a bug.
        using var sim = Persisted.Run(Persisted.Config(_dir, persist: true, ticks: 40));
        var half = sim.Config.WorldEdgeM * 0.5f;
        var atOrigin = 0;
        var outOfWorld = 0;

        using (var tx = sim.Dbe.CreateQuickTransaction())
        {
            foreach (var id in sim.Index.Players)
            {
                var move = tx.Open(id).Read(Player.Move);
                if (MathF.Abs(move.DestX) < 1f && MathF.Abs(move.DestZ) < 1f)
                {
                    atOrigin++;
                }

                if (MathF.Abs(move.DestX) > half || MathF.Abs(move.DestZ) > half)
                {
                    outOfWorld++;
                }
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(atOrigin, Is.Zero, $"{atOrigin} player(s) are walking to (0, 0): the rebuilt index is handing out default coordinates");
            Assert.That(outOfWorld, Is.Zero, $"{outOfWorld} player(s) are walking off the planet");
            Assert.That(sim.Index.Cities, Is.Not.Empty, "precondition: the rebuilt index has cities to walk to");
            Assert.That(sim.Index.Shuttleports, Has.Count.EqualTo(sim.Index.Cities.Count), "every city needs its port, or its passengers walk into the desert");
        });
    }

    [Test]
    public void PossessionDoesNotSurviveARestart()
    {
        // A world saved while a client was connected reopens with a player marked as that client's, and the client is gone. Nothing would ever clear it —
        // PlayerThink skips anything not InProcess — so the player would stand still for the life of the process while the simulation waited for intents from a
        // session that does not exist.
        EntityId possessed;
        using (var sim = Persisted.Run(Persisted.Config(_dir, persist: true, ticks: 5)))
        {
            possessed = sim.Index.Players[0];
            using var tx = sim.Dbe.CreateQuickTransaction();
            var player = tx.OpenMut(possessed);
            player.Write(Player.Control).Kind = ControllerKind.Human;
            ref var session = ref player.Write(Player.Session);
            session.Controller = 4242u;
            tx.Commit();
        }

        using (var sim = Persisted.Run(Persisted.Config(_dir, persist: true, ticks: 5)))
        {
            // Read out first: EntityRef is a ref struct and cannot be captured by Assert.Multiple's lambda (CS8175).
            byte kind;
            uint controller;
            using (var tx = sim.Dbe.CreateQuickTransaction())
            {
                var player = tx.Open(possessed);
                kind = player.Read(Player.Control).Kind;
                controller = player.Read(Player.Session).Controller;
            }

            Assert.Multiple(() =>
            {
                Assert.That(kind, Is.EqualTo(ControllerKind.InProcess),
                    "a player possessed when the world was saved is still marked as somebody's, and that session is gone");
                Assert.That(controller, Is.Zero, "the dead session's id is still on the player");
            });
        }
    }
}

/// <summary>
/// P-3: a run that dies leaves an artefact that can be read instead of reproduced.
/// </summary>
/// <remarks>
/// <b>Driven by a real fault through the real path.</b> <c>--fault-at-tick</c> makes a system body throw the way a defect would; the runtime decides the tick is
/// aborted, raises <c>OnTickAborted</c>, and the handler writes the directory. Calling the writer directly would assert that a file-writing method writes files,
/// which is not the claim — and it would have missed the thing this actually found: under the engine's default exception policy the event never fires at all, so
/// the demo's abort reporting had been dead code.
/// </remarks>
[TestFixture]
[NonParallelizable]
public sealed class CrashArtefactTests
{
    private string _dir;
    private string _artefact;

    [OneTimeSetUp]
    public void RunUntilItDies()
    {
        _dir = Worlds.NewDirectory();
        var config = Persisted.Config(_dir, persist: false, ticks: 40);
        config.FaultAtTick = 12;
        using var sim = new TatooineSim(config);
        sim.Initialize();
        sim.Run();
        _artefact = sim.CrashArtefactPath;
    }

    [OneTimeTearDown]
    public void Cleanup() => Worlds.Delete(_dir);

    [Test]
    public void AnAbortedTick_WritesAnArtefactBesideTheDatabase()
    {
        Assert.That(_artefact, Is.Not.Null, "no artefact was written: the tick abort did not reach the handler");
        Assert.That(Directory.Exists(_artefact), Is.True, $"the artefact path does not exist: {_artefact}");
        Assert.That(Path.GetDirectoryName(_artefact), Is.EqualTo(Path.TrimEndingDirectorySeparator(_dir)).IgnoreCase,
            "the artefact must sit beside the database, which is the other half of the evidence");
    }

    [Test]
    public void TheArtefactNamesTheFailingSystem_AndTheTick()
    {
        var reason = File.ReadAllText(Path.Combine(_artefact, "reason.txt"));
        var exception = File.ReadAllText(Path.Combine(_artefact, "exception.txt"));

        Assert.Multiple(() =>
        {
            Assert.That(reason, Does.Contain("tick 12"), "the reason must name the tick the fault happened on");
            Assert.That(reason, Does.Contain("SpatialTelemetry"), "the reason must name the system that failed");

            // The whole exception, stack included: a type and a message name a symptom, not the line that raised it.
            Assert.That(exception, Does.Contain("InvalidOperationException"));
            Assert.That(exception, Does.Contain("fault-at-tick"));
            Assert.That(exception, Does.Contain("SimBridge"), "the stack trace is missing, so the artefact names a symptom and not a line");
        });
    }

    [Test]
    public void TheArtefactCarriesTheApproachToTheFault_AndTheWorldItWasRunning()
    {
        var ticks = File.ReadAllLines(Path.Combine(_artefact, "ticks.csv"));
        var census = File.ReadAllText(Path.Combine(_artefact, "census.txt"));
        var config = File.ReadAllText(Path.Combine(_artefact, "config.txt"));

        Assert.Multiple(() =>
        {
            // A header plus the ticks before the fault. Twelve ticks in, so there is real history rather than an empty file with a header.
            Assert.That(ticks, Has.Length.GreaterThan(5), "the tick history is empty, so a build-up to the fault could not be seen");
            Assert.That(ticks[0], Is.EqualTo("index,ms"));
            Assert.That(ticks[1], Does.Match(@"^0,\d+\.\d+$"), "each line is a tick index and a duration in milliseconds");

            Assert.That(census, Does.Contain("entities"), "the census is missing, so the artefact does not say what world was running");
            Assert.That(census, Does.Contain("creatures:"), "the creature composition is missing (S0-5)");

            // Enough to run the same world again, which is what makes the artefact actionable rather than merely informative.
            Assert.That(config, Does.Contain("seed:"));
            Assert.That(config, Does.Contain("label:"));
            Assert.That(config, Does.Contain("cell-m:"));
        });
    }

    [Test]
    public void ARunThatDoesNotFault_WritesNoArtefact()
    {
        // The control. Without it, an implementation that wrote an artefact on every run would pass every assertion above.
        var dir = Worlds.NewDirectory();
        try
        {
            using var sim = new TatooineSim(Persisted.Config(dir, persist: false, ticks: 20));
            sim.Initialize();
            sim.Run();

            Assert.That(sim.CrashArtefactPath, Is.Null, "a healthy run wrote a crash artefact");
            Assert.That(Directory.GetDirectories(dir, "*.crash-*"), Is.Empty);
        }
        finally
        {
            Worlds.Delete(dir);
        }
    }
}
