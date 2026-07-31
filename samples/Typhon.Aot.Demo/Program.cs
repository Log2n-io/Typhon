// Typhon.Aot.Demo — the #409 Native AOT proof and JIT-vs-AOT measurement harness.
//
// ONE program, published two ways, running the SAME workload:
//     dotnet run     -c Release --project samples/Typhon.Aot.Demo -- --json out/jit.json
//     dotnet publish -c Release -r win-x64 samples/Typhon.Aot.Demo   →  Typhon.Aot.Demo.exe --json out/aot.json
//
// Every phase is timed with the same Stopwatch code in both builds, and every phase ASSERTS its result, so "it ran"
// and "it produced the right answers" are the same check. A native binary that silently skipped the spatial index or
// lost the reopened wallet would fail here rather than print a cheerful banner.
//
// The workload is the guide's arc, made deterministic: deploy a shard, transact (commit / rollback / snapshot),
// query it four ways (indexed, scan, spatial, read-your-own-writes over pending spawns), tick a six-system DAG,
// destroy a faction, then close and REOPEN to verify each storage mode came back as designed.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using SwgGuide;
using Typhon.Engine;
using Typhon.Samples.Swg.Shard;
using Typhon.Schema.Definition;

const int ShardSize = 20_000;
const int StartingCredits = 100;

var processStart = Stopwatch.GetTimestamp();
var phases = new List<(string Name, double Ms)>();
var failures = new List<string>();
string jsonPath = ArgValue(args, "--json");
// Tick count is a parameter because the JIT-vs-AOT comparison has two distinct regimes: a short run is dominated by
// tier-0 warm-up (where AOT trivially wins), while a long run lets tiered compilation + dynamic PGO reach steady state
// (where AOT has no profile data and could plausibly lose). Reporting only one of the two would be misleading. #409
int TickTarget = int.TryParse(ArgValue(args, "--ticks"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var tt) ? tt : 300;
var dbDirectory = ArgValue(args, "--dir") ?? Path.Combine(Path.GetTempPath(), "typhon-aot-demo");
Directory.CreateDirectory(dbDirectory);

Console.WriteLine("Typhon Native AOT demo");
Console.WriteLine($"  runtime          : {(RuntimeFeature.IsDynamicCodeSupported ? "JIT (dynamic code supported)" : "NATIVE AOT (no dynamic code)")}");
Console.WriteLine($"  IsDynamicCodeCompiled: {RuntimeFeature.IsDynamicCodeCompiled}");
Console.WriteLine($"  shard            : {ShardSize:N0} entities, {TickTarget} ticks");
Console.WriteLine($"  database dir     : {dbDirectory}");
Console.WriteLine();

// A fresh database every run — the two builds must measure identical work, and a resumed shard would not be identical.
new PagedMMFOptions { DatabaseName = "aot-demo", DatabaseDirectory = dbDirectory }.EnsureFileDeleted();

EntityId probe = default, mover = default;
long creditsAfterCommit = 0, creditsAfterRollback = 0;
int imperialsBefore = 0, woundedCount = 0, nearCount = 0, pendingMatches = 0;
int survivors = 0, destroyedCount = 0;
double tickP50 = 0, tickP99 = 0;
long ticksRun = 0;
int entitiesPerTick = 0;

// ══════════════════════════════════════════════════════════════════════════════
// Phase 1 — open. Under AOT this is the moment of truth for the generated [ModuleInitializer] registration barrier:
// every [Archetype] and [Component] in the referenced schema assembly must have registered itself with no reflection.
// ══════════════════════════════════════════════════════════════════════════════
var swOpen = Stopwatch.StartNew();
var dbe = OpenEngine(dbDirectory);
swOpen.Stop();
phases.Add(("open_engine", swOpen.Elapsed.TotalMilliseconds));
// Querying the archetype at all proves the generated [ModuleInitializer] barrier ran and finalized it: an unregistered
// archetype throws here rather than returning an empty result. A fresh database must report exactly zero.
using (var tx = dbe.CreateQuickTransaction())
{
    Check(tx.Query<Character>().Count() == 0, "archetype resolved through the generated registration barrier (fresh database, 0 entities)");
}

// ══════════════════════════════════════════════════════════════════════════════
// Phase 2 — deploy the shard. Exercises the spawn path, cluster storage, WAL, and the mixed storage modes
// (Versioned Wallet / SingleVersion Transform+Bounds+Ham+Faction / Transient Intent).
// ══════════════════════════════════════════════════════════════════════════════
var swSpawn = Stopwatch.StartNew();
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
            Character.Faction.Set(new Faction { Value = i % 3 }),
            Character.Wallet.Set(new Wallet { Credits = StartingCredits }),
            Character.Intent.Set(new Intent()));
        if (i == 0)
        {
            mover = e;
        }
    }
    probe = tx.Spawn<Character>(
        Character.Transform.Set(new Transform { Pos = new Point2F { X = 10f, Y = 20f }, Vel = new Point2F { X = 0f, Y = 0f } }),
        Character.Bounds.Set(PointBounds(10f, 20f)),
        Character.Ham.Set(new Ham { Health = 100, MaxHealth = 100, Action = 100, MaxAction = 100, Mind = 100, MaxMind = 100 }),
        Character.Faction.Set(new Faction { Value = Factions.Hutt }),
        Character.Wallet.Set(new Wallet { Credits = StartingCredits }),
        Character.Intent.Set(new Intent()));
    tx.Commit();
}
swSpawn.Stop();
phases.Add(("spawn_commit", swSpawn.Elapsed.TotalMilliseconds));

using (var tx = dbe.CreateQuickTransaction())
{
    Check(tx.Query<Character>().Count() == ShardSize + 1, $"shard holds {ShardSize + 1:N0} characters after commit");
}

dbe.WriteTickFence(1);   // enters the new characters into the spatial grid

// ══════════════════════════════════════════════════════════════════════════════
// Phase 3 — transactions: durable commit, rollback, snapshot isolation.
// ══════════════════════════════════════════════════════════════════════════════
var swTx = Stopwatch.StartNew();
using (var uow = dbe.CreateUnitOfWork(DurabilityMode.GroupCommit))
using (var tx = uow.CreateTransaction())
{
    tx.OpenMut(probe).Write(Character.Wallet).Credits += 40;
    tx.Commit();
}
creditsAfterCommit = ReadCredits(dbe, probe);

using (var tx = dbe.CreateQuickTransaction())
{
    tx.OpenMut(probe).Write(Character.Wallet).Credits += 5000;
    tx.Rollback();
}
creditsAfterRollback = ReadCredits(dbe, probe);

bool snapshotHeld;
using (var reader = dbe.CreateReadOnlyTransaction())
{
    long before = reader.Open(probe).Read(Character.Wallet).Credits;
    using (var w = dbe.CreateQuickTransaction())
    {
        w.OpenMut(probe).Write(Character.Wallet).Credits += 10;
        w.Commit();
    }
    snapshotHeld = reader.Open(probe).Read(Character.Wallet).Credits == before;
}
swTx.Stop();
phases.Add(("transactions", swTx.Elapsed.TotalMilliseconds));

Check(creditsAfterCommit == StartingCredits + 40, $"committed write is visible ({creditsAfterCommit} credits)");
Check(creditsAfterRollback == StartingCredits + 40, $"rolled-back write left no trace ({creditsAfterRollback} credits)");
Check(snapshotHeld, "read-only transaction held its MVCC snapshot across a concurrent commit");

// ══════════════════════════════════════════════════════════════════════════════
// Phase 4 — queries. WhereField is the interesting one under AOT: it takes an Expression<Func<T,bool>>, which is
// exactly the shape that cannot be Compile()d without dynamic code. The engine parses it into its own predicate AST
// for the indexed scan; the pending-spawn read-your-own-writes probe below forces the OTHER half of that path.
// ══════════════════════════════════════════════════════════════════════════════
var swQuery = Stopwatch.StartNew();
using (var tx = dbe.CreateQuickTransaction())
{
    imperialsBefore = tx.Query<Character>().WhereField<Faction>(x => x.Value == Factions.Imperial).Count();
    woundedCount = tx.Query<Character>().Where<Ham>(h => h.Health < h.MaxHealth).Execute().Count;
    nearCount = tx.Query<Character>().WhereNearby<Bounds>(500, 500, 0, 100).Count();
}
swQuery.Stop();
phases.Add(("queries", swQuery.Elapsed.TotalMilliseconds));

Check(imperialsBefore > 0, $"indexed query (WhereField on a SingleVersion component) returned {imperialsBefore:N0} Imperials");
Check(woundedCount > 0, $"scan query returned {woundedCount:N0} wounded characters");
Check(nearCount > 0, $"spatial query (WhereNearby) returned {nearCount:N0} characters near the shard centre");

// Read-your-own-writes over PENDING spawns: entities spawned in this uncommitted transaction have no index entries,
// so WhereField must evaluate the predicate against them directly. On CoreCLR that path historically ran a compiled
// expression delegate; a Native AOT build has to reach the same answer without emitting code.
var swPending = Stopwatch.StartNew();
using (var tx = dbe.CreateQuickTransaction())
{
    for (int i = 0; i < 32; i++)
    {
        var (x, y) = (600f + i, 600f + i);
        tx.Spawn<Character>(
            Character.Transform.Set(new Transform { Pos = new Point2F { X = x, Y = y } }),
            Character.Bounds.Set(PointBounds(x, y)),
            Character.Ham.Set(new Ham { Health = 10, MaxHealth = 100, Action = 10, MaxAction = 100, Mind = 10, MaxMind = 100 }),
            Character.Faction.Set(new Faction { Value = Factions.Hutt }),
            Character.Wallet.Set(new Wallet { Credits = 7 }),
            Character.Intent.Set(new Intent()));
    }
    // The probe is Hutt too, so the expected count is the 32 pending spawns + the 1 committed probe.
    pendingMatches = tx.Query<Character>().WhereField<Faction>(x => x.Value == Factions.Hutt).Count();
    tx.Rollback();
}
swPending.Stop();
phases.Add(("pending_spawn_readback", swPending.Elapsed.TotalMilliseconds));
Check(pendingMatches == 33, $"read-your-own-writes over pending spawns matched {pendingMatches} (expected 33)");

// ══════════════════════════════════════════════════════════════════════════════
// Phase 5 — the tick loop: a six-system DAG across two phases, parallel dispatch, per-tick transactions.
// This is the throughput number that actually distinguishes JIT from AOT, because it is the only phase dominated by
// steady-state optimized code rather than one-shot setup.
// ══════════════════════════════════════════════════════════════════════════════
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

var swTick = Stopwatch.StartNew();
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
}, new RuntimeOptions { BaseTickRate = 60 }))
{
    runtime.Start();
    SpinWait.SpinUntil(() => runtime.CurrentTickNumber >= TickTarget, TimeSpan.FromSeconds(TickTarget / 60.0 + 30));
    runtime.Shutdown();
    ticksRun = runtime.CurrentTickNumber;
    (tickP50, tickP99, entitiesPerTick) = TickCost(runtime);
}
swTick.Stop();
phases.Add(("tick_loop", swTick.Elapsed.TotalMilliseconds));
characters.Dispose();

Check(ticksRun >= TickTarget, $"tick loop reached {ticksRun} ticks (target {TickTarget})");
Check(tickP50 > 0, $"tick telemetry recorded execution time (p50 {tickP50:F3} ms)");

using (var tx = dbe.CreateQuickTransaction())
{
    var moverPos = tx.Open(mover).Read(Character.Transform).Pos;
    Check(Math.Abs(moverPos.X - startX) > 0.001f, $"the simulation actually moved entities ({startX:F1} → {moverPos.X:F1})");
}

// ══════════════════════════════════════════════════════════════════════════════
// Phase 6 — destroy a faction, then close and reopen. The reopen is the durability claim: the process exits the
// engine entirely and comes back to the same file.
// ══════════════════════════════════════════════════════════════════════════════
var swDestroy = Stopwatch.StartNew();
using (var tx = dbe.CreateQuickTransaction())
{
    foreach (var id in tx.Query<Character>().WhereField<Faction>(x => x.Value == Factions.Imperial).Execute())
    {
        tx.Destroy(id);
        destroyedCount++;
    }
    tx.Commit();
}
swDestroy.Stop();
phases.Add(("destroy", swDestroy.Elapsed.TotalMilliseconds));
Check(destroyedCount > 0, $"destroyed {destroyedCount:N0} Imperial characters");

long walletBeforeClose = ReadCredits(dbe, probe);
float transformXBeforeClose;
using (var tx = dbe.CreateQuickTransaction())
{
    transformXBeforeClose = tx.Open(probe).Read(Character.Transform).Pos.X;
}

dbe.Dispose();

var swReopen = Stopwatch.StartNew();
using (var reopened = OpenEngine(dbDirectory))
{
    using var tx = reopened.CreateQuickTransaction();
    survivors = tx.Query<Character>().Count();

    var e = tx.Open(probe);
    var wallet = e.Read(Character.Wallet);
    var transform = e.Read(Character.Transform);
    var intent = e.Read(Character.Intent);

    swReopen.Stop();
    phases.Add(("reopen", swReopen.Elapsed.TotalMilliseconds));

    Check(survivors > 0 && survivors < ShardSize + 1, $"{survivors:N0} characters survived the reopen (destruction persisted)");
    Check(wallet.Credits == walletBeforeClose, $"Versioned component came back durable ({wallet.Credits} credits)");
    Check(Math.Abs(transform.Pos.X - transformXBeforeClose) < 0.001f, $"SingleVersion component came back durable (x={transform.Pos.X:F1})");
    Check(intent.Target.X == 0 && intent.Target.Y == 0, "Transient component was reset on reopen, as designed");
}

// ══════════════════════════════════════════════════════════════════════════════
// Report
// ══════════════════════════════════════════════════════════════════════════════
var totalMs = Stopwatch.GetElapsedTime(processStart).TotalMilliseconds;
var peakWorkingSetMb = Process.GetCurrentProcess().PeakWorkingSet64 / (1024.0 * 1024.0);

Console.WriteLine();
Console.WriteLine("phase timings");
foreach (var (name, ms) in phases)
{
    Console.WriteLine($"  {name,-24} {ms,10:F2} ms");
}
Console.WriteLine($"  {"TOTAL (in-process)",-24} {totalMs,10:F2} ms");
Console.WriteLine();
Console.WriteLine($"tick cost: p50 {tickP50:F3} ms   p99 {tickP99:F3} ms   entities/tick {entitiesPerTick:N0}");
if (tickP50 > 0)
{
    Console.WriteLine($"throughput: {entitiesPerTick / tickP50 * 1000.0 / 1e6:F2}M entity-updates/sec");
}
Console.WriteLine($"peak working set: {peakWorkingSetMb:F1} MB");
Console.WriteLine();

if (failures.Count == 0)
{
    Console.WriteLine($"RESULT: PASS — all {phases.Count} phases completed and every assertion held.");
}
else
{
    Console.WriteLine($"RESULT: FAIL — {failures.Count} assertion(s) failed:");
    foreach (var f in failures)
    {
        Console.WriteLine("  - " + f);
    }
}

if (jsonPath != null)
{
    WriteJson(jsonPath);
}

return failures.Count == 0 ? 0 : 1;

// ── helpers ──────────────────────────────────────────────────────────────────

void Check(bool ok, string what)
{
    Console.WriteLine((ok ? "  [ok]   " : "  [FAIL] ") + what);
    if (!ok)
    {
        failures.Add(what);
    }
}

void WriteJson(string path)
{
    var dir = Path.GetDirectoryName(Path.GetFullPath(path));
    if (!string.IsNullOrEmpty(dir))
    {
        Directory.CreateDirectory(dir);
    }
    // Hand-built rather than JsonSerializer: this program is published with PublishAot, and reflection-based
    // serialization is precisely what that forbids. Three primitive types, so a StringBuilder is honest and simple.
    var sb = new StringBuilder(1024);
    var inv = CultureInfo.InvariantCulture;
    sb.Append("{\n");
    sb.Append("  \"mode\": \"").Append(RuntimeFeature.IsDynamicCodeCompiled ? "jit" : "aot").Append("\",\n");
    sb.Append("  \"dynamicCodeSupported\": ").Append(RuntimeFeature.IsDynamicCodeSupported ? "true" : "false").Append(",\n");
    sb.Append("  \"shardSize\": ").Append(ShardSize.ToString(inv)).Append(",\n");
    sb.Append("  \"tickTarget\": ").Append(TickTarget.ToString(inv)).Append(",\n");
    sb.Append("  \"ticksRun\": ").Append(ticksRun.ToString(inv)).Append(",\n");
    sb.Append("  \"tickP50Ms\": ").Append(tickP50.ToString("F4", inv)).Append(",\n");
    sb.Append("  \"tickP99Ms\": ").Append(tickP99.ToString("F4", inv)).Append(",\n");
    sb.Append("  \"entitiesPerTick\": ").Append(entitiesPerTick.ToString(inv)).Append(",\n");
    sb.Append("  \"peakWorkingSetMb\": ").Append(peakWorkingSetMb.ToString("F1", inv)).Append(",\n");
    sb.Append("  \"totalMs\": ").Append(totalMs.ToString("F2", inv)).Append(",\n");
    sb.Append("  \"failures\": ").Append(failures.Count.ToString(inv)).Append(",\n");
    sb.Append("  \"phases\": {\n");
    for (int i = 0; i < phases.Count; i++)
    {
        sb.Append("    \"").Append(phases[i].Name).Append("\": ").Append(phases[i].Ms.ToString("F3", inv));
        sb.Append(i == phases.Count - 1 ? "\n" : ",\n");
    }
    sb.Append("  }\n}\n");
    File.WriteAllText(path, sb.ToString());
    Console.WriteLine($"wrote {path}");
}

static string ArgValue(string[] args, string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static DatabaseEngine OpenEngine(string directory)
{
    var dbe = DatabaseEngine.Open(Path.Combine(directory, "aot-demo.typhon"), o => o
        .Register<Transform>()
        .Register<Bounds>()
        .Register<Ham>()
        .Register<Faction>()
        .Register<Wallet>()
        .Register<Intent>()
        .ConfigureSpatialGrid(new SpatialGridConfig(Vector2.Zero, new Vector2(1000f, 1000f), cellSize: 50f)));
    dbe.SetSpatialBarrierOnly<Character>();
    return dbe;
}

static long ReadCredits(DatabaseEngine dbe, EntityId id)
{
    using var tx = dbe.CreateQuickTransaction();
    return tx.Open(id).Read(Character.Wallet).Credits;
}

static (float x, float y) Place(int i)
{
    const int cols = 141;
    const float spacing = 7f;
    return (5f + (i % cols) * spacing, 5f + (i / cols) * spacing);
}

static Bounds PointBounds(float x, float y)
    => new Bounds { Box = new AABB2F { MinX = x, MaxX = x, MinY = y, MaxY = y } };

static (double p50, double p99, int entities) TickCost(TyphonRuntime runtime)
{
    var ring = runtime.Telemetry;
    long oldest = ring.OldestAvailableTick;
    long newest = ring.NewestTick;
    if (newest < oldest)
    {
        return (0, 0, 0);
    }

    int count = (int)(newest - oldest + 1);
    var durations = new float[count];
    int entities = 0;
    for (int i = 0; i < count; i++)
    {
        ref readonly var t = ref ring.GetTick(oldest + i);
        durations[i] = t.ActualDurationMs;
        entities = Math.Max(entities, t.TotalEntitiesProcessed);
    }

    Array.Sort(durations);
    return (durations[count / 2], durations[Math.Min(count - 1, (int)(count * 0.99f))], entities);
}
