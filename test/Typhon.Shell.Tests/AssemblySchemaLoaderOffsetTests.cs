using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Typhon.Schema.Definition;
using Typhon.Shell.Schema;

namespace Typhon.Shell.Tests;

/// <summary>
/// Two <c>char</c>s then an <c>int</c>, declared in THIS assembly so the test can hand its own DLL to the loader as an external schema assembly. Managed
/// layout is <c>A@0 B@2 C@4</c>; the marshalled layout that <c>Marshal.OffsetOf</c> reports is <c>A@0 B@1 C@4</c>. Both are 8 bytes, so only the offsets give
/// the divergence away.
/// </summary>
[Component("Typhon.Shell.Test.CharPair", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct LoaderCharPair
{
    [Field] public char A;
    [Field] public char B;
    [Field] public int C;
}

/// <summary>All-wide fields — the two layouts coincide, so this one pins that the fix does not disturb the ordinary case.</summary>
[Component("Typhon.Shell.Test.Plain", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct LoaderPlain
{
    [Field] public long A;
    [Field] public int B;
}

/// <summary>
/// #819: <see cref="AssemblySchemaLoader"/> reads a component's field offsets out of a compiled assembly with <c>Marshal.OffsetOf</c> — the MARSHALLED
/// layout — while every engine accessor reads the MANAGED one. An assembly compiled with Typhon's source generator already carries a spec with managed
/// offsets, registered by its own <c>[ModuleInitializer]</c>; the loader never asks for it.
/// <para>
/// This assembly references the generator (see its csproj), so the fixtures above have generated specs and loading this DLL is a faithful stand-in for
/// <c>typhon check --schema &lt;dll&gt;</c> pointed at a properly-built schema assembly.
/// </para>
/// </summary>
[TestFixture]
public class AssemblySchemaLoaderOffsetTests
{
    private static ComponentSchema Load(string schemaName)
    {
        // Deliberately does NOT touch the fixture types first. This assembly's module initializer has already been run by the test host, which would mask
        // whether the loader forces it — the point of loading a foreign assembly in FromAnotherAssembly_* below.
        var (_, components) = AssemblySchemaLoader.LoadAssembly(typeof(LoaderCharPair).Assembly.Location);
        var match = components.FirstOrDefault(c => c.Schema.Name == schemaName);
        Assert.That(match.Schema, Is.Not.Null, $"loader did not return a schema named '{schemaName}'");
        return match.Schema;
    }

    [Test]
    public void LoadAssembly_CharPair_ReportsManagedOffsets()
    {
        // B is the tell: 2 in the layout the engine reads, 1 in the layout Marshal.OffsetOf describes. A loader that reflects reports 1 and every downstream
        // field decode is a byte off, with the totals matching so nothing notices.
        var schema = Load("Typhon.Shell.Test.CharPair");

        Assert.Multiple(() =>
        {
            Assert.That(schema.Fields.Single(f => f.Name == "A").Offset, Is.EqualTo(0), "A");
            Assert.That(schema.Fields.Single(f => f.Name == "B").Offset, Is.EqualTo(2), "B — 2 managed, 1 marshalled");
            Assert.That(schema.Fields.Single(f => f.Name == "C").Offset, Is.EqualTo(4), "C");
        });
    }

    [Test]
    public void LoadAssembly_PlainComponent_IsUnaffected()
    {
        // Both branches agree on this shape, so it cannot say which one ran — it is here to catch the generated branch mis-describing an ordinary component,
        // which the char cases would not notice.
        var schema = Load("Typhon.Shell.Test.Plain");

        Assert.Multiple(() =>
        {
            Assert.That(schema.Fields.Single(f => f.Name == "A").Offset, Is.EqualTo(0), "A");
            Assert.That(schema.Fields.Single(f => f.Name == "B").Offset, Is.EqualTo(8), "B");
            Assert.That(schema.Fields.Single(f => f.Name == "A").Size, Is.EqualTo(8), "A size");
            Assert.That(schema.Fields.Single(f => f.Name == "B").Size, Is.EqualTo(4), "B size");
        });
    }

    [Test]
    public void LoadAssembly_CharPair_ReportsManagedSizes()
    {
        // The other axis of the same bug: pairing a managed OFFSET with a marshalled SIZE describes a char as occupying one byte at a correct offset.
        // Marshal.SizeOf(typeof(char)) is 1 and bool is 4; the managed widths are 2 and 1.
        var schema = Load("Typhon.Shell.Test.CharPair");

        Assert.Multiple(() =>
        {
            Assert.That(schema.Fields.Single(f => f.Name == "A").Size, Is.EqualTo(2), "A — 2 managed, 1 marshalled");
            Assert.That(schema.Fields.Single(f => f.Name == "B").Size, Is.EqualTo(2), "B");
            Assert.That(schema.Fields.Single(f => f.Name == "C").Size, Is.EqualTo(4), "C");
        });
    }

    // ═══════════════════════════════════════════════════════════════════════
    // A genuinely foreign assembly — nothing here has touched its module init
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void ForeignAssembly_CharPair_ReportsManagedOffsets()
    {
        // Typhon.Shell.Tests.SchemaFixture is built with the generator and copied beside these tests, but deliberately NOT referenced, so the runtime has had
        // no reason to run its [ModuleInitializer] and its spec is absent from the registry until the loader forces it. That makes this the only test in the
        // fixture that can fail if the loader stops calling RuntimeHelpers.RunModuleConstructor — the same-assembly cases pass either way, because the test
        // host already ran their initializer.
        var path = Path.Combine(AppContext.BaseDirectory, "Typhon.Shell.Tests.SchemaFixture.dll");
        Assert.That(File.Exists(path), Is.True, $"foreign schema fixture missing at '{path}' — check the csproj copy target");

        var (_, components) = AssemblySchemaLoader.LoadAssembly(path);
        var schema = components.Single(c => c.Schema.Name == "Typhon.Shell.Fixture.ForeignCharPair").Schema;

        Assert.Multiple(() =>
        {
            Assert.That(schema.Fields.Single(f => f.Name == "A").Offset, Is.EqualTo(0), "A");
            Assert.That(schema.Fields.Single(f => f.Name == "B").Offset, Is.EqualTo(2), "B — 2 managed, 1 marshalled; this is the branch discriminator");
            Assert.That(schema.Fields.Single(f => f.Name == "C").Offset, Is.EqualTo(4), "C");
        });
    }
}
