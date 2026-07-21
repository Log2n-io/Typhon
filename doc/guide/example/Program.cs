// Runnable companion to doc/guide, and the source template `typhon new` emits. Every snippet in the guide is mirrored
// here so it is known to compile and run against the current engine. Run with:
//   dotnet run --project doc/guide/example
//
// It walks the guide's arc (declare -> spawn -> read -> transact -> query -> view -> tick) printing checkable text,
// then confirms the profiler wrote a trace. The data model lives in the Typhon.Samples.Swg assembly (SWG Light); the
// systems live in Systems.cs. Profiling is config-driven: typhon.telemetry.json turns it on, the engine self-wires it
// inside TyphonRuntime.Create, and the .typhon-trace is flushed when the engine is disposed — zero profiling code here.

using System;
using System.IO;
using System.Numerics;
using System.Threading;
using Typhon.Engine;
using Typhon.Samples.Swg;
using Typhon.Schema.Definition;
using SwgGuide;

// The profiler writes its trace here (see typhon.telemetry.json). Create the folder up front — the exporter opens the
// file when the runtime starts.
var tracePath = Path.GetFullPath(Path.Combine("captures", "guide.typhon-trace"));
Directory.CreateDirectory(Path.GetDirectoryName(tracePath));

// Scope the engine so it disposes (and flushes the trace) before we check for the trace file below.
{
    // Fresh DB each run: wipe any prior bundle before opening.
    new PagedMMFOptions { DatabaseName = "swg-guide", DatabaseDirectory = "." }.EnsureFileDeleted();

    // One call: names the database, registers the SWG Light components + archetype, configures the spatial grid
    // (required by the [SpatialIndex] on Footprint), and wires the archetypes — a ready-to-use engine.
    using var dbe = DatabaseEngine.Open("swg-guide.typhon", o => o
        .Register<Position>()
        .Register<Footprint>()
        .Register<Cargo>()
        .Register<Drift>()
        .Register<Extractor>()
        .ConfigureSpatialGrid(new SpatialGridConfig(Vector2.Zero, new Vector2(1000f, 1000f), cellSize: 50f)));

    // ════════════════════════════════════════════════════════════════════════
    Banner("ch.1 — spawn, read, query");
    // ════════════════════════════════════════════════════════════════════════

    EntityId probe;             // one drone we inspect throughout
    EntityId mover = default;   // a drone we watch move in ch.5
    using (var tx = dbe.CreateQuickTransaction())
    {
        // Deploy six drones across three resource kinds at distinct positions.
        for (int i = 0; i < 6; i++)
        {
            float x = 100f + i * 20f, y = 100f;
            var e = tx.Spawn<Harvester>(
                Harvester.Position.Set(new Position { P = new Point2F { X = x, Y = y } }),
                Harvester.Footprint.Set(PointFootprint(x, y)),
                Harvester.Cargo.Set(new Cargo { Amount = 0, Capacity = 1000 }),
                Harvester.Drift.Set(new Drift { Dx = 5f, Dy = 0f }),
                Harvester.Extractor.Set(new Extractor { ResourceKind = (i % 3) + 1, Rate = 5 }));
            if (i == 0) mover = e;
        }
        probe = tx.Spawn<Harvester>(
            Harvester.Position.Set(new Position { P = new Point2F { X = 10f, Y = 20f } }),
            Harvester.Footprint.Set(PointFootprint(10f, 20f)),
            Harvester.Cargo.Set(new Cargo { Amount = 250, Capacity = 1000 }),
            Harvester.Drift.Set(new Drift { Dx = 0f, Dy = 0f }),
            Harvester.Extractor.Set(new Extractor { ResourceKind = 1, Rate = 5 }));
        tx.Commit();
    }

    // The spatial index is maintained by the tick fence. Outside the runtime, run it once after spawning so
    // WhereNearby / WhereInAABB can filter (inside the runtime it runs every tick).
    dbe.WriteTickFence(1);

    using (var tx = dbe.CreateQuickTransaction())
    {
        var e = tx.Open(probe);
        var pos = e.Read(Harvester.Position);
        var cargo = e.Read(Harvester.Cargo);
        Console.WriteLine($"probe drone: cargo {cargo.Amount}/{cargo.Capacity} at ({pos.P.X}, {pos.P.Y})");

        int filling = tx.Query<Harvester>().Where<Cargo>(c => c.Amount < c.Capacity).Count();
        Console.WriteLine($"drones still filling: {filling}");
        int total = tx.Query<Harvester>().Count();
        Console.WriteLine($"total drones: {total}");
    }

    // ════════════════════════════════════════════════════════════════════════
    Banner("ch.2 — generated accessors (ReadAll)");
    // ════════════════════════════════════════════════════════════════════════

    using (var tx = dbe.CreateQuickTransaction())
    {
        var h = Harvester.ReadAll(tx, probe);
        Console.WriteLine($"ReadAll: kind={h.Extractor.ResourceKind} cargo={h.Cargo.Amount}/{h.Cargo.Capacity} pos=({h.Position.P.X},{h.Position.P.Y})");
    }

    // ════════════════════════════════════════════════════════════════════════
    Banner("ch.3 — transactions: write, rollback, snapshot");
    // ════════════════════════════════════════════════════════════════════════

    // Explicit UoW + transaction (the form ch.3 opens up).
    using (var uow = dbe.CreateUnitOfWork(DurabilityMode.GroupCommit))
    using (var tx = uow.CreateTransaction())
    {
        var e = tx.OpenMut(probe);
        e.Write(Harvester.Cargo).Amount += 100;   // Versioned write
        tx.Commit();
    }
    PrintCargo("after committed +100", dbe, probe);

    // Rollback: a Versioned write that never lands.
    using (var tx = dbe.CreateQuickTransaction())
    {
        tx.OpenMut(probe).Write(Harvester.Cargo).Amount += 5000;
        tx.Rollback();
    }
    PrintCargo("after rolled-back +5000", dbe, probe);

    // Snapshot isolation: a read-only transaction doesn't see later commits.
    using (var reader = dbe.CreateReadOnlyTransaction())
    {
        int before = reader.Open(probe).Read(Harvester.Cargo).Amount;
        using (var w = dbe.CreateQuickTransaction())
        {
            w.OpenMut(probe).Write(Harvester.Cargo).Amount += 50;
            w.Commit();
        }
        int after = reader.Open(probe).Read(Harvester.Cargo).Amount;
        Console.WriteLine($"reader snapshot held: {before} == {after} -> {before == after}");
    }
    PrintCargo("outside the reader, the +50 is visible", dbe, probe);

    // ════════════════════════════════════════════════════════════════════════
    Banner("ch.4 — queries, spatial, live views");
    // ════════════════════════════════════════════════════════════════════════

    using (var tx = dbe.CreateQuickTransaction())
    {
        int kind1 = tx.Query<Harvester>().WhereField<Extractor>(x => x.ResourceKind == 1).Count();   // indexed
        Console.WriteLine($"kind-1 drones (WhereField, indexed): {kind1}");

        var filling = tx.Query<Harvester>().Where<Cargo>(c => c.Amount < c.Capacity).Execute();       // broad scan
        Console.WriteLine($"filling drones (Where, scan): {filling.Count}");

        int near = tx.Query<Harvester>().WhereNearby<Footprint>(120f, 100f, 0f, 50f).Count();         // spatial
        Console.WriteLine($"drones within 50 of (120,100) (WhereNearby): {near}");
    }

    // A live view + delta: one view, refreshed across a change.
    EcsView<Harvester> hauling;
    using (var tx = dbe.CreateQuickTransaction())
    {
        hauling = tx.Query<Harvester>().Where<Cargo>(c => c.Amount > c.Capacity / 2).ToView();
        hauling.Refresh(tx);                                     // baseline
        Console.WriteLine($"hauling view initial members: {hauling.Count}");

        tx.OpenMut(probe).Write(Harvester.Cargo).Amount = 900;  // probe crosses half-full
        tx.Commit();
    }
    using (var tx = dbe.CreateQuickTransaction())
    {
        hauling.Refresh(tx);                                     // sees the change committed above
        int added = 0;
        foreach (var _ in hauling.GetDelta().Added) added++;
        Console.WriteLine($"hauling view after top-up: {hauling.Count} member(s), {added} added");
        hauling.ClearDelta();
    }
    hauling.Dispose();

    // ════════════════════════════════════════════════════════════════════════
    Banner("ch.5 — systems & the tick loop");
    // ════════════════════════════════════════════════════════════════════════

    // One long-lived input View for the entity systems.
    EcsView<Harvester> drones;
    using (var tx = dbe.CreateQuickTransaction())
    {
        drones = tx.Query<Harvester>().ToView();
    }

    float startX;
    int startCount;
    using (var tx = dbe.CreateQuickTransaction())
    {
        startX = tx.Open(mover).Read(Harvester.Position).P.X;
        startCount = tx.Query<Harvester>().Count();
    }
    Console.WriteLine($"before run: {startCount} drones, mover.x = {startX}");

    using (var runtime = TyphonRuntime.Create(dbe, schedule =>
    {
        schedule.PublicTrack
            .DeclareDag("Sim")
            .Phases(Phase.Input, Phase.Simulation)
            .Add(new SpawnSystem())
            .Add(new RoamSystem(drones))
            .Add(new FootprintSyncSystem(drones))
            .Add(new HarvestSystem(drones));
    }, new RuntimeOptions { BaseTickRate = 120, WorkerCount = 2 }))
    {
        runtime.Start();
        SpinWait.SpinUntil(() => runtime.CurrentTickNumber >= 60, TimeSpan.FromSeconds(5));
        runtime.Shutdown();
        Console.WriteLine($"ran {runtime.CurrentTickNumber} ticks");
    }

    using (var tx = dbe.CreateQuickTransaction())
    {
        float endX = tx.Open(mover).Read(Harvester.Position).P.X;
        int endCount = tx.Query<Harvester>().Count();
        int probeCargo = tx.Open(probe).Read(Harvester.Cargo).Amount;
        Console.WriteLine($"after run: {endCount} drones (deployed {endCount - startCount}), probe cargo {probeCargo}");
        Console.WriteLine($"mover moved: x {startX} -> {endX}  (Drift*dt integrated each tick)");
    }

    drones.Dispose();
}
// The engine is disposed here — that flushes the profiler trace to disk.

Console.WriteLine();
if (File.Exists(tracePath) && new FileInfo(tracePath).Length > 0)
{
    Console.WriteLine($"OK — ran end to end; profiler trace written: {tracePath} ({new FileInfo(tracePath).Length:N0} bytes)");
}
else
{
    Console.WriteLine($"WARN — ran, but no trace at {tracePath}. Check typhon.telemetry.json has a Profiler with Enabled=true and a Trace path.");
}

// ── helpers ──────────────────────────────────────────────────────────────
static void Banner(string title)
{
    Console.WriteLine();
    Console.WriteLine("== " + title + " ==");
}

static Footprint PointFootprint(float x, float y)
    => new Footprint { Box = new AABB2F { MinX = x, MaxX = x, MinY = y, MaxY = y } };

static void PrintCargo(string label, DatabaseEngine dbe, EntityId id)
{
    using var tx = dbe.CreateQuickTransaction();
    var c = tx.Open(id).Read(Harvester.Cargo);
    Console.WriteLine($"{label}: cargo {c.Amount}/{c.Capacity}");
}
