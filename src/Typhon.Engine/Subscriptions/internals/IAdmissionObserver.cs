namespace Typhon.Engine.Internals;

/// <summary>
/// A link that wants to know when its connection became a session — the moment <c>HELLO</c> was admitted.
/// </summary>
/// <remarks>
/// The engine's own TCP link implements it, to release the pending-connection slot it took at accept (<see
/// cref="TcpSubscriptionOptions.MaxPendingConnections"/>).
/// Internal on purpose: <see cref="ISubscriptionLink"/> is the public seam and stays as it is; a link from another transport simply does not implement this,
/// and nothing changes for it. Called from inside the connection's lock, so an implementation must not block.
/// </remarks>
internal interface IAdmissionObserver
{
    /// <summary>The connection's <c>HELLO</c> was admitted: it is a session now. Called at most once.</summary>
    void OnAdmitted();
}
