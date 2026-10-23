using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SwgTatooine.Bots;

/// <summary>
/// Puts an internet-shaped delay on the bot box's own interface, with <c>tc netem</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>In the kernel and not in the client, and that distinction is the whole reason this file exists.</b> A delay added in the bot
/// process around a send or a receive postpones when the application sees a message and nothing else: the TCP ACK still goes back
/// at line rate, so the congestion window, the server's send buffer and every backpressure and slow-consumer path behave exactly
/// as they do on a loopback. Those paths are a large part of what a thousand remote sessions are supposed to exercise, so
/// simulating latency above the socket measures the one thing it was meant to stress least.
/// </para>
/// <para>
/// <b>Interface-wide, which means every bot gets the same delay.</b> That is correct for what this knob is for — the question is
/// "what does the server do when its clients are 60 ms away", not "what if bot 404 is slower". Per-bot variability belongs to the
/// brain's reaction latency (07-bots § 2: 200-600 ms of think time), which is an application-level number and models a different
/// thing: a human's hands rather than a network's path. Conflating the two would produce a run where neither is interpretable.
/// </para>
/// <para>
/// <b>It needs <c>CAP_NET_ADMIN</c>, so it shells out to <c>sudo tc</c> and reports failure rather than hiding it.</b> A run that
/// silently went unshaped would be reported as a 60 ms run while being a 0.2 ms run, and every conclusion drawn from it would be
/// wrong in the flattering direction. Linux only: on Windows and macOS this answers that it cannot shape, which is the honest
/// answer for a dev box and the reason the control panel shows the shaping it actually got rather than the one it asked for.
/// </para>
/// </remarks>
public static class NetemShaper
{
    /// <summary>Whether this platform can shape at all.</summary>
    public static bool Supported => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    /// <summary>What <see cref="ApplyAsync"/> actually did.</summary>
    /// <param name="Device">The interface that was shaped, resolved.</param>
    /// <param name="Description">A sentence fit for an operator.</param>
    /// <remarks>
    /// <b>The device is returned because the caller has to unshape THAT one.</b> Passing an empty device to the clear
    /// path re-resolved the default route, and a route that had changed in between -- which on a cloud instance it can --
    /// left the shaped interface shaped. The next run then measured a delay nothing had asked for and nothing reported.
    /// </remarks>
    public readonly record struct NetemApplied(string Device, string Description);

    /// <summary>
    /// Puts a delay and jitter on an interface, replacing whatever was there.
    /// </summary>
    /// <param name="device">The interface, or empty to use the route to <paramref name="routeProbe"/>.</param>
    /// <param name="latencyMs">One-way delay, in milliseconds.</param>
    /// <param name="jitterMs">Jitter around it, in milliseconds.</param>
    /// <param name="routeProbe">The host the traffic to be shaped actually goes to; empty falls back to a public address.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>The device shaped and a description of it.</returns>
    /// <exception cref="InvalidOperationException">The platform cannot shape, or <c>tc</c> refused.</exception>
    public static async Task<NetemApplied> ApplyAsync(
        string device,
        int latencyMs,
        int jitterMs,
        string routeProbe = null,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(latencyMs);
        ArgumentOutOfRangeException.ThrowIfNegative(jitterMs);

        if (!Supported)
        {
            throw new InvalidOperationException(
                $"network shaping needs Linux and tc netem; this is {RuntimeInformation.OSDescription}. Run unshaped, or run the fleet on the bot box.");
        }

        var target = string.IsNullOrWhiteSpace(device) ? await DefaultDeviceAsync(routeProbe, ct).ConfigureAwait(false) : device.Trim();
        RequireInterfaceName(target);

        // Replace, not add: `add` fails when a qdisc is already there, which after one run it is. `replace` makes the operation
        // idempotent, so a panel's Start button does not have to know what the last run left behind.
        var jitter = jitterMs > 0 ? " " + jitterMs.ToString(CultureInfo.InvariantCulture) + "ms" : string.Empty;
        var arguments = $"qdisc replace dev {target} root netem delay {latencyMs.ToString(CultureInfo.InvariantCulture)}ms{jitter}";

        var (code, output) = await RunAsync("tc", arguments, ct).ConfigureAwait(false);
        if (code != 0)
        {
            throw new InvalidOperationException(
                $"tc refused to shape {target} (exit {code.ToString(CultureInfo.InvariantCulture)}): {output}. It needs CAP_NET_ADMIN — "
                + "give the unit `AmbientCapabilities=CAP_NET_ADMIN`, or allow `tc` through sudoers for the fleet's user.");
        }

        var description = jitterMs > 0
            ? $"{target}: {latencyMs.ToString(CultureInfo.InvariantCulture)} ms +/- {jitterMs.ToString(CultureInfo.InvariantCulture)} ms (netem)"
            : $"{target}: {latencyMs.ToString(CultureInfo.InvariantCulture)} ms (netem)";

        return new NetemApplied(target, description);
    }

    /// <summary>
    /// Takes the shaping off an interface.
    /// </summary>
    /// <param name="device">The interface <see cref="ApplyAsync"/> reported shaping. Empty re-resolves, which is a last resort.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>Whether the qdisc was removed.</returns>
    /// <remarks>
    /// <para>
    /// <b>Pass the device <see cref="NetemApplied.Device"/> named.</b> Re-resolving the default route here was a real defect: if
    /// the route changed between shaping and teardown the shaped interface stayed shaped, and the next run on that box measured a
    /// delay it never asked for.
    /// </para>
    /// <para>
    /// Failure is swallowed, deliberately: this runs on the way out, and a box left shaped is a visible, diagnosable state, while
    /// an exception thrown during teardown would mask whatever actually ended the run.
    /// </para>
    /// </remarks>
    public static async Task<bool> ClearAsync(string device, CancellationToken ct = default)
    {
        if (!Supported)
        {
            return false;
        }

        try
        {
            var target = string.IsNullOrWhiteSpace(device) ? await DefaultDeviceAsync(null, ct).ConfigureAwait(false) : device.Trim();
            RequireInterfaceName(target);
            var (code, _) = await RunAsync("tc", $"qdisc del dev {target} root", ct).ConfigureAwait(false);
            return code == 0;
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception
            or OperationCanceledException or System.IO.IOException)
        {
            // ArgumentException because RequireInterfaceName throws it, IOException because a dying pipe does, and Win32Exception
            // because `sudo` may simply not be installed -- all of which are "could not unshape", not "the run failed".
            return false;
        }
    }

    /// <summary>
    /// Refuses anything that is not an interface name.
    /// </summary>
    /// <param name="device">The candidate.</param>
    /// <exception cref="ArgumentException">It is not a name the kernel could have given an interface.</exception>
    /// <remarks>
    /// <b>Because this value comes from a web form and ends up in <c>tc</c>'s argument list.</b> There is no shell in the
    /// path — <see cref="ProcessStartInfo"/> is given arguments, not a command line for <c>sh</c> — so a semicolon cannot
    /// chain a second command. What it CAN do is inject further <c>tc</c> arguments: a "device" of
    /// <c>eth0 root netem loss 100%</c> is five arguments and <c>tc</c> would accept them. The operator is already trusted
    /// and the console binds loopback, so this is a guard rather than a breach — but shaping that silently did something
    /// other than what the page said is exactly the class of thing that makes a measurement untrustworthy.
    /// <para>
    /// Fifteen characters because Linux's <c>IFNAMSIZ</c> is sixteen including the terminator, so anything longer cannot
    /// name a real interface.
    /// </para>
    /// </remarks>
    private static void RequireInterfaceName(string device)
    {
        if (string.IsNullOrEmpty(device) || device.Length > 15)
        {
            throw new ArgumentException($"'{device}' is not an interface name (1 to 15 characters)", nameof(device));
        }

        foreach (var c in device)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '.' && c != '_' && c != '-' && c != ':' && c != '@')
            {
                throw new ArgumentException($"'{device}' is not an interface name: '{c}' cannot appear in one", nameof(device));
            }
        }
    }

    /// <summary>
    /// The interface the traffic being shaped actually leaves by.
    /// </summary>
    /// <param name="routeProbe">The destination to ask about; empty falls back to a public address.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>The device name.</returns>
    /// <exception cref="InvalidOperationException">The route could not be read.</exception>
    /// <remarks>
    /// <b>It asks about the SERVER's address, not a public one.</b> This probed <c>1.1.1.1</c>, which is exactly wrong for the
    /// topology 07-bots § 5 prescribes: the bot box sits in the same subnet as the server, reaches it by private IP, and may have
    /// no route to the internet at all -- so on the intended deployment the probe failed and shaping refused to start. The address
    /// the fleet is about to hammer is the only one whose route matters.
    /// <para>
    /// <c>ip route get</c> rather than parsing <c>ip route show</c>: it answers for a concrete destination, so a box with several
    /// routes gives the one that will actually carry the traffic instead of whichever line came first.
    /// </para>
    /// </remarks>
    private static async Task<string> DefaultDeviceAsync(string routeProbe, CancellationToken ct)
    {
        var destination = string.IsNullOrWhiteSpace(routeProbe) ? "1.1.1.1" : routeProbe.Trim();
        var (code, output) = await RunAsync("ip", "-o route get " + destination, ct).ConfigureAwait(false);
        if (code != 0)
        {
            throw new InvalidOperationException($"could not read the route to {destination} to pick an interface: {output}");
        }

        return DeviceFrom(output)
            ?? throw new InvalidOperationException($"the route to {destination} named no interface: {output}");
    }

    /// <summary>
    /// Pulls the interface name out of one <c>ip -o route get</c> line.
    /// </summary>
    /// <param name="line">What <c>ip</c> printed.</param>
    /// <returns>The device, or <see langword="null"/> when the line names none.</returns>
    /// <remarks>
    /// Separated from the process call so it can be tested against real <c>ip</c> output -- including the <c>unreachable</c> form,
    /// which names no device and must answer null rather than return the next word after a missing <c>dev</c>.
    /// </remarks>
    internal static string DeviceFrom(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        var words = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length - 1; i++)
        {
            if (string.Equals(words[i], "dev", StringComparison.Ordinal))
            {
                return words[i + 1];
            }
        }

        return null;
    }

    /// <summary>Runs a command under <c>sudo -n</c> and collects what it said.</summary>
    /// <param name="command">The executable.</param>
    /// <param name="arguments">Its arguments.</param>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns>The exit code and the combined output.</returns>
    /// <remarks>
    /// <c>sudo -n</c>, never interactive: a password prompt from inside a service or a web request is a hang with no operator in
    /// front of it. If sudo would need a password it fails immediately and the message above says what to configure.
    /// </remarks>
    private static async Task<(int Code, string Output)> RunAsync(string command, string arguments, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "sudo",
                Arguments = "-n " + command + " " + arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            // `sudo` itself is missing, which Start throws for rather than returning a code. Translated here so the caller gets
            // the sentence about CAP_NET_ADMIN instead of a raw Win32 error from a path that never reached tc.
            throw new InvalidOperationException(
                $"could not run `sudo {command}`: {e.Message}. The bot box needs sudo and tc, or the unit needs AmbientCapabilities=CAP_NET_ADMIN.", e);
        }

        // BOTH pipes concurrently. Reading stdout to EOF and only then stderr deadlocks a child that fills stderr's buffer
        // first -- the classic one, and `async` does not help: the child blocks writing while we block reading the other pipe.
        // `tc` is quiet enough that it would probably never have happened, which is what makes it the kind of latent bug that
        // surfaces on the one run nobody can reproduce.
        var readingOut = process.StandardOutput.ReadToEndAsync(ct);
        var readingError = process.StandardError.ReadToEndAsync(ct);
        await Task.WhenAll(readingOut, readingError).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        var output = (await readingOut.ConfigureAwait(false) + " " + await readingError.ConfigureAwait(false))
            .Replace('\n', ' ')
            .Replace('\r', ' ')
            .Trim();
        return (process.ExitCode, output);
    }
}
