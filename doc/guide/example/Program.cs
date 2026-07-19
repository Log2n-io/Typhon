// Runnable companion to doc/guide. Every snippet in the guide is mirrored here so it
// is known to compile and run against the current engine. Run with:
//   dotnet run --project doc/guide/example
//
// It walks the guide's arc (declare -> spawn -> read -> transact -> query -> view -> tick)
// printing checkable text to the console. The data model + systems live in Model.cs.

using System;
using System.Numerics;
using System.Threading;
using Typhon.Engine;
using Typhon.Schema.Definition;
using SkirmishGuide;

// ── Open the engine + register schema + spatial grid (ch.1 / ch.2) ────────
// Fresh DB each run: wipe any prior bundle before opening.
new PagedMMFOptions { DatabaseName = "skirmish-guide", DatabaseDirectory = "." }.EnsureFileDeleted();

// One call: names the database, registers the components + archetype, configures the spatial grid
// (required by the [SpatialIndex] on Bounds), and wires the archetypes — a ready-to-use engine.
using var dbe = DatabaseEngine.Open("skirmish-guide.typhon", o => o
    .Register<Position>()
    .Register<Bounds>()
    .Register<Health>()
    .Register<Velocity>()
    .Register<Team>()
    .ConfigureSpatialGrid(new SpatialGridConfig(Vector2.Zero, new Vector2(1000f, 1000f), cellSize: 50f)));

// ════════════════════════════════════════════════════════════════════════
Banner("ch.1 — spawn, read, query");
// ════════════════════════════════════════════════════════════════════════

EntityId soldier;
EntityId mover = default;   // a rank-and-file unit we watch move in ch.5
using (var tx = dbe.CreateQuickTransaction())
{
    // Spawn six units across three teams at distinct positions.
    for (int i = 0; i < 6; i++)
    {
        float x = 100f + i * 20f, y = 100f;
        var e = tx.Spawn<Unit>(
            Unit.Position.Set(new Position { P = new Point2F { X = x, Y = y } }),
            Unit.Bounds.Set(PointBounds(x, y)),
            Unit.Health.Set(new Health(100, 100)),
            Unit.Velocity.Set(new Velocity(5f, 0f)),
            Unit.Team.Set(new Team { Id = (i % 3) + 1 }));
        if (i == 0) mover = e;
    }
    soldier = tx.Spawn<Unit>(
        Unit.Position.Set(new Position { P = new Point2F { X = 10f, Y = 20f } }),
        Unit.Bounds.Set(PointBounds(10f, 20f)),
        Unit.Health.Set(new Health(100, 100)),
        Unit.Velocity.Set(new Velocity(0f, 0f)),
        Unit.Team.Set(new Team { Id = 1 }));
    tx.Commit();
}

// The spatial index is maintained by the tick fence. Outside the runtime, run it once after
// spawning so WhereNearby/WhereInAABB can filter (inside the runtime it runs every tick).
dbe.WriteTickFence(1);

using (var tx = dbe.CreateQuickTransaction())
{
    var e   = tx.Open(soldier);
    var pos = e.Read(Unit.Position);
    var hp  = e.Read(Unit.Health);
    Console.WriteLine($"soldier: HP {hp.Current}/{hp.Max} at ({pos.P.X}, {pos.P.Y})");

    int wounded = tx.Query<Unit>().Where<Health>(h => h.Current < h.Max).Count();
    Console.WriteLine($"wounded units: {wounded}");
    int total = tx.Query<Unit>().Count();
    Console.WriteLine($"total units: {total}");
}

// ════════════════════════════════════════════════════════════════════════
Banner("ch.2 — generated accessors (ReadAll)");
// ════════════════════════════════════════════════════════════════════════

using (var tx = dbe.CreateQuickTransaction())
{
    var u = Unit.ReadAll(tx, soldier);
    Console.WriteLine($"ReadAll: team={u.Team.Id} hp={u.Health.Current}/{u.Health.Max} pos=({u.Position.P.X},{u.Position.P.Y})");
}

// ════════════════════════════════════════════════════════════════════════
Banner("ch.3 — transactions: write, rollback, snapshot");
// ════════════════════════════════════════════════════════════════════════

// Explicit UoW + transaction (the form ch.3 opens up).
using (var uow = dbe.CreateUnitOfWork(DurabilityMode.GroupCommit))
using (var tx = uow.CreateTransaction())
{
    var e = tx.OpenMut(soldier);
    e.Write(Unit.Health).Current -= 25;   // Versioned write
    tx.Commit();
}
PrintHp("after committed -25", dbe, soldier);

// Rollback: a Versioned write that never lands.
using (var tx = dbe.CreateQuickTransaction())
{
    tx.OpenMut(soldier).Write(Unit.Health).Current -= 1000;
    tx.Rollback();
}
PrintHp("after rolled-back -1000", dbe, soldier);

// Snapshot isolation: a read-only transaction doesn't see later commits.
using (var reader = dbe.CreateReadOnlyTransaction())
{
    int before = reader.Open(soldier).Read(Unit.Health).Current;
    using (var w = dbe.CreateQuickTransaction())
    {
        w.OpenMut(soldier).Write(Unit.Health).Current -= 10;
        w.Commit();
    }
    int after = reader.Open(soldier).Read(Unit.Health).Current;
    Console.WriteLine($"reader snapshot held: {before} == {after} -> {before == after}");
}
PrintHp("outside the reader, the -10 is visible", dbe, soldier);

// ════════════════════════════════════════════════════════════════════════
Banner("ch.4 — queries, spatial, live views");
// ════════════════════════════════════════════════════════════════════════

using (var tx = dbe.CreateQuickTransaction())
{
    int team1 = tx.Query<Unit>().WhereField<Team>(t => t.Id == 1).Count();           // indexed
    Console.WriteLine($"team 1 units (WhereField, indexed): {team1}");

    var wounded = tx.Query<Unit>().Where<Health>(h => h.Current < h.Max).Execute();   // broad scan
    Console.WriteLine($"wounded units (Where, scan): {wounded.Count}");

    int near = tx.Query<Unit>().WhereNearby<Bounds>(120f, 100f, 0f, 50f).Count();     // spatial
    Console.WriteLine($"units within 50 of (120,100) (WhereNearby): {near}");
}

// A live view + delta: one view, refreshed across a change.
EcsView<Unit> lowHp;
using (var tx = dbe.CreateQuickTransaction())
{
    lowHp = tx.Query<Unit>().Where<Health>(h => h.Current < h.Max / 2).ToView();
    lowHp.Refresh(tx);                                       // baseline
    Console.WriteLine($"low-hp view initial members: {lowHp.Count}");

    tx.OpenMut(soldier).Write(Unit.Health).Current = 10;    // soldier drops below half
    tx.Commit();
}
using (var tx = dbe.CreateQuickTransaction())
{
    lowHp.Refresh(tx);                                       // sees the change committed above
    int added = 0;
    foreach (var _ in lowHp.GetDelta().Added) added++;
    Console.WriteLine($"low-hp view after damage: {lowHp.Count} member(s), {added} added");
    lowHp.ClearDelta();
}
lowHp.Dispose();

// ════════════════════════════════════════════════════════════════════════
Banner("ch.5 — systems & the tick loop");
// ════════════════════════════════════════════════════════════════════════

// One long-lived input View for the entity systems.
EcsView<Unit> units;
using (var tx = dbe.CreateQuickTransaction())
{
    units = tx.Query<Unit>().ToView();
}

float startX;
int startCount;
using (var tx = dbe.CreateQuickTransaction())
{
    startX = tx.Open(mover).Read(Unit.Position).P.X;
    startCount = tx.Query<Unit>().Count();
}
Console.WriteLine($"before run: {startCount} units, mover.x = {startX}");

using (var runtime = TyphonRuntime.Create(dbe, schedule =>
{
    schedule.PublicTrack
        .DeclareDag("Game")
        .Phases(Phase.Input, Phase.Simulation)
        .Add(new SpawnSystem())
        .Add(new MovementSystem(units))
        .Add(new BoundsSyncSystem(units))
        .Add(new CombatSystem(units));
}, new RuntimeOptions { BaseTickRate = 120, WorkerCount = 2 }))
{
    runtime.Start();
    SpinWait.SpinUntil(() => runtime.CurrentTickNumber >= 60, TimeSpan.FromSeconds(5));
    runtime.Shutdown();
    Console.WriteLine($"ran {runtime.CurrentTickNumber} ticks");
}

using (var tx = dbe.CreateQuickTransaction())
{
    float endX = tx.Open(mover).Read(Unit.Position).P.X;
    int endCount = tx.Query<Unit>().Count();
    int wounded = tx.Query<Unit>().Where<Health>(h => h.Current < h.Max).Count();
    Console.WriteLine($"after run: {endCount} units (spawned {endCount - startCount}), wounded {wounded}");
    Console.WriteLine($"mover moved: x {startX} -> {endX}  (Velocity*dt integrated each tick)");
}

units.Dispose();
Console.WriteLine();
Console.WriteLine("OK — guide example ran end to end.");

// ── helpers ──────────────────────────────────────────────────────────────
static void Banner(string title)
{
    Console.WriteLine();
    Console.WriteLine("== " + title + " ==");
}

static Bounds PointBounds(float x, float y) =>
    new Bounds { Box = new AABB2F { MinX = x, MaxX = x, MinY = y, MaxY = y } };

static void PrintHp(string label, DatabaseEngine dbe, EntityId id)
{
    using var tx = dbe.CreateQuickTransaction();
    var hp = tx.Open(id).Read(Unit.Health);
    Console.WriteLine($"{label}: HP {hp.Current}/{hp.Max}");
}
