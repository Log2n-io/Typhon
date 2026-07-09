// CS1591: this file declares public-accessibility types that live in the internal namespace (Phase 2b entanglement, see
// claude/research/PublicVsInternalApiClassification.md). They are excluded from the published API reference, so consumer-facing
// doc coverage is not enforced here.
#pragma warning disable 1591

using JetBrains.Annotations;

namespace Typhon.Engine.Internals;

[PublicAPI]
public interface IMemoryResource : IResource
{
    /// <summary>
    /// Return an estimation of the memory taken by the instance whose type is implementing the interface.
    /// </summary>
    /// <remarks>
    /// If the type has children resources, they must NOT be considered for the computation of this estimated size.
    /// </remarks>
    int EstimatedMemorySize { get; }
}