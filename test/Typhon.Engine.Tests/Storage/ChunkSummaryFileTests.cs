using NUnit.Framework;
using System;
using System.IO;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// The chunk summary file's format (#1143): what a clean close writes is read back exactly, and anything else — another close's file, damage, an entry no
/// close can write — is refused whole, so the open falls back to reading the pages.
/// </summary>
[TestFixture]
class ChunkSummaryFileTests
{
    private const ulong Nonce = 0x1143_0000_ABCDUL;

    private string _dir;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "Typhon.Tests", nameof(ChunkSummaryFileTests), TestContext.CurrentContext.Test.Name);
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    private string PathOf(string name) => Path.Combine(_dir, name);

    private static ChunkSegmentSummary[] Segments() =>
    [
        new(12, 70, 1234, [0b1011UL, 1UL << 5]),
        new(40, 1, 3, [1UL]),
    ];

    private static ClusterListSummary[] Clusters() =>
    [
        new(12, 7, [1, 2, 7, 9]),
        new(55, -1, []),
    ];

    private byte[] WriteBytes(ChunkSegmentSummary[] segments, ClusterListSummary[] clusters)
    {
        var path = PathOf("chunk-summary");
        ChunkSummaryFile.Write(path, Nonce, segments, clusters, flushToDisk: false);
        return File.ReadAllBytes(path);
    }

    [Test]
    public void WhatACloseWrites_IsReadBackExactly()
    {
        var bytes = WriteBytes(Segments(), Clusters());

        var contents = ChunkSummaryFile.Parse(bytes, Nonce, out var result);

        Assert.That(result, Is.EqualTo(ChunkSummaryReadResult.Loaded));
        Assert.That(contents.Segments, Has.Count.EqualTo(2));
        var s = contents.Segments[12];
        Assert.That((s.PageCount, s.AllocatedCount), Is.EqualTo((70, 1234)));
        Assert.That(s.PagesWithRoom, Is.EqualTo(new[] { 0b1011UL, 1UL << 5 }));
        Assert.That(s.HasRoom(0) && s.HasRoom(1) && !s.HasRoom(2) && s.HasRoom(3) && s.HasRoom(69), Is.True);
        Assert.That(contents.Segments[40].AllocatedCount, Is.EqualTo(3));

        Assert.That(contents.Clusters, Has.Count.EqualTo(2));
        Assert.That(contents.Clusters[12].FreeClusterHead, Is.EqualTo(7));
        Assert.That(contents.Clusters[12].ActiveClusterIds, Is.EqualTo(new[] { 1, 2, 7, 9 }));
        Assert.That(contents.Clusters[55].FreeClusterHead, Is.EqualTo(-1));
        Assert.That(contents.Clusters[55].ActiveClusterIds, Is.Empty);
    }

    [Test]
    public void AFileFromAnotherClose_IsANonceMismatch()
    {
        var bytes = WriteBytes(Segments(), Clusters());

        Assert.That(ChunkSummaryFile.Parse(bytes, Nonce + 1, out var result), Is.Null);
        Assert.That(result, Is.EqualTo(ChunkSummaryReadResult.NonceMismatch));
        Assert.That(ChunkSummaryFile.Parse(bytes, 0, out result), Is.Null, "nonce 0 is what a close that wrote no summary records: never a match");
        Assert.That(result, Is.EqualTo(ChunkSummaryReadResult.NonceMismatch));
    }

    [TestCase(3, TestName = "AFlippedByte_InTheHeader_IsInvalid")]
    [TestCase(40, TestName = "AFlippedByte_InASegmentEntry_IsInvalid")]
    [TestCase(-6, TestName = "AFlippedByte_InAClusterEntry_IsInvalid")]
    public void AFlippedByte_IsInvalid(int offset)
    {
        var bytes = WriteBytes(Segments(), Clusters());
        bytes[offset < 0 ? bytes.Length + offset : offset] ^= 0x10;

        Assert.That(ChunkSummaryFile.Parse(bytes, Nonce, out var result), Is.Null);
        Assert.That(result, Is.EqualTo(ChunkSummaryReadResult.Invalid));
    }

    [Test]
    public void ATruncatedFile_IsInvalid()
    {
        var bytes = WriteBytes(Segments(), Clusters());

        Assert.That(ChunkSummaryFile.Parse(bytes.AsSpan(0, bytes.Length - 4), Nonce, out var result), Is.Null);
        Assert.That(result, Is.EqualTo(ChunkSummaryReadResult.Invalid));
        Assert.That(ChunkSummaryFile.Parse(bytes.AsSpan(0, 20), Nonce, out result), Is.Null);
        Assert.That(result, Is.EqualTo(ChunkSummaryReadResult.Invalid));
    }

    /// <summary>
    /// A cluster list no close can write — ids out of order, a repeated id, chunk 0, a head that is not in the list — is refused, intact CRC or not.
    /// </summary>
    [TestCase(new[] { 2, 1 }, -1, TestName = "AClusterListOutOfOrder_IsInvalid")]
    [TestCase(new[] { 3, 3 }, -1, TestName = "AClusterListWithARepeat_IsInvalid")]
    [TestCase(new[] { 0, 3 }, -1, TestName = "AClusterListHoldingChunkZero_IsInvalid")]
    [TestCase(new[] { 1, 3 }, 2, TestName = "AFreeHeadOutsideTheList_IsInvalid")]
    public void AClusterListNoCloseCanWrite_IsInvalid(int[] ids, int freeHead)
    {
        var bytes = WriteBytes(Segments(), [new ClusterListSummary(12, freeHead, ids)]);

        Assert.That(ChunkSummaryFile.Parse(bytes, Nonce, out var result), Is.Null);
        Assert.That(result, Is.EqualTo(ChunkSummaryReadResult.Invalid));
    }

    [Test]
    public void NoFile_IsMissing()
    {
        Assert.That(ChunkSummaryFile.TryRead(PathOf("absent"), Nonce, out var result), Is.Null);
        Assert.That(result, Is.EqualTo(ChunkSummaryReadResult.Missing));
    }

    [Test]
    public void ACloseWithNothingToRecord_StillWritesAReadableFile()
    {
        var bytes = WriteBytes([], []);

        var contents = ChunkSummaryFile.Parse(bytes, Nonce, out var result);

        Assert.That(result, Is.EqualTo(ChunkSummaryReadResult.Loaded));
        Assert.That(contents.Segments, Is.Empty);
        Assert.That(contents.Clusters, Is.Empty);
    }
}
