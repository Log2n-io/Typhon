using System;
using System.IO;
using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// Unit tests for <see cref="CpuSamplerSession"/> — the Phase 1 (#351) in-process EventPipe CPU-sampling capture. Covers lifecycle idempotency,
/// the QPC anchor, the graceful-degrade contract, and that a <c>.nettrace</c> companion is produced.
/// </summary>
[TestFixture]
public sealed class CpuSamplerSessionTests
{
    private string _tempDir;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "typhon-cpusampler-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best-effort temp cleanup — a leftover temp file is not a test failure.
        }
    }

    private string TracePath() => Path.Combine(_tempDir, "session.typhon-trace");

    [Test]
    public void Start_ThenStop_CapturesQpcAndWritesCompanion()
    {
        using var sampler = new CpuSamplerSession();
        sampler.Start(TracePath());

        if (!sampler.IsRunning)
        {
            Assert.Ignore("EventPipe diagnostics server unavailable in this environment — the live capture path could not be exercised.");
        }

        Assert.That(sampler.SamplingSessionStartQpc, Is.Not.Zero, "Start must capture a non-zero QPC anchor.");
        sampler.Stop();

        var netTrace = Path.ChangeExtension(TracePath(), ".nettrace");
        Assert.That(File.Exists(netTrace), Is.True, "Stop must leave the .nettrace companion on disk for the later parsing phase.");
        Assert.That(new FileInfo(netTrace).Length, Is.GreaterThan(0), "The .nettrace companion must be non-empty.");
    }

    [Test]
    public void Start_IsIdempotent()
    {
        using var sampler = new CpuSamplerSession();
        sampler.Start(TracePath());
        Assert.DoesNotThrow(() => sampler.Start(TracePath()), "A second Start while running must be a no-op, not throw.");
        sampler.Stop();
    }

    [Test]
    public void Stop_WithoutStart_IsNoOp()
    {
        using var sampler = new CpuSamplerSession();
        Assert.DoesNotThrow(sampler.Stop);
        Assert.That(sampler.IsRunning, Is.False);
    }

    [Test]
    public void Stop_IsIdempotent()
    {
        using var sampler = new CpuSamplerSession();
        sampler.Start(TracePath());
        sampler.Stop();
        Assert.DoesNotThrow(sampler.Stop, "A second Stop must be a no-op.");
    }

    [Test]
    public void Dispose_StopsSessionAndIsIdempotent()
    {
        var sampler = new CpuSamplerSession();
        sampler.Start(TracePath());
        sampler.Dispose();
        Assert.That(sampler.IsRunning, Is.False, "Dispose must stop a running session.");
        Assert.DoesNotThrow(sampler.Dispose, "A second Dispose must be a no-op.");
    }

    [Test]
    public void Start_WithUnwritablePath_DegradesWithoutThrowing()
    {
        using var sampler = new CpuSamplerSession();
        // A .nettrace path under a non-existent directory makes the output FileStream constructor throw — exercising the graceful-degrade path.
        var badPath = Path.Combine(_tempDir, "no-such-subdir", "session.typhon-trace");
        Assert.DoesNotThrow(() => sampler.Start(badPath), "A start failure must be swallowed (best-effort), not thrown into the host.");
        Assert.That(sampler.IsRunning, Is.False, "A failed Start must leave the session not-running.");
        Assert.That(sampler.SamplingSessionStartQpc, Is.Zero, "A failed Start must reset the QPC anchor to 0.");
    }
}
