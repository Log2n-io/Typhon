using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Reactive system with multi-stage gather/process/scatter pipeline. Execution model deferred to Patate design.
/// Derive from this class and implement <see cref="Configure"/>.
/// </summary>
[PublicAPI]
public abstract class PipelineSystem
{
    /// <summary>Declare the system's name, dependencies, input View, and configuration.</summary>
    protected abstract void Configure(SystemBuilder b);

    internal static void InvokeConfigure(PipelineSystem system, SystemBuilder b) => system.Configure(b);
}
