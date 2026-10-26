using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using SwgTatooine.Bots;

namespace SwgTatooine.Tests;

/// <summary>
/// The fleet runner's own contract: what it does when nothing is there to connect to, and what it does to two callers at once.
/// </summary>
/// <remarks>
/// <para>
/// <b>These exist because the control panel is a second caller, and every bug a review found here came from that.</b> The runner
/// was written as though one thread drove it start to finish; a web page calls <c>Snapshot()</c> whenever it re-renders, presses
/// Stop in the middle of a ramp, and starts a second run after the first — and each of those found something.
/// </para>
/// <para>
/// <b>No server.</b> Every case here either needs no connection or wants the failure, so the endpoint is a loopback listener that
/// hangs up on every connection. That keeps them about the runner rather than about the demo: the cases that need a real server and
/// real accounts are <see cref="BotProvisionerChecks"/>'s.
/// </para>
/// </remarks>
[TestFixture]
internal sealed class FleetRunnerChecks
{
    /// <summary>An endpoint that hangs up on every connection, so a ramp fails fast and deterministically.</summary>
    /// <remarks>
    /// <b>Not a closed port, which is what it was, because on Windows a refused loopback connect is not fast.</b> The stack retries the SYN before it
    /// believes the reset — about two seconds a connect — and that was this fixture's twenty seconds. A listener that accepts and closes fails the
    /// WebSocket handshake at once, and a failed handshake is all any case here asks for.
    /// </remarks>
    private static string Nowhere => $"ws://127.0.0.1:{((IPEndPoint)_hangUp.LocalEndpoint).Port}/ws";

    private static TcpListener _hangUp;
    private static CancellationTokenSource _hangingUp;

    [OneTimeSetUp]
    public void StartHangingUp()
    {
        _hangUp = new TcpListener(IPAddress.Loopback, 0);
        _hangUp.Start();
        _hangingUp = new CancellationTokenSource();
        var ct = _hangingUp.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var socket = await _hangUp.AcceptSocketAsync(ct);
                }
                catch (Exception)
                {
                    return;   // stopped
                }
            }
        });
    }

    [OneTimeTearDown]
    public void StopHangingUp()
    {
        _hangingUp.Cancel();
        _hangUp.Stop();
        _hangingUp.Dispose();
    }

    /// <summary>A god fleet needs no accounts and no key, which is what makes the panel usable with nothing configured.</summary>
    [Test]
    public void AGodFleetNeedsNoAccountsFileAndNoKey()
    {
        var settings = new FleetSettings { Kind = "god", Endpoint = Nowhere, Count = 1 };

        Assert.Multiple(() =>
        {
            Assert.That(settings.IsPlayer, Is.False);
            Assert.That(new FleetSettings { Kind = "player" }.IsPlayer, Is.True);
        });
    }

    /// <summary>A runner that has never run reports an idle, empty snapshot rather than throwing or showing a stale one.</summary>
    [Test]
    public async Task AFreshRunnerSnapshotsAsIdleAndEmpty()
    {
        await using var runner = new FleetRunner();
        var snapshot = runner.Snapshot();

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Phase, Is.EqualTo(FleetPhase.Idle));
            Assert.That(runner.Busy, Is.False);
            Assert.That(snapshot.Connected, Is.Zero);
            Assert.That(snapshot.Elapsed, Is.EqualTo(TimeSpan.Zero), "a run that never started has not been running for any time");
            Assert.That(snapshot.Fault, Is.Null);
            Assert.That(snapshot.Disconnects, Is.Not.Null, "a renderer walks this; null would be a crash on the first paint");
            Assert.That(runner.Log(), Is.Empty);
        });

        await runner.StopAsync();
    }

    /// <summary>
    /// A fleet that cannot open a single session faults, says so, and goes back to a state a second attempt can start from.
    /// </summary>
    /// <remarks>
    /// The "goes back" half is the point. A faulted runner that still reported <c>Busy</c> would leave the panel's Start button
    /// disabled and its Stop button doing nothing, with no way out but restarting the process.
    /// </remarks>
    [Test]
    [CancelAfter(60_000)]
    [Category("Integration")]
    public async Task AFleetThatOpensNothingFaultsAndStaysRestartable()
    {
        await using var runner = new FleetRunner();
        // One bot: the contract under test is "faults, says so, stays restartable", which a single refused connect proves. The rate is high because a
        // ramp opens a chunk of 250 every 250 / rate seconds and gives up after three empty rounds: at 100 a second that was 5 s per attempt.
        var settings = new FleetSettings { Kind = "god", Endpoint = Nowhere, Count = 1, ConnectRatePerSecond = 100_000 };

        Assert.That(async () => await runner.StartAsync(settings), Throws.InstanceOf<Exception>());

        var snapshot = runner.Snapshot();
        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Phase, Is.EqualTo(FleetPhase.Faulted));
            Assert.That(snapshot.Fault, Is.Not.Null.And.Not.Empty, "a fault with no message is a blank page on the panel");
            Assert.That(runner.Busy, Is.False, "a faulted run must not leave the panel with Start disabled and Stop inert");
            Assert.That(runner.Log(), Is.Not.Empty, "the log is the only account of what went wrong");
        });

        // And a second attempt is accepted rather than refused as "already running".
        Assert.That(async () => await runner.StartAsync(settings), Throws.InstanceOf<Exception>());
        Assert.That(runner.Snapshot().Phase, Is.EqualTo(FleetPhase.Faulted));
    }

    /// <summary>
    /// Snapshotting hard from another thread while a run starts and faults must not throw.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What this covers, precisely.</b> The runner's own fields — phase, opened, provisioned, the fault, the clock — are written
    /// by the run thread throughout provisioning and the ramp while this reader samples them, so it exercises the lock discipline
    /// that a review found missing: those were plain reads racing plain writes, which on the arm64 the bot box runs can show
    /// <c>Running</c> with nothing opened.
    /// </para>
    /// <para>
    /// <b>What it does NOT cover, said plainly so nobody trusts it for more than it is.</b> The other half of that defect was the
    /// swarm holding its population in a <c>List</c> that every counter enumerated, so a panel re-rendering during the ramp walked
    /// a list being appended to and got "collection was modified". **No bot is ever appended here** — every connect to a closed
    /// port fails and <c>OpenOneAsync</c> answers null — so this test cannot reach that path, and writing it as though it could
    /// would be worse than not having it. That half is now structural: the population is a pre-sized array with a published count
    /// and there is no growable list left to modify. The append path itself is exercised by a live run against a real server.
    /// </para>
    /// <para>
    /// Hammering rather than sleeping: the window is the ramp, so the reader has to run throughout it. A throw on the reader is
    /// captured and re-raised, because an exception on a background thread would otherwise pass as a green test.
    /// </para>
    /// </remarks>
    [Test]
    [CancelAfter(60_000)]
    [Category("Integration")]
    public async Task SnapshottingFromAnotherThreadDuringARampNeverThrows()
    {
        await using var runner = new FleetRunner();

        // Eight, not sixty-four: a larger population buys no extra coverage. The rate is the ramp's length — three empty rounds of
        // 250 / 500 s, about a second — which is the window the reader races.
        var settings = new FleetSettings { Kind = "god", Endpoint = Nowhere, Count = 8, ConnectRatePerSecond = 500 };

        using var reading = new CancellationTokenSource();
        Exception captured = null;
        var reads = 0;

        var reader = Task.Run(() =>
        {
            while (!reading.IsCancellationRequested)
            {
                try
                {
                    var snapshot = runner.Snapshot();
                    _ = snapshot.Connected + snapshot.Opened + snapshot.Provisioned;
                    _ = runner.Log();
                    _ = runner.Busy;
                    reads++;
                }
                catch (Exception e)
                {
                    captured = e;
                    return;
                }
            }
        });

        try
        {
            await runner.StartAsync(settings);
        }
        catch (Exception)
        {
            // Expected: nothing is listening. The ramp is what this test needs, not its success.
        }

        await reading.CancelAsync();
        await reader;

        Assert.Multiple(() =>
        {
            Assert.That(captured, Is.Null, $"reading the runner from another thread threw: {captured}");
            Assert.That(reads, Is.GreaterThan(0), "the reader never ran, so this test proved nothing");
        });
    }

    /// <summary>Stop on a runner that is not running is a no-op rather than a throw.</summary>
    /// <remarks>
    /// A panel's Stop button can be pressed twice, and the second press arrives when the first has already finished.
    /// </remarks>
    [Test]
    [CancelAfter(30_000)]
    public async Task StoppingAnIdleRunnerIsHarmlessAndRepeatable()
    {
        await using var runner = new FleetRunner();

        await runner.StopAsync();
        await runner.StopAsync();

        Assert.That(runner.Snapshot().Phase, Is.EqualTo(FleetPhase.Idle));
    }

    /// <summary>A player fleet with no accounts file is refused with a message that names the cause.</summary>
    /// <remarks>
    /// The server refuses a player session with no token (close 4102), so a fleet that started anyway would open nothing and the
    /// operator would be reading a connection failure rather than a configuration one.
    /// </remarks>
    [Test]
    [CancelAfter(30_000)]
    public async Task APlayerFleetWithoutAnAccountsFileIsRefusedClearly()
    {
        await using var runner = new FleetRunner();
        var settings = new FleetSettings { Kind = "player", Endpoint = Nowhere, Count = 1, AccountsFile = string.Empty };

        var thrown = Assert.ThrowsAsync<InvalidOperationException>(async () => await runner.StartAsync(settings));

        Assert.Multiple(() =>
        {
            Assert.That(thrown.Message, Does.Contain("accounts file"));
            Assert.That(runner.Snapshot().Phase, Is.EqualTo(FleetPhase.Faulted));
        });

        await runner.StopAsync();
    }

    /// <summary>Shaping is refused on a platform that cannot shape, rather than running unshaped and reporting a latency.</summary>
    /// <remarks>
    /// Failing closed is the whole point: a run that silently went unshaped would be reported as a 60 ms run while being a 0.2 ms
    /// one, and every conclusion drawn from it would be wrong in the flattering direction.
    /// </remarks>
    [Test]
    [CancelAfter(30_000)]
    public void ShapingOnAnUnsupportedPlatformFailsRatherThanLying()
    {
        if (NetemShaper.Supported)
        {
            Assert.Ignore("this host can shape, so the refusal path is not reachable here");
        }

        Assert.That(
            async () => await NetemShaper.ApplyAsync(string.Empty, 60, 15),
            Throws.InstanceOf<InvalidOperationException>().With.Message.Contains("tc netem"));
    }
}
