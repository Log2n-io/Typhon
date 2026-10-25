using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Typhon.Engine.Tests.Backup;

/// <summary>
/// BKP-13 (05 §2, plan 1415 §3.2): a compacted pack materialises to exactly its point's image — each page the newest version along the chain, a hole
/// as zeros — carries the point's identity, LSNs, times and tail, keeps the suspect flag of the pack each page came from, and changes nothing else in
/// the store. Synthetic chains of depth 1 to 50 (growing images, holes, overlapping rewrites, tails); the engine-produced chain is in
/// <see cref="CompactionOfAnEngineChainTests"/>.
/// </summary>
[TestFixture]
internal sealed class CompactionEquivalenceTests
{
    private const string Root = SyntheticChain.Root;
    private const string Marker = "BKP-13: the compacted pack";

    internal static BackupCompactionResult Compact(IStoreFileSystem fs, uint id, BackupLifecycleOptions options = null)
    {
        using var session = BackupStore.OpenForMaintenance(Root, "compact", fs);
        return BackupCompactor.Compact(session, id, options);
    }

    /// <summary>A point's image as a restore materialises it, from whatever packs its chain resolves to now.</summary>
    internal static byte[] Materialize(InMemorySparseFileSystem fs, uint id)
    {
        var view = StoreView.Open(Root, fs, trustCatalog: false);
        var packs = PackChainPageSource.OpenPacks(fs, view, ChainResolver.Resolve(view, id));
        try
        {
            fs.CreateDirectory("/t");
            var path = "/t/" + Guid.NewGuid().ToString("N");
            using (var target = fs.OpenCreateNew(path))
            {
                ChainMaterializer.Materialize(packs, target);
            }

            var bytes = fs.ReadAll(path);
            fs.DeleteFile(path);
            return bytes;
        }
        finally
        {
            foreach (var p in packs)
            {
                p.Dispose();
            }
        }
    }

    private static Dictionary<string, (long, uint)> PackFingerprints(InMemorySparseFileSystem fs) =>
        fs.EnumerateFiles(Root + "/packs").ToDictionary(f => f.Name, f => fs.Fingerprint(Root + "/packs/" + f.Name));

    [TestCase(1, 11, false)]
    [TestCase(2, 12, true)]
    [TestCase(5, 13, false)]
    [TestCase(9, 14, true)]
    [TestCase(17, 15, false)]
    [TestCase(50, 16, false)]
    [CancelAfter(30_000)]
    [VerifiesRule("BKP-13")]
    public void APointCompacts_ToExactlyItsImage_AndNothingElseChanges(int depth, int seed, bool withTail)
    {
        var chain = SyntheticChain.Build(depth, seed, withTail: withTail, image: withTail ? BackupTestImages.Unclean : null);
        var fs = chain.Fs;
        var targets = depth >= 5 ? new[] { depth, (depth / 2) + 1 } : new[] { depth };
        var before = Enumerable.Range(1, depth).ToDictionary(i => (uint)i, i => Materialize(fs, (uint)i));

        foreach (var x in targets.Select(t => (uint)t))
        {
            var packsBefore = PackFingerprints(fs);
            var hwm = fs.ReadAll(Root + "/store.hwm");
            var result = Compact(fs, x);

            Assert.That(result.AlreadyBase, Is.EqualTo(x == 1), "only the full point is already a base");
            Assert.That(result.Pages, Is.EqualTo(chain.ExpectedPageCount((int)x - 1)));
            AssertCompactedEquivalent(fs, chain, x, before[x], expectedSuspects: []);
            if (x != 1)
            {
                var holes = Enumerable.Range(0, result.Pages).Count(p => !chain.Holds((int)x - 1, p));
                Assert.That(result.HolePages, Is.EqualTo(holes), "every hole is a zero entry");
            }

            foreach (var (name, fingerprint) in packsBefore)
            {
                Assert.That(fs.Fingerprint(Root + "/packs/" + name), Is.EqualTo(fingerprint), $"{name} changed");
            }

            Assert.That(fs.ReadAll(Root + "/store.hwm"), Is.EqualTo(hwm), "no id allocated");
            Assert.That(Compact(fs, x).AlreadyBase, Is.True, "a second compaction is a no-op");
        }

        foreach (var (id, image) in before)
        {
            Assert.That(Materialize(fs, id).AsSpan().SequenceEqual(image), Is.True, $"point #{id} no longer materialises as before");
        }

        Assert.That(BackupVerifier.VerifyStore(Root, VerifyLevel.Full, null, fs).Verdict, Is.EqualTo(VerifyVerdict.Ok));
    }

    /// <summary>The growth of a chain's image leaves pages no point wrote: they are zero entries, so the base holds every page (DL-1).</summary>
    [Test]
    [CancelAfter(30_000)]
    [VerifiesRule("BKP-13")]
    public void Holes_AreZeroEntries_SoTheBaseHoldsEveryPage()
    {
        var chain = SyntheticChain.Build(17, 1415, firstPages: 40, maxPages: 400);
        var fs = chain.Fs;
        var n = chain.ExpectedPageCount(16);
        var holes = Enumerable.Range(0, n).Count(p => !chain.Holds(16, p));
        Assert.That(holes, Is.GreaterThan(0), "premise: the image grew past pages anyone wrote");

        var result = Compact(fs, 17);

        Assert.That(result.HolePages, Is.EqualTo(holes));
        using var reader = PackReader.Open(fs, Root + "/packs/00000017-C.tpack");
        var index = reader.LoadIndex();
        Assert.That(index.Select(e => (int)e.PageId), Is.EqualTo(Enumerable.Range(0, n)), "one entry per page of the image");
        Assert.That(index.Where(e => !chain.Holds(16, (int)e.PageId)).All(e => e.StoredLength == 0 && e.PageCrc == PackFormat.ZeroPageCrc), Is.True);
    }

    /// <summary>A Compacted pack missing a page is refused by the writer: dropping holes cannot slip through.</summary>
    [Test]
    [CancelAfter(15_000)]
    public void ACompactedPackMissingAPage_IsRefusedByTheWriter()
    {
        var chain = SyntheticChain.Build(3, 7);
        using var session = BackupStore.OpenForMaintenance(Root, "compact", chain.Fs);
        var writer = session.BeginCompaction(session.View.Find(3).Original);
        var n = chain.ExpectedPageCount(2);
        writer.SetImage(n, chain.Image.CheckpointLsn, 1);
        for (var p = 0; p < n - 1; p++)
        {
            writer.AddPage(p, chain.ExpectedPage(2, p), false);
        }

        var ex = Assert.Throws<InvalidOperationException>(() => writer.Complete(chain.Image.CheckpointLsn, 2));
        Assert.That(ex.Message, Does.Contain($"a Compacted point holds {n - 1} of its {n} pages"));
        writer.Abort();
        Assert.That(chain.Fs.EnumerateFiles(Root + "/packs").Any(f => f.Name.Contains("-C.")), Is.False);
    }

    /// <summary>
    /// The suspect set follows each page to the pack its newest version came from: a page rewritten after it was suspect is no longer suspect, one
    /// suspect in the base and never rewritten still is.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("BKP-13")]
    public void TheSuspectSet_IsThatOfThePackEachPageCameFrom()
    {
        var (fs, image) = SuspectChain();
        var before = Materialize(fs, 3);

        Compact(fs, 3);

        AssertCompactedEquivalent(fs, null, 3, before, expectedSuspects: [7, 9]);
    }

    /// <summary>The verifier can fail: a compacted pack built with the OLDEST version of each page (a merge that lets the base win).</summary>
    [Test]
    [CancelAfter(15_000)]
    [RuleMutant("BKP-13")]
    public void Mutant_OldestVersionWins_IsReported()
    {
        var chain = SyntheticChain.Build(6, 21);
        var fs = chain.Fs;
        var before = Materialize(fs, 6);
        WriteCompactedByHand(fs, 6, p => chain.Holds(0, p) && p >= 2 ? chain.ExpectedPage(0, p) : chain.ExpectedPage(5, p), _ => false);

        RuleMutants.AssertDetects("BKP-13", Marker, () => AssertCompactedEquivalent(fs, chain, 6, before, expectedSuspects: []));
    }

    /// <summary>The verifier can fail: a compacted pack whose pages are right but whose suspect set was not carried.</summary>
    [Test]
    [CancelAfter(15_000)]
    [RuleMutant("BKP-13")]
    public void Mutant_SuspectSetNotCarried_IsReported()
    {
        var (fs, _) = SuspectChain();
        var before = Materialize(fs, 3);
        var pages = before.Chunk(PackFormat.PageSize).ToArray();
        WriteCompactedByHand(fs, 3, p => pages[p], _ => false);

        RuleMutants.AssertDetects("BKP-13", Marker, () => AssertCompactedEquivalent(fs, null, 3, before, expectedSuspects: [7, 9]));
    }

    [Test]
    [CancelAfter(15_000)]
    public void Cancellation_PublishesNothing_AndProgressRisesToTheChainsSize()
    {
        var chain = SyntheticChain.Build(9, 31, firstPages: 1200, maxPages: 1300);
        var fs = chain.Fs;
        var progress = new List<(long Done, long Total)>();
        using var cts = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => Compact(fs, 9, new BackupLifecycleOptions
        {
            Cancellation = cts.Token,
            Progress = (done, total) =>
            {
                progress.Add((done, total));
                cts.Cancel();
            },
        }));

        Assert.That(progress, Has.Count.EqualTo(1), "cancelled at the first check");
        Assert.That(fs.EnumerateFiles(Root + "/packs").Any(f => f.Name.Contains("-C.")), Is.False, "no -C and no temp");

        progress.Clear();
        Compact(fs, 9, new BackupLifecycleOptions { Progress = (done, total) => progress.Add((done, total)) });
        Assert.That(progress.Select(p => p.Done), Is.Ordered);
        Assert.That(progress.Select(p => p.Total).Distinct().Single(), Is.EqualTo(fs.EnumerateFiles(Root + "/packs")
            .Where(f => f.Name.EndsWith(".tpack", StringComparison.Ordinal) && !f.Name.Contains("-C.")).Sum(f => f.Length)));
    }

    /// <summary>
    /// BKP-13's publish rule: a compacted pack becomes visible only after a full verify of the sealed temp passed. Here the temp is damaged after it is
    /// sealed and before that verify reads it: the compaction fails, nothing is visible, and the point still resolves through its chain.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("BKP-13")]
    public void ATempDamagedBeforeItsVerify_IsNeverPublished()
    {
        var chain = SyntheticChain.Build(4, 41);
        var inner = chain.Fs;
        var before = Materialize(inner, 4);
        var poked = false;
        var fs = new HookedFileSystem(inner)
        {
            BeforeOpenRead = path =>
            {
                if (!poked && path.EndsWith("-C.tpack.tmp", StringComparison.Ordinal))
                {
                    poked = true;
                    var at = PackFormat.HeaderSize + 100;
                    inner.Poke(path, at, [(byte)(inner.ReadAll(path)[at] ^ 0x10)]);
                }
            },
        };

        Assert.Throws<BackupPackCorruptException>(() => Compact(fs, 4));

        Assert.That(poked, Is.True, "premise: the verify read the temp");
        AssertNoUnverifiedCompactedPackIsVisible(inner);
        Assert.That(inner.EnumerateFiles(Root + "/packs").Any(f => f.Name.Contains("-C.")), Is.False, "no -C, no temp");
        Assert.That(Materialize(inner, 4).AsSpan().SequenceEqual(before), Is.True);
    }

    /// <summary>The verifier can fail: a damaged temp renamed into place before its verify (a publish that verifies after the rename, or never).</summary>
    [Test]
    [CancelAfter(15_000)]
    [RuleMutant("BKP-13")]
    public void Mutant_RenameBeforeVerify_IsReported()
    {
        var chain = SyntheticChain.Build(4, 42);
        var fs = chain.Fs;
        Compact(fs, 4);
        var path = Root + "/packs/00000004-C.tpack";
        var at = PackFormat.HeaderSize + 100;
        fs.Poke(path, at, [(byte)(fs.ReadAll(path)[at] ^ 0x10)]);

        RuleMutants.AssertDetects("BKP-13", "BKP-13: a visible compacted pack", () => AssertNoUnverifiedCompactedPackIsVisible(fs));
    }

    /// <summary>Every visible <c>-C</c> verifies in full: what the publish rule guarantees at any instant.</summary>
    internal static void AssertNoUnverifiedCompactedPackIsVisible(InMemorySparseFileSystem fs)
    {
        foreach (var f in fs.EnumerateFiles(Root + "/packs").Where(f => f.Name.EndsWith("-C.tpack", StringComparison.Ordinal)))
        {
            var failure = PackChecks.VerifyFull(fs, Root + "/packs/" + f.Name);
            Assert.That(failure, Is.Null, $"BKP-13: a visible compacted pack does not verify: {f.Name}: {failure}");
        }
    }

    /// <summary>
    /// X resolves to its compacted pack alone, which materialises to <paramref name="image"/> and carries X's identity, LSNs, times and tail, with the
    /// given suspect set; with a model (<paramref name="chain"/>), every page also equals the model's newest write.
    /// </summary>
    internal static void AssertCompactedEquivalent(InMemorySparseFileSystem fs, SyntheticChain chain, uint x, byte[] image, uint[] expectedSuspects)
    {
        var view = StoreView.Open(Root, fs, trustCatalog: false);
        var (compacted, original) = view.Find(x);
        var resolved = ChainResolver.Resolve(view, x);
        if (original.Kind == BackupKind.Full)
        {
            Assert.That(compacted, Is.Null);
            return;
        }

        Assert.That(compacted, Is.Not.Null, $"{Marker} of #{x} is missing");
        Assert.That(resolved.Packs, Is.EqualTo(new[] { compacted }), $"{Marker}: #{x} must resolve to its -C alone");
        Assert.That((compacted.Kind, compacted.ParentId, compacted.BaseId), Is.EqualTo((BackupKind.Compacted, 0u, x)));
        Assert.That((compacted.ChainId, compacted.TimelineId, compacted.StoreId, compacted.DatabaseId),
            Is.EqualTo((original.ChainId, original.TimelineId, original.StoreId, original.DatabaseId)), $"{Marker}: identity");
        Assert.That((compacted.CheckpointLsn, compacted.PointLsn, compacted.ImagePages, compacted.FreezeUtc, compacted.TailEndUtc),
            Is.EqualTo((original.CheckpointLsn, original.PointLsn, original.ImagePages, original.FreezeUtc, original.TailEndUtc)), $"{Marker}: C, P, N, times");
        Assert.That((compacted.FormatRevision, compacted.EngineVersion, compacted.Offline), Is.EqualTo((original.FormatRevision, original.EngineVersion,
            original.Offline)));
        Assert.That(compacted.PageCount, Is.EqualTo(compacted.ImagePages), $"{Marker} must hold every page of the image");

        using (var c = PackReader.Open(fs, view.Paths.PackPath(compacted.File)))
        using (var o = PackReader.Open(fs, view.Paths.PackPath(original.File)))
        {
            var ct = c.LoadTailDirectory().Segments.Select(s => (s.SegmentId, s.FirstLsn, s.RawLength, s.RawCrc));
            var ot = o.LoadTailDirectory().Segments.Select(s => (s.SegmentId, s.FirstLsn, s.RawLength, s.RawCrc));
            Assert.That(ct, Is.EqualTo(ot), $"{Marker}: the tail is not X's");
            Assert.That(c.LoadSuspects(), Is.EqualTo(expectedSuspects), $"{Marker}: the suspect set is not the one each page came with");
        }

        var actual = Materialize(fs, x);
        Assert.That(actual.Length, Is.EqualTo(image.Length), $"{Marker}: image size");
        for (var p = 0; p < image.Length / PackFormat.PageSize; p++)
        {
            var page = actual.AsSpan(p * PackFormat.PageSize, PackFormat.PageSize);
            Assert.That(page.SequenceEqual(image.AsSpan(p * PackFormat.PageSize, PackFormat.PageSize)), Is.True,
                $"{Marker}: page {p} of #{x} differs from the point's image");
            if (chain != null)
            {
                Assert.That(page.SequenceEqual(chain.ExpectedPage((int)x - 1, p)), Is.True, $"{Marker}: page {p} of #{x} is not the newest write");
            }
        }
    }

    /// <summary>
    /// #1 full, suspect {5, 7}; #2 rewrites 5 (clean) and 9 (suspect); #3 rewrites 11. Compacting #3: 7 (from #1) and 9 (from #2) are suspect, 5 is not.
    /// </summary>
    private static (InMemorySparseFileSystem Fs, BackupTestImage Image) SuspectChain()
    {
        var image = BackupTestImages.Clean;
        var fs = new InMemorySparseFileSystem();
        fs.CreateDirectory("/");
        using var store = BackupStore.OpenForWrite(Root, image.DatabaseId, true, fs);
        var full = Write(store, image, default, Enumerable.Range(0, Math.Max(16, image.PageCount)), [5, 7], 1);
        Write(store, image, full.ChainId, [0, 1, 5, 9], [9], 2);
        Write(store, image, full.ChainId, [0, 1, 11], [], 3);
        return (fs, image);

        static BackupPointInfo Write(BackupStore store, BackupTestImage image, Guid chain, IEnumerable<int> pages, int[] suspect, int salt)
        {
            var sink = store.BeginPoint(StoreTestKit.Plan(store, image, chain));
            var page = StoreTestKit.Salted(image, salt);
            sink.SetImage(Math.Max(16, image.PageCount), image.CheckpointLsn, salt);
            foreach (var p in pages)
            {
                sink.AddPage(p, page(p), suspect.Contains(p));
            }

            return sink.Complete(image.CheckpointLsn, salt + 100);
        }
    }

    /// <summary>A compacted pack for <paramref name="x"/> written page by page from <paramref name="page"/>, through the store's own publish.</summary>
    private static void WriteCompactedByHand(InMemorySparseFileSystem fs, uint x, Func<int, byte[]> page, Func<int, bool> suspect)
    {
        using var session = BackupStore.OpenForMaintenance(Root, "compact", fs);
        var source = session.View.Find(x).Original;
        using var point = PackReader.Open(fs, session.View.Paths.PackPath(source.File));
        var writer = session.BeginCompaction(source);
        writer.SetImage(point.Header.ImagePages, point.Header.CheckpointLsn, point.Header.FreezeUtcTicks);
        for (var p = 0; p < point.Header.ImagePages; p++)
        {
            writer.AddPage(p, page(p), suspect(p));
        }

        var tail = point.LoadTailDirectory();
        for (var i = 0; i < tail.Segments.Length; i++)
        {
            point.CopyTailSegment(tail, i, writer);
        }

        writer.Complete(point.Footer.PointLsn, point.Footer.TailEndUtcTicks);
    }
}
