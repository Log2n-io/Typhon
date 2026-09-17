using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// What a session is allowed to do, decided by the application's admission hook and carried on the session for the rest of its life. A command type names the
/// roles it accepts; a session outside that set never reaches the system that applies it.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="Spectator"/> is zero on purpose.</b> A role that is forgotten, defaulted or left unset must be the one that can do the least, not the one
/// that can do the most — the failure mode of the opposite choice is a client that was never admitted as a player being allowed to act as one.
/// </para>
/// <para>
/// The set is deliberately small. It is the engine's authorization axis, not the application's: a game with guild officers, GMs and bots expresses those in
/// its own data and keeps this to "may this connection change the world".
/// </para>
/// </remarks>
[PublicAPI]
public enum SessionRole
{
    /// <summary>Watches, sends no world-changing command. Tools, viewer bots and god cameras.</summary>
    Spectator = 0,

    /// <summary>Controls an entity and sends intents for it.</summary>
    Player = 1,
}
