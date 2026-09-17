using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace Typhon.Engine.Tests.Runtime.Subscriptions;

/// <summary>
/// AC-19 — what <c>Typhon.Engine</c> is allowed to depend on.
/// </summary>
/// <remarks>
/// <b>A design decision only a test can hold.</b> <c>design/Subscriptions/04-transport.md § 1</c> puts HTTP parsing, TLS policy, origin checks and upgrade
/// limits in Kestrel's attack surface rather than the engine's, and ADR-004's embedded thesis says an engine a game server links against must not drag a web
/// stack in with it. Nothing about adding <c>&lt;PackageReference Include="Microsoft.AspNetCore.App" /&gt;</c> would fail a build, a test or a review that was
/// not looking for it — the WebSocket adapter is a separate package precisely so that this line exists somewhere that breaks.
/// </remarks>
[TestFixture]
class EngineReferenceTests
{
    /// <summary>The engine's assembly references carry nothing from ASP.NET Core.</summary>
    [Test]
    public void EngineDoesNotReferenceAspNetCore()
    {
        var engine = typeof(TyphonRuntime).Assembly;
        var offenders = new List<string>();

        foreach (var reference in engine.GetReferencedAssemblies())
        {
            var name = reference.Name;
            if (name != null && name.StartsWith("Microsoft.AspNetCore", StringComparison.OrdinalIgnoreCase))
            {
                offenders.Add(name);
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(engine.GetName().Name, Is.EqualTo("Typhon.Engine"), "this checks the engine, not whatever assembly the test type came from");
            Assert.That(
                offenders,
                Is.Empty,
                "Typhon.Engine references ASP.NET Core. The built-in TCP transport uses System.Net.Sockets only; a WebSocket endpoint belongs in " +
                "Typhon.Subscriptions.AspNetCore (04 § 5), not here.");
        });
    }

    /// <summary>
    /// The built-in transport is reachable, which is what makes the rule above affordable rather than a prohibition with no alternative.
    /// </summary>
    [Test]
    public void TheEngineShipsATransportOfItsOwn()
    {
        var transport = typeof(Typhon.Engine.Internals.TcpSubscriptionTransport);

        Assert.Multiple(() =>
        {
            Assert.That(typeof(ISubscriptionTransport).IsAssignableFrom(transport));
            Assert.That(transport.Assembly, Is.EqualTo(typeof(TyphonRuntime).Assembly), "a bot, a Unity client or a test needs no second package to connect");
            Assert.That(typeof(TcpSubscriptionOptions).GetProperty("Address", BindingFlags.Public | BindingFlags.Instance), Is.Not.Null);
        });
    }
}
