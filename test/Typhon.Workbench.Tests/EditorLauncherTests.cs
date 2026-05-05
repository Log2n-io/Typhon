using System.Linq;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Typhon.Workbench.Hosting;

namespace Typhon.Workbench.Tests;

/// <summary>
/// Argv-shape tests for <see cref="EditorLauncher"/> — exercises the <c>BuildXxxProcessStartInfo</c>
/// helpers without spawning processes. Per-(EditorKind, OS) coverage matches the design's dispatch
/// table (#302 §5.5). Live <c>Process.Start</c> is intentionally NOT exercised here; that's covered
/// by manual QA on Windows during release prep — the tests here pin the wire-shape that the OS
/// shell sees.
/// </summary>
[TestFixture]
public sealed class EditorLauncherTests
{
    [Test]
    public void BuildFileUrl_VsCode_EncodesPathAndAppendsLine()
    {
        var url = EditorLauncher.BuildFileUrl("vscode", @"C:\Dev\repo\src\BTree.cs", 42);
        Assert.That(url, Is.EqualTo("vscode://file/C%3A/Dev/repo/src/BTree.cs:42"));
    }

    [Test]
    public void BuildFileUrl_Cursor_UsesCursorScheme()
    {
        var url = EditorLauncher.BuildFileUrl("cursor", "/home/user/repo/src/BTree.cs", 17);
        Assert.That(url, Does.StartWith("cursor://file/"));
        Assert.That(url, Does.EndWith(":17"));
    }

    [Test]
    public void BuildRiderProcessStartInfos_PassesLineAndPathAsDiscreteArgs()
    {
        var infos = EditorLauncher.BuildRiderProcessStartInfos("/path/to/File.cs", 99).ToArray();
        Assert.That(infos, Is.Not.Empty);
        var first = infos[0];
        Assert.That(first.UseShellExecute, Is.False);
        Assert.That(first.ArgumentList, Is.EqualTo(new[] { "--line", "99", "/path/to/File.cs" }));

        var expectedFirst = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "rider.cmd" : "rider";
        Assert.That(first.FileName, Is.EqualTo(expectedFirst));
    }

    [Test]
    public void BuildVisualStudioProcessStartInfo_UsesEditAndCommandArgs()
    {
        var psi = EditorLauncher.BuildVisualStudioProcessStartInfo(@"C:\src\Foo.cs", 7);
        Assert.That(psi.FileName, Is.EqualTo("devenv.exe"));
        Assert.That(psi.UseShellExecute, Is.False);
        Assert.That(psi.ArgumentList, Is.EqualTo(new[] { "/Edit", @"C:\src\Foo.cs", "/Command", "Edit.GoTo 7" }));
    }

    [Test]
    public void BuildCustomProcessStartInfo_SubstitutesPlaceholdersAsDiscreteArgs()
    {
        var psi = EditorLauncher.BuildCustomProcessStartInfo("nvim-qt --remote +{line} {file}", "/tmp/x.cs", 12, column: null);
        Assert.That(psi, Is.Not.Null);
        Assert.That(psi.FileName, Is.EqualTo("nvim-qt"));
        Assert.That(psi.UseShellExecute, Is.False);
        Assert.That(psi.ArgumentList, Is.EqualTo(new[] { "--remote", "+12", "/tmp/x.cs" }));
    }

    [Test]
    public void BuildCustomProcessStartInfo_EmptyTemplate_ReturnsNull()
    {
        Assert.That(EditorLauncher.BuildCustomProcessStartInfo("", "/x", 1, null), Is.Null);
        Assert.That(EditorLauncher.BuildCustomProcessStartInfo("   ", "/x", 1, null), Is.Null);
    }

    [Test]
    public void Launch_VisualStudioOnNonWindows_ReturnsErrorWithHint()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.Ignore("Windows-only assertion would actually launch devenv.exe.");
        }
        var launcher = new EditorLauncher();
        var result = launcher.Launch(
            new EditorOptions { Kind = EditorKind.VisualStudio, CustomCommand = "" },
            "/tmp/x.cs", 1, null);
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Does.Contain("Visual Studio is not available"));
        Assert.That(result.Hint, Does.Contain("VS Code, Cursor, or Rider"));
    }
}
