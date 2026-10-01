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
    // ── P-1: the persistence surface ────────────────────────────────────────────────────────────────────────────

    /// <summary>The default is delete-on-start, and the name carries no process id.</summary>
    /// <remarks>
    /// Both halves are the settled answer to the one question WP-3's scoping had to decide (§6 Q1), and both are asserted here rather than left to the two
    /// source items' opposite assumptions. The asymmetry is the reason: a run that wrongly persists measures a world some earlier run left behind and says
    /// nothing about it, while a run that wrongly starts fresh loses a demonstration and is obvious.
    /// </remarks>
    [Test]
    public void PersistenceDefaultsToOff_AndTheDatabaseNameIsBare()
    {
        var c = CommandLine.Parse([]);
        Assert.Multiple(() =>
        {
            Assert.That(c.Persist, Is.False, "delete-on-start is the default");
            Assert.That(c.DatabaseName, Is.EqualTo(SimConfig.DefaultDatabaseName));
            Assert.That(c.DatabaseName, Does.Not.Contain("_"), "the process id used to be suffixed here — that is the 154 GB incident");
        });
    }

    /// <summary>There is no <c>--fresh</c>: two flags with opposite senses is how the two source items came to disagree.</summary>
    [Test]
    public void ThereIsNoFreshFlag()
    {
        var ex = Assert.Throws<ArgumentException>(() => CommandLine.Parse(["--fresh"]));
        Assert.That(ex.Message, Does.Contain("--fresh"), "the message has to name the token it refused");
    }

    /// <summary><c>--persist</c> and <c>--sweep</c> are different intentions and the combination is refused rather than resolved.</summary>
    [Test]
    public void PersistWithSweepIsRefused()
    {
        var ex = Assert.Throws<ArgumentException>(() => CommandLine.Parse(["--persist", "--sweep"]));
        Assert.Multiple(() =>
        {
            Assert.That(ex.Message, Does.Contain("--persist"));
            Assert.That(ex.Message, Does.Contain("--sweep"));
        });
    }

    /// <summary><c>--db-name</c> is a name, not a path: <c>--db-dir</c> is the directory.</summary>
    /// <remarks>
    /// <para>
    /// Without this a name containing a separator would be composed into a path the storage layer cannot open, and the failure would arrive as an I/O error
    /// from inside the engine rather than as a refusal naming the flag.
    /// </para>
    /// <para>
    /// <b>The backslash case has to be written <c>@"a\b"</c>, and both halves of that matter.</b> It was <c>"a\b"</c>, which C# compiles to <c>'a'</c>
    /// followed by U+0008 BACKSPACE rather than a separator — so the case that was supposed to cover the Windows separator covered a control character
    /// instead. It still passed on Windows, where control characters are invalid in a file name, and failed on Linux, where they are not. Fixing the escape
    /// then exposed the real defect behind it: <c>Path.GetInvalidFileNameChars()</c> omits <c>'\\'</c> on Unix, so the validator accepted a Windows-style
    /// path on the platform the demo actually deploys to. Both are fixed; this case now fails if either regresses.
    /// </para>
    /// </remarks>
    [TestCase("worlds/mine")]
    [TestCase(@"a\b")]
    [TestCase("a\b")]
    [TestCase(" ")]
    public void ADatabaseNameThatIsAPathIsRefused(string name)
    {
        var ex = Assert.Throws<ArgumentException>(() => CommandLine.Parse(["--db-name", name]));
        Assert.That(ex.Message, Does.Contain("--db-name"));
    }

    /// <summary><c>--fault-at-tick</c> exists to exercise the crash artefact, so it cannot be combined with a server that must not kill itself.</summary>
    [Test]
    public void FaultAtTickWithServeIsRefused()
    {
        var ex = Assert.Throws<ArgumentException>(() => CommandLine.Parse(["--serve", "8080", "--fault-at-tick", "10"]));
        Assert.That(ex.Message, Does.Contain("--fault-at-tick"));
    }

    /// <summary>The three new flags are in <c>--help</c>, which is generated from the declarations the parse walks.</summary>
    [Test]
    public void TheNewPersistenceFlagsAreDocumented()
    {
        var help = CommandLine.Parse(["--help"]).HelpText;
        Assert.Multiple(() =>
        {
            Assert.That(help, Does.Contain("--persist"));
            Assert.That(help, Does.Contain("--db-name"));
            Assert.That(help, Does.Contain("--fault-at-tick"));

            // The default has to be visible, because --help is the only place a reader learns what a run with no flags does.
            Assert.That(help, Does.Contain(SimConfig.DefaultDatabaseName), "--db-name's default must be printed");
        });
    }

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

    /// <summary>A negative number is a value, not a flag: one dash, not two. It is then refused on its range, which is a different message.</summary>
    [Test]
    public void ANegativeValueIsReadAsAValueAndThenRangeChecked()
    {
        var ex = Assert.Throws<ArgumentException>(() => CommandLine.Parse(["--player-leave", "-1"]));
        Assert.Multiple(() =>
        {
            Assert.That(ex.Message, Does.Contain("--player-leave"));
            Assert.That(ex.Message, Does.Not.Contain("is a flag"), "it was read as a value, so the complaint is about its range");
            Assert.That(ex.Message, Does.Contain("at least"));
        });
    }

    /// <summary>
    /// A well-formed number outside its range is refused, and the range is on the declaration so it cannot be forgotten.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>These ten all parsed and ran before the ranges moved onto the declarations</b>, and <c>--pop -1</c> in particular built an empty world and reported
    /// it as a measurement — which is the exact failure SWG-07 exists to prevent, surviving inside SWG-07. A validation list at the end of the parse had them
    /// missing; a bound written beside the flag cannot be.
    /// </para>
    /// <para>They are <c>[TestCase]</c>s rather than one case with ten asserts, so a regression names the flag it broke.</para>
    /// </remarks>
    /// <param name="flag">The flag.</param>
    /// <param name="value">A value outside its range.</param>
    [TestCase("--pop", "-1")]
    [TestCase("--world", "-5")]
    [TestCase("--cell", "-1")]
    [TestCase("--ticks", "-1")]
    [TestCase("--ticks", "0")]
    [TestCase("--warm", "-1")]
    [TestCase("--workers", "-1")]
    [TestCase("--cache-mib", "0")]
    [TestCase("--shuttle-share", "5")]
    [TestCase("--tightness", "-1")]
    [TestCase("--idle-creatures", "2")]
    [TestCase("--hz", "0")]
    [TestCase("--planets", "0")]
    [TestCase("--max-clients", "-1")]
    [TestCase("--serve", "99999")]
    public void AValueOutsideItsRangeIsRefused(string flag, string value)
    {
        var ex = Assert.Throws<ArgumentException>(() => CommandLine.Parse([flag, value]));
        Assert.Multiple(() =>
        {
            Assert.That(ex.Message, Does.Contain(flag));
            Assert.That(ex.Message, Does.Contain(value));
        });
    }

    /// <summary>
    /// <c>--idle-creatures</c> is refused rather than clamped, which is what every other share does.
    /// </summary>
    /// <remarks>
    /// It used to go through <c>Math.Clamp</c>, so <c>--idle-creatures 2</c> ran at 1 and said nothing — a sixth, silent way to be wrong inside a file whose
    /// thesis is that there are five and all are fatal. The repository's rule for a load-bearing parameter is to refuse at start, never to clamp.
    /// </remarks>
    [Test]
    public void AShareIsRefusedRatherThanClamped()
    {
        Assert.Multiple(() =>
        {
            Assert.Throws<ArgumentException>(() => CommandLine.Parse(["--idle-creatures", "2"]), "above the range");
            Assert.Throws<ArgumentException>(() => CommandLine.Parse(["--idle-creatures", "-0.5"]), "below the range");
            Assert.That(CommandLine.Parse(["--idle-creatures", "0.25"]).IdleCreatureFraction, Is.EqualTo(0.25d), "inside the range it is taken as given");
        });
    }

    /// <summary>
    /// <c>-h</c> asks for help only as the first argument, because leniency is decided before anything is parsed.
    /// </summary>
    /// <remarks>
    /// <b>The case this rules out is <c>--db-dir -h</c>.</b> Help has to be recognised before the parse begins, so it cannot know which tokens are values —
    /// and <c>-h</c> is a legitimate directory name. <c>--help</c> is safe anywhere, because a value beginning with <c>--</c> is refused outright and so no
    /// flag can legitimately be followed by it; <c>-h</c> is safe at index 0 because nothing precedes it.
    /// </remarks>
    [Test]
    public void ShortHelpIsOnlyTheFirstArgument()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CommandLine.Parse(["-h"]).HelpText, Is.Not.Null, "the first argument");
            Assert.That(CommandLine.Parse(["-h", "--pop", "2"]).HelpText, Is.Not.Null, "still the first argument");
            Assert.That(CommandLine.Parse(["--db-dir", "-h"]).HelpText, Is.Null, "a value, not a request for help");
            Assert.That(CommandLine.Parse(["--db-dir", "-h"]).DatabaseDirectory, Is.EqualTo("-h"));
            Assert.That(CommandLine.Parse(["--help"]).HelpText, Does.Contain("-h"), "the alias is listed, or it is a flag nobody can discover");
        });
    }

    /// <summary>
    /// A configuration built directly — by a test, or by anything that is not the parser — has sweep axes, rather than nulls.
    /// </summary>
    /// <remarks>
    /// The three axes were defaulted in the parser only, so `Sweep.Run` over a hand-built configuration dereferenced null inside the matrix loop.
    /// </remarks>
    [Test]
    public void AHandBuiltConfigurationHasSweepAxes()
    {
        var config = new SimConfig();
        Assert.Multiple(() =>
        {
            Assert.That(config.SweepWorlds, Is.Not.Null.And.Not.Empty);
            Assert.That(config.SweepPops, Is.Not.Null.And.Not.Empty);
            Assert.That(config.SweepCells, Is.Not.Null.And.Not.Empty);
        });
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

    /// <summary>
    /// A token far from every declared flag gets no suggestion, which is the half of <c>Nearest</c> a positive case cannot show.
    /// </summary>
    /// <remarks>
    /// A suggester that always suggests is worse than none: it sends the reader to a flag they did not mean, and the nearest of eighty names is never far. This
    /// is the case that keeps the threshold honest.
    /// </remarks>
    [TestCase("--verbose")]
    [TestCase("--nonsense-flag-entirely")]
    public void ATokenFarFromEveryFlagGetsNoSuggestion(string token)
    {
        var ex = Assert.Throws<ArgumentException>(() => CommandLine.Parse([token]));
        Assert.Multiple(() =>
        {
            Assert.That(ex.Message, Does.Contain(token));
            Assert.That(ex.Message, Does.Not.Contain("did you mean"), "a token this far from every flag was given a suggestion");
        });
    }

    /// <summary>A bare word that is not a flag at all is refused, rather than ignored as a stray.</summary>
    [Test]
    public void AStrayPositionalTokenIsRefused()
    {
        var ex = Assert.Throws<ArgumentException>(() => CommandLine.Parse(["report.md"]));
        Assert.That(ex.Message, Does.Contain("report.md"));
    }

    /// <summary><c>--serve 0</c> means "measure", which is the one port value that is not a port.</summary>
    [Test]
    public void ServePortZeroMeasures()
    {
        var config = CommandLine.Parse(["--serve", "0"]);
        Assert.Multiple(() =>
        {
            Assert.That(config.ServePort, Is.Zero);
            Assert.That(config.HelpText, Is.Null, "it is a legitimate line, not a refusal");
        });
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

    /// <summary><c>--help</c> is grouped, so a list of eighty flags is readable.</summary>
    /// <remarks>
    /// <b>The rendered header, not the bare word.</b> <c>Does.Contain("mode")</c> passed with every group header deleted, because <c>--subs-mode</c> contains
    /// "mode" — a vacuous assertion of exactly the kind that makes a green suite mean nothing.
    /// </remarks>
    [Test]
    public void HelpIsGrouped()
    {
        var help = CommandLine.Parse(["--help"]).HelpText;
        Assert.Multiple(() =>
        {
            Assert.That(help, Does.Contain("── mode ──"));
            Assert.That(help, Does.Contain("── sessions and replication ──"));
            Assert.That(help, Does.Contain("── persistence and output ──"));
        });
    }

    /// <summary>
    /// Each valued flag prints its default and, where it has one, its range — the only place a reader can learn either.
    /// </summary>
    /// <remarks>
    /// The bracket is <c>[default]</c> or <c>[default, range]</c>, so the pattern ends at a comma or a bracket rather than assuming which. A reader who cannot
    /// see that <c>--max-clients 0</c> means unlimited will set it to 1 and wonder why the second client is refused.
    /// </remarks>
    [Test]
    public void HelpPrintsDefaultsAndRanges()
    {
        var help = CommandLine.Parse(["--help"]).HelpText;
        Assert.Multiple(() =>
        {
            Assert.That(help, Does.Match(@"--hz\s+<integer>\s+.*\[10[,\]]"), "the baseline tick rate");
            Assert.That(help, Does.Match(@"--max-clients\s+<integer>\s+.*\[0[,\]]"), "0 is unlimited, and a reader has to be able to see that");
            Assert.That(help, Does.Match(@"--pop\s+<number>\s+.*>= 0\.0001"), "a flag with a lower bound prints it");
            Assert.That(help, Does.Match(@"--shuttle-share\s+<number>\s+.*0\.\.1"), "a flag bounded both ways prints both");
        });
    }
}
