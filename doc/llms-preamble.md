<!--
  Preamble injected verbatim into the generated /llms.txt (see scripts/gen-llms-txt.py).
  Keep it short — it primes an agent BEFORE it reads the map. The distilled
  agent-guidance gotchas from #498 land here (tracked as #500). HTML comments
  like this one are stripped from the emitted preamble.
-->
Typhon is an **ECS database, not SQL**. Before writing code against the `Typhon`
NuGet package, load the mental model: read **Key Concepts** first, then the **Guide**.

Non-negotiable idioms an agent gets wrong by default:

- **Data = blittable-struct components**, addressed by `EntityId` — there are no tables, rows, or SQL.
- **All mutation happens inside a transaction**; `Commit()` returning does *not* imply on-disk durability unless the unit of work uses a durable mode.
- **Storage mode sets the ACID scope** (Versioned / SingleVersion / Transient / Committed) — pick it per component, it is not a global switch.
- **Transactions are single-thread-affine**; never share one across threads.
- **Query with the ECS view API**, not LINQ-to-SQL — see Key Concepts › View.

Every page listed below is also available as clean markdown at `<url>.md`, and the
whole conceptual core is at https://doc.typhondb.io/latest/llms-full.txt.
