using JetBrains.Annotations;
using System;
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

    [SetUp]
    public virtual void Setup()
    {
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
    }
}
