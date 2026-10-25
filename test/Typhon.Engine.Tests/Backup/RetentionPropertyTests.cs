using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Typhon.Engine.Tests.Backup;

/// <summary>
/// T11 (plan 1415 §8.2, BKP-14): random store histories — full and incremental points on two interleaving chains, compactions, drill records, retentions
/// under random valid policies, a power cut at a random operation of a retention, a clock that moves — checked against a model of every point's image.
/// After each retention, and after each crash and reopen:
/// (1) every kept point resolves and materialises to the model's image (R1); (2) the newest MinKeep restorable points of before the run are still
/// there, and the newest drill-passed one when KeepLastDrilled (R2); (3) the store holds no more than MaxTotalBytes plus what this run's compactions
/// wrote, unless the plan says Blocked (R3, DL-12); (4) every point still listed resolves as it did (descending deletes); (5) no id is ever handed out
/// twice (BKP-08).
/// </summary>
[TestFixture]
[Seeded]
internal sealed class RetentionPropertyTests
{
    private const string Root = SyntheticChain.Root;

    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("BKP-14")]
    public void RandomHistories_NeverLoseAPromisedPoint_EvenWhenARetentionIsCutShort() => Run(64);

    [Test]
    [CancelAfter(600_000)]
    [Explicit("640 histories — tens of seconds; the gate runs 64")]
    [Category("Nightly")]
    public void RandomHistories_NeverLoseAPromisedPoint_EvenWhenARetentionIsCutShort_Nightly() => Run(640);

    /// <summary>The crash-prefix check can fail: deletes run OLDEST first and cut short leave a listed point without its base.</summary>
    [Test]
    [CancelAfter(15_000)]
    [RuleMutant("BKP-14")]
    public void Mutant_AscendingDeletes_CutShort_IsReported()
    {
        // Chain A: F1 I2 I3; chain B: F4 I5. Keeping two points deletes all of chain A.
        var model = new Model(new Random(1));
        var fs = NewStore();
        WritePoint(fs, model, slot: 0, full: true);
        WritePoint(fs, model, slot: 0, full: false);
        WritePoint(fs, model, slot: 0, full: false);
        WritePoint(fs, model, slot: 1, full: true);
        WritePoint(fs, model, slot: 1, full: false);
        model.CloseStore();

        var before = Restorable(fs);
        var plan = RetentionPlanner.Plan(StoreView.Open(Root, fs, trustCatalog: false), Policy(minKeep: 1, maxCount: 2), [], model.Clock, null);
        Assert.That(plan.Deletions.Select(d => d.Name), Is.EqualTo(new[] { "00000003-I.tpack", "00000002-I.tpack", "00000001-F.tpack" }), "premise");
        var ascending = plan.Deletions.AsEnumerable().Reverse().ToList();
        fs.DeleteFile(Root + "/packs/" + ascending[0].Name);   // the base first, then the cut

        RuleMutants.AssertDetects("BKP-14", "BKP-14: listed point", () => AssertListedPointsResolve(fs, model, before));
    }

    /// <summary>The kept-point check can fail: a plan whose deletions assume a compaction of chain B that was skipped.</summary>
    [Test]
    [CancelAfter(15_000)]
    [RuleMutant("BKP-14")]
    public void Mutant_SkippingTheSecondChainsCompaction_IsReported()
    {
        var model = new Model(new Random(2));
        var fs = NewStore();
        for (var i = 0; i < 8; i++)
        {
            WritePoint(fs, model, slot: i % 2, full: i < 2);
        }

        model.CloseStore();
        var decision = RetentionPlanner.Plan(StoreView.Open(Root, fs, trustCatalog: false), Policy(minKeep: 1, maxCount: 4), [], model.Clock, null);
        Assert.That(decision.Compactions, Has.Count.EqualTo(2), "premise: both chains reach below the cutoff");
        using (var session = BackupStore.OpenForMaintenance(Root, "prune", fs))
        {
            BackupCompactor.Compact(session, decision.Compactions[0], null);
        }

        foreach (var d in decision.Deletions)
        {
            fs.DeleteFile(Root + "/packs/" + d.Name);
        }

        RuleMutants.AssertDetects("BKP-14", "BKP-14: kept point", () => AssertKeptPointsIntact(fs, model, decision.Kept));
    }

    private static void Run(int histories)
    {
        var baseSeed = TestSeed.For(nameof(Run));
        for (var h = 0; h < histories; h++)
        {
            var seed = baseSeed + h;
            try
            {
                RunHistory(seed);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new AssertionException($"history seed {seed} (run seed {TestSeed.RunSeed}; {TestSeed.Repro}): {ex.Message}", ex);
            }
        }
    }

    private static void RunHistory(int seed)
    {
        var random = new Random(seed);
        var model = new Model(random);
        var fs = NewStore(random.Next(2) == 0 ? CrashModelKind.Windows : CrashModelKind.Linux);
        WritePoint(fs, model, 0, full: true);
        var operations = random.Next(40, 121);
        for (var op = 0; op < operations; op++)
        {
            var r = random.Next(100);
            if (r < 12)
            {
                WritePoint(fs, model, random.Next(2), full: true);
            }
            else if (r < 55)
            {
                WritePoint(fs, model, random.Next(2), full: false);
            }
            else if (r < 63)
            {
                model.CloseStore();
                CompactOne(fs, random);
            }
            else if (r < 72)
            {
                var restorable = Restorable(fs);
                model.NewestDrill[restorable[random.Next(restorable.Count)]] = random.Next(10) < 7;
            }
            else if (r < 90)
            {
                model.CloseStore();
                Retain(fs, model, random);
            }
            else
            {
                model.Clock = model.Clock.AddMinutes(random.Next(1, 600));
            }
        }

        model.CloseStore();
        Retain(fs, model, random);
    }

    private static void Retain(CrashModelFileSystem fs, Model model, Random random)
    {
        var policy = RandomPolicy(random);
        var before = Restorable(fs);
        var promisedByMinKeep = before.OrderByDescending(i => i).Take(policy.MinKeep).ToList();
        var drilled = model.NewestDrill.Where(kv => kv.Value).Select(kv => kv.Key).ToList();
        var promisedByDrill = policy.KeepLastDrilled ? drilled.Where(before.Contains).DefaultIfEmpty(0u).Max() : 0u;

        // What came before was made durable by its own protocol (StorePublishCrashTests, CompactionCrashTests): the cut lands in this retention only.
        var crash = random.Next(4) == 0;
        if (crash)
        {
            fs.SyncAll();
        }

        fs.CrashBefore(crash ? random.Next(0, 60) : int.MaxValue);
        BackupRetentionResult result = null;
        var crashed = false;
        try
        {
            using var session = BackupStore.OpenForMaintenance(Root, "prune", fs);
            result = RetentionExecutor.Apply(session, policy, null, drilled, model.Clock, Guid.Empty, null);
        }
        catch (SimulatedCrashException)
        {
            crashed = true;
        }

        fs.CrashBefore(int.MaxValue);
        if (crashed)
        {
            fs.Crash();
            using (BackupStore.OpenForMaintenance(Root, "reopen", fs))
            {
                // The next lock holder deletes what the crash left half-written.
            }
        }

        // (4) after any prefix: every listed point that resolved before resolves to its image. (1)/(2): what the plan kept and what the policy
        // promised is still listed (and so, by (4), intact).
        var view = AssertListedPointsResolve(fs, model, before);
        var listed = view.Catalog.Packs.Select(p => p.BackupId).ToHashSet();
        foreach (var id in promisedByMinKeep)
        {
            Assert.That(listed, Does.Contain(id), $"BKP-14: kept point #{id} (MinKeep {policy.MinKeep}) was lost (crashed: {crashed})");
        }

        if (promisedByDrill != 0)
        {
            Assert.That(listed, Does.Contain(promisedByDrill), $"BKP-14: kept point #{promisedByDrill} (KeepLastDrilled) was lost (crashed: {crashed})");
        }

        if (result != null)
        {
            Assert.That(listed, Is.SupersetOf(result.Plan.Kept), "BKP-14: kept point lost");
            if (policy.MaxTotalBytes > 0 && result.Plan.Blocked == BackupRetentionBlock.None)
            {
                var bytes = fs.EnumerateFiles(Root + "/packs").Sum(f => f.Length);
                var written = result.Compactions.Sum(c => c.BytesWritten);
                Assert.That(bytes, Is.LessThanOrEqualTo(policy.MaxTotalBytes + written),
                    $"R3: {bytes} bytes kept under MaxTotalBytes {policy.MaxTotalBytes} (+{written} compacted this run), not blocked");
            }
        }

        model.Forget(listed);
    }

    private static void CompactOne(CrashModelFileSystem fs, Random random)
    {
        var view = StoreView.Open(Root, fs, trustCatalog: false);
        var candidates = ChainResolver.RestorablePoints(view).Where(id => !ChainResolver.Resolve(view, id).Point.IsBase).ToList();
        if (candidates.Count == 0)
        {
            return;
        }

        using var session = BackupStore.OpenForMaintenance(Root, "compact", fs);
        BackupCompactor.Compact(session, candidates[random.Next(candidates.Count)], null);
    }

    private static BackupRetentionPolicy RandomPolicy(Random random)
    {
        while (true)
        {
            var minKeep = random.Next(1, 5);
            var policy = new BackupRetentionPolicy
            {
                MinKeep = minKeep,
                MaxCount = random.Next(3) == 0 ? 0 : random.Next(minKeep, minKeep + 7),
                MaxAge = random.Next(3) == 0 ? TimeSpan.Zero : TimeSpan.FromMinutes(random.Next(30, 3000)),
                MaxTotalBytes = random.Next(3) == 0 ? 0 : random.Next(50_000, 2_000_000),
                CompactThreshold = random.Next(3) == 0 ? 0 : random.Next(2, 8),
                KeepLastDrilled = random.Next(2) == 0,
            };
            if (policy.Validate(out _))
            {
                return policy;
            }
        }
    }

    private static BackupRetentionPolicy Policy(int minKeep, int maxCount) => new()
    {
        MinKeep = minKeep, MaxCount = maxCount, MaxAge = TimeSpan.Zero, MaxTotalBytes = 0, CompactThreshold = 0, KeepLastDrilled = false,
    };

    private static CrashModelFileSystem NewStore(CrashModelKind kind = CrashModelKind.Linux)
    {
        var fs = new CrashModelFileSystem(kind);
        fs.CreateDirectory("/");
        fs.CreateDirectory("/t");
        return fs;
    }

    /// <summary>One point on chain slot <paramref name="slot"/>: incremental on that chain's head when asked and the head is still there, else a full point.</summary>
    private static void WritePoint(CrashModelFileSystem fs, Model model, int slot, bool full)
    {
        var image = BackupTestImages.Clean;
        var random = model.Random;
        // One writer session across consecutive points, as an engine keeps its store: closed before any other lock holder runs.
        var store = model.Store ??= BackupStore.OpenForWrite(Root, image.DatabaseId, true, fs);
        var chain = model.Slots[slot];
        var incremental = !full && chain != Guid.Empty && store.TryGetChainHead(chain, out var head) && model.Pages.ContainsKey(head.BackupId);
        BackupChainHead parent = default;
        if (incremental)
        {
            store.TryGetChainHead(chain, out parent);
        }

        var plan = incremental
            ? new BackupPointPlan(image.DatabaseId, StoreTestKit.Timeline, BackupKind.Incremental, chain, parent.BackupId, image.FormatRevision, "t", false)
            : new BackupPointPlan(image.DatabaseId, StoreTestKit.Timeline, BackupKind.Full, Guid.Empty, 0, image.FormatRevision, "t", false);
        var sink = store.BeginPoint(plan);
        var id = sink.BackupId;
        Assert.That(id, Is.GreaterThan(model.MaxId), "BKP-08: an id was handed out twice");
        model.MaxId = id;

        var n = incremental ? Math.Min(48, model.Pages[parent.BackupId] + (random.Next(4) == 0 ? random.Next(1, 6) : 0)) : random.Next(8, 25);
        var versions = incremental ? new Dictionary<int, uint>(model.Versions[parent.BackupId]) : [];
        var written = incremental
            ? Enumerable.Range(0, random.Next(1, 7)).Select(_ => random.Next(2, n)).Distinct().ToList()
            : Enumerable.Range(2, n - 2).Where(_ => random.Next(5) != 0).ToList();   // a full point of a sparse file has holes too
        foreach (var p in written)
        {
            versions[p] = id;
        }

        sink.SetImage(n, image.CheckpointLsn, model.Clock.Ticks);
        sink.AddPage(0, image.Pages[0], false);
        sink.AddPage(1, image.Pages[1], false);
        foreach (var p in written)
        {
            sink.AddPage(p, Content(p, id), false);
        }

        if (!incremental)
        {
            // A full point holds every page: one never written is stored as zeros.
            foreach (var p in Enumerable.Range(2, n - 2).Except(written))
            {
                sink.AddPage(p, new byte[PackFormat.PageSize], false);
            }
        }

        sink.Complete(image.CheckpointLsn, model.Clock.Ticks);
        model.Pages[id] = n;
        model.Versions[id] = versions;
        model.Slots[slot] = sink.ChainId;
        model.Clock = model.Clock.AddMinutes(random.Next(1, 120));
    }

    private static byte[] Content(int pageId, uint version)
    {
        var page = new byte[PackFormat.PageSize];
        new Random((int)((version * 7919) + (uint)pageId)).NextBytes(page.AsSpan(0, 96));
        return page;
    }

    private static uint ExpectedCrc(Model model, uint id)
    {
        if (model.Crcs.TryGetValue(id, out var known))
        {
            return known;
        }

        var image = BackupTestImages.Clean;
        var crc = Crc32CUtil.Start;
        var zero = new byte[PackFormat.PageSize];
        for (var p = 0; p < model.Pages[id]; p++)
        {
            var page = p < 2 ? image.Pages[p] : model.Versions[id].TryGetValue(p, out var v) ? Content(p, v) : zero;
            crc = Crc32CUtil.Append(crc, page);
        }

        return model.Crcs[id] = Crc32CUtil.Finish(crc);
    }

    private static List<uint> Restorable(InMemorySparseFileSystem fs) => ChainResolver.RestorablePoints(StoreView.Open(Root, fs, trustCatalog: false));

    /// <summary>Every point still listed that resolved before resolves now, to the model's image: what descending deletes guarantee at any prefix.</summary>
    private static StoreView AssertListedPointsResolve(InMemorySparseFileSystem fs, Model model, List<uint> before)
    {
        var view = StoreView.Open(Root, fs, trustCatalog: false);
        foreach (var id in view.Catalog.Packs.Select(p => p.BackupId).Distinct())
        {
            if (!before.Contains(id))
            {
                continue;
            }

            Assert.That(ChainResolver.TryResolve(view, id, out var chain, out _, out var why), Is.True, $"BKP-14: listed point #{id} no longer resolves: {why}");

            // Packs never change and an id's names are never reused (BKP-08): a resolution already checked against the model is that image again.
            var key = string.Join("|", chain.Packs.Select(p => p.File + ":" + p.Size));
            if (model.Checked.Contains(key))
            {
                continue;
            }

            Assert.That(MaterializedCrc(fs, view, id), Is.EqualTo(ExpectedCrc(model, id)), $"BKP-14: listed point #{id} materialises to another image");
            model.Checked.Add(key);
        }

        return view;
    }

    private static void AssertKeptPointsIntact(InMemorySparseFileSystem fs, Model model, IEnumerable<uint> kept)
    {
        var view = StoreView.Open(Root, fs, trustCatalog: false);
        foreach (var id in kept)
        {
            Assert.That(ChainResolver.TryResolve(view, id, out _, out _, out var why), Is.True, $"BKP-14: kept point #{id} does not resolve: {why}");
            Assert.That(MaterializedCrc(fs, view, id), Is.EqualTo(ExpectedCrc(model, id)), $"BKP-14: kept point #{id} materialises to another image");
        }
    }

    /// <summary>The CRC of a point's materialised image, streamed: nothing is stored.</summary>
    private static uint MaterializedCrc(InMemorySparseFileSystem fs, StoreView view, uint id)
    {
        var packs = PackChainPageSource.OpenPacks(fs, view, ChainResolver.Resolve(view, id));
        try
        {
            using var target = new CrcTarget();
            ChainMaterializer.Materialize(packs, target);
            return target.Crc;
        }
        finally
        {
            foreach (var p in packs)
            {
                p.Dispose();
            }
        }
    }

    /// <summary>A materialisation target that only hashes what is written to it, in order.</summary>
    private sealed class CrcTarget : IStoreFile
    {
        private uint _crc = Crc32CUtil.Start;

        public uint Crc => Crc32CUtil.Finish(_crc);

        public string Path => "crc";

        public long Length { get; private set; }

        public int Read(Span<byte> destination, long offset) => throw new NotSupportedException();

        public void Write(ReadOnlySpan<byte> source, long offset)
        {
            Assert.That(offset, Is.EqualTo(Length), "the materialiser writes ascending and completely");
            _crc = Crc32CUtil.Append(_crc, source);
            Length += source.Length;
        }

        public System.Threading.Tasks.Task WriteAsync(ReadOnlyMemory<byte> source, long offset) => throw new NotSupportedException();

        public void SetLength(long length) => throw new NotSupportedException();

        public void Flush()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class Model(Random random)
    {
        public Random Random { get; } = random;

        public Dictionary<uint, int> Pages { get; } = [];

        public Dictionary<uint, Dictionary<int, uint>> Versions { get; } = [];

        public Guid[] Slots { get; } = new Guid[2];

        public Dictionary<uint, bool> NewestDrill { get; } = [];

        public uint MaxId { get; set; }

        /// <summary>Each point's expected image CRC, computed once: a point's image never changes.</summary>
        public Dictionary<uint, uint> Crcs { get; } = [];

        /// <summary>The resolutions (pack names and sizes) already materialised and found equal to the model.</summary>
        public HashSet<string> Checked { get; } = [];

        /// <summary>The writer session of consecutive points, or <c>null</c>.</summary>
        public BackupStore Store { get; set; }

        public void CloseStore()
        {
            Store?.Dispose();
            Store = null;
        }

        public DateTime Clock { get; set; } = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>Drops the points the store no longer lists (their drill records with them).</summary>
        public void Forget(HashSet<uint> listed)
        {
            foreach (var id in Pages.Keys.Where(id => !listed.Contains(id)).ToList())
            {
                Pages.Remove(id);
                Versions.Remove(id);
                NewestDrill.Remove(id);
                Crcs.Remove(id);
            }
        }
    }
}
