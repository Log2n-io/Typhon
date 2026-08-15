using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace Typhon.Shell.Tests.SchemaFixture;

/// <summary>
/// Two <c>char</c>s then an <c>int</c> — the shape whose managed and marshalled layouts differ without differing in size. Managed <c>A@0 B@2 C@4</c>;
/// <c>Marshal.OffsetOf</c> reports <c>A@0 B@1 C@4</c>. Both total 8 bytes.
/// </summary>
/// <remarks>
/// This assembly exists to be opened by <c>AssemblySchemaLoader</c> as a foreign schema assembly, so B's offset is the single value that says which branch
/// the loader took: 2 means it read the source-generated spec, 1 means it reflected (#819).
/// </remarks>
[Component("Typhon.Shell.Fixture.ForeignCharPair", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct ForeignCharPair
{
    [Field] public char A;
    [Field] public char B;
    [Field] public int C;
}
