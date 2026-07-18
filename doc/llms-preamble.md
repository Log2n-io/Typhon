<!--
  Preamble injected verbatim into the generated /llms.txt (see scripts/gen-llms-txt.py).
  Keep it short — it primes an agent BEFORE it reads the map. HTML comments are stripped.
  The gotchas below are empirically ranked from an agent-consumption study (#498). Full
  guidance: doc/guide/using-with-ai-coding-agents.md.
-->
Typhon is an **ECS database, not SQL**. Load the mental model before writing code: read
**Key Concepts**, then the **Guide** — and for the idioms coding agents get wrong by default,
read **Using Typhon with an AI coding agent** (https://doc.typhondb.io/latest/guides/using-with-ai-coding-agents.html).

The idioms that trip up first attempts:

- **Data = blittable-struct components** addressed by `EntityId` — no tables, rows, or SQL. A component
  must be **≥ 8 bytes** and its fields **`public`** (private fields are invisible to the schema).
- **Component/archetype types must be declared inside a `namespace`** — not the global namespace of a
  top-level-statements file. Add `using Typhon.Schema.Definition;` for the `[Component]` / `[Archetype]` attributes.
- **An archetype is** `[Archetype] partial class Foo : Archetype<Foo>` with `public static readonly Comp<T> X = Register<T>();`;
  spawn via `tx.Spawn<Foo>(Foo.X.Set(new T{…}))`.
- **All mutation happens inside a transaction.** `Commit()` returning does not imply on-disk durability
  unless the unit of work uses a durable mode. Open an entity with `tx.OpenMut(id)` to write.
- **Query with the fluent view API** — `tx.Query<Foo>().Where<T>(x => …).Count()` — **not** LINQ-to-SQL; then
  **read/mutate each entity via `tx.Open(id)`**, not the query-enumerated reference.
- **Bootstrap** with `DatabaseEngine.Open(path, …)`, or via DI with `services.AddTyphon(…)` **plus `AddLogging()`**.
- **Storage mode sets the ACID scope** (Versioned / SingleVersion / Transient / Committed) — chosen per component.

Every page below also exists as clean markdown at `<url>.md`; the whole conceptual core is at
https://doc.typhondb.io/latest/llms-full.txt.
