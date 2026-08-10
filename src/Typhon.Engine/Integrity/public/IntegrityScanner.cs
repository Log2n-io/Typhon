using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Typhon.Engine;

/// <summary>
/// Answers three questions about a database without changing a byte of it: <i>is it sound</i>, <i>what exactly is wrong</i>,
/// and <i>what would a repair cost</i>.
/// </summary>
/// <remarks>
/// <para>
/// The scan is provably read-only — it reaches pages through an <see cref="IPageSource"/>, which is contractually
/// side-effect free — so it is always safe to run: on a production database, on a corrupt one, on one that will not open.
/// That last case is the point. The engine already repairs on the crash path, but it does so silently, only at open, only
/// when it feels like it, and reports into log lines. A database can therefore be silently repaired with the operator
/// learning nothing, or refuse to open with the operator learning nothing useful.
/// </para>
/// <para>
/// The scanner is also a test oracle. Because it needs no model of what the database <i>should</i> contain, it can be
/// asserted after any crash-injection cell — including ones whose correct final state the harness cannot compute.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// using var source = new OfflineBundlePageSource("game.typhon");
/// var report = IntegrityScanner.Scan(source, IntegrityOptions.Deep);
/// Console.WriteLine(IntegrityReportText.Render(report));
/// return report.ExitCode;
/// </code>
/// </example>
[PublicAPI]
public static class IntegrityScanner
{
    /// <summary>
    /// Scans a page source and produces a report.
    /// </summary>
    /// <param name="source">The source to read through. Not disposed by this method.</param>
    /// <param name="options">Depth, filters and budgets. Defaults to <see cref="IntegrityOptions.Standard"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <c>null</c>.</exception>
    public static IntegrityReport Scan(IPageSource source, IntegrityOptions options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= IntegrityOptions.Standard;

        var bundle = source as OfflineBundlePageSource;
        var mode = bundle is { LockHeld: true } ? ScanMode.OnlineSampled : ScanMode.Offline;

        var stopwatch = Stopwatch.StartNew();
        var ctx = new ScanContext
        {
            Source = source,
            Options = options,
            Mode = mode,
            Bundle = bundle,
            Findings = new FindingCollector(options)
        };

        var pageCount = source.PageCount;
        ctx.Roles = new PageRole[pageCount];
        ctx.Owner = new int[pageCount];
        ctx.FlagsByte = new byte[pageCount];
        Array.Fill(ctx.Owner, -1);

        ctx.Bootstrap = BootstrapReader.Read(source);
        BootstrapChecks.RunEarly(ctx);

        if (!ctx.StopScan)
        {
            // Spine is the tier that runs on every open, so it must stay bounded by the number of SEGMENTS rather than
            // the size of the database — it reaches structures through the bootstrap's own pointers and never sweeps.
            // Deeper tiers can afford the physical sweep, which is strictly better at finding things the bootstrap does
            // not mention, but paying for it at every open is the tax this design explicitly refused.
            if (ctx.Options.Depth == ScanDepth.Spine)
            {
                DiscoverSpine(ctx);
                BootstrapChecks.RunLate(ctx);
                SegmentChecks.Run(ctx);
            }
            else
            {
                DiscoverStructure(ctx);
                ReadOccupancy(ctx);
                ReadManifest(ctx);
                BootstrapChecks.RunLate(ctx);
                SegmentChecks.Run(ctx);
                SweepPages(ctx);
                ChainChecks.Run(ctx);
                ClusterChecks.Run(ctx);
                EntityMapChecks.Run(ctx);

                // After the three walks above, never before: CHN-06 compares sets that the chain pass and the map pass
                // each fill half of, so running it earlier reports the unfilled half as damage.
                CrossStructureChecks.Run(ctx);
                BufferChecks.Run(ctx);
                IndexChecks.Run(ctx);
                IndexContentChecks.Run(ctx);
                WalChecks.Run(ctx);
            }
        }

        stopwatch.Stop();
        return Build(ctx, stopwatch.Elapsed);
    }

    /// <summary>
    /// The always-on open-time tier: page-0 pair selection, the bootstrap stream, and that every segment pointer resolves
    /// to a real segment root. Bounded by the number of <i>segments</i> rather than the size of the database — kilobytes
    /// read, sub-millisecond — which is what makes it affordable on every open.
    /// </summary>
    /// <remarks>
    /// The clean-shutdown flag records that the last process closed properly. It does <b>not</b> record that the bytes are
    /// still correct. Damage that happens while a database is closed — bit rot, a truncated copy, a restore from the wrong
    /// place, a file-level backup tool writing through — is invisible to a clean open, which then proceeds to serve it.
    /// This tier is the cheapest thing that catches the whole structurally-broken class, which is exactly the damage where
    /// opening anyway does the most harm.
    /// </remarks>
    /// <param name="source">The source to verify.</param>
    public static IntegrityReport VerifySpine(IPageSource source) => Scan(source, IntegrityOptions.Spine);

    /// <summary>
    /// Segment discovery for the <see cref="ScanDepth.Spine"/> tier: follow the bootstrap's own segment pointers and walk
    /// only those segments' directories. Reads on the order of a few pages per segment, never the whole file.
    /// </summary>
    /// <remarks>
    /// This deliberately gives up what the physical sweep buys — a segment the bootstrap has forgotten about is invisible
    /// here — and that trade is the whole point of the tier. Spine answers "can this database be traversed at all", which
    /// is the question worth asking on every open; "is every page accounted for" is a question worth asking when someone
    /// asks it.
    /// </remarks>
    private static void DiscoverSpine(ScanContext ctx)
    {
        var walker = new SegmentWalker(ctx.Source);
        var seen = new HashSet<int>();

        // The genesis pages exist before any segment does, so they are reachable by definition.
        for (var p = 0; p < Math.Min(ManagedPagedMMF.InitialReservedPageCount, ctx.Source.PageCount); p++)
        {
            ctx.Roles[p] = PageRole.Reserved;
        }

        for (var i = 0; i < ctx.Bootstrap.Entries.Count; i++)
        {
            var value = ctx.Bootstrap.Entries[i].Value;
            for (var c = 0; c < value.IntCount; c++)
            {
                var spi = value.GetInt(c);

                // A bootstrap value is an arbitrary integer until proven otherwise, so only follow one that addresses a
                // real page AND says on that page that it is a segment root. Anything else is configuration, not a
                // pointer, and chasing it would manufacture findings out of ordinary values.
                if (spi <= 0 || !ctx.IsInRange(spi) || !seen.Add(spi))
                {
                    continue;
                }

                if (!LooksLikeSegmentRoot(ctx, spi))
                {
                    continue;
                }

                var segment = walker.WalkSegment(spi);
                ctx.Segments[spi] = segment;
                AttributePages(ctx, segment);
            }
        }
    }

    /// <summary>Whether a page declares itself the root of the segment its own directory names.</summary>
    private static bool LooksLikeSegmentRoot(ScanContext ctx, int pageIndex)
    {
        Span<byte> page = new byte[IntegrityConstants.PageSize];
        if (!ctx.Source.TryReadPage(pageIndex, page))
        {
            return false;
        }

        ctx.FlagsByte[pageIndex] = (byte)PageImage.Flags(page);
        if ((PageImage.Flags(page) & PageBlockFlags.IsLogicalSegmentRoot) == 0)
        {
            return false;
        }

        return System.Runtime.InteropServices.MemoryMarshal.Read<int>(PageImage.RawData(page)) == pageIndex;
    }

    private static void DiscoverStructure(ScanContext ctx)
    {
        var source = ctx.Source;
        var pageCount = source.PageCount;
        Span<byte> page = new byte[IntegrityConstants.PageSize];

        // Pass 1 — read every page's flags byte. Segment discovery is PHYSICAL: a root page says so in its own header, so
        // the sweep finds every segment without trusting a dictionary that may itself be damaged.
        //
        // The one trap is A/B protection: a directory page's twin is a BYTE COPY of its primary, root flag included, so a
        // naive "flagged as root" test reports every paired segment twice and then declares every one of its pages
        // double-claimed. The discriminator is self-reference — a segment's page directory names its own root as entry 0,
        // and a twin's copy of that directory still names the PRIMARY. So a page is a root exactly when its own directory
        // points back at it. That test needs no cross-page state, which matters: it stays correct on a file where the
        // pointers themselves are what is damaged.
        var roots = new List<int>();
        for (var p = 0; p < pageCount; p++)
        {
            if (ctx.Options.Cancellation.IsCancellationRequested)
            {
                ctx.Findings.NoteCaveat($"The scan was cancelled after {p:N0} of {pageCount:N0} pages; coverage is partial.");
                return;
            }

            if (!source.TryReadPage(p, page))
            {
                continue;
            }

            var flags = PageImage.Flags(page);
            ctx.FlagsByte[p] = (byte)flags;
            if ((flags & PageBlockFlags.IsLogicalSegmentRoot) == 0)
            {
                continue;
            }

            var firstDirectoryEntry = System.Runtime.InteropServices.MemoryMarshal.Read<int>(PageImage.RawData(page));
            if (firstDirectoryEntry == p)
            {
                roots.Add(p);
            }
            else if (PageImage.TwinPage(page) != 0)
            {
                ctx.Roles[p] = PageRole.DirectoryTwin;   // provisional; the owning segment's walk confirms it
            }
        }

        // The genesis pages exist before any segment does, so they are reachable by definition rather than by walking.
        for (var p = 0; p < Math.Min(ManagedPagedMMF.InitialReservedPageCount, pageCount); p++)
        {
            ctx.Roles[p] = PageRole.Reserved;
        }

        // Pass 2 — walk each discovered segment and attribute its pages.
        var walker = new SegmentWalker(source);
        for (var i = 0; i < roots.Count; i++)
        {
            var seg = walker.WalkSegment(roots[i]);
            ctx.Segments[roots[i]] = seg;
            AttributePages(ctx, seg);
            ctx.Options.Progress?.Invoke("segments", i + 1, roots.Count);
        }
    }

    private static void AttributePages(ScanContext ctx, SegmentView seg)
    {
        Claim(ctx, seg.RootPageIndex, seg, PageRole.SegmentRoot);

        for (var i = 0; i < seg.MapExtensionPages.Count; i++)
        {
            Claim(ctx, seg.MapExtensionPages[i], seg, PageRole.MapExtension);
        }

        for (var i = 0; i < seg.TwinPages.Count; i++)
        {
            ctx.TwinSlots.Add(seg.TwinPages[i]);
            Claim(ctx, seg.TwinPages[i], seg, PageRole.DirectoryTwin);
        }

        for (var i = 0; i < seg.Pages.Count; i++)
        {
            var p = seg.Pages[i];
            if (p == seg.RootPageIndex)
            {
                continue;
            }

            Claim(ctx, p, seg, PageRole.SegmentData);
        }
    }

    private static void Claim(ScanContext ctx, int page, SegmentView seg, PageRole role)
    {
        if (!ctx.IsInRange(page))
        {
            return;
        }

        var existing = ctx.Owner[page];
        if (existing >= 0 && existing != seg.RootPageIndex)
        {
            // Two owners will eventually write over each other. Report it once per page, at the moment the conflict is
            // provable, with both claimants named.
            ctx.Segments.TryGetValue(existing, out var other);
            ctx.Report(SegmentChecks.SingleOwner, IntegritySeverity.DataLoss, "", new Locus(page, seg.RootPageIndex, seg.Kind),
                $"Page {page} is claimed by two segments.",
                $"The {other?.Kind.ToString() ?? "unknown"} segment rooted at page {existing} and the {seg.Kind} segment "
                + $"rooted at page {seg.RootPageIndex} both list it. Whichever writes second destroys the other's data, and "
                + "neither can be regenerated from the other.",
                Repairability.NotRepairable,
                new LossEstimate
                {
                    Kind = LossKind.Unknown,
                    EntityCount = -1,
                    BoundedMin = 1,
                    BoundedMax = 475,
                    Explanation = $"Whatever one of the two segments stored on page {page}; which one has already lost is not "
                        + "determinable from the file."
                });
            return;
        }

        ctx.Owner[page] = seg.RootPageIndex;

        // Do not let a data-page claim downgrade a structural role: a page that is both a root and listed in its own
        // directory (which every root is) must stay classified as a root.
        if (ctx.Roles[page] == PageRole.Unclaimed || role < ctx.Roles[page])
        {
            ctx.Roles[page] = role;
        }
    }

    private static void ReadOccupancy(ScanContext ctx)
    {
        // The occupancy segment is found the same way as any other: physically. The bootstrap's pointer to it is checked
        // against this, not used to find it.
        foreach (var seg in ctx.Segments.Values)
        {
            if (seg.Kind != StorageSegmentKind.Occupancy)
            {
                continue;
            }

            ctx.Occupancy = OccupancyView.Read(ctx.Source, seg);
            for (var i = 0; i < ctx.Occupancy.Diagnostics.Count; i++)
            {
                ctx.Findings.NoteCaveat(ctx.Occupancy.Diagnostics[i]);
            }

            return;
        }

        ctx.Findings.NoteCaveat("No occupancy segment was found, so page-allocation state could not be compared against the "
            + "reachability walk. Leak and phantom detection are unavailable.");
    }

    /// <summary>
    /// Reads the database's own schema manifest — the catalogs that name every component and archetype.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runs after the structural sweep because every pointer the manifest carries is validated against the segments the
    /// sweep found, rather than against the manifest's own claims. That is the same primary-over-derived discipline the
    /// rest of the catalogue applies, and it is what keeps a damaged catalog row from steering a check at somebody
    /// else's segment.
    /// </para>
    /// <para>
    /// It is not fatal for this to fail. A database whose manifest is unreadable still gets a physical and structural
    /// report; it just loses the cross-structure families, which say so through <c>Limits.ChecksSkipped</c> rather than
    /// silently reporting nothing.
    /// </para>
    /// </remarks>
    private static void ReadManifest(ScanContext ctx)
    {
        if (!ctx.AtLeast(ScanDepth.Deep))
        {
            return;
        }

        var reader = new SchemaCatalogReader(ctx.Source, ctx.Segments.Keys);
        reader.Read(ctx.Bootstrap);
        ctx.Manifest = reader;

        for (var i = 0; i < reader.Diagnostics.Count; i++)
        {
            ctx.Findings.NoteCaveat(reader.Diagnostics[i]);
        }
    }

    private static void SweepPages(ScanContext ctx)
    {
        if (!ctx.AtLeast(ScanDepth.Quick))
        {
            ctx.Findings.NoteSkipped("CHK-PHY-*", "needs Quick depth or deeper");
            return;
        }

        var source = ctx.Source;
        var pageCount = source.PageCount;
        Span<byte> page = new byte[IntegrityConstants.PageSize];

        for (var p = 0; p < pageCount; p++)
        {
            if (ctx.Options.Cancellation.IsCancellationRequested)
            {
                ctx.Findings.NoteCaveat($"The page sweep was cancelled after {p:N0} of {pageCount:N0} pages; coverage is partial.");
                break;
            }

            if (!source.TryReadPage(p, page))
            {
                continue;
            }

            ctx.PagesScanned++;
            var allocated = ctx.Occupancy?.IsAllocated(p) ?? ctx.Roles[p] != PageRole.Unclaimed;
            PhysicalChecks.RunForPage(ctx, p, page, allocated);

            if ((p & 0xFFF) == 0)
            {
                ctx.Options.Progress?.Invoke("pages", p, pageCount);
            }
        }

        if (!ctx.AtLeast(ScanDepth.Standard))
        {
            ctx.Findings.NoteSkipped(PhysicalChecks.Checksum, "needs Standard depth or deeper");
        }
    }

    private static IntegrityReport Build(ScanContext ctx, TimeSpan duration)
    {
        var b = ctx.Bootstrap;
        var (checkpointLsn, cleanShutdown) = b.ReadWatermarks();

        var allocated = 0;
        if (ctx.Occupancy != null)
        {
            allocated = ctx.Occupancy.PopCount;
        }
        else
        {
            for (var i = 0; i < ctx.Roles.Length; i++)
            {
                if (ctx.Roles[i] != PageRole.Unclaimed)
                {
                    allocated++;
                }
            }
        }

        long leakedBytes = 0;
        var leakCount = ctx.Findings.CountFor(SegmentChecks.OccupancyAgreement);
        if (leakCount > 0 && ctx.Occupancy != null)
        {
            leakedBytes = Math.Max(0, allocated - CountReachable(ctx)) * (long)IntegrityConstants.PageSize;
        }

        // Every scan states what it could not have detected, including a fully green one. Suppressing it when everything
        // passes is precisely when suppressing it does harm.
        var caveats = new List<string>(ctx.Findings.Caveats);
        if (!ctx.AtLeast(ScanDepth.Deep))
        {
            caveats.Add($"Depth was {ctx.Options.Depth}; cross-structure checks (chains, clusters, indexes, entity maps, "
                + "allocator watermarks) were not run. Run at Deep depth for those.");
        }

        if (ctx.Mode == ScanMode.OnlineSampled)
        {
            caveats.Add("The database was open in another process, so every cross-structure conclusion is reported as "
                + "Suspected rather than Confirmed. Re-run with the database closed before acting on any of them.");
        }

        return new IntegrityReport
        {
            Source = ctx.Source.Describe(),
            Mode = ctx.Mode,
            Depth = ctx.Options.Depth,
            Identity = new DatabaseIdentity
            {
                Name = b.DatabaseName,
                FormatRevision = b.FormatRevision,
                PageCount = ctx.Source.PageCount,
                SizeBytes = ctx.Bundle?.SizeBytes ?? ((long)ctx.Source.PageCount * IntegrityConstants.PageSize),
                CheckpointLsn = checkpointLsn,
                CleanShutdown = cleanShutdown,
                MetaSlot = b.SelectedSlot,
                MetaGeneration = b.SelectedGeneration,
                WalSegmentCount = ctx.Bundle?.WalSegments.Count ?? 0,
                WalBytes = SumWal(ctx)
            },
            Findings = ctx.Findings.Build(),
            Totals = new ScanTotals
            {
                PagesScanned = ctx.PagesScanned,
                PagesAllocated = allocated,
                ChecksumFailures = ctx.ChecksumFailures,
                PagesWithSectorFooters = ctx.PagesWithSectorFooters,
                SectorFailures = ctx.SectorFailures,
                SegmentsWalked = ctx.Segments.Count,
                BytesLeaked = leakedBytes,
                BySeverity = ctx.Findings.SeverityHistogram()
            },
            Limits = new ScanLimits { ChecksSkipped = ctx.Findings.Skipped, Caveats = caveats },
            Duration = duration
        };
    }

    private static int CountReachable(ScanContext ctx)
    {
        var n = 0;
        for (var i = 0; i < ctx.Roles.Length; i++)
        {
            if (ctx.Roles[i] != PageRole.Unclaimed)
            {
                n++;
            }
        }

        return n;
    }

    private static long SumWal(ScanContext ctx)
    {
        if (ctx.Bundle == null)
        {
            return 0;
        }

        long total = 0;
        for (var i = 0; i < ctx.Bundle.WalSegments.Count; i++)
        {
            total += ctx.Bundle.WalSegments[i].SizeBytes;
        }

        return total;
    }
}
