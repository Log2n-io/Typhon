# Pluggable WAL I/O Backend (IWalFileIO seam)
> Swap the WAL's disk backend for an in-memory one and run the full WAL + checkpoint pipeline with zero disk I/O.

**Status:** ✅ Implemented · **Visibility:** Internal · **Category:** [Hosting](./README.md)

## 🎯 What it solves

WAL and checkpoint are mandatory in Typhon — there is no "no-WAL" engine mode
([ADR-054](../../../claude/adr/054-remove-no-wal-mode.md)). That's correct for production, but a
fast inner loop (unit tests, benchmarks, throwaway shell sessions) still needs to avoid touching
disk on every WAL segment write. This seam lets a host replace the low-level file-IO backend the
WAL talks to, so tests exercise the exact same durability pipeline as production without a
divergent "fast but different" code path.

## ⚙️ How it works (in brief)

`AddDatabaseEngine`'s factory resolves an optional `IWalFileIO` from the DI container before
constructing the engine. If nothing is registered — the production case — `GetService` returns
`null` and the engine builds its own disk-backed backend. If a host registers one, that instance
is used instead; every other code path (WAL records, checkpoints, recovery) is unchanged.
`IWalFileIO` and its implementations are `internal` to `Typhon.Engine`: only assemblies granted
`[InternalsVisibleTo]` (Typhon's own test, benchmark, and shell assemblies) can see the interface
or register a backend — this is not a seam for arbitrary application code.

## 💻 Usage

```csharp
// Host assembly with InternalsVisibleTo on Typhon.Engine (test/benchmark/shell code)
using Typhon.Engine.Internals;

services
    .AddSingleton<IWalFileIO>(_ => new InMemoryWalFileIO())   // or AddScoped for per-scope isolation
    .AddDatabaseEngine(o =>
    {
        o.Wal = new WalWriterOptions { UseFUA = false };        // FUA is moot with no real disk
        o.Resources.CheckpointIntervalMs = int.MaxValue;        // keep the checkpoint thread idle
    });

var engine = serviceProvider.GetRequiredService<DatabaseEngine>();
// Full WAL + checkpoint pipeline runs; no segment files are created on disk.
```

| Registration lifetime | When to use |
|---|---|
| `AddScoped<IWalFileIO>` | One engine per DI scope (e.g. a test fixture that opens/reopens within one run) — each scope gets its own isolated in-memory backend. |
| `AddSingleton<IWalFileIO>` | One engine per process lifetime (benchmark harness). |

To exercise the real disk-backed path instead (crash/replay fixtures that must survive a process
restart), register `new WalFileIO()` — the same internal type the engine falls back to when
nothing is injected.

## ⚠️ Guarantees & limits

- **Production is unaffected.** No production DI setup registers `IWalFileIO`, so the engine
  always resolves `null` and builds its own disk-backed `WalFileIO`.
- **Internal seam, not public API.** `IWalFileIO`, `WalFileIO`, and `InMemoryWalFileIO` are all
  `internal`; only friend assemblies (via `[InternalsVisibleTo]`) can reference or register them.
- **Same pipeline, different medium.** Injecting `InMemoryWalFileIO` does not skip WAL records,
  checkpoints, or recovery — only where the bytes land changes. Behavior under test matches
  production storage semantics.
- **`WalManager` never disposes an injected backend.** Its lifetime is owned by whichever DI
  scope/container registered it. `InMemoryWalFileIO.Dispose` is idempotent, so double-dispose
  across recovery + steady-state is safe.
- **The supported replacement for the removed no-WAL mode.** Durability stays a real, always-on
  knob (WAL always runs); only the storage medium is pluggable.

## 🧪 Tests

- [WalIntegrationTests](../../../test/Typhon.Engine.Tests/Durability/WalIntegrationTests.cs) — registers `AddSingleton<IWalFileIO>(new WalFileIO())` to swap in the real disk-backed implementation and runs the full WAL/checkpoint/reopen/crash-recovery pipeline against it — the documented alternative to the default in-memory backend

## 🔗 Related

- Source: [`TyphonBuilderExtensions.cs`](../../../src/Typhon.Engine/Hosting/public/TyphonBuilderExtensions.cs) (`CreateDatabaseEngine`), [`IWalFileIO.cs`](../../../src/Typhon.Engine/Durability/internals/IWalFileIO.cs), [`InMemoryWalFileIO.cs`](../../../src/Typhon.Engine/Durability/internals/InMemoryWalFileIO.cs), [`WalFileIO.cs`](../../../src/Typhon.Engine/Durability/internals/WalFileIO.cs)
- Sibling: [Write-Ahead Log (WAL v2 logical records)](../Durability/wal-v2.md) — the durability pipeline this seam swaps the disk backend under, unchanged.
- Sibling: [Pluggable Storage Backend (Persistent vs Transient)](../Storage/pluggable-storage-backend/README.md) — the analogous backend-injection seam for page storage instead of WAL.

<!-- Deep dive: claude/design/Hosting/di-extensions.md — IWalFileIO section, ADR-054: Remove the no-WAL engine mode (claude/adr/054-remove-no-wal-mode.md), claude/overview/06-durability.md -->
