using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>How deep a verify reads.</summary>
internal enum VerifyLevel : byte
{
    /// <summary>Headers, footers, INDEX, TAILDIR, SUSPECT, tails, chains: milliseconds per pack plus the tail bytes.</summary>
    Quick,

    /// <summary>Quick, plus every byte's FileCRC, every page decoded against its PageCrc, and the point's meta pair against its header.</summary>
    Full,
}

internal enum VerifySeverity : byte
{
    /// <summary>Worth knowing; nothing is lost (an orphan temp, a point that faithfully holds the live database's damage).</summary>
    Advisory,

    /// <summary>Bytes of a pack are wrong or missing: every point needing that pack is not restorable.</summary>
    Damage,
}

internal enum PackVerifyStatus : byte
{
    Ok,
    Damaged,

    /// <summary>The header or footer cannot be read: nothing of the pack is usable.</summary>
    Unreadable,
}

internal enum VerifyVerdict : byte
{
    Ok,
    Warning,
    Damaged,
    NoRestorablePoint,
}

/// <summary>One finding, coded <c>BKP-VER-nn</c> (plan 1413 §2.11).</summary>
internal readonly record struct VerifyFinding(string Code, VerifySeverity Severity, string File, long PageId, string Message);

internal sealed class PackVerification
{
    public string File { get; init; }

    public uint BackupId { get; init; }

    public PackVerifyStatus Status { get; set; }

    public List<VerifyFinding> Findings { get; } = [];
}

internal sealed class PointVerification
{
    public uint Id { get; init; }

    public bool Restorable { get; init; }

    /// <summary>The points whose packs make this one unrestorable (missing, unreadable, damaged or inconsistent).</summary>
    public IReadOnlyList<uint> BrokenBy { get; init; }
}

internal sealed class VerifyReport
{
    public VerifyLevel Level { get; init; }

    public VerifyVerdict Verdict { get; init; }

    public IReadOnlyList<PackVerification> Packs { get; init; }

    public IReadOnlyList<PointVerification> Points { get; init; }

    /// <summary>Findings about the store rather than one pack (orphan temps) and about chains (links).</summary>
    public IReadOnlyList<VerifyFinding> StoreFindings { get; init; }

    /// <summary>Checks that could not run in this build, with why.</summary>
    public IReadOnlyList<string> SkippedChecks { get; init; }

    public TimeSpan Duration { get; init; }

    public long BytesRead { get; init; }

    public IEnumerable<VerifyFinding> AllFindings => StoreFindings.Concat(Packs.SelectMany(p => p.Findings));
}

/// <summary>
/// BKP-VER-12's seam: the highest LSN recovery applies as a unit in a point's copied tail, computed by #1412's <c>WalTailInspector</c>. Given the
/// open point pack, its checked TAILDIR and its CheckpointLSN.
/// </summary>
internal delegate long TailPointLsnInspector(PackReader pack, TailDirectory tail, long checkpointLsn);

internal sealed class VerifyOptions
{
    public static readonly VerifyOptions Default = new();

    // TODO(#1412): wire WalTailInspector.ComputePointLsn here once capture lands; until then BKP-VER-12 is reported as skipped, never as passed.
    public TailPointLsnInspector TailInspector { get; init; }

    public CancellationToken Cancellation { get; init; }

    public ILogger Logger { get; init; }
}

/// <summary>
/// Verifies a store, or the chain of one point (plan 1413 §2.11, 04 §4). Never throws for damage — it reports it; throws only when the store
/// root is not a store or is of a format this build cannot read.
/// </summary>
internal static class BackupVerifier
{
    private const int MaxListedPages = 64;

    /// <summary>Every pack of the store, and every point.</summary>
    public static VerifyReport VerifyStore(string storePath, VerifyLevel level, VerifyOptions options = null, IStoreFileSystem fs = null) =>
        Run(storePath, null, level, options, fs);

    /// <summary>The packs of one point's chain, and that point.</summary>
    public static VerifyReport VerifyPoint(string storePath, PointSelector point, VerifyLevel level, VerifyOptions options = null,
        IStoreFileSystem fs = null) =>
        Run(storePath, point, level, options, fs);

    private static VerifyReport Run(string storePath, PointSelector? point, VerifyLevel level, VerifyOptions options, IStoreFileSystem fs)
    {
        options ??= VerifyOptions.Default;
        fs ??= StoreFileSystem.Instance;
        var log = options.Logger ?? NullLogger.Instance;
        var clock = Stopwatch.StartNew();

        // BKP-VER-01 (store side): store.json readable and of a known version, or this throws.
        var view = StoreView.Open(storePath, fs, log, trustCatalog: false);
        var storeFindings = new List<VerifyFinding>();
        var skipped = new List<string>();

        // BKP-VER-08: orphan temps never join a chain; the next writer deletes them.
        foreach (var t in view.Temps)
        {
            storeFindings.Add(new VerifyFinding("BKP-VER-08", VerifySeverity.Advisory, t.Name, -1,
                "temporary file of an interrupted backup; ignored by every reader, deleted by the next writer"));
        }

        // The points in scope, and the pack files they need.
        var pointIds = new SortedSet<uint>();
        foreach (var p in view.Catalog.Packs)
        {
            pointIds.Add(p.BackupId);
        }

        foreach (var u in view.Catalog.Unreadable)
        {
            if (PackFormat.TryParseFileName(u.File, out var id, out _, out _))
            {
                pointIds.Add(id);
            }
        }

        var files = new SortedSet<string>(StringComparer.Ordinal);
        if (point is { } selector)
        {
            var id = pointIds.Count == 0 && selector.IsLatest ? 0 : ChainResolver.Select(view, selector);
            pointIds.Clear();
            if (id != 0)
            {
                pointIds.Add(id);
            }

            foreach (var file in ChainFiles(view, id))
            {
                files.Add(file);
            }
        }
        else
        {
            foreach (var f in fs.EnumerateFiles(view.Paths.Packs))
            {
                if (StoreCatalog.IsPublishedPack(f.Name))
                {
                    files.Add(f.Name);
                }
            }
        }

        var packs = new Dictionary<string, PackVerification>(StringComparer.Ordinal);
        long bytesRead = 0;
        foreach (var file in files)
        {
            options.Cancellation.ThrowIfCancellationRequested();
            packs[file] = VerifyPack(fs, view, file, level, options, skipped, ref bytesRead);
        }

        // Points: restorable iff the chain resolves and every pack of it verified without damage (BKP-VER-06/07 for the links).
        var points = new List<PointVerification>();
        foreach (var id in pointIds)
        {
            var broken = new SortedSet<uint>();
            if (ChainResolver.TryResolve(view, id, out var chain, out var brokenAt, out var why, out var code))
            {
                foreach (var p in chain.Packs)
                {
                    if (!packs.TryGetValue(p.File, out var pv) || pv.Status != PackVerifyStatus.Ok)
                    {
                        broken.Add(p.BackupId);
                    }
                }
            }
            else
            {
                broken.Add(brokenAt);
                var finding = new VerifyFinding(code, VerifySeverity.Damage, PackFile(view, brokenAt), -1, $"point #{id}: backup-{brokenAt:D4} {why}");
                if (!storeFindings.Contains(finding))
                {
                    storeFindings.Add(finding);
                }
            }

            points.Add(new PointVerification { Id = id, Restorable = broken.Count == 0, BrokenBy = [.. broken] });
        }

        var packList = packs.Values.ToList();
        var anyDamage = packList.Any(p => p.Status != PackVerifyStatus.Ok) || storeFindings.Any(f => f.Severity == VerifySeverity.Damage)
                        || points.Any(p => !p.Restorable);
        var anyAdvisory = storeFindings.Count > 0 || packList.Any(p => p.Findings.Count > 0);
        var verdict = points.All(p => !p.Restorable) ? VerifyVerdict.NoRestorablePoint
            : anyDamage ? VerifyVerdict.Damaged
            : anyAdvisory ? VerifyVerdict.Warning
            : VerifyVerdict.Ok;

        return new VerifyReport
        {
            Level = level,
            Verdict = verdict,
            Packs = packList,
            Points = points,
            StoreFindings = storeFindings,
            SkippedChecks = skipped.Distinct().ToList(),
            Duration = clock.Elapsed,
            BytesRead = bytesRead,
        };
    }

    private static IEnumerable<string> ChainFiles(StoreView view, uint id)
    {
        // The packs the walk reaches, even when it breaks further down: they are still worth verifying.
        while (id != 0)
        {
            var (compacted, original) = view.Find(id);
            var pack = compacted ?? original;
            if (pack == null)
            {
                foreach (var u in view.Catalog.Unreadable)
                {
                    if (PackFormat.TryParseFileName(u.File, out var uid, out _, out _) && uid == id)
                    {
                        yield return u.File;
                    }
                }

                yield break;
            }

            yield return pack.File;
            if (pack.IsBase || pack.ParentId >= id)
            {
                yield break;
            }

            id = pack.ParentId;
        }
    }

    private static string PackFile(StoreView view, uint id)
    {
        var (compacted, original) = view.Find(id);
        return (compacted ?? original)?.File ?? $"backup-{id:D4}";
    }

    /// <summary>BKP-VER-01..05 and 09..14 for one pack.</summary>
    private static PackVerification VerifyPack(IStoreFileSystem fs, StoreView view, string file, VerifyLevel level, VerifyOptions options,
        List<string> skipped, ref long bytesRead)
    {
        PackFormat.TryParseFileName(file, out var id, out _, out _);
        var result = new PackVerification { File = file, BackupId = id };
        PackReader reader;
        try
        {
            reader = PackReader.Open(fs, view.Paths.PackPath(file));
        }
        catch (BackupFormatUnsupportedException ex)
        {
            Add(result, "BKP-VER-02", -1, ex.Message);
            result.Status = PackVerifyStatus.Unreadable;
            return result;
        }
        catch (BackupPackCorruptException ex)
        {
            Add(result, ex.Section == "Header" ? "BKP-VER-02" : "BKP-VER-03", -1, ex.Message);
            result.Status = PackVerifyStatus.Unreadable;
            return result;
        }
        catch (IOException ex)
        {
            Add(result, "BKP-VER-03", -1, ex.Message);
            result.Status = PackVerifyStatus.Unreadable;
            return result;
        }

        using (reader)
        {
            var header = reader.Header;

            // BKP-VER-01: the pack belongs to the store's database.
            if (header.DatabaseId != view.Identity.DatabaseId)
            {
                Add(result, "BKP-VER-01", -1, $"pack of database {header.DatabaseId}, store of {view.Identity.DatabaseId}");
            }

            // BKP-VER-04: the INDEX, streamed: order, bounds, IndexCRC. Pages 0 and 1 are kept for the meta pair.
            PackPageEntry? meta0 = null;
            PackPageEntry? meta1 = null;
            var indexOk = Guard(result, "BKP-VER-04", () =>
            {
                using var stream = reader.OpenIndexStream();
                while (stream.TryNext(out var e))
                {
                    if (e.PageId == 0)
                    {
                        meta0 = e;
                    }
                    else if (e.PageId == 1)
                    {
                        meta1 = e;
                    }
                }
            });
            if (indexOk && (meta0 == null || meta1 == null))
            {
                Add(result, "BKP-VER-04", -1, "the meta pair (pages 0 and 1) is not in the pack");
                indexOk = false;
            }

            // BKP-VER-05: TAILDIR and SUSPECT.
            TailDirectory tail = null;
            var tailDirOk = Guard(result, "BKP-VER-05", () => tail = reader.LoadTailDirectory());
            uint[] suspects = null;
            Guard(result, "BKP-VER-05", () => suspects = reader.LoadSuspects());

            // The image's own meta pair, read from the pack.
            BackupImageMeta? imageMeta = null;
            if (indexOk)
            {
                var p0 = new byte[PackFormat.PageSize];
                var p1 = new byte[PackFormat.PageSize];
                string e1 = null;
                if (reader.TryReadPage(meta0.Value, p0, out var e0) && reader.TryReadPage(meta1.Value, p1, out e1))
                {
                    imageMeta = BackupImageMeta.Read(p0, p1);
                }
                else
                {
                    Add(result, "BKP-VER-11", e0 != null ? 0 : 1, "the meta pair does not decode: " + (e0 ?? e1));
                }
            }

            // BKP-VER-09: every tail segment decodes, matches its RawCrc and its WAL header; an unclean image carries one.
            if (tailDirOk)
            {
                for (var i = 0; i < tail.Segments.Length; i++)
                {
                    var index = i;
                    Guard(result, "BKP-VER-09", () => reader.ReadTailSegment(tail, index, null));
                }

                if (tail.Segments.Length == 0 && imageMeta is { Usable: true, CleanShutdown: false })
                {
                    Add(result, "BKP-VER-09", -1, "the image needs crash recovery but the point carries no WAL segment (DC-3)");
                }
            }

            // BKP-VER-14: a suspect point is restorable; it says where the live database was damaged.
            if (suspects is { Length: > 0 })
            {
                result.Findings.Add(new VerifyFinding("BKP-VER-14", VerifySeverity.Advisory, file, -1,
                    $"the live database had {suspects.Length} damaged page(s) at capture, faithfully kept: {List(suspects)}"));
            }

            if (level == VerifyLevel.Full)
            {
                VerifyFull(reader, result, indexOk, tailDirOk, tail, imageMeta, options, skipped);
            }

            bytesRead += reader.BytesRead;
        }

        result.Status = result.Findings.Any(f => f.Severity == VerifySeverity.Damage) ? PackVerifyStatus.Damaged : PackVerifyStatus.Ok;
        return result;
    }

    /// <summary>BKP-VER-10..13: every byte, every page, the tail's P, the meta pair against the header.</summary>
    private static void VerifyFull(PackReader reader, PackVerification result, bool indexOk, bool tailDirOk, TailDirectory tail,
        BackupImageMeta? imageMeta, VerifyOptions options, List<string> skipped)
    {
        // BKP-VER-10: one pass over the file. The page pass below reads the frames again through a forward window: two reads of the data
        // region, in exchange for memory independent of the pack's size (no offset map of every frame).
        if (!reader.VerifyFileCrc())
        {
            Add(result, "BKP-VER-10", -1, "FileCRC mismatch");
        }

        // BKP-VER-11: every page decodes to 8 KiB and matches its PageCrc.
        if (indexOk)
        {
            var damaged = new List<uint>();
            Guard(result, "BKP-VER-04", () =>
            {
                using var frames = new FrameReader(reader, 4 << 20);
                using var stream = reader.OpenIndexStream();
                var page = new byte[PackFormat.PageSize];
                var n = 0;
                while (stream.TryNext(out var e))
                {
                    if (!frames.TryRead(e, page, out _))
                    {
                        damaged.Add(e.PageId);
                    }

                    if ((++n & 0xFFF) == 0)
                    {
                        options.Cancellation.ThrowIfCancellationRequested();
                    }
                }
            });
            if (damaged.Count > 0)
            {
                result.Findings.Add(new VerifyFinding("BKP-VER-11", VerifySeverity.Damage, result.File, damaged[0],
                    $"{damaged.Count} page(s) fail to decode or their PageCrc: {List(damaged)}"));
            }
        }

        // BKP-VER-12: the copied tail still yields the point's P.
        if (options.TailInspector == null)
        {
            skipped.Add("BKP-VER-12: needs WalTailInspector (#1412)");
        }
        else if (tailDirOk)
        {
            Guard(result, "BKP-VER-12", () =>
            {
                var p = options.TailInspector(reader, tail, reader.Header.CheckpointLsn);
                if (p != reader.Footer.PointLsn)
                {
                    Add(result, "BKP-VER-12", -1, $"the tail yields P = {p}, the footer records {reader.Footer.PointLsn}");
                }
            });
        }

        // BKP-VER-13: the image's meta pair says what the header says.
        if (imageMeta is { } meta)
        {
            var h = reader.Header;
            if (!meta.Usable)
            {
                Add(result, "BKP-VER-13", 0, "the image's meta pair is unusable: " + meta.Problem);
            }
            else if (meta.CheckpointLsn != h.CheckpointLsn || meta.DatabaseId != h.DatabaseId || meta.FormatRevision != h.DatabaseFormatRevision)
            {
                Add(result, "BKP-VER-13", 0,
                    $"the meta pair records C={meta.CheckpointLsn}, database {meta.DatabaseId}, revision {meta.FormatRevision}; the header "
                    + $"C={h.CheckpointLsn}, database {h.DatabaseId}, revision {h.DatabaseFormatRevision}");
            }
        }
    }

    private static bool Guard(PackVerification result, string code, Action check)
    {
        try
        {
            check();
            return true;
        }
        catch (BackupPackCorruptException ex)
        {
            Add(result, code, ex.PageId, ex.Message);
            return false;
        }
        catch (IOException ex)
        {
            Add(result, code, -1, ex.Message);
            return false;
        }
    }

    private static void Add(PackVerification result, string code, long pageId, string message) =>
        result.Findings.Add(new VerifyFinding(code, VerifySeverity.Damage, result.File, pageId, message));

    private static string List(IReadOnlyList<uint> ids) =>
        string.Join(", ", ids.Take(MaxListedPages)) + (ids.Count > MaxListedPages ? $", … ({ids.Count - MaxListedPages} more)" : "");
}
