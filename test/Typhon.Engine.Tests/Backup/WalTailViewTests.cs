using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// <see cref="WalTailView"/> reads a segment the way restore recreates it: its copied length as written, zeros past it, whatever the source holds there
/// (C3). Over a live source the bytes past the length are a batch being written, which the tail must not see.
/// </summary>
[TestFixture]
public class WalTailViewTests
{
    private const int Page = 4096;

    [Test]
    public void OverASource_ReadsZerosPastTheCopiedLength()
    {
        using var source = new InMemoryWalFileIO();
        var path = Path.Combine("wal", "0000000000000001.wal");
        var written = new byte[3 * Page];
        written.AsSpan().Fill(0xAB);
        using (var handle = source.OpenSegment(path, false))
        {
            source.WriteAligned(handle, 0, written);
        }

        var view = WalTailView.OverSource(source, new Dictionary<string, long> { [path] = 2 * Page }, null);
        AssertReadsTwoPagesThenZeros(view, path);
    }

    /// <summary>BKP-04: over a live engine's segments, a read without the reclaim floor is refused, not served.</summary>
    [Test]
    public void OverASource_RefusesEveryReadOnceTheFloorIsGone()
    {
        using var source = new InMemoryWalFileIO();
        var path = Path.Combine("wal", "0000000000000001.wal");
        using (var handle = source.OpenSegment(path, false))
        {
            source.WriteAligned(handle, 0, new byte[Page]);
        }

        var held = true;
        var view = WalTailView.OverSource(source, new Dictionary<string, long> { [path] = Page }, () => held);
        using var open = view.OpenSegmentForRead(path);
        view.ReadAligned(open, 0, new byte[Page]);
        held = false;
        Assert.Throws<InvalidOperationException>(() => view.ReadAligned(open, 0, new byte[Page]));
        Assert.Throws<InvalidOperationException>(() => view.OpenSegmentForRead(path));
    }

    [Test]
    public void OverPrefixes_ReadsZerosPastThePrefix()
    {
        var path = Path.Combine("tail", "0000000000000001.wal");
        var prefix = new byte[2 * Page];
        prefix.AsSpan().Fill(0xAB);
        var view = WalTailView.OverPrefixes(new Dictionary<string, ReadOnlyMemory<byte>> { [path] = prefix });
        AssertReadsTwoPagesThenZeros(view, path);
    }

    [Test]
    public void IsReadOnly()
    {
        var view = WalTailView.OverPrefixes(new Dictionary<string, ReadOnlyMemory<byte>>());
        Assert.Throws<NotSupportedException>(() => view.OpenSegment("x", false));
        Assert.Throws<NotSupportedException>(() => view.Delete("x"));
        Assert.That(view.Exists("x"), Is.False);
    }

    private static void AssertReadsTwoPagesThenZeros(WalTailView view, string path)
    {
        Assert.That(view.Exists(path), Is.True);
        var buffer = new byte[4 * Page];
        buffer.AsSpan().Fill(0xCD);   // stale bytes the view must overwrite
        using (var handle = view.OpenSegmentForRead(path))
        {
            view.ReadAligned(handle, 0, buffer);
        }

        Assert.Multiple(() =>
        {
            Assert.That(buffer.AsSpan(0, 2 * Page).IndexOfAnyExcept((byte)0xAB), Is.EqualTo(-1), "the copied pages read as written");
            Assert.That(buffer.AsSpan(2 * Page).IndexOfAnyExcept((byte)0), Is.EqualTo(-1), "past the copied length a restored segment holds zeros");
        });

        var past = new byte[Page];
        past.AsSpan().Fill(0xCD);
        using (var handle = view.OpenSegmentForRead(path))
        {
            view.ReadAligned(handle, 3 * Page, past);
        }

        Assert.That(past.AsSpan().IndexOfAnyExcept((byte)0), Is.EqualTo(-1), "a read wholly past the copied length is all zeros");
    }
}
