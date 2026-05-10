// Global usings for the engine assembly.
//
// Engine source code freely uses both namespaces — this file makes both visible everywhere
// without requiring per-file `using` directives. Public-vs-internal discipline is enforced
// by the InternalApiLeakAnalyzer (TYPHON008), not by per-file using lists.
//
// See claude/research/PublicVsInternalApiClassification.md §3.1 (folder/namespace decoupling).

global using Typhon.Engine.Internals;
