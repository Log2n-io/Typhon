using System.Collections.Generic;
using NUnit.Framework;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Tests.Sessions;

/// <summary>
/// Unit tests for <see cref="SampleClassifier"/> (#364 §8.7) — the per-sample on-CPU / voluntary-wait / involuntary-stall
/// classifier. Drives <see cref="SampleClassifier.Classify"/> with synthetic GC-suspension intervals and per-slot
/// context-switch slices built via <see cref="SampleClassifier.Create"/>, so the interval-join rules, the GC-wins
/// precedence, the slice boundaries and the wait-reason → off-CPU-class mapping are each verified without an on-disk
/// trace. The chunk-decode path (<see cref="SampleClassifier.Build"/>) is covered by the end-to-end trace verification.
/// </summary>
[TestFixture]
public sealed class SampleClassifierTests
{
    // Slot 0: two ON-CPU slices. The gap after slice A is a voluntary wait; the gap after slice B is scheduler preemption.
    private static SampleClassifier MakeClassifier()
        => SampleClassifier.Create(
            gcIntervals: [(5000, 6000)],
            slicesBySlot: new Dictionary<int, SampleClassifier.OnCpuSlice[]>
            {
                [0] =
                [
                    new SampleClassifier.OnCpuSlice(1000, 2000, SampleClass.Voluntary),
                    new SampleClassifier.OnCpuSlice(3000, 4000, SampleClass.InvoluntaryScheduler),
                ],
            },
            classificationAvailable: true);

    [Test]
    public void Classify_QpcInsideOnCpuSlice_ReturnsOnCpu()
    {
        Assert.That(MakeClassifier().Classify(1500, 0), Is.EqualTo(SampleClass.OnCpu));
    }

    [Test]
    public void Classify_SliceBoundaries_AreInclusiveOnCpu()
    {
        var c = MakeClassifier();
        Assert.That(c.Classify(1000, 0), Is.EqualTo(SampleClass.OnCpu), "slice start is on-CPU");
        Assert.That(c.Classify(2000, 0), Is.EqualTo(SampleClass.OnCpu), "slice end is on-CPU");
    }

    [Test]
    public void Classify_QpcInGapAfterVoluntarySlice_ReturnsVoluntary()
    {
        // 2500 is past slice A's end (2000) and before slice B's start (3000) — the gap carries slice A's off-CPU class.
        Assert.That(MakeClassifier().Classify(2500, 0), Is.EqualTo(SampleClass.Voluntary));
    }

    [Test]
    public void Classify_QpcInGapAfterSchedulerSlice_ReturnsInvoluntaryScheduler()
    {
        // 4500 is past slice B's end (4000) — slice B's gap is scheduler preemption.
        Assert.That(MakeClassifier().Classify(4500, 0), Is.EqualTo(SampleClass.InvoluntaryScheduler));
    }

    [Test]
    public void Classify_QpcInGcInterval_ReturnsInvoluntaryGc()
    {
        Assert.That(MakeClassifier().Classify(5500, 0), Is.EqualTo(SampleClass.InvoluntaryGc));
    }

    [Test]
    public void Classify_GcWinsOverContextSwitchEvidence()
    {
        // A sample inside both a GC interval and (hypothetically) a slice gap must classify GC — rule 1 outranks rule 2.
        var c = SampleClassifier.Create(
            gcIntervals: [(1200, 1800)],
            slicesBySlot: new Dictionary<int, SampleClassifier.OnCpuSlice[]>
            {
                [0] = [new SampleClassifier.OnCpuSlice(1000, 1100, SampleClass.Voluntary)],
            },
            classificationAvailable: true);
        // 1500 is in the GC interval and also in the gap after the slice — GC wins.
        Assert.That(c.Classify(1500, 0), Is.EqualTo(SampleClass.InvoluntaryGc));
    }

    [Test]
    public void Classify_NegativeThreadSlot_ReturnsUnknown()
    {
        // A non-Typhon thread (slot -1) has no context-switch slices — only GC could classify it.
        Assert.That(MakeClassifier().Classify(2500, -1), Is.EqualTo(SampleClass.Unknown));
    }

    [Test]
    public void Classify_NegativeThreadSlot_StillClassifiesGc()
    {
        Assert.That(MakeClassifier().Classify(5500, -1), Is.EqualTo(SampleClass.InvoluntaryGc));
    }

    [Test]
    public void Classify_NoSlicesForSlot_ReturnsUnknown()
    {
        Assert.That(MakeClassifier().Classify(2500, 7), Is.EqualTo(SampleClass.Unknown));
    }

    [Test]
    public void Classify_QpcBeforeFirstSlice_ReturnsUnknown()
    {
        // No slice starts at or before qpc 500 — there is no evidence, so the caller falls back to the SampleType proxy.
        Assert.That(MakeClassifier().Classify(500, 0), Is.EqualTo(SampleClass.Unknown));
    }

    [Test]
    public void Empty_ClassifiesEverythingUnknown_AndReportsUnavailable()
    {
        Assert.That(SampleClassifier.Empty.ClassificationAvailable, Is.False);
        Assert.That(SampleClassifier.Empty.Classify(1500, 0), Is.EqualTo(SampleClass.Unknown));
    }

    [Test]
    public void Create_PropagatesClassificationAvailableFlag()
    {
        var available = SampleClassifier.Create([], new Dictionary<int, SampleClassifier.OnCpuSlice[]>(), classificationAvailable: true);
        var degraded = SampleClassifier.Create([], new Dictionary<int, SampleClassifier.OnCpuSlice[]>(), classificationAvailable: false);
        Assert.That(available.ClassificationAvailable, Is.True);
        Assert.That(degraded.ClassificationAvailable, Is.False);
    }

    [TestCase((byte)0, SampleClass.Voluntary, TestName = "Executive → voluntary")]
    [TestCase((byte)6, SampleClass.Voluntary, TestName = "UserRequest → voluntary")]
    [TestCase((byte)15, SampleClass.Voluntary, TestName = "WrQueue → voluntary")]
    [TestCase((byte)29, SampleClass.Voluntary, TestName = "WrMutex → voluntary")]
    [TestCase((byte)30, SampleClass.InvoluntaryScheduler, TestName = "WrQuantumEnd → scheduler")]
    [TestCase((byte)32, SampleClass.InvoluntaryScheduler, TestName = "WrPreempted → scheduler")]
    [TestCase((byte)31, SampleClass.InvoluntaryScheduler, TestName = "WrDispatchInt → scheduler")]
    [TestCase((byte)2, SampleClass.InvoluntaryPaging, TestName = "PageIn → paging")]
    [TestCase((byte)18, SampleClass.InvoluntaryPaging, TestName = "WrVirtualMemory → paging")]
    [TestCase((byte)37, SampleClass.Voluntary, TestName = "MaximumWaitReason (unknown) → voluntary")]
    public void OffCpuClassFor_MapsWaitReasonToClass(byte waitReason, SampleClass expected)
    {
        Assert.That(SampleClassifier.OffCpuClassFor(waitReason), Is.EqualTo(expected));
    }
}
