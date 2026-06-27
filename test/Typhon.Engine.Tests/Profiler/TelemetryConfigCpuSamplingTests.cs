using NUnit.Framework;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// Verifies the Phase 1 (#351) <c>CpuSampling</c> telemetry configuration: the new <see cref="TelemetryConfig.ProfilerCpuSamplingEnabled"/> /
/// <see cref="TelemetryConfig.ProfilerCpuSamplingActive"/> fields read <c>Typhon:Profiler:CpuSampling:Enabled</c> and compose correctly.
/// </summary>
/// <remarks>
/// <see cref="TelemetryConfig"/> is a static class loaded once per process from <c>typhon.telemetry.json</c>. The test project's config sets
/// <c>Typhon:Profiler:CpuSampling:Enabled = true</c>, so the JSON-read path is what these tests assert. The
/// <c>TYPHON__PROFILER__CPUSAMPLING__ENABLED</c> env-var override is generic <c>IConfiguration.AddEnvironmentVariables</c> behavior (not
/// CpuSampling-specific) and cannot be re-exercised in-process after the static constructor has run — it is covered by the #351 end-to-end verification.
/// </remarks>
[TestFixture]
[NonParallelizable] // activates the global profiler emission pipeline; must not run concurrently with other fixtures
public sealed class TelemetryConfigCpuSamplingTests
{
    [Test]
    public void CpuSamplingEnabled_ReadsFromTelemetryJson()
    {
        // test/Typhon.Engine.Tests/typhon.telemetry.json sets Typhon:Profiler:CpuSampling:Enabled = true.
        Assert.That(TelemetryConfig.ProfilerCpuSamplingEnabled, Is.True,
            "ProfilerCpuSamplingEnabled must reflect Typhon:Profiler:CpuSampling:Enabled from typhon.telemetry.json.");
    }

    [Test]
    public void CpuSamplingActive_IsProfilerActiveAndEnabled()
    {
        Assert.That(
            TelemetryConfig.ProfilerCpuSamplingActive,
            Is.EqualTo(TelemetryConfig.ProfilerActive && TelemetryConfig.ProfilerCpuSamplingEnabled),
            "ProfilerCpuSamplingActive must equal ProfilerActive AND ProfilerCpuSamplingEnabled.");
    }

    [Test]
    public void ConfigurationSummary_SurfacesCpuSampling()
    {
        Assert.That(TelemetryConfig.GetConfigurationSummary(), Does.Contain("CpuSampling"),
            "GetConfigurationSummary must surface the CpuSampling state for startup diagnostics.");
    }
}
