using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using NUnit.Framework;
using Typhon.Engine;

namespace Typhon.Engine.Tests.Integrity;

/// <summary>
/// Breaks a healthy database in ways we know exactly, and proves that is what happened.
/// </summary>
/// <remarks>
/// <para>
/// A repair test is only worth what its damage is worth. If a fixture corrupts a page by some approximate means and the
/// repair afterwards reports success, all that has been established is that some code ran — not that it did the right
/// thing, and not that it did nothing else. This kit exists so that every assertion downstream of a corruption rests on
/// a statement of exactly which bytes moved and exactly what a correct scanner must say about them.
/// </para>
/// <para>
/// Three disciplines, each enforced mechanically rather than by convention:
/// </para>
/// <list type="number">
///   <item>
///     <b>The baseline is proven, not assumed.</b> <see cref="Baseline"/> asserts the database scans
///     <see cref="IntegrityVerdict.Sound"/> before anything is touched. Without it, a finding after the damage might
///     equally have predated it, and the test proves nothing about the damage.
///   </item>
///   <item>
///     <b>The mutation is byte-exact.</b> Every operation records the ranges it intends to write, and
///     <see cref="AssertOnlyDeclaredBytesChanged"/> compares the file before and after: any byte that moved outside a
///     declared range fails the test. A corruption primitive that damages more than it claims makes every conclusion
///     drawn from it unsound, and that failure is silent unless something looks.
///   </item>
///   <item>
///     <b>The detection is exact in both directions.</b> <see cref="AssertDetectedExactly"/> requires the declared
///     finding codes to appear <i>and</i> requires no others. "It reported something" is the failure mode this whole
///     feature exists to replace.
///   </item>
/// </list>
/// <para>
/// Damage modes are keyed to the taxonomy in <c>claude/design/Durability/Integrity/02-damage-taxonomy.md</c> §2 so a
/// reader can go from a test to the class of fault it stands for.
/// </para>
/// </remarks>
internal static class DamageKit
{
    /// <summary>A byte range within the data file that an operation declares it will write.</summary>
    internal readonly record struct ByteRange(long Offset, int Length)
    {
        public long EndExclusive => Offset + Length;

        public override string ToString() => $"[{Offset}, {EndExclusive})";
    }

    /// <summary>
    /// What a damage operation did and what a correct scanner must make of it.
    /// </summary>
    /// <param name="Mode">The taxonomy class (02 §2), for readability at the call site.</param>
    /// <param name="Description">One sentence, used in assertion messages so a failure explains itself.</param>
    /// <param name="Ranges">Every byte range the operation wrote. Checked against the file.</param>
    /// <param name="ExpectedFindingCodes">The complete set of codes a scan must report — no more, no fewer.</param>
    /// <param name="ExpectedVerdict">The verdict a scan must return.</param>
    /// <param name="RepairIsLossless">Whether a repair of this damage must report zero loss.</param>
    internal sealed record DamageRecord(
        string Mode,
        string Description,
        IReadOnlyList<ByteRange> Ranges,
        IReadOnlyList<string> ExpectedFindingCodes,
        IntegrityVerdict ExpectedVerdict,
        bool RepairIsLossless);

    /// <summary>A byte-for-byte copy of the data file, taken before damage so the mutation can be diffed against it.</summary>
    internal sealed class FileSnapshot
    {
        internal FileSnapshot(string dataPath)
        {
            Path = dataPath;
            Bytes = File.ReadAllBytes(dataPath);
        }

        internal string Path { get; }

        internal byte[] Bytes { get; }
    }

    // ── Baseline ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Scans the bundle and asserts it is <see cref="IntegrityVerdict.Sound"/>, returning a snapshot of the data file.
    /// </summary>
    /// <remarks>
    /// Every damage test starts here. A test that damages a database without first proving it was healthy cannot
    /// attribute anything it later finds to the damage — the finding could have been there all along, and on a feature
    /// whose job is to find pre-existing damage that is not a hypothetical concern.
    /// </remarks>
    internal static FileSnapshot Baseline(string bundlePath)
    {
        var report = Scan(bundlePath);
        Assert.That(report.Verdict, Is.EqualTo(IntegrityVerdict.Sound),
            "precondition: the database must be healthy before it is damaged, or nothing found afterwards can be "
            + "attributed to the damage.\n" + IntegrityReportText.Render(report));

        return new FileSnapshot(DataPath(bundlePath));
    }

    /// <summary>Runs a scan at the given depth with no engine involved.</summary>
    internal static IntegrityReport Scan(string bundlePath, ScanDepth depth = ScanDepth.Standard)
    {
        using var source = new OfflineBundlePageSource(bundlePath);
        return IntegrityScanner.Scan(source, new IntegrityOptions { Depth = depth });
    }

    // ── Assertions ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Asserts the data file differs from <paramref name="before"/> only inside the ranges the operation declared.
    /// </summary>
    /// <remarks>
    /// This is the assertion that makes the rest of the kit trustworthy. A primitive meant to tear one sector but which
    /// also clears a header field would still produce a finding, a plan and a green verification — and the test would
    /// pass while proving something other than what it claims. Comparing the whole file is cheap at fixture sizes and
    /// removes the possibility entirely.
    /// </remarks>
    internal static void AssertOnlyDeclaredBytesChanged(FileSnapshot before, DamageRecord damage)
    {
        var after = File.ReadAllBytes(before.Path);
        Assert.That(after.Length, Is.EqualTo(before.Bytes.Length),
            $"{damage.Mode}: the file changed length; no declared range implies a resize");

        var declared = damage.Ranges;
        var stray = new List<long>();
        for (long i = 0; i < after.Length; i++)
        {
            if (after[i] == before.Bytes[i])
            {
                continue;
            }

            var inside = false;
            for (var r = 0; r < declared.Count; r++)
            {
                if (i >= declared[r].Offset && i < declared[r].EndExclusive)
                {
                    inside = true;
                    break;
                }
            }

            if (!inside)
            {
                stray.Add(i);
                if (stray.Count >= 8)
                {
                    break;
                }
            }
        }

        Assert.That(stray, Is.Empty,
            $"{damage.Mode} ({damage.Description}) wrote outside the ranges it declared "
            + $"({string.Join(", ", declared)}). First stray offsets: {string.Join(", ", stray)}. "
            + "A damage primitive that mutates more than it claims invalidates every conclusion drawn from it.");
    }

    /// <summary>
    /// Asserts the scan reports the declared verdict and <b>exactly</b> the declared finding codes.
    /// </summary>
    /// <remarks>
    /// Both directions matter. A missing code means the damage is undetected — the check does not work. An extra code
    /// means the damage was less surgical than claimed, or a check is firing on something it should not; either way the
    /// fixture no longer isolates what it says it isolates.
    /// </remarks>
    internal static void AssertDetectedExactly(IntegrityReport report, DamageRecord damage)
    {
        var actual = report.Findings.Select(f => f.Code).Distinct().OrderBy(c => c, StringComparer.Ordinal).ToArray();
        var expected = damage.ExpectedFindingCodes.Distinct().OrderBy(c => c, StringComparer.Ordinal).ToArray();

        Assert.That(actual, Is.EqualTo(expected),
            $"{damage.Mode} ({damage.Description}) must produce exactly {{{string.Join(", ", expected)}}}.\n"
            + IntegrityReportText.Render(report));

        Assert.That(report.Verdict, Is.EqualTo(damage.ExpectedVerdict),
            $"{damage.Mode}: verdict mismatch.\n" + IntegrityReportText.Render(report));
    }

    /// <summary>Asserts a repair healed the damage: every step succeeded, the re-scan is clean, and loss matches the claim.</summary>
    internal static void AssertHealed(RepairOutcome outcome, DamageRecord damage)
    {
        Assert.That(outcome.Succeeded, Is.True, $"{damage.Mode}: repair did not succeed");
        Assert.That(outcome.VerificationReport, Is.Not.Null, $"{damage.Mode}: a repair must verify what it wrote");
        Assert.That(outcome.VerificationReport.Verdict, Is.EqualTo(IntegrityVerdict.Sound),
            $"{damage.Mode}: post-repair verification is not clean.\n"
            + IntegrityReportText.Render(outcome.VerificationReport));

        if (damage.RepairIsLossless)
        {
            var lossy = outcome.Results.Where(r => r.ActualLoss.Kind != LossKind.None).ToArray();
            Assert.That(lossy, Is.Empty,
                $"{damage.Mode} is declared lossless, but steps reported loss: "
                + string.Join(" · ", lossy.Select(r => $"{r.Step.Action}={r.ActualLoss.Kind}")));
        }
    }

    // ── Primitives ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Which half of the A/B meta pair to destroy. They are not interchangeable — see the remarks.</summary>
    internal enum MetaSlot
    {
        /// <summary>
        /// The slot the database currently reads from: the newest generation, carrying the clean-shutdown flag and the
        /// latest checkpoint LSN written by the final close.
        /// </summary>
        Current,

        /// <summary>The older half of the pair, superseded by the last metadata write.</summary>
        Stale
    }

    /// <summary>
    /// <b>D12 (half)</b> — clobbers one slot of the page-0 meta pair, leaving the other intact.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pair exists so a torn write can never destroy the only copy of the root metadata. Destroying one slot is
    /// therefore the canonical <i>survivable</i> catastrophe: the database still opens from the sibling, and the repair
    /// is provably lossless because it reads only the slot that verifies.
    /// </para>
    /// <para>
    /// <b>Which slot matters, and it took a failing test to notice.</b> Slots alternate, so the survivor after
    /// clobbering <see cref="MetaSlot.Current"/> is the *previous* metadata write — one generation behind, with the
    /// clean-shutdown flag clear and an older checkpoint LSN. Both choices report the same finding, but they are not
    /// the same damage: only <see cref="MetaSlot.Stale"/> leaves the database's watermarks intact. Callers that care
    /// assert on <c>Identity.CleanShutdown</c>, which is the observable that actually differs.
    /// </para>
    /// <para>
    /// Worth recording because the first version of this kit got it wrong in a way a green test would have hidden: with
    /// the WAL <i>outside</i> the bundle — as the older integrity fixture places it — clobbering the current slot also
    /// drew <c>CHK-BOO-06</c>, because the rolled-back checkpoint LSN pointed at committed work and there was no log to
    /// replay it from. That finding was correct; the fixture was not.
    /// </para>
    /// </remarks>
    internal static DamageRecord ClobberMetaSlot(string bundlePath, MetaSlot which)
    {
        // Ask the database which slot it is actually reading rather than assuming — the alternation depends on how many
        // metadata writes the fixture happened to perform.
        var current = Scan(bundlePath, ScanDepth.Spine).Identity.MetaSlot;
        var target = which == MetaSlot.Current ? current : 1 - current;

        // Inside the metadata region and clear of the base header, so the page stays structurally parseable and the
        // finding is a checksum mismatch rather than an unparseable page.
        var range = WritePattern(bundlePath, target, 200, 64, 0xAB);

        return which == MetaSlot.Stale
            ? new DamageRecord(
                "D12-half(stale)",
                $"stale meta slot {target} clobbered; the current slot {current} still verifies",
                [range],
                ["CHK-BOO-03"],
                IntegrityVerdict.Divergent,
                RepairIsLossless: true)
            : new DamageRecord(
                "D12-half(current)",
                $"current meta slot {target} clobbered; the database falls back to the older slot {1 - target}",
                [range],
                ["CHK-BOO-03"],
                IntegrityVerdict.Divergent,
                RepairIsLossless: true);
    }

    /// <summary>
    /// <b>D12</b> — clobbers both slots of the meta pair. The database becomes unopenable by construction.
    /// </summary>
    /// <remarks>
    /// The case that most justifies an offline scanner: no engine can open this, so nothing that works through an
    /// engine could ever report on it.
    /// </remarks>
    internal static DamageRecord ClobberBothMetaSlots(string bundlePath)
    {
        var a = WritePattern(bundlePath, 0, 200, 64, 0xAB);
        var b = WritePattern(bundlePath, 1, 200, 64, 0xCD);

        return new DamageRecord(
            "D12",
            "both meta slots clobbered; no copy of the root metadata survives",
            [a, b],
            ["CHK-BOO-03"],
            IntegrityVerdict.Unopenable,
            RepairIsLossless: false);
    }

    /// <summary>
    /// <b>D1</b> — flips one byte inside a page's data region, so its checksum no longer matches its content.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The single smallest detectable at-rest fault, and the one the CRC sweep exists for. One byte rather than a
    /// region on purpose: a check that only notices large damage is a check with an unstated threshold.
    /// </para>
    /// <para>
    /// The <b>verdict is a parameter, not a constant</b>, because it is not a property of the flip: the same one-byte
    /// corruption is <see cref="IntegrityVerdict.DataLoss"/> on a page holding live primary data and
    /// <see cref="IntegrityVerdict.Divergent"/> on a derived one. Only the caller knows which page it picked and why,
    /// so only the caller can state what a correct scanner must conclude. Baking a default in would have made the kit
    /// assert its own assumption.
    /// </para>
    /// </remarks>
    internal static DamageRecord FlipByteInPage(
        string bundlePath,
        int filePageIndex,
        IntegrityVerdict expectedVerdict,
        int offsetInPage = IntegrityConstants.PageHeaderSize + 16)
    {
        var dataPath = DataPath(bundlePath);
        using var fs = new FileStream(dataPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var at = (long)filePageIndex * IntegrityConstants.PageSize + offsetInPage;
        fs.Seek(at, SeekOrigin.Begin);
        var b = fs.ReadByte();
        if (b < 0)
        {
            throw new InvalidOperationException($"page {filePageIndex} offset {offsetInPage} is past the end of the data file");
        }

        fs.Seek(at, SeekOrigin.Begin);
        fs.WriteByte((byte)(b ^ 0xFF));
        fs.Flush(true);

        return new DamageRecord(
            "D1",
            $"one byte flipped in page {filePageIndex} at +{offsetInPage}",
            [new ByteRange(at, 1)],
            ["CHK-PHY-01"],
            expectedVerdict,
            RepairIsLossless: false);
    }

    /// <summary>
    /// <b>D13</b> — rewrites the recorded on-disk format revision in both meta slots, producing a bundle that a different
    /// build of the engine would have written.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not corruption: the output is a <i>well-formed database of another revision</i>, which is a harder and more useful
    /// thing to forge. The revision is patched in both slots and each is re-stamped with the engine's own
    /// <see cref="PagedMMF.StampPageForWrite"/>, so the file that reaches a reader is checksum-valid in every respect
    /// except the one under test. Patching the four bytes alone would produce a CRC failure, and every test built on it
    /// would pass while proving something else entirely.
    /// </para>
    /// <para>
    /// Both slots, not one: a reader selects the newest valid slot, so patching one leaves a coin flip over which
    /// revision the file appears to be — and the flip depends on how many metadata writes the fixture happened to perform.
    /// </para>
    /// <para>
    /// Expected finding is <c>CHK-BOO-02</c> at <see cref="IntegritySeverity.Advisory"/>, so the verdict stays
    /// <see cref="IntegrityVerdict.Sound"/>. That is the design and not an oversight: the database <i>is</i> sound as far
    /// as this build could check it, the coverage shortfall belongs in <c>Limits</c>, and it is repair — not diagnosis —
    /// that refuses (IR-01).
    /// </para>
    /// </remarks>
    /// <param name="bundlePath">The bundle to convert.</param>
    /// <param name="revision">The revision to record. Must differ from the build's, or the forgery proves nothing.</param>
    internal static DamageRecord ForgeFormatRevision(string bundlePath, int revision)
    {
        Assert.That(revision, Is.Not.EqualTo(DatabaseRepair.SupportedFormatRevision),
            "forging the revision this build already speaks would make the fixture a no-op that still passes");

        var path = DataPath(bundlePath);
        var data = File.ReadAllBytes(path);
        var ranges = new List<ByteRange>(4);

        for (var slot = 0; slot <= 1; slot++)
        {
            var page = new byte[IntegrityConstants.PageSize];
            Array.Copy(data, slot * IntegrityConstants.PageSize, page, 0, IntegrityConstants.PageSize);

            var at = FindRevisionOffset(page);
            Assert.That(at, Is.GreaterThan(0), $"could not locate the format revision in meta slot {slot}");
            BitConverter.GetBytes(revision).CopyTo(page, at);

            // Stamp with the page's OWN recorded index, not the slot number. They are not the same on an A/B pair — the
            // twin's image records the PRIMARY's index, which is exactly how a reader tells a twin from a root — and
            // passing the slot number rewrites that field, converting a re-label into a structural change.
            //
            // This is not hypothetical: the first version of this primitive passed `slot`, and it took
            // AssertOnlyDeclaredBytesChanged to notice — one stray byte at offset 48 of page 1. The test that used it was
            // green, because it asserted an exception thrown by the version gate, which fires long before anything reads
            // a page index. Byte-exactness caught what the assertion could not have.
            //
            // allowSectorFooter: false because an A/B protected page is covered by its twin rather than by per-sector
            // CRCs, so the whole-page form is the one the engine uses for it.
            PagedMMF.StampPageForWrite(page, PageSectorFooter.ReadFilePageIndex(page), allowSectorFooter: false);
            Array.Copy(page, 0, data, slot * IntegrityConstants.PageSize, IntegrityConstants.PageSize);

            var pageBase = (long)slot * IntegrityConstants.PageSize;
            ranges.Add(new ByteRange(pageBase + at, sizeof(int)));
            ranges.Add(new ByteRange(pageBase + PageBaseHeader.PageChecksumOffset, PageBaseHeader.PageChecksumSize));
        }

        File.WriteAllBytes(path, data);

        return new DamageRecord(
            "D13",
            $"both meta slots re-stamped as format revision {revision} (this build speaks {DatabaseRepair.SupportedFormatRevision})",
            ranges,
            ["CHK-BOO-02"],
            IntegrityVerdict.Sound,
            RepairIsLossless: false);
    }

    /// <summary>
    /// Locates the format revision by finding the header signature and stepping over it, rather than hard-coding an offset
    /// that a later layout change would silently invalidate.
    /// </summary>
    private static int FindRevisionOffset(byte[] page)
    {
        var signature = System.Text.Encoding.UTF8.GetBytes("TyphonDatabase");
        for (var i = 0; i + signature.Length < IntegrityConstants.PageHeaderSize + 256; i++)
        {
            var hit = true;
            for (var j = 0; j < signature.Length; j++)
            {
                if (page[i + j] != signature[j])
                {
                    hit = false;
                    break;
                }
            }

            if (hit)
            {
                // HeaderSignature is a fixed 32-byte field; DatabaseFormatRevision is the int immediately after it.
                return i + 32;
            }
        }

        return -1;
    }

    /// <summary>
    /// <b>D6</b> — points one component-catalog row's data-segment pointer at a page that is not a segment root.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The manifest is a set of pointers, and this is the shape a torn catalog page leaves: a row that still parses,
    /// still names a page inside the file, and names the wrong one. That is the case a reader must survive — an
    /// out-of-range pointer is easy to reject, while an in-range pointer to the wrong page is what turns a scan into a
    /// crash or, worse, into confident nonsense about a segment that belongs to somebody else.
    /// </para>
    /// <para>
    /// The target is deliberately page 3 — inside the file, not a segment root, and not the meta pair, so the damage is
    /// exactly "this pointer is wrong" and nothing else.
    /// </para>
    /// </remarks>
    /// <param name="bundlePath">The bundle to damage.</param>
    /// <param name="ownerName">Receives the schema name of the row whose pointer was redirected.</param>
    /// <param name="bogusTarget">Receives the page the pointer now names.</param>
    internal static DamageRecord RedirectCatalogSegmentPointer(string bundlePath, out string ownerName, out int bogusTarget)
    {
        const int Target = 3;
        bogusTarget = Target;

        using var source = new OfflineBundlePageSource(bundlePath);
        var bootstrap = BootstrapReader.Read(source);
        Assert.That(bootstrap.TryGet("sys.ComponentR1", out var spi), Is.True, "the bootstrap must name the component catalog");

        var catalogRoot = spi.GetInt(0);
        var page = new byte[IntegrityConstants.PageSize];
        Assert.That(source.TryReadPage(catalogRoot, page), Is.True);

        var geometry = ChunkGeometry.FromPage(page);
        Assert.That(geometry.IsUsable, Is.True, "the catalog segment must record a stride");

        var segment = new SegmentWalker(source).WalkSegment(catalogRoot);
        var pages = segment.Pages;

        // Pick the first row that actually owns a data segment, so the damage lands on a live pointer rather than on a
        // legitimately-absent one.
        for (var id = 0; id < geometry.Capacity(pages.Count); id++)
        {
            if (!geometry.TryLocate(id, out var ordinal, out var chunkInPage) || ordinal >= pages.Count)
            {
                continue;
            }

            if (!source.TryReadPage(pages[ordinal], page) || !geometry.IsChunkAllocated(page, ordinal == 0, chunkInPage))
            {
                continue;
            }

            var at = geometry.OffsetInPage(ordinal, chunkInPage);
            var row = MemoryMarshal.Read<ComponentR1>(new ReadOnlySpan<byte>(page, at, Unsafe.SizeOf<ComponentR1>()));
            var name = row.Name.AsString;
            if (string.IsNullOrWhiteSpace(name) || row.ComponentSPI == 0 || name == ArchetypeR1.SchemaName)
            {
                continue;   // leave the archetype catalog reachable, so the test can prove the rest survives
            }

            ownerName = name;
            var fieldOffset = Marshal.OffsetOf<ComponentR1>(nameof(ComponentR1.ComponentSPI)).ToInt32();
            var fileOffset = ((long)pages[ordinal] * IntegrityConstants.PageSize) + at + fieldOffset;

            source.Dispose();
            var ranges = new List<ByteRange> { WriteInt(bundlePath, fileOffset, Target) };
            ranges.AddRange(RestampPage(bundlePath, pages[ordinal]));

            return new DamageRecord(
                "D6",
                $"component catalog row '{name}' redirected from segment {row.ComponentSPI} to page {Target}",
                ranges,
                ["CHK-BOO-05"],
                IntegrityVerdict.Unopenable,
                RepairIsLossless: false);
        }

        ownerName = null;
        throw new InvalidOperationException("no component-catalog row owned a data segment, so nothing could be redirected");
    }

    /// <summary>How to break a revision chain.</summary>
    internal enum ChainBreak
    {
        /// <summary>Point the chain's tail at a chunk id the segment does not have.</summary>
        OutOfRange,

        /// <summary>Point the chain's tail at itself.</summary>
        Cycle
    }

    /// <summary>
    /// <b>D6</b> — rewrites one revision chain's <c>NextChunkId</c> so the chain leads somewhere it must not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Four bytes, inside a chunk, on a page that is re-stamped afterwards — so what a scanner meets is a chain that
    /// leads astray, not a torn page. That distinction is the whole value of the fixture: a checksum failure would be
    /// found by the physical sweep, which proves nothing about whether the chain family works.
    /// </para>
    /// <para>
    /// <b>Two findings are expected, not one, and that is correct.</b> A chain whose head points anywhere at all is by
    /// definition not collapsed, so <c>CHK-CHN-02</c> fires alongside the pointer or cycle finding. Declaring only the
    /// interesting one would make <see cref="AssertDetectedExactly"/> fail — which is the kit doing its job rather than
    /// a defect in the check.
    /// </para>
    /// </remarks>
    /// <param name="bundlePath">The bundle to damage.</param>
    /// <param name="how">Which way to break the chain.</param>
    internal static DamageRecord BreakRevisionChain(string bundlePath, ChainBreak how)
    {
        int filePage;
        long fieldOffset;
        int newValue;
        int rootChunk;
        string owner;

        using (var source = new OfflineBundlePageSource(bundlePath))
        {
            var bootstrap = BootstrapReader.Read(source);
            var walker = new SegmentWalker(source);
            var roots = SweepRoots(source);

            var manifest = new SchemaCatalogReader(source, roots);
            manifest.Read(bootstrap);
            Assert.That(manifest.IsUsable, Is.True, "precondition: the manifest must be readable to find a revision chain");

            if (!TryFindChainRoot(source, walker, manifest, out owner, out var segment, out var geometry, out rootChunk))
            {
                throw new InvalidOperationException("no revision chain root was found, so none could be broken");
            }

            geometry.TryLocate(rootChunk, out var ordinal, out var chunkInPage);
            filePage = segment.Pages[ordinal];

            // NextChunkId is documented as the FIRST field of CompRevStorageHeader, so the chain's tail pointer is at
            // chunk offset 0. Asserting that rather than trusting the comment: a layout change would otherwise silently
            // relocate the damage into whatever field moved into its place.
            Assert.That(Marshal.OffsetOf<CompRevStorageHeader>(nameof(CompRevStorageHeader.NextChunkId)).ToInt32(), Is.Zero,
                "NextChunkId must be the first field of the chain header, or this primitive damages the wrong bytes");

            fieldOffset = ((long)filePage * IntegrityConstants.PageSize) + geometry.OffsetInPage(ordinal, chunkInPage);
            newValue = how == ChainBreak.Cycle ? rootChunk : geometry.Capacity(segment.Pages.Count) + 1000;
        }

        var ranges = new List<ByteRange> { WriteInt(bundlePath, fieldOffset, newValue) };
        ranges.AddRange(RestampPage(bundlePath, filePage));

        var codes = how == ChainBreak.Cycle
            ? new[] { ChainChecks.Collapsed, ChainChecks.NoCycles }
            : [ChainChecks.Collapsed, ChainChecks.PointerResolves];

        return new DamageRecord(
            how == ChainBreak.Cycle ? "D6(cycle)" : "D6(dangling)",
            how == ChainBreak.Cycle
                ? $"revision chain for '{owner}' at chunk {rootChunk} points at itself"
                : $"revision chain for '{owner}' at chunk {rootChunk} points at chunk {newValue}, past the segment's capacity",
            ranges,
            codes,
            how == ChainBreak.Cycle ? IntegrityVerdict.Unopenable : IntegrityVerdict.DataLoss,
            RepairIsLossless: false);
    }

    /// <summary>Finds the first allocated revision chunk that owns an entity — a chain root.</summary>
    private static bool TryFindChainRoot(OfflineBundlePageSource source, SegmentWalker walker, SchemaCatalogReader manifest,
        out string owner, out SegmentView segment, out ChunkGeometry geometry, out int chunkId)
    {
        var page = new byte[IntegrityConstants.PageSize];

        foreach (var component in manifest.Components.Values)
        {
            if (component.RevisionSegmentRoot == 0 || !source.TryReadPage(component.RevisionSegmentRoot, page))
            {
                continue;
            }

            var g = ChunkGeometry.FromPage(page);
            if (!g.IsUsable)
            {
                continue;
            }

            var seg = walker.WalkSegment(component.RevisionSegmentRoot);
            for (var id = 0; id < g.Capacity(seg.Pages.Count); id++)
            {
                if (!g.TryLocate(id, out var ordinal, out var chunkInPage) || ordinal >= seg.Pages.Count)
                {
                    continue;
                }

                if (!source.TryReadPage(seg.Pages[ordinal], page) || !g.IsChunkAllocated(page, ordinal == 0, chunkInPage))
                {
                    continue;
                }

                var at = g.OffsetInPage(ordinal, chunkInPage);
                var header = MemoryMarshal.Read<CompRevStorageHeader>(
                    new ReadOnlySpan<byte>(page, at, Unsafe.SizeOf<CompRevStorageHeader>()));

                if (header.EntityPK == 0)
                {
                    continue;
                }

                owner = component.Name;
                segment = seg;
                geometry = g;
                chunkId = id;
                return true;
            }
        }

        owner = null;
        segment = null;
        geometry = default;
        chunkId = -1;
        return false;
    }

    /// <summary>Every segment root the physical sweep finds — the list every pointer is validated against.</summary>
    private static List<int> SweepRoots(IPageSource source)
    {
        var roots = new List<int>();
        var page = new byte[IntegrityConstants.PageSize];

        for (var p = 0; p < source.PageCount; p++)
        {
            if (!source.TryReadPage(p, page) || (PageImage.Flags(page) & PageBlockFlags.IsLogicalSegmentRoot) == 0)
            {
                continue;
            }

            if (MemoryMarshal.Read<int>(PageImage.RawData(page)) == p)
            {
                roots.Add(p);
            }
        }

        return roots;
    }

    /// <summary>How to break a cluster slot.</summary>
    internal enum ClusterBreak
    {
        /// <summary>Zero the entity key of a slot the occupancy word says is live.</summary>
        ClearLiveKey,

        /// <summary>Give a second live slot the same entity key as the first.</summary>
        DuplicateKey,

        /// <summary>Raise a live slot's entity key past the archetype's restored watermark.</summary>
        KeyAboveWatermark
    }

    /// <summary>
    /// <b>D5</b> — rewrites one live cluster slot's entity key so identity, occupancy or the watermark disagrees.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Eight bytes, at <c>8 + 8·componentCount + 8·slot</c> from the cluster base — an offset that is only computable
    /// because the archetype's component count is in <c>ArchetypeR1</c>. The slot array holds packed
    /// <c>EntityId</c>s (routing id in the low 16 bits, key above), so a forged key is shifted into place rather than
    /// written raw; writing a bare counter here would produce an id belonging to archetype 0 and the damage would be
    /// about routing rather than about identity.
    /// </para>
    /// <para>
    /// <see cref="ClusterBreak.KeyAboveWatermark"/> expects two codes, and both are true: a key past the watermark is
    /// <c>CHK-CLU-05</c> seen per entity and <c>CHK-ALO-02</c> seen per archetype — the same disagreement from the two
    /// ends RB-06 describes.
    /// </para>
    /// </remarks>
    /// <param name="bundlePath">The bundle to damage.</param>
    /// <param name="how">Which way to break the slot.</param>
    internal static DamageRecord BreakClusterSlot(string bundlePath, ClusterBreak how)
    {
        const int RoutingBits = 16;

        int filePage;
        long slotOffset;
        long newRaw;
        string archetypeName;
        var ranges = new List<ByteRange>();

        using (var source = new OfflineBundlePageSource(bundlePath))
        {
            var roots = SweepRoots(source);
            var manifest = new SchemaCatalogReader(source, roots);
            manifest.Read(BootstrapReader.Read(source));
            Assert.That(manifest.IsUsable, Is.True, "precondition: the manifest must be readable to find a cluster");

            var walker = new SegmentWalker(source);
            var page = new byte[IntegrityConstants.PageSize];

            ArchetypeView target = null;
            SegmentView segment = null;
            var geometry = default(ChunkGeometry);
            var chunkId = -1;
            var liveSlots = new List<int>();

            foreach (var a in manifest.Archetypes.Values)
            {
                if (a.ClusterSegmentRoot == 0 || !source.TryReadPage(a.ClusterSegmentRoot, page))
                {
                    continue;
                }

                var g = ChunkGeometry.FromPage(page);
                if (!g.IsUsable)
                {
                    continue;
                }

                var seg = walker.WalkSegment(a.ClusterSegmentRoot);
                for (var id = 0; id < g.Capacity(seg.Pages.Count) && chunkId < 0; id++)
                {
                    if (!g.TryLocate(id, out var ordinal, out var chunkInPage) || ordinal >= seg.Pages.Count)
                    {
                        continue;
                    }

                    if (!source.TryReadPage(seg.Pages[ordinal], page) || !g.IsChunkAllocated(page, ordinal == 0, chunkInPage))
                    {
                        continue;
                    }

                    var at = g.OffsetInPage(ordinal, chunkInPage);
                    var occupancy = MemoryMarshal.Read<ulong>(new ReadOnlySpan<byte>(page, at, sizeof(ulong)));
                    liveSlots.Clear();
                    for (var s = 0; s < 64 && (8 + 8 * a.ComponentCount + s * 8 + 8) <= g.Stride; s++)
                    {
                        if ((occupancy & (1UL << s)) != 0)
                        {
                            liveSlots.Add(s);
                        }
                    }

                    // DuplicateKey needs two live slots in ONE cluster; the others need only one.
                    if (liveSlots.Count >= (how == ClusterBreak.DuplicateKey ? 2 : 1))
                    {
                        target = a;
                        segment = seg;
                        geometry = g;
                        chunkId = id;
                    }
                }

                if (chunkId >= 0)
                {
                    break;
                }
            }

            if (chunkId < 0)
            {
                throw new InvalidOperationException($"no cluster with enough live slots was found for {how}");
            }

            geometry.TryLocate(chunkId, out var ord, out var inPage);
            filePage = segment.Pages[ord];
            source.TryReadPage(filePage, page);

            var clusterAt = geometry.OffsetInPage(ord, inPage);
            var keysAt = clusterAt + 8 + (8 * target.ComponentCount);
            var victim = how == ClusterBreak.DuplicateKey ? liveSlots[1] : liveSlots[0];
            slotOffset = ((long)filePage * IntegrityConstants.PageSize) + keysAt + (victim * 8);

            var victimRaw = MemoryMarshal.Read<long>(new ReadOnlySpan<byte>(page, keysAt + victim * 8, sizeof(long)));
            var routing = victimRaw & ((1L << RoutingBits) - 1);

            newRaw = how switch
            {
                ClusterBreak.ClearLiveKey => 0,
                ClusterBreak.DuplicateKey => MemoryMarshal.Read<long>(
                    new ReadOnlySpan<byte>(page, keysAt + liveSlots[0] * 8, sizeof(long))),
                _ => ((target.NextEntityKey + 1000) << RoutingBits) | routing
            };

            archetypeName = target.Name;
        }

        ranges.Add(WriteLong(bundlePath, slotOffset, newRaw));
        ranges.AddRange(RestampPage(bundlePath, filePage));

        // The MAP codes are not incidental noise — they are the damage seen from the other side, and declaring them is
        // what proves MAP-01/02 actually cross-check. Every one of these mutations changes WHICH identities the cluster
        // holds, and the EntityMap is not edited to match, so the two structures genuinely disagree afterwards:
        //
        //   ClearLiveKey       the entity leaves the cluster; the map still names it        → MAP-01 (orphan)
        //   DuplicateKey       one entity leaves, another gains a second slot               → MAP-01 (orphan + misdirect)
        //   KeyAboveWatermark  the old key leaves and a new one appears, in both directions → MAP-01 and MAP-02
        //
        // Before the map comparison ran these mutations produced only their CLU/ALO codes, and the fixtures said so.
        // That they now say more is the checks working, not the fixtures drifting.
        var (codes, verdict, description) = how switch
        {
            ClusterBreak.ClearLiveKey =>
                (new[] { ClusterChecks.OccupancyAgreesWithKeys, EntityMapChecks.EntriesResolve }, IntegrityVerdict.DataLoss,
                    $"a live slot of '{archetypeName}' had its entity key zeroed"),
            ClusterBreak.DuplicateKey =>
                (new[] { ClusterChecks.NoDuplicateKeys, EntityMapChecks.EntriesResolve }, IntegrityVerdict.Unopenable,
                    $"two live slots of '{archetypeName}' now claim the same entity"),
            _ => (new[]
                    {
                        ClusterChecks.KeysBelowWatermark, ClusterChecks.KeyWatermark,
                        EntityMapChecks.EntriesResolve, EntityMapChecks.SlotsAreReachable
                    },
                    IntegrityVerdict.Unopenable,
                    $"a live slot of '{archetypeName}' holds a key past the archetype's watermark")
        };

        return new DamageRecord($"D5({how})", description, ranges, codes, verdict, RepairIsLossless: false);
    }

    /// <summary>
    /// <b>D6</b> — points an EntityMap directory slot at a chunk id outside its own segment.
    /// </summary>
    /// <remarks>
    /// The shape RB-01 warns about in the sharpest terms: a hash directory holds chunk-id POINTERS, and trusting a torn
    /// one "dereferences garbage into a hard process crash before any loud-fail can fire". The target is deliberately far
    /// past the segment's capacity rather than merely free — an in-range-but-free id is a state linear hashing produces
    /// legitimately mid-split, so it is caveated rather than reported, while an id the segment cannot contain at all is
    /// unambiguously damage.
    /// </remarks>
    /// <param name="bundlePath">The bundle to damage.</param>
    internal static DamageRecord RedirectEntityMapDirectorySlot(string bundlePath)
    {
        int filePage;
        long slotFileOffset;
        int bogus;
        string archetypeName;

        using (var source = new OfflineBundlePageSource(bundlePath))
        {
            var roots = SweepRoots(source);
            var manifest = new SchemaCatalogReader(source, roots);
            manifest.Read(BootstrapReader.Read(source));

            var walker = new SegmentWalker(source);
            var page = new byte[IntegrityConstants.PageSize];

            ArchetypeView target = null;
            SegmentView segment = null;
            var geometry = default(ChunkGeometry);
            var directoryChunkId = -1;

            foreach (var a in manifest.Archetypes.Values)
            {
                if (a.EntityMapRoot == 0 || !source.TryReadPage(a.EntityMapRoot, page))
                {
                    continue;
                }

                var g = ChunkGeometry.FromPage(page);
                if (!g.IsUsable)
                {
                    continue;
                }

                var seg = walker.WalkSegment(a.EntityMapRoot);

                // The meta record is chunk 0; its first inline directory id names the chunk to redirect.
                g.TryLocate(0, out var metaOrd, out var metaInPage);
                if (metaOrd >= seg.Pages.Count || !source.TryReadPage(seg.Pages[metaOrd], page))
                {
                    continue;
                }

                var metaAt = g.OffsetInPage(metaOrd, metaInPage);
                var firstDir = MemoryMarshal.Read<int>(new ReadOnlySpan<byte>(page, metaAt + 28, sizeof(int)));
                if (firstDir <= 0)
                {
                    continue;
                }

                target = a;
                segment = seg;
                geometry = g;
                directoryChunkId = firstDir;
                break;
            }

            if (directoryChunkId < 0)
            {
                throw new InvalidOperationException("no EntityMap with an inline directory chunk was found");
            }

            geometry.TryLocate(directoryChunkId, out var ord, out var inPage);
            filePage = segment.Pages[ord];
            slotFileOffset = ((long)filePage * IntegrityConstants.PageSize) + geometry.OffsetInPage(ord, inPage);
            bogus = geometry.Capacity(segment.Pages.Count) + 100_000;
            archetypeName = target.Name;
        }

        var ranges = new List<ByteRange> { WriteInt(bundlePath, slotFileOffset, bogus) };
        ranges.AddRange(RestampPage(bundlePath, filePage));

        return new DamageRecord(
            "D6(map-directory)",
            $"the EntityMap for '{archetypeName}' now names bucket chunk {bogus}, which its segment cannot contain",
            ranges,
            [EntityMapChecks.PointersResolve],
            IntegrityVerdict.Divergent,
            RepairIsLossless: true);
    }

    /// <summary>
    /// <b>D6</b> — clears one entity record's chain pointer, stranding a revision chain nothing references.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Four bytes in the value half of a bucket, chosen because the resulting shape is one no single-structure walk can
    /// see. The chain is still there, still well-formed, still carrying its owning entity key; the entity is still there,
    /// still live, still in the map. Only the reference between them is gone — so the chain family reports a healthy
    /// chain, the map family reports a reachable entity, and the storage is unreclaimable forever. That is precisely
    /// <c>CHN-06</c>'s territory and nothing else's.
    /// </para>
    /// <para>
    /// The offset arithmetic is the reason this primitive could not be written before the manifest read the VSBS: it
    /// needs the bucket's key/value split, which needs the entity-record size, which needs the archetype's Versioned
    /// component count.
    /// </para>
    /// </remarks>
    /// <param name="bundlePath">The bundle to damage.</param>
    /// <param name="strandedChunk">Receives the chain-root chunk id that is now unreferenced.</param>
    internal static DamageRecord OrphanRevisionChain(string bundlePath, out int strandedChunk)
    {
        int filePage;
        long pointerOffset;
        string archetypeName;

        using (var source = new OfflineBundlePageSource(bundlePath))
        {
            var roots = SweepRoots(source);
            var manifest = new SchemaCatalogReader(source, roots);
            manifest.Read(BootstrapReader.Read(source));
            Assert.That(manifest.IsUsable, Is.True, "precondition: the manifest must be readable to find an entity record");

            var walker = new SegmentWalker(source);
            var page = new byte[IntegrityConstants.PageSize];

            var archetype = manifest.Archetypes.Values.First(a => a.EntityMapRoot != 0 && a.VersionedSlotCount > 0);
            archetypeName = archetype.Name;

            var segment = walker.WalkSegment(archetype.EntityMapRoot);
            Assert.That(source.TryReadPage(segment.Pages[0], page), Is.True);
            var geometry = ChunkGeometry.FromPage(page);
            Assert.That(geometry.IsUsable, Is.True);

            var recordSize = archetype.EntityRecordSize;
            var capacity = (geometry.Stride - 12) / (sizeof(long) + recordSize);
            var valuesAt = 12 + (capacity * sizeof(long));

            // The meta's first inline directory slot names a directory chunk; its first populated slot names a bucket.
            var meta = ReadChunk(source, segment, geometry, 0);
            var directoryId = MemoryMarshal.Read<int>(meta.AsSpan(28));
            var directory = ReadChunk(source, segment, geometry, directoryId);

            var found = -1;
            byte[] bucket = null;
            var entryIndex = -1;
            for (var slot = 0; slot < 64 && found < 0; slot++)
            {
                var bucketId = MemoryMarshal.Read<int>(directory.AsSpan(slot * sizeof(int)));
                if (bucketId <= 0)
                {
                    continue;
                }

                var candidate = ReadChunk(source, segment, geometry, bucketId);
                if (candidate[4] == 0)
                {
                    continue;   // an empty bucket has no record to strand
                }

                // Take an entry whose chain pointer is actually set, so clearing it strands a real chain.
                for (var i = 0; i < candidate[4] && i < capacity; i++)
                {
                    var at = valuesAt + (i * recordSize) + ClusterEntityRecordAccessor.CompRevOffset;
                    if (MemoryMarshal.Read<int>(candidate.AsSpan(at)) > 0)
                    {
                        found = bucketId;
                        bucket = candidate;
                        entryIndex = i;
                        break;
                    }
                }
            }

            Assert.That(found, Is.GreaterThan(0), "no entity record with a chain pointer was found, so none could be stranded");

            var recordAt = valuesAt + (entryIndex * recordSize) + ClusterEntityRecordAccessor.CompRevOffset;
            strandedChunk = MemoryMarshal.Read<int>(bucket.AsSpan(recordAt));

            geometry.TryLocate(found, out var ordinal, out var chunkInPage);
            filePage = segment.Pages[ordinal];
            pointerOffset = ((long)filePage * IntegrityConstants.PageSize) + geometry.OffsetInPage(ordinal, chunkInPage) + recordAt;
        }

        var ranges = new List<ByteRange> { WriteInt(bundlePath, pointerOffset, 0) };
        ranges.AddRange(RestampPage(bundlePath, filePage));

        return new DamageRecord(
            "D6(orphan-chain)",
            $"an entity record of '{archetypeName}' no longer references revision chain {strandedChunk}",
            ranges,
            [CrossStructureChecks.ChainRootsReferenced],
            IntegrityVerdict.Divergent,
            RepairIsLossless: true);
    }

    /// <summary>Which way to break a component-collection handle.</summary>
    internal enum HandleBreak
    {
        /// <summary>Point the handle at a buffer id the collection segment does not contain.</summary>
        Dangle,

        /// <summary>Clear the handle, leaving its buffer allocated and referenced by nothing.</summary>
        Strand
    }

    /// <summary>
    /// <b>D6</b> — rewrites a component row's field-collection handle so its buffer dangles or is stranded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Four bytes, and the two modes produce genuinely different damage rather than two views of one. <b>Dangle</b> is
    /// data loss: the handle names storage that is not there, and a collection has no derived copy to rebuild from.
    /// <b>Strand</b> loses nothing and leaks everything — the buffer stays allocated, correct, and unreachable, which is
    /// the shape <b>#389</b> describes and the one no walk of a single structure can see.
    /// </para>
    /// <para>
    /// The target is a component row's <c>Fields</c> collection because it is a handle the manifest itself records, so
    /// the check can account for it without decoding per-entity component data. That is also exactly the boundary
    /// <c>ALO-04</c>'s reverse half declares: it stands down the moment a user component puts handles somewhere the scan
    /// cannot enumerate.
    /// </para>
    /// </remarks>
    /// <param name="bundlePath">The bundle to damage.</param>
    /// <param name="how">Which way to break it.</param>
    /// <param name="ownerName">Receives the schema name of the row whose handle was rewritten.</param>
    internal static DamageRecord BreakCollectionHandle(string bundlePath, HandleBreak how, out string ownerName)
    {
        const int BogusBuffer = 900_000;

        int filePage;
        long fieldOffset;
        int originalBuffer;

        using (var source = new OfflineBundlePageSource(bundlePath))
        {
            var bootstrap = BootstrapReader.Read(source);
            Assert.That(bootstrap.TryGet("sys.ComponentR1", out var spi), Is.True);

            var catalogRoot = spi.GetInt(0);
            var page = new byte[IntegrityConstants.PageSize];
            Assert.That(source.TryReadPage(catalogRoot, page), Is.True);

            var geometry = ChunkGeometry.FromPage(page);
            var segment = new SegmentWalker(source).WalkSegment(catalogRoot);
            var found = false;
            ownerName = null;
            fieldOffset = 0;
            filePage = 0;
            originalBuffer = 0;

            for (var id = 0; id < geometry.Capacity(segment.Pages.Count) && !found; id++)
            {
                if (!geometry.TryLocate(id, out var ordinal, out var chunkInPage) || ordinal >= segment.Pages.Count)
                {
                    continue;
                }

                if (!source.TryReadPage(segment.Pages[ordinal], page) || !geometry.IsChunkAllocated(page, ordinal == 0, chunkInPage))
                {
                    continue;
                }

                var at = geometry.OffsetInPage(ordinal, chunkInPage);
                var row = MemoryMarshal.Read<ComponentR1>(new ReadOnlySpan<byte>(page, at, Unsafe.SizeOf<ComponentR1>()));
                var name = row.Name.AsString;
                if (string.IsNullOrWhiteSpace(name) || row.Fields._bufferId == 0)
                {
                    continue;
                }

                ownerName = name;
                originalBuffer = row.Fields._bufferId;
                filePage = segment.Pages[ordinal];
                fieldOffset = ((long)filePage * IntegrityConstants.PageSize) + at
                    + Marshal.OffsetOf<ComponentR1>(nameof(ComponentR1.Fields)).ToInt32();
                found = true;
            }

            Assert.That(found, Is.True, "no component row carried a field-collection handle to break");
        }

        var newValue = how == HandleBreak.Dangle ? BogusBuffer : 0;
        var ranges = new List<ByteRange> { WriteInt(bundlePath, fieldOffset, newValue) };
        ranges.AddRange(RestampPage(bundlePath, filePage));

        return how == HandleBreak.Dangle
            ? new DamageRecord(
                "D6(dangling-handle)",
                $"component row '{ownerName}' now names buffer {BogusBuffer}, which its collection segment cannot contain",
                ranges,
                [BufferChecks.HandleTable],
                IntegrityVerdict.DataLoss,
                RepairIsLossless: false)
            : new DamageRecord(
                "D6(stranded-buffer)",
                $"component row '{ownerName}' no longer references buffer {originalBuffer}, which stays allocated",
                ranges,
                [BufferChecks.HandleTable],
                IntegrityVerdict.Divergent,
                RepairIsLossless: true);
    }

    /// <summary>How to break a B+Tree node's sibling link.</summary>
    internal enum IndexBreak
    {
        /// <summary>Point <c>NextChunk</c> at a chunk id the segment does not contain.</summary>
        Dangle,

        /// <summary>Point <c>NextChunk</c> back at the node itself.</summary>
        Cycle
    }

    /// <summary>
    /// <b>D6</b> — rewrites a B+Tree node's <c>NextChunk</c> so the level chain dangles or loops.
    /// </summary>
    /// <remarks>
    /// <c>NextChunk</c> sits at offset 12 of every node layout — inside the 20-byte prefix all four share — so this
    /// primitive needs no knowledge of the tree's key width, which is the same reason <c>IDX-06</c> can check it.
    /// The root is taken from the segment's chunk-0 directory rather than guessed, so the damage lands on a node that
    /// is genuinely part of a registered tree.
    /// </remarks>
    /// <param name="bundlePath">The bundle to damage.</param>
    /// <param name="how">Which way to break the link.</param>
    /// <param name="newTarget">Receives the chunk id the link now names.</param>
    internal static DamageRecord BreakIndexSiblingLink(string bundlePath, IndexBreak how, out int newTarget)
    {
        const int NextChunkOffset = 12;

        int filePage;
        long linkOffset;
        int rootChunk;

        using (var source = new OfflineBundlePageSource(bundlePath))
        {
            var roots = SweepRoots(source);
            var manifest = new SchemaCatalogReader(source, roots);
            manifest.Read(BootstrapReader.Read(source));
            Assert.That(manifest.IsUsable, Is.True);

            var walker = new SegmentWalker(source);
            var reader = new IndexDirectoryReader(source);
            var page = new byte[IntegrityConstants.PageSize];
            var entries = new List<IndexTreeEntry>();

            SegmentView segment = null;
            var geometry = default(ChunkGeometry);
            rootChunk = 0;

            foreach (var archetypeRoot in manifest.Archetypes.Values
                .SelectMany(a => new[] { a.IndexRoot, a.String64IndexRoot })
                .Where(r => r != 0)
                .Distinct())
            {
                if (!source.TryReadPage(archetypeRoot, page))
                {
                    continue;
                }

                var g = ChunkGeometry.FromPage(page);
                if (!g.IsUsable)
                {
                    continue;
                }

                var seg = walker.WalkSegment(archetypeRoot);
                if (!reader.TryReadDirectory(seg, g, entries))
                {
                    continue;
                }

                var tree = entries.FirstOrDefault(e => e.RootChunkId > 0);
                if (tree.RootChunkId <= 0)
                {
                    continue;
                }

                segment = seg;
                geometry = g;
                rootChunk = tree.RootChunkId;
                break;
            }

            Assert.That(rootChunk, Is.GreaterThan(0), "no index segment with a registered, non-empty tree was found");

            newTarget = how == IndexBreak.Cycle ? rootChunk : geometry.Capacity(segment.Pages.Count) + 50_000;

            geometry.TryLocate(rootChunk, out var ordinal, out var chunkInPage);
            filePage = segment.Pages[ordinal];
            linkOffset = ((long)filePage * IntegrityConstants.PageSize) + geometry.OffsetInPage(ordinal, chunkInPage)
                + NextChunkOffset;
        }

        var ranges = new List<ByteRange> { WriteInt(bundlePath, linkOffset, newTarget) };
        ranges.AddRange(RestampPage(bundlePath, filePage));

        return new DamageRecord(
            how == IndexBreak.Cycle ? "D6(index-cycle)" : "D6(index-dangling)",
            how == IndexBreak.Cycle
                ? $"index node {rootChunk} now links to itself"
                : $"index node {rootChunk} now links to chunk {newTarget}, past the segment's capacity",
            ranges,
            [IndexChecks.TreeStructure],
            how == IndexBreak.Cycle ? IntegrityVerdict.Unopenable : IntegrityVerdict.Divergent,
            RepairIsLossless: true);
    }

    /// <summary>What to break inside a B+Tree leaf entry.</summary>
    internal enum IndexEntryBreak
    {
        /// <summary>Rewrite a key so the node's keys are no longer ascending.</summary>
        KeyOrder,

        /// <summary>Point an entry's value at a cluster slot that holds no entity.</summary>
        DanglingValue
    }

    /// <summary>
    /// <b>D6</b> — corrupts one entry of a B+Tree leaf: its key's order, or the slot its value names.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both modes leave the tree structurally perfect — every link resolves, every chain terminates, every count fits —
    /// so <c>IDX-06</c> sees nothing. That separation is the point: structure and contents fail independently, and an
    /// index whose shape is flawless can still answer every query wrongly.
    /// </para>
    /// <para>
    /// The leaf is found by scanning the segment's allocated chunks for the leaf flag rather than by descending, so the
    /// primitive does not depend on the same traversal the check under test uses. A fixture that shares its subject's
    /// traversal cannot catch a defect in it.
    /// </para>
    /// </remarks>
    /// <param name="bundlePath">The bundle to damage.</param>
    /// <param name="how">Which way to break the entry.</param>
    /// <param name="fieldName">Receives the name of the indexed field whose tree was damaged.</param>
    internal static DamageRecord BreakIndexEntry(string bundlePath, IndexEntryBreak how, out string fieldName)
    {
        int filePage;
        long targetOffset;
        int writeSize;
        long newValue;

        using (var source = new OfflineBundlePageSource(bundlePath))
        {
            var roots = SweepRoots(source);
            var manifest = new SchemaCatalogReader(source, roots);
            manifest.Read(BootstrapReader.Read(source));
            Assert.That(manifest.IsUsable, Is.True);

            var walker = new SegmentWalker(source);
            var reader = new IndexDirectoryReader(source);
            var page = new byte[IntegrityConstants.PageSize];
            var entries = new List<IndexTreeEntry>();

            SegmentView segment = null;
            var geometry = default(ChunkGeometry);
            var layout = default(IndexNodeLayout);
            var leafChunk = -1;
            var count = 0;
            fieldName = null;

            foreach (var archetype in manifest.Archetypes.Values)
            {
                foreach (var root in new[] { archetype.IndexRoot, archetype.String64IndexRoot })
                {
                    if (root == 0 || leafChunk >= 0 || !source.TryReadPage(root, page))
                    {
                        continue;
                    }

                    var g = ChunkGeometry.FromPage(page);
                    var seg = walker.WalkSegment(root);
                    if (!g.IsUsable || !reader.TryReadDirectory(seg, g, entries))
                    {
                        continue;
                    }

                    foreach (var entry in entries)
                    {
                        if (entry.StableId < 0 || entry.Slot >= archetype.ComponentNames.Count
                            || !manifest.Components.TryGetValue(archetype.ComponentNames[entry.Slot], out var component))
                        {
                            continue;
                        }

                        var field = component.Fields.FirstOrDefault(f => f.FieldId == entry.StableId && f.HasIndex);
                        if (field == null)
                        {
                            continue;
                        }

                        var l = IndexNodeLayout.ForFieldType(field.Type);
                        if (!l.IsUsable)
                        {
                            continue;
                        }

                        // Descend THIS tree's leftmost path to its first leaf. Scanning the segment for any leaf was the
                        // first attempt and it silently damaged the wrong tree: an archetype index segment hosts the
                        // primary-key tree alongside the field ones, and a PK node read through an int layout is written
                        // at an offset that belongs to nothing. The scan came back Sound, because the tree under test was
                        // untouched — a fixture failing open, which is the worst way for one to fail.
                        var node = entry.RootChunkId;
                        for (var depth = 0; depth < 32 && node > 0; depth++)
                        {
                            if (!reader.IsAllocated(seg, g, node) || !reader.TryGetChunk(seg, g, node, out var bytes))
                            {
                                node = 0;
                                break;
                            }

                            if (IndexNodeLayout.IsLeaf(bytes))
                            {
                                if (IndexDirectoryReader.CountOf(bytes) < 2)
                                {
                                    node = 0;
                                }

                                break;
                            }

                            node = l.ValueAt(bytes, 0);
                        }

                        if (node <= 0)
                        {
                            continue;
                        }

                        reader.TryGetChunk(seg, g, node, out var leafBytes);
                        segment = seg;
                        geometry = g;
                        layout = l;
                        leafChunk = node;
                        count = Math.Min(IndexDirectoryReader.CountOf(leafBytes), l.Capacity);
                        fieldName = field.Name;
                        break;
                    }
                }
            }

            Assert.That(leafChunk, Is.GreaterThan(0), "no B+Tree leaf with two or more entries was found");

            reader.TryGetChunk(segment, geometry, leafChunk, out var leaf);
            geometry.TryLocate(leafChunk, out var ordinal, out var chunkInPage);
            filePage = segment.Pages[ordinal];
            var chunkAt = ((long)filePage * IntegrityConstants.PageSize) + geometry.OffsetInPage(ordinal, chunkInPage);

            if (how == IndexEntryBreak.KeyOrder)
            {
                // Overwrite the LAST key with the first one's value, so entry n-1 sorts before entry n-2 and the node's
                // ascending order breaks without any pointer changing.
                var slot = layout.PhysicalSlot(leaf, count - 1);
                targetOffset = chunkAt + layout.KeysOffset + (slot * layout.KeySize);
                writeSize = layout.KeySize;
                newValue = layout.KeySize <= sizeof(long)
                    ? MemoryMarshal.Read<long>(PadTo8(layout.KeyAt(leaf, 0)))
                    : 0;

                Assert.That(layout.KeySize, Is.LessThanOrEqualTo(sizeof(long)),
                    "the key-order primitive writes a scalar key; a string-keyed tree needs a different write");
            }
            else
            {
                // Point the entry at a cluster slot far past anything the archetype allocated. It stays a well-formed
                // ClusterLocation, so nothing structural notices.
                var slot = layout.PhysicalSlot(leaf, 0);
                targetOffset = chunkAt + layout.ValuesOffset + (slot * sizeof(int));
                writeSize = sizeof(int);
                newValue = ClusterLocation.Pack(500_000, 7);
            }
        }

        var ranges = new List<ByteRange>
        {
            writeSize == sizeof(int)
                ? WriteInt(bundlePath, targetOffset, (int)newValue)
                : WriteBytes(bundlePath, targetOffset, BitConverter.GetBytes(newValue).AsSpan(0, writeSize))
        };
        ranges.AddRange(RestampPage(bundlePath, filePage));

        return how == IndexEntryBreak.KeyOrder
            ? new DamageRecord(
                "D6(index-key-order)",
                $"the index on '{fieldName}' has a leaf whose keys no longer ascend",
                ranges,
                [IndexContentChecks.KeyOrder],
                IntegrityVerdict.Divergent,
                RepairIsLossless: true)
            : new DamageRecord(
                "D6(index-dangling-value)",
                $"an entry of the index on '{fieldName}' names a cluster slot that holds no entity",
                ranges,
                [IndexContentChecks.ValuesResolve],
                IntegrityVerdict.Divergent,
                RepairIsLossless: true);
    }

    /// <summary>Right-pads a key's bytes into 8 so a narrow key can be read as a long.</summary>
    private static ReadOnlySpan<byte> PadTo8(ReadOnlySpan<byte> key)
    {
        var buffer = new byte[sizeof(long)];
        key[..Math.Min(key.Length, buffer.Length)].CopyTo(buffer);
        return buffer;
    }

    /// <summary>Writes raw bytes at an absolute file offset and returns the range it wrote.</summary>
    private static ByteRange WriteBytes(string bundlePath, long fileOffset, ReadOnlySpan<byte> bytes)
    {
        using var fs = new FileStream(DataPath(bundlePath), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        fs.Seek(fileOffset, SeekOrigin.Begin);
        fs.Write(bytes);
        fs.Flush(true);
        return new ByteRange(fileOffset, bytes.Length);
    }

    /// <summary>Copies one chunk out of a segment. Copies, because callers hold several at once.</summary>
    private static byte[] ReadChunk(IPageSource source, SegmentView segment, ChunkGeometry geometry, int chunkId)
    {
        var page = new byte[IntegrityConstants.PageSize];
        Assert.That(geometry.TryLocate(chunkId, out var ordinal, out var chunkInPage), Is.True, $"chunk {chunkId}");
        Assert.That(ordinal, Is.LessThan(segment.Pages.Count), $"chunk {chunkId} is past the segment");
        Assert.That(source.TryReadPage(segment.Pages[ordinal], page), Is.True);

        return page.AsSpan(geometry.OffsetInPage(ordinal, chunkInPage), geometry.Stride).ToArray();
    }

    /// <summary>Writes one long at an absolute file offset and returns the range it wrote.</summary>
    private static ByteRange WriteLong(string bundlePath, long fileOffset, long value)
    {
        using var fs = new FileStream(DataPath(bundlePath), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        fs.Seek(fileOffset, SeekOrigin.Begin);
        fs.Write(BitConverter.GetBytes(value));
        fs.Flush(true);
        return new ByteRange(fileOffset, sizeof(long));
    }

    /// <summary>Writes one int at an absolute file offset and returns the range it wrote.</summary>
    private static ByteRange WriteInt(string bundlePath, long fileOffset, int value)
    {
        using var fs = new FileStream(DataPath(bundlePath), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        fs.Seek(fileOffset, SeekOrigin.Begin);
        fs.Write(BitConverter.GetBytes(value));
        fs.Flush(true);
        return new ByteRange(fileOffset, sizeof(int));
    }

    /// <summary>
    /// Re-stamps a page's checksum so the damage is the thing under test rather than a torn page.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without this the scan reports a checksum failure and never reaches the damage, so the test would pass while
    /// proving something else — the same trap the format-revision forgery had to avoid.
    /// </para>
    /// <para>
    /// <b>The page's own declared geometry decides the form, and forcing one is a bug.</b> A/B protected pages carry a
    /// whole-page CRC (their twin is what protects them from a torn write); ordinary data pages carry per-sector footers
    /// since format revision 6. An early version of this helper passed <c>allowSectorFooter: false</c> unconditionally,
    /// correct for the meta pair it was written against and wrong for every data page — it stamped a whole-page CRC over
    /// a page that declares sectors, so the scan reported <c>CHK-PHY-01</c> on top of the intended finding and
    /// <see cref="AssertDetectedExactly"/> caught it. Passing <c>true</c> lets <c>StampPageForWrite</c> read the page's
    /// declared sector count and pick the same form the engine would, which is correct for both classes.
    /// </para>
    /// </remarks>
    private static List<ByteRange> RestampPage(string bundlePath, int filePageIndex)
    {
        var path = DataPath(bundlePath);
        var data = File.ReadAllBytes(path);
        var pageBase = (long)filePageIndex * IntegrityConstants.PageSize;

        var before = new byte[IntegrityConstants.PageSize];
        Array.Copy(data, pageBase, before, 0, IntegrityConstants.PageSize);

        var page = (byte[])before.Clone();
        PagedMMF.StampPageForWrite(page, PageSectorFooter.ReadFilePageIndex(page), allowSectorFooter: true);

        Array.Copy(page, 0, data, pageBase, IntegrityConstants.PageSize);
        File.WriteAllBytes(path, data);

        // Report what the stamp ACTUALLY touched rather than what it was assumed to. Which bytes move depends on the
        // page's declared geometry — a whole-page CRC at offset 8 for an A/B page, per-sector footers growing down from
        // the end of the metadata region for a data page — and hard-coding either declares the wrong range for the other.
        // Diffing is exact by construction and stays correct when the footer layout changes.
        return Diff(before, page, pageBase);
    }

    /// <summary>Coalesces the byte-level differences between two page images into contiguous ranges.</summary>
    private static List<ByteRange> Diff(byte[] before, byte[] after, long baseOffset)
    {
        var ranges = new List<ByteRange>();
        var i = 0;

        while (i < before.Length)
        {
            if (before[i] == after[i])
            {
                i++;
                continue;
            }

            var start = i;
            while (i < before.Length && before[i] != after[i])
            {
                i++;
            }

            ranges.Add(new ByteRange(baseOffset + start, i - start));
        }

        return ranges;
    }

    /// <summary>Truncates the data file mid-page, the shape a partial copy leaves behind.</summary>
    internal static DamageRecord TruncateMidPage(string bundlePath, int keepBytesOfLastPage)
    {
        var dataPath = DataPath(bundlePath);
        long newLength;
        using (var fs = new FileStream(dataPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var wholePages = fs.Length / IntegrityConstants.PageSize;
            newLength = (wholePages - 1) * IntegrityConstants.PageSize + keepBytesOfLastPage;
            fs.SetLength(newLength);
            fs.Flush(true);
        }

        // A truncation is a length change, not a byte rewrite — AssertOnlyDeclaredBytesChanged does not apply to it,
        // and saying so here is cheaper than a special case that silently passes.
        return new DamageRecord(
            "truncation",
            $"data file truncated to {newLength} bytes, mid-page",
            [],
            ["CHK-BOO-01"],
            IntegrityVerdict.Divergent,
            RepairIsLossless: false);
    }

    // ── Internals ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Absolute path to the bundle's data file.</summary>
    internal static string DataPath(string bundlePath) => Path.Combine(bundlePath, IntegrityConstants.DataFileName);

    /// <summary>Fills a range inside one page with a constant, and returns the range it wrote.</summary>
    private static ByteRange WritePattern(string bundlePath, int filePageIndex, int offsetInPage, int length, byte value)
    {
        var dataPath = DataPath(bundlePath);
        var at = (long)filePageIndex * IntegrityConstants.PageSize + offsetInPage;

        using var fs = new FileStream(dataPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        if (at + length > fs.Length)
        {
            throw new InvalidOperationException($"page {filePageIndex} is past the end of the data file");
        }

        var buf = new byte[length];
        Array.Fill(buf, value);
        fs.Seek(at, SeekOrigin.Begin);
        fs.Write(buf);
        fs.Flush(true);

        return new ByteRange(at, length);
    }

    /// <summary>SHA-256 of the data file, for "this repair wrote nothing" assertions.</summary>
    internal static string HashDataFile(string bundlePath)
        => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(DataPath(bundlePath))));
}
