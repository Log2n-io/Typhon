using JetBrains.Annotations;
using System;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Serilog;

namespace Typhon.Engine.Tests;

/// <summary>
/// Base class for allocator tests that need IResourceRegistry and IMemoryAllocator.
/// </summary>
[PublicAPI]
public abstract class AllocatorTestBase
{
    protected IServiceProvider ServiceProvider { get; private set; }
    protected IResourceRegistry ResourceRegistry => ServiceProvider.GetRequiredService<IResourceRegistry>();
    private protected IMemoryAllocator MemoryAllocator => ServiceProvider.GetRequiredService<IMemoryAllocator>();
    protected IResource AllocationResource => ResourceRegistry.Allocation;

    /// <summary>
    /// Per-fixture isolated database directory. Fixtures that build their own MMF must place it here rather than directly in <see cref="Path.GetTempPath"/>:
    /// under parallel execution dozens of fixtures create/open/delete DB bundles at once, and sharing the temp root produces "file used by another process"
    /// contention. A per-fixture subdirectory keeps each fixture's I/O isolated (fixtures parallelize; tests within a fixture run serially).
    /// </summary>
    protected string TestDatabaseDir { get; private set; }

    [SetUp]
    public virtual void Setup()
    {
        TestDatabaseDir = Path.Combine(Path.GetTempPath(), "Typhon.Tests", GetType().Name);
        Directory.CreateDirectory(TestDatabaseDir);

        var config = new LoggerConfiguration()
            .MinimumLevel.Warning()
            .Enrich.FromLogContext();

        Log.Logger = config.CreateLogger();

        var serviceCollection = new ServiceCollection();
        serviceCollection
            .AddLogging(builder =>
            {
                builder.AddSerilog();
                builder.SetMinimumLevel(LogLevel.Warning);
            })
            .AddResourceRegistry()
            .AddMemoryAllocator();

        ServiceProvider = serviceCollection.BuildServiceProvider();
    }

    [TearDown]
    public virtual void TearDown()
    {
        Log.CloseAndFlush();
        if (ServiceProvider is IDisposable disposable)
        {
            disposable.Dispose();
        }

        if (TestDatabaseDir != null)
        {
            try { Directory.Delete(TestDatabaseDir, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }
}
