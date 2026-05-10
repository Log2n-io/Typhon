using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Reactive system that processes entities from a View. Auto-skips when no filtered components were written and event queues are empty.
/// Supports optional <see cref="SystemBuilder.Parallel"/> mode for automatic chunking across workers.
/// Derive from this class, implement <see cref="Configure"/> and <see cref="Execute"/>.
/// </summary>
[PublicAPI]
public abstract class QuerySystem
{
    /// <summary>Declare the system's name, dependencies, input View, and configuration.</summary>
    protected abstract void Configure(SystemBuilder b);

    /// <summary>Execute the system's logic. <c>ctx.Entities</c> yields the filtered entity set.</summary>
    protected abstract void Execute(TickContext ctx);

    internal static void InvokeConfigure(QuerySystem system, SystemBuilder b) => system.Configure(b);
    internal static void InvokeExecute(QuerySystem system, TickContext ctx) => system.Execute(ctx);
}
