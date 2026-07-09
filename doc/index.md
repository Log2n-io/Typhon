# Typhon Documentation

**Real-time, low-latency ACID database engine** with an ECS data model and microsecond-level performance — embedded, in-process, no server.

> New here? Start with **[Getting Started](guide/getting-started.md)** — a working Typhon app in about five minutes.

## Explore the docs

| Section | What's inside |
|---|---|
| **[Overview](in-depth-overview/README.md)** | How the engine is built — 14 chapters spanning storage, MVCC, indexing, durability, runtime, and errors. |
| **[Guides](guide/README.md)** | Task-oriented how-to: model your data, transact, query, run systems — plus the [Feature Catalog](feature-set/README.md) (one page per feature). |
| **[Tools](tools/index.md)** | The **Workbench** GUI (`typhon ui`) and the **`typhon` CLI** — Typhon is a complete toolchain, not just a library. |
| **[API Reference](api/index.md)** | Generated reference for the public API (engine + Profiler + Protocol + Schema.Definition). |
| **[Demos](demos/index.md)** | **AntHill** — a persistent world larger than RAM, proving Typhon's beyond-RAM ECS thesis. |
| **[Benchmarks](../benchmark/reports/latest.md)** | The latest regression report, run on CI reference hardware. |

## Who is Typhon for?

| Workload | Why it fits |
|---|---|
| **Game servers** | High-frequency entity updates, ECS-native model, transactional safety |
| **Simulations** | MVCC snapshot reads during world ticks — readers never block writers |
| **Real-time systems** | Microsecond reads, predictable latency, no GC pressure |
| **Embedded apps** | No server process, runs in-process, single-file storage |

---

Product site: **[typhondb.io](https://typhondb.io)** · Source: **[github.com/Log2n-io/Typhon](https://github.com/Log2n-io/Typhon)**
