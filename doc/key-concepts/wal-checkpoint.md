---
uid: concept-wal-checkpoint
title: 'WAL & checkpoint'
description: 'The write-ahead log is what makes a commit durable — a change is safe once its record is fsync''d. The checkpoint consolidates the WAL into the data file so segments can be recycled; it adds no durability. Both are mandatory.'
---

# WAL & checkpoint

> **In one line:** the **write-ahead log** makes a commit durable; the **checkpoint** consolidates it into the data file and recycles WAL space — it adds *no* durability.

A commit is written to the append-only WAL, and becomes **durable the instant its record is `fsync`'d** — *when* that happens is set by the [durability mode](xref:concept-durability). The checkpoint runs in the background, draining dirty [pages](xref:concept-page-cache) into the data file and marking consumed WAL segments recyclable. Crucially, the checkpoint **doesn't add durability** — a change is equally safe whether recovered by WAL replay or already on the data pages; the checkpoint only moves *where* the durable copy lives.

WAL and checkpoint are **mandatory** (a whole-database no-WAL mode was removed); to run without disk I/O you inject an in-memory WAL backend rather than disabling it. On crash, recovery replays the WAL and reconstructs every transaction whose commit record reached disk, atomically.

## How it relates

- **[Durability — mode & discipline](xref:concept-durability)** — decides *when* the WAL flushes and *how* a SingleVersion write is logged.
- **[Unit of Work](xref:concept-unit-of-work)** — one WAL flush per UoW makes its commits durable.
- **[Tick fence](xref:concept-tick-fence)** — emits the WAL records for SingleVersion writes.
- **[Page cache](xref:concept-page-cache)** — the checkpoint drains dirty pages the WAL already protects.

## In the API

- [`WalWriterOptions`](xref:Typhon.Engine.WalWriterOptions) — enables and tunes the WAL (`options.Wal = new WalWriterOptions()`).

## Learn & use

- **Narrative:** [Guide ch.3 — durability](xref:guide-transactions) · [cheat sheet](xref:guide-isolation-durability)
- **Feature detail:** [durability](xref:feature-durability-index) · [WAL v2](xref:feature-durability-wal-v2) · [checkpoint v2](xref:feature-durability-checkpoint-v2-index)
