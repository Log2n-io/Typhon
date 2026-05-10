using JetBrains.Annotations;
using System;
using System.Buffers;
using System.Collections.Generic;

namespace Typhon.Engine.Internals;

[PublicAPI]
public abstract class MemoryBlockBase : MemoryManager<byte>, IMemoryResource, IDebugPropertiesProvider
{
    public MemoryAllocator Allocator { get; }
    public abstract int EstimatedMemorySize { get; }
    public abstract int MemoryBlockSize { get; }
    public abstract bool IsDisposed { get; }
    public abstract Span<byte> DataAsSpan { get; }
    public abstract Memory<byte> DataAsMemory { get; }

    /// <summary>
    /// Interned call-site tag (see <see cref="Profiler.MemoryAllocSource"/>). Stored so the symmetric free-side
    /// <see cref="Profiler.TraceEventKind.MemoryAllocEvent"/> can carry the same tag as its alloc pair. <c>0</c> means unattributed.
    /// </summary>
    public ushort SourceTag { get; }

    protected override void Dispose(bool disposing)
    {
        Allocator.Remove(this);
        Parent.RemoveChild(this);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    public string Id { get; }
    public string Name => Id;
    public int? Count => null;
    public ResourceType Type => ResourceType.Memory;
    public IResource Parent { get; }
    public abstract IEnumerable<IResource> Children { get; }
    public DateTime CreatedAt { get; }
    public IResourceRegistry Owner { get; }
    public bool RegisterChild(IResource child) => false;
    public bool RemoveChild(IResource resource) => false;

    /// <inheritdoc />
    public abstract IReadOnlyDictionary<string, object> GetDebugProperties();

    protected MemoryBlockBase(MemoryAllocator allocator, string id, IResource parent, ushort sourceTag = 0)
    {
        Allocator = allocator ?? throw new ArgumentNullException(nameof(allocator), "Memory allocator cannot be null");
        Parent = parent ?? throw new ArgumentNullException(nameof(parent), "Parent resource cannot be null. Resources must have an explicit parent.");
        Id = id ?? throw new ArgumentNullException(nameof(id), "Resource ID cannot be null");
        SourceTag = sourceTag;
        CreatedAt = DateTime.UtcNow;
        Owner = Parent.Owner;
        Parent.RegisterChild(this);
    }
}