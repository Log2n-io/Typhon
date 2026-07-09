using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Common shape for class-based systems registered with <see cref="RuntimeSchedule"/>. Implemented by <see cref="QuerySystem"/>, <see cref="CallbackSystem"/>,
/// and <see cref="PipelineSystem"/>.
/// </summary>
/// <remarks>
/// <para>
/// Mainly a marker so engine plumbing (registration, source attribution, etc.) can refer to "any class-based system" without falling back to <c>object</c>.
/// The exposed properties carry identity values that the engine populates during registration / build, so user code inside <c>Execute</c> can read its
/// own <see cref="Name"/> / <see cref="Index"/> without threading them through manually.
/// </para>
/// <para>
/// <b>Lifecycle.</b> <see cref="Name"/> is set immediately after <c>Configure(SystemBuilder)</c> returns inside <see cref="Dag.Add(QuerySystem)"/>
/// (and siblings). <see cref="Index"/> is set when <see cref="RuntimeSchedule.Build"/> assigns the system its DAG position. Both are <c>null</c> / <c>-1</c> prior
/// to registration — do not rely on them inside <c>Configure</c>.
/// </para>
/// </remarks>
[PublicAPI]
public interface ISystem
{
    /// <summary>Configured name from <see cref="SystemBuilder.Name"/>. Null before registration; never changes after.</summary>
    string Name { get; }

    /// <summary>DAG position assigned by <see cref="RuntimeSchedule.Build"/>. <c>-1</c> before build.</summary>
    int Index { get; }
}
