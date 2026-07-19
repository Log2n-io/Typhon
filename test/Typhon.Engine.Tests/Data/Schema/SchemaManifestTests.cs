using System;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// Components/archetype used to exercise the persisted schema-assembly manifest (AssemblyR1 + ComponentR1/ArchetypeR1.AssemblyId).
// They live in the Typhon.Engine.Tests assembly — NOT the core Typhon.Engine assembly — so they must produce a manifest row.

[Component("Typhon.Schema.UnitTest.ManifestX", 1)]
[StructLayout(LayoutKind.Sequential)]
struct ManifestX
{
    public int V;
    public float F;
}

[Component("Typhon.Schema.UnitTest.ManifestY", 1)]
[StructLayout(LayoutKind.Sequential)]
struct ManifestY
{
    public int W;
    public float G;
}

[Archetype(880)]
class ManifestArch : Archetype<ManifestArch>
{
    public static readonly Comp<ManifestX> X = Register<ManifestX>();
    public static readonly Comp<ManifestY> Y = Register<ManifestY>();
}

[TestFixture]
[NonParallelizable]
class SchemaManifestTests : TestBase<SchemaManifestTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        Archetype<ManifestArch>.Touch();
    }

    [Test]
    public void Manifest_RecordsDeclaringAssemblyOnce_AndExcludesCore()
    {
        // Session 1 — create, register two components + an archetype from the test assembly, spawn.
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<ManifestX>();
            dbe.RegisterComponentFromAccessor<ManifestY>();
            dbe.InitializeArchetypes();

            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            t.Spawn<ManifestArch>(ManifestArch.X.Set(new ManifestX { V = 1 }), ManifestArch.Y.Set(new ManifestY { W = 2 }));
            t.Commit();
        }

        // Session 2 — reopen and read the manifest with NO schema registration (it must be available schemaless).
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();

            var required = dbe.GetRequiredAssemblies();
            var testAsm = typeof(ManifestX).Assembly.GetName();
            var names = required.Select(a => a.Name).ToArray();

            Assert.That(names, Does.Contain(testAsm.Name), "test assembly must be recorded in the manifest");
            Assert.That(names.Count(n => n == testAsm.Name), Is.EqualTo(1), "two components + archetype from one assembly must dedup to a single manifest row");
            Assert.That(names, Does.Not.Contain(typeof(DatabaseEngine).Assembly.GetName().Name), "core engine assembly must be excluded from the manifest");

            // Version round-trips (build/revision are clamped to >= 0 at write time).
            var recorded = required.First(a => a.Name == testAsm.Name).Version;
            Assert.That(recorded.Major, Is.EqualTo(testAsm.Version.Major));
            Assert.That(recorded.Minor, Is.EqualTo(testAsm.Version.Minor));
        }
    }

    [Test]
    public void AssemblyId_LinksComponentAndArchetypeToManifestRow_SystemComponentsAreCore()
    {
        // Session 1 — persist.
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<ManifestX>();
            dbe.RegisterComponentFromAccessor<ManifestY>();
            dbe.InitializeArchetypes();

            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            t.Spawn<ManifestArch>(ManifestArch.X.Set(new ManifestX { V = 1 }), ManifestArch.Y.Set(new ManifestY { W = 2 }));
            t.Commit();
        }

        // Session 2 — reopen, re-register (to load persisted archetypes), assert the links from disk.
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<ManifestX>();
            dbe.RegisterComponentFromAccessor<ManifestY>();
            dbe.InitializeArchetypes();

            var testAsmName = typeof(ManifestX).Assembly.GetName().Name;

            // User component → non-zero AssemblyId pointing at the manifest row for the test assembly.
            var compX = dbe._persistedComponents["Typhon.Schema.UnitTest.ManifestX"].Comp;
            Assert.That(compX.AssemblyId, Is.Not.Zero, "user component must carry a manifest AssemblyId");
            Assert.That(dbe._persistedAssemblies[compX.AssemblyId].Asm.SimpleName.AsString, Is.EqualTo(testAsmName));

            // Archetype → same row (persisted archetypes are keyed by name).
            var arch = dbe._persistedArchetypes[typeof(ManifestArch).Name].Arch;
            Assert.That(arch.AssemblyId, Is.EqualTo(compX.AssemblyId), "archetype and its components from one assembly must share a manifest row");

            // System component (declared in core Typhon.Engine) → AssemblyId 0, not in the manifest.
            var compR1 = dbe._persistedComponents[ComponentR1.SchemaName].Comp;
            Assert.That(compR1.AssemblyId, Is.Zero, "core system components must not get a manifest row");
        }
    }
}
