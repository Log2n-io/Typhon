using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// A retention approved from a preview would now delete a pack the preview did not list (a point landed, a drill passed, time moved past an age limit):
/// nothing was compacted or deleted. Preview again and approve the new plan.
/// </summary>
[PublicAPI]
public sealed class BackupPlanChangedException : BackupException
{
    /// <summary>Creates the failure.</summary>
    /// <param name="storePath">The store directory.</param>
    /// <param name="approved">The fingerprint of the plan that was approved.</param>
    /// <param name="current">The fingerprint of the plan the store makes now.</param>
    public BackupPlanChangedException(string storePath, string approved, string current)
        : base(TyphonErrorCode.BackupPlanChanged,
            $"The retention plan of backup store '{storePath}' changed since it was approved (plan {approved}, now {current}): it would delete a pack "
            + "the approved plan did not list. Nothing was deleted; preview it again.")
    {
        Approved = approved;
        Current = current;
    }

    /// <summary>The fingerprint of the plan that was approved.</summary>
    public string Approved { get; }

    /// <summary>The fingerprint of the plan the store makes now.</summary>
    public string Current { get; }

    /// <inheritdoc />
    public override bool IsTransient => true;
}
