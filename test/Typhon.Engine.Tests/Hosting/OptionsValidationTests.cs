using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using System.IO;

namespace Typhon.Engine.Tests;

/// <summary>
/// #148 — startup option validation. The DI registration methods now wire real <see cref="IValidateOptions{TOptions}"/>:
/// <list type="bullet">
/// <item><description><c>PagedMMFOptions</c> delegates to its own <c>Validate()</c> (name / directory / cache size).</description></item>
/// <item><description><c>DatabaseEngineOptions</c> range-checks the wired <c>Resources</c> knobs.</description></item>
/// <item><description><c>MemoryAllocatorOptions</c> / <c>ResourceRegistryOptions</c> get no validator (only a diagnostic Name).</description></item>
/// </list>
/// Validation fires lazily on first <c>IOptions&lt;T&gt;.Value</c> — these tests resolve options only (no MMF/engine is
/// constructed), so no database files are created.
/// </summary>
[TestFixture]
class OptionsValidationTests
{
    private string _dir;

    [SetUp]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "Typhon.Tests", nameof(OptionsValidationTests));
        Directory.CreateDirectory(_dir);
    }

    // ── PagedMMFOptions: delegates to the type's own Validate() ──

    [Test]
    public void PagedMMF_ValidConfig_ResolvesWithoutThrowing()
    {
        var sp = new ServiceCollection()
            .AddScopedManagedPagedMemoryMappedFile(o =>
            {
                o.DatabaseName = "valid_db";
                o.DatabaseDirectory = _dir;
            })
            .BuildServiceProvider();

        Assert.DoesNotThrow(() => _ = sp.GetRequiredService<IOptions<ManagedPagedMMFOptions>>().Value);
    }

    [Test]
    public void PagedMMF_InvalidDatabaseName_FailsAtResolution()
    {
        var sp = new ServiceCollection()
            .AddScopedManagedPagedMemoryMappedFile(o =>
            {
                o.DatabaseName = "bad name!";  // spaces + '!' violate the single-word rule
                o.DatabaseDirectory = _dir;
            })
            .BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(() => _ = sp.GetRequiredService<IOptions<ManagedPagedMMFOptions>>().Value);
        Assert.That(ex.Message, Does.Contain("bad name!"), "the failure must carry the specific rule message from PagedMMFOptions.Validate");
    }

    [Test]
    public void PagedMMF_NonexistentDirectory_FailsAtResolution()
    {
        var sp = new ServiceCollection()
            .AddScopedManagedPagedMemoryMappedFile(o =>
            {
                o.DatabaseName = "valid_db";
                o.DatabaseDirectory = Path.Combine(_dir, "does", "not", "exist");
            })
            .BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => _ = sp.GetRequiredService<IOptions<ManagedPagedMMFOptions>>().Value);
    }

    [Test]
    public void PagedMMF_InvalidCacheSize_FailsAtResolution()
    {
        var sp = new ServiceCollection()
            .AddScopedManagedPagedMemoryMappedFile(o =>
            {
                o.DatabaseName = "valid_db";
                o.DatabaseDirectory = _dir;
                o.DatabaseCacheSize = 1;  // not a page-size multiple and below the minimum
            })
            .BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => _ = sp.GetRequiredService<IOptions<ManagedPagedMMFOptions>>().Value);
    }

    // ── DatabaseEngineOptions: range-checks the wired Resources knobs ──

    [Test]
    public void DatabaseEngine_ValidResources_ResolvesWithoutThrowing()
    {
        var sp = new ServiceCollection()
            .AddScopedDatabaseEngine(_ => { /* defaults are all valid */ })
            .BuildServiceProvider();

        Assert.DoesNotThrow(() => _ = sp.GetRequiredService<IOptions<DatabaseEngineOptions>>().Value);
    }

    [TestCase("MaxActiveTransactions")]
    [TestCase("WalRingBufferSizeBytes")]
    [TestCase("CheckpointIntervalMs")]
    [TestCase("CheckpointBarrierTimeoutMs")]
    public void DatabaseEngine_NonPositiveWiredKnob_FailsWithSpecificMessage(string knob)
    {
        var sp = new ServiceCollection()
            .AddScopedDatabaseEngine(o =>
            {
                switch (knob)
                {
                    case "MaxActiveTransactions": o.Resources.MaxActiveTransactions = 0; break;
                    case "WalRingBufferSizeBytes": o.Resources.WalRingBufferSizeBytes = 0; break;
                    case "CheckpointIntervalMs": o.Resources.CheckpointIntervalMs = 0; break;
                    case "CheckpointBarrierTimeoutMs": o.Resources.CheckpointBarrierTimeoutMs = -1; break;
                }
            })
            .BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(() => _ = sp.GetRequiredService<IOptions<DatabaseEngineOptions>>().Value);
        Assert.That(ex.Message, Does.Contain(knob), "the failure message must name the offending knob");
    }

    [TestCase("SegmentSize")]
    [TestCase("StagingBufferSize")]
    [TestCase("GroupCommitIntervalMs")]
    [TestCase("PreAllocateSegments")]
    public void DatabaseEngine_InvalidWalKnob_FailsWithSpecificMessage(string knob)
    {
        var sp = new ServiceCollection()
            .AddScopedDatabaseEngine(o =>
            {
                o.Wal = new WalWriterOptions();
                switch (knob)
                {
                    case "SegmentSize": o.Wal.SegmentSize = 0; break;
                    case "StagingBufferSize": o.Wal.StagingBufferSize = 4097; break;  // not a multiple of 4096
                    case "GroupCommitIntervalMs": o.Wal.GroupCommitIntervalMs = 0; break;
                    case "PreAllocateSegments": o.Wal.PreAllocateSegments = -1; break;
                }
            })
            .BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(() => _ = sp.GetRequiredService<IOptions<DatabaseEngineOptions>>().Value);
        Assert.That(ex.Message, Does.Contain(knob), "the failure message must name the offending Wal knob");
    }

    // ── MemoryAllocatorOptions / ResourceRegistryOptions: no validator (only a diagnostic Name) ──

    [Test]
    public void NoOpOptions_Configured_ResolveWithoutValidation()
    {
        var sp = new ServiceCollection()
            .AddMemoryAllocator(o => o.Name = "custom allocator")
            .AddResourceRegistry(o => o.Name = "custom registry")
            .BuildServiceProvider();

        Assert.Multiple(() =>
        {
            Assert.DoesNotThrow(() => _ = sp.GetRequiredService<IOptions<MemoryAllocatorOptions>>().Value);
            Assert.DoesNotThrow(() => _ = sp.GetRequiredService<IOptions<ResourceRegistryOptions>>().Value);
        });
    }
}
