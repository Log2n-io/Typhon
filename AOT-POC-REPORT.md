# Native AOT POC — `Typhon.Engine`

**Date:** 2026-07-31
**Branch:** `poc/aot` (worktree `.claude/worktrees/aot-poc`)
**Tracking issue:** [#409 — Native AOT readiness for Typhon.Engine](https://github.com/Log2n-io/Typhon/issues/409)
**Prior art:** `claude/research/AotReadiness.md` (2026-06-27 assessment)
**Status:** Complete. Engine is AOT-clean and enforced; a native binary runs the full stack and passes every correctness assertion.

---

## TL;DR

| Question | Answer |
|---|---|
| Is `Typhon.Engine` Native-AOT compatible? | **Yes.** 22 IL warnings → **0**, with `<IsAotCompatible>true</IsAotCompatible>` now enforced (warnings are errors). |
| Does a native binary actually work? | **Yes.** 21 MB self-contained `.exe`, 17/17 correctness assertions pass across the full stack. |
| Are the AOT-hostile dependencies gone? | **Yes.** TraceEvent + `Diagnostics.NETCore.Client` moved out of the engine into the optional `Typhon.Diagnostics` assembly. |
| Did AOT-proofing cost JIT performance? | **No — measured.** Median tick p50 delta **0.0 %**, fully overlapping ranges over 5 interleaved runs per build. |
| Does *running* AOT cost performance? | **Yes — ~12–13 % steady-state throughput.** Bought with 57 % faster startup, 7 % lower RSS and no runtime dependency. |
| Bugs found? | **One AOT-only crash** (`MetadataToken` on the mainline `TyphonRuntime.Create` path) that no analyzer could see. Fixed. |

The engine test suite is **4076 passed / 0 failed** after all changes.

---

## 1. Scope as agreed

- **Hard bar:** `Typhon.Engine` + the three bundled siblings (`Schema.Definition`, `Protocol`, `Profiler`) — i.e. everything inside the published `Typhon` NuGet package — analyzer-clean *and* free of AOT-hostile dependencies.
- **Best-effort:** the profiler. Accepted floor was "AOT works with profiling disabled". **Outcome: better than the floor** — profiling works under AOT; only the two OS-level tracing providers (ETW scheduling, EventPipe CPU sampling) are unavailable, and they now degrade with an actionable message instead of dragging their packages into every build.
- **Demo:** the canonical consumer shape (separate schema assembly + consumer source generator + engine), full-stack, with JIT-vs-AOT deltas.
- **Out of scope:** `Typhon.Workbench` (runtime `*.schema.dll` loading into collectible ALCs is definitionally non-AOT), `Typhon.Client`, `Typhon.Shell`.

---

## 2. What was wrong, and what changed

### 2.1 Starting state

`dotnet build src/Typhon.Engine -c Release -t:Rebuild -p:IsAotCompatible=true` → **22 unique IL warnings, 0 errors**:

| Class | Count | Meaning |
|---|---:|---|
| IL2xxx (trim) | 13 | Reflection the trimmer cannot follow |
| IL3050 (AOT) | 5 | Runtime code generation |
| IL3000 / IL3002 (single-file) | 4 | `Assembly.Location` / `Module.FullyQualifiedName` |

### 2.2 The fixes, by kind

**a) Genuine removals of reflection (net improvements, not annotations).**

| Site | Before | After |
|---|---|---|
| `DagScheduler.ValidateContextBindings` | Base-type walk + `GetProperty("Context", NonPublic)` + `GetValue` per registered system | `ChunkedCallbackSystem.HasUnboundContext(out Type)` virtual. Reflection-free **and cheaper** — the engine's only private-member reflection probe is gone. |
| `PagedMMF` advisory lock file | `JsonSerializer.Serialize(anonymousType)` — both `RequiresUnreferencedCode` *and* `RequiresDynamicCode` for a three-field file | `Utf8JsonWriter`. Reflection-free, correct escaping, fewer allocations. Format unchanged — the existing `DatabaseFileLockingTests` pass untouched. |
| `TelemetryConfig` config discovery | `Assembly.Location` (returns `""` under single-file/AOT, silently disabling the fallback) | `AppContext.BaseDirectory`. Works in every host shape — a **behaviour fix**, not a workaround. |
| `TyphonBuilderExtensions` storage factory | `Activator.CreateInstance(typeof(TS), …)` fallback | Deleted. It was unreachable dead code *and* passed constructor arguments matching neither real `PagedMMF` constructor — it could never have worked. Replaced with an explicit `NotSupportedException`. |

**b) Making types statically knowable (the only fix for `IL2059`).**

`RuntimeHelpers.RunClassConstructor(archetypeType.TypeHandle)` in `ArchetypeRegistry.EnsureFinalized` was the trickiest item. Per the [`DynamicallyAccessedMemberTypes` documentation](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.codeanalysis.dynamicallyaccessedmembertypes), **no DAM value preserves a static constructor** — so annotation is impossible and the type must become statically known.

Resolution:
- Added `EnsureFinalized<TArchetype>()` / `DatabaseEngine.RegisterArchetype<TArchetype>()`, where `typeof(T)` is known per instantiation.
- **Changed `ArchetypeAccessorGenerator`** so the generated `[ModuleInitializer]` barrier emits `RegisterArchetype<Foo>()` instead of `RegisterArchetype(typeof(Foo))`. Every consumer's registration path is now the AOT-safe one by construction.
- The `Type`-based overload remains for dynamic schema-loading hosts, annotated `[RequiresUnreferencedCode]`.

**c) Feature-gating dynamic code.** `RuntimeFeature.IsDynamicCodeSupported` is a recognised [`[FeatureGuard(typeof(RequiresDynamicCodeAttribute))]`](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.codeanalysis.featureguardattribute), so ILC both silences IL3050 *and* drops the guarded branch from native builds, while the JIT folds it to a constant. Applied to four sites: the reflective `ComponentCollection<T>` factory fallback, `RegisterComponentByType`, the collectible-ALC metadata reset, and the `ExpressionParser` constant-eval fallback. Each now throws an **actionable** message under AOT rather than a `MissingRuntimeArtifact` from deep inside reflection.

**d) Annotations and justified suppressions.** DAM propagation through `IOptions<TO>`, `RegisterComponentFromAccessor<T>`, `TyphonOptions.Register<T>()`, and the schema reflection path. Four `[UnconditionalSuppressMessage]`s, each with a written justification of *why* the analyzer's concern cannot materialise. One is worth flagging: **DAM cannot be applied to a `Type[]` field** (IL2097 — only `Type` and `string` are permitted), which is why the cascade-graph scan carries the suppression at the reflection site rather than on `ArchetypeMetadata._slotToComponentType`.

### 2.3 The dependency purge (option 2)

The 2026-06 assessment flagged the real trap and it is worth restating:

> The two hostile deps emit **zero** IL warnings — because they aren't `Requires*`-annotated, the analyzer is blind to them. A green analyzer build does **not** prove AOT safety.

Three files (1,068 lines) moved to a new **`src/Typhon.Diagnostics`** assembly, which is now the only project in the solution referencing `Microsoft.Diagnostics.Tracing.TraceEvent` or `Microsoft.Diagnostics.NETCore.Client`:

- `EtwSchedulingPump` — Windows kernel context-switch tracing
- `CpuSamplerSession` — in-process EventPipe CPU sampling
- `CpuSampleParser` — `.nettrace` transcode + symbolication

The engine declares `ISchedulingPump` / `ICpuSamplerSession` / `TyphonDiagnosticsHooks`; an application that wants these features references `Typhon.Diagnostics` and calls `TyphonDiagnostics.Enable()`. Unregistered providers degrade exactly as they already did when the OS or privileges said no — a stderr note, profiling continues. **Cost: nil** — the hooks are read once per profiling-session start, never on an emit path.

### 2.4 Locked in

`<IsAotCompatible>true</IsAotCompatible>` is now set on `Typhon.Engine`, `Typhon.Schema.Definition`, `Typhon.Protocol` and `Typhon.Profiler`. Combined with the pre-existing `TreatWarningsAsErrors`, **any new un-followable reflection breaks the build**. The 22→0 result cannot silently rot.

---

## 3. The bug the analyzers could not find

Running the native binary crashed on the **mainline** `TyphonRuntime.Create` path:

```
Unhandled exception. System.InvalidOperationException: There is no metadata token available for the given member.
   at System.Reflection.Runtime.MethodInfos.NativeFormat.NativeFormatMethodCommon.get_MetadataToken()
   at Typhon.Engine.Internals.SystemSourceResolver.ResolveMethod(MethodInfo)
   at Typhon.Engine.DagBuilder.AddCallbackSystemInternal(...)
   at Typhon.Engine.RuntimeSchedule.Build(...)
   at Typhon.Engine.TyphonRuntime.Create(...)
```

`MethodInfo.MetadataToken` carries no `Requires*` or DAM annotation, so **every analyzer was silent** and the publish was clean. Native images have no IL metadata tokens; the property throws.

Two things make this the most valuable single result of the POC:

1. **It was a hard crash on a mainline path**, not a degraded profiler feature. Any AOT-published Typhon application that starts a runtime would have died at startup.
2. **I had reasoned it would degrade gracefully** — that claim is written into the `IL3000` suppression justification I added earlier the same day. It was wrong. Static analysis plus careful reading was not sufficient; only executing the published binary found it.

Fixed with a narrow `catch (InvalidOperationException)` restoring the documented "no Source row" fallback. Caught narrowly so a genuine PDB bug still surfaces.

---

## 4. The demo

**`samples/Typhon.Aot.Demo`** — one program, published two ways, shaped like a real consumer: it references the canonical SWG sample schema as a compiled assembly plus the consumer source generator, and it **links the guide's real tick systems** (`doc/guide/example/Systems.cs`) rather than copying them, so engine API drift breaks the demo the same way it would break a user.

Every phase asserts its own result, so "it ran" and "it produced the right answers" are the same check.

| # | Phase | What it proves |
|---|---|---|
| 1 | Open | Generated `[ModuleInitializer]` barrier registered every archetype/component with no reflection |
| 2 | Spawn 20 001 + commit | Cluster storage, WAL, mixed storage modes (Versioned / SingleVersion / Transient) |
| 3 | Transactions | Durable commit, rollback leaves no trace, MVCC snapshot held across a concurrent commit |
| 4 | Queries | Indexed (`WhereField`), scan (`Where`), spatial (`WhereNearby`) |
| 5 | Pending-spawn readback | Read-your-own-writes over uncommitted spawns — the `Expression`-predicate path that historically needed a compiled delegate |
| 6 | Tick loop | Six-system DAG, two phases, parallel dispatch, per-tick transactions |
| 7 | Destroy | Cascade-aware entity destruction |
| 8 | Reopen | Versioned + SingleVersion durable; Transient reset — each storage mode came back as designed |

**Result: 17/17 assertions pass under Native AOT**, identical answers to the JIT build (6 666 Imperials, 20 000 wounded, 638 near centre, 33 pending matches, 13 341 survivors).

Native binary: **20.96 MB**, self-contained, no .NET runtime required.

### ILC publish warnings

**20 warnings, 100 % from MemoryPack, 0 from any Typhon assembly.**

They come from MemoryPack's *dynamic formatter provider* (`MakeGenericType` / `Activator` for types with no source-generated formatter), reachable through `SubscriptionOutputPhase` → `Typhon.Protocol`. All 7 Protocol wire types are `[MemoryPackable]`, so the **typed** path they actually use is source-generated and AOT-safe; the warnings are on a fallback those types never take, which is why the demo runs clean. Worth noting as a follow-up: this contradicts the 2026-06 dependency matrix, which marked MemoryPack an unqualified ✅.

---

## 5. Measurements

Hardware: AMD Ryzen 7950X. All figures median of 3 runs (regression check: 5 interleaved runs). `Release`, `win-x64`.

### 5.1 Did AOT-proofing cost JIT performance? — No

Unmodified engine (`origin/main`, built in a throwaway baseline worktree) vs the #409 engine, **both JIT**, 3000 ticks, 5 interleaved runs each:

| | tick p50 samples (ms) | median |
|---|---|---|
| base | 0.670 · 0.691 · 0.699 · 0.709 · 0.756 | **0.699** |
| #409 | 0.664 · 0.693 · 0.699 · 0.716 · 0.719 | **0.699** |

**Median delta 0.0 %**, ranges fully overlapping. The guarantee I committed to holds. This is the expected outcome by construction — every changed site is cold (registration, file open, engine dispose, diagnostics setup, query *construction*); none is in a commit, read, index-walk or tick loop.

*(An earlier non-interleaved run showed +2.1 %; interleaving the two builds removed it, confirming it was run-ordering drift rather than a code effect. Non-interleaved measurement of a ~2 % effect is not trustworthy on this machine.)*

### 5.2 What does running AOT cost? — ~12–13 % steady-state throughput

This is the number that matters, and it needs **two** regimes to be honest:

**Short run (300 ticks, ~5 s) — warm-up dominated:**

| metric | JIT | AOT | AOT vs JIT |
|---|---:|---:|---:|
| process wall clock (ms) | 6423.3 | 5720.9 | **−10.9 %** |
| runtime startup, pre-`Main` (ms) | 130.0 | 56.4 | **−56.6 %** |
| engine open (ms) | 412.1 | 254.7 | **−38.2 %** |
| spawn 20 k + commit (ms) | 249.1 | 49.8 | **−80.0 %** |
| reopen (ms) | 205.3 | 123.4 | **−39.9 %** |
| tick p99 (ms) | 27.2 | 1.3 | **−95.1 %** |
| peak working set (MB) | 563.2 | 524.1 | **−6.9 %** |

**Long run (3000 ticks, ~50 s) — steady state, tier-1 + dynamic PGO fully engaged:**

| metric | JIT | AOT | AOT vs JIT |
|---|---:|---:|---:|
| tick p50 (ms) | 0.681 | 0.779 | **+14.4 %** (worse) |
| tick p99 (ms) | 1.190 | 1.262 | +6.0 % (worse) |
| throughput (M entity-updates/s) | 117.5 | 102.7 | **−12.6 %** (worse) |

JIT p50 spread 0.671–0.681 vs AOT 0.756–0.804 — **non-overlapping**, so this is a real effect, not noise.

**Reading it.** A short-run measurement makes AOT look uniformly better; that impression is an artifact of JIT tier-0 warm-up, and quoting it alone would be misleading. Once tiered compilation and dynamic PGO have fully engaged, **the JIT produces materially better code for Typhon's hot loops** — as predicted, because AOT compiles without profile feedback and therefore without PGO-driven devirtualization and hot/cold layout. The 80 % spawn win and 95 % p99 win in the short run are almost entirely "JIT hasn't finished compiling yet".

**The trade, stated plainly:** Native AOT costs roughly **an eighth of steady-state throughput** and buys **57 % faster startup, ~40 % faster engine open, 7 % lower RSS, single-file deployment with no runtime dependency, and no JIT warm-up cliff** (p99 28 ms → 1.3 ms during the first seconds).

That maps cleanly onto workloads: a long-lived server that runs for days should stay on the JIT; a CLI, a short-lived job, a serverless function, a container that scales from zero, or anything where a 28 ms warm-up p99 is unacceptable, wants AOT.

---

## 6. Files changed

**New:**
- `src/Typhon.Diagnostics/` — extracted ETW/EventPipe providers (csproj, 3 moved files, `TyphonDiagnostics.Enable()`, AssemblyInfo)
- `src/Typhon.Engine/Profiler/public/TyphonDiagnosticsHooks.cs`, `Profiler/internals/DiagnosticsProviderHooks.cs` — the seam
- `samples/Typhon.Aot.Demo/` — the demo
- `scripts/aot-compare.ps1` — reproducible JIT-vs-AOT harness
- `test/Typhon.Engine.Tests/Data/Query/ExpressionParserAotTests.cs` — 6 tests pinning the predicate shapes that must never reach the (AOT-gated) compile fallback

**Modified (engine):** `ArchetypeRegistry`, `ArchetypeMetadata`, `Archetype`, `DatabaseEngine`, `DatabaseDefinitions`, `DatabaseSchema`, `TyphonBuilderExtensions`, `TyphonOptions`, `TelemetryConfig`, `SystemSourceResolver`, `ProfilerLauncher`, `TyphonProfiler`, `ExpressionParser`, `ChunkedCallbackSystem`, `DagScheduler`, `PagedMMF`, `AssemblyInfo`, `Typhon.Engine.csproj`
**Modified (other):** `ArchetypeAccessorGenerator` (emits generic registration), three sibling csprojs (`IsAotCompatible`), `Typhon.slnx`, test csproj

---

## 7. Verification

| Check | Result |
|---|---|
| `dotnet build Typhon.slnx -c Debug` | Clean — 0 errors, 0 IL warnings |
| Engine analyzer sweep (`IsAotCompatible=true`, `-t:Rebuild`) | **0 warnings** (was 22) |
| Siblings (Schema.Definition / Protocol / Profiler) | **0 warnings** each |
| `dotnet publish -r win-x64` (PublishAot) | Succeeds — 20.96 MB native `.exe`; 20 ILC warnings, all MemoryPack, **0 Typhon** |
| Native binary full-stack run | **17/17 assertions pass** |
| JIT binary full-stack run | 17/17 assertions pass, identical answers |
| Engine test suite | **4076 passed / 0 failed** / 51 skipped |
| JIT perf regression | **0.0 % median**, overlapping ranges |

### Reproduce

```bash
# analyzer sweep (no native toolchain needed) — must print nothing
dotnet build src/Typhon.Engine/Typhon.Engine.csproj -c Release -t:Rebuild -p:SuppressTrimAnalysisWarnings=false

# native publish + run (needs VS C++ tools; add the VS Installer dir to PATH for vswhere.exe)
dotnet publish -c Release -r win-x64 samples/Typhon.Aot.Demo/Typhon.Aot.Demo.csproj
./samples/Typhon.Aot.Demo/bin/Release/net10.0/win-x64/publish/Typhon.Aot.Demo.exe

# JIT vs AOT comparison (use -Ticks 3000 for the steady-state numbers)
./scripts/aot-compare.ps1 -Ticks 3000
```

> `PublishAot` must stay **csproj-local**. Passing `-p:PublishAot=true` globally leaks into the netstandard2.0 analyzer/generator references and fails with `NETSDK1207`.
>
> `PublishAot=true` also stamps `IsDynamicCodeSupported=false` into `runtimeconfig.json` for plain `dotnet build` output. A "JIT" baseline built from this project without `-p:PublishAot=false` therefore runs the AOT branches under the JIT — useful as a cross-check, invalid as a baseline. `aot-compare.ps1` handles this.

---

## 8. Follow-ups (not done here)

1. **MemoryPack's dynamic provider** (20 ILC warnings). Not exercised by the demo and not a blocker, but it contradicts the 2026-06 dependency matrix and should be either confirmed unreachable for the subscription path or pinned to source-generated formatters. Worth a `Typhon.Client` + subscriptions AOT test before anyone claims the *wire* path is AOT-clean.
2. **CI enforcement.** The analyzers are locked in per-project, but nothing yet runs `dotnet publish -r win-x64` on the demo in CI. A publish-and-run smoke job is what would have caught the `MetadataToken` crash automatically — the per-project analyzers provably could not.
3. **`Typhon.Client` / `Typhon.Shell`** were out of scope. A native `typhon` CLI is attractive (instant startup is exactly the AOT sweet spot) but needs the Workbench split out of the CLI first.
4. **Cross-platform.** Only `win-x64` was published. `linux-x64` should be verified before any AOT claim reaches documentation.
5. **The perf trade should reach the docs.** "Typhon supports Native AOT" without "~12 % steady-state throughput, 57 % faster startup" would set the wrong expectation for a server workload.

---

## 9. Honest caveats

- Throughput deltas are from **one workload on one machine**. The 12–13 % figure is specific to this six-system tick DAG over 20 k entities on a 7950X; a different mix (more branchy, more virtual dispatch) would likely show a *larger* PGO advantage for the JIT, and a flatter, more vectorised workload a smaller one.
- The `IL2059` suppression on `EnsureFinalized<TArchetype>` rests on the argument that ILC compiles concrete instantiations and never strips a kept type's static constructor. That is sound, and the native binary registering 6 components + 1 archetype correctly is empirical support — but it is a suppression, not a proof.
- `catch (InvalidOperationException)` around `MetadataToken` restores the documented degradation, but the profiler's source-attribution feature is simply **unavailable** under AOT. Nothing recovers IL sequence points from a native image; that is a property of the platform, not a gap to close.
- The engine is AOT-clean; **an application is not automatically so**. Consumer code, and any package it adds, must carry its own weight.
