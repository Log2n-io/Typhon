using System.Collections.Concurrent;

namespace Typhon.Workbench.Backups;

/// <summary>
/// The retention plans the Workbench served (#1417 WB-8), by store and fingerprint, with the policy each was planned for: the apply runs that policy with
/// the very plan the operator reviewed (<c>ApplyRetention(…, approved)</c> refuses when the store would now delete a pack it did not list), never with one
/// the client sends back or one re-planned at apply time. Kept 1 h; a plan no longer known (a Workbench restart) is a plan to review again.
/// </summary>
public sealed class BackupPlanCache
{
    private static readonly TimeSpan Keep = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<(string Store, string Fingerprint), (BackupRetentionPolicy Policy, BackupRetentionPlan Plan, DateTime Utc)> _plans =
        new();

    public void Add(string store, BackupRetentionPolicy policy, BackupRetentionPlan plan)
    {
        var now = DateTime.UtcNow;
        foreach (var (key, value) in _plans)
        {
            if (now - value.Utc >= Keep)
            {
                _plans.TryRemove(key, out _);
            }
        }

        _plans[(BackupPaths.Key(store), plan.Fingerprint)] = (policy, plan, now);
    }

    /// <summary>The reviewed plan and its policy, or false when the Workbench did not serve it (or served it over an hour ago).</summary>
    public bool TryFind(string store, string fingerprint, out BackupRetentionPolicy policy, out BackupRetentionPlan plan)
    {
        if (fingerprint != null && _plans.TryGetValue((BackupPaths.Key(store), fingerprint), out var entry) && DateTime.UtcNow - entry.Utc < Keep)
        {
            (policy, plan) = (entry.Policy, entry.Plan);
            return true;
        }

        (policy, plan) = (null, null);
        return false;
    }
}
