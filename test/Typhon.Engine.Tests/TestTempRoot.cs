using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace Typhon.Engine.Tests;

/// <summary>
/// #1170 — gives this test process a temp root of its own build output, so two checkouts running the same fixture at once stop sharing database files.
/// </summary>
/// <remarks>
/// <para>
/// Fixtures build their paths under <see cref="Path.GetTempPath"/> (<c>Typhon.Tests/&lt;Fixture&gt;</c>, fixed file names, and so on), which is one directory
/// for every worktree, session and test process on the machine: two runs of one fixture deleted, locked or reopened each other's files. Rather than route
/// every site through a helper, this redirects the temp root itself, before any test code runs, so the sites that exist and the ones written later are all
/// covered, and so is engine code that asks for a temp path (the database registry's temp check, the Workbench's live-cache directory).
/// </para>
/// <para>
/// The root is <c>TYPHON_TEST_ROOT</c> when set; otherwise <c>&lt;temp&gt;/typhon-tests-&lt;hash&gt;</c>, keyed by the test assembly's directory, so each
/// worktree and configuration has its own. Processes run from one output directory still share it: <c>shard.py</c>'s shards run disjoint classes, and two
/// sessions sharing a checkout set <c>TYPHON_TEST_ROOT</c>. The variable is written back, so a child process inherits this root instead of nesting another.
/// </para>
/// <para>
/// <see cref="Path.GetTempPath"/> reads the environment on every call (<c>TMP</c>/<c>TEMP</c> through <c>GetTempPath2W</c> on Windows, <c>TMPDIR</c>
/// elsewhere), which is what makes setting it here enough.
/// </para>
/// </remarks>
internal static class TestTempRoot
{
    private const string Variable = "TYPHON_TEST_ROOT";

#pragma warning disable CA2255 // a module initializer in a test assembly is the point: it runs before NUnit touches any fixture, static initializers included
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Apply()
    {
        var root = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrEmpty(root))
        {
            var key = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)), 0, 4).ToLowerInvariant();
            root = Path.Combine(Path.GetTempPath(), $"typhon-tests-{hash}");
        }

        root = Path.GetFullPath(root);
        Directory.CreateDirectory(root);

        Environment.SetEnvironmentVariable(Variable, root);
        Environment.SetEnvironmentVariable("TMP", root);
        Environment.SetEnvironmentVariable("TEMP", root);
        Environment.SetEnvironmentVariable("TMPDIR", root);
    }
}
