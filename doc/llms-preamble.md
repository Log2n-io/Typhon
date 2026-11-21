<!--
  Preamble injected verbatim into the generated /llms.txt (see scripts/gen-llms-txt.py).
  Keep it short — it primes an agent BEFORE it reads the map. HTML comments are stripped.
  The gotchas below are empirically ranked from an agent-consumption study. Full
  guidance: doc/guide/using-with-ai-coding-agents.md (mirror every change there).
  Every code idiom here is compiled by doc/guide/example (Program.cs, Systems.cs,
  AgentIdioms.cs) as part of Typhon.slnx — an API change breaks the build, not the text.
-->
Typhon is an **ECS database, not SQL**. Load the mental model before writing code: read
**Key Concepts**, then the **Guide** — and for the idioms coding agents get wrong by default,
read **Using Typhon with an AI coding agent** (https://doc.typhondb.io/latest/guides/using-with-ai-coding-agents.html.md).

The idioms that trip up first attempts:

- **Data = blittable-struct components** addressed by `EntityId` — no tables, rows, or SQL. Declare with
  `[Component("Name", 1)]` (the name + revision are **required** args). A **Versioned** component must total **≥ 8 bytes**
  (its `sizeof`, padding included); only **`public`** fields are schema fields (indexable, migrated, visible to tools).
- **Add `using Typhon.Schema.Definition;`** for the `[Component]` / `[Archetype]` attributes. Types work in any
  namespace, including the global namespace of a top-level-statements file.
- **An archetype is** `[Archetype] partial class Foo : Archetype<Foo>` with `public static readonly Comp<T> X = Register<T>();`
  (no id arg — identity is the CLR type name, or `[Archetype(Name="…")]`); it **self-registers at assembly load**, so there is no
  `RegisterArchetype` call; spawn via `tx.Spawn<Foo>(Foo.X.Set(new T{…}))`.
- **Register every component an archetype uses** — `DatabaseEngine.Open(path, o => o.Register<T>()…)`. Miss one and the archetype is
  skipped at open; the first `Spawn` into it throws "not registered with this database".
- **All mutation happens inside a transaction.** `Commit()` returning does not imply on-disk durability
  unless the unit of work uses a durable mode. Open an entity with `tx.OpenMut(id)` (an `EntityRefMut`) to write;
  `tx.Open(id)` returns a read-only `EntityRef` with no write member. For an id that may be stale, `TryOpenMut` /
  `TryOpen` return `false` instead of throwing, and `IsAlive(id)` tests existence.
- **Query with the fluent view API**, not LINQ: `tx.Query<Foo>().WhereField<T>(x => x.Field == v)` uses an `[Index]`;
  `Where<T>(x => …)` is a per-entity predicate over a **full scan** — use it to refine, not to find. The loop variable of
  `foreach (var e in query)` is a read-only `EntityRef`: read it directly, write through `tx.OpenMut(e.Id)`. A clause that can never
  match (unregistered component, a component the queried archetypes lack, `WhereField` naming a field the component lacks) throws
  at the call; `WhereField` on a field without `[Index]` throws at the terminal.
  `q.Explain()` runs the query and reports the access path each archetype took — check it before guessing why a query is slow.
- **Bootstrap** with `DatabaseEngine.Open(path, …)`, or via DI with `services.AddTyphon(…)` (it self-registers a no-op logger, so `AddLogging()` is optional).
- **Storage mode sets the ACID scope** (Versioned / SingleVersion / Transient) — chosen per component, fixed per revision.
- **Schema changes bump a revision**: change a struct's fields or its storage mode → bump its `[Component]` revision; change an
  archetype's component set → bump its `[Archetype]` revision. The engine migrates at open; without the bump the open is refused.

In a `TyphonRuntime` tick (systems), the idioms agents miss — none of these failures is reported, so get them right up front:

- **Declare every component a system touches** in `Configure`: `Writes<T>()`, `Reads<T>()`, `ReadsFresh<T>()` (after this
  tick's writers), `ReadsSnapshot<T>()` (before them; Versioned only), `SideWrites<T>()` (side transactions). The scheduler
  orders and parallelizes from these declarations alone — it never inspects the body, so an **undeclared read is a silent race**.
  `Build()` throws on two same-phase writers of a component (add `.After("Other")`) and on a plain `Reads<T>()` beside a
  same-phase writer. In a **Debug build** every read, write, spawn, destroy, `ctx.Writer(q)` and `q.Drain` inside a class-form system throws
  `InvalidAccessException` unless declared (`Typhon:Checks:DeclaredAccess`; set it `true` in a test project's `typhon.telemetry.json`, since
  under `dotnet test` the entry assembly is the optimized test host). Fix the declaration, never catch the exception. A spawn or destroy
  needs `Writes<T>()` for every component of the archetype; events need `WritesEvents(q)` / `ReadsEvents(q)`.
- **SingleVersion, Transient and spatial-archetype writes belong inside a system.** From any other thread (an HTTP handler, a
  test) while the runtime ticks, only Versioned components of an archetype that is neither spatial nor realm-keyed may be written, and
  only such an archetype's entities spawned, destroyed, enabled or disabled; anything else throws `InvalidOperationException` (EW-02). Hand such a write to the tick: `await runtime.WriteInTick((tx, tick) => …,
  DurabilityMode.GroupCommit)` (or `InTick` to return a value) — it runs at the next tick start, before that tick's systems.
- **Replication is explicit**: with the default `PushDetection.Explicit`, a system that changes a replicated entity must call
  `ctx.Subscriptions.Replicate(in cluster, slot)` (or `Replicate(in entityRefMut)`); otherwise no client is ever sent the change.
- **Never write through a span** (`ClusterRef.GetSpan`/`Get`) an `[Index]`ed SingleVersion/Transient component, a Versioned one, one with a
  `String1024` field or the realm key's — it throws; write with `cluster.Set(Foo.X, slot, value)` / `entity.Set(Foo.X, value)`, a text with
  `entity.SetText(...)`.
- **A transaction lives on one thread**: never `await` inside its `using` scope — Commit, Rollback and Dispose throw on another thread
  (EP-04, analyzer TYPHON016), and any use after `Dispose` throws. A `[Index(Sparse = true)]` field never indexes the value `0` ("no data").

**Build diagnostics are real defects — fix, don't suppress.** The `Typhon` package runs analyzers in your build (in the project that
references the package directly): TYPHON004 = a `Transaction`/enumerator never disposed → `using var`; TYPHON001/003 = a no-copy type passed by value
or copied → `ref`; TYPHON011 = `bool`/`char` in a component → `byte`/`ushort`; TYPHON010 = avoidable padding → `[StructLayout(LayoutKind.Sequential,
Pack = 4)]`; TYPHON012 = `Set` on an `in`/readonly `EntityRefMut` → take it by `ref`; TYPHON009 = spatial component written through `GetSpan` →
`WriteSpatial`; TYPHON013 = component over 112 bytes passed to `Spawn` → split it; TYPHON017 = `Where(Func)` on an `[Index]` field → `WhereField`. All of them, with fixes:
https://doc.typhondb.io/latest/guides/diagnostics.html.

The whole conceptual core is also available as one file at https://doc.typhondb.io/llms-full.txt.
