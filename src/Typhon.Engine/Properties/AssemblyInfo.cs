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
[assembly: InternalsVisibleTo("Typhon.Benchmark")]
// Added 2026-06-29: competitive benchmark harness needs InMemoryWalFileIO for zero-disk D0 CPU measurement
// (same genuine internal reuse as Typhon.Benchmark — measures the commit path without WAL-writer disk noise).
[assembly: InternalsVisibleTo("Typhon.CompetitiveBenchmark")]
[assembly: InternalsVisibleTo("Typhon.Engine.Tests")]
// Added 2026-10-19 (#1040, AC-15): the WebSocket parity test puts the engine's own SubscriptionAcceptor and the engine tests' fake host behind the real
// ASP.NET Core endpoint, so the bytes it compares are the engine's; the TCP parity test does the same from Typhon.Engine.Tests.
[assembly: InternalsVisibleTo("Typhon.Subscriptions.AspNetCore.Tests")]
// Added 2026-10-27 (#1517): the request-correlation tests start the profiler with the engine tests' ring observer behind a real ASP.NET Core host, to
// read back the records a request produced.
[assembly: InternalsVisibleTo("Typhon.Profiler.AspNetCore.Tests")]
// Added 2026-07-19 (#514 D1): archetype catalog ids are now engine-assigned (no author-set [Archetype(Id=N)]), so the Workbench controller tests resolve an
// archetype's runtime id via Archetype<T>.Metadata instead of a hardcoded literal.
[assembly: InternalsVisibleTo("Typhon.Workbench.Tests")]
// Added 2026-10-21 (#1418): `typhon backup log` and `restore --lsn/--time` are tested over a real WAL archive whose segments, samples and point the test
// lays down on a logical clock: the kit builds the archiver with CreateWalArchiver's seams and drives its Step and ArchivePending itself, and captures
// through the coordinator. The public BackupOptions configures archiving, but cannot make that archive deterministic.
[assembly: InternalsVisibleTo("Typhon.Shell.Tests")]
[assembly: InternalsVisibleTo("Typhon.IOProfileRunner")]
[assembly: InternalsVisibleTo("Typhon.MonitoringDemo")]
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
[assembly: InternalsVisibleTo("SwgTatooine.Server")]
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
//
// Dropped 2026-10-20 (#851: a demo uses no InternalsVisibleTo):
//   "AntHill.Core"               — builds clean.
//   "AntHill.Demo"               — read two profiler drop counters off the internal TyphonEvent; TyphonProfiler exposes both.
