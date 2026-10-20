using NUnit.Framework;
using Typhon.Workbench.Backups;

namespace Typhon.Workbench.Tests.Backups;

/// <summary>#1417 R10: one path identity for "is this the session's bundle", "is this the store's running job", "is this a restore's target".</summary>
[TestFixture]
public sealed class BackupPathsTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "typhon-wb-paths", "db", "world.typhon");

    [Test]
    public void TrailingSeparators_Quotes_AndRelativeSegments_NameTheSameEntry()
    {
        Assert.That(BackupPaths.Same(Root, Root + Path.DirectorySeparatorChar), Is.True);
        Assert.That(BackupPaths.Same(Root, $"\"{Root}\""), Is.True);
        Assert.That(BackupPaths.Same(Root, Path.Combine(Path.GetDirectoryName(Root)!, "..", "db", "world.typhon")), Is.True);
        Assert.That(BackupPaths.Same(Root, Root + "2"), Is.False);
        Assert.That(BackupPaths.Same(Root, null), Is.False);
        Assert.That(BackupPaths.Key(Root + Path.DirectorySeparatorChar), Is.EqualTo(BackupPaths.Key(Root)));
    }

    [Test]
    public void Case_FollowsTheFileSystem()
    {
        Assert.That(BackupPaths.Same(Root, Root.ToUpperInvariant()), Is.EqualTo(OperatingSystem.IsWindows()));
    }

    [Test]
    [Platform("Win")]
    public void TheExtendedLengthPrefix_IsNotAnotherPath()
    {
        Assert.That(BackupPaths.Same(Root, @"\\?\" + Root), Is.True);
    }
}
