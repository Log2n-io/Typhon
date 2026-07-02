# Worker Pool & Threading Model
> Core allocation between a dedicated tick-metronome thread and a pool of worker threads that execute the system DAG in parallel.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Tick-Based Execution Engine](./README.md)

## 🎯 What it solves

A tick loop needs two things that fight each other on a single thread: jitter-free, sub-millisecond
timing for the metronome, and as much parallel throughput as possible for system execution. Putting
both on one thread means a slow tick delays the next tick's wakeup; dedicating every core to workers
leaves nothing driving the clock. The Runtime separates the two concerns onto different threads, each
using a wait strategy suited to its own latency budget, so timing precision and worker throughput
don't trade off against each other.

## ⚙️ How it works (in brief)

A dedicated `Typhon.TickDriver` thread (`AboveNormal` priority) owns the metronome: a three-phase
wait — `Thread.Sleep(1)` while far from the scheduled tick, `Thread.Yield()` as it nears, raw
`Thread.SpinWait` for the final ~50µs — gets sub-microsecond timing precision without burning a full
core when ticks are milliseconds apart. The next tick's target is always computed from the
*previous target* (metronome-style), so timing error never compounds. `RuntimeOptions.WorkerCount`
worker threads (also `AboveNormal` priority) execute the system DAG via any-worker dispatch — workers
race to claim ready systems via CAS, with no fixed thread-to-system affinity. Between ticks, workers
block on a kernel wait (`ManualResetEventSlim`, ~1-5µs wake latency) rather than spinning: that
latency is negligible against a tick gap that's milliseconds long, and the wait costs zero CPU while
idle. The TickDriver wakes every worker at once for the next tick (a generation-counter bump plus a
single `Set()`) — there is no per-worker staggered wake in the current implementation.

## 💻 Usage

```csharp
var options = new RuntimeOptions
{
    BaseTickRate = 60,
    WorkerCount = 12,   // explicit; -1 (default) = Max(1, Environment.ProcessorCount - 4)
};

using var runtime = TyphonRuntime.Create(engine, schedule => { /* ... */ }, options);
runtime.Start();        // launches WorkerCount worker threads + the Typhon.TickDriver thread

Console.WriteLine($"{runtime.Scheduler.WorkerCount} workers, tick {runtime.CurrentTickNumber}");
```

| Option | Default | Effect |
|---|---|---|
| `RuntimeOptions.WorkerCount` | -1 (`Max(1, ProcessorCount - 4)`) | Number of worker threads executing the system DAG |
| `RuntimeOptions.BaseTickRate` | 60 | Metronome rate (Hz) driving the TickDriver thread |

## ⚠️ Guarantees & limits

- The auto-detect formula reserves headroom for I/O and OS/background work — `Max(1,
  ProcessorCount - 4)`, never all cores by default.
- Worker wake-up is a single kernel-event signal shared by every worker for a given tick; there is no
  dynamic/partial worker wake. This is a deliberate v1 choice — under-waking risked doubling tick
  time at 60-128Hz in POC measurements — not a missing feature; idle workers cost almost nothing
  while blocked on the kernel wait.
- The TickDriver's three-phase wait self-calibrates once at startup against this machine's actual
  `Thread.Sleep(1)` resolution, so the Sleep→Yield split point adapts across OS/hardware without
  configuration.
- An unhandled exception inside a system body cannot kill a worker thread: it is caught, the failing
  system and its successors are marked failed for that tick, and the worker keeps claiming other
  ready systems.
- Scaling falls off once a workload exceeds a single CCD's worth of cores on multi-CCD hardware
  (cross-CCD memory access cost dominates) — see Performance Characteristics in
  `claude/overview/13-runtime.md`.

## 🧪 Tests

- [DagSchedulerTests](../../../../test/Typhon.Engine.Tests/Runtime/DagSchedulerTests.cs) — `MultiWorker_DependencyRespected` (any-worker CAS dispatch), `Shutdown_Clean` (worker join on teardown)
- [TyphonRuntimeTests](../../../../test/Typhon.Engine.Tests/Runtime/TyphonRuntimeTests.cs) — `Start_Shutdown_Clean` — TickDriver + worker pool full lifecycle

## 🔗 Related

- Parent feature: [Tick-Based Execution Engine](./README.md)
- Sibling: [Execution Modes](./execution-modes.md), [Parallel Tick Fence](./parallel-tick-fence.md)

<!-- Deep dive: claude/design/Runtime/04-threading-and-modes.md, claude/overview/13-runtime.md (§Cadence, §Worker Thread Model) -->
