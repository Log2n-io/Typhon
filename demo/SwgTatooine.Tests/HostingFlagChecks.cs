using System;
using System.IO;
using System.Net;
using NUnit.Framework;
using SwgTatooine.Replication;

namespace SwgTatooine.Tests;

/// <summary>
/// I1 — the web host binds loopback unless told otherwise, a reachable bind has to name its origins, and a deployed
/// server is told where its client is.
/// </summary>
/// <remarks>
/// <para>
/// <b>What these are for.</b> The host called <c>ListenAnyIP</c> unconditionally with <c>AllowAnyOrigin</c>, so
/// <c>--serve 8080</c> on a laptop published the world to whatever network the laptop was on. Survivable while a session
/// can only watch; not survivable on the day there is an account to create, which is the next phase. And
/// <c>DefaultClientRoot</c> walks up from the binary for <c>demo/SwgTatooine.Client/dist</c> — a dev tree's layout and no
/// deployment's — so a published server served a WebSocket and no page, with nothing saying so.
/// </para>
/// <para>
/// Pure parse and resolution checks: no host, no socket, no world.
/// </para>
/// </remarks>
[TestFixture]
public sealed class HostingFlagChecks
{
    /// <summary>Loopback is the default, and that is the whole point of the change.</summary>
    [Test]
    public void TheDefaultBindIsLoopback()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CommandLine.Parse(["--serve", "8080"]).BindAddress, Is.Null, "nothing to say means loopback");
            Assert.That(TatooineHost.ResolveBindAddress(null), Is.EqualTo(IPAddress.Loopback));
            Assert.That(TatooineHost.ResolveBindAddress("   "), Is.EqualTo(IPAddress.Loopback), "and so does an empty value");
        });
    }

    /// <summary>An explicit address is honoured, including the one that means every interface.</summary>
    [Test]
    public void AnExplicitAddressIsHonoured()
    {
        Assert.Multiple(() =>
        {
            Assert.That(TatooineHost.ResolveBindAddress("127.0.0.1"), Is.EqualTo(IPAddress.Loopback));
            Assert.That(TatooineHost.ResolveBindAddress("0.0.0.0"), Is.EqualTo(IPAddress.Any));
            Assert.That(TatooineHost.ResolveBindAddress(" 10.0.0.4 "), Is.EqualTo(IPAddress.Parse("10.0.0.4")), "surrounding space is not an error");
        });
    }

    /// <summary>An address that does not parse is refused rather than quietly becoming loopback.</summary>
    /// <remarks>
    /// Falling back would be the safe direction for exposure and the wrong one for trust: an operator who asked for a
    /// reachable address and got a server reachable by nobody would be debugging the client.
    /// </remarks>
    [Test]
    public void AnAddressThatDoesNotParseIsRefused()
    {
        Assert.Multiple(() =>
        {
            Assert.That(() => TatooineHost.ResolveBindAddress("not-an-ip"), Throws.ArgumentException);
            Assert.That(() => CommandLine.Parse(["--serve", "8080", "--bind", "not-an-ip"]), Throws.ArgumentException);
            Assert.That(() => CommandLine.Parse(["--serve", "8080", "--bind", "999.1.1.1"]), Throws.ArgumentException);
        });
    }

    /// <summary>A reachable bind must name its origins; a loopback one need not.</summary>
    /// <remarks>
    /// Allowing any origin on a public bind lets any page a visitor opens drive this server from their browser. On a dev
    /// box it is the right default, because the Vite port is not the server's.
    /// </remarks>
    [Test]
    public void AReachableBindRequiresAnOrigin()
    {
        Assert.Multiple(() =>
        {
            Assert.That(() => CommandLine.Parse(["--serve", "8080", "--bind", "0.0.0.0"]), Throws.ArgumentException);
            Assert.That(() => CommandLine.Parse(["--serve", "8080", "--bind", "10.0.0.4"]), Throws.ArgumentException);
            Assert.That(CommandLine.Parse(["--serve", "8080", "--bind", "127.0.0.1"]).Origins, Is.Empty, "loopback needs none");
            Assert.That(CommandLine.Parse(["--serve", "8080", "--bind", "0.0.0.0", "--origin", "https://x.test"]).Origins,
                Is.EqualTo(new[] { "https://x.test" }));
        });
    }

    /// <summary>Origins are a comma-separated list, trimmed, matching the house pattern for a list flag.</summary>
    /// <remarks>
    /// Comma-separated rather than repeatable because this parser refuses a flag given twice — the first occurrence used
    /// to win silently, which made a corrected A/B arm run the value it was meant to replace.
    /// </remarks>
    [Test]
    public void OriginsAreACommaSeparatedList()
    {
        var c = CommandLine.Parse(["--serve", "8080", "--bind", "0.0.0.0", "--origin", "https://a.test, https://b.test ,https://c.test"]);
        Assert.Multiple(() =>
        {
            Assert.That(c.Origins, Is.EqualTo(new[] { "https://a.test", "https://b.test", "https://c.test" }));
            Assert.That(CommandLine.Parse(["--serve", "8080"]).Origins, Is.Empty);
        });
    }

    /// <summary>The hosting flags are refused without <c>--serve</c>, because there is then no host to configure.</summary>
    [Test]
    public void TheHostingFlagsNeedAServer()
    {
        Assert.Multiple(() =>
        {
            Assert.That(() => CommandLine.Parse(["--bind", "127.0.0.1"]), Throws.ArgumentException);
            Assert.That(() => CommandLine.Parse(["--origin", "https://x.test"]), Throws.ArgumentException);
            Assert.That(() => CommandLine.Parse(["--client-dir", "."]), Throws.ArgumentException);
        });
    }

    /// <summary>A <c>--client-dir</c> that is not there is refused at the parse, not skipped at the host.</summary>
    /// <remarks>
    /// The host skips a missing directory silently, so a deployment whose payload unpacked wrong would serve a WebSocket
    /// and no page — and the operator would be looking at the browser. This is the check that makes it say so.
    /// </remarks>
    [Test]
    public void AMissingClientDirectoryIsRefused()
    {
        var absent = Path.Combine(Path.GetTempPath(), "swg-client-dir-that-is-not-there-" + Guid.NewGuid().ToString("N"));
        Assert.Multiple(() =>
        {
            Assert.That(() => CommandLine.Parse(["--serve", "8080", "--client-dir", absent]), Throws.ArgumentException);
            Assert.That(CommandLine.Parse(["--serve", "8080", "--client-dir", Path.GetTempPath()]).ClientDirectory, Is.EqualTo(Path.GetTempPath()),
                "one that exists is taken as given");
        });
    }

    /// <summary>All three are listed by <c>--help</c>, so they are discoverable rather than folklore.</summary>
    [Test]
    public void TheHostingFlagsAreListed()
    {
        var help = CommandLine.Parse(["--help"]).HelpText;
        Assert.Multiple(() =>
        {
            Assert.That(help, Does.Contain("--bind"));
            Assert.That(help, Does.Contain("--origin"));
            Assert.That(help, Does.Contain("--client-dir"));
        });
    }
}
