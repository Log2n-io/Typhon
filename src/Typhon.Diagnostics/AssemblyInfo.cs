using System.Runtime.CompilerServices;

// The three providers moved here from Typhon.Engine (#409) keep their original `internal` accessibility and their
// Typhon.Engine.Internals namespace — they are engine internals that merely live in a separate, optional assembly.
// Their existing unit tests moved with them in spirit, not in location, so the engine test project stays their home.
[assembly: InternalsVisibleTo("Typhon.Engine.Tests")]
