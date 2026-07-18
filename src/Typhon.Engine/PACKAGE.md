# Typhon

**A real-time, low-latency ACID database engine for .NET.**

Typhon is an embedded database engine built on an **ECS (Entity-Component-System)** architecture with
**MVCC snapshot isolation**, engineered for microsecond-level performance. It targets workloads that need
transactional correctness *and* tight, predictable latency — real-time simulations, game servers, and
in-process analytical/transactional (HTAP-shaped) data.

> ⚠️ **Pre-alpha.** This package is published as a prerelease. APIs, on-disk formats, and defaults will
> change without notice until the first stable release. Not for production use yet.

## Install

```bash
dotnet add package Typhon --prerelease
```

Prerelease packages are opt-in — the `--prerelease` flag (or checking "Include prerelease" in your IDE)
is required.

## What's in the box

This single package bundles the full public surface:

- **Engine** — transactions, component tables, B+Tree indexes, storage/persistence, MVCC.
- **Profiler** — the in-box CPU/event profiler APIs.
- **Protocol** — the wire-format types.
- **Schema.Definition** — the `[Archetype]` / component-definition attributes.

A **source generator** ships alongside it: decorate a partial class with `[Archetype]` and Typhon generates
the strongly-typed, zero-copy component accessors for it at compile time.

## Using with an AI coding agent

Typhon is new — it's in no model's training data, so a coding agent will guess a SQL-shaped API by
default. Point it at **<https://doc.typhondb.io/llms.txt>** (most agent tools probe it automatically),
or paste this primer:

> Typhon is an ECS database (not SQL). Model data as `[Component]` blittable structs (≥ 8 bytes, public
> fields) declared **inside a namespace**; an archetype is `[Archetype] partial class Foo : Archetype<Foo>`
> with `public static readonly Comp<T> X = Register<T>();`. Open with `DatabaseEngine.Open(...)`; do all
> writes in `using var tx = dbe.CreateQuickTransaction(); … tx.Commit();`. Query with
> `tx.Query<Foo>().Where<T>(x => …)` (not LINQ); read an entity via `tx.Open(id).Read(Foo.X)`.

Full guide: <https://doc.typhondb.io/latest/guides/using-with-ai-coding-agents.html>

## Requirements

- **.NET 10** (`net10.0`).

## Links

- Documentation: <https://doc.typhondb.io>
- Website: <https://typhondb.io>
- Source: <https://github.com/log2n-io/Typhon>

## License

Source-available. See the bundled `LICENSE.md`. Pre-1.0 use is unrestricted.
