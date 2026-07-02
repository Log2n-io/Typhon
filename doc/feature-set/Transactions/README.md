# Transactions

> Implements Typhon's three-tier execution model (`DatabaseEngine` → `UnitOfWork` → `Transaction`): the durability
> boundary, the ACID commit/rollback pipeline, per-UoW durability modes plus the orthogonal per-transaction
> `SingleVersion` discipline, deadline/cancellation propagation, and the crash-safe UoW identity that together decide
> how application writes become atomic, isolated, and durable. Three creation patterns (standard, quick, read-only)
> and an opt-in Bulk Load path cover everything from per-tick batching to multi-million-entity imports.

> 🔬 **Recommended:** read [in-depth-overview/08-transactions.md](../../in-depth-overview/08-transactions.md) (Chapter 08: Transactions) first to understand the overall design and concepts behind this category, before diving into the specific features below.

## Public Features

| Feature | Summary | Status | Level |
|---|---|---|---|
| [Unit of Work (durability boundary)](unit-of-work.md) | Middle tier of the three-tier API hierarchy — batches Transactions under a single flush/durability boundary, owning the shared ChangeSet, deadline, and UoW identity | ✅ Implemented | 🟢 Start Here |
| [Durability Modes (Deferred / GroupCommit / Immediate)](durability-modes/README.md) | Per-UoW control of WAL flush timing — trade commit latency for the data-at-risk window on crash | ✅ Implemented | 🟢 Start Here |
| &nbsp;&nbsp;↳ [Per-Transaction Durability Override](durability-modes/durability-override-escalation.md) | Escalate one critical operation to zero-loss durability without raising the durability mode of the surrounding batch | ✅ Implemented | 🟣 Advanced |
| [Transaction Creation Patterns](transaction-creation-patterns/README.md) | Three ways to obtain a `Transaction` — explicit UoW + `CreateTransaction`, single-shot quick transaction, or UoW-less read-only snapshot transaction | ✅ Implemented | 🟢 Start Here |
| &nbsp;&nbsp;↳ [Standard (UnitOfWork + CreateTransaction)](transaction-creation-patterns/transaction-creation-standard.md) | Open a `UnitOfWork` once and draw as many transactions from it as a batch needs, sharing one durability/flush boundary | ✅ Implemented | 🟢 Start Here |
| &nbsp;&nbsp;↳ [CreateQuickTransaction (single-shot, auto-dispose)](transaction-creation-patterns/transaction-creation-quick.md) | One call fuses a `UnitOfWork` and its one `Transaction` into a single disposable for single-shot writes | ✅ Implemented | 🟢 Start Here |
| &nbsp;&nbsp;↳ [CreateReadOnlyTransaction (snapshot reads)](transaction-creation-patterns/transaction-creation-readonly.md) | A `Transaction` with no `UnitOfWork`, UoW ID, or `ChangeSet` at all, for pure-read MVCC-snapshot workloads | ✅ Implemented | 🔵 Core |
| [SingleVersion Durability Discipline (TickFence / Commit)](durability-discipline/README.md) | Per-transaction knob, orthogonal to `DurabilityMode`, that picks how a `SingleVersion` write becomes durable | ✅ Implemented | 🔵 Core |
| &nbsp;&nbsp;↳ [TickFence Discipline (Default)](durability-discipline/durability-discipline-tickfence.md) | The default, lowest-cost `SingleVersion` write — durable at the next tick fence, not at commit | ✅ Implemented | 🔵 Core |
| &nbsp;&nbsp;↳ [Commit Discipline (Variant-A Staging)](durability-discipline/durability-discipline-commit.md) | Atomic, zero-loss `SingleVersion` writes — durable and visible together at `Commit()`, with no revision chain | ✅ Implemented | 🟣 Advanced |
| [Commit / Rollback Pipeline (ACID Commit Path)](commit-rollback-pipeline.md) | `Transaction.Commit`/`Rollback` overloads implementing the append-before-publish commit pipeline with atomic conflict resolution and always-completing rollback | ✅ Implemented | 🔵 Core |
| [Optimistic Concurrency Conflict Resolution](optimistic-conflict-resolution.md) | Pluggable `ConcurrencyConflictHandler` invoked per conflicting entity during commit, exposing four data views via `ConcurrencyConflictSolver`; default with no handler is last-writer-wins | ✅ Implemented | 🔵 Core |
| [Deadline & Cooperative Cancellation](deadline-cancellation.md) | An absolute deadline rides every transaction commit, propagating through every lock and aborting cleanly only before work starts | ✅ Implemented | 🔵 Core |
| [UoW Identity & Crash-Safe Recovery Boundary](uow-identity-crash-recovery.md) | Each UoW gets a 15-bit ID from a persistent, back-pressured `UowRegistry`; on crash, still-`Pending` UoW IDs are voided so their revisions become instantly invisible with no replay | ✅ Implemented | 🟣 Advanced |
| [Transaction Lifecycle, Thread Affinity & Pooling](transaction-lifecycle-pooling.md) | `Transaction` is single-thread-affine with a fail-fast state machine; `TransactionChain` provides lock-free CAS-based creation, exclusive-lock removal, 16-instance pooling, and `MinTSN` tracking for MVCC garbage collection | ✅ Implemented | 🟣 Advanced |
| [Bulk Load Session](bulk-load-session.md) | Opt-in, exclusive write path that batches writes through a recycled `Transaction` and commits the whole load atomically via a checkpoint barrier | ✅ Implemented | 🟣 Advanced |

## Internal Features

*No internal-only engine machinery broken out in this category — Transactions is Typhon's primary
application-facing tier (`DatabaseEngine` → `UnitOfWork` → `Transaction`). The engine machinery behind it
(`TransactionChain`, `UowRegistry`, `CommitContext`) is covered inline within the public features above, as the
mechanics that back their guarantees, rather than as separately usable capabilities.*