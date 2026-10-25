namespace Typhon.Workbench.Backups;

/// <summary>A job body's failure whose category is already known (a child process's report, a Workbench refusal), carried to the job's error as is.</summary>
public sealed class BackupJobException(string category, string message) : Exception(message)
{
    /// <summary><c>refused</c> | <c>store_damaged</c> | <c>failed</c> | <c>plan_changed</c>.</summary>
    public string Category { get; } = category;

    /// <summary>A settle that failed only because its process does not register the application's archetypes (7012): not damage.</summary>
    public bool NeedsSchema { get; init; }
}
