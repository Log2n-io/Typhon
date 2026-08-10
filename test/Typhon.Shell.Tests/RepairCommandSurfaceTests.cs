using System;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Spectre.Console.Cli;
using Typhon.Shell.Commands;

namespace Typhon.Shell.Tests;

/// <summary>
/// Guards the shape of <c>typhon repair</c>'s command line, which carries a decision that is otherwise only prose.
/// </summary>
/// <remarks>
/// IR-01 says repair refuses any on-disk format revision but the build's own, <b>with no override</b>. That half of the
/// rule cannot be verified by exercising the engine — an escape hatch is proven absent only by looking at the surface that
/// would expose it. A comment saying "deliberately no --force" is a comment; this is the thing that fails when someone
/// adds one, which is the point at which the decision deserves to be re-argued rather than quietly reversed.
/// </remarks>
[TestFixture]
public sealed class RepairCommandSurfaceTests
{
    /// <summary>
    /// Substrings that would name an override. Deliberately broad: the risk is not that someone types <c>--force</c>
    /// specifically, it is that the capability arrives under whatever word felt reasonable that afternoon.
    /// </summary>
    private static readonly string[] OverrideWords =
        ["force", "ignore-version", "ignore-revision", "any-version", "any-revision", "skip-version", "skip-revision",
         "unsafe", "no-version-check", "override"];

    [Test]
    public void RepairExposesNoOverrideForTheFormatRevisionGate()
    {
        var offenders = OptionNames(typeof(RepairCommand.Settings))
            .Where(name => OverrideWords.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        Assert.That(offenders, Is.Empty,
            "IR-01: `typhon repair` must expose no way to write to a database whose on-disk format revision this build "
            + "does not speak. An option like this exists only to let someone corrupt a database the tool cannot "
            + "interpret, on a day they are already recovering from something else. Found: "
            + string.Join(", ", offenders)
            + ".\nIf the flag is genuinely wanted, change rules/durability.md IR-01 first — the rule is the decision, and "
            + "this test is only its enforcement.");
    }

    [Test]
    public void TheOptionsThatDoExistAreStillThere()
    {
        // The guard above is a negative assertion, and a negative assertion over a surface that has silently become empty
        // passes for the wrong reason — a renamed settings class, a moved command, a reflection call that quietly returns
        // nothing. Pin the options that must exist so the absence proved above is an absence from a real list.
        var names = OptionNames(typeof(RepairCommand.Settings));

        Assert.That(names, Is.SupersetOf(["--plan", "--apply", "--allow-loss", "--dry-run"]),
            "the repair command's own options went missing; the no-override check above is reading nothing. Found: "
            + string.Join(", ", names));
    }

    /// <summary>Every long and short option name declared by a Spectre settings type.</summary>
    private static string[] OptionNames(Type settings)
        => settings.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.GetCustomAttribute<CommandOptionAttribute>())
            .Where(a => a != null)
            .SelectMany(a => a.LongNames.Select(n => "--" + n).Concat(a.ShortNames.Select(n => "-" + n)))
            .ToArray();

    [Test]
    public void NoDescriptionAdvertisesAnEscapeHatch()
    {
        // A flag that does something else but is DESCRIBED as bypassing the version check is the same reversal wearing a
        // different name, and it is what an operator would actually read and reach for.
        var descriptions = typeof(RepairCommand.Settings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description)
            .Where(d => !string.IsNullOrEmpty(d))
            .ToArray();

        foreach (var d in descriptions)
        {
            Assert.That(d, Does.Not.Contain("version check").IgnoreCase);
            Assert.That(d, Does.Not.Contain("format revision").IgnoreCase);
        }
    }
}
