// Global usings for the engine assembly.
//
// Engine source code freely uses both namespaces — this file makes both visible everywhere
// without requiring per-file `using` directives. Public-vs-internal discipline is enforced
// by the InternalApiLeakAnalyzer (TYPHON008), not by per-file using lists.
//
// See claude/research/PublicVsInternalApiClassification.md §3.1 (folder/namespace decoupling).

global using Typhon.Engine.Internals;

// `namespace Typhon.Engine.Profiler` holds the TraceEventGenerator's attribute surface
// (Profiler/public/TraceEventAttributes.cs). All event payload structs in `Profiler/internals/` carry
// `[TraceEvent(...)]`, and engine code that calls the generated factories lives across many files.
// The global using brings the namespace in everywhere so engine code compiles without per-file usings.
global using Typhon.Engine.Profiler;
