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