// Friend-assembly access to engine internals.
//
// The Typhon.Engine assembly grants Typhon.Engine.Tests visibility into Typhon.Engine.Internals
// via [InternalsVisibleTo] in its AssemblyInfo. This global using makes those internal types
// visible everywhere in the test project without per-file `using` directives.
//
// See claude/research/PublicVsInternalApiClassification.md §3.1 + §8.

global using Typhon.Engine.Internals;
