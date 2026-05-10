using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Defines the execution model of a system in the system DAG.
/// </summary>
[PublicAPI]
public enum SystemType
{
    /// <summary>
    /// Bulk data-parallel system. Work is divided into chunks distributed across workers via atomic counter (D4). Multiple workers process chunks concurrently.
    /// </summary>
    PipelineSystem,

    /// <summary>
    /// Single-worker entity iteration system. Processes an input source (View/Query) on one worker.
    /// Used for small result sets, cross-entity logic, and lightweight per-entity work.
    /// </summary>
    QuerySystem,

    /// <summary>
    /// Lightweight single-invocation system. Executes inline on the dispatching worker (D3).
    /// Used for input processing, cleanup, timers, and non-entity work.
    /// </summary>
    CallbackSystem
}
