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
- **`https://doc.typhondb.io/latest/llms-full.txt`** — the Guide, Key Concepts, and In-Depth Overview
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
| 2 | Make components **≥ 8 bytes** with **`public`** fields | Use a single 4-byte field, or `private` padding — the schema sizes on public fields, not `sizeof(T)` |
| 3 | Query with the fluent builder `tx.Query<TArch>().Where<TComp>(x => …).Count()` | Write LINQ-to-SQL style `tx.Query(x => x.Score >= 50)` — the filtered component is a separate generic argument |
| 4 | **Read/mutate each entity via `tx.Open(id)`** (query to *find*, open to *read*) | Read directly off the reference the query enumerator yields |
| 5 | Spawn with `tx.Spawn<TArch>(TArch.Handle.Set(new TComp{…}))` | Pass field values or a constructed instance to `Spawn` |
| 6 | Bootstrap with `DatabaseEngine.Open(path, …)`, or DI `services.AddTyphon(…)` (`AddLogging()` is optional — it's self-registered) | `new DatabaseEngine(...)` (no public constructor) |
| 7 | `using Typhon.Schema.Definition;` for the attributes | Rely on `using Typhon.Engine;` alone — `Archetype<T>` (base class) and `[Archetype]` (attribute) share a name and collide |
| 8 | Do all writes **inside a transaction** and `Commit()` | Assume `Commit()` implies durability — that depends on the unit-of-work's durability mode |
| 9 | Pick a **storage mode per component** (`Versioned` = full ACID, `SingleVersion`, `Transient`, `Committed`) | Treat storage/ACID as a global switch |

## 4. A minimal program that compiles

This is the smallest correct end-to-end example — declare a component and archetype, spawn, mutate,
and read back. Split into two files so the top-level `Program.cs` stays clean.

**`Model.cs`**

```csharp
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace Swg;   // (1) a named namespace is tidier for a real project — the global namespace also works

// (2) a component is a blittable struct, >= 8 bytes, public fields
[Component("Swg.HarvesterData", 1, StorageMode = StorageMode.Versioned)]  // (9) per-component storage mode
public struct HarvesterData
{
    public int X;
    public int Cargo;
}

// an archetype is the entity's shape: partial class : Archetype<TSelf>, one Comp<T> handle per component
// no id arg — identity is the CLR type name (or [Archetype(Name="…")]); it self-registers at assembly load
[Archetype]
public sealed partial class Harvester : Archetype<Harvester>
{
    public static readonly Comp<HarvesterData> Data = Register<HarvesterData>();
}
```

**`Program.cs`**

```csharp
using Typhon.Engine;
using Swg;

using var dbe = DatabaseEngine.Open("field.typhon", o => o   // (6) bootstrap
    .Register<HarvesterData>());   // components register explicitly; archetypes self-register at assembly load — no RegisterArchetype call

// (5) spawn inside a transaction; (8) Commit
EntityId id;
using (var tx = dbe.CreateQuickTransaction())
{
    id = tx.Spawn<Harvester>(Harvester.Data.Set(new HarvesterData { X = 0, Cargo = 0 }));
    tx.Commit();
}

// mutate: OpenMut for a writable handle
using (var tx = dbe.CreateQuickTransaction())
{
    tx.OpenMut(id).Write(Harvester.Data).Cargo += 10;
    tx.Commit();
}

// (3) query to FIND, (4) Open to READ
using (var tx = dbe.CreateQuickTransaction())
{
    foreach (var e in tx.Query<Harvester>())
    {
        var h = tx.Open(e.Id).Read(Harvester.Data);
        Console.WriteLine($"X={h.X} Cargo={h.Cargo}");   // X=0 Cargo=10
    }
}
```

Install the package first: `dotnet add package Typhon --prerelease` (Typhon is pre-alpha, so the
prerelease flag is required).

## 5. A primer to paste into your agent

If your agent isn't reading `llms.txt` automatically, paste this block into your prompt:

```text
Typhon is an ECS database (not SQL) from the `Typhon` NuGet package. Rules:
- Model data as `[Component("Name", 1)]` blittable structs (>= 8 bytes, public fields) — the name + revision
  are REQUIRED attribute args. Add `using Typhon.Schema.Definition;`. (Any namespace works, including the
  global namespace of a top-level-statements file.)
- An archetype is `[Archetype] partial class Foo : Archetype<Foo>` (no id arg — identity is the type name, or
  `[Archetype(Name="…")]`) with `public static readonly Comp<T> X = Register<T>();`. Archetypes self-register at assembly load.
- Open the engine with DatabaseEngine.Open(path, o => o.Register<T>()) — register components; archetypes need no registration call.
- All changes happen in a transaction: `using var tx = dbe.CreateQuickTransaction(); … tx.Commit();`.
  Spawn: `tx.Spawn<Foo>(Foo.X.Set(new T{…}))`. Mutate: `tx.OpenMut(id).Write(Foo.X).Field = …`.
- Query with the fluent view API: `tx.Query<Foo>().Where<T>(x => …).Count()` — NOT LINQ. To read an
  entity, use `tx.Open(id).Read(Foo.X)`, not the query-enumerated reference.
- Full docs: https://doc.typhondb.io/llms.txt
```

## See also

- [Getting Started](getting-started.md) — the guided first app.
- [Key Concepts](https://doc.typhondb.io/latest/key-concepts/) — the conceptual reference.
