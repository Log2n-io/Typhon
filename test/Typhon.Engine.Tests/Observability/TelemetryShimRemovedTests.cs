using NUnit.Framework;
using System.Reflection;

namespace Typhon.Engine.Tests.Observability;

/// <summary>
/// Guards the removal of the expired Phase-0 legacy telemetry-namespace back-compat shim (issue #443).
///
/// <para>
/// The shim was promised for "one release" and outlived its expiry by many. Once deleted, nothing should
/// re-introduce the deprecated public surface — the delegating <c>AddTyphonTelemetry</c> extension or the
/// <c>LegacyConfigDetected</c> diagnostic flag. This reflection guard fails at PR time if either returns.
/// It runs in &lt;1 ms; the behavioral side (config now resolves solely from <c>Typhon:Profiler:*</c>) is
/// covered by the full suite staying green.
/// </para>
/// </summary>
[TestFixture]
public class TelemetryShimRemovedTests
{
    [Test]
    public void LegacyConfigDetected_Field_Is_Gone()
    {
        var field = typeof(TelemetryConfig).GetField("LegacyConfigDetected", BindingFlags.Public | BindingFlags.Static);
        Assert.That(field, Is.Null,
            "The legacy telemetry-namespace shim was removed (#443); `LegacyConfigDetected` must not be re-added.");
    }

    [Test]
    public void AddTyphonTelemetry_Extension_Is_Gone()
    {
        var method = typeof(TelemetryServiceExtensions).GetMethod("AddTyphonTelemetry", BindingFlags.Public | BindingFlags.Static);
        Assert.That(method, Is.Null,
            "The obsolete `AddTyphonTelemetry` shim was removed (#443); use `AddTyphonProfiler`.");
    }
}
