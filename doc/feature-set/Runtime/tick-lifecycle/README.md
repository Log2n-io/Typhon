# Tick Lifecycle & Transaction Management
> Runtime-owned UoW per tick and auto-created per-system Transactions — the developer never commits or disposes a Transaction manually.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Runtime](../README.md)

## 🎯 What it solves

Every tick, every system needs entity access that is correct, isolated, and durable — without the developer
hand-rolling a UoW/Transaction lifecycle. Manual management invites three failure modes: forgetting to commit
or dispose a Transaction (silent data loss, resource leak), creating a Transaction on the wrong thread
(violates Transaction's single-thread affinity), or not handling a commit failure (crash instead of graceful
recovery). The Runtime eliminates all three by owning the lifecycle entirely.

## ⚙️ How it works (in brief)

At tick start the Runtime opens one Deferred-durability `UnitOfWork`. For each `CallbackSystem`/`QuerySystem`
in the DAG, it creates a `Transaction` on the worker thread that executes that system, hands it to the system
via `TickContext.Transaction`, and commits it the instant the system returns — application code must never
call `Commit`/`Dispose` on it. Entities spawned or destroyed through that Transaction become visible to
systems that run later in the tick (their Transactions get a higher TSN) but not to systems already running or
running in parallel. After the last system completes, the Runtime flushes the UoW once — a single WAL fsync
covering every system's commits for the whole tick — then disposes it. `PipelineSystem`s do not receive a
Transaction at all; their entity access goes through Gather/Scatter pipelines instead.

## 💻 Usage

```csharp
EntityId spawnedId = default;

using var runtime = TyphonRuntime.Create(dbe, schedule =>
{
    schedule.PublicTrack.DeclareDag("Game")
        .CallbackSystem("Spawner", ctx =>
        {
            var pos = new Position(1, 2, 3);
            spawnedId = ctx.Transaction.Spawn<Unit>(Unit.Pos.Set(in pos));
            // No Commit()/Dispose() here — the Runtime commits ctx.Transaction when this system returns.
        })
        .CallbackSystem("Reader", ctx =>
        {
            // Reader gets its own Transaction (higher TSN) — sees Spawner's commit within the same tick.
            if (ctx.Transaction.TryOpen(spawnedId, out var unit)) { /* ... */ }
        }, after: "Spawner");
}, new RuntimeOptions { BaseTickRate = 60 });

runtime.Start();
// ... runtime ticks on its own dedicated thread ...
runtime.Shutdown();
```

| Option | Default | Effect |
|---|---|---|
| `RuntimeOptions.BaseTickRate` | 60 | Tick metronome rate (Hz) — also the cadence of the per-tick WAL flush |
| `RuntimeOptions.WorkerCount` | -1 (`ProcessorCount - 4`) | Worker threads executing per-system Transactions in parallel |

## ⚠️ Guarantees & limits

- Exactly one `Transaction` per `CallbackSystem`/`QuerySystem` invocation; parallel `QuerySystem`s get one
  Transaction per chunk. Never call `Commit`/`Dispose` on `ctx.Transaction` — the Runtime owns its lifecycle.
- The tick's main UoW uses Deferred durability: one WAL flush at tick end (~0.1ms) regardless of write count.
  A crash mid-tick loses at most that one tick's uncommitted work (acceptable for SV/Transient data) — see
  [Side-Transactions](./side-transactions.md) for writes that cannot tolerate that loss.
- Spawn/Destroy via a system's Transaction is visible to systems that run later in the DAG (new Transaction,
  higher TSN) and to the spawning system itself (pending-spawns map) — NOT to systems already executing or
  running in parallel with it.
- Destroyed entities remain readable (tombstoned, `DiedTSN` set) until deferred GC reclaims them; they cannot
  be written.
- UoW flush failure is not retried — input queues and side effects from the tick may have already changed, so
  the tick is skipped rather than replayed; the previous tick's committed state remains safe via the WAL.
- `OnFirstTick`/`OnShutdown` handlers receive their own `TickContext` with a dedicated Transaction (Immediate
  durability for `OnShutdown`) for one-time initialization/cleanup work.

## 🔗 Related

- Sub-features: [Side-Transactions (Immediate Durability)](./side-transactions.md), [Parallel Tick Fence (WriteTickFence)](./parallel-tick-fence.md)

<!-- Deep dive: claude/design/Runtime/01-tick-lifecycle.md, claude/overview/13-runtime.md -->
