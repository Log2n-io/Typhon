using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// An operation approved on a plan (a restore's <see cref="BackupRestoreOptions.ApprovedPlan"/>, a retention apply's) found, under its locks, that what it
/// would do now differs from what was reviewed. Nothing was written: plan again, review, and approve the new plan.
/// </summary>
[PublicAPI]
public sealed class BackupPlanChangedException : BackupException
{
    /// <summary>Creates the failure.</summary>
    /// <param name="approved">The fingerprint of the plan that was approved.</param>
    /// <param name="current">The fingerprint of the plan as it stands now.</param>
    /// <param name="what">What changed, in a few words.</param>
    public BackupPlanChangedException(string approved, string current, string what)
        : base(TyphonErrorCode.BackupPlanChanged, $"The approved plan {approved} no longer holds ({what}); the plan is now {current}. Nothing was written.")
    {
        Approved = approved;
        Current = current;
    }

    /// <summary>The fingerprint of the plan that was approved.</summary>
    public string Approved { get; }

    /// <summary>The fingerprint of the plan as it stands now.</summary>
    public string Current { get; }
}
