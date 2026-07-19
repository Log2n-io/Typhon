using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// Tests <see cref="ProfilerSessionMetadataBuilder"/> — the engine-side replacement for the deleted host glue
/// <c>AntHill.Core.ProfilerSetup.BuildSessionMetadata</c> (issue #332). The key guarantee: the session's
/// <c>RuntimeConfig</c> is fully derived from <see cref="RuntimeOptions"/>, with no host-supplied or stubbed values.
/// </summary>
/// <remarks>
/// The full self-wiring path (<c>ProfilerBootstrap.TryStart</c>) cannot be unit-tested in isolation: it gates on
/// <see cref="TelemetryConfig.ProfilerActive"/>, a <c>static readonly</c> baked at class load from the config file —
/// it is exercised end-to-end by running a host with the profiler enabled. This fixture covers the part that has no
/// gate and carries the issue's core acceptance: the metadata composition itself.
/// </remarks>
[NonParallelizable]
[TestFixture]
class ProfilerSessionMetadataBuilderTests : TestBase<ProfilerSessionMetadataBuilderTests>
{
    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<EcsPosition>();
        dbe.RegisterComponentFromAccessor<EcsVelocity>();
        dbe.RegisterComponentFromAccessor<EcsHealth>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    [Test]
    public void Build_RuntimeConfig_FullyDerivedFromOptions()
    {
        using var dbe = SetupEngine();
        var options = new RuntimeOptions
        {
            WorkerCount = 3,
            BaseTickRate = 120,
            TelemetryRingCapacity = 2048,
            ParallelQueryMinChunkSize = 128,
        };
        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Noop", _ => { });
        }, options);

        var metadata = ProfilerSessionMetadataBuilder.Build(runtime, samplingSessionStartQpc: 0);

        Assert.That(metadata, Is.Not.Null);
        Assert.That(metadata.WorkerCount, Is.EqualTo(3));
        Assert.That(metadata.BaseTickRate, Is.EqualTo(120f));

        var rc = metadata.RuntimeConfig;
        Assert.That(rc, Is.Not.Null, "RuntimeConfig must be populated");
        Assert.That(rc.BaseTickRate, Is.EqualTo(120), "BaseTickRate derived from RuntimeOptions");
        Assert.That(rc.WorkerCount, Is.EqualTo(3), "WorkerCount derived from RuntimeOptions — not the old hardcoded 16");
        Assert.That(rc.TelemetryRingCapacity, Is.EqualTo(2048), "TelemetryRingCapacity derived from RuntimeOptions — not the old stub 0");
        Assert.That(rc.ParallelQueryMinChunkSize, Is.EqualTo(128), "ParallelQueryMinChunkSize derived from RuntimeOptions — not the old stub 0");
    }

    [Test]
    public void Build_PopulatesSystemsAndStaticTables()
    {
        using var dbe = SetupEngine();
        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Noop", _ => { });
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        var metadata = ProfilerSessionMetadataBuilder.Build(runtime, samplingSessionStartQpc: 0);

        Assert.That(metadata.Systems, Is.Not.Empty, "system definitions derived from runtime.Systems");
        Assert.That(metadata.ResourceGraphNodes, Is.Not.Empty, "resource graph snapshot taken from the engine");
        Assert.That(metadata.StartTimestamp, Is.GreaterThan(0), "session start timestamp captured internally");
    }
}
