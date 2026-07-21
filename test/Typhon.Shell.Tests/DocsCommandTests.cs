using NUnit.Framework;
using Typhon.Shell.Commands;

namespace Typhon.Shell.Tests;

/// <summary>
/// Verifies <c>typhon docs</c> URL construction (<see cref="DocsCommand.BuildUrl"/>): the site root by default, and a
/// clean join for an optional deep-link page (no doubled separator regardless of a leading slash).
/// </summary>
[TestFixture]
public sealed class DocsCommandTests
{
    [Test]
    public void BuildUrl_NoPath_ReturnsSiteRoot()
    {
        Assert.That(DocsCommand.BuildUrl(null), Is.EqualTo("https://doc.typhondb.io/latest/"));
        Assert.That(DocsCommand.BuildUrl(""), Is.EqualTo("https://doc.typhondb.io/latest/"));
        Assert.That(DocsCommand.BuildUrl("   "), Is.EqualTo("https://doc.typhondb.io/latest/"));
    }

    [Test]
    public void BuildUrl_WithPath_JoinsUnderLatest()
    {
        Assert.That(
            DocsCommand.BuildUrl("guide/getting-started"),
            Is.EqualTo("https://doc.typhondb.io/latest/guide/getting-started"));
    }

    [Test]
    public void BuildUrl_LeadingSlashOrWhitespace_DoesNotDoubleSeparator()
    {
        Assert.That(
            DocsCommand.BuildUrl("/guide/getting-started"),
            Is.EqualTo("https://doc.typhondb.io/latest/guide/getting-started"));
        Assert.That(
            DocsCommand.BuildUrl("  guide  "),
            Is.EqualTo("https://doc.typhondb.io/latest/guide"));
    }
}
