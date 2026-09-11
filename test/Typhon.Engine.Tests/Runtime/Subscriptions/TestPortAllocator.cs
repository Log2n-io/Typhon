using System.Net;
using System.Net.Sockets;

namespace Typhon.Engine.Tests.Runtime;

// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// Ports for the subscription fixtures, taken from the OS instead of written into the source.
//
// The three fixtures here each used to hard-code one (19950, 19876, 19900) with a comment saying "high port to avoid conflicts". High is not free: 19950 and
// 19951 are what a running Rider instance listens on, so both OutputPhaseTests cases failed on any developer machine with the IDE open and passed on CI,
// which has no IDE. The failure did not look like a port conflict either — the client CONNECTS, to the other process, so the test's own runtime simply never
// sees a connection and the assertion that follows dies somewhere else entirely.
//
// A fixed port cannot be made safe by choosing a better number. Only the OS knows what is free, and it only knows at the moment it is asked.
// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>Hands out a TCP port the OS has just confirmed to be free.</summary>
internal static class TestPortAllocator
{
    /// <summary>
    /// Binds a listener to port 0, reads back the port the OS assigned, releases it and returns it.
    /// </summary>
    /// <remarks>
    /// <para><b>The gap between releasing and the caller re-binding is real and is the right trade.</b> Something else could take the port in between, but it
    /// would have to be asking for an ephemeral port in that same instant, whereas a hard-coded constant collides with anything that ever chose the same
    /// number and keeps colliding for as long as that program runs. This narrows a permanent, machine-specific failure to a race nobody has lost yet.</para>
    /// <para>Called per TEST rather than per fixture: a socket from the previous test can sit in TIME_WAIT on the same port, and a fresh number each time
    /// costs nothing.</para>
    /// </remarks>
    internal static int NextFreePort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint).Port;
    }
}
