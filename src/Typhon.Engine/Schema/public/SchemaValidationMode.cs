using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Controls how schema mismatches are handled during component registration.
/// </summary>
[PublicAPI]
public enum SchemaValidationMode
{
    /// <summary>Throw <see cref="SchemaValidationException"/> on breaking changes (default).</summary>
    Enforce,

    /// <summary>Skip validation — UNSAFE, may cause data corruption if layout changed.</summary>
    Skip,
}
