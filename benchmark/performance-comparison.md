# Typhon vs Other Databases — Plain English

> ## Read this first
>
> **Every number here comes from a benchmark suite that ships in this repository**, and every one is reproducible:
>
> - `test/Typhon.CompetitiveBenchmark/` — the competitive harness and all competitor adapters
> - `test/Typhon.Benchmark/` — the Typhon-only measurements
>
> Run commands are given with each section.
>
> **On the competing engines — please read this in good faith, and hold us to it.** SQLite, RocksDB, LMDB and FASTER are
> excellent, mature systems built by people who understand them far better than we do. Their adapters here were written
> to the best of our knowledge and **may be far from optimal**: a specialist would very likely make any of them faster,
> in places substantially. Where an adapter is naive, the number it produces flatters Typhon — and that is a defect in
> our measurement, not a property of that engine.
>
> This is not hypothetical. Reviewing the LMDB adapter before publishing, we found it was re-opening the database handle
> on every batch and allocating on every operation. Fixing it made LMDB **up to 2× faster**, enough to take the batched-read
> row and to triple its lead on range scans. Those corrected numbers are the ones below. We assume more such defects
> remain.
>
> We publish anyway, because the alternative — comparative claims with no published method — is worse. The intent is to
> state what we measured, how, and with what code, so it can be checked and corrected.
>
> **If you know one of these engines well and our usage is wrong, please open an issue or a PR against the adapter.** We
> would rather publish a corrected number than a favourable one.
>
> **Measured:** 2026-07-29 · AMD Ryzen 9 7950X (Zen 4), **16 threads** pinned to one CCD · Windows 11 · .NET 10 · engine
> revision `81e2a46` · no per-commit fsync, so these measure the *engine*, not the disk. Single machine, single session —
> treat these as orders of magnitude, not precision figures.

---

## 1. Who's in the race

The single most important thing to understand: **these engines do not all do the same job.** An engine that offers fewer
guarantees can be faster simply because it is doing less work.

| Engine | What it actually is | Does it protect your data? |
|---|---|---|
| **Typhon** | Full database: transactions, MVCC, indexes, spatial queries, ECS data model | ✅ Yes — full ACID |
| **SQLite** | The classic embedded SQL database | ✅ Yes — full ACID |
| **RocksDB** | Key→value store (log-structured) | ✅ Durable, but no transactions spanning keys |
| **LMDB** | Key→value store (memory-mapped) | ✅ Durable, but **one writer at a time** |
| **FASTER** | In-memory key→value cache | ❌ **No** — data is gone on crash |

### Why FASTER is in this list

FASTER is **not a competitor** — it is a *reference line*. It is a key-value cache, not a database: no transactions, no
MVCC, no indexes, no ordered scan, and it loses your data on a crash. It is also genuinely, deservedly fast, with
excellent concurrency scaling.

It is here because it marks roughly **what the hardware can do** when an engine carries none of the machinery a database
needs. When Typhon lands within 2× of FASTER while providing full ACID transactions and MVCC snapshot isolation, that
gap is the price of those guarantees — and that is a more useful thing to know than any ratio against a rival.

---

## 2. The main operations

**Throughput — higher is better, in millions of operations per second, 16 threads.** Typhon is the baseline; brackets
show how many times slower or faster each engine is.

```bash
cd test/Typhon.CompetitiveBenchmark
dotnet run -c Release -- m       # read / change matrix
dotnet run -c Release -- ycsb    # mixed read+write
dotnet run -c Release -- rmw     # read-then-write
dotnet run -c Release -- scan    # ordered range scan
dotnet run -c Release -- c2      # readers and writers at the same time
```

| What you're doing | Typhon | SQLite | RocksDB | LMDB | FASTER |
|---|---:|---:|---:|---:|---:|
| **Read records** (in batches) | **66.9** | 10.9 *(6.1× slower)* | 15.9 *(4.2× slower)* | **70.6** *(1.1× faster)* | 116.2 *(1.7× faster)* |
| **Read one record at a time** | **51.6** | 0.2 † | 15.1 *(3.4× slower)* | 4.8 *(10.8× slower)* | 85.6 *(1.7× faster)* |
| **Change records** | **60.0** | 1.3 † | 4.7 *(12.8× slower)* | 4.0 *(15.1× slower)* | 126.6 *(2.1× faster)* |
| **Half reads / half writes** | **38.6** | 0.1 † | 1.3 *(30× slower)* | 0.06 † | 76.8 *(2.0× faster)* |
| **Read-then-write the same record** | **28.1** | 0.05 † | 0.8 *(34× slower)* | 0.03 † | 79.8 *(2.8× faster)* |
| **Scan a sorted range** | 118.5 | 52.2 *(2.3× slower)* | 73.0 *(1.6× slower)* | **421.3** *(3.6× faster)* | n/a |
| **Readers and writers at once** *(writer speed)* | **15.0** | 0.01 † | 0.4 *(35× slower)* | 0.01 † | 36.6 *(2.4× faster)* |

† **Not a performance failure — a documented design choice.** SQLite and LMDB both permit **one writer at a time**:
SQLite takes a single write lock, LMDB a single global write mutex. On any workload containing writes, every writer
queues, so throughput is bounded by one core regardless of how many you have. Both engines chose this deliberately — it
is what makes their read paths so simple and their crash behaviour so easy to reason about. Quoting a multiplier here
would say more about the workload than about the engine, so we give the raw figure and leave it at that. **If your
workload is read-mostly or single-writer, these rows do not apply to you.**

> **How to read a row.** On **batched reads**, LMDB does 70.6 million per second against Typhon's 66.9 — LMDB wins.
> On **change records**, Typhon does 60.0 against RocksDB's 4.7, about 13 of ours for every one of theirs.

### The caveat that cuts in Typhon's favour — there are three ways to reach data, and this table shows the slowest

These are point-access numbers, because point access is all a key-value store can do. Typhon has two further routes that
a KV store structurally does not:

| Way in | What happens | Lookup cost per entity |
|---|---|---|
| **1. Point access** *(this table)* | Hand the engine one id; it probes the entity map, checks visibility, resolves the slot | a hash probe **per entity** |
| **2. Batch / BulkLoad** | Two distinct mechanisms: `SpawnBatch` resolves the archetype, table and accessor **once** per batch; `BulkLoad` additionally skips the per-row write-ahead log for mass ingest. They perform differently — see §4 | amortised — **once per batch** |
| **3. Cluster iteration** | Walk the storage in the order it physically sits in memory, taking one packed column per component and stepping an occupancy bitmask | **none — no hash, no index at all** |

Route 3 is **110–148× cheaper per record** than the point-access figures above — measured directly, all routes on the
same data in the same run (see §4). Nothing is being *found*; the data is *read in order*, which is what makes it
cache-friendly. This is the path an ECS tick, a physics pass or a full-column aggregate runs on.

#### Route 3 has a spatial form, and it is the one that matters for games

Entities are stored in **clusters** (8–64 slots, one packed column per component). For archetypes with a spatial
component, Typhon enforces an extra rule: **every entity in a cluster belongs to the same grid _cell_**, and each cell
owns a list of the clusters sitting in it.

That turns "process everything near here" into: look up the cell → walk its cluster list → every cluster you touch is
wholly relevant. **No per-entity position test happens at all.** Each cell also carries a simulation **tier**, and
clusters can go dormant individually, so whole regions can be skipped without reading a single entity.

Two things route 3 is *not*: the **"scan a sorted range"** row above is not route 3 (it walks a B+Tree index), and
**"bulk"** in Typhon means route 2 — a write/ingest optimisation — not a way of reading.

---

## 3. Where lower is better

**Time per operation — lower is better.** Single-threaded, no fsync.

```bash
dotnet run -c Release -- c3      # cross-component atomic commit
```

| What you're doing | Typhon | SQLite | RocksDB | LMDB |
|---|---:|---:|---:|---:|
| **Save 3 fields as one atomic change** | **3.10 µs** | 11.28 µs *(3.6× slower)* | 5.90 µs *(1.9× slower)* | 12.52 µs *(4.0× slower)* |

Typhon wins outright, including against the key-value stores — which have no equivalent of "save these three fields
together or not at all" and must assemble it from separate writes. Typhon emits **one logical redo record** for the whole
entity; the others write three-key batches.

---

## 4. What one operation costs inside Typhon

Everything so far compares engines. This section compares **Typhon against itself** — the answer to "so what does one
operation actually cost?", and the table to reach for when someone quotes a single number without saying which route it
came from.

```bash
cd test/Typhon.Benchmark
dotnet run -c Release -- --filter '*StorageModeProof*'    # the mode × scope matrix
dotnet run -c Release -- --filter '*EcsAccessPath*'       # the route comparison
dotnet run -c Release -- --bulk-load 500000               # the four ingest routes
```

The three storage modes are Typhon's durability choice per component: **Versioned** keeps full history, **SingleVersion**
overwrites in place, **Transient** is never saved to disk. Columns are the routes from §2. **All figures in nanoseconds**
— §2 and §3 were in millions/second and microseconds.

| Storage mode | Operation | In a system loop *(cluster iteration)* | By entity id *(point access)* | …of which, the field op | Write + commit |
|---|---|---:|---:|---:|---:|
| **Versioned** | read | **0.74** | **115** | *50* | — ² |
| **Versioned** | write | n/a ¹ | **550** | *486* | **749** |
| **SingleVersion** | read | **0.77** | **80** | *~15* ³ | — ² |
| **SingleVersion** | write *(tick fence)* | **0.74** | **84** | *~19* ³ | **86** |
| **SingleVersion** | write *(commit discipline)* | n/a ¹ | **104** | *~39* ³ | **163** |
| **Transient** | read | **0.71** | **74** | *~9* ³ | — ² |
| **Transient** | write | **0.74** | **85** | *~21* ³ | **84** ⁴ |

**Bold = measured directly.** *Italic = worked out by subtraction*, not measured: locating an entity by id costs **65 ns**
on its own, and that is subtracted out to leave the field operation alone.

¹ **Not a real operation.** A system loop hands you the data where it physically sits — but a Versioned write must create
a new revision, and a commit-discipline write must be staged until the commit. Neither can be expressed as a write
straight into storage, so there is no honest number to put here. *(It would be easy to print one — it would just be
measuring the wrong thing.)*
² **Meaningless by definition.** "Write + commit" is a write followed by making it durable. A read has nothing to commit.
³ **Below the measurement noise.** These are small differences between two larger measurements, so treat them as
indicative. For cheap operations the trustworthy figure is the system-loop column, measured directly.
⁴ **Not a durability cost.** Transient data is never written to disk, so committing does nothing for it. This is the cost
of a transaction round trip, which is why it matches the SingleVersion figure above it.

**The single most useful line here:** locating an entity by id costs **65 ns**, and every point-access number includes it.
For a SingleVersion read at 80 ns, **81 % of the cost is finding the entity, not reading it** — which is exactly why the
system-loop column, where nothing is looked up, is two orders of magnitude cheaper. The next table measures by how much.

### The routes, measured against each other

The table above splits by *storage mode*. This one splits by *route* — the three ways of reaching data from §2, now with
numbers. Every row does the **identical arithmetic on the identical entities**; only the way of reaching them changes.
**Nanoseconds per entity.**

| Route | 10 000 entities | 100 000 entities | vs point access |
|---|---:|---:|---:|
| **Point access** — one id at a time | 87.0 | 110.1 | 1× *(baseline)* |
| **Flat sweep** — whole archetype, in storage order | **0.79** | **0.74** | **110× → 148× faster** |
| **Cell-by-cell sweep** — spatially partitioned | 2.80 | **0.94** | **31× → 117× faster** |
| **3×3 cell neighbourhood** — "everything near me" | 4.09 | **1.09** | *(see below)* |

**1. The advantage grows with size.** Point access gets *worse* as the database grows (87 → 110 ns) because finding an
entity increasingly means going to main memory. Sweeping does not (0.79 → 0.74) — it reads in order, which hardware is
built for. The gap widens from **110× to 148×** as you scale up, rather than narrowing.

**2. Spatial partitioning is nearly free at realistic sizes.** Splitting the world into cells costs **3.5×** over a flat
sweep at 10 000 entities but only **1.26×** at 100 000. The fixed cost per cell is the same either way; with 390 entities
in a cell instead of 39, it disappears. **You get full spatial scoping for ~26 % over the theoretical floor.**

**3. Processing a small region costs the same per entity as processing everything.** The 3×3 row touches 3 519 entities
out of 100 000 — 3 % of the world — at **1.09 ns each**, against 0.94 for sweeping all of it. Skipping 97 % of the data
costs essentially nothing, because **the cell list is the filter**: no per-entity distance test, no index lookup. That row
is not comparable with the others on total time (it does far less work); it is there to show that being selective is not
paid for.

> **Why the flat sweep is the wrong number to publish.** It is the fastest, but no game can use it — it means "process
> the entire world every frame". **Cell-by-cell at 0.94 ns is the honest headline**, because it is the shape real
> simulation code runs in, and it is still **117× faster than point access**.

*All three full-world routes are checksum-verified in benchmark setup to prove they visit the same entities — otherwise
the ratios would mean nothing. See `ValidateRoutesAgree` in `Ecs/EcsAccessPathBenchmarks.cs`.*

### And what "bulk" is actually worth

§2 lists batching as route 2. It answers a different question — batching applies to **creating and destroying**, not to
reading — and "bulk" in Typhon means two different mechanisms that perform very differently.

Four ways to load 500 000 entities into a fresh database. Real on-disk write-ahead log, median of 3 runs:

| How you load | ns per entity | vs naive | vs batched commits |
|---|---:|---:|---:|
| **Naive** — one transaction per entity | 3 838 | 1× *(baseline)* | 0.24× |
| **Batched commits** — commit every 8 192 | 914 | **4.2× faster** | 1× |
| **`SpawnBatch`** — archetype resolved once per batch | **821** | **4.7× faster** | 1.11× |
| **`BulkLoad`** — skips the per-row log, one checkpoint at the end | 1 244 | 3.1× faster | **0.73×** |

**The biggest win by far is batching your commits — 4.2×, for a one-line change.** Everything after that is small.

`BulkLoad` (`DatabaseEngine.BeginBulkLoad`) is the dedicated mass-ingest path: it skips the per-row write-ahead log
entirely and pays a single forced checkpoint at the end instead. It is **3.1× faster than the naive loader** — which is
the baseline it was designed and accepted against. But against a loader that merely batches its commits it is currently
**slower**. The reason is that the log writer already batches and flushes in the background, so a per-row record costs a
memory copy rather than a disk write; the end-of-session checkpoint ends up costing more than the log traffic it saved.
This holds at scale: 4 M entities take **10.9 s** via `BulkLoad` against **9.2 s** with batched commits.

> **So: batch your commits. Reach for `BulkLoad` for its durability properties — a bulk that crashes is as if it never
> started — not for throughput.** We publish this because it is what we measured, and it is not the result we expected.

*(Batching `Destroy` behaves the same way: `DestroyBatch` is 1.10× over a destroy loop — one mutability check and a
pre-sized pending list, with the per-entity cascade work unchanged.)*

**Choosing the right access route is worth 117×. Choosing the right ingest route is worth 4.7×.** Both matter; they are
not the same order of decision, which is why they are not in the same table.

---

## 5. Where Typhon is in a different league

### Writes that scale with your cores

This is the one to look at first. Every other durable engine here serialises its writers behind a lock; Typhon does not
take one. Reads use **optimistic lock coupling** — a reader validates a version stamp instead of acquiring anything, and
retries on the rare occasion it loses a race. Writes coordinate with **compare-and-swap in the worst case**. No mutex, no
reader-writer lock, no single-writer gate anywhere on the hot path.

That is an architectural claim, so here is what it produces. Same workload, thread count raised from 1 to 16:

| Engine | Reads 1t → 16t | Writes 1t → 16t |
|---|---:|---:|
| **Typhon** | **16.3× faster** | **17.7× faster** |
| SQLite | 8.7× faster | **1.0× — flat** |
| RocksDB | 9.8× faster | 1.7× faster |
| LMDB | 10.3× faster | **0.79× — slower with more threads** |
| *FASTER (not durable)* | *18.6× faster* | *20.5× faster* |

**Typhon is the only durable engine here whose writes scale at all.** SQLite gains nothing from 15 extra cores; LMDB
actively loses throughput as threads contend for its single write mutex. Both are deliberate designs, and both mean the
same thing for you: on a write-heavy workload, buying more cores buys nothing.

Typhon's 17.7× on 16 logical cores (8 physical + SMT) is slightly superlinear because the work is memory-latency-bound —
SMT siblings fill each other's stall cycles. The read figure lands within 12 % of FASTER, an in-memory cache carrying
none of Typhon's transactional machinery.

### Capabilities the key-value stores do not have at all

No ratio to quote here — only a presence or an absence:

- **Spatial queries.** "Everything within this box / radius" is served from the native cluster grid. SQLite needs the
  R*Tree extension; RocksDB, LMDB and FASTER have no spatial concept whatsoever.
- **Cross-component atomic commit.** §3 — one logical record for an entity spanning several components.
- **MVCC snapshot isolation.** Readers never block writers and never see a torn state, without taking a lock.
- **Full-column analytics on live transactional data.** Route 3 at ~0.8 ns/record, on the same data being transacted
  against — no ETL, no separate analytical copy.

---

## 6. How to read all this honestly

**FASTER wins most raw numbers — and that is the point of including it** (see §1). No transactions, no MVCC, no indexes,
no ordered scan, and it loses your data on a crash. It marks the hardware's speed limit; it is a reference line, not an
opponent.

**LMDB has a split personality, and it is genuinely excellent at what it is for.** Its memory-mapped pages are never
copied, so readers never contend: it **beats Typhon on batched reads** (70.6 vs 66.9) and is **3.6× faster on range
scans**, which is the single largest margin against us in this document. The trade is its single write mutex — one writer
at a time, so anything with write traffic in it collapses. If your workload is read-mostly, LMDB is a superb choice and
this document should not talk you out of it.

**The comparison that means the most is Typhon vs SQLite** — the only other full ACID database here. Matched guarantee
for guarantee, Typhon leads every row: 6.1× on batched reads, and larger margins on writes and mixed workloads where
SQLite's single-writer design binds.

**Typhon's real differentiator is concurrency under mixed load** — see §5 for the scaling figures. It is the only
*durable* engine here where writers scale (15.0 M/s) while readers stay fast (13.4 M/s) *at the same time*, because it
takes no lock on either path. Every other durable engine serialises its writers.

**The honest losses:** range scans against LMDB's memory-mapped cursor (3.6×), batched reads against LMDB (1.1×), and raw
single-operation speed against FASTER, which carries none of Typhon's machinery and is not durable. Each is a known
trade, not a defect.

**What this document does not measure:** durability tiers involving real fsync, crash-recovery time, on-disk size, memory
footprint, or multi-process access. The no-fsync setting isolates CPU cost; it does not tell you what any of these
engines does when the power fails.

---

## Reproducing this

```bash
# competitive numbers (§2, §3)
cd test/Typhon.CompetitiveBenchmark
dotnet run -c Release -- verify   # checksum-validates every engine's read path first
dotnet run -c Release -- m
dotnet run -c Release -- ycsb
dotnet run -c Release -- rmw
dotnet run -c Release -- scan
dotnet run -c Release -- c2
dotnet run -c Release -- c3

# Typhon-only numbers (§4)
cd test/Typhon.Benchmark
dotnet run -c Release -- --filter '*StorageModeProof*'
dotnet run -c Release -- --filter '*EcsAccessPath*'
dotnet run -c Release -- --filter '*EcsLifecycle*'
dotnet run -c Release -- --bulk-load 500000 4000000      # ingest routes, at two scales
```

`verify` is worth running first: it checks every engine's read path against an analytically known checksum, so a
"fast" adapter that has stopped returning correct rows fails loudly instead of quietly producing a good number.
