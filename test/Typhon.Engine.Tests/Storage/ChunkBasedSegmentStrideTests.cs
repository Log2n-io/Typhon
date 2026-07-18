using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;

namespace Typhon.Engine.Tests;

/// <summary>
/// #506 item 1: a chunk-based segment requires a stride of at least 8 bytes (chunk 0 is a reserved sentinel and free
/// chunks store an 8-byte in-place link). The reworded error must be actionable — naming the 8-byte minimum and the
/// public-field remedy — instead of the old opaque "Invalid stride size, given 4, but must be at least 8 bytes".
/// </summary>
public sealed class ChunkBasedSegmentStrideTests
{
    private IServiceProvider _serviceProvider;
    private IMemoryAllocator _allocator;
    private EpochManager _epochManager;

    [SetUp]
    public void Setup()
    {
        var services = new ServiceCollection();
        services
            .AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager();

        _serviceProvider = services.BuildServiceProvider();
        _allocator = _serviceProvider.GetRequiredService<IMemoryAllocator>();
        _epochManager = _serviceProvider.GetRequiredService<EpochManager>();
    }

    [TearDown]
    public void TearDown() => (_serviceProvider as IDisposable)?.Dispose();

    [Test]
    public void ChunkBasedSegment_StrideBelow8Bytes_ThrowsActionableError()
    {
        var parent = (IResource)_allocator;
        var options = new TransientOptions { PagesPerBlock = 1, MaxMemoryBytes = 64L * 1024 * 1024 };
        var store = new TransientStore(options, _allocator, _epochManager, parent);

        // A single 4-byte int is the canonical trigger (a Versioned component with one public int).
        var ex = Assert.Catch(() => new ChunkBasedSegment<TransientStore>(_epochManager, store, stride: 4));

        Assert.That(ex, Is.Not.Null, "a sub-8-byte stride must be rejected");
        Assert.That(ex.Message, Does.Contain("8 bytes"), "the error must state the 8-byte minimum");
        Assert.That(ex.Message, Does.Contain("public"),
            "the error must point at the public-field remedy (private padding does not count) — #506 item 1");
    }
}
