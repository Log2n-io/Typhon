using System;
using System.Collections.Generic;
using System.IO;

namespace Typhon.Engine.Internals;

/// <summary>
/// <c>BOO</c> — root and bootstrap checks. Runs first, because everything else depends on them: a <c>Fatal</c> here stops
/// the scan rather than letting later checks report a flood of phantom findings derived from a bootstrap that was never
/// valid.
/// </summary>
internal static class BootstrapChecks
{
    /// <summary>Check code: bundle shape.</summary>
    public const string BundleShape = "CHK-BOO-01";

    /// <summary>Check code: page-0 identity.</summary>
    public const string Identity = "CHK-BOO-02";

    /// <summary>Check code: A/B meta-pair selection.</summary>
    public const string MetaPair = "CHK-BOO-03";

    /// <summary>Check code: bootstrap stream well-formedness.</summary>
    public const string Stream = "CHK-BOO-04";

    /// <summary>Check code: every segment pointer resolves.</summary>
    public const string SegmentPointers = "CHK-BOO-05";

    /// <summary>Check code: checkpoint LSN versus the WAL window.</summary>
    public const string CheckpointWindow = "CHK-BOO-06";

    /// <summary>
    /// Bootstrap keys whose <c>IntN</c> components are segment root-page indices, with the component positions that hold
    /// them. Only these are cross-checked; an unrecognised key is reported as an advisory rather than guessed at, because
    /// treating an arbitrary integer as a page index manufactures findings out of ordinary configuration values.
    /// </summary>
    private static readonly Dictionary<string, int[]> SpiKeys = new(StringComparer.Ordinal)
    {
        ["OccupancyMapSPI"] = [0],
        ["UowRegistrySPI"] = [0],
        ["sys.ComponentR1"] = [0, 1],
        ["sys.SchemaHistory"] = [0, 1],
        ["sys.AssemblyR1"] = [0, 1],
        ["collection.FieldR1"] = [0]
    };

    /// <summary>
    /// The checks that must run <b>before</b> the structural sweep, because the sweep is meaningless without them: if the
    /// file is not a Typhon database, or neither meta slot is valid, every later finding would be an artefact of reading
    /// garbage. A <see cref="IntegritySeverity.Fatal"/> here sets <see cref="ScanContext.StopScan"/>.
    /// </summary>
    /// <param name="ctx">The scan context; <see cref="ScanContext.Bootstrap"/> must already be populated.</param>
    public static void RunEarly(ScanContext ctx)
    {
        CheckBundleShape(ctx);
        CheckMetaPair(ctx);

        if (ctx.StopScan)
        {
            return;
        }

        CheckIdentity(ctx);
        CheckStream(ctx);
    }

    /// <summary>
    /// The checks that need the structural sweep's results — every segment pointer is validated against what the sweep
    /// actually found on disk, rather than against the dictionary's own claims.
    /// </summary>
    /// <param name="ctx">The scan context, with segments walked and roles assigned.</param>
    public static void RunLate(ScanContext ctx)
    {
        CheckSegmentPointers(ctx);
        CheckCheckpointWindow(ctx);
    }

    private static void CheckBundleShape(ScanContext ctx)
    {
        var bundle = ctx.Bundle;
        if (bundle == null)
        {
            return;
        }

        if (bundle.TrailingBytes != 0)
        {
            ctx.Report(BundleShape, IntegritySeverity.Divergence, "STO-01", Locus.Database,
                $"The data file is truncated {bundle.TrailingBytes} bytes into a page.",
                $"File size {bundle.SizeBytes:N0} B is not a whole multiple of the {IntegrityConstants.PageSize} B page size. "
                + "Every write the engine performs is whole-page, so a partial trailing page means the file was copied or "
                + "truncated by something outside the engine.",
                Repairability.Lossy,
                new LossEstimate
                {
                    Kind = LossKind.Unknown,
                    EntityCount = -1,
                    BoundedMin = 0,
                    BoundedMax = 1,
                    Explanation = "At most the contents of one page; possibly nothing, if the page was never written."
                });
        }

        if (bundle.LockHeld)
        {
            ctx.Report(BundleShape, IntegritySeverity.Advisory, "", Locus.Database,
                "Another process holds this database open.",
                "db.lock is held. Cross-structure findings from this scan describe a moving target and are reported as "
                + "Suspected. Re-run with the database closed to confirm any of them.");
        }

        if (ctx.Source.PageCount < ManagedPagedMMF.MinimumPhysicalPageCount)
        {
            ctx.Report(BundleShape, IntegritySeverity.Fatal, "", Locus.Database,
                $"The data file holds only {ctx.Source.PageCount} pages; a Typhon database has at least {ManagedPagedMMF.MinimumPhysicalPageCount}.",
                "Genesis materialises the meta pair, the occupancy segment's directory root and its twin, and the first "
                + "page of occupancy data. A file shorter than that was never a complete database, or has been truncated "
                + "to nothing.");
            ctx.StopScan = true;
        }
    }

    private static void CheckMetaPair(ScanContext ctx)
    {
        var b = ctx.Bootstrap;

        if (b.SelectedSlot < 0)
        {
            ctx.Report(MetaPair, IntegritySeverity.Fatal, "CK-05", new Locus(0),
                "Both slots of the page-0 meta pair are invalid — the database cannot be opened.",
                DescribeSlot(b.SlotA) + " " + DescribeSlot(b.SlotB)
                + " The meta pair holds the root identity header and the bootstrap dictionary; with neither slot valid there "
                + "is no way to locate any segment. This is recoverable only from a backup.");
            ctx.StopScan = true;
            return;
        }

        // One bad slot is survivable by design — that is what the pair is for — but it means the next write has no
        // fallback, so it is worth saying out loud rather than leaving the operator on one copy without knowing.
        var other = b.SelectedSlot == 0 ? b.SlotB : b.SlotA;
        if (!other.IsValid)
        {
            ctx.Report(MetaPair, IntegritySeverity.Divergence, "CK-05", new Locus(other.SlotIndex),
                $"Meta-pair slot {other.SlotIndex} is unusable; the database is running on a single copy of its root metadata.",
                DescribeSlot(other) + $" Slot {b.SelectedSlot} is valid at generation {b.SelectedGeneration}. The pair heals "
                + "on the next metadata write, which alternates back into the bad slot.",
                Repairability.Lossless);
        }
    }

    private static void CheckIdentity(ScanContext ctx)
    {
        var b = ctx.Bootstrap;

        if (!b.SignatureValid)
        {
            ctx.Report(Identity, IntegritySeverity.Fatal, "", new Locus(b.SelectedSlot),
                "Page 0 does not carry the Typhon signature — this is not a Typhon database.",
                $"Expected '{BootstrapView.ExpectedSignature}', found '{b.Signature}'. The page's checksum is valid, so the "
                + "bytes are intact; they are simply not a Typhon root header.");
            ctx.StopScan = true;
            return;
        }

        if (b.FormatRevision <= 0)
        {
            ctx.Report(Identity, IntegritySeverity.Fatal, "", new Locus(b.SelectedSlot),
                $"Page 0 records an impossible format revision ({b.FormatRevision}).",
                "The signature matched but the revision field did not, so the identity header is partially damaged.");
            ctx.StopScan = true;
            return;
        }

        if (b.FormatRevision != PagedMMF.DatabaseFormatRevision)
        {
            ReportRevisionMismatch(ctx, b.FormatRevision, b.SelectedSlot);
        }
    }

    /// <summary>
    /// Reports a format revision this build does not speak — in <b>either</b> direction — as an advisory, and never stops
    /// the scan.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The split is by verb (<c>05-repair.md</c> §7, OQ-7). <b>Diagnosis degrades; mutation does not.</b> Refusing to read
    /// a database because its revision is unfamiliar is the opposite of what a scanner is for — the operator reaching for
    /// one has already lost the happy path. So the scan runs, states what it could not interpret, and lets
    /// <see cref="DatabaseRepair"/> be the thing that refuses.
    /// </para>
    /// <para>
    /// <b>Older is not safer than newer,</b> which is the half this check was missing. Pre-alpha carries no compatibility
    /// obligation, so a revision bump is free to re-mean bytes that a previous revision left unused — revision 7 did
    /// exactly that, claiming <c>[54,56)</c> for the chunk stride. Those bytes are <i>zero</i> on a revision-6 page, and
    /// zero is not "unknown"; it is this build's sentinel for "this segment has no chunks". A reader that shrugged at an
    /// older revision would therefore not fail — it would conclude the precise opposite of the truth about a segment full
    /// of them, and every cross-structure check downstream would agree with it.
    /// </para>
    /// </remarks>
    private static void ReportRevisionMismatch(ScanContext ctx, int found, int slot)
    {
        var mine = PagedMMF.DatabaseFormatRevision;
        var direction = found > mine ? "newer than" : "older than";

        ctx.Report(Identity, IntegritySeverity.Advisory, "", new Locus(slot),
            $"The database is format revision {found}; this build speaks revision {mine}.",
            $"Revision {found} is {direction} this build's, so structures whose layout differs between the two are not "
            + "decoded and the scan's coverage is incomplete. Repair is refused outright on any revision mismatch: "
            + "repairing a layout the tool does not fully understand is how a tool corrupts a database it was asked to "
            + $"save. Run this database's own build to check or repair it, or a build that speaks revision {found}.",
            Repairability.NotRepairable);

        ctx.Findings.NoteCaveat(
            $"The database is format revision {found}, {direction} this build's {mine}; any structure whose meaning changed "
            + "between the two revisions was read under this build's interpretation, so cross-structure conclusions may be "
            + "wrong rather than merely absent.");
    }

    private static void CheckStream(ScanContext ctx)
    {
        var b = ctx.Bootstrap;

        for (var i = 0; i < b.ParseDiagnostics.Count; i++)
        {
            ctx.Report(Stream, IntegritySeverity.Fatal, "", new Locus(b.SelectedSlot),
                "The bootstrap dictionary is malformed.",
                b.ParseDiagnostics[i]
                + $" {b.Entries.Count} entries were recovered before the problem. The bootstrap names every system segment, "
                + "so a truncated stream means some subsystems' data is unreachable even though its pages are intact.");
        }

        if (b.Entries.Count == 0 && b.ParseDiagnostics.Count == 0)
        {
            ctx.Report(Stream, IntegritySeverity.Fatal, "", new Locus(b.SelectedSlot),
                "The bootstrap dictionary is empty.",
                "A valid meta page always carries at least the occupancy segment pointer. An empty, well-formed stream means "
                + "the page was written by something that did not populate it.");
        }
    }

    private static void CheckSegmentPointers(ScanContext ctx)
    {
        var b = ctx.Bootstrap;

        for (var i = 0; i < b.Entries.Count; i++)
        {
            var key = b.Entries[i].Key;
            if (!SpiKeys.TryGetValue(key, out var positions))
            {
                continue;
            }

            var value = b.Entries[i].Value;
            for (var p = 0; p < positions.Length; p++)
            {
                if (positions[p] >= value.IntCount)
                {
                    continue;
                }

                var spi = value.GetInt(positions[p]);
                if (spi == 0)
                {
                    continue;   // a legitimately absent subsystem
                }

                ValidateSpi(ctx, key, spi);
            }
        }
    }

    private static void ValidateSpi(ScanContext ctx, string key, int spi)
    {
        if (!ctx.IsInRange(spi))
        {
            ctx.Report(SegmentPointers, IntegritySeverity.Fatal, "", new Locus(spi),
                $"Bootstrap key '{key}' points at page {spi}, which is past the end of the file.",
                $"The data file holds {ctx.Source.PageCount:N0} pages. An engine following this pointer would read outside the "
                + "file. Every subsystem reachable only through this key is unreachable.");
            return;
        }

        if (ctx.Roles.Length > spi && ctx.Roles[spi] != PageRole.SegmentRoot && ctx.Roles[spi] != PageRole.Reserved)
        {
            ctx.Report(SegmentPointers, IntegritySeverity.Fatal, "", new Locus(spi),
                $"Bootstrap key '{key}' points at page {spi}, which is not a segment root.",
                $"The page's role is {ctx.Roles[spi]}. Either the pointer is stale or the root page's header was overwritten; "
                + "in both cases the segment behind this key cannot be loaded.");
            return;
        }

        if (ctx.Occupancy is { IsComplete: true } && !ctx.Occupancy.IsAllocated(spi))
        {
            ctx.Report(SegmentPointers, IntegritySeverity.Divergence, "CK-09", new Locus(spi),
                $"Bootstrap key '{key}' points at page {spi}, which the allocation bitmap marks as free.",
                "The bitmap is derived state, so the page is treated as allocated and the bitmap as wrong. Left unrepaired, "
                + "the allocator could hand this page out a second time and two owners would fight over it.",
                Repairability.Lossless);
        }
    }

    private static void CheckCheckpointWindow(ScanContext ctx)
    {
        var bundle = ctx.Bundle;
        if (bundle == null)
        {
            return;
        }

        var (checkpointLsn, cleanShutdown) = ctx.Bootstrap.ReadWatermarks();

        if (bundle.WalSegments.Count == 0)
        {
            if (checkpointLsn > 0 && !cleanShutdown)
            {
                ctx.Report(CheckpointWindow, IntegritySeverity.DataLoss, "WP-09", Locus.Database,
                    "The database did not shut down cleanly and its write-ahead log is missing.",
                    $"The last checkpoint reached LSN {checkpointLsn:N0} and the clean-shutdown flag is clear, so transactions "
                    + "committed after that checkpoint lived only in the log. With no wal/ directory there is nothing to replay "
                    + "them from.",
                    Repairability.NotRepairable,
                    new LossEstimate
                    {
                        Kind = LossKind.Unknown,
                        EntityCount = -1,
                        BoundedMin = 0,
                        BoundedMax = long.MaxValue,
                        Explanation = "Every transaction committed after the last checkpoint. The count cannot be bounded from "
                            + "inside the database, because the only record of it was the log."
                    });
            }

            return;
        }

        // Pre-allocated spares carry no records, so they say nothing about the window's reach.
        var headers = new List<WalHeaderView>();
        foreach (var h in WalChecks.ReadSegmentHeaders(bundle))
        {
            if (h.Valid && !h.Unused)
            {
                headers.Add(h);
            }
        }

        if (headers.Count == 0)
        {
            return;
        }

        if (checkpointLsn > 0 && checkpointLsn < headers[0].FirstLsn)
        {
            ctx.Report(CheckpointWindow, IntegritySeverity.DataLoss, "LOG-03", Locus.Database,
                "The write-ahead log does not reach back to the last checkpoint.",
                $"The oldest surviving segment starts at LSN {headers[0].FirstLsn:N0}, but the last checkpoint recorded "
                + $"{checkpointLsn:N0}. The records between the two were recycled before their contents were checkpointed, so "
                + "the replay window has a hole at its start.",
                Repairability.NotRepairable,
                new LossEstimate
                {
                    Kind = LossKind.Unknown,
                    EntityCount = -1,
                    BoundedMin = 1,
                    BoundedMax = long.MaxValue,
                    Explanation = $"Every transaction between LSN {checkpointLsn:N0} and {headers[0].FirstLsn:N0}."
                });
        }
    }

    private static string DescribeSlot(MetaSlotView slot)
    {
        if (!slot.Present)
        {
            return $"Slot {slot.SlotIndex}: absent (the file is too short to contain it).";
        }

        if (!slot.ChecksumValid)
        {
            return $"Slot {slot.SlotIndex}: checksum mismatch (stored 0x{slot.StoredChecksum:X8}, computed 0x{slot.ComputedChecksum:X8}).";
        }

        return slot.Generation == 0
            ? $"Slot {slot.SlotIndex}: checksum valid but never written through the alternation path (generation 0)."
            : $"Slot {slot.SlotIndex}: valid at generation {slot.Generation}.";
    }
}
