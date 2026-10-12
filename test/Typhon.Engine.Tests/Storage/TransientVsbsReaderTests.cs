using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// A buffer over a <see cref="TransientStore"/> — a transient multi-value index's — read past an empty chunk. The reader's unlink latches pages by
/// their memory page index, which the accessor computed from the persistent cache's base address: null for a transient store, so the index was garbage.
/// </summary>
public sealed class TransientVsbsReaderTests
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
    [CancelAfter(10_000)]
    public unsafe void AReaderPastAnEmptyChunk_OfATransientBuffer_ReadsTheRest()
    {
        var store = new TransientStore(new TransientOptions { MaxMemoryBytes = 64L * 1024 * 1024 }, _allocator, _epochManager, (IResource)_allocator);
        var depth = _epochManager.EnterScope();
        try
        {
            Span<int> pages = stackalloc int[4];
            store.AllocatePages(ref pages, 0, null);
            var segment = new ChunkBasedSegment<TransientStore>(_epochManager, store, 64);
            segment.Create(PageBlockType.None, StorageSegmentKind.ComponentCollection, pages);
            var vsbs = new VariableSizedBufferSegment<int, TransientStore>(segment);
            var perChunk = vsbs.ElementCountPerChunk;

            var a = segment.CreateChunkAccessor();
            int buffer;
            var chunks = new List<int>();
            try
            {
                buffer = vsbs.AllocateBuffer(ref a);
                for (var i = 1; i <= vsbs.ElementCountRootChunk + perChunk * 3; i++)
                {
                    var chunk = vsbs.AddElement(buffer, i, ref a);
                    if (chunk != buffer && !chunks.Contains(chunk))
                    {
                        chunks.Add(chunk);
                    }
                }

                Assert.That(chunks, Has.Count.EqualTo(3), "premise: three data chunks");
                var header = (VariableSizedBufferChunkHeader*)a.GetChunkAddress(chunks[1]);
                var elements = (int*)((byte*)header + sizeof(VariableSizedBufferChunkHeader));
                var values = new int[header->ElementCount];
                for (var i = 0; i < values.Length; i++)
                {
                    values[i] = elements[i];
                }

                foreach (var v in values)
                {
                    vsbs.DeleteElement(buffer, chunks[1], v, ref a);
                }
            }
            finally
            {
                a.Dispose();
            }

            var read = new List<int>();
            using (var reader = vsbs.GetReadOnlyAccessor(buffer))
            {
                do
                {
                    foreach (var v in reader.ReadOnlyElements)
                    {
                        read.Add(v);
                    }
                }
                while (reader.NextChunk());
            }

            Assert.That(read, Has.Count.EqualTo(vsbs.ElementCountRootChunk + perChunk * 2), "the elements of the other chunks");
        }
        finally
        {
            _epochManager.ExitScope(depth);
        }
    }
}
