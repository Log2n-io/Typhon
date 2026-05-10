using System.Collections.Generic;
using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Groups related sub-systems into a single unit in the parent DAG. Completes when all sub-systems finish.
/// Derive from this class, implement <see cref="Configure"/>, and call <see cref="Add"/> to register sub-systems.
/// </summary>
[PublicAPI]
public abstract class CompoundSystem
{
    internal readonly List<object> _systems = [];

    /// <summary>Configure the compound by adding sub-systems via <see cref="Add"/>.</summary>
    protected abstract void Configure();

    /// <summary>Add a CallbackSystem to this compound.</summary>
    protected void Add(CallbackSystem system) => _systems.Add(system);

    /// <summary>Add a QuerySystem to this compound.</summary>
    protected void Add(QuerySystem system) => _systems.Add(system);

    /// <summary>Add a PipelineSystem to this compound.</summary>
    protected void Add(PipelineSystem system) => _systems.Add(system);

    internal static void InvokeConfigure(CompoundSystem system) => system.Configure();
}
