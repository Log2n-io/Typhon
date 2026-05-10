using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>Status codes for B+Tree key lookups.</summary>
[PublicAPI]
public enum BTreeLookupStatus : byte
{
    /// <summary>Key found, value returned.</summary>
    Success = 0,

    /// <summary>Key does not exist in the tree.</summary>
    NotFound = 1,
}
