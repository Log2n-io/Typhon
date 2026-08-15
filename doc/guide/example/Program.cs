// Runnable companion to doc/guide, and the source template `typhon new` emits. Every snippet in the guide is mirrored
// here so it is known to compile and run against the current engine. Run with:
//   dotnet run --project doc/guide/example              (resumes the existing shard, or deploys a fresh one)
//   dotnet run --project doc/guide/example -- --reset   (wipes the database and deploys a fresh shard)
//
// It walks the guide's arc on a *real* planet shard: deploy thousands of characters -> read/query at scale -> transact ->
// spatial queries -> tick the runtime so the shard lives (characters move, regenerate HAM, wander, and trade credits) ->
// then CLOSE AND REOPEN the database to prove what survives. That last step is the point of a *database*: durable
// state comes back, and each storage mode comes back differently.
//
// The lesson is JUDGMENT, not a feature list: the hot per-tick state (Transform/Ham/Bounds/Faction) is SingleVersion —
// written lock-free in parallel, no MVCC — while only the economy (Wallet) is Versioned, touched at event cadence. The
// data model lives in the Typhon.Samples.Swg assembly; the systems live in Systems.cs. Profiling is config-driven:
// typhon.telemetry.json turns it on, the engine self-wires it inside TyphonRuntime.Create, and the .typhon-trace is
// flushed when the engine is disposed — zero profiling code here. Open the resulting world-shard.typhon in the
// Workbench (`typhon ui --open-db`) to browse the shard, and the trace (`typhon ui --open-latest`) to profile it.

using System;
using System.IO;
using System.Numerics;
using System.Threading;
using Typhon.Engine;
using Typhon.Samples.Swg.Shard;
using Typhon.Schema.Definition;
using SwgGuide;

// Tunables — a real shard so the Workbench has something to explore (a paginated entity browser, a File Map with real
// occupancy, a dense spatial index) and so the parallel tick systems have enough entities per worker to be worth
// fanning out. ~15 s end to end in Release. Shrink ShardSize for a faster loop; if you change it, re-tune Place()
// so the shard still fits the 1000x1000 spatial world.
const int ShardSize = 20_000;
const int TickTarget = 200;
const int StartingCredits = 100;

// Captures live WITH the database, in its own profilings/ directory — this file never names a path. typhon.telemetry.json
// leaves Typhon:Profiler:Trace absent, and an absent destination means "a file in {bundle}/profilings/" (#616), so the
// capture is always reachable through the database it describes instead of landing wherever the process happened to run.
// That co-location is what lets the Workbench correlate a capture with the data it was recorded against.
var profilingsDir = TraceLocation.ProfilingsDirectoryOf(Path.GetFullPath("world-shard.typhon"));
// Snapshot the newest capture's timestamp BEFORE the run so the completion check can tell a freshly-written one from a
// leftover — WITHOUT deleting anything. Past captures are kept on purpose (that's what `typhon ui --open-latest`
// browses); a stale file is told apart by its timestamp, not by wiping history.
var traceBefore = NewestCapture(profilingsDir)?.LastWriteTimeUtc ?? DateTime.MinValue;

// The database PERSISTS across runs — it's a database, not a scratch buffer. Pass `--reset` to wipe it and deploy a
// fresh shard; otherwise a re-run RESUMES the shard that survived the previous run (ch.1 detects which case it's in).
var reset = Array.IndexOf(args, "--reset") >= 0;
if (reset)
{
    new PagedMMFOptions { DatabaseName = "world-shard", DatabaseDirectory = "." }.EnsureFileDeleted();
}

EntityId probe = default, mover = default;

// ══ engine scope #1: build the shard, run it, decommission a batch — then dispose (flushing the trace) ══
{
    using var dbe = OpenEngine();

    // ════════════════════════════════════════════════════════════════════════
    Banner($"ch.1 — deploy a shard of {ShardSize:N0}");
    // ════════════════════════════════════════════════════════════════════════

    int existing;
    using (var tx = dbe.CreateQuickTransaction())
    {
        existing = tx.Query<Character>().Count();
    }

    if (existing == 0)
    {
        // Empty database (first run, or after --reset): deploy a fresh shard.
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < ShardSize; i++)
            {
                var (x, y) = Place(i);
                var e = tx.Spawn<Character>(
                    Character.Transform.Set(new Transform { Pos = new Point2F { X = x, Y = y }, Vel = new Point2F { X = 3f + (i % 3), Y = 2f } }),
                    Character.Bounds.Set(PointBounds(x, y)),
                    Character.Ham.Set(new Ham
                    {
                        Health = 40 + (i % 50), MaxHealth = 100,
                        Action = 40 + (i % 40), MaxAction = 100,
                        Mind = 40 + (i % 30), MaxMind = 100,
                    }),
                    Character.Faction.Set(new Faction { Value = i % 3 }),   // Neutral / Rebel / Imperial
                    Character.Wallet.Set(new Wallet { Credits = StartingCredits }),
                    Character.Intent.Set(new Intent()));
                if (i == 0)
                {
                    mover = e;   // we watch character 0 move in ch.5 (Neutral → survives the Imperial decommission)
                }
            }
            // A probe we track throughout — the ONLY character aligned with the Hutt cartel (the shard deploys
            // Neutral/Rebel/Imperial only), so it is uniquely recoverable after a restart (see FindProbe). Being Hutt
            // also means it survives the Imperial decommission in ch.5.5.
            probe = tx.Spawn<Character>(
                Character.Transform.Set(new Transform { Pos = new Point2F { X = 10f, Y = 20f }, Vel = new Point2F { X = 0f, Y = 0f } }),
                Character.Bounds.Set(PointBounds(10f, 20f)),
                Character.Ham.Set(new Ham { Health = 100, MaxHealth = 100, Action = 100, MaxAction = 100, Mind = 100, MaxMind = 100 }),
                Character.Faction.Set(new Faction { Value = Factions.Hutt }),
                Character.Wallet.Set(new Wallet { Credits = StartingCredits }),
                Character.Intent.Set(new Intent()));
            tx.Commit();
        }
        using (var tx = dbe.CreateQuickTransaction())
        {
            Console.WriteLine($"shard deployed: {tx.Query<Character>().Count():N0} characters");
        }
    }
    else
    {
        // Resume: the shard survived a prior run (durable across process restarts). Re-find our probe + a mover to
        // watch, then continue the walkthrough on the existing data. Wallet/Transform/Ham are durable so state carries
        // over; Intent is Transient so wander targets start empty — exactly the contrast ch.6 spells out.
        using (var tx = dbe.CreateQuickTransaction())
        {
            probe = FindProbe(tx);
            mover = FindMover(tx, probe);
        }
        Console.WriteLine($"resumed existing shard: {existing:N0} characters survived a prior run — pass --reset to redeploy fresh.");
    }

    // The spatial index is maintained by the tick fence. Run it once here so ch.4's WhereNearby can filter: for a fresh
    // deploy it enters the new characters into the grid; on a resume it rebuilds the grid over the reopened shard.
    dbe.WriteTickFence(1);

    // ════════════════════════════════════════════════════════════════════════
    Banner("ch.2 — generated accessors (ReadAll)");
    // ════════════════════════════════════════════════════════════════════════

    using (var tx = dbe.CreateQuickTransaction())
    {
        var c = Character.ReadAll(tx, probe);
        Console.WriteLine($"probe: faction={c.Faction.Value} credits={c.Wallet.Credits} "
            + $"HAM={c.Ham.Health}/{c.Ham.Action}/{c.Ham.Mind} pos=({c.Transform.Pos.X},{c.Transform.Pos.Y})");
    }

    // ════════════════════════════════════════════════════════════════════════
    Banner("ch.3 — transactions: write, rollback, snapshot");
    // ════════════════════════════════════════════════════════════════════════

    using (var uow = dbe.CreateUnitOfWork(DurabilityMode.GroupCommit))
    using (var tx = uow.CreateTransaction())
    {
        tx.OpenMut(probe).Write(Character.Wallet).Credits += 40;   // Versioned write
        tx.Commit();
    }
    PrintWallet("after committed +40 credits", dbe, probe);

    using (var tx = dbe.CreateQuickTransaction())
    {
        tx.OpenMut(probe).Write(Character.Wallet).Credits += 5000;
        tx.Rollback();
    }
    PrintWallet("after rolled-back +5000 credits", dbe, probe);

    using (var reader = dbe.CreateReadOnlyTransaction())
    {
        long before = reader.Open(probe).Read(Character.Wallet).Credits;
        using (var w = dbe.CreateQuickTransaction())
        {
            w.OpenMut(probe).Write(Character.Wallet).Credits += 10;
            w.Commit();
        }
        long after = reader.Open(probe).Read(Character.Wallet).Credits;
        Console.WriteLine($"reader snapshot held: {before} == {after} -> {before == after}");
    }

    // ════════════════════════════════════════════════════════════════════════
    Banner("ch.4 — queries at scale: indexed, scan, spatial, aggregate");
    // ════════════════════════════════════════════════════════════════════════

    using (var tx = dbe.CreateQuickTransaction())
    {
        // index scan (on a SingleVersion component!)
        int imperials = tx.Query<Character>().WhereField<Faction>(x => x.Value == Factions.Imperial).Count();
        Console.WriteLine($"Imperial characters (WhereField, indexed on a SingleVersion component): {imperials:N0}");

        var wounded = tx.Query<Character>().Where<Ham>(h => h.Health < h.MaxHealth).Execute();          // broad scan
        Console.WriteLine($"wounded characters (Where, scan): {wounded.Count:N0}");

        int near = tx.Query<Character>().WhereNearby<Bounds>(500, 500, 0, 100).Count();                 // spatial AoI
        Console.WriteLine($"characters within 100 of the shard centre (WhereNearby, spatial): {near:N0}");

        long totalCredits = SumCredits(tx);                                                           // economy aggregate
        Console.WriteLine($"total credits in circulation: {totalCredits:N0}");
    }

    // ════════════════════════════════════════════════════════════════════════
    Banner($"ch.5 — the shard lives ({TickTarget} ticks)");
    // ════════════════════════════════════════════════════════════════════════

    EcsView<Character> characters;
    using (var tx = dbe.CreateQuickTransaction())
    {
        characters = tx.Query<Character>().ToView();
    }

    float startX;
    using (var tx = dbe.CreateQuickTransaction())
    {
        startX = tx.Open(mover).Read(Character.Transform).Pos.X;
    }

    using (var runtime = TyphonRuntime.Create(dbe, schedule =>
    {
        schedule.PublicTrack
            .DeclareDag("Sim")
            .Phases(Phase.Input, Phase.Simulation)
            .Add(new SpawnSystem())
            .Add(new MoveSystem(characters))
            .Add(new BoundsSyncSystem(characters))
            .Add(new RegenSystem(characters))
            .Add(new WanderSystem(characters))
            .Add(new TradeSystem());
    }, new RuntimeOptions { BaseTickRate = 60 }))   // WorkerCount defaults to -1 → max(1, CPUs - 4): use the machine
    {
        runtime.Start();
        SpinWait.SpinUntil(() => runtime.CurrentTickNumber >= TickTarget, TimeSpan.FromSeconds(30));
        runtime.Shutdown();
        Console.WriteLine($"ran {runtime.CurrentTickNumber} ticks");
        ReportThroughput(runtime);
    }
    characters.Dispose();

    // The shard has been living: characters moved (SingleVersion, lock-free) and credits sloshed between wallets through
    // atomic Versioned transfers.
    using (var tx = dbe.CreateQuickTransaction())
    {
        var moverPos = tx.Open(mover).Read(Character.Transform).Pos;
        Console.WriteLine($"mover moved: ({startX:F1}) -> ({moverPos.X:F1}, {moverPos.Y:F1})  (velocity integrated each tick)");

        // Trading has spread the credits. Wallet.Credits isn't indexed (it churns), so wealth queries are plain scans.
        Console.WriteLine("wealth distribution (Where scan on the un-indexed Wallet.Credits):");
        Console.WriteLine($"  richer than start (> {StartingCredits}): {tx.Query<Character>().Where<Wallet>(w => w.Credits > StartingCredits).Execute().Count:N0} characters");
        Console.WriteLine($"  poorer than start (< {StartingCredits}): {tx.Query<Character>().Where<Wallet>(w => w.Credits < StartingCredits).Execute().Count:N0} characters");

        Console.WriteLine($"probe wallet now: {tx.Open(probe).Read(Character.Wallet).Credits} credits");
    }

    // ════════════════════════════════════════════════════════════════════════
    Banner("ch.5.5 — lifecycle: decommission (Destroy)");
    // ════════════════════════════════════════════════════════════════════════

    using (var tx = dbe.CreateQuickTransaction())
    {
        int before = tx.Query<Character>().Count();
        int destroyed = 0;
        foreach (var id in tx.Query<Character>().WhereField<Faction>(x => x.Value == Factions.Imperial).Execute())
        {
            tx.Destroy(id);
            destroyed++;
        }
        tx.Commit();
        Console.WriteLine($"the Imperial garrison withdraws — destroyed {destroyed:N0}: shard {before:N0} -> {before - destroyed:N0}");
    }
}
// engine #1 disposed here — the profiler trace is finalized to disk.

// ══ engine scope #2: reopen the SAME database — prove what survived, and how each storage mode came back ══
{
    Banner("ch.6 — durability: close & reopen");

    using var dbe = OpenEngine();   // no runtime here → no profiling → the ch.5 trace is untouched
    using var tx = dbe.CreateQuickTransaction();

    Console.WriteLine($"REOPEN — {tx.Query<Character>().Count():N0} characters survived (the Imperial withdrawal persisted)");

    var e = tx.Open(probe);
    var wallet = e.Read(Character.Wallet);
    var transform = e.Read(Character.Transform);
    var ham = e.Read(Character.Ham);
    var intent = e.Read(Character.Intent);
    Console.WriteLine($"probe came back:");
    Console.WriteLine($"   Wallet    (Versioned)     durable → {wallet.Credits} credits");
    Console.WriteLine($"   Transform (SingleVersion) durable → ({transform.Pos.X:F1}, {transform.Pos.Y:F1})");
    Console.WriteLine($"   Ham       (SingleVersion) durable → {ham.Health}/{ham.Action}/{ham.Mind} (H/A/M)");
    Console.WriteLine($"   Intent    (Transient)     RESET   → ({intent.Target.X}, {intent.Target.Y})   (heap-only; dropped on reopen by design)");
}

Console.WriteLine();
var capture = NewestCapture(profilingsDir);
if (capture != null && capture.Length > 0 && capture.LastWriteTimeUtc > traceBefore)
{
    Console.WriteLine($"OK — ran end to end; profiler trace written: {capture.FullName} ({capture.Length:N0} bytes)");
}
else
{
    Console.WriteLine($"WARN — no fresh trace written this run in {profilingsDir}. Enable profiling in typhon.telemetry.json.");
}

// ── helpers ──────────────────────────────────────────────────────────────

// The newest capture in a database's profilings/ directory, or null if there is none. Captures are named by the engine
// (a UTC timestamp, see TraceLocation.NewCapturePath), so they are found by scanning rather than by a path this file
// knows in advance — which is the point: the destination belongs to the database, not to the app.
static FileInfo NewestCapture(string profilingsDirectory)
{
    if (!Directory.Exists(profilingsDirectory))
    {
        return null;
    }

    FileInfo newest = null;
    foreach (var file in new DirectoryInfo(profilingsDirectory).GetFiles("*" + TraceLocation.TraceExtension))
    {
        if (newest == null || file.LastWriteTimeUtc > newest.LastWriteTimeUtc)
        {
            newest = file;
        }
    }

    return newest;
}

static DatabaseEngine OpenEngine()
{
    var dbe = DatabaseEngine.Open("world-shard.typhon", o => o
        .Register<Transform>()
        .Register<Bounds>()
        .Register<Ham>()
        .Register<Faction>()
        .Register<Wallet>()
        .Register<Intent>()
        .ConfigureSpatialGrid(new SpatialGridConfig(Vector2.Zero, new Vector2(1000f, 1000f), cellSize: 50f)));

    // Every write to the spatial component goes through ClusterRef.WriteSpatial (BoundsSyncSystem is the only writer; spawns go through the spawn path).
    // Declaring that lets the tick fence trust WriteSpatial's inline cell-crossing detector and skip its fall-back scan over every dirty slot — the scan
    // exists only for archetypes whose spatial field can also be written via OpenMut + Write. TYPHON009 is what makes this assertion checkable: it flags
    // any mutable span/ref access to a [SpatialIndex] component, and this project builds with zero.
    dbe.SetSpatialBarrierOnly<Character>();
    return dbe;
}

// Deterministic shard placement: a ~45-wide grid spread across the 1000x1000 world (varied positions → spatial density).
static (float x, float y) Place(int i)
{
    // A square-ish grid sized for ShardSize (cols ≈ √ShardSize) at a spacing that keeps the whole shard inside the
    // 1000x1000 world the spatial grid is configured for: 141 cols x 7 units = 987 wide, 142 rows x 7 = 994 tall.
    // Bump these together with ShardSize — entities placed outside the world bounds never enter the spatial index.
    const int cols = 141;
    const float spacing = 7f;
    return (5f + (i % cols) * spacing, 5f + (i / cols) * spacing);
}

// Re-find the tracked probe after a restart: it's the unique character aligned with the Hutt cartel — the shard's own
// characters are Neutral/Rebel/Imperial, so none collide — which keeps it identifiable across runs. Returns default if
// no such character exists.
static EntityId FindProbe(Transaction tx)
{
    foreach (var id in tx.Query<Character>().WhereField<Faction>(x => x.Value == Factions.Hutt).Execute())
    {
        return id;
    }
    return default;
}

// Pick any surviving character other than the probe to watch over the tick loop.
static EntityId FindMover(Transaction tx, EntityId probe)
{
    foreach (var id in tx.Query<Character>().Execute())
    {
        if (!id.Equals(probe))
        {
            return id;
        }
    }
    return default;
}

// The shard's total credits. NOTE: this walks the characters per-entity for clarity; the high-throughput shape is a SoA
// column sweep over the cluster (GetClusterEnumerator + GetReadOnlySpan) — the constant-cost aggregate Typhon is built
// for. Kept simple here so the seed stays readable; the guide text points at the columnar version.
static long SumCredits(Transaction tx)
{
    long sum = 0;
    foreach (var id in tx.Query<Character>().Execute())
    {
        sum += tx.Open(id).Read(Character.Wallet).Credits;
    }
    return sum;
}

// What the shard actually costs per tick. This reads TickTelemetry.ActualDurationMs — the tick's real execution time — NOT wall clock: BaseTickRate caps
// the loop at 60 Hz, so the runtime sleeps out whatever budget the systems don't use and wall-clock reports 60 Hz no matter how fast they are. Headroom is
// the number that matters: it says how much simulation you could still add before the shard stops keeping up.
static void ReportThroughput(TyphonRuntime runtime)
{
    var ring = runtime.Telemetry;
    long oldest = ring.OldestAvailableTick;
    long newest = ring.NewestTick;
    if (newest < oldest)
    {
        return;
    }

    int count = (int)(newest - oldest + 1);
    var durations = new float[count];
    float target = 0f;
    int entities = 0;
    for (int i = 0; i < count; i++)
    {
        ref readonly var t = ref ring.GetTick(oldest + i);
        durations[i] = t.ActualDurationMs;
        target = t.TargetDurationMs;
        entities = Math.Max(entities, t.TotalEntitiesProcessed);
    }

    Array.Sort(durations);
    float p50 = durations[count / 2];
    float p99 = durations[Math.Min(count - 1, (int)(count * 0.99f))];
    float worst = durations[count - 1];

    Console.WriteLine($"tick cost over {count} ticks (execution time, not wall clock — the 60 Hz limiter sleeps out the remainder):");
    Console.WriteLine($"   p50 {p50:F2} ms   p99 {p99:F2} ms   worst {worst:F2} ms   budget {target:F2} ms");
    if (p50 > 0f)
    {
        Console.WriteLine($"   {entities / p50 * 1000f / 1_000_000f:F1}M entity-updates/sec   {target / p50:F1}x headroom at {1000f / target:F0} Hz");
    }
}

static void Banner(string title)
{
    Console.WriteLine();
    Console.WriteLine("== " + title + " ==");
}

static Bounds PointBounds(float x, float y)
    => new Bounds { Box = new AABB2F { MinX = x, MaxX = x, MinY = y, MaxY = y } };

static void PrintWallet(string label, DatabaseEngine dbe, EntityId id)
{
    using var tx = dbe.CreateQuickTransaction();
    var w = tx.Open(id).Read(Character.Wallet);
    Console.WriteLine($"{label}: wallet {w.Credits} credits");
}
