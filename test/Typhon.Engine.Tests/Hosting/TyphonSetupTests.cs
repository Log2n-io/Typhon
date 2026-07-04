using System;
using System.IO;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// Covers the simplified-setup surface (#147): <see cref="ServiceCollectionExtensions.AddTyphon"/>, the
/// <see cref="DatabaseEngine.Open(string,System.Action{TyphonOptions},Microsoft.Extensions.Logging.ILoggerFactory)"/>
/// factory, and <see cref="TyphonOptions.Register{T}"/> component wiring.
/// </summary>
/// <remarks>
/// Unlike <c>TestBase&lt;T&gt;</c>, these tests exercise the real batteries-included graph (real <c>WalFileIO</c> + on-disk
/// paged file) — that IS the code under test. Each test runs in its own temp directory with the WAL routed inside it, and
/// the directory is removed on teardown. <see cref="NonParallelizableAttribute"/> because the graph touches real disk and
/// the global archetype registry.
/// </remarks>
[NonParallelizable]
public class TyphonSetupTests
{
    private string _dir;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "Typhon.Tests", nameof(TyphonSetupTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup — a lingering pending-delete handle on Windows must not fail the test.
        }
    }

    // The .typhon path a test hands to DatabaseFile()/Open(). The engine currently materialises it as "{stem}.bin".
    private string DbPath(string stem) => Path.Combine(_dir, stem + ".typhon");

    // Leave WalDirectory unset so the engine derives {bundle}/wal (the bundle-format default); just turn FUA off so a
    // one-entity commit doesn't pay a synchronous fsync.
    private void ConfigureWalForTest(TyphonOptions options) => options.ConfigureEngine(engine =>
    {
        engine.Wal.UseFUA = false;
    });

    // AC1/AC2/AC4 — AddTyphon composes a working engine and its Register<T> is applied by the post-build hook.
    [Test]
    public void AddTyphon_DiPath_ResolvesWorkingEngine_WithRegisteredComponent()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTyphon(options =>
        {
            options.DatabaseFile(DbPath("ditest")).Register<CompA>().RegisterArchetype<CompAArch>();
            ConfigureWalForTest(options);
        });

        using var provider = services.BuildServiceProvider();
        var dbe = provider.GetRequiredService<DatabaseEngine>();

        Assert.That(dbe, Is.Not.Null);
        AssertCompARoundTrips(dbe); // proves CompA was registered by AddTyphon's descriptor decoration
    }

    // AC3 — Open() returns a usable engine that OWNS its private ServiceProvider and disposes it on Dispose. The proof is
    // that the *provider* is disposed — asserting only the .bin handle would be a false positive, because the engine's own
    // teardown disposes the MMF regardless of the owned-provider logic. What the owned-provider disposal uniquely protects
    // are the singletons the engine never touches itself (EpochManager, watchdog + timer threads, allocator).
    [Test]
    public void Open_FactoryPath_OwnsAndDisposesItsPrivateProvider()
    {
        var ownedProviderField = typeof(DatabaseEngine).GetField("_ownedProvider", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.That(ownedProviderField, Is.Not.Null, "test depends on DatabaseEngine._ownedProvider");

        ServiceProvider ownedProvider;
        var dbe = DatabaseEngine.Open(DbPath("opentest"), options =>
        {
            options.Register<CompA>().RegisterArchetype<CompAArch>();
            ConfigureWalForTest(options);
        });
        try
        {
            AssertCompARoundTrips(dbe);

            ownedProvider = (ServiceProvider)ownedProviderField.GetValue(dbe);
            Assert.That(ownedProvider, Is.Not.Null, "Open() must attach the owned provider");
            // Live before dispose: the engine graph really resolves from this provider.
            Assert.That(ownedProvider.GetService(typeof(IResourceRegistry)), Is.Not.Null);
        }
        finally
        {
            dbe.Dispose();
        }

        // The engine's Dispose must have disposed the owned provider — resolving from a disposed provider throws.
        Assert.Throws<ObjectDisposedException>(
            () => ownedProvider.GetService(typeof(IResourceRegistry)),
            "Dispose() must dispose the owned ServiceProvider (else its singletons — watchdog/timer threads, allocator — leak)");

        // Secondary: the bundle's data file was created (game.typhon/data) and its handle released (MMF disposed).
        var dataFile = Path.Combine(_dir, "opentest.typhon", "data");
        Assert.That(File.Exists(dataFile), Is.True, "Open() should have created the bundle's data file");
        Assert.DoesNotThrow(() => File.Delete(dataFile));
    }

    // AC5 (multi-register) — several Register<T> calls all take effect (distinct closed generics).
    [Test]
    public void AddTyphon_MultipleRegister_AllComponentsUsable()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTyphon(options =>
        {
            options.DatabaseFile(DbPath("multi")).Register<EcsPosition>().Register<EcsVelocity>().RegisterArchetype<EcsUnit>();
            ConfigureWalForTest(options);
        });

        using var provider = services.BuildServiceProvider();
        var dbe = provider.GetRequiredService<DatabaseEngine>();

        EntityId id;
        var pos = new EcsPosition(1f, 2f, 3f);
        var vel = new EcsVelocity(4f, 5f, 6f);
        using (var t = dbe.CreateQuickTransaction())
        {
            id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
            t.Commit();
        }

        using (var rt = dbe.CreateQuickTransaction())
        {
            var accessor = rt.Open(id);
            Assert.That(accessor.Read(EcsUnit.Position).X, Is.EqualTo(1f));
            Assert.That(accessor.Read(EcsUnit.Velocity).Dz, Is.EqualTo(6f));
        }
    }

    // AC5 (regression) — the power-user manual Add* chain is unaffected by AddTyphon (no decoration, no TyphonOptions).
    [Test]
    public void PowerUser_ManualAddStarChain_StillBuildsEngine()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddHighResolutionSharedTimer()
            .AddDeadlineWatchdog()
            .AddManagedPagedMMF(o =>
            {
                o.DatabaseName = "poweruser";
                o.DatabaseDirectory = _dir;
            })
            .AddDatabaseEngine(e =>
            {
                e.Wal.WalDirectory = Path.Combine(_dir, "wal");
                e.Wal.UseFUA = false;
            });

        using var provider = services.BuildServiceProvider();
        var dbe = provider.GetRequiredService<DatabaseEngine>();

        Assert.That(dbe, Is.Not.Null);

        // The power-user path owns the full lifecycle itself: register components, touch archetypes, then
        // InitializeArchetypes. AddTyphon's whole value is doing exactly this for you (see the AddTyphon tests, which never
        // call InitializeArchetypes or Touch).
        dbe.RegisterComponentFromAccessor<CompA>();
        Archetype<CompAArch>.Touch();
        dbe.InitializeArchetypes();
        AssertCompARoundTrips(dbe);
    }

    // Spawn one CompA entity, commit, then read it back in a fresh transaction. Proves the component is registered and the
    // engine is fully functional.
    private static void AssertCompARoundTrips(DatabaseEngine dbe)
    {
        EntityId id;
        var a = new CompA(42);
        using (var t = dbe.CreateQuickTransaction())
        {
            id = t.Spawn<CompAArch>(CompAArch.A.Set(in a));
            t.Commit();
        }

        using (var rt = dbe.CreateQuickTransaction())
        {
            var read = rt.Open(id).Read(CompAArch.A);
            Assert.That(read.A, Is.EqualTo(42));
        }
    }
}
