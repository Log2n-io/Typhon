using System.Runtime.CompilerServices;

// InternalsVisibleTo policy (per claude/research/PublicVsInternalApiClassification.md §8 + §10.3 E):
// The list below is the minimum set the solution needs to compile, established by a mechanical
// audit: comment everything out, build, add back only the assemblies the compiler insists on.
// Last audited 2026-05-11. If you add a new assembly that references Typhon.Engine, do not blindly
// add a friend declaration — try building first; only add if the build fails AND the failure is
// driven by genuine internal-implementation reuse (refactor to the public surface if possible).

// Production friend assemblies
[assembly: InternalsVisibleTo("tsh")]                       // Typhon.Shell (AssemblyName=tsh)
[assembly: InternalsVisibleTo("Typhon.Workbench")]

// Test / sample friend assemblies
[assembly: InternalsVisibleTo("AntHill.Core")]
[assembly: InternalsVisibleTo("AntHill.Demo")]
[assembly: InternalsVisibleTo("Typhon.Benchmark")]
// Added 2026-06-29: competitive benchmark harness needs InMemoryWalFileIO for zero-disk D0 CPU measurement
// (same genuine internal reuse as Typhon.Benchmark — measures the commit path without WAL-writer disk noise).
[assembly: InternalsVisibleTo("Typhon.CompetitiveBenchmark")]
[assembly: InternalsVisibleTo("Typhon.Client.Tests")]
[assembly: InternalsVisibleTo("Typhon.Engine.Tests")]
[assembly: InternalsVisibleTo("Typhon.IOProfileRunner")]
[assembly: InternalsVisibleTo("Typhon.MonitoringDemo")]
// Re-added 2026-05-25 (#376 Stage-3 4A): the `with-queries` trace fixture must emit QueryPlan + phase SPAN
// records, whose typed `EncodeTo` encoders are internal source-generated `[TraceEvent]` ref structs
// (QueryPlanEvent et al.) with NO public surface. Genuine internal-implementation reuse — the fixture drives
// the engine's own encoders rather than hand-packing the wire format. (Assembly name is the `.schema` variant.)
[assembly: InternalsVisibleTo("Typhon.Workbench.Fixtures.schema")]

// Dropped 2026-05-11 — verified not needed by the mechanical audit (build succeeds without them):
//   "Typhon.Shell"               — redundant: Shell's AssemblyName is `tsh`, not `Typhon.Shell`.
//   "Typhon.Shell.Extensibility" — builds clean.
//   "Typhon.Workbench.Fixtures"  — builds clean.
//   "Typhon.ARPG.Shell"          — builds clean.
//   "Typhon.Workbench.Tests"     — builds clean (it goes through Typhon.Workbench's surface only).
