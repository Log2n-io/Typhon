using System;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// Two CLR types deliberately sharing the SAME durable schema identity ("Typhon.Test.ModeLock", revision 1) but declaring DIFFERENT StorageModes. This is the
// illegal state rules/ecs.md ARCH-01 forbids: StorageMode is fixed for a given (component, revision). Re-opening a database that persisted the component one way
// while the code now declares it the other way must fail loudly (changing the mode requires a revision bump), never silently reinterpret persisted bytes.
[Component("Typhon.Test.ModeLock", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential)]
struct ModeLockVersioned
{
    public int V;
    public int W; // pad — a component's storage must total >= 8 bytes
    public ModeLockVersioned(int v) { V = v; W = 0; }
}

[Component("Typhon.Test.ModeLock", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct ModeLockSingleVersion
{
    public int V;
    public int W;
    public ModeLockSingleVersion(int v) { V = v; W = 0; }
}

[TestFixture]
class StorageModeRevisionLockTests : TestBase<StorageModeRevisionLockTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    [Test]
    public void Reopen_SameRevision_DifferentStorageMode_Throws()
    {
        // Session 1 — create the DB persisting "Typhon.Test.ModeLock" v1 as Versioned.
        using (var scope = ServiceProvider.CreateScope())
        using (var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>())
        {
            Assert.That(dbe.RegisterComponentFromAccessor<ModeLockVersioned>(), Is.True);
            dbe.InitializeArchetypes();
        }

        // Session 2 — reopen the SAME database, now declaring the same (name, revision) as SingleVersion. Must throw, not silently reinterpret.
        using (var scope2 = ServiceProvider.CreateScope())
        using (var dbe2 = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>())
        {
            var ex = Assert.Throws<InvalidOperationException>(() => dbe2.RegisterComponentFromAccessor<ModeLockSingleVersion>());
            Assert.That(ex.Message, Does.Contain("StorageMode").And.Contain("revision"),
                "the error must explain the (name, revision) mode lock and point at a revision bump");
        }
    }
}
