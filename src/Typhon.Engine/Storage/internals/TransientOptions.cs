using JetBrains.Annotations;

namespace Typhon.Engine.Internals;

/// <summary>
/// Configuration for transient (heap-backed) page stores.
/// Transient pages are allocated from pinned memory blocks via <see cref="IMemoryAllocator"/>,
/// never persisted to disk, and lost on process exit or crash.
/// </summary>
[PublicAPI]
public class TransientOptions
{
    /// <summary>
    /// Hard memory cap for transient page allocation. When exceeded, <see cref="TransientStore.AllocatePages"/>
    /// throws <see cref="InsufficientMemoryException"/>. Default: 256 MB.
    /// </summary>
    public long MaxMemoryBytes { get; set; } = 256 * 1024 * 1024;

    /// <summary>
    /// Number of 8 KB pages per allocation block. Each block is a single
    /// <see cref="IMemoryAllocator.AllocatePinned"/> call. Larger values reduce allocation
    /// overhead but waste memory for small stores. Default: 32 (256 KB per block).
    /// </summary>
    public int PagesPerBlock { get; set; } = 32;
}
