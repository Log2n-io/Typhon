using System;
using System.Diagnostics;
using System.IO;
using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// Unit tests for <see cref="CpuSampleParser"/> — the Phase 2 (#351) <c>.nettrace</c> parse + symbol-resolution step. The capture-dependent tests drive a
/// real in-process <see cref="CpuSamplerSession"/> capture of a CPU-burning workload, then parse the produced companion; they self-ignore when the EventPipe
/// diagnostics server is unavailable. The graceful-degrade tests run unconditionally.
/// </summary>
/// <remarks>
/// The capture + <see cref="CpuSampleParser.Parse"/> run once in <see cref="OneTimeSetUp"/>. These are genuine integration tests (in-process EventPipe
/// capture + TraceLog ETLX conversion) — they run longer than the sub-300 ms guideline but well under the 15 s timeout.
/// </remarks>
[TestFixture]
[NonParallelizable] // activates the global profiler emission pipeline; must not run concurrently with other fixtures
public sealed class CpuSampleParserTests
{
    private string _tempDir;
    private bool _captureAvailable;
    private ParsedCpuSamples _samples;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "typhon-cpusampleparser-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var tracePath = Path.Combine(_tempDir, "session.typhon-trace");
        using var sampler = new CpuSamplerSession();
        sampler.Start(tracePath);
        if (!sampler.IsRunning)
        {
            _captureAvailable = false;
            return;
        }
        _captureAvailable = true;

        SpinHotLoop(600);

        sampler.Stop();
        _samples = CpuSampleParser.Parse(Path.ChangeExtension(tracePath, ".nettrace"));
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
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

    // Named CPU-burn method — sampled while the EventPipe session runs, so a stack frame for it must resolve back to this source file.
    private static long SpinHotLoop(int milliseconds)
    {
        var sw = Stopwatch.StartNew();
        long acc = 1;
        while (sw.ElapsedMilliseconds < milliseconds)
        {
            for (var i = 0; i < 20_000; i++)
            {
                acc = acc * 1_000_003 + i;
                acc ^= acc >> 13;
            }
        }
        return acc;
    }

    private void RequireCapture()
    {
        if (!_captureAvailable)
        {
            Assert.Ignore("EventPipe diagnostics server unavailable in this environment — the capture→parse round-trip could not be exercised.");
        }
    }

    [Test]
    public void Parse_ProducesSamples()
    {
        RequireCapture();
        Assert.That(_samples, Is.Not.Null);
        Assert.That(_samples.SampleCount, Is.GreaterThan(0), "A ~600 ms CPU-burn at 1 kHz must yield CPU samples.");
    }

    [Test]
    public void Parse_InternsStacksAndFrames()
    {
        RequireCapture();
        // C1: parsing interns. A real session has many samples but far fewer unique stacks, and every sample references a valid stack index.
        Assert.That(_samples.Stacks.Length, Is.GreaterThan(0));
        Assert.That(_samples.Stacks.Length, Is.LessThanOrEqualTo(_samples.SampleCount), "interned stacks must not exceed the sample count");
        Assert.That(_samples.Frames.Length, Is.GreaterThan(0));
        foreach (var s in _samples.Samples)
        {
            Assert.That((int)s.StackIndex, Is.InRange(0, _samples.Stacks.Length - 1), "every sample references a valid interned stack");
        }
        foreach (var stack in _samples.Stacks)
        {
            foreach (var frameId in stack)
            {
                Assert.That((int)frameId, Is.InRange(0, _samples.Frames.Length - 1), "every stack frame id references a valid interned frame");
            }
        }
    }

    [Test]
    public void Parse_SamplesGroupedByThreadSlotThenQpc()
    {
        RequireCapture();
        // Build() sorts records by (threadSlot, qpc) so the Workbench per-thread index is a direct slice.
        var samples = _samples.Samples;
        for (var i = 1; i < samples.Length; i++)
        {
            var ordered = samples[i - 1].ThreadSlot < samples[i].ThreadSlot
                || (samples[i - 1].ThreadSlot == samples[i].ThreadSlot && samples[i - 1].Qpc <= samples[i].Qpc);
            Assert.That(ordered, Is.True, "samples must be sorted by (threadSlot, qpc)");
        }
    }

    [Test]
    public void Parse_ResolvesEngineFrameToSource()
    {
        RequireCapture();
        var resolvedHotFrame = false;
        foreach (var frame in _samples.Frames)
        {
            if (frame.Method.Contains("SpinHotLoop", StringComparison.Ordinal) &&
                frame.FilePath != null &&
                frame.FilePath.EndsWith("CpuSampleParserTests.cs", StringComparison.OrdinalIgnoreCase) &&
                frame.Line > 0)
            {
                resolvedHotFrame = true;
            }
        }
        Assert.That(resolvedHotFrame, Is.True, "The named CPU-burn method must appear in a sampled stack and resolve to this test file via the portable PDB.");
    }

    [Test]
    public void Parse_MergesFramesByMethod()
    {
        RequireCapture();
        // A call-tree node is a method, not a call site: the CPU-burn method is sampled at many IL offsets within its inner
        // loop, but those must all intern to ONE frame symbol — keying on line as well would split it into a frame per line.
        var spinFrames = 0;
        foreach (var frame in _samples.Frames)
        {
            if (frame.Method.Contains("SpinHotLoop", StringComparison.Ordinal))
            {
                spinFrames++;
            }
        }
        Assert.That(spinFrames, Is.EqualTo(1), "the same method must intern to a single frame regardless of sampled IL offset");
    }

    [Test]
    public void Parse_KeepsNonTyphonThreadsUnslotted()
    {
        RequireCapture();
        // The capture runs with no engine threads registered, so every sampled thread (GC / finalizer / sampler / IPC / the test thread) is non-Typhon: the
        // parser must KEEP those samples with ThreadSlot = -1, not drop them.
        var sawUnslotted = false;
        foreach (var sample in _samples.Samples)
        {
            if (sample.ThreadSlot == -1)
            {
                sawUnslotted = true;
            }
        }
        Assert.That(sawUnslotted, Is.True, "Samples on non-Typhon threads must be kept with ThreadSlot = -1, not dropped.");
    }

    [Test]
    public void Parse_ResolvesUnknownModuleFramesNameOnly()
    {
        RequireCapture();
        var sawNameOnlyFrame = false;
        foreach (var frame in _samples.Frames)
        {
            if (frame.FilePath == null && !string.IsNullOrEmpty(frame.Method))
            {
                sawNameOnlyFrame = true;
            }
        }
        Assert.That(sawNameOnlyFrame, Is.True, "BCL / native / dynamic frames with no local portable PDB must render name-only (FilePath null, name kept).");
    }

    [Test]
    public void Parse_SampleTypeIsManagedOrExternal()
    {
        RequireCapture();
        foreach (var sample in _samples.Samples)
        {
            Assert.That(sample.SampleType, Is.LessThanOrEqualTo((byte)1), "Error samples must be dropped — only Managed (0) / External (1) survive.");
        }
    }

    [Test]
    public void Parse_NonExistentFile_ReturnsEmpty()
    {
        var result = CpuSampleParser.Parse(Path.Combine(_tempDir, "no-such-file.nettrace"));
        Assert.That(result, Is.Not.Null);
        Assert.That(result.SampleCount, Is.Zero);
    }

    [Test]
    public void Parse_NullOrEmptyPath_ReturnsEmpty()
    {
        Assert.That(CpuSampleParser.Parse(null).SampleCount, Is.Zero);
        Assert.That(CpuSampleParser.Parse(string.Empty).SampleCount, Is.Zero);
    }

    [Test]
    public void Parse_CorruptFile_ReturnsEmpty()
    {
        var garbage = Path.Combine(_tempDir, "garbage.nettrace");
        File.WriteAllBytes(garbage, [0x01, 0x02, 0x03, 0x04, 0xDE, 0xAD, 0xBE, 0xEF]);
        var result = CpuSampleParser.Parse(garbage);
        Assert.That(result, Is.Not.Null);
        Assert.That(result.SampleCount, Is.Zero, "A corrupt .nettrace must degrade to an empty list, not throw.");
    }

    // ─── GC-safepoint-poll leaf detection (#364) ─────────────────────────────────────────────────
    // A sample whose leaf frame is the runtime GC poll is observer-effect noise — the sampler froze the thread at a
    // safepoint to walk its stack. CpuSampleParser drops such samples; this is the leaf-name predicate behind that.

    [TestCase("System.Threading.Thread.PollGC", true, TestName = "Thread.PollGC is the poll")]
    [TestCase("System.Threading.Thread.<PollGC>g__PollGCWorker|70_0", true, TestName = "PollGCWorker local function (ordinal 70_0)")]
    [TestCase("System.Threading.Thread.<PollGC>g__PollGCWorker|67_0", true, TestName = "PollGCWorker — a different build's ordinal still matches")]
    [TestCase("Typhon.Engine.DatabaseEngine.ExecuteMigrationsSlice", false, TestName = "a real engine method is not the poll")]
    [TestCase("System.Threading.Monitor.Wait", false, TestName = "Monitor.Wait is not the poll")]
    [TestCase("", false, TestName = "empty name is not the poll")]
    public void IsGcPollMethodName_DetectsTheSafepointPoll(string methodName, bool expected)
    {
        Assert.That(CpuSampleParser.IsGcPollMethodName(methodName), Is.EqualTo(expected));
    }

    [Test]
    public void IsGcPollMethodName_NullName_IsNotPoll()
    {
        Assert.That(CpuSampleParser.IsGcPollMethodName(null), Is.False);
    }
}
