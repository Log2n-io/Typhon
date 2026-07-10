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

## Requirements

- **.NET 10** (`net10.0`).

## Links

- Documentation: <https://doc.typhondb.io>
- Website: <https://typhondb.io>
- Source: <https://github.com/log2n-io/Typhon>

## License

Source-available. See the bundled `LICENSE.md`. Pre-1.0 use is unrestricted.
