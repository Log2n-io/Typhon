using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Proactive system that runs every tick. Used for non-entity work: timers, input draining, global state.
/// Derive from this class, implement <see cref="Configure"/> and <see cref="Execute"/>.
/// </summary>
[PublicAPI]
public abstract class CallbackSystem
{
    /// <summary>Declare the system's name, dependencies, and configuration.</summary>
    protected abstract void Configure(SystemBuilder b);

    /// <summary>Execute the system's logic for this tick.</summary>
    protected abstract void Execute(TickContext ctx);

    internal static void InvokeConfigure(CallbackSystem system, SystemBuilder b) => system.Configure(b);
    internal static void InvokeExecute(CallbackSystem system, TickContext ctx) => system.Execute(ctx);
}
