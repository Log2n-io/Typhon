namespace Typhon.Analyzers;

/// <summary>
/// Help links for Typhon's build diagnostics (#1584). Every shipped ID has an anchor on the public "Build diagnostics" guide page
/// (<c>doc/guide/diagnostics.md</c>), so the link an IDE shows next to a diagnostic lands on its meaning and fix. Linked into every analyzer and
/// generator assembly that reports diagnostics.
/// </summary>
internal static class DiagnosticHelp
{
    private const string Page = "https://doc.typhondb.io/latest/guides/diagnostics.html";

    /// <summary>The help URL of <paramref name="id"/>: the guide page anchored at the lower-cased ID.</summary>
    public static string For(string id) => Page + "#" + id.ToLowerInvariant();
}
