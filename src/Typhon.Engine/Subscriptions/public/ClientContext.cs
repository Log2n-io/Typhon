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
    /// Game-specific player identity. Set by game code via <see cref="TyphonRuntime.SetClientData{T}"/> (future).
    /// For v1, game code sets this after connection via callback.
    /// </summary>
    public object UserData { get; set; }
}
