using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Typhon.Workbench.Hosting;
using Typhon.Workbench.Middleware;

namespace Typhon.Workbench.Controllers;

/// <summary>
/// Workbench-wide profiler operations that aren't tied to a specific session: the
/// "Open in editor" handoff and the inline source-preview fetch. See
/// claude/design/Profiler/10-profiler-source-attribution.md §5.5 + §5.6.
/// </summary>
[ApiController]
[Route("api/profiler")]
[Tags("Profiler")]
[RequireBootstrapToken]
public sealed class ProfilerSourceController : ControllerBase
{
    private readonly OptionsStore _options;
    private readonly EditorLauncher _launcher;

    public ProfilerSourceController(OptionsStore options, EditorLauncher launcher)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
    }

    /// <summary>
    /// Report the workspace root the server will use to resolve repo-relative paths from trace
    /// manifests. When the user has set <see cref="ProfilerOptions.WorkspaceRoot"/> we return that
    /// verbatim; otherwise we auto-detect by walking up from CWD looking for a <c>.git</c> entry.
    /// The Profiler options form displays this so the user can see what will resolve.
    /// </summary>
    [HttpGet("workspace-root")]
    public ActionResult<WorkspaceRootDto> GetWorkspaceRoot()
    {
        var configured = _options.Get().Profiler.WorkspaceRoot;
        if (!string.IsNullOrEmpty(configured))
        {
            return Ok(new WorkspaceRootDto(Effective: configured, Source: "configured"));
        }
        var detected = AutoDetectRepoRoot();
        if (detected != null)
        {
            return Ok(new WorkspaceRootDto(Effective: detected, Source: "auto-detected"));
        }
        return Ok(new WorkspaceRootDto(Effective: Directory.GetCurrentDirectory(), Source: "cwd-fallback"));
    }

    /// <summary>
    /// Launch the user's configured editor at the given file:line. <paramref name="file"/> is a
    /// repo-relative path from the trace manifest (e.g. "/_/src/.../BTree.cs"). The server joins
    /// it with the configured workspace root and dispatches to <see cref="EditorLauncher"/>.
    /// </summary>
    [HttpPost("open-in-editor")]
    public ActionResult<OpenInEditorResult> OpenInEditor([FromBody] OpenInEditorRequest body)
    {
        if (body == null) return BadRequest(new OpenInEditorResult(false, "Body required", ""));
        if (string.IsNullOrWhiteSpace(body.File))
        {
            return BadRequest(new OpenInEditorResult(false, "File path required", ""));
        }
        if (body.Line <= 0)
        {
            return BadRequest(new OpenInEditorResult(false, "Line must be > 0", ""));
        }

        var opts = _options.Get();
        var absolutePath = ResolveAbsolutePath(body.File, opts.Profiler.WorkspaceRoot);

        var result = _launcher.Launch(opts.Editor, absolutePath, body.Line, body.Column);
        if (!result.Success)
        {
            return Ok(new OpenInEditorResult(false, result.ErrorMessage, result.Hint));
        }
        return Ok(new OpenInEditorResult(true, "", ""));
    }

    /// <summary>
    /// Fetch a window of source lines around <paramref name="line"/> from the file at <paramref name="path"/>.
    /// <paramref name="path"/> is a repo-relative path from the trace manifest. Path-traversal guarded:
    /// the resolved absolute path must remain inside the configured workspace root. <paramref name="context"/>
    /// is the number of lines on each side of <paramref name="line"/> (default 20, capped at 100).
    /// </summary>
    [HttpGet("source")]
    public ActionResult<SourceWindowDto> GetSource([FromQuery] string path, [FromQuery] int line, [FromQuery] int? context)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return BadRequest(new { error = "path query parameter required" });
        }
        if (line <= 0)
        {
            return BadRequest(new { error = "line must be > 0" });
        }
        var ctx = Math.Clamp(context ?? 20, 1, 100);

        var opts = _options.Get();
        var workspaceRoot = string.IsNullOrEmpty(opts.Profiler.WorkspaceRoot)
            ? AutoDetectRepoRoot() ?? Directory.GetCurrentDirectory()
            : opts.Profiler.WorkspaceRoot;

        // #302 system attribution: PDB-resolved paths for user-defined systems (e.g. AntHill) are
        // absolute and live outside the Typhon workspace root. The traversal guard exists to stop
        // a relative path with `..` segments from escaping; an absolute path from the trace manifest
        // is already a deliberate target. This endpoint requires the bootstrap token, so an attacker
        // can't address arbitrary local files via the browser.
        // The `/_/` repo-relative prefix must be checked BEFORE the absolute branch — on Linux a `/_/…` path
        // is itself fully-qualified. A fully-qualified path (drive/UNC on Windows, /-rooted on Linux) is a
        // deliberate absolute target that bypasses the workspace-root traversal guard (#426).
        var isRepoRelative = path.StartsWith("/_/", StringComparison.Ordinal);
        var isAbsolute = !isRepoRelative && Path.IsPathFullyQualified(path);
        var fullPath = Path.GetFullPath(ResolveAbsolutePath(path, workspaceRoot));
        if (!isAbsolute)
        {
            var fullRoot = Path.GetFullPath(workspaceRoot);
            if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest(new { error = "path is outside the workspace root", workspaceRoot = fullRoot });
            }
        }
        if (!System.IO.File.Exists(fullPath))
        {
            return NotFound(new { error = $"File not found: {fullPath}" });
        }

        var allLines = System.IO.File.ReadAllLines(fullPath, Encoding.UTF8);

        // Prefer showing exactly the method block enclosing `line` — Roslyn-detected, so braces inside
        // strings / comments / interpolations never truncate it. Fall back to a fixed ±context window
        // for non-C# files, or a line that sits in no method-like member.
        int startLine;
        int endLine;
        var method = path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            ? FindEnclosingMethodLines(string.Join('\n', allLines), line)
            : null;
        if (method is { } block)
        {
            startLine = Math.Clamp(block.Start, 1, allLines.Length);
            endLine = Math.Clamp(block.End, startLine, allLines.Length);
        }
        else
        {
            startLine = Math.Max(1, line - ctx);
            endLine = Math.Min(allLines.Length, line + ctx);
        }
        var window = new string[endLine - startLine + 1];
        Array.Copy(allLines, startLine - 1, window, 0, window.Length);

        return Ok(new SourceWindowDto(
            File: path,
            Line: line,
            StartLine: startLine,
            EndLine: endLine,
            Lines: window));
    }

    /// <summary>
    /// The 1-based <c>[Start, End]</c> line range of the smallest method-like declaration enclosing
    /// <paramref name="line"/> in the C# <paramref name="source"/> — method / constructor / operator,
    /// local function, property / indexer / event, or accessor. Returns <c>null</c> when the line sits
    /// in no such member (the caller then falls back to a fixed ±context window).
    /// </summary>
    /// <remarks>
    /// Roslyn-based on purpose: brace counting from the method line mis-handles braces inside strings,
    /// char literals, comments and interpolations. <see cref="CSharpSyntaxTree.ParseText(string, CSharpParseOptions, string, Encoding, System.Threading.CancellationToken)"/>
    /// never throws on malformed input — it yields a best-effort tree — so a partially-broken file still
    /// resolves what it can.
    /// </remarks>
    internal static (int Start, int End)? FindEnclosingMethodLines(string source, int line)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var target = line - 1; // Roslyn line positions are 0-based.
        (int Start, int End)? best = null;
        foreach (var node in root.DescendantNodes())
        {
            if (node is not (BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax
                or AccessorDeclarationSyntax or BasePropertyDeclarationSyntax))
            {
                continue;
            }
            var span = node.GetLocation().GetLineSpan();
            var start = span.StartLinePosition.Line;
            var end = span.EndLinePosition.Line;
            if (target < start || target > end)
            {
                continue;
            }
            // Smallest enclosing member wins — a local function inside a method, an accessor in a property.
            if (best is not { } cur || (end - start) < (cur.End - cur.Start))
            {
                best = (start + 1, end + 1);
            }
        }
        return best;
    }

    /// <summary>
    /// Strip the design's "/_/" repo-relative prefix and join with the workspace root. Trim leading
    /// path separators on the relative portion to keep <see cref="Path.Combine"/> well-behaved.
    /// When <paramref name="workspaceRoot"/> is empty, auto-detects the repo root by walking up from
    /// CWD looking for a <c>.git</c> directory; otherwise falls back to CWD itself. The Workbench's
    /// CWD is typically <c>tools/Typhon.Workbench/</c> when launched via <c>dotnet run</c>, which
    /// would resolve <c>/_/src/Typhon.Engine/...</c> to a non-existent path — auto-detect avoids that.
    /// </summary>
    public static string ResolveAbsolutePath(string repoRelative, string workspaceRoot)
    {
        // /_/ prefix from PathMap = repo-relative; strip and join with workspace root. Must run
        // BEFORE the IsPathRooted check — Path.IsPathRooted("/_/...") returns true on Windows.
        if (repoRelative.StartsWith("/_/", StringComparison.Ordinal))
        {
            var relative = repoRelative.Substring(3).TrimStart('/', '\\');
            if (string.IsNullOrEmpty(workspaceRoot))
            {
                workspaceRoot = AutoDetectRepoRoot() ?? Directory.GetCurrentDirectory();
            }
            return Path.GetFullPath(Path.Combine(workspaceRoot, relative));
        }
        // A fully-qualified path is a deliberate absolute target — PDB-resolved system paths (#302) live
        // outside the Typhon workspace root (e.g. AntHill at C:\Dev\github\Typhon\test\AntHill\…).
        // Path.IsPathFullyQualified is OS-agnostic: drive-letter / UNC on Windows, /-rooted on Linux, while a
        // bare-relative path stays relative on both. The old hand-rolled drive-letter check treated a POSIX
        // absolute path as relative and joined it onto the workspace root on Linux (#426).
        if (Path.IsPathFullyQualified(repoRelative))
        {
            return Path.GetFullPath(repoRelative);
        }
        // Bare relative — keep prior behavior.
        var rel = repoRelative.TrimStart('/', '\\');
        if (string.IsNullOrEmpty(workspaceRoot))
        {
            workspaceRoot = AutoDetectRepoRoot() ?? Directory.GetCurrentDirectory();
        }
        return Path.GetFullPath(Path.Combine(workspaceRoot, rel));
    }

    /// <summary>
    /// Walk up from <see cref="Directory.GetCurrentDirectory"/> looking for a directory that
    /// contains a <c>.git</c> entry (file or folder — submodules and worktrees use a file). Returns
    /// the matching directory or <c>null</c> if none is found.
    /// </summary>
    public static string AutoDetectRepoRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            var gitPath = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(gitPath) || System.IO.File.Exists(gitPath))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return null;
    }

    public sealed record OpenInEditorRequest(string File, int Line, int? Column);
    public sealed record OpenInEditorResult(bool Ok, string Error, string Hint);
    public sealed record SourceWindowDto(string File, int Line, int StartLine, int EndLine, string[] Lines);
    public sealed record WorkspaceRootDto(string Effective, string Source);
}
