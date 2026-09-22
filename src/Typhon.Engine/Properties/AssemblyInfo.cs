using System.Runtime.CompilerServices;

// InternalsVisibleTo policy (per claude/research/PublicVsInternalApiClassification.md §8 + §10.3 E):
// The list below is the minimum set the solution needs to compile, established by a mechanical
// audit: comment everything out, build, add back only the assemblies the compiler insists on.
// Last audited 2026-05-11. If you add a new assembly that references Typhon.Engine, do not blindly
// add a friend declaration — try building first; only add if the build fails AND the failure is
// driven by genuine internal-implementation reuse (refactor to the public surface if possible).

// Production friend assemblies
[assembly: InternalsVisibleTo("typhon")]                    // Typhon.Shell / CLI (AssemblyName=typhon, #428)
[assembly: InternalsVisibleTo("Typhon.Workbench")]

// Test / sample friend assemblies
[assembly: InternalsVisibleTo("AntHill.Core")]
[assembly: InternalsVisibleTo("AntHill.Demo")]
[assembly: InternalsVisibleTo("Typhon.Benchmark")]
// Added 2026-06-29: competitive benchmark harness needs InMemoryWalFileIO for zero-disk D0 CPU measurement
// (same genuine internal reuse as Typhon.Benchmark — measures the commit path without WAL-writer disk noise).
[assembly: InternalsVisibleTo("Typhon.CompetitiveBenchmark")]
[assembly: InternalsVisibleTo("Typhon.Engine.Tests")]
// Added 2026-07-19 (#514 D1): archetype catalog ids are now engine-assigned (no author-set [Archetype(Id=N)]), so the Workbench controller tests resolve an
// archetype's runtime id via Archetype<T>.Metadata instead of a hardcoded literal.
[assembly: InternalsVisibleTo("Typhon.Workbench.Tests")]
[assembly: InternalsVisibleTo("Typhon.IOProfileRunner")]
[assembly: InternalsVisibleTo("Typhon.MonitoringDemo")]
// Added 2026-08-14: the SpaceBattle demo is a spatial-partitioning observatory — it renders the per-cell
// CellState (EntityCount/ClusterCount/Tier), the authoritative cluster->cell map and the per-tick migration
// counters. None of those has a public surface (SpatialGridAccessor.GetCell is internal because CellState is,
// ArchetypeClusterState is internal wholesale), and the whole point of the tool is to show the internal state
// the public API deliberately hides. Genuine internal-implementation reuse; not refactorable to public.
[assembly: InternalsVisibleTo("SpaceBattle")]
// Added 2026-09-07 (#906); justification narrowed 2026-09-18 (#205, P1-23). The original two reasons are CLOSED:
// ClusterSpatialQueryResult carries an EntityId rather than a raw long (#909), and Archetype<T>.CatalogId is public,
// so the blueprint no longer needs EntityId.FromRaw. What remains is MEASUREMENT, not gameplay and not replication:
// roughly 980 lines of work probes and the spatial census read ArchetypeClusterState directly, and the census is the
// last EpochGuard caller in the demo. Those belong in SwgTatooine.Bench, which the architecture already names as the
// only friend assembly; extracting them is the open half of P1-23 and this line goes with them.
//
// The debt cannot grow while it waits: scripts/check-blueprint-public-api.py ratchets the exact set of demo files
// allowed to touch internals, and fails both when a new one appears AND when a listed one is cleaned without the
// list being tightened. The demo's replication code is already clean and the same script asserts it.
[assembly: InternalsVisibleTo("SwgTatooine")]
// Re-added 2026-05-25 (#376 Stage-3 4A): the `with-queries` trace fixture must emit QueryPlan + phase SPAN
// records, whose typed `EncodeTo` encoders are internal source-generated `[TraceEvent]` ref structs
// (QueryPlanEvent et al.) with NO public surface. Genuine internal-implementation reuse — the fixture drives
// the engine's own encoders rather than hand-packing the wire format. (Assembly name is the `.schema` variant.)
[assembly: InternalsVisibleTo("Typhon.Workbench.Fixtures.schema")]

// Dropped 2026-05-11 — verified not needed by the mechanical audit (build succeeds without them):
//   "Typhon.Shell"               — redundant: Shell's AssemblyName is `typhon`, not `Typhon.Shell`.
//   "Typhon.Shell.Extensibility" — builds clean.
//   "Typhon.Workbench.Fixtures"  — builds clean.
//   "Typhon.Workbench.Tests"     — builds clean (it goes through Typhon.Workbench's surface only).
