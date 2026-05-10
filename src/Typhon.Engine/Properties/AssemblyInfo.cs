using System.Runtime.CompilerServices;

// InternalsVisibleTo policy (per claude/research/PublicVsInternalApiClassification.md §8):
// "Include any assembly that needs it for the rest of the solution to compile.
//  No upfront audit — surface the list mechanically as build errors appear during the migration.
//  A leak-tightening pass (refactor consumers to the public surface so this list shrinks)
//  is explicitly deferred — fight for another day."
//
// All assemblies that have a project reference to Typhon.Engine are listed here so the
// big-bang public/internal namespace migration doesn't break friend assemblies as types
// flip from public to internal accessibility.

// Production friend assemblies
[assembly: InternalsVisibleTo("Typhon.Shell")]
[assembly: InternalsVisibleTo("Typhon.Shell.Extensibility")]
[assembly: InternalsVisibleTo("Typhon.Workbench")]
[assembly: InternalsVisibleTo("Typhon.Workbench.Fixtures")]
[assembly: InternalsVisibleTo("tsh")]

// Test friend assemblies
[assembly: InternalsVisibleTo("AntHill")]
[assembly: InternalsVisibleTo("Typhon.ARPG.Shell")]
[assembly: InternalsVisibleTo("Typhon.Benchmark")]
[assembly: InternalsVisibleTo("Typhon.Client.Tests")]
[assembly: InternalsVisibleTo("Typhon.Engine.Tests")]
[assembly: InternalsVisibleTo("Typhon.IOProfileRunner")]
[assembly: InternalsVisibleTo("Typhon.MonitoringDemo")]
[assembly: InternalsVisibleTo("Typhon.Workbench.Tests")]
