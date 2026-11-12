---
uid: guide-ai-coding-agents
title: Using Typhon with an AI coding agent
description: How to ground a coding agent (Claude, Cursor, …) so it writes idiomatic, compiling Typhon code against the NuGet package on the first try.
---

# Using Typhon with an AI coding agent

Typhon is new — it is in **no model's training data**. A coding agent asked to "use the Typhon
database" will, by default, pattern-match to SQL or a generic ORM and produce code that does not
compile or does not behave the way you expect. This page shows how to ground the agent so it writes
idiomatic Typhon on the first try, and lists the specific idioms agents get wrong.

## 1. Point the agent at the docs

Typhon publishes machine-readable documentation for exactly this purpose:

- **`https://doc.typhondb.io/llms.txt`** — a map of the whole doc set. Most agent tools probe this
  automatically; if yours doesn't, paste the URL into your prompt.
- **Every page has a clean-markdown twin** at `<url>.md` (e.g. `.../key-concepts/transaction.html.md`) —
  no navigation chrome, ideal for a context window.
- **`https://doc.typhondb.io/llms-full.txt`** — the Guide, Key Concepts, and In-Depth Overview
  concatenated into one file, for when you want to load the whole mental model at once.

The XML documentation shipped inside the NuGet package grounds *API signatures* on disk; the docs above
add the *conceptual layer* (how the pieces fit) that signatures alone don't convey.

## 2. The one-paragraph mental model

Typhon is an **ECS (Entity-Component-System) database, not a relational one**. You don't define tables
and rows; you define **components** — small blittable structs — and attach them to **entities**
identified by an `EntityId`. A **transaction** under **MVCC snapshot isolation** is how you read and
change data. You query with a fluent **view** API, not SQL or LINQ-to-SQL.

## 3. Idioms agents get wrong (give these to your agent)

These are ranked by how often a naive agent trips on them. Most are hard requirements of the current
API; the rest are strong recommendations.

| # | Do | Don't |
|---|----|-------|
| 1 | Declare `[Component("Name", rev)]` / `[Archetype]` types in **any namespace** — including the global namespace of a top-level-statements file | Assume they *must* be wrapped in a named `namespace` — the generator supports both; a named one is just tidier for a real project |
| 2 | Make **Versioned** components total **≥ 8 bytes** (`sizeof(T)`, padding included; SingleVersion/Transient have no minimum) and put every value you index, migrate or inspect in a **`public`** field | Use a single 4-byte field — the open throws; or keep data in `private` fields, which the schema does not see (private *padding* is fine) |
| 3 | Query with the fluent builder: `tx.Query<TArch>().WhereField<TComp>(x => x.Field == v)` on an `[Index]`ed field, then refine with `Where<TComp>(x => …)` | Write LINQ-to-SQL style `tx.Query(x => x.Score >= 50)`; or lead with `Where<T>` — it is a per-entity predicate over a **full scan** |
| 4 | Read the `foreach` variable directly (it is a read-only `EntityRef`); **write through `tx.OpenMut(e.Id)`** | Try to `Set` through the enumerated reference — it has no write member |
| 5 | Spawn with `tx.Spawn<TArch>(TArch.Handle.Set(new TComp{…}))` | Pass field values or a constructed instance to `Spawn` |
| 6 | Bootstrap with `DatabaseEngine.Open(path, …)`, or DI `services.AddTyphon(…)` (`AddLogging()` is optional — it's self-registered) | `new DatabaseEngine(...)` (no public constructor) |
| 7 | `using Typhon.Schema.Definition;` for the attributes | Rely on `using Typhon.Engine;` alone — `Archetype<T>` (base class) and `[Archetype]` (attribute) share a name and collide |
| 8 | Do all writes **inside a transaction** and `Commit()` | Assume `Commit()` implies durability — that depends on the unit-of-work's durability mode |
| 9 | Pick a **storage mode per component** (`Versioned` = full ACID, `SingleVersion`, `Transient`) | Treat storage/ACID as a global switch; or list `Committed` as a fourth mode — there are exactly three `StorageMode` values; `CommitDiscipline.Commit` is a separate per-transaction axis on `SingleVersion` |
| 10 | **Register every component** an archetype uses (`o.Register<T>()`) | Leave one out — the archetype is skipped at open and the first `Spawn` into it throws "not registered with this database" |
| 11 | **Bump the revision** with the change: fields or storage mode → `[Component]` revision; component set → `[Archetype]` revision | Change a struct and reopen as-is — the open is refused (a storage-mode change too) |

### Inside a `TyphonRuntime` tick

Systems add four idioms whose failure **nothing reports** — get them right up front:

| # | Do | Don't |
|---|----|-------|
| 12 | Declare every component a system touches in `Configure`: `Writes<T>()`, `Reads<T>()`, `ReadsFresh<T>()`, `ReadsSnapshot<T>()` (Versioned only), `SideWrites<T>()`; order two same-phase writers with `.After("Other")` | Leave a read undeclared — the scheduler never inspects the body, so the system races the writer silently. `Build()` only catches declared conflicts; `Typhon:Checks:Enabled` + `Typhon:Checks:DeclaredAccess` assert undeclared *writes* in development |
| 13 | Write `SingleVersion`, `Transient` and spatial-archetype components **inside a system** | Write them from another thread (a request handler, a timer) — only Versioned components of a non-spatial archetype are safe off-tick; the rest races the tick fence with no error |
| 14 | Call `ctx.Subscriptions.Replicate(in cluster, slot)` (or `Replicate(in entityRefMut)`) after changing a replicated entity | Expect clients to see a plain write — under the default `PushDetection.Explicit` an unmarked change is never sent |
| 15 | Write an `[Index]`ed `SingleVersion`/`Transient` component with `cluster.Set(…)` / `entity.Set(…)` | Write it through `GetSpan`/`Get` — that throws; and don't store `0` in a `[Index(Sparse = true)]` field expecting to find it — `0` means "no data" and is never indexed |
| 16 | Write a `String1024` text field with `EntityRefMut.SetText` / `EntityRefMut.ClearText`; write the rest of a `String1024`-containing component with `ClusterRef.Set` (preserves the out-of-line handle) | Write it through `GetSpan`/`Get` — that throws in every build; a store through the span could drop or corrupt the out-of-line text handle |

Every idiom in this section is compiled in the guide's example project (`doc/guide/example/AgentIdioms.cs`), so it tracks the API.

## 4. A minimal program that compiles

This is the smallest correct end-to-end example — declare a component and archetype, spawn, mutate,
and read back. Split into two files so the top-level `Program.cs` stays clean.

**`Model.cs`**

```csharp
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace Shard;   // (1) a named namespace is tidier for a real project — the global namespace also works

// (2) a component is a blittable struct with public fields; a Versioned one is >= 8 bytes
[Component("Shard.CharacterData", 1, StorageMode = StorageMode.Versioned)]  // (9) per-component storage mode
public struct CharacterData
{
    public long Credits;   // widest field first: no padding (the TYPHON010 analyzer flags avoidable padding)
    public int Faction;
    public int Level;
}

// an archetype is the entity's shape: partial class : Archetype<TSelf>, one Comp<T> handle per component
// no id arg — identity is the CLR type name (or [Archetype(Name="…")]); it self-registers at assembly load
[Archetype]
public sealed partial class Character : Archetype<Character>
{
    public static readonly Comp<CharacterData> Data = Register<CharacterData>();
}
```

**`Program.cs`**

```csharp
using Typhon.Engine;
using Shard;

using var dbe = DatabaseEngine.Open("shard.typhon", o => o   // (6) bootstrap
    .Register<CharacterData>());   // components register explicitly; archetypes self-register at assembly load — no RegisterArchetype call

// (5) spawn inside a transaction; (8) Commit
EntityId id;
using (var tx = dbe.CreateQuickTransaction())
{
    id = tx.Spawn<Character>(Character.Data.Set(new CharacterData { Faction = 1, Credits = 0 }));
    tx.Commit();
}

// mutate: OpenMut for a writable handle
using (var tx = dbe.CreateQuickTransaction())
{
    var entity = tx.OpenMut(id);
    var data = entity.Read(Character.Data);
    data.Credits += 10;
    entity.Set(Character.Data, data);
    tx.Commit();
}

// (3) query to FIND, (4) read the enumerated EntityRef directly
using (var tx = dbe.CreateQuickTransaction())
{
    foreach (var e in tx.Query<Character>())
    {
        var c = e.Read(Character.Data);
        Console.WriteLine($"Faction={c.Faction} Credits={c.Credits}");   // Faction=1 Credits=10
    }
}
```

Install the package first: `dotnet add package Typhon --prerelease` (Typhon is in alpha, so the
prerelease flag is required).

## 5. A primer to paste into your agent

If your agent isn't reading `llms.txt` automatically, paste this block into your prompt:

```text
Typhon is an ECS database (not SQL) from the `Typhon` NuGet package. Rules:
- Model data as `[Component("Name", 1)]` blittable structs (public fields; >= 8 bytes when Versioned) — the name + revision
  are REQUIRED attribute args. Add `using Typhon.Schema.Definition;`. Changing fields or storage mode needs a revision bump. (Any namespace works, including the
  global namespace of a top-level-statements file.)
- An archetype is `[Archetype] partial class Foo : Archetype<Foo>` (no id arg — identity is the type name, or
  `[Archetype(Name="…")]`) with `public static readonly Comp<T> X = Register<T>();`. Archetypes self-register at assembly load.
- Open the engine with DatabaseEngine.Open(path, o => o.Register<T>()) — register EVERY component an archetype uses; archetypes need no registration call.
- All changes happen in a transaction: `using var tx = dbe.CreateQuickTransaction(); … tx.Commit();`.
  Spawn: `tx.Spawn<Foo>(Foo.X.Set(new T{…}))`. Mutate: `var e = tx.OpenMut(id); var x = e.Read(Foo.X); x.Field = …; e.Set(Foo.X, x);` — reads copy, writes are `Set`; there is no `Write`.
- Query with the fluent view API — NOT LINQ: `tx.Query<Foo>().WhereField<T>(x => x.Field == v)` uses an [Index];
  `Where<T>(x => …)` is a full scan. `foreach (var e in query)` yields a read-only EntityRef: `e.Read(Foo.X)`; write via `tx.OpenMut(e.Id)`.
- In systems: declare every component touched (Writes/Reads/ReadsFresh/ReadsSnapshot/SideWrites) — undeclared reads race silently;
  write SingleVersion/Transient/spatial data only inside systems; call ctx.Subscriptions.Replicate(in cluster, slot) after a replicated change.
- Full docs: https://doc.typhondb.io/llms.txt
```

## See also

- [Getting Started](getting-started.md) — the guided first app.
- [Key Concepts](https://doc.typhondb.io/latest/key-concepts/) — the conceptual reference.
