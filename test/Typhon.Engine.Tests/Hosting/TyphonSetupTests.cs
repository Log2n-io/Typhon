using System;
using System.IO;
using System.Numerics;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

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

    // #506 item 3: AddTyphon must NOT require the caller to call AddLogging() first. The engine's storage/engine services
    // take a required ILogger<T>; without a logging backend, resolving DatabaseEngine used to throw an opaque
    // "No service for type 'ILogger<PagedMMF>'". AddTyphon now self-heals via a no-op AddLogging() (registered with TryAdd,
    // so a caller who DID configure logging is unaffected). Note: intentionally no services.AddLogging() below.
    [Test]
    public void AddTyphon_WithoutAddLogging_StillResolvesWorkingEngine()
    {
        var services = new ServiceCollection();
        services.AddTyphon(options =>
        {
            options.DatabaseFile(DbPath("nologtest")).Register<CompA>().RegisterArchetype<CompAArch>();
            ConfigureWalForTest(options);
        });

        using var provider = services.BuildServiceProvider();

        DatabaseEngine dbe = null;
        Assert.That(() => dbe = provider.GetRequiredService<DatabaseEngine>(), Throws.Nothing,
            "AddTyphon must register a no-op logging backend so DatabaseEngine resolves without an explicit AddLogging() call (#506).");
        Assert.That(dbe, Is.Not.Null);
        AssertCompARoundTrips(dbe);
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

    // §11 / 6a — a throw from an engine teardown step must NOT leak the owned ServiceProvider. Dispose() runs teardown in a
    // try and releases the owned container in a finally, so a mid-teardown failure (here forced via ThrowInDisposeCoreForTest,
    // modelling e.g. a full disk during the final checkpoint) still disposes the provider while the original error propagates.
    [Test]
    public void Open_TeardownThrows_StillDisposesOwnedProvider()
    {
        var ownedProviderField = typeof(DatabaseEngine).GetField("_ownedProvider", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.That(ownedProviderField, Is.Not.Null, "test depends on DatabaseEngine._ownedProvider");

        var dbe = DatabaseEngine.Open(DbPath("teardownthrow"), options =>
        {
            options.Register<CompA>().RegisterArchetype<CompAArch>();
            ConfigureWalForTest(options);
        });

        // Capture the owned provider before Dispose() nulls it in its finally.
        var ownedProvider = (ServiceProvider)ownedProviderField.GetValue(dbe);
        Assert.That(ownedProvider, Is.Not.Null, "Open() must attach the owned provider");

        dbe.ThrowInDisposeCoreForTest = true;

        // The teardown exception must still surface to the caller (the finally does not swallow it)...
        Assert.Throws<InvalidOperationException>(() => dbe.Dispose());

        // ...yet the owned provider must have been disposed anyway, so its singletons (watchdog/timer threads, allocator)
        // don't leak. Resolving from a disposed provider throws.
        Assert.Throws<ObjectDisposedException>(
            () => ownedProvider.GetService(typeof(IResourceRegistry)),
            "a mid-teardown throw must not leak the owned ServiceProvider — Dispose() disposes it in a finally");
    }

    // The one-line setup can configure the spatial grid: Open() applies ConfigureSpatialGrid in the pre-InitializeArchetypes
    // window it otherwise closes. A spatial archetype REQUIRES a grid (InitializeArchetypes throws without one), so a clean
    // Open + a working spatial query proves the grid landed — no dropping to the manual Add* chain.
    [Test]
    public void Open_WithSpatialArchetype_ConfiguresGridThroughOpen()
    {
        using var dbe = DatabaseEngine.Open(DbPath("spatial"), options =>
        {
            options
                .Register<SetupSpatialBox>()
                .RegisterArchetype<SetupSpatialArch>()
                .ConfigureSpatialGrid(new SpatialGridConfig(Vector2.Zero, new Vector2(1000f, 1000f), cellSize: 50f));
            ConfigureWalForTest(options);
        });

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<SetupSpatialArch>(SetupSpatialArch.Box.Set(new SetupSpatialBox { Box = Aabb(100f, 100f) }));
            tx.Spawn<SetupSpatialArch>(SetupSpatialArch.Box.Set(new SetupSpatialBox { Box = Aabb(900f, 900f) }));
            tx.Commit();
        }

        dbe.WriteTickFence(1);   // outside the runtime, refresh the spatial index once so WhereNearby can filter

        using (var tx = dbe.CreateQuickTransaction())
        {
            int near = tx.Query<SetupSpatialArch>().WhereNearby<SetupSpatialBox>(100f, 100f, 0f, 50f).Count();
            Assert.That(near, Is.EqualTo(1),
                "the grid was configured through Open() — exactly one of the two entities is within 50 units of (100,100)");
        }
    }

    private static AABB2F Aabb(float x, float y) => new AABB2F { MinX = x, MaxX = x, MinY = y, MaxY = y };

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

// Spatial component + cluster-eligible archetype for the ConfigureSpatialGrid-through-Open test. Mirrors the guide model
// (a SingleVersion component carrying a [SpatialIndex] AABB). A spatial archetype REQUIRES a grid, so a successful Open
// proves ConfigureSpatialGrid landed in the pre-InitializeArchetypes window. Archetype id 212 avoids the 200-211 range.
[Component("Typhon.Schema.SetupTest.SpatialBox", 1, StorageMode = StorageMode.SingleVersion)]
public struct SetupSpatialBox
{
    [SpatialIndex(2f)] public AABB2F Box;
}

[Archetype(212)]
class SetupSpatialArch : Archetype<SetupSpatialArch>
{
    public static readonly Comp<SetupSpatialBox> Box = Register<SetupSpatialBox>();
}
