using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
