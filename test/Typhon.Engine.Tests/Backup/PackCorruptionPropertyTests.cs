using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Typhon.Engine.Tests.Backup;

/// <summary>
/// T7 / AC-V1 / BKP-08: every byte of a pack is under a CRC that verify checks, and restore never hands back wrong bytes. CRC32C catches every
/// single-bit error by construction, so what this proves is COVERAGE: that no byte escapes a checked CRC, and that verify and restore actually
/// check the ones they read (plan 1413 §6.3).
/// </summary>
[TestFixture]
internal sealed class PackCorruptionPropertyTests
{
    private const string Root = "/s";
    private const string Target = "/r";

    private InMemorySparseFileSystem _fs;
    private BackupTestImage _image;
    private BackupPointInfo[] _points;
    private Dictionary<string, byte[]> _pristine;
    private Dictionary<uint, Dictionary<string, (long Length, uint Crc)>> _restored;
    private Dictionary<string, Layout> _layouts;

    /// <summary>What each byte of a pack is, and which check owns it.</summary>
    private enum Region : byte
    {
        Header,
        PageFrame,
        TailBlock,
        TailDir,
        Suspect,
        Index,
        Footer,
        FileCrc,
    }

    private sealed class Layout
    {
        public Region[] Regions;
        public int SuspectCount;

        /// <summary>For a frame byte, the index of its entry in <see cref="Entries"/> (page) or <see cref="Blocks"/> (tail).</summary>
        public int[] Owner;
        public PackPageEntry[] Entries;
        public TailBlockEntry[] Blocks;
    }

    [OneTimeSetUp]
    public void BuildReferenceStore()
    {
        // A small engine database, 1 full + 2 incrementals, each with a WAL tail: packs of 5 to 25 KB. The tail is each segment's first
        // 8 KiB — its real header and first records, declared size kept — so a flip sweep spends its time on coverage, not on 1 MiB decodes.
        _image = BackupTestImages.WithShortTail(BackupTestImages.Unclean);
        _fs = new InMemorySparseFileSystem();
        _fs.CreateDirectory("/");
        _fs.CreateDirectory(Target);
        using (var store = BackupStore.OpenForWrite(Root, _image.DatabaseId, true, _fs))
        {
            var full = StoreTestKit.WritePoint(store, _image, withTail: true);
            var second = StoreTestKit.WritePoint(store, _image, full.ChainId, pages: [2, 3, 5], withTail: true, @override: StoreTestKit.Salted(_image, 2));
            var third = StoreTestKit.WritePoint(store, _image, full.ChainId, pages: [3, 7], withTail: true, @override: StoreTestKit.Salted(_image, 3));
            _points = [full, second, third];
        }

        _pristine = _points.ToDictionary(Path, p => _fs.ReadAll(Path(p)));
        _layouts = _points.ToDictionary(Path, p => MapLayout(Path(p)));
        _restored = new Dictionary<uint, Dictionary<string, (long Length, uint Crc)>>();
        foreach (var p in _points)
        {
            _restored[p.BackupId] = RestoreToTarget(p.BackupId);
        }
    }

    private static string Path(BackupPointInfo p) => InMemorySparseFileSystem.Normalize(p.PackPath);

    private Layout MapLayout(string path)
    {
        var bytes = _fs.ReadAll(path);
        var regions = new Region[bytes.Length];
        using var reader = PackReader.Open(_fs, path);
        var f = reader.Footer;
        Array.Fill(regions, Region.Header, 0, PackFormat.HeaderSize);
        var dataStart = PackFormat.HeaderSize;
        var covered = new bool[bytes.Length];
        var owner = new int[bytes.Length];
        var entries = reader.LoadIndex();
        for (var k = 0; k < entries.Length; k++)
        {
            var e = entries[k];
            for (var i = e.DataOffset - PackFormat.FramePrefixSize; e.StoredLength > 0 && i < e.DataOffset + e.StoredLength; i++)
            {
                regions[i] = Region.PageFrame;
                owner[i] = k;
                covered[i] = true;
            }
        }

        var blocks = reader.LoadTailDirectory().Blocks;
        for (var k = 0; k < blocks.Length; k++)
        {
            var b = blocks[k];
            for (var i = b.DataOffset - PackFormat.FramePrefixSize; i < b.DataOffset + b.StoredLength; i++)
            {
                regions[i] = Region.TailBlock;
                owner[i] = k;
                covered[i] = true;
            }
        }

        for (var i = dataStart; i < f.DataRegionEnd; i++)
        {
            Assert.That(covered[i], Is.True, $"{path}: data byte {i} belongs to no frame — no check would ever read it");
        }

        Array.Fill(regions, Region.TailDir, (int)f.TailDirOffset, (int)(f.SuspectOffset - f.TailDirOffset));
        Array.Fill(regions, Region.Suspect, (int)f.SuspectOffset, (int)(f.IndexOffset - f.SuspectOffset));
        Array.Fill(regions, Region.Index, (int)f.IndexOffset, (int)(bytes.Length - PackFormat.FooterSize - f.IndexOffset));
        Array.Fill(regions, Region.Footer, bytes.Length - PackFormat.FooterSize, PackFormat.FooterSize - 4);
        Array.Fill(regions, Region.FileCrc, bytes.Length - 4, 4);
        return new Layout { Regions = regions, SuspectCount = f.SuspectCount, Owner = owner, Entries = entries, Blocks = blocks };
    }

    /// <summary>
    /// The code a flip at <paramref name="offset"/> must raise. A frame can absorb a flip (an LZ4 match offset moved onto another run of the
    /// same bytes decodes to the same page): then the content is intact and only FileCRC can see the change. That is decided by decoding the
    /// damaged frame, never assumed.
    /// </summary>
    private string ExpectedCode(string path, Layout layout, int offset)
    {
        var region = layout.Regions[offset];
        if (region == Region.PageFrame && DecodesUnchanged(path, layout.Entries[layout.Owner[offset]]))
        {
            return "BKP-VER-10";
        }

        if (region == Region.TailBlock && DecodesUnchanged(path, layout.Blocks[layout.Owner[offset]]))
        {
            return "BKP-VER-10";
        }

        return CodeOf(region);
    }

    private bool DecodesUnchanged(string path, in PackPageEntry entry)
    {
        var frame = _fs.ReadAll(path).AsSpan((int)entry.DataOffset - PackFormat.FramePrefixSize, PackFormat.FramePrefixSize + (int)entry.StoredLength);
        var page = new byte[PackFormat.PageSize];
        return PackReader.DecodePage(entry, frame, page, out _);
    }

    private bool DecodesUnchanged(string path, in TailBlockEntry block)
    {
        var now = _fs.ReadAll(path);
        var before = _pristine[path];
        var start = (int)block.DataOffset - PackFormat.FramePrefixSize;
        var length = PackFormat.FramePrefixSize + block.StoredLength;
        var a = Decode(now.AsSpan(start, length), block);
        var b = Decode(before.AsSpan(start, length), block);
        return a != null && b != null && a.AsSpan().SequenceEqual(b);
    }

    private static byte[] Decode(ReadOnlySpan<byte> frame, in TailBlockEntry block)
    {
        if (BitConverter.ToInt32(frame) != block.StoredLength)
        {
            return null;
        }

        var payload = frame[PackFormat.FramePrefixSize..];
        var raw = new byte[block.RawLength];
        if (block.StoredLength == block.RawLength)
        {
            payload.CopyTo(raw);
            return raw;
        }

        return K4os.Compression.LZ4.LZ4Codec.Decode(payload, raw) == block.RawLength ? raw : null;
    }

    private static string CodeOf(Region region) => region switch
    {
        Region.Header => "BKP-VER-02",
        Region.PageFrame => "BKP-VER-11",
        Region.TailBlock => "BKP-VER-09",
        Region.TailDir or Region.Suspect => "BKP-VER-05",
        Region.Index => "BKP-VER-04",
        Region.Footer => "BKP-VER-03",
        Region.FileCrc => "BKP-VER-10",
        _ => throw new ArgumentOutOfRangeException(nameof(region)),
    };

    /// <summary>The restore of point <paramref name="id"/>, as files (relative path → length and CRC); throws what the restore throws.</summary>
    private Dictionary<string, (long Length, uint Crc)> RestoreToTarget(uint id)
    {
        _fs.DeleteDirectory(Target + "/" + _image.DatabaseName + ".typhon");
        var outcome = RestoreStager.Run(Root, PointSelector.ById(id), Target, fs: _fs);
        var bundle = InMemorySparseFileSystem.Normalize(outcome.Target);
        return _fs.AllFiles.Where(f => f.StartsWith(bundle + "/", StringComparison.Ordinal) && !f.EndsWith("/restore.json", StringComparison.Ordinal))
            .ToDictionary(f => f[bundle.Length..], _fs.Fingerprint);
    }

    /// <summary>Gate: every byte outside DATA, every 8th inside; the whole store verified and every point restored on a seeded sample.</summary>
    [Test]
    [CancelAfter(120_000)]
    public void EveryFlip_IsDetected_AndNoRestoreReturnsWrongBytes() => FlipSweep(dataStride: 8, outsideStride: 1, storeSamples: 24);

    [Test]
    [CancelAfter(1_800_000)]
    [Explicit("Every byte of every pack, each with a whole-store verify and a restore of every point — minutes; the gate runs the strided form")]
    [Category("Nightly")]
    public void EveryFlip_IsDetected_AndNoRestoreReturnsWrongBytes_Exhaustive() => FlipSweep(dataStride: 1, outsideStride: 1, storeSamples: int.MaxValue);

    private void FlipSweep(int dataStride, int outsideStride, int storeSamples)
    {
        var random = TestSeed.Random(nameof(FlipSweep));
        var clock = Stopwatch.StartNew();
        var flips = 0;
        foreach (var point in _points)
        {
            var path = Path(point);
            var pristine = _pristine[path];
            var layout = _layouts[path];
            var offsets = Enumerable.Range(0, pristine.Length)
                .Where(i => layout.Regions[i] is Region.PageFrame or Region.TailBlock ? i % dataStride == 0 : i % outsideStride == 0)
                .ToList();
            var storeAt = offsets.OrderBy(_ => random.Next()).Take(storeSamples).ToHashSet();
            var view = StoreView.Open(Root, _fs, trustCatalog: false);
            foreach (var offset in offsets)
            {
                var bit = random.Next(8);
                _fs.Poke(path, offset, [(byte)(pristine[offset] ^ (1 << bit))]);
                try
                {
                    var region = layout.Regions[offset];
                    var context = $"{System.IO.Path.GetFileName(path)} byte {offset} bit {bit} ({region}). {TestSeed.Repro}";
                    var expected = ExpectedCode(path, layout, offset);
                    CheckPack(view, point, region, expected, context);
                    if (storeAt.Contains(offset))
                    {
                        CheckStore(point, expected, context);
                        CheckRestores(point, expected == "BKP-VER-10" || (region == Region.Suspect && layout.SuspectCount == 0), context);
                    }
                }
                finally
                {
                    _fs.Poke(path, offset, [pristine[offset]]);
                }

                flips++;
            }
        }

        TestContext.Out.WriteLine($"{flips} flips in {clock.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// The damaged pack alone: quick must see every flip outside DATA but the FileCRC field (full includes every quick check, so this also
    /// proves full sees it, under the same code); full must see every other flip, under the code of the section that owns the byte.
    /// </summary>
    private void CheckPack(StoreView view, BackupPointInfo point, Region region, string expected, string context)
    {
        var file = System.IO.Path.GetFileName(Path(point));
        var level = region is Region.PageFrame or Region.TailBlock or Region.FileCrc ? VerifyLevel.Full : VerifyLevel.Quick;
        var pack = BackupVerifier.VerifyPackFile(_fs, view, file, level);
        Assert.That(pack.Status, Is.Not.EqualTo(PackVerifyStatus.Ok), $"{level} verify missed a flip: {context}");
        Assert.That(pack.Findings.Select(f => f.Code), Does.Contain(expected), context);
    }

    /// <summary>The whole store, full: the pack damaged, every point that needs it not restorable, the others still restorable.</summary>
    private void CheckStore(BackupPointInfo point, string expected, string context)
    {
        var full = BackupVerifier.VerifyStore(Root, VerifyLevel.Full, null, _fs);
        var pack = full.Packs.Single(p => p.BackupId == point.BackupId);
        Assert.That(pack.Status, Is.Not.EqualTo(PackVerifyStatus.Ok), "full verify missed a flip: " + context);
        Assert.That(pack.Findings.Select(f => f.Code), Does.Contain(expected), context);
        Assert.That(full.Verdict, Is.Not.EqualTo(VerifyVerdict.Ok), context);
        foreach (var p in full.Points)
        {
            // A linear chain: every point at or after the damaged pack needs it.
            Assert.That(p.Restorable, Is.EqualTo(p.Id < point.BackupId), $"point #{p.Id}: {context}");
        }
    }

    private void CheckRestores(BackupPointInfo damaged, bool contentIntact, string context)
    {
        foreach (var p in _points)
        {
            Dictionary<string, (long Length, uint Crc)> restored;
            try
            {
                restored = RestoreToTarget(p.BackupId);
            }
            catch (Exception ex) when (ex is BackupPackCorruptException or BackupChainBrokenException or BackupFormatUnsupportedException)
            {
                continue;
            }

            // A restore that did not refuse must be exact: the damaged byte was one it never needed (an older point's, a shadowed page's,
            // the FileCRC field) — never one it read and let through.
            Assert.That(restored.Keys, Is.EquivalentTo(_restored[p.BackupId].Keys), $"restore of #{p.BackupId}: {context}");
            foreach (var (file, bytes) in restored)
            {
                Assert.That(bytes, Is.EqualTo(_restored[p.BackupId][file]), $"restore of #{p.BackupId} wrote wrong bytes in {file}: {context}");
            }

            // The damaged pack's own point reads every byte of it that carries content: it may succeed only when the flip changed none (the
            // FileCRC field, an empty SUSPECT section's reserved word, a frame that decodes the same), and then it was exact (above).
            Assert.That(p.BackupId != damaged.BackupId || contentIntact, Is.True,
                $"restore of #{p.BackupId} read a damaged byte and succeeded: {context}");
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public void EveryTruncation_IsDetected()
    {
        var random = TestSeed.Random(nameof(EveryTruncation_IsDetected));
        foreach (var point in _points)
        {
            var path = Path(point);
            var pristine = _pristine[path];
            var cuts = Enumerable.Range(0, (pristine.Length / 4096) + 1).Select(i => i * 4096)
                .Concat(Enumerable.Range(0, 64).Select(_ => random.Next(0, pristine.Length)))
                .Where(c => c < pristine.Length).Distinct();
            foreach (var cut in cuts)
            {
                _fs.Resize(path, cut);
                try
                {
                    var report = BackupVerifier.VerifyStore(Root, VerifyLevel.Quick, null, _fs);
                    var pack = report.Packs.Single(p => p.BackupId == point.BackupId);
                    Assert.That(pack.Status, Is.EqualTo(PackVerifyStatus.Unreadable), $"{path} cut at {cut}");
                    Assert.That(pack.Findings.Select(f => f.Code), Does.Contain("BKP-VER-03"), $"{path} cut at {cut}");
                }
                finally
                {
                    _fs.Resize(path, 0);
                    _fs.Poke(path, 0, pristine);
                }
            }
        }

        Assert.That(BackupVerifier.VerifyStore(Root, VerifyLevel.Full, null, _fs).Verdict, Is.EqualTo(VerifyVerdict.Ok), "the store was restored");
    }

    [Test]
    [CancelAfter(30_000)]
    public void ALostCatalog_IsRebuiltByteEqual()
    {
        var before = _fs.ReadAll(Root + "/catalog.json");
        _fs.DeleteFile(Root + "/catalog.json");
        BackupStore.OpenForWrite(Root, _image.DatabaseId, true, _fs).Dispose();

        Assert.That(_fs.ReadAll(Root + "/catalog.json"), Is.EqualTo(before));
    }

    [Test]
    [CancelAfter(30_000)]
    public void RandomBytesAsAFrame_AreRefused_WithoutACrash()
    {
        var random = TestSeed.Random(nameof(RandomBytesAsAFrame_AreRefused_WithoutACrash));
        var page = new byte[PackFormat.PageSize];
        var frame = new byte[PackFormat.FramePrefixSize + PackFormat.PageSize];
        for (var round = 0; round < 8_000; round++)
        {
            var stored = random.Next(1, PackFormat.PageSize);
            random.NextBytes(frame.AsSpan(PackFormat.FramePrefixSize, stored));
            BitConverter.TryWriteBytes(frame, stored);
            var entry = new PackPageEntry(5, (uint)stored, 1000, (uint)random.Next());

            Assert.That(PackReader.DecodePage(entry, frame.AsSpan(0, PackFormat.FramePrefixSize + stored), page, out var error), Is.False,
                $"round {round}: random bytes accepted as a page. {TestSeed.Repro}");
            Assert.That(error, Is.Not.Null);
        }

        // And through a real pack: a page frame overwritten with noise of its own length is refused by restore.
        var victim = _points[1];
        var path = Path(victim);
        PackPageEntry target;
        using (var reader = PackReader.Open(_fs, path))
        {
            target = reader.LoadIndex().First(e => e.PageId >= 2 && e.StoredLength > 64 && e.StoredLength < PackFormat.PageSize);
        }

        var noise = new byte[target.StoredLength];
        random.NextBytes(noise);
        _fs.Poke(path, target.DataOffset, noise);
        try
        {
            Assert.Throws<BackupPackCorruptException>(() => RestoreToTarget(victim.BackupId));
        }
        finally
        {
            _fs.Poke(path, 0, _pristine[path]);
        }
    }
}
