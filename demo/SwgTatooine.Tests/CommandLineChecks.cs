using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace SwgTatooine.Tests;

/// <summary>
/// SWG-07 — the command line refuses what it does not understand, and <c>--help</c> is generated from what it reads.
/// </summary>
/// <remarks>
/// <para>
/// <b>What these are for.</b> The parser they cover used to fall back to the default for an absent flag, a misspelt flag and an unparseable value alike, so
/// a typo in a sweep script produced a complete and plausible report of the wrong configuration. This repository's measurement rules require interleaved
/// same-binary A/B pairs; a silently-ignored <c>--target-ratio</c> made both arms the same run. Every case below is one way that happened.
/// </para>
/// <para>
/// They are pure parse checks — no world, no engine, no database — so they cost microseconds and can be the first thing to run.
/// </para>
/// </remarks>
[TestFixture]
public sealed class CommandLineChecks
{
    /// <summary>A flag nobody declared is named, not ignored.</summary>
    [Test]
    public void AnUnknownFlagIsRefusedAndNamed()
    {
        var ex = Assert.Throws<ArgumentException>(() => CommandLine.Parse(["--nosuchflag"]));
        Assert.That(ex.Message, Does.Contain("--nosuchflag"), "the message has to name the token, or the operator has to diff the parser");
    }

    /// <summary>
    /// A near miss names the flag it is near. Not decoration: <c>--target_ratio</c> for <c>--target-ratio</c> is the shape the typo actually took.
    /// </summary>
    [Test]
    public void AMisspeltFlagSuggestsTheRealOne()
    {
        var ex = Assert.Throws<ArgumentException>(() => CommandLine.Parse(["--target_ratio", "0.3"]));
        Assert.That(ex.Message, Does.Contain("--target-ratio"));
    }

    /// <summary>A value that will not parse is refused, rather than becoming the default.</summary>
    [Test]
    public void AMalformedValueIsRefusedAndNamed()
    {
        var ex = Assert.Throws<ArgumentException>(() => CommandLine.Parse(["--hz", "banana"]));
        Assert.Multiple(() =>
        {
            Assert.That(ex.Message, Does.Contain("--hz"));
            Assert.That(ex.Message, Does.Contain("banana"));
        });
    }

    /// <summary>A flag at the end of the line with nothing after it is refused.</summary>
    [Test]
    public void AFlagWithNoValueIsRefused()
    {
        var ex = Assert.Throws<ArgumentException>(() => CommandLine.Parse(["--hz"]));
        Assert.That(ex.Message, Does.Contain("--hz"));
    }

    /// <summary>
    /// A flag followed by another flag is a forgotten value. It used to consume the next flag, so one omission lost two settings.
    /// </summary>
    [Test]
    public void AValueThatIsItselfAFlagIsRefused()
    {
        var ex = Assert.Throws<ArgumentException>(() => CommandLine.Parse(["--hz", "--pop", "2"]));
        Assert.Multiple(() =>
        {
            Assert.That(ex.Message, Does.Contain("--hz"));
            Assert.That(ex.Message, Does.Contain("--pop"));
        });
    }

    /// <summary>
    /// The same flag twice is refused, because <c>IndexOf</c> took the first: appending a corrected value ran the value it was meant to replace.
    /// </summary>
    [Test]
    public void ARepeatedFlagIsRefused()
    {
        var ex = Assert.Throws<ArgumentException>(() => CommandLine.Parse(["--hz", "10", "--hz", "50"]));
        Assert.That(ex.Message, Does.Contain("--hz"));
    }

    /// <summary>A negative number is a value, not a flag: one dash, not two.</summary>
    [Test]
    public void ANegativeValueIsStillAValue()
    {
        var config = CommandLine.Parse(["--player-leave", "-1"]);
        Assert.That(config.PlayerLeaveM, Is.EqualTo(-1d));
    }

    /// <summary>A choice outside its set is refused and the set is printed.</summary>
    [Test]
    public void AnUnknownChoiceIsRefusedWithItsAlternatives()
    {
        var ex = Assert.Throws<ArgumentException>(() => CommandLine.Parse(["--awareness-api", "telepathy"]));
        Assert.Multiple(() =>
        {
            Assert.That(ex.Message, Does.Contain("telepathy"));
            Assert.That(ex.Message, Does.Contain("movenext"));
        });
    }

    /// <summary>One bad entry refuses the whole list, rather than silently sweeping the default matrix.</summary>
    [Test]
    public void AMalformedSweepAxisIsRefused()
    {
        var ex = Assert.Throws<ArgumentException>(() => CommandLine.Parse(["--sweep-cells", "64,x,256"]));
        Assert.That(ex.Message, Does.Contain("--sweep-cells"));
    }

    /// <summary>A well-formed line parses, and the values reach the configuration.</summary>
    [Test]
    public void AWellFormedLineParses()
    {
        var config = CommandLine.Parse(["--hz", "20", "--pop", "2.5", "--serve", "9001", "--max-clients", "4", "--interiors"]);
        Assert.Multiple(() =>
        {
            Assert.That(config.TickRateHz, Is.EqualTo(20));
            Assert.That(config.PopulationScale, Is.EqualTo(2.5f));
            Assert.That(config.ServePort, Is.EqualTo(9001));
            Assert.That(config.MaxClients, Is.EqualTo(4));
            Assert.That(config.Interiors, Is.True);
            Assert.That(config.HelpText, Is.Null, "a line that is not asking for help gets a configuration");
        });
    }

    /// <summary>An empty line is the default configuration, not an error.</summary>
    [Test]
    public void AnEmptyLineIsTheDefaultConfiguration()
    {
        var config = CommandLine.Parse([]);
        Assert.Multiple(() =>
        {
            Assert.That(config.TickRateHz, Is.EqualTo(10), "10 Hz is the baseline every published number is quoted at");
            Assert.That(config.ServePort, Is.Zero);
            Assert.That(config.HelpText, Is.Null);
        });
    }

    /// <summary>
    /// The three sampling probes report at the end of a measured run, so they cannot be asked for in a run with no end.
    /// </summary>
    /// <param name="probe">The probe flag under test.</param>
    [TestCase("--probe")]
    [TestCase("--work-probe")]
    [TestCase("--chunk-stats")]
    public void AProbeCannotBeCombinedWithServe(string probe)
    {
        var ex = Assert.Throws<ArgumentException>(() => CommandLine.Parse(["--serve", "8080", probe]));
        Assert.That(ex.Message, Does.Contain("--serve"));
    }

    /// <summary><c>--unpaced</c> disables the overload response, which is wrong for a server clients connect to.</summary>
    [Test]
    public void UnpacedCannotBeCombinedWithServe()
        => Assert.Throws<ArgumentException>(() => CommandLine.Parse(["--serve", "8080", "--unpaced"]));

    /// <summary>Two modes are not a mode.</summary>
    [Test]
    public void SweepCannotBeCombinedWithServe()
        => Assert.Throws<ArgumentException>(() => CommandLine.Parse(["--serve", "8080", "--sweep"]));

    /// <summary>A port outside the range is refused rather than quietly becoming 8080, which is what it used to do.</summary>
    [Test]
    public void AnOutOfRangePortIsRefused()
    {
        var ex = Assert.Throws<ArgumentException>(() => CommandLine.Parse(["--serve", "99999"]));
        Assert.That(ex.Message, Does.Contain("--serve"));
    }

    // ── --help ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary><c>--help</c> answers with the list rather than with a configuration.</summary>
    [Test]
    public void HelpAnswersWithTheFlagList()
    {
        var config = CommandLine.Parse(["--help"]);
        Assert.Multiple(() =>
        {
            Assert.That(config.HelpText, Is.Not.Null.And.Not.Empty);
            Assert.That(config.HelpText, Does.Contain("--hz"));
            Assert.That(config.HelpText, Does.Contain("--serve"));
        });
    }

    /// <summary>
    /// Asking for help on a line that is otherwise wrong still gets help. That is the line on which help is most wanted.
    /// </summary>
    [Test]
    public void HelpSurvivesAnOtherwiseWrongLine()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CommandLine.Parse(["--nosuchflag", "--help"]).HelpText, Is.Not.Null, "an unknown flag");
            Assert.That(CommandLine.Parse(["--hz", "banana", "--help"]).HelpText, Is.Not.Null, "a malformed value");
            Assert.That(CommandLine.Parse(["--help", "--hz"]).HelpText, Is.Not.Null, "a flag with no value");
            Assert.That(CommandLine.Parse(["--hz", "1", "--hz", "2", "--help"]).HelpText, Is.Not.Null, "a repeated flag");
        });
    }

    /// <summary>
    /// Every flag <c>--help</c> lists is a flag the parser accepts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The direction that needs a test.</b> That every accepted flag is listed holds by construction — the list is built from the declarations the parse
    /// walked, so a flag cannot be read without being recorded. The converse does not: a name changed in one place and not the other, or a flag removed from
    /// the reads but left in a hand-written list, would leave <c>--help</c> advertising something the parser refuses. Naming each one it cannot accept is
    /// what makes the failure readable.
    /// </para>
    /// <para>
    /// The value handed to each flag is a number, because every valued flag here takes a number, a path or a choice — and a path accepts a number while a
    /// choice is checked by <see cref="AnUnknownChoiceIsRefusedWithItsAlternatives"/>. A choice would refuse "1", so its line is recognised by the
    /// <c>|</c> in its printed kind and given its first alternative instead.
    /// </para>
    /// </remarks>
    [Test]
    public void EveryFlagHelpListsIsAFlagTheParserAccepts()
    {
        var help = CommandLine.Parse(["--help"]).HelpText;
        var rejected = new List<string>();
        var seen = 0;
        foreach (var line in help.Split('\n'))
        {
            var match = Regex.Match(line, @"^\s{2}(--[a-z0-9-]+)(?:\s+(\S+))?\s{2,}");
            if (!match.Success)
            {
                continue;
            }

            var flag = match.Groups[1].Value;
            if (flag == "--help")
            {
                continue;
            }

            seen++;
            var kind = match.Groups[2].Value;
            string[] args = kind.Length == 0 ? [flag]
                : kind.Contains('|') ? [flag, kind.Split('|')[0]]
                : [flag, "1"];

            try
            {
                CommandLine.Parse(args);
            }
            catch (ArgumentException ex) when (ex.Message.Contains("unknown argument"))
            {
                rejected.Add($"{flag} ({ex.Message})");
            }
            catch (ArgumentException)
            {
                // A value of 1 that a range check refuses, or a pair of settings that contradict, is this flag being READ — which is all this case claims.
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(rejected, Is.Empty, "--help lists flags the parser does not know");

            // Without this the case is satisfiable by a regex that matches nothing: an empty walk leaves `rejected` empty and reports success having checked
            // no flag at all. That is the failure mode `lint-orphaned-doc-comments.py` was written after — a gate that passed having scanned zero files.
            Assert.That(seen, Is.GreaterThan(50), "the help format stopped matching, so this case checked nothing");
        });
    }

    /// <summary><c>--help</c> is grouped, so a list of seventy flags is readable.</summary>
    [Test]
    public void HelpIsGrouped()
    {
        var help = CommandLine.Parse(["--help"]).HelpText;
        Assert.Multiple(() =>
        {
            Assert.That(help, Does.Contain("mode"));
            Assert.That(help, Does.Contain("sessions and replication"));
            Assert.That(help, Does.Contain("persistence and output"));
        });
    }

    /// <summary>Each valued flag prints its default, which is the only place a reader can learn what a run with no flags does.</summary>
    [Test]
    public void HelpPrintsDefaults()
    {
        var help = CommandLine.Parse(["--help"]).HelpText;
        Assert.Multiple(() =>
        {
            Assert.That(help, Does.Match(@"--hz\s+<integer>\s+.*\[10\]"), "the baseline tick rate");
            Assert.That(help, Does.Match(@"--max-clients\s+<integer>\s+.*\[0\]"), "0 is unlimited, and a reader has to be able to see that");
        });
    }
}
