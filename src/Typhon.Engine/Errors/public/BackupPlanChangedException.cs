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
    /// <param name="storePath">The store the plan was made against.</param>
    /// <param name="approved">The fingerprint of the plan that was approved.</param>
    /// <param name="current">The fingerprint of the plan as it stands now.</param>
    public BackupPlanChangedException(string storePath, string approved, string current)
        : base(TyphonErrorCode.BackupPlanChanged,
            $"The plan approved for the store '{storePath}' ({approved}) no longer holds: it is now {current}. Nothing was written; plan again.")
    {
        Approved = approved;
        Current = current;
    }

    /// <summary>The fingerprint of the plan that was approved.</summary>
    public string Approved { get; }

    /// <summary>The fingerprint of the plan as it stands now.</summary>
    public string Current { get; }
}
