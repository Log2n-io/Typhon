# SpaceBattle — Design & Implementation Review

**Date:** 2026-08-13
**Scope:** `demo/SpaceBattle/` — `SpaceBattle.Main` (5,791 LOC) + `SpaceBattle.Tests` (3,193 LOC)
**Provenance:** one squashed commit, `7558b6e`, 10,480 insertions, single external contributor
**Method:** documentation-first (`claude/overview/`, `claude/design/`, `doc/`, `rules/`), then code; five parallel analysis agents; all headline claims independently verified

Performance figures come from the demo's own `benchmark/reports/performance-report-20260812.md` (Ryzen 7 260, 16 logical, 12 workers, Release, 50k ships) **and** from an independent build-and-run on a 32-logical-processor machine, which reproduced them to within 0.1 %.

---

## 1. What the demo is

**A 50,000-agent free-for-all attrition simulation. Not a game, and not watchable.**

A single 1000³ cube, 50,000 identical ships, fixed 25 TPS / Δt = 0.04 s, hard cap 45,000 ticks
(`SimulationDefinition.cs:14-24`).

**No factions, no ship classes, no weapon types.** A repo-wide grep for `faction|team|alliance|ShipClass|shield|armor`
across `SpaceBattle.Main/` returns nothing. Every ship is identical: 1,000 HP, one weapon (200 damage, 250-unit range,
fires every 50 ticks), one target lock maximum.

### Ship state machine — `Snapshots.cs:24-32`

| Mode | Trigger | Duration | Speed |
|---|---|---|---|
| `Staging` | spawn | 250 ticks | 0 |
| `Wandering` | staging expiry | re-decides every 250 ticks | ≤ 37.5 (0.75×, random) |
| `Tracking` | 1-in-3 wander roll | 250 ticks | 50 |
| `Combat` | 1-in-3 wander roll | 250 ticks | — |
| `Disengaging` | you scored a kill | 75 ticks | 25 (0.5×) |
| `Escaping` | you took damage and lived | 125 ticks | 75 (1.5×), afterburner on |

Movement is billiard-ball reflection off the cube walls (`MovementRules.cs:57-93`) — no wrap, no steering physics.

Damage is simultaneous: hits are pushed into per-worker `EventQueue<DamageIntent>` (`SpaceBattleSimulation.cs:2168`)
and aggregated in a separate phase, so mutual kills resolve on the same tick.

Victory: 1 alive → `Winner`; 0 alive → `Draw`; 45,000 ticks → `TimedOut` (exit code 1).

**What a player would see: nothing.** There is no player, camera, or input.

### Rendering: none, and none planned

Zero. `SpaceBattle.Main.csproj` references only `Typhon.Engine`, `Typhon.Schema.Definition`, `Typhon.Profiler` and two
analyzers. No Godot, no web SDK, no serialization package.

The doc frames this as terminal, not a phase — `doc/demos/space-battle.md:11`:

> "Unlike AntHill, SpaceBattle deliberately has no renderer, web server, command prompt, or gameplay input."

and `:51` lists `Graphical client | Deliberately headless`. Contrast AntHill, which ships a full Godot 4.6 renderer
(13 scripts, HUD, god-game tools) and a documented phase 0–9 roadmap with art at phase 8.

Output is console text **entirely in Chinese**, plus a markdown report in benchmark mode.

### The observation surface

Three APIs, all in-process managed objects — nothing is serialized, so any consumer must share the process.

| API | What it gives | Fit for a renderer |
|---|---|---|
| `ISpaceBattleObservationSink` (`Observations.cs:5-8`) | async via `Channel<T>`, decoupled from the tick | **Aggregates only** — "3,412 ships in Combat", never *where* |
| `GetSnapshot()` → `InitialWorldSnapshot` (`Snapshots.cs`) | per-ship position, bounds, motion, health, mode, all locks | The renderable payload, but a request/capture protocol allocating 50,000 heap records per call |
| `SpaceBattleHost.ReadSnapshot(...)` (`SpaceBattleHost.cs:293-302`) | same, from a read-only DB open | Offline / post-mortem only |

**The data a renderer needs exists and is well-shaped; the streaming path that exists carries none of it.** Wiring a
client means adding a path, not consuming one.

---

## 2. How it uses Typhon — the design

**Data layer: genuinely idiomatic. Access layer: bypassed almost entirely.**

### What is real Typhon usage

- 15 components, declarative source-generated schema, correct `StorageMode.SingleVersion` on all hot components
  (`Components.cs`), enableable components for equipment state.
- 3 archetypes, `EntityLink<Ship>` typed FKs, `[Index(AllowMultiple = true)]` secondary indexes, `[SpatialIndex(20f)]`.
- A **proper 9-phase DAG** on `schedule.PublicTrack` with declared `Reads`/`Writes`/`ReadsFresh` sets and `.After()`
  chaining (`SpaceBattleSimulation.cs:359-387`).
- Per-worker `EventQueue<DamageIntent>`, tick-fence durability, WAL, checkpoint/resume, 24 recovery invariant checks.
- **100 % of authoritative state lives in components.** No shadow copy of ship state.

Tick order:

```
ShipViewRefresh → State → Steering → Movement → TargetLockCleanup
  → Targeting → Combat (∥) → DamageResolution → Resolution → Output
```

Transactions: **~21 per tick** — one per `CallbackSystem` (9) plus one per parallel Combat chunk (12) — all sharing one
Unit of Work with a single batched WAL flush at the fence. That is the engine's design, correctly used.

### 2.1 The systems

**10 systems, 9 phases.** `Resolution` is both a phase name and a system name, and `DamageResolution` is a *different*
system living inside the `Resolution` phase (`:2186`, `:2380-2381`). That collision is why phase timing needs the
`_resolutionPhaseBegan` flag (`PhaseTiming.cs:46`).

All ten share one policy block (`SpaceBattleSystemPolicies.Apply`, `:427-433`):
`Priority(Critical)` · `TickDivisor(1)` · `ThrottledTickDivisor(1)` · `CanShed(false)`. Every one also gates on
`.ShouldRun(() => state.IsRunning)`. Nothing can be shed, throttled, or skipped — which is why overrunning the budget
has no relief valve.

**Only `Combat` declares an `.Input(...)`.** The other nine ignore the ECS entirely for iteration and walk
`state.TickWorkset` — a managed `EntityId[]` — calling `IsAlive` + `OpenMut` per ship. That single fact is the whole
performance story.

#### The damage-intent event queues

Two systems in the table below reference "12 event queues". They are the demo's fan-in mechanism from the one parallel
system back into the serial world, and they are the best-engineered thing in the schedule.

**What they are.** One `EventQueue<DamageIntent>` **per worker**, built at DAG-construction time
(`CreateDamageIntentQueues`, `:389-400`):

```csharp
var queues = new EventQueue<DamageIntent>[SpaceBattleProductionSettings.EffectiveWorkerCount];
for (int workerId = 0; workerId < queues.Length; workerId++)
    queues[workerId] = dag.CreateEventQueue<DamageIntent>(
        $"DamageIntent-{workerId}", BehaviorRules.DamageIntentQueueCapacity);   // 65_536
```

So `DamageIntent-0` … `DamageIntent-11` on the benchmark's 12-worker config. Worker count is
`max(1, ProcessorCount - 4)` (`ProductionSettings.cs:13`).

| Property | Value |
|---|---|
| Payload | `readonly record struct DamageIntent(EntityId Attacker, EntityId Target)` (`CombatRules.cs:5`) |
| Payload size | **16 bytes** — `EntityId` is a packed `ulong`, `[StructLayout(LayoutKind.Explicit, Size = 8)]` |
| Capacity, each | 65,536 (`BehaviorRules.cs:39`) |
| Resident memory | 12 × 65,536 × 16 B = **12 MiB** |
| Lifetime | created once at DAG build; fully drained every tick, so no backlog accrues |

**Why one per worker.** It makes each queue single-producer / single-consumer. During the parallel `Combat` phase a
worker touches **only its own** queue (`:2143`), so there is no contention, no CAS, no lock on the hot path:

```csharp
EventQueue<DamageIntent> damageIntentQueue = damageIntentQueues[context.WorkerId];   // :2143
...
if (fireResult.Hit)
    damageIntentQueue.Push(new DamageIntent(attackerId, targetId));                  // :2168
```

This is exactly the pattern `doc/guide/05-systems.md:230` prescribes: never let parallel workers write shared
cross-entity state — have each emit into its own queue and let one serial system reduce them.

**How the DAG knows.** Both systems declare the queues explicitly, which is what makes the scheduler order them
(on top of the redundant `.After("Combat")`):

```csharp
foreach (var queue in damageIntentQueues) builder.WritesEvents(queue);   // Combat,           :2132-2135
foreach (var queue in damageIntentQueues) builder.ReadsEvents(queue);    // DamageResolution, :2190-2193
```

**How they are consumed** (`DamageResolutionSystem.Execute`, `:2196-2230`) — gate, count, rent, drain, sort, group:

```csharp
// gate (:2187) — skip the whole system when nothing was emitted
.ShouldRun(() => state.IsRunning && HasDamageIntents())      // LINQ .Any(q => !q.IsEmpty)  ⚠ :2282

int intentCount = 0;
foreach (var queue in damageIntentQueues) intentCount += queue.Count;
if (intentCount == 0) return;

var damageIntents = ArrayPool<DamageIntent>.Shared.Rent(intentCount);   // pooled — no per-tick alloc
int count = 0;
foreach (var queue in damageIntentQueues)
    count += queue.Drain(damageIntents.AsSpan(count, queue.Count));     // concatenate in worker order

Array.Sort(damageIntents, 0, count, DamageIntentComparer.Instance);     // by (Target, Attacker)  :2220
// then walk contiguous same-Target runs → one damage application per target
```

**The sort is the determinism device, and it is the point of the whole design.** Chunk-to-worker assignment is not
reproducible, so the concatenated order of the drained queues is not reproducible either. Sorting by
`(Target.EntityKey, Attacker.EntityKey)` canonicalises it, after which grouping by target is a linear scan over
contiguous runs and multi-attacker kill attribution is deterministic. **This is what allows `Combat` to be parallel at
all** — and it is why parallelising the other phases is safe rather than risky.

*Two observations.* The drain is genuinely allocation-free (`ArrayPool` rent/return), which makes it the cleanest hot
path in the demo — a striking contrast with `TargetLockIndexes`, which allocates ~280 KB per tick to do less. But the
gate is LINQ `.Any()` evaluated by the scheduler every tick (`:2187`, `:2282`), and the 65,536 capacity is **per queue**
while `ProductionSettings.cs:16` uses that same constant as the **global** `MaximumSupportedShipCount` — so the cap is
conflated: with 12 queues the real emission headroom is 786,432 intents, but ship count is refused above 65,536.

#### How a target is chosen, and how the 300-unit range is enforced

Referenced by system 6 below. Two mechanisms are easy to conflate: *how a candidate is picked* (a stateless hash) and
*how the range is enforced* (rejection, after the fact). The hash knows nothing about positions.

**Picking — addressing, not a stream.** There is no RNG object and no stored state. Every draw in the run has a unique
address, and `Value()` is a pure splitmix64 hash of it (`DeterministicRandom.cs:71-83`):

```csharp
Value(seed, packedShipId, decisionOrdinal, purpose) = Mix(seed ^ Mix(e) ^ Mix(d) ^ Mix(p))
```

Candidate #k simply uses `purpose = LockTargetCandidatePurpose + k` (→ 9…72). The hash's job is avalanche: `purpose`
increments by 1 per draw, yet the folded roster indices scatter across the whole roster. Without it, adjacent ships
would target a fixed offset away — `PackShipId` shifts the key left by 12, so naive arithmetic yields a rigid +4096
progression.

Folding to an index (`BehaviorRules.cs:152-172`, `DeterministicRandom.cs:30-52`) draws from `N-1` with bias-corrected
rejection, then skips the ship's own slot without a retry: `selected < sourceIndex ? selected : selected + 1`.

*Why this design:* the same battle replays identically after a crash, on another machine, regardless of which worker
runs first — `decisionOrdinal` is persisted in the DB (`Components.cs:67`). No shared mutable RNG means no draw-order
dependence, **which is exactly what makes parallelising the serial phases (§11 item 1) a safe mechanical change.**

**Enforcing the range — three separate layers.**

1. **At acquisition — reject and retry** (`:2090-2108`). The hash returns an arbitrary ship anywhere in the world; the
   code then opens it, measures, and discards it if too far:
   ```csharp
   var candidate = context.Transaction.Open(roster[candidateIndex]);
   if (TargetLockCleanupSystem.IsWithinLockRange(source, candidate)) return roster[candidateIndex];
   // else: next hash address, up to 64 tries; then EntityId.Null and no lock this tick
   ```
   `IsWithinLockRange` (`:1984-1992` → `CombatRules.cs:40-49`) is a squared-distance compare against
   `LockRange = 300f`, no `sqrt`. A failed attempt still consumes a `decisionOrdinal` (`:2036`), so next tick's draws
   differ.
2. **Continuously — re-validation every tick** (`:1850`). `TargetLockCleanup` re-tests every live lock with the same
   predicate and destroys it (disabling the weapon) once the target drifts beyond 300.
3. **At firing — a tighter, independent test** (`CombatRules.cs:33`) against `WeaponRange = 250f`. A ship can hold a
   valid lock and still miss, because the target sits in the 250–300 band.

**The consequence.** Acquisition succeeds as a function of *density*, not of correctness. At the demo's settings ~7.9 %
of all ships are in range, so ~12.6 draws suffice and only ~0.5 % of attempts exhaust all 64. Halve `LockRange` and
79 % of attempts return null — still "correct", but combat quietly stops happening. See §6 for why that is the real
argument for adopting the spatial index.

#### Recap table

Components abbreviated: Pos = Position, Bounds = SpatialBounds, Beh = Behavior, Trk = Tracking, Wpn = Weapon,
Afb = Afterburner, Lock = TargetLock, Mbr = ShipMembership, Run/RunState = SimulationRun\*.
`F` prefix = `ReadsFresh`, `A` prefix = `AdditionalReads`.

| # | System | Phase | Kind | Iterates | Reads | Writes | mean ms | µs/ship |
|---|---|---|---|---|---|---|---:|---:|
| 1 | ShipViewRefresh | ShipViewRefresh | `CallbackSystem` | the 2 `EcsView<Ship>` | Mbr | — | **0.000** † | — |
| 2 | State | State | `CallbackSystem` | `TickWorkset` | — | Beh | 51.0 | 1.02 |
| 3 | Steering | Steering | `CallbackSystem` | `TickWorkset` | `F`Beh, Pos | Beh, Trk, Motion, Afb | 57.6 | 1.15 |
| 4 | Movement | Movement | `CallbackSystem` | `TickWorkset` | `F`Beh, `F`Motion | Pos, **Bounds**, Motion | 60.5 | 1.21 |
| 5 | TargetLockCleanup | TargetLockCleanup | `CallbackSystem` | `TickWorkset` + lock ids | Pos, `F`Beh, `F`Lock | Beh, Trk, Motion, Wpn, Lock | **84.7** | **1.69** |
| 6 | Targeting | Targeting | `CallbackSystem` | `TickWorkset` | Pos, `F`Beh, `F`Lock | Beh, Lock | 47.6 | 0.95 |
| 7 | **Combat** | Combat | **`QuerySystem` `.Parallel()`** | **`.Input(ships)`** | `F`Beh, `F`Trk, `A`Pos | Wpn + 12 event queues | **8.9** ‡ | 2.14 ‡ |
| 8 | DamageResolution | **Resolution** | `CallbackSystem` | 12 event queues | ⚠ Beh *undeclared* | Health | 49.5 § | 0.99 § |
| 9 | Resolution | **Resolution** | `CallbackSystem` | destroyed/damaged sets | — | Beh, Trk, Motion, Wpn, Afb, Lock | 49.5 § | 0.99 § |
| 10 | Output | Output | `CallbackSystem` | `TickWorkset` (on capture) | `F`× 9 components | Run, RunState | 0.07 | — |

† Structurally unmeasurable — see §4. ‡ Wall-clock across 12 workers; ~107 ms CPU-time. § The two systems share one
timed phase; 49.5 ms is the pair.

#### Per-system detail

**1 · ShipViewRefresh** — `:1341-1355`
Refreshes both `EcsView<Ship>` instances and merges their `ViewDelta` (added/removed) into the sorted `EntityId[]`
roster via `ShipRoster.ApplyDelta`. The DAG root — the only system with no `.After()`.
*Notes:* does **two** refreshes over byte-identical views (`ShipMembershipViews.cs:70-71,102-112`) plus an 800 KB roster
reallocation. Its measured cost is erased one system later by `StateSystem`'s `BeginTick()`, so this — the one phase
most likely to be expensive — is the one phase nobody can see.

**2 · State** — `:1357-1408`
Calls `state.BeginTick(tx)`, reconciles pending target-lock index mutations, then decrements `Staging` timers and
promotes expired ships to `Wandering`.
*Notes:* calls `PhaseTiming.BeginTick()` (`:1368`), which `Array.Clear`s the phase accumulators and resets
`_tickStartTimestamp` — wiping system 1's measurement and excluding it from the reported tick total.

**3 · Steering** — `:1410-1692` (283 lines)
The behaviour FSM. Switches on `BehaviorMode` and dispatches to `ProcessWandering` / `ProcessTracking` /
`ProcessEscaping`, or re-wanders an expired `Disengaging` ship.
*Notes:* **there is no `Combat` case** (`:1445-1487`) — combat ships never steer, fly straight, and drift out of their
own lock range. Defers `Tracking` starts into a `List<TrackingStart>` and resolves them in a second pass (`:1497-1510`)
to avoid selecting targets mid-mutation. Binary-searches the roster at `:1603` to recover an index it already has.

**4 · Movement** — `:1694-1753`
`MovementRules.Advance` — integrate position, reflect off the six world walls — then rewrite `PositionComponent` and
`SpatialBoundsComponent`.
*Notes:* the AABB it writes is degenerate (`min == max == position`, `:1742-1747`), a 24-byte duplicate of the 12-byte
position, feeding an R-Tree that is never queried — ~3.5 ms/tick and 1.2 MB/tick of write bandwidth for nothing.
`MovementRules.Advance` also re-normalises (a `MathF.Sqrt`) and runs 12 `float.IsFinite` guards per ship per tick on a
vector that was already normalised when written.

**5 · TargetLockCleanup** — `:1755-1993` (239 lines) — **the heaviest phase**
Two independent loops: `AdvanceTimedBehaviorDurations` decrements Combat/Disengaging/Escaping timers over the whole
roster; `AdvanceExistingLocks` walks every active lock and advances `Acquiring → Locked → Releasing`, enabling the
weapon on lock and destroying locks whose target left range.
*Notes:* the only system doing **three** entity resolutions per ship (`IsAlive` + `Open` + `OpenMut`, `:1785-1800`),
which is why it is the most expensive. Allocates a fresh `EntityId[]` of all active locks every tick
(`state.CopyTargetLockIds()`, `:1825`).

**6 · Targeting** — `:1995-2110`
For each Combat-mode ship below its lock cap, find a target and `Spawn<TargetLock>`.
*Notes:* target search is **64 uniformly-random roster probes** with a distance rejection test (`:2081-2109`) — no
spatial query. Allocates a ~280 KB `Dictionary<long,int>` of owner lock counts **every tick** (`:2011`). Spawns
`PauseTargetLockCheckpointComponent` alongside every lock (`:2062-2071`) — an undeclared write, and double the write
volume. Binary-searches the array it is iterating (`:2037`) for an index the loop already has.

**7 · Combat** — `:2112-2174` — **the only parallel system**
The one idiomatic system: `QuerySystem` + `.Input(() => ships)` + `.Parallel()`, sharded across workers, each chunk
getting its own `Transaction` and `EntityAccessor`. Advances weapon cooldown, range-tests attacker→target, and pushes
`DamageIntent` into **its own worker's** queue (`damageIntentQueues[context.WorkerId]`) — textbook fan-in, exactly what
`doc/guide/05-systems.md:230` prescribes.
*Notes:* 2.5 % of tick time for comparable work to Movement's 16.7 %. `RecordShot` (`:2353`) `Interlocked`-increments
two adjacent `int`s on one cache line (`:2337-2338`) from all 12 workers — false sharing in the one parallel system.
Its input view is misnamed: `combatShips` has a predicate byte-identical to `runtimeShips`, so it fans out over all
50 k ships and `continue`s on the non-combat ones (`:2151`).

**8 · DamageResolution** — `:2176-2314` (phase `Resolution`)
Drains all 12 event queues, **sorts the merged intents by `(Target.EntityKey, Attacker.EntityKey)`** (`:2220`), groups
by target, applies damage, and `Destroy`s ships that reach zero health.
*Notes:* the sort is the determinism device and it is correct — but serial and global. `ShouldRun` calls
`HasDamageIntents()`, which is LINQ `.Any()` on the scheduler's gate path, every tick (`:2187`, `:2282`). Reads
`Ship.Behavior` at `:2246` while declaring only `.Writes<HealthComponent>()` — an undeclared access the author had no
legal way to declare (§8). **This is where entity destroys happen**, which is what CLUSTERWALK-01 constrains.

**9 · Resolution** — `:2374-2520` (phase `Resolution`, `.After("DamageResolution")`)
Post-damage reactions: clear locks referencing dead ships, put damaged survivors into `Escaping` (afterburner on,
weapon off, locks released), put killers into `Disengaging`, and re-wander ships whose tracking target died.
*Notes:* destroys `TargetLock` entities. Widest write set in the demo — six components.

**10 · Output** — `:2522-2665`
Writes the `SimulationRun` / `SimulationRunState` rows, detects terminal conditions (1 alive → Winner, 0 → Draw, cap →
TimedOut), captures an `InitialWorldSnapshot` if one was requested, publishes the tick sample to the observation
channel, and calls `EndTick`.
*Notes:* declares `ReadsFresh` on nine component types — the widest read set — but costs 0.07 ms because it touches
per-ship data only when a snapshot was explicitly requested. Publication is off-thread via `Channel<T>`, so the tick
never blocks on I/O.

#### What the table shows at a glance

- **`Behavior` is written by five systems** (State, Steering, TargetLockCleanup, Targeting, Resolution) and read fresh
  by four more. It is the contention hub of the whole schedule, and the reason DamageResolution cannot legally declare
  its read of it.
- **Six systems each make a full independent pass over all 50,000 ships.** Nothing is fused; the working set is walked
  six times per tick and does not fit L2.
- **The one system that declares an `.Input(...)` is the one that is fast.** That is the entire argument of this
  review, visible in a single column.

### What is hand-rolled over the top

| Side structure | Duplicates |
|---|---|
| `_shipRoster` : `EntityId[]` (800 KB) | the `Ship` archetype's entity set |
| `_tickWorkset` | **nothing — it is the same array**, aliased at `:733`, `:1031`, `:1109` |
| `TargetLockIndexes` (`SortedDictionary` + 2× `Dictionary<long, SortedSet<long>>` + relation map, 207 LOC) | two engine B+Tree indexes already declared on `TargetLockComponent.Owner`/`.Target` |
| 6× `_modeCount*`, `_aliveShipCount`, `_shotsFired`, `_hits`, `_deaths` | `COUNT(*) GROUP BY Behavior.Mode` |
| `[SpatialIndex]` R-Tree | **maintained every tick, never queried** |
| `PauseShipCheckpointComponent` (23 fields) + lock + run mirrors, 561 LOC | the engine's own WAL / tick-fence durability |

The demo showcases Typhon as a *storage* engine and routes around it as a *query* engine.

### Engine capabilities never touched

`PipelineSystem` · `ChunkedCallbackSystem` / `ChunkedParallel` · `.ChangeFilter(...)` · `.WritesVersioned()` ·
`SimTier` / `.Tier()` / `.CellAmortize()` / `.Checkerboard()` · cluster dormancy · extra tracks (`DeclareTrack`) ·
`PublishView` / TCP subscriptions · `NavigationQueryBuilder` (joins) · `OrderByField` / `ExecuteOrdered` ·
`IndexRef` / `EnumerateIndex` · `ClusterRef` / `GetSpan` / `WriteSpatial` (the SoA path the shipped sample uses) ·
`ForceCheckpoint` · `ctx.CreateSideTransaction` · `EpochGuard`.

Generated helpers exist and are unused: archetypes are `partial`, so `Ship.ReadAll` / `ReadWriteAll` / `SpawnBatch` are
all generated. The demo instead writes 6 sequential `Read`s in `CaptureShip`, 6 `Write`s in `Restore`, and spawns 50,000
ships one at a time.

**Overload response is not merely unused — it is switched off.** `MinTickRateHz = FixedTickRate` and
`QueueGrowthTicks = 0` (`:274-275`) pin the multiplier at 1×, while all ten systems are `SystemPriority.Critical`,
`CanShed(false)`, `TickDivisor(1)` (`:429-433`) — and the budget is missed 9×.

---

## 3. Implementation

`SpaceBattleSimulation.cs` is **2,665 lines / 13 types**. That is a file-organisation problem more than a god-class
problem — but `SimulationRuntimeState` (`:553-1339`, 787 lines, 20+ fields, four unrelated concerns, one `object _sync`)
is a genuine god class.

### The core mistake: the traversal idiom

Every system iterates a managed `EntityId[]` and resolves entities one at a time:

```csharp
foreach (var shipId in state.TickWorkset)      // IReadOnlyList → heap enumerator, 2 interface calls/element
{
    if (!context.Transaction.IsAlive(shipId)) continue;   // resolution #1
    var ship = context.Transaction.OpenMut(shipId);       // resolution #2
    ref var position = ref ship.Write(Ship.Position);      // slot lookup
    ...
}
```

Seven phases × 50,000 ships × 2 resolutions ≈ **700 k redundant entity resolutions per tick**, plus ~300 k interface
dispatches. `TargetLockCleanupSystem` does three (`IsAlive` + `Open` + `OpenMut`, `:1785-1800`).

This is exactly the ~1 µs/ship/phase the timings show. AntHill's own code comments the same figure independently:
*"~1.1 µs / hit on a cache-cold MVCC component-table read"* (`TyphonBridge.cs:2062`).

### Determinism machinery — the best part of the codebase

`DeterministicRandom` is **stateless**. Every draw is a pure hash:

```csharp
Value(seed, entityId, decisionOrdinal, purpose) = Mix(seed ^ Mix(entityId) ^ Mix(decisionOrdinal) ^ Mix(purpose))
```

`Mix` is splitmix64. `UniformIndex` uses proper rejection sampling. `UnitDirection` uses the correct Archimedes
uniform-sphere method, not naive lat/long. `DecisionOrdinal` lives **in the database** (`Components.cs:67`), so streams
reproduce across process restarts. `DamageResolutionSystem` sorts intents by `(Target, Attacker)` before applying
anything (`:2220`).

No shared PRNG state exists anywhere — **which is precisely what makes the parallelisation fix safe.**

---

## 4. Performance

**The demo runs at 2.8 TPS against its own 25 TPS specification.**

| | |
|---|---|
| Steady-state tick | **361.7 ms** (p99 482.7, max 1,815) |
| Budget for 25 TPS | 40 ms |
| Miss | **9× on mean, 12× on p99** |
| Real-time factor | **0.111× — 9× slower than real time** |
| Default run to timeout | **4.5 hours wall-clock for 30 min of logical time** |

### Per phase — the cause is visible in one column

| Phase | mean ms | µs/ship | Threading |
|---|---:|---:|---|
| ShipViewRefresh | 0.000 | — | **not measurable — see below** |
| State | 51.0 | 1.02 | serial |
| Steering | 57.6 | 1.15 | serial |
| Movement | 60.5 | 1.21 | serial |
| TargetLockCleanup | 84.7 | 1.69 | serial |
| Targeting | 47.6 | 0.95 | serial |
| **Combat** | **8.9** | 2.14 (CPU-time) | **parallel, 12 workers** |
| Resolution | 49.5 | 0.99 | serial |
| Output | 0.07 | — | serial |

**97.0 % of the tick is single-threaded.** Nine of ten systems are `CallbackSystem`; only `CombatSystem` is a
`QuerySystem` with `.Parallel()` (`:2126`). Amdahl ceiling on 12 workers: **1.02×**.

### Independent reproduction — on twice the cores

| | Committed report (16 logical) | Independent run (**32 logical**) |
|---|---:|---:|
| Steady-state tick | 361.676 ms | **362.04 ms** |

**Doubling the core count changed the tick time by 0.1 %.** The Amdahl ceiling is confirmed empirically, not argued.

### The benchmark's own evidence is partly self-invalidating

`ShipViewRefreshSystem` records its elapsed time into `_phaseElapsedTicks[0]` (`:1351-1353`). `StateSystem`, which runs
immediately after (`.After("ShipViewRefresh")`), calls `PhaseTiming.BeginTick()` (`:1368`), whose first act is
`Array.Clear(_phaseElapsedTicks)` (`PhaseTiming.cs:69-72`).

**ShipViewRefresh is structurally guaranteed to report 0.000 ms.** `BeginTick()` also sets `_tickStartTimestamp`, so its
cost is excluded from the reported total tick as well. The true tick is 361.7 ms **plus an unmeasured amount** — and the
unmeasured phase does two full `EcsView.Refresh()` passes over two byte-identical 50 k views plus an 800 KB roster
reallocation.

The report signs off acceptance criterion **AC4** on exactly that number
(`benchmark/reports/performance-report-20260812.md:104,122`).

Further instrumentation defects:

- `BeginParallelPhase`/`EndParallelPhase` (`PhaseTiming.cs:111-129`) is a refcount, not a barrier. If one worker exits
  before another enters, the "last worker" branch fires twice and double-counts — so **Combat's 8.9 ms is unreliable**,
  i.e. the one number used to argue parallelism works.
- The "2048-tick rolling window p99" yields exactly **one** window (`MeasurementTicks == RollingWindowSize == 2048`), so
  three table rows are three copies of one number. AC5 is structurally vacuous.
- `PhaseTimingCollector` runs unconditionally in production, allocates a `double[9]` per tick, and appends to an
  unbounded `List<T>` whose `Reset()` has zero call sites — a 45,000-tick run retains 45,000 samples forever.
- The console p50/p95/p99 are **histogram bucket midpoints**; at 50 k ships p95 and p99 collapse to the identical
  362.04. The observability surface the doc sells cannot resolve the tail it reports.

### Three artifacts that misrepresent

1. **The committed report was hand-placed.** `Program.cs:145-149` walks five `..` levels from
   `bin/Release/net10.0/` and needs six — it resolves to `demo/benchmark/reports/`, a directory that does not exist in
   the repo. The file in repo-root `benchmark/reports/` cannot have come from that code path.
2. **Its §7 test table is fabricated.** 12 of 15 per-fixture rows are wrong (BehaviorRules listed 4, actual 13;
   ShipMembershipView listed 11, actual 3). The 76 total is right — back-filled to a known number rather than
   transcribed.
3. **The machine name is a hardcoded string literal** (`BenchmarkDriver.cs:30`). It printed "AMD Ryzen 7 260" on a
   32-thread non-Ryzen machine, two lines above the correctly-detected `逻辑处理器数: 32`.

---

## 5. Scalability

**Real capacity is ~5,000 ships, not 50,000.** Measured post-combat steady state against the 40 ms budget:

| Ships | p50 | p99 | Achieved TPS | Verdict |
|---:|---:|---:|---:|---|
| 5,000 | 10.25 | 13.25 | 25.0 | **PASS** |
| 20,000 | 50.25 | 143.75 | 19.9 | fail 3.6× |
| 50,000 | 156.75 | 362.04 | **6.4** | fail 9.1× |

The crossover sits between 5,000 and 20,000 — **an order of magnitude below the advertised configuration**. There is
also a hard architectural cap at **65,536 ships** (`ProductionSettings.cs:16` ← `BehaviorRules.DamageIntentQueueCapacity`),
only 1.3× above the default.

### What caps scale

- **Single-threaded by construction** (97 %). More cores buy 2 %, now proven.
- **Six full-roster passes per tick** over a working set that does not fit L2, each re-resolving every entity.
- **Per-tick LOH churn:** `CopyOwnerLockCounts()` allocates a ~280 KB `Dictionary` every tick (`:2011`);
  `CopyTargetLockIds()` an 80 KB array (`:1825`); `ShipRoster.ApplyDelta` a fresh 800 KB array, twice per tick when
  ships die. ≈ **7 MB/s of LOH garbage** in a microsecond-latency engine's flagship demo.
- **~20,000 `SortedSet<long>` red-black trees each holding at most one element**, because
  `MaximumTargetLocksPerShip = 1` (`BehaviorRules.cs:41`).
- **False sharing:** `_shotsFiredThisTick`/`_hitsThisTick` are adjacent `int`s on one cache line (`:2337-2338`),
  `Interlocked`-incremented from all 12 workers — in the one place the demo parallelises.
- **LINQ `.Any()` on the scheduler's `ShouldRun` gate**, every tick (`:2187`, `:2282`) — direct violation of the repo's
  own "no LINQ in hot paths" rule.
- **Serial global `Array.Sort`** over all damage intents each tick (`:2220`) — the determinism mechanism, and serial.

---

## 6. Spatial partitioning

**Reliance on space partitioning: zero. The demo declares an index, maintains it every tick, and never queries it.**

`[SpatialIndex(20f)]` on `SpatialBoundsComponent` (`Components.cs:42`), a grid configured
(`SpaceBattleDatabase.cs:33-36`), and 50,000 AABBs rewritten every tick (`:1741-1747`) — where `min == max == position`,
a degenerate point-box duplicating `PositionComponent`. Every consumer of `SpatialBounds` is snapshot / checkpoint /
recovery code. Source-only grep for `WhereNearby|WhereInAABB|WhereRay|ClusterSpatialQuery`: **0 hits**.

Target acquisition instead draws up to **64 uniformly-random indices from the entire 50,000-ship roster** and
range-tests each (`:2081-2109`).

`TargetLockIndexes.cs` is **not** spatial — it is a bidirectional who-targets-whom adjacency map, zero geometry.

### Typhon's spatial machinery is mature and public

| API | Location | Status |
|---|---|---|
| `tx.Query<T>().WhereNearby<TComp>(x,y,z,r)` | `EcsQuery.cs:515` | works |
| `tx.Query<T>().WhereInAABB<TComp>(...)` | `EcsQuery.cs:528` | works |
| `tx.Query<T>().WhereRay<TComp>(...)` | `EcsQuery.cs:542` | works |
| `engine.ClusterSpatialQuery<T>().Radius(in BSphere3F)` | `ClusterSpatialQuery.cs:157` | works, **zero-alloc `ref struct` enumerator** |
| KNN / nearest | `SpatialRTree.Query.cs:1237` | **internal only** |

~8,775 LOC, **482 spatial tests passing**, differentially tested against a brute-force oracle. **AntHill uses it in its
hot loop and was deliberately migrated onto the faster enumerator form** for a documented ~1.1 µs/hit saving
(`TyphonBridge.cs:2059-2067`). SpaceBattle ignored all of it.

### But spatial is not the lever — and this is the counterintuitive part

The random sampler is O(1) *expected*, because at `LockRange = 300` in a 1000³ cube with 50,000 ships, **7.9 % of the
entire world population is within lock range of any ship** (~3,935 neighbours; ~12.6 probes to a hit). Total distance
math is ~16,000 computations/tick ≈ **25 µs — 0.07 % of the budget**.

A naive swap to `WhereNearby(...).Execute()` would be a **300× regression**: it returns a materialised `HashSet`
(`EcsQuery.cs:830`) of ~3,935 entries when the caller wants one. Only the zero-alloc enumerator with an immediate
`break` is viable here.

Re-based against the **actual** 361.7 ms tick, not the 40 ms target:

| Fix | Saves | % of 40 ms budget | % of actual tick |
|---|---:|---:|---:|
| Delete / properly use the dead R-Tree | 3.5 ms | 8.8 % | **0.97 %** |
| Fix `TargetLockIndexes` storage | 0.85 ms | 2.1 % | 0.24 % |
| `TickDivisor` on 2 phases | 1.5 ms | 3.8 % | 0.41 % |
| Add Combat steering (lock churn) | 2.0 ms | 5.0 % | 0.55 % |
| **All spatial / index work** | **7.9 ms** | 19.6 % | **2.2 %** |
| **Parallelise the 6 serial phases** | **~322 ms** | — | **89 %** |

**Parallelism is 41× the leverage of every spatial and indexing fix combined.**

**The genuine spatial defect is robustness, not throughput.** Halve the lock range and 79 % of acquisitions return null
— combat silently stops happening, with no error and no slowdown. A spatial index degrades gracefully; a capped
rejection sampler falls off a cliff. *That* is the argument for adopting it.

Two tuning errors regardless: `[SpatialIndex(20f)]` omits `cellSize`, which per `Attributes.cs:213` **disables the
coarse broadphase filter entirely** (the demo already has a cell size and does not pass it); and `LockRange = 300` vs
`cellSize = 100` is mistuned — a range query would touch 7×7 cells of a 10×10 grid.

### Data layout

**Helps.** Typhon stores each component type in its own table — already structure-of-arrays across components.
`StorageMode.SingleVersion` on all hot components means no MVCC chain walk. `SpatialBoundsComponent` is already an
`AABB3F` in the engine's native spatial field type.

**The demo defeats it.** Access is AoS-shaped: every pass does `OpenMut(shipId)` then touches 3–5 components on that one
entity. Iteration is via a roster of `EntityId`, a permutation over storage order, with every access through `Open()`.
**A spatial rewrite that keeps this idiom will not go faster, because the cache misses live there, not in the neighbour
search.**

---

## 7. Ease of use — what this says about Typhon

**Total: 6,550 LOC for a simulation whose actual game rules are 453 lines (7 %).** Everything else is plumbing.
~357 lines of pure engine-facing setup before the first tick body executes, plus ~1,900 lines of derived-state
scaffolding built because the engine's abstractions were not reached for.

### Positive

The declarative schema is clean (~10 lines per component, ~12 per archetype, source-generated). The DAG declaration is
pleasant — phases, `.After()`, typed access sets. Per-worker `EventQueue<T>` for cross-system fan-in is a good
primitive. **An external contributor with no prior Typhon exposure produced a working, durable, recoverable 50 k-entity
simulation in one commit.** That is a real DX win and the strongest evidence in the demo's favour.

### Friction, in order of severity

1. **The fast path is not the obvious path.** The contributor reached for `CallbackSystem` + manual roster iteration
   nine times out of ten, and `QuerySystem` + `.Parallel()` once. Nothing in the API made the 12× difference visible.
   If the default shape a newcomer writes is 97 % serial, the API is steering them wrong.
2. **The engine's own indexes are invisible in use.** They declared `[Index(AllowMultiple=true)]` on `Owner`/`Target`
   and then hand-built a four-way managed index over the same relation.
   `doc/guide/02-modeling.md:135` says *"You don't query the index directly — you filter on the field in a normal
   query."* `rules/indexing.md:29` (IX-01) names two-index-homes as the failure this causes.
3. **Double registration.** Every component must be declared in the archetype (`Register<T>()`) *and* registered on the
   engine (`TyphonOptions.Register<T>()`). One fact, two surfaces; forget one and you fail at open, not at compile.
4. **Two bootstrap idioms.** SpaceBattle uses static `DatabaseEngine.Open(...)`; AntHill uses DI
   `AddScopedDatabaseEngine` + `RegisterComponentFromAccessor<T>()`. Two demos in one repo disagree on how to start
   the engine.
5. **Durability got mirrored by hand.** 561 LOC of `Pause*Checkpoint` components duplicating every ship field, written
   on every lock spawn (`:2062-2071`). Partly justified — Typhon has no PITR — but `Restore` bails unless
   `checkpoint.CompletedTicks == completedTicks` (`SpaceBattleCheckpoint.cs:112-122`), i.e. the shadow is only used when
   tick-fence data is *already correct*. It is validation, not recovery.
6. **Incremental vs pull view semantics are invisible and load-bearing.** Whether `ToView()` returns an auto-refreshed
   or manually-refreshed view depends on whether a `WhereField` was attached — and only the runtime knows. SpaceBattle
   needed a dedicated system plus a 153-line wrapper to work this out.
7. **The app doesn't trust the view.** 17 defensive `transaction.IsAlive(id)` guards, one at the top of nearly every
   entity loop.
8. **Doc defects the demo exposes.** Archetype ids are hard-coded 3000–3002 despite `doc/guide/01-first-app.md:179`
   promising *"you never pick a number"*. `doc/guide/02-modeling.md:117` states components must be ≥ 8 bytes, but
   `WeaponComponent` is 2 bytes and `HealthComponent` 4 and both work — the floor is enforced only on the Versioned
   path (`ChunkBasedSegment.cs:96`), so the rule is storage-mode-dependent and documented as universal.

---

## 8. Correctness against `rules/`

No hard violation. Several near-misses, one genuine defect, one architectural caveat.

### Genuine defect — MVCC cleanup pinned for the whole run

`ShipMembershipViews.cs:65` opens `engine.CreateQuickTransaction()` and holds it until process shutdown. It becomes
`TransactionChain.Tail` with the lowest TSN, and `ProcessDeferredCleanups` only runs when the tail transaction is
disposed — so **every destroyed ship's revision chain and freed chunk stays pinned for the entire run** (up to 50,000
entities over 4.5 hours).

The docs are unambiguous that this is unnecessary — `doc/feature-set/Querying/persistent-views.md:61`:

> "No held transaction. A view tracks a TSN watermark, not an open transaction — it never blocks MVCC cleanup, however
> long it lives."

AntHill does it correctly: `using var txView = DBE.CreateQuickTransaction(); AntView = txView.Query<Ant>().ToView();`
(`TyphonBridge.cs:430-431`). **One-line fix.**

### Architectural caveat — CLUSTERWALK-01 gates phase fusion

`rules/ecs.md:49` **CLUSTERWALK-01** `[fatal]` `[silent]`, `verified: NOT COVERED`: no cluster walk may run concurrently
with `Destroy`+`Commit` on the same archetype. Destroy is applied *synchronously inside `Commit()`*, not fence-deferred
like migration and AABB refresh. Violation silently skips or double-processes entities.

SpaceBattle destroys Ships and TargetLocks every tick. **It is safe today only because its DAG is a strict serial
chain — its worst performance decision is what makes it correct.** AntHill dodged this by eliminating destroys entirely
(respawn-as-larva); the rule notes the hazard was *avoided, not fixed*.

Consequence: parallelising Movement / State / Steering / Targeting stays safe, because destroys are confined to the
Resolution phase and phases are barriers. **Phase fusion, and any parallelism inside the Resolution phase, walks into a
fatal, silent, untested hazard.**

### An engine API gap the demo documents

`DamageResolutionSystem` reads `Ship.Behavior` (`:2246`) while declaring only `.Writes<HealthComponent>()` (`:2187`) — an
undeclared access (DV-01, `rules/runtime-scheduling.md:216`, strict-mode opt-in, off here).

The author had no legal way to declare it: `Reads<Behavior>` is a hard AC-02 error (same-phase R×W with
`ResolutionSystem`), `ReadsFresh` cycles against the explicit `.After`, and `ReadsSnapshot` is banned by AC-05 because
Behavior is SingleVersion. **The access model cannot express "I read T, and I am explicitly ordered before the
same-phase writer."** The only exit was silence.

### Other

- **The core data path sits on an explicitly unvalidated feature.** Both views are keyed on
  `ShipRunMembershipComponent`, which is `StorageMode.Transient` (`Components.cs:94`).
  `persistent-views.md:70`: *"SingleVersion/Transient views are not fully validated yet."* That includes the input to
  the one parallel system.
- **Crash recovery is untested and the doc oversells it.** `doc/demos/space-battle.md:24,49` claims ✅ Working. No test
  kills a process; what exists is graceful-pause resume. On a hard crash there is no valid pause checkpoint, so
  `Restore` early-returns and validation throws rather than recovers.
- `engine.WriteTickFence(0)` (`ShipMembershipViews.cs:63`) is documented as the *"test/admin path"*
  (`DatabaseEngine.TickFence.cs:23,27`); the only production caller in `src/` is the runtime itself.
- **Startup: 4.15 s of the 5.99 s is a full engine dispose-and-reopen** after bulk load (`SpaceBattleHost.cs:63-73`),
  where `ForceCheckpoint()` is the documented answer for exactly this scenario
  (`doc/feature-set/Durability/checkpoint-v2/README.md:36-38`).

---

## 9. Tests

**76/76 pass.** No flakes, no hangs. Builds clean in Debug and Release. Both projects are in `Typhon.slnx`.

| Area | Rating |
|---|---|
| Recovery / checkpoint | **Strong** — 7 validation tests incl. no-partial-restore; 8 startup; 7 pause incl. a 4-cycle leak check |
| Simulation rules | **Strong** — 21 pure-function tests, all < 30 ms, no I/O |
| Determinism | **Weak** — 2 tests, **both at 8 ships in an 80³ world** |
| Observation / telemetry | **Weak** — 3 tests, ordering-only assertions (`p95 >= p50`); no value validated |
| Concurrency | **Weak** — exactly one test drives real parallelism, at WorkerCount = 2 |
| Performance regression | **Absent** — zero perf assertions; `BenchmarkDriver` invoked by no test |

### The three structural problems

1. **Every test runs at ≤ 64 ships.** Production is 50,000 — a **780× gap**. Terminal outcomes (Winner/Draw) are
   verified only at 1 and 2 ships with 10-tick caps (`TerminalOutcomeTests.cs:35,67,98`).
2. **No CI coverage whatsoever.** `grep -rn -i spacebattle .github/ scripts/` returns nothing. `pre-push.sh` runs
   Engine + Workbench only. These 76 tests have never been enforced; the demo will bit-rot on the next engine API
   change.
3. **The suite takes 553 s (9.2 min).** 46 of 75 measured tests exceed the repo's 300 ms ceiling; two determinism tests
   take 2m14s each and alone consume 27 % of the suite. Root cause is structural:
   `SpaceBattleSimulation.cs:270` hard-wires `BaseTickRate = 25 Hz` **in tests as well as production**. There is no
   fast-forward, so a test waiting for tick *N* costs ≥ *N*/25 seconds by construction.

**Credit:** zero `Thread.Sleep`/`Task.Delay` — the tests use real synchronisation, per the repo convention.

**Debits:** every `WaitFor*` is `SpinWait.SpinUntil` (`:73`, `:94`, `:134`) — busy-spin, inherited by tests.
`PauseRecoveryTests.cs:52,62` prove absence by timeout. `SpaceBattleProductionSettings.TestWorkerCountOverride`
(`ProductionSettings.cs:10`) is a mutable static set and reset by tests — safe only because nothing is
`[Parallelizable]`.

---

## 10. The good, the bad, the ugly

### The good

- **Determinism design is excellent** (§3) and makes the parallelisation fix safe.
- Recovery/resume is serious: 24 invariant checks, terminal-state protection, identity validation, refuses to overwrite
  completed runs.
- Pure rule modules (`MovementRules`, `CombatRules`, `BehaviorRules`) are clean, side-effect-free, English-only,
  unit-tested.
- Observation properly decoupled via `Channel<T>` off the tick thread; sink exceptions swallowed rather than killing
  the sim.
- Correct engine etiquette: class-based systems over lambdas; never calling `Commit`/`Dispose` on `ctx.Transaction`;
  per-worker queues feeding a serial resolver — exactly the pattern `doc/guide/05-systems.md:230` prescribes.

### The bad

- 97 % serial; 9× over its own budget; ships as ✅ Working on every row.
- Real capacity ~5,000 ships against 50,000 advertised.
- Tests capped at 64 ships, in no CI gate, 9.2 minutes long.
- Dead code: `IsDestroyedShip`, `TargetLockIndexes.Contains`, `PhaseTimingCollector.Reset`/`SampleCount`,
  `WeaponFireResult.Attempted`, `DamageResolution.ParticipatingAttackerCount` (computed per group, stored, never read),
  `SimulationDefinition.SpatialMargin`.
- `_tickWorkset` and `_shipRoster` are the same array behind two fields, two wrappers, two counters.
- `combatShips` and `runtimeShips` are created with a **byte-identical predicate** (`ShipMembershipViews.cs:70-71`),
  both refreshed every tick; `CombatSystem` then fans out over all 50 k ships and `continue`s on the non-combat ones.
- `TargetingSystem` binary-searches the array it is currently iterating to recover the loop index it already has
  (`:2037`, and again at `:1603`).
- `PhaseTimingCollector._samples` is an unbounded `List<T>` raced across threads, returned live to the benchmark reader
  while the sim runs.
- Naming collision (`ShipRoster` static class vs `SimulationRuntimeState.ShipRoster` property) forces `global::`
  escapes at `:729`, `:846`, `:1102`.
- Repo hygiene: a stray `docs/adr/` at the root with one Chinese ADR numbered 0008, colliding with the real
  `claude/adr/` (64 ADRs); a demo-specific perf report dropped into `benchmark/reports/` where CI regression reports
  live. **The commit that claimed to clean both up is empty** — its tree hash is identical to its parent.
- Commit message uses a `feat:` Conventional Commits prefix, which `CLAUDE.md` explicitly forbids.

### The ugly

- **The benchmark validates AC4 against a number the instrumentation cannot produce as anything but zero** (§4). That
  is the one that undermines trust in the rest of the report.
- **The committed report was hand-placed, its test table is fabricated, and its machine name is a hardcoded literal**
  (§4).
- **All user-facing output and ~40 exception messages are in Chinese** in an English codebase. Identifiers are all
  English and the pure-logic files are English-only — a consistent boundary, not sloppiness — but
  `throw new InvalidOperationException("未知的游荡决策。")` and a fully Chinese generated perf report are not shippable
  for a public demo.
- **`SteeringSystem` has no `Combat` case** (`:1445-1487`). A ship that enters Combat stops steering entirely, flies
  straight, and drifts out of its own 300-unit lock range in ~150 ticks. Acquisition costs 50 ticks, weapon interval is
  50 — so **~2 shots per lock against the 5 hits needed for a kill**, while any damaged survivor flees at 1.5× with its
  weapon disabled. Attrition from 50,000 to 1 looks unreachable; `doc/demos/space-battle.md:66`'s claim that runs
  "normally end earlier with one or zero ships remaining" is unsubstantiated and, on this math, false.
  **It isn't a battle — it's Brownian motion with occasional potshots.**
- A latent RNG bug no test can catch: `LockTargetCandidatePurpose = 9` spans purposes 9–72 (64 candidates), but
  `EscapeFacePurpose = 10` sits **inside** that span (`BehaviorRules.cs:43-49`). The escape-face roll and lock-candidate
  #1 share a stream. `UniformIndex`'s rejection loop bumps `purpose + attempt`, walking further in. Separately,
  `Value()` XOR-combines its inputs, so it is commutative in (entityId, ordinal, purpose) — chain the mixes instead.

---

## 11. Ranked improvements

| # | Fix | Effect | Cost |
|---|---|---|---|
| 0 | **Dispose the view transaction** (`ShipMembershipViews.cs:65`) | removes a whole-run MVCC cleanup leak | **1 line** |
| 1 | **Convert Movement / State / Steering / Targeting to `QuerySystem` + `.Parallel()`.** Access sets are already declared; determinism is order-independent by construction; destroys stay Resolution-phase-only so CLUSTERWALK-01 is not exposed | **361.7 → ~38 ms. Meets the 25 TPS spec** | days, low risk |
| 2 | **Fix `PhaseTiming`** — move `BeginTick()` ahead of ShipViewRefresh, make the parallel phase a real barrier, bound `_samples`, fix the rolling window, gate it behind the benchmark flag | makes every other number trustworthy | hours |
| 3 | **`ForceCheckpoint()` instead of the engine dispose-and-reopen** | −4.15 s startup | 1 line |
| 4 | **Decouple tick rate from tests, then put the suite in the merge gate**; add one test above 10 k ships | 9.2 min → seconds; closes the 780× gap; stops silent rot | hours |
| 5 | **Add a `Combat` case to `SteeringSystem`** | makes it an actual battle; kills ~360 lock respawn cycles/tick | ~20 lines |
| 6 | **Re-enable overload response, or stop claiming a fixed 25 TPS** | honesty | 2 lines |
| 7 | **Delete `TargetLockIndexes`**; query the engine indexes already declared. At minimum flatten to `long[]` and stop copying it twice per tick | −7 MB/s LOH, −0.85 ms | ~1 day |
| 8 | **Either use the spatial index via the zero-alloc enumerator, or delete it** (and the AABB writes). Do **not** use `WhereNearby(...).Execute()` here | −3.5 ms; robustness at any range | 5 lines to delete |
| 9 | Fix the RNG purpose-namespace collision; chain the mixes in `Value()` | correctness | ~5 lines |
| 10 | Translate output/exceptions to English; move `docs/adr/0008` into `claude/adr/`; remove the demo report from `benchmark/reports/` | shippable | hours |
| ~~11~~ | Phase fusion / roster removal — **gated on CLUSTERWALK-01** | deferred | blocked |

**Reject:** SIMD distance kernels (0.05 % of budget) and Morton-ordering the roster (it does not reorder component
storage — that needs the engine's cluster migration machinery).

---

## 12. Findings for the engine, not the demo

Three items are Typhon findings and worth raising regardless of what happens to this demo:

1. **The access-declaration model cannot express ordered same-phase reads** (§8). `Reads` is a hard AC-02 error,
   `ReadsFresh` cycles, `ReadsSnapshot` is banned for SingleVersion. A correctness-declaration system that forces users
   to under-declare is worse than none.
2. **CLUSTERWALK-01 is `[fatal]`, `[silent]`, and `NOT COVERED`** — and both demos hit its shape. AntHill avoided it by
   design constraint; SpaceBattle avoids it by accident of serialisation. The next person who parallelises a system
   that destroys entities gets silent data loss.
3. **The obvious spatial API is the trap.** `WhereNearby(...).Execute()` materialises a `HashSet` — 3,935 entries
   allocated when the caller wants one. The zero-alloc `ClusterSpatialQuery` enumerator exists but is the non-obvious
   path.

Plus: components must be registered twice; the two demos disagree on how to bootstrap the engine; and the guide
promises "you never pick a number" for archetype ids while both demos pick numbers.

---

## 13. Bottom line

As an **engine exercise** this is substantial, competent work. The determinism design and recovery machinery are better
than most demos get, and it proves a newcomer can build a persistent, durable, recoverable 50 k-entity simulation on
Typhon from the documentation alone.

As a **performance showcase** it currently argues against Typhon: it runs 9× slower than real time, leaves 11 of 12
cores idle, its real capacity is an order of magnitude below what it advertises, and its headline optimisation claim is
an artifact of broken instrumentation.

The gap between those two readings is almost entirely one mechanical change — item 1 — and everything else on the list
is cheap by comparison.

**One judgement call:** the demo is in `Typhon.slnx` and published at `doc/demos/space-battle.md` claiming 25 TPS with
✅ on every row. Until item 1 lands, that page overstates what the code does, and the honest 12× miss is disclosed only
in a Chinese-language report under `benchmark/` that the public doc never references.
