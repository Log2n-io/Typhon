# Distributed Tracing (Activity API)
> A centralized `System.Diagnostics.ActivitySource` and OTel-semantic attribute constants for correlating Typhon with an application's own OTLP trace.

**Status:** 🚧 Partial · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Observability](./README.md)

## 🎯 What it solves
Diagnosing a slow or failed operation across a whole application usually means correlating "what the app asked for"
with "what happened downstream" — an HTTP handler, a gRPC call, a database operation, all as one connected trace
instead of separate, timestamp-correlated logs. `TyphonActivitySource` gives host applications a ready-made,
OTel-semantic `ActivitySource` to fold their own Typhon-adjacent instrumentation into, using the same attribute
naming (`typhon.transaction.tsn`, `typhon.entity.id`, ...) the engine's other observability surfaces use, exportable
to any OTLP backend (Jaeger, Grafana Tempo, etc.) alongside the rest of the application's trace.

## ⚙️ How it works (in brief)
`TyphonActivitySource.Instance` is a single `System.Diagnostics.ActivitySource` (name `Typhon.Engine`); `TyphonSpanAttributes`
supplies matching OTel-semantic attribute-name constants (transaction, entity, index, page-cache, ECS). Spans started on it
nest automatically under whatever `Activity.Current` the host already has open — no explicit context plumbing — and export
through the standard OpenTelemetry SDK. **The engine itself does not yet call this source internally**: Transaction, Entity,
B+Tree, and page-cache operations are instrumented instead through the engine's own [Typed-Event Profiler](../../../claude/overview/09-observability.md#98-typed-event-profiler)
(`TyphonEvent`) — a separate, higher-throughput, non-OTel pipeline consumed by the Typhon Workbench rather than an OTLP
backend. The Profiler does capture `Activity.Current`'s trace context when a host span is open, so its records nest under
an application's OTel trace for correlation, but no engine operation materializes as an `Activity` on this `ActivitySource`
today; it exists for hosts that want to add their own manually-instrumented spans using the same naming convention.

## 💻 Usage
```csharp
// Program.cs / host startup — register Typhon's source with the OTel SDK
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("MyApp"))
    .WithTracing(tracing => tracing
        .AddSource(TyphonActivitySource.Name)   // "Typhon.Engine"
        .AddOtlpExporter(o => o.Endpoint = new Uri("http://localhost:4317")));

// Manual instrumentation of your own code, nesting under the same source and attribute convention
using var activity = TyphonActivitySource.Instance.StartActivity("MyApp.CustomStep");
activity?.SetTag(TyphonSpanAttributes.EntityId, entityId);
```

| Attribute group | Constants | Typical use |
|---|---|---|
| Transaction | `TransactionTsn`, `TransactionStatus`, `TransactionComponentCount`, `TransactionConflictDetected` | Tagging a host-defined span around a UoW |
| Entity | `EntityId`, `ComponentType`, `ComponentRevision`, `ReadFound` | Tagging a host-defined span around an ECS operation |
| Index | `IndexName`, `IndexOperation`, `IndexNodeSplit`, `IndexNodeMerge` | Tagging a host-defined span around an index operation |
| Page cache | `PageId`, `PageSource`, `CacheHit` | Tagging a host-defined span around a storage operation |

## ⚠️ Guarantees & limits
- **No engine-internal spans yet** — `TyphonActivitySource` is real, callable API, but nothing in `Typhon.Engine` calls
  `StartActivity` on it today; Transaction/Entity/B+Tree/page-cache visibility comes from the Typed-Event Profiler instead,
  which is not exported via OTLP. Treat this feature as attribute-naming + correlation infrastructure for your own spans,
  not as auto-instrumentation.
- **`StartActivity` returns `null` with no listener registered** — always use `?.` (`activity?.SetTag(...)`); this is
  standard `Activity` API behavior, not Typhon-specific.
- **~150–250 ns per span when enabled** — `Activity` allocation cost if you do start spans on this source; too heavy for
  per-row hot loops, where the Profiler's ~25–50 ns/event typed records are the right tool instead.
- **WAL and Checkpoint spans are not yet designed as `Activity` spans** — `typhon.wal.flush` and `typhon.checkpoint` exist
  only as Profiler event kinds, not on this OTel-facing surface.
- Standard `Activity.Current` / `AsyncLocal<Activity>` semantics apply — spans started on a worker thread inherit
  whatever activity was current on the thread that scheduled the work; this is .NET runtime behavior, not configurable.

## 🔗 Related
- Sibling: [Telemetry Configuration & Gating](./telemetry-config-gating.md) — gates whether this `ActivitySource`'s spans (and the Profiler) are active
- Sibling: [Profiler](../Profiler/README.md) — the typed-event pipeline that actually instruments engine internals today; this `ActivitySource` doesn't yet

<!-- Deep dive: claude/overview/09-observability.md §9.3 Traces — note the "Implemented Trace Spans" table there describes Profiler typed events, not literal Activity spans on this source -->
<!-- Guide: claude/design/Observability/activitysource-startactivity-guide.md -->
<!-- Stack setup: claude/design/Observability/01-monitoring-stack-setup.md -->
