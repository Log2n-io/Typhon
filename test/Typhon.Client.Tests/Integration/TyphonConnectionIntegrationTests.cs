using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace Typhon.Client.Tests.Integration;

// ═══════════════════════════════════════════════════════════════
// Local test component types (self-contained, no Engine.Tests dependency)
// ═══════════════════════════════════════════════════════════════

[Component("Typhon.Client.Test.Position", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct ClientTestPosition
{
    public float X, Y, Z;

    public ClientTestPosition(float x, float y, float z)
    {
        X = x;
        Y = y;
        Z = z;
    }
}

[Component("Typhon.Client.Test.Health", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct ClientTestHealth
{
    public int Current, Max;

    public ClientTestHealth(int current, int max)
    {
        Current = current;
        Max = max;
    }
}

[Archetype(200)]
partial class ClientTestUnit : Archetype<ClientTestUnit>
{
    public static readonly Comp<ClientTestPosition> Position = Register<ClientTestPosition>();
    public static readonly Comp<ClientTestHealth> Health = Register<ClientTestHealth>();
}

/// <summary>
/// End-to-end integration test: real TyphonRuntime with TCP subscription server + TyphonClient SDK.
/// Verifies the full pipeline: server spawn -> subscription delta -> client cache.
/// </summary>
[NonParallelizable]
[TestFixture]
public class TyphonConnectionIntegrationTests
{
    private const int TestPort = 19890;
    private IServiceProvider _serviceProvider;
    private string _testDatabaseDir;

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        Archetype<ClientTestUnit>.Touch();
    }

    [SetUp]
    public void Setup()
    {
        _testDatabaseDir = Path.Combine(Path.GetTempPath(), "Typhon.Tests", "ClientIntegration");
        Directory.CreateDirectory(_testDatabaseDir);

        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "mm:ss.fff ";
            });
            builder.SetMinimumLevel(LogLevel.Warning);
        });
        services.AddResourceRegistry();
        services.AddMemoryAllocator();
        services.AddEpochManager();
        services.AddHighResolutionSharedTimer();
        services.AddDeadlineWatchdog();
        services.AddScopedManagedPagedMemoryMappedFile(options =>
        {
            options.DatabaseName = $"T_ClientSDK_{TestContext.CurrentContext.Test.Name}_db";
            options.DatabaseDirectory = _testDatabaseDir;
            options.DatabaseCacheSize = PagedMMF.MinimumCacheSize;
        });
        services.AddScopedDatabaseEngine(_ => { });

        _serviceProvider = services.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
    }

    [TearDown]
    public void TearDown()
    {
        (_serviceProvider as IDisposable)?.Dispose();

        if (_testDatabaseDir != null)
        {
            try
            {
                foreach (var f in Directory.GetFiles(_testDatabaseDir, "*.bin"))
                {
                    File.Delete(f);
                }
            }
            catch { /* best effort */ }
        }
    }

    private DatabaseEngine SetupEngine()
    {
        var dbe = _serviceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClientTestPosition>();
        dbe.RegisterComponentFromAccessor<ClientTestHealth>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    [Test]
    public void Client_Connects_SubscribesAndReceivesAddedEntity()
    {
        using var dbe = SetupEngine();
        using var viewTx = dbe.CreateQuickTransaction();
        var subsView = viewTx.Query<ClientTestUnit>().ToView();

        EntityId spawnedId = default;
        var spawnedOnTick = 0;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Spawner", ctx =>
            {
                if ((int)ctx.TickNumber == 2)
                {
                    var pos = new ClientTestPosition(10f, 20f, 30f);
                    var health = new ClientTestHealth(100, 100);
                    spawnedId = ctx.Transaction.Spawn<ClientTestUnit>(
                        ClientTestUnit.Position.Set(in pos),
                        ClientTestUnit.Health.Set(in health));
                    Interlocked.Exchange(ref spawnedOnTick, (int)ctx.TickNumber);
                }
            });
        }, new RuntimeOptions
        {
            WorkerCount = 1,
            BaseTickRate = 30,
            SubscriptionServer = new SubscriptionServerOptions { Port = TestPort }
        });

        var published = runtime.PublishView("test_units", subsView);
        runtime.Start();

        try
        {
            Thread.Sleep(100); // Let server start

            // Connect via SDK
            using var conn = TyphonClient.Connect("127.0.0.1", TestPort,
                new TyphonConnectionOptions { AutoReconnect = false });

            // Subscribe locally
            var sub = conn.Subscribe("test_units");
            CachedEntity addedEntity = null;
            sub.OnEntityAdded += e => Interlocked.Exchange(ref addedEntity, e);

            // Wait for server to see our connection and push subscriptions
            SpinWait.SpinUntil(() => runtime.ClientConnections.Count > 0, TimeSpan.FromSeconds(2));
            var clientConn = runtime.ClientConnections.GetAll().First();
            runtime.SetSubscriptions(clientConn.Context, published);

            // Wait for entity to spawn + arrive via SDK
            SpinWait.SpinUntil(() => spawnedOnTick > 0, TimeSpan.FromSeconds(5));
            SpinWait.SpinUntil(() => addedEntity != null, TimeSpan.FromSeconds(5));

            Assert.That(addedEntity, Is.Not.Null, "Should receive Added entity via SDK");
            Assert.That(addedEntity.Id, Is.Not.EqualTo(0));
            Assert.That(addedEntity.Components, Is.Not.Null.And.Not.Empty);
            Assert.That(sub.Entities.ContainsKey(addedEntity.Id), Is.True, "Entity should be cached");
        }
        finally
        {
            runtime.Shutdown();
        }
    }
}
