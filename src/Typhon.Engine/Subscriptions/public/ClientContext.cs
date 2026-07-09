using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Public identity of a connected client. Passed to per-client View factories.
/// </summary>
[PublicAPI]
public sealed class ClientContext
{
    /// <summary>Unique connection identifier assigned by the server.</summary>
    public int ConnectionId { get; internal init; }

    /// <summary>
    /// Game-specific player identity. Game code sets this after connection (e.g. from a connect callback); the engine
    /// treats it as an opaque payload and never inspects it.
    /// </summary>
    public object UserData { get; set; }
}
