namespace Typhon.Workbench.Sessions;

/// <summary>
/// A session that is watching a running engine over TCP.
/// </summary>
/// <remarks>
/// <para>
/// Endpoint handlers used to ask <c>session is AttachSession</c> to find the live runtime. That worked while "watching a
/// live engine" and "being an attach session" were the same thing. They stopped being the same thing once an
/// <see cref="OpenSession"/> — a database whose own application currently holds it, so the session opened paused — could
/// watch that application. The session is still an Open session, and its kind never changes; what changes is whether it
/// is watching. This is the same reasoning that produced <see cref="SessionCapability"/>: a capability is acquired and
/// released during a session's life, and no kind enum can express that.
/// </para>
/// <para>
/// <b>Not every <c>is AttachSession</c> test should become this one.</b> Some genuinely ask about kind — projecting the
/// session DTO, or enforcing the one-session-per-endpoint rule. Those must stay as they are. This interface is for the
/// handlers that only ever wanted "give me the live stream".
/// </para>
/// </remarks>
public interface ILiveProfilerHost
{
    /// <summary>The live runtime, or <c>null</c> when this session is not currently watching anything.</summary>
    AttachSessionRuntime LiveRuntime { get; }
}
