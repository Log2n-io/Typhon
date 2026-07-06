namespace Typhon.Shell.Session;

/// <summary>
/// Generates the dynamic prompt string based on session state.
/// </summary>
internal static class PromptBuilder
{
    public static string Build(ShellSession session)
    {
        if (!session.IsOpen)
        {
            return "typhon> ";
        }

        if (!session.HasTransaction)
        {
            return $"typhon:{session.DatabaseName}> ";
        }

        var tsn = session.Transaction.TSN;
        var dirty = session.IsDirty ? "*" : "";
        return $"typhon:{session.DatabaseName}[tx:{tsn}{dirty}]> ";
    }
}
