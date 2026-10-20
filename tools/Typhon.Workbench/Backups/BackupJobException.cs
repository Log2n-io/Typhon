namespace Typhon.Workbench.Backups;

/// <summary>A job body's failure whose category is already known (a drill child's report, a Workbench refusal), carried to the job's error as is.</summary>
public sealed class BackupJobException(string category, string message) : Exception(message)
{
    /// <summary><c>refused</c> | <c>store_damaged</c> | <c>failed</c> | <c>plan_changed</c>.</summary>
    public string Category { get; } = category;
}
