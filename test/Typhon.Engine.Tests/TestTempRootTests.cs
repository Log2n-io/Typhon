using System;
using System.IO;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>#1170 — the temp root every fixture builds its paths under is this process's own, not the machine's.</summary>
[TestFixture]
class TestTempRootTests
{
    [Test]
    public void TheTempPathIsTheProcessTestRoot()
    {
        var root = Environment.GetEnvironmentVariable("TYPHON_TEST_ROOT");
        var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));

        Assert.Multiple(() =>
        {
            Assert.That(root, Is.Not.Null.And.Not.Empty, "the module initializer publishes the root, so a child process inherits it");
            Assert.That(temp, Is.EqualTo(Path.TrimEndingDirectorySeparator(root)).IgnoreCase, "Path.GetTempPath answers the test root");
            Assert.That(Directory.Exists(temp), Is.True);
        });
    }
}
