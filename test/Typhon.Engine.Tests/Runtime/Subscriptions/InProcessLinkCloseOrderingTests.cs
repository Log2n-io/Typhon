using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Typhon.Protocol;

namespace Typhon.Engine.Tests.Runtime.Subscriptions;

/// <summary>
/// The close-publication order of <see cref="InProcessLink"/>: a thread that observes the close must be able to read the close's code.
/// </summary>
/// <remarks>
/// <b>Why a fixture for a test double.</b> Every subscriptions fixture reads a refusal's code through this link, and an open session's refusal is sent and the
/// link closed by the SEND PUMP, on another thread, while the test waits. So the handoff below is not hypothetical plumbing — it is the shape of every one of
/// those assertions. <c>InProcessLink.Close</c> used to bump the counter behind <c>IsClosed</c> on entry and store the code afterwards, which made the close
/// observable one instruction before its own code; the 2026-10-05 nightly read close code 0 on two of six <c>ClientInputFuzzTests</c> seeds, 0 being a code
/// no constant in <see cref="CloseCodes"/> defines and nothing anywhere sends.
/// </remarks>
[TestFixture]
[Category("Subscriptions")]
internal sealed class InProcessLinkCloseOrderingTests
{
    /// <summary>
    /// Race a tight-spinning reader against the close, the way the fuzz test's <c>Send</c> races the send pump, and require the code every time.
    /// </summary>
    /// <remarks>
    /// The reader spins without yielding on purpose: a <see cref="Thread.Yield"/> or a sleep in the loop is long enough for the store that follows the counter
    /// to become visible, which is exactly how this defect stayed invisible on a 32-thread dev box and surfaced on a 4-vCPU runner. Iterating is what makes the
    /// window land — a single pass proves nothing about an ordering bug.
    /// </remarks>
    [Test]
    [Repeat(3)]
    public void AReaderThatSeesTheCloseCanReadItsCode()
    {
        const int iterations = 2_000;

        for (var i = 0; i < iterations; i++)
        {
            var link = new InProcessLink();
            var release = new ManualResetEventSlim(false);
            var closer = Task.Run(() =>
            {
                release.Wait();
                link.Close(CloseCodes.ProtocolError, "refused");
            });

            release.Set();
            while (!link.IsClosed)
            {
                // deliberately no yield — see the remarks
            }

            var code = link.CloseCode;
            var reason = link.CloseReason;

            closer.Wait();
            release.Dispose();

            Assert.That(code, Is.EqualTo(CloseCodes.ProtocolError), $"iteration {i}: the close was observable before its code was stored");
            Assert.That(reason, Is.EqualTo("refused"), $"iteration {i}: the close was observable before its reason was stored");
        }
    }
}
