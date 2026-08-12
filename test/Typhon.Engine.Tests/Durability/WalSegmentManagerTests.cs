using NUnit.Framework;
using System;
using System.IO;

namespace Typhon.Engine.Tests;

/// <summary>
/// Tests for <see cref="WalSegmentManager"/> — segment creation, rotation, and pre-allocation.
/// Uses <see cref="InMemoryWalFileIO"/> for isolation from disk I/O.
/// </summary>
[TestFixture]
public class WalSegmentManagerTests
{
    private InMemoryWalFileIO _fileIO;
    private string _walDir;

    [SetUp]
    public void Setup()
    {
        _fileIO = new InMemoryWalFileIO();
        _walDir = Path.Combine(Path.GetTempPath(), $"typhon_wal_test_{Guid.NewGuid():N}");
    }

    [TearDown]
    public void TearDown()
    {
        _fileIO.Dispose();
        if (Directory.Exists(_walDir))
        {
            Directory.Delete(_walDir, true);
        }
    }

    private WalSegmentManager CreateManager(uint segmentSize = 64 * 1024 * 1024, int preAllocCount = 4, bool useFUA = false) =>
        new(_fileIO, _walDir, segmentSize, preAllocCount, useFUA);

    #region Initialize

    [Test]
    public void Initialize_CreatesWalDirectory()
    {
        using var mgr = CreateManager();

        mgr.Initialize(lastSegmentId: 0, firstLSN: 1);

        Assert.That(Directory.Exists(_walDir), Is.True);
    }

    [Test]
    public void Initialize_CreatesActiveSegment()
    {
        using var mgr = CreateManager();

        mgr.Initialize(lastSegmentId: 0, firstLSN: 1);

        Assert.That(mgr.ActiveSegment, Is.Not.Null);
        Assert.That(mgr.ActiveSegment.SegmentId, Is.EqualTo(1));
        Assert.That(mgr.ActiveSegment.FirstLSN, Is.EqualTo(1));
        Assert.That(mgr.ActiveSegment.WriteOffset, Is.EqualTo(WalSegmentHeader.SizeInBytes));
    }

    [Test]
    public void Initialize_DoesNotPreAllocate_UntilTheFirstRotation()
    {
        using var mgr = CreateManager(preAllocCount: 4);

        mgr.Initialize(lastSegmentId: 0, firstLSN: 1);

        // #784: the pool is built on the first rotation, not at open. Before a database has filled a segment there is no
        // evidence it ever will, and the placeholders are never sealed — so reclamation cannot see them and they would sit
        // on disk forever (4 x 64 MiB at the defaults).
        Assert.That(_fileIO.Exists(mgr.GetSegmentPath(1)), Is.True, "the active segment exists");
        Assert.That(_fileIO.Exists(mgr.GetSegmentPath(2)), Is.False);
        Assert.That(_fileIO.Exists(mgr.GetSegmentPath(3)), Is.False);
        Assert.That(_fileIO.Exists(mgr.GetSegmentPath(4)), Is.False);
        Assert.That(_fileIO.Exists(mgr.GetSegmentPath(5)), Is.False);
    }

    [Test]
    public void FirstRotation_BuildsTheFullPreAllocationPool()
    {
        using var mgr = CreateManager(preAllocCount: 4);
        mgr.Initialize(lastSegmentId: 0, firstLSN: 1);

        mgr.RotateSegment(firstLSN: 100, prevLastLSN: 99);

        // active = 2, pool ahead of it = 3, 4, 5, 6 — the same depth as before #784, just paid for on demand.
        Assert.That(mgr.ActiveSegment.SegmentId, Is.EqualTo(2));
        Assert.That(_fileIO.Exists(mgr.GetSegmentPath(3)), Is.True);
        Assert.That(_fileIO.Exists(mgr.GetSegmentPath(4)), Is.True);
        Assert.That(_fileIO.Exists(mgr.GetSegmentPath(5)), Is.True);
        Assert.That(_fileIO.Exists(mgr.GetSegmentPath(6)), Is.True);
    }

    [Test]
    public void Initialize_ContinuesFromLastSegmentId()
    {
        using var mgr = CreateManager();

        mgr.Initialize(lastSegmentId: 10, firstLSN: 5000);

        Assert.That(mgr.ActiveSegment.SegmentId, Is.EqualTo(11));
        Assert.That(mgr.ActiveSegment.FirstLSN, Is.EqualTo(5000));
    }

    #endregion

    #region RotateSegment

    [Test]
    public void RotateSegment_AdvancesToNextSegment()
    {
        using var mgr = CreateManager();
        mgr.Initialize(lastSegmentId: 0, firstLSN: 1);

        var oldId = mgr.ActiveSegment.SegmentId;
        mgr.RotateSegment(firstLSN: 1000, prevLastLSN: 999);

        Assert.That(mgr.ActiveSegment.SegmentId, Is.EqualTo(oldId + 1));
        Assert.That(mgr.ActiveSegment.FirstLSN, Is.EqualTo(1000));
        Assert.That(mgr.ActiveSegment.WriteOffset, Is.EqualTo(WalSegmentHeader.SizeInBytes));
    }

    [Test]
    public void RotateSegment_MultipleRotations_IncrementSegmentId()
    {
        using var mgr = CreateManager();
        mgr.Initialize(lastSegmentId: 0, firstLSN: 1);

        mgr.RotateSegment(firstLSN: 100, prevLastLSN: 99);
        Assert.That(mgr.ActiveSegment.SegmentId, Is.EqualTo(2));

        mgr.RotateSegment(firstLSN: 200, prevLastLSN: 199);
        Assert.That(mgr.ActiveSegment.SegmentId, Is.EqualTo(3));

        mgr.RotateSegment(firstLSN: 300, prevLastLSN: 299);
        Assert.That(mgr.ActiveSegment.SegmentId, Is.EqualTo(4));
    }

    [Test]
    public void RotateSegment_ReplenishesPreAllocatedPool()
    {
        using var mgr = CreateManager(preAllocCount: 2);
        mgr.Initialize(lastSegmentId: 0, firstLSN: 1);

        // After init: active=1, pre-alloc=2,3
        mgr.RotateSegment(firstLSN: 100, prevLastLSN: 99);

        // After rotate: active=2, pre-alloc=3,4
        Assert.That(_fileIO.Exists(mgr.GetSegmentPath(4)), Is.True);
    }

    #endregion

    #region GetSegmentPath

    [Test]
    public void GetSegmentPath_FormatsCorrectly()
    {
        using var mgr = CreateManager();

        var path = mgr.GetSegmentPath(1);

        Assert.That(path, Does.EndWith("0000000000000001.wal"));
    }

    [Test]
    public void GetSegmentPath_LargeSegmentId_FormatsCorrectly()
    {
        using var mgr = CreateManager();

        var path = mgr.GetSegmentPath(1234567890);

        Assert.That(path, Does.EndWith("0000001234567890.wal"));
    }

    #endregion

    #region ActiveSegmentUtilization

    [Test]
    public void ActiveSegmentUtilization_InitiallyAtHeaderRatio()
    {
        using var mgr = CreateManager(segmentSize: 64 * 1024);
        mgr.Initialize(lastSegmentId: 0, firstLSN: 1);

        var util = mgr.ActiveSegmentUtilization;

        // Header is 4096 bytes out of 64KB = ~6.25%
        Assert.That(util, Is.GreaterThan(0.0));
        Assert.That(util, Is.LessThan(0.1));
    }

    #endregion

    #region Dispose

    [Test]
    public void Dispose_ClearsActiveSegment()
    {
        var mgr = CreateManager();
        mgr.Initialize(lastSegmentId: 0, firstLSN: 1);

        mgr.Dispose();

        Assert.That(mgr.ActiveSegment, Is.Null);
    }

    [Test]
    public void Dispose_Idempotent()
    {
        var mgr = CreateManager();
        mgr.Initialize(lastSegmentId: 0, firstLSN: 1);

        mgr.Dispose();
        Assert.DoesNotThrow(() => mgr.Dispose());
    }

    #endregion
}
