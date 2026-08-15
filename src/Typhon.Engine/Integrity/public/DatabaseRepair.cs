using JetBrains.Annotations;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Typhon.Engine;

/// <summary>
/// Turns an <see cref="IntegrityReport"/> into a reviewable <see cref="RepairPlan"/>, and applies one.
/// </summary>
/// <remarks>
/// <para>
/// The expensive, subtle part of repair — regenerating derived state from primary data in the correct order without
/// trusting anything stale — is already built and rule-governed inside the engine's recovery net. What was missing was
/// <b>invocability, ordering control, and reporting</b>: the net runs only at open, only on the crash path, cannot be
/// asked to run, and reports into log lines. This type gives it a front door, a plan, and a receipt.
/// </para>
/// <para>
/// <b>What repair will never do.</b> Edit bytes inside a primary page (guessing at damaged content manufactures plausible
/// data, which is strictly worse than a hole because it is undetectable afterwards). Splice a page from a backup into a
/// live database (that produces a page consistent with an older generation of its neighbours, converting detectable
/// corruption into undetectable corruption). Trust a derived structure to reconstruct primary data (an index is not a
/// backup of the rows). Or delete user data to satisfy a derived constraint.
/// </para>
/// </remarks>
[PublicAPI]
public static class DatabaseRepair
{
    /// <summary>The single on-disk format revision this build understands. Repair requires an exact match; scanning does not.</summary>
    public static int SupportedFormatRevision => PagedMMF.DatabaseFormatRevision;

    /// <summary>
    /// Why repair must refuse a database at this on-disk format revision, or <c>null</c> when it may proceed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Any</b> mismatch, not merely a newer one (<c>05-repair.md</c> §7, OQ-7). Pre-alpha carries no compatibility
    /// obligation, so this build knows exactly one revision and older and newer are equally un-understood. The asymmetry
    /// people expect — "older is surely safe to read" — is the dangerous one: a revision bump is free to re-mean bytes an
    /// older revision left unused, so an older page does not fail to decode, it decodes to a confident lie.
    /// </para>
    /// <para>
    /// The verb matters. <see cref="IntegrityScanner"/> still scans and reports a mismatch as a finding — diagnosis
    /// degrades, because refusing to diagnose is the opposite of what a scanner is for. Only mutation refuses, and it
    /// refuses without an override: a <c>--force</c> here would be a switch whose only function is to let someone corrupt
    /// a database this build cannot interpret, on a day they are already having a bad one.
    /// </para>
    /// </remarks>
    /// <param name="found">The revision recorded in the database.</param>
    public static string DescribeRevisionRefusal(int found)
    {
        var mine = SupportedFormatRevision;
        if (found == mine)
        {
            return null;
        }

        var direction = found > mine ? "newer" : "older";
        return $"This database is on-disk format revision {found}; this build speaks revision {mine}. Repair is refused on "
            + $"any revision mismatch. A {direction} revision is not a subset of this one — a revision bump may re-mean "
            + "bytes the other revision used differently, so writing to it under this build's interpretation would corrupt "
            + $"a database that is, as far as anyone knows, intact. Use a build that speaks revision {found} to repair it. "
            + "Scanning it is still safe and still works: run 'typhon check' for a diagnosis.";
    }

    /// <summary>
    /// Derives a repair plan from a report. Read-only: produces a description, changes nothing.
    /// </summary>
    /// <param name="report">The report to plan against.</param>
    /// <exception cref="ArgumentNullException"><paramref name="report"/> is <c>null</c>.</exception>
    public static RepairPlan Plan(IntegrityReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        // A plan is a proposal to mutate, so a revision this build must not write to produces a plan with no steps and the
        // reason attached — rather than a list of repairs that Apply is guaranteed to refuse. The scan's own findings are
        // preserved untouched: the operator still gets the diagnosis, just not an offer to act on it.
        var refusal = DescribeRevisionRefusal(report.Identity.FormatRevision);
        if (refusal != null)
        {
            return new RepairPlan
            {
                DatabaseFingerprint = Fingerprint(report),
                Source = report.Source,
                Verdict = report.Verdict,
                Steps = [],
                Loss = new LossManifest { Entries = [] },
                Unaddressed = [refusal],
                BlockedReason = refusal
            };
        }

        var steps = new List<RepairStep>();
        var losses = new List<LossEstimate>();
        var unaddressed = new List<string>();

        var pairSlots = new List<Locus>();
        var occupancyCodes = new List<string>();
        var derivedCodes = new List<string>();
        var lossyFindings = new List<IntegrityFinding>();

        for (var i = 0; i < report.Findings.Count; i++)
        {
            var f = report.Findings[i];

            // A live scan sees a consistent page but never a consistent database, so a cross-structure disagreement it
            // observed may simply be a mutation in flight. Acting on that would repair a database that was healthy.
            if (f.Confidence != IntegrityConfidence.Confirmed)
            {
                unaddressed.Add($"{f.Code}: observed on a live database (Suspected). Re-run the scan offline to confirm it before repairing.");
                continue;
            }

            switch (f.Code)
            {
                case "CHK-BOO-03" when f.Severity == IntegritySeverity.Divergence:
                case "CHK-PHY-01" when f.Repair == Repairability.Lossless && f.Locus.Kind == StorageSegmentKind.Other:
                    pairSlots.Add(f.Locus);
                    break;

                case "CHK-SEG-02":
                    occupancyCodes.Add(f.Code);
                    break;

                default:
                    if (f.Repair == Repairability.Lossless)
                    {
                        derivedCodes.Add(f.Code);
                    }
                    else if (f.Repair == Repairability.Lossy)
                    {
                        lossyFindings.Add(f);
                    }
                    else if (f.Severity <= IntegritySeverity.Divergence)
                    {
                        unaddressed.Add($"{f.Code} at {f.Locus}: {f.Summary} — no repair primitive applies. {EscalationFor(f)}");
                    }

                    break;
            }
        }

        var order = 0;

        // Ordering is a correctness constraint. Pair slots go first: everything downstream reads directories, and a
        // directory pair running on one copy is one torn write away from making the database unopenable.
        for (var i = 0; i < pairSlots.Count; i++)
        {
            steps.Add(new RepairStep
            {
                Order = ++order,
                Action = RepairAction.RestorePairSlot,
                Class = RepairClass.Regenerate,
                Addresses = ["CHK-BOO-03", "CHK-PHY-01"],
                Locus = pairSlots[i],
                Description = $"Rewrite page {pairSlots[i].FilePageIndex} from its valid sibling slot.",
                Rationale = "The pair's other slot holds a complete, verified image. Copying it back restores the redundancy "
                    + "that lets a torn write to this page be survived rather than being fatal. Nothing is read from the "
                    + "damaged slot, so nothing damaged can propagate."
            });
        }

        if (derivedCodes.Count > 0)
        {
            steps.Add(new RepairStep
            {
                Order = ++order,
                Action = RepairAction.RegenerateDerivedStructures,
                Class = RepairClass.Regenerate,
                Addresses = derivedCodes,
                Description = "Open the database so the engine regenerates every derived structure, then close it cleanly.",
                Rationale = "Indexes, entity maps, revision chains, cluster heads and spatial state are pure functions of "
                    + "primary data. The engine's own recovery net already rebuilds them in the order the rules require — "
                    + "chains scrubbed before indexes are built over them, the entity map re-derived before anything reads "
                    + "it. Reusing it is safer than reimplementing that ordering here."
            });
        }

        // Occupancy last among the lossless steps: it is derived from the reachability walk, so it must be recomputed
        // AFTER anything that changes what is reachable.
        if (occupancyCodes.Count > 0)
        {
            steps.Add(new RepairStep
            {
                Order = ++order,
                Action = RepairAction.RederiveOccupancy,
                Class = RepairClass.Regenerate,
                Addresses = occupancyCodes,
                Description = "Recompute the page-allocation bitmap from the segments that actually exist.",
                Rationale = "The bitmap is derived state and is never the authority on what is allocated, so a disagreement "
                    + "means the bitmap is wrong rather than that the pages are. Recomputing it reclaims leaked space and — "
                    + "more importantly — clears phantom free bits that would otherwise let the allocator hand a live page "
                    + "to a second owner."
            });
        }

        for (var i = 0; i < lossyFindings.Count; i++)
        {
            var f = lossyFindings[i];
            unaddressed.Add(
                $"{f.Code} at {f.Locus}: {f.Summary} — repairing this means excising data that is already unreadable, and "
                + "this build will not guess at where an archetype's rows begin and end on a damaged page. "
                + EscalationFor(f));
            losses.Add(f.Loss);
        }

        return new RepairPlan
        {
            DatabaseFingerprint = Fingerprint(report),
            Source = report.Source,
            Verdict = report.Verdict,
            Steps = steps,
            Loss = new LossManifest { Entries = losses },
            Unaddressed = unaddressed
        };
    }

    /// <summary>
    /// Applies a plan. The only mutating operation in the feature.
    /// </summary>
    /// <param name="bundlePath">Path to the bundle to repair. Must not be open in another process.</param>
    /// <param name="plan">The plan to apply.</param>
    /// <param name="allowLoss">Consent to lossy steps. Without it, every <see cref="RepairClass.Excise"/> step is skipped.</param>
    /// <param name="backupFirst">Copy the bundle beside itself before the first mutation. Cheap insurance; on by default.</param>
    /// <param name="dryRun">Log every step and execute none.</param>
    /// <param name="regenerateDerived">
    /// Callback that opens and cleanly closes the database so the engine's rebuild net runs. Supplied by the caller
    /// because the repair module must not take a dependency on engine construction. <c>null</c> skips those steps.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="bundlePath"/> or <paramref name="plan"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException">The database changed since the plan was produced.</exception>
    public static RepairOutcome Apply(string bundlePath, RepairPlan plan, bool allowLoss = false, bool backupFirst = true,
        bool dryRun = false, Action<string> regenerateDerived = null)
    {
        ArgumentNullException.ThrowIfNull(bundlePath);
        ArgumentNullException.ThrowIfNull(plan);

        var resolved = OfflineBundlePageSource.ResolveBundleDirectory(bundlePath);

        // Re-scan and refuse on drift. Repairing against a stale diagnosis is the failure mode this guard exists for.
        using (var probe = new OfflineBundlePageSource(resolved))
        {
            if (probe.LockHeld)
            {
                throw new InvalidOperationException(
                    $"'{resolved}' is open in another process. Repair requires exclusive access — close the database and retry.");
            }

            var current = IntegrityScanner.Scan(probe, new IntegrityOptions { Depth = plan.Verdict == IntegrityVerdict.Unopenable ? ScanDepth.Standard : ScanDepth.Deep });

            // The revision gate comes BEFORE the drift check, and reads the FILE rather than the plan. Before, because a
            // fingerprint mismatch sends the operator to "re-scan and make a fresh plan" — advice that loops forever when
            // the real problem is that this build must not write here at all. From the file, because a plan is an artefact
            // that can arrive from another build, another machine or a text editor, and the only revision that matters is
            // the one on the bytes about to be mutated.
            var refusal = DescribeRevisionRefusal(current.Identity.FormatRevision);
            if (refusal != null)
            {
                throw new InvalidOperationException(refusal);
            }

            var currentFingerprint = Fingerprint(current);
            if (!string.Equals(currentFingerprint, plan.DatabaseFingerprint, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The database has changed since this plan was produced, so the plan's diagnosis no longer describes it. "
                    + "Re-run the scan and produce a fresh plan.\n"
                    + $"  plan was built for: {plan.DatabaseFingerprint}\n"
                    + $"  database is now:    {currentFingerprint}");
            }
        }

        var results = new List<RepairStepResult>(plan.Steps.Count);
        string backupPath = null;

        if (backupFirst && !dryRun && plan.Steps.Count > 0)
        {
            backupPath = CopyBundle(resolved);
        }

        for (var i = 0; i < plan.Steps.Count; i++)
        {
            var step = plan.Steps[i];

            if (step.Class == RepairClass.Excise && !allowLoss)
            {
                results.Add(new RepairStepResult(step, StepOutcome.Skipped,
                    "Skipped: this step destroys data and no consent was given.", LossEstimate.None));
                continue;
            }

            if (dryRun)
            {
                results.Add(new RepairStepResult(step, StepOutcome.Skipped, "Dry run: not executed.", LossEstimate.None));
                continue;
            }

            try
            {
                var detail = Execute(resolved, step, regenerateDerived);
                results.Add(new RepairStepResult(step, StepOutcome.Succeeded, detail, LossEstimate.None));
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                results.Add(new RepairStepResult(step, StepOutcome.Failed, ex.Message, LossEstimate.None));
                break;   // a failed step may leave later ones invalid; stop and let the operator look
            }
        }

        IntegrityReport verification = null;
        if (!dryRun && plan.Steps.Count > 0)
        {
            using var source = new OfflineBundlePageSource(resolved);
            verification = IntegrityScanner.Scan(source, IntegrityOptions.Deep);
        }

        return new RepairOutcome { Plan = plan, Results = results, BackupPath = backupPath, VerificationReport = verification };
    }

    private static string Execute(string bundlePath, RepairStep step, Action<string> regenerateDerived) => step.Action switch
    {
        RepairAction.RestorePairSlot => PairSlotRepair.Restore(bundlePath, step.Locus.FilePageIndex),
        RepairAction.RederiveOccupancy => OccupancyRepair.Rederive(bundlePath),
        RepairAction.RegenerateDerivedStructures => RunRegeneration(bundlePath, regenerateDerived),
        _ => throw new InvalidOperationException($"Repair action {step.Action} is not implemented in this build.")
    };

    private static string RunRegeneration(string bundlePath, Action<string> regenerateDerived)
    {
        if (regenerateDerived == null)
        {
            throw new InvalidOperationException(
                "Regenerating derived structures needs a callback that opens and closes the database; none was supplied.");
        }

        regenerateDerived(bundlePath);
        return "The engine opened the database, rebuilt its derived structures and closed cleanly.";
    }

    /// <summary>
    /// A stable identity for "this database, in this state". Deliberately coarse: it must change when the database
    /// changes, and must not change merely because a scan ran twice.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both halves of that contract were broken, and the failure was total rather than occasional: the findings fold
    /// used <c>string.GetHashCode</c>, which .NET <b>randomises per process</b> as hash-flood mitigation. Two scans of
    /// byte-identical data therefore produced different fingerprints in different processes, so the documented
    /// two-step workflow — <c>repair --plan</c>, review it, <c>repair --apply</c> — could never succeed. It refused
    /// with <i>"the database has changed since this plan was produced"</i>, which is the most misleading answer
    /// available: it accuses the database of drifting to explain a defect in the comparison.
    /// </para>
    /// <para>
    /// The fold was also order-dependent, chaining findings in list order, so a re-scan that merely enumerated the same
    /// findings in a different sequence changed the value too. Now: sort, then FNV-1a over UTF-8 bytes with a
    /// separator — the same construction <c>ProfilerSessionMetadataBuilder</c> uses for schema fingerprints, and for
    /// the same reason. Sorting is what makes it order-independent; the byte-level hash is what makes it
    /// process-independent; the separator stops <c>("Ab", 1)</c> and <c>("A", …)</c> colliding by concatenation.
    /// </para>
    /// </remarks>
    /// <param name="report">The report to fingerprint.</param>
    public static string Fingerprint(IntegrityReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var id = report.Identity;
        var sb = new StringBuilder(128);
        sb.Append(id.Name).Append('|').Append(id.FormatRevision).Append('|').Append(id.PageCount).Append('|');
        sb.Append(id.SizeBytes).Append('|').Append(id.CheckpointLsn).Append('|').Append(id.MetaGeneration).Append('|');

        // Fold the findings in, so a plan built for one set of problems cannot be applied to a database with another set.
        var entries = new List<(string Code, int Page, long Occurrences)>(report.Findings.Count);
        for (var i = 0; i < report.Findings.Count; i++)
        {
            var f = report.Findings[i];
            entries.Add((f.Code ?? string.Empty, f.Locus.FilePageIndex, f.Occurrences));
        }

        entries.Sort(static (x, y) =>
        {
            var byCode = string.CompareOrdinal(x.Code, y.Code);
            if (byCode != 0)
            {
                return byCode;
            }
            var byPage = x.Page.CompareTo(y.Page);
            return byPage != 0 ? byPage : x.Occurrences.CompareTo(y.Occurrences);
        });

        const ulong offsetBasis = 14695981039346656037;
        const ulong prime = 1099511628211;
        var hash = offsetBasis;
        Span<byte> intBytes = stackalloc byte[sizeof(int)];
        Span<byte> longBytes = stackalloc byte[sizeof(long)];
        foreach (var (code, page, occurrences) in entries)
        {
            foreach (var b in Encoding.UTF8.GetBytes(code))
            {
                hash = (hash ^ b) * prime;
            }
            BinaryPrimitives.WriteInt32LittleEndian(intBytes, page);
            foreach (var b in intBytes)
            {
                hash = (hash ^ b) * prime;
            }
            BinaryPrimitives.WriteInt64LittleEndian(longBytes, occurrences);
            foreach (var b in longBytes)
            {
                hash = (hash ^ b) * prime;
            }
            hash = (hash ^ 0xFF) * prime;
        }

        sb.Append(hash.ToString("x16", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    private static string CopyBundle(string bundlePath)
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var target = bundlePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + $".pre-repair-{stamp}";
        Directory.CreateDirectory(target);

        foreach (var file in Directory.GetFiles(bundlePath, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(bundlePath, file);
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            File.Copy(file, destination, overwrite: true);
        }

        return target;
    }

    private static string EscalationFor(IntegrityFinding finding)
    {
        // "Nothing is lost" is about DATA, and says nothing about whether the database still functions — so it must not
        // be the consolation offered for a Fatal finding. A renamed bundle loses no bytes at all and cannot be opened by
        // any means; telling its owner "the database continues to work" is false at the exact moment they are reading a
        // report to find out whether it does.
        if (finding.Severity == IntegritySeverity.Fatal)
        {
            return "The database cannot be opened in this state; the finding above says what to do about it.";
        }

        if (finding.Loss == null || finding.Loss.IsNone)
        {
            return "Nothing is lost by leaving it; the database continues to work.";
        }

        return $"What is affected: {finding.Loss.Explanation} Restoring from a backup taken before the damage is the only "
            + "way to get it back — repair from within this database cannot recover data that is not there.";
    }
}
