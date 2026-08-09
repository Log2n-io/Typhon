using JetBrains.Annotations;
using System;
using System.Collections.Generic;

namespace Typhon.Engine;

/// <summary>
/// What a repair step is allowed to do, which is the safety boundary of the whole feature.
/// </summary>
[PublicAPI]
public enum RepairClass
{
    /// <summary>
    /// Discard a derived structure and rebuild it from primary data. Loss is definitionally zero: the output is a pure
    /// function of data that was not touched.
    /// </summary>
    Regenerate = 0,

    /// <summary>
    /// Remove a reference to primary data that cannot be read, so the rest of the database becomes usable. The datum is
    /// <b>already</b> gone — excision does not destroy it, it stops the database pretending it is there. Always requires
    /// explicit consent, because the operator may prefer a restore over a database that has silently narrowed.
    /// </summary>
    Excise = 1
}

/// <summary>The kinds of repair this build can actually perform.</summary>
[PublicAPI]
public enum RepairAction
{
    /// <summary>Rewrite the unusable half of an A/B protected page pair from its valid sibling, restoring the redundancy.</summary>
    RestorePairSlot = 0,

    /// <summary>Recompute the page-allocation bitmap from the reachability walk, reclaiming leaks and clearing phantoms.</summary>
    RederiveOccupancy = 1,

    /// <summary>
    /// Open the database so the engine's rebuild net regenerates every derived structure — indexes, entity maps, chains,
    /// cluster heads, spatial state — in the order those rules require, then close it cleanly.
    /// </summary>
    RegenerateDerivedStructures = 2,

    /// <summary>Excise references to primary data that cannot be read. Lossy; requires consent.</summary>
    ExciseUnreadablePrimary = 3
}

/// <summary>One ordered step of a repair.</summary>
[PublicAPI]
public sealed class RepairStep
{
    /// <summary>Position in the plan. Steps execute in this order and the order is a correctness constraint, not a preference.</summary>
    public required int Order { get; init; }

    /// <summary>What this step does.</summary>
    public required RepairAction Action { get; init; }

    /// <summary>Whether it regenerates or excises.</summary>
    public required RepairClass Class { get; init; }

    /// <summary>Check codes this step answers.</summary>
    public required IReadOnlyList<string> Addresses { get; init; }

    /// <summary>Where it acts.</summary>
    public Locus Locus { get; init; }

    /// <summary>Plain-English description of the action, for the operator reviewing the plan.</summary>
    public required string Description { get; init; }

    /// <summary>Why this step is safe, or what it costs. The sentence an operator needs before consenting.</summary>
    public required string Rationale { get; init; }

    /// <summary>What executing it would lose. <see cref="LossEstimate.None"/> for every <see cref="RepairClass.Regenerate"/> step.</summary>
    public LossEstimate Loss { get; init; } = LossEstimate.None;

    /// <inheritdoc />
    public override string ToString() => $"{Order}. [{Class}] {Action} — {Description}";
}

/// <summary>
/// The complete enumeration of what a repair would destroy, kept separate from the plan because it can be enormous.
/// </summary>
/// <remarks>
/// A modal that says <i>"47 entities will be affected — OK?"</i> is not consent. The list is the consent.
/// </remarks>
[PublicAPI]
public sealed class LossManifest
{
    /// <summary>Per-step loss estimates, in plan order.</summary>
    public IReadOnlyList<LossEstimate> Entries { get; init; } = [];

    /// <summary>Whether anything at all would be lost.</summary>
    public bool IsEmpty
    {
        get
        {
            for (var i = 0; i < Entries.Count; i++)
            {
                if (!Entries[i].IsNone)
                {
                    return false;
                }
            }

            return true;
        }
    }
}

/// <summary>
/// A reviewable, ordered description of what a repair will do — produced by a read-only pass and consumed by the only
/// mutating one.
/// </summary>
/// <remarks>
/// <para>
/// <b>The plan is a file, not a flag.</b> It records the report it was derived from and the identity of the database it
/// was derived for, and applying it re-scans first and <b>refuses if the database changed</b>. Repairing against a stale
/// diagnosis is how a repair tool damages a healthy database, and a plan that cannot detect staleness is a plan that
/// invites it.
/// </para>
/// <para>
/// The natural product instinct is one command that does the right thing. That is rejected here: the cost of a wrong
/// automatic repair is unbounded and unrecoverable, while the cost of one extra command is thirty seconds.
/// </para>
/// </remarks>
[PublicAPI]
public sealed class RepairPlan
{
    /// <summary>Schema version of the plan's serialized form.</summary>
    public const int PlanVersion = 1;

    /// <summary>Fingerprint of the report this plan was derived from, and of the database state it described.</summary>
    public required string DatabaseFingerprint { get; init; }

    /// <summary>Human-readable identity of the target database.</summary>
    public required string Source { get; init; }

    /// <summary>When the plan was produced.</summary>
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;

    /// <summary>The verdict the plan was built to address.</summary>
    public required IntegrityVerdict Verdict { get; init; }

    /// <summary>The ordered steps.</summary>
    public required IReadOnlyList<RepairStep> Steps { get; init; }

    /// <summary>The full loss enumeration.</summary>
    public required LossManifest Loss { get; init; }

    /// <summary>Findings this plan cannot address at all, with the reason. The honest remainder.</summary>
    public required IReadOnlyList<string> Unaddressed { get; init; }

    /// <summary>Whether any step would destroy something, and therefore whether consent is required.</summary>
    public bool RequiresLossyConsent
    {
        get
        {
            for (var i = 0; i < Steps.Count; i++)
            {
                if (Steps[i].Class == RepairClass.Excise)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Whether the plan would do anything.</summary>
    public bool IsEmpty => Steps.Count == 0;
}

/// <summary>What happened to one step when the plan was applied.</summary>
[PublicAPI]
public enum StepOutcome
{
    /// <summary>The step ran and did what it said.</summary>
    Succeeded = 0,

    /// <summary>The step was not attempted — no consent, or a prior step made it unnecessary.</summary>
    Skipped = 1,

    /// <summary>The step was attempted and failed. Later steps did not run.</summary>
    Failed = 2
}

/// <summary>The receipt for one applied step.</summary>
/// <param name="Step">The step that was attempted.</param>
/// <param name="Outcome">What happened.</param>
/// <param name="Detail">What the step actually did, or why it did not.</param>
/// <param name="ActualLoss">
/// What was really lost, which may be <b>less</b> than estimated — a suspect page can turn out to be orphaned and heal.
/// </param>
[PublicAPI]
public readonly record struct RepairStepResult(RepairStep Step, StepOutcome Outcome, string Detail, LossEstimate ActualLoss);

/// <summary>The result of applying a plan: what was attempted, what worked, and what it really cost.</summary>
[PublicAPI]
public sealed class RepairOutcome
{
    /// <summary>The plan that was applied.</summary>
    public required RepairPlan Plan { get; init; }

    /// <summary>Per-step receipts, in execution order.</summary>
    public required IReadOnlyList<RepairStepResult> Results { get; init; }

    /// <summary>Path of the pre-repair copy, when one was taken.</summary>
    public string BackupPath { get; init; }

    /// <summary>The verification scan run after the repair. <c>null</c> when the repair aborted before it.</summary>
    public IntegrityReport VerificationReport { get; init; }

    /// <summary>Whether every attempted step succeeded.</summary>
    public bool Succeeded
    {
        get
        {
            for (var i = 0; i < Results.Count; i++)
            {
                if (Results[i].Outcome == StepOutcome.Failed)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
