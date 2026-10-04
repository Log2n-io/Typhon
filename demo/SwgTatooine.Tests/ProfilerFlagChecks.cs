using System;
using NUnit.Framework;
using Typhon.Engine;

namespace SwgTatooine.Tests;

/// <summary>
/// W1/W2 — the profiler flags parse, are refused rather than ignored, and the startup line names who asked for profiling.
/// </summary>
/// <remarks>
/// <para>
/// <b>What these are for.</b> A Typhon host cannot switch its own profiler on: the engine's master gate is a <c>static readonly bool</c> resolved in a static
/// constructor, and this assembly's generated <c>[ModuleInitializer]</c> loads the engine before <c>Main</c> runs, so the gate is already baked. Two
/// versions of
/// this feature shipped a silent no-op before the third stopped trying to be clever — first by writing the environment variables from <c>Main</c>, where they
/// could not take effect, then by refusing a flag only when the gate was shut, which left <c>--live 9200</c> under an inherited <c>…LIVE=9100</c>
/// running happily
/// on 9100. Each case below is one of those.
/// </para>
/// <para>
/// They are pure parse and formatting checks — no world, no engine, no socket — so they cost microseconds.
/// </para>
/// </remarks>
[TestFixture]
public sealed class ProfilerFlagChecks
{
    /// <summary>The flags are declared in the strict parser, so <c>--help</c> lists them and their values are range-checked.</summary>
    /// <remarks>
    /// This is also what lets <see cref="ProfilerArgs.From"/> have no parser of its own: the values it reports are the ones this parse validated.
    /// </remarks>
    [Test]
    public void TheFlagsAreDeclaredAndRangeChecked()
    {
        var help = CommandLine.Parse(["--help"]).HelpText;
        Assert.Multiple(() =>
        {
            Assert.That(help, Does.Contain("--live"));
            Assert.That(help, Does.Contain("--profiler-bind"));
            Assert.That(help, Does.Contain("--trace"));
            Assert.That(CommandLine.Parse(["--live", "9100"]).ProfilerLivePort, Is.EqualTo(9100));
            Assert.That(CommandLine.Parse(["--trace", "x.typhon-trace"]).ProfilerTracePath, Is.EqualTo("x.typhon-trace"));
            Assert.That(CommandLine.Parse(["--profiler-bind", "10.0.0.4"]).ProfilerBindAddress, Is.EqualTo("10.0.0.4"));
            Assert.That(() => CommandLine.Parse(["--live", "70000"]), Throws.ArgumentException, "a port above the range is refused by name");
            Assert.That(() => CommandLine.Parse(["--live", "-1"]), Throws.ArgumentException);
            Assert.That(() => CommandLine.Parse(["--live"]), Throws.ArgumentException, "a bare --live has no value");
            Assert.That(() => CommandLine.Parse(["--live", "9100", "--live", "9200"]), Throws.ArgumentException, "and a repeated flag is refused");
        });
    }

    /// <summary>A command line with no profiler flag asks for nothing and is not refused.</summary>
    [Test]
    public void NothingAskedIsNothingRefused()
    {
        var r = ProfilerArgs.From(CommandLine.Parse(["--serve", "5080", "--hz", "50"]));
        Assert.Multiple(() =>
        {
            Assert.That(r.AskedByFlag, Is.False);
            Assert.That(ProfilerStatus.MustRefuse(r), Is.False);
            Assert.That(r.LivePort, Is.Zero);
        });
    }

    /// <summary><c>--live 0</c> means off, so it asks for nothing and is not refused.</summary>
    /// <remarks>
    /// Zero is "off" on <c>--serve</c> too, so an operator writing <c>--live 0</c> is turning the thing off, and answering that with a refusal would be
    /// refusing
    /// them for agreeing with the default.
    /// </remarks>
    [Test]
    public void LiveZeroIsOffRatherThanARequest()
    {
        var r = ProfilerArgs.From(CommandLine.Parse(["--live", "0"]));
        Assert.Multiple(() =>
        {
            Assert.That(r.AskedByFlag, Is.False);
            Assert.That(ProfilerStatus.MustRefuse(r), Is.False);
        });
    }

    /// <summary>Each of the three flags is refused on its own, and <c>--trace ""</c> — a value the operator typed — is refused too.</summary>
    /// <remarks>
    /// The empty case is a regression test: an earlier version collapsed an empty value to <see langword="null"/>, so <c>--trace ""</c> set nothing, refused
    /// nothing and exited 0. A flag given and silently dropped is the failure this whole area exists to remove.
    /// </remarks>
    [Test]
    public void EveryFlagIsRefused()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ProfilerStatus.MustRefuse(ProfilerArgs.From(CommandLine.Parse(["--live", "9100"]))), Is.True);
            Assert.That(ProfilerStatus.MustRefuse(ProfilerArgs.From(CommandLine.Parse(["--trace", "run.typhon-trace"]))), Is.True);
            Assert.That(ProfilerStatus.MustRefuse(ProfilerArgs.From(CommandLine.Parse(["--profiler-bind", "10.0.0.4"]))), Is.True);
            Assert.That(ProfilerStatus.MustRefuse(ProfilerArgs.From(CommandLine.Parse(["--trace", string.Empty]))), Is.True, "an empty value was still typed");
        });
    }

    /// <summary>A flag is refused even when profiling is already on — the case an earlier version let through, and the worse of the two silences.</summary>
    /// <remarks>
    /// <b>This is the regression test for the review's highest finding.</b> With <c>TYPHON__PROFILER__LIVE=9100</c> inherited and <c>--live 9200</c> typed, the
    /// server used to listen on 9100, exit 0, and report that the command line had asked for nothing. <see cref="ProfilerStatus.MustRefuse"/> deliberately does
    /// not take the gate's state as an input, and this asserts that: the same request must be refused whatever the environment holds.
    /// </remarks>
    [Test]
    public void AFlagIsRefusedEvenWhenProfilingIsAlreadyOn()
    {
        var asIfInherited = new ProfilerArgs.Requested { LivePort = 9200, InheritedLive = "9100" };
        var asIfLauncher = new ProfilerArgs.Requested { LivePort = 9200, Launcher = "scripts/swg-serve.ps1 -Live" };

        Assert.Multiple(() =>
        {
            Assert.That(ProfilerStatus.MustRefuse(asIfInherited), Is.True, "9200 was asked for and 9100 would have been served");
            Assert.That(ProfilerStatus.MustRefuse(asIfLauncher), Is.True);
            Assert.That(ProfilerArgs.ExplainRefusal(asIfInherited), Does.Contain("already on"), "and the message says why it is refused anyway");
        });
    }

    /// <summary>The refusal names every flag given, with the variable that matches each one.</summary>
    /// <remarks>
    /// A regression test: an earlier version chose the headline flag live-first and the suggested variable trace-first, so <c>--live 9100 --trace x</c>
    /// complained
    /// about <c>--live</c> and then told the operator to set the trace variable. <c>--profiler-bind</c>'s variable had no reader at all.
    /// </remarks>
    [Test]
    public void TheRefusalNamesEveryFlagAndItsVariable()
    {
        var text = ProfilerArgs.ExplainRefusal(ProfilerArgs.From(
            CommandLine.Parse(["--live", "9100", "--trace", "run.typhon-trace", "--profiler-bind", "10.0.0.4"])));

        Assert.Multiple(() =>
        {
            Assert.That(text, Does.Contain("--live").And.Contain("--trace").And.Contain("--profiler-bind"));
            Assert.That(text, Does.Contain(ProfilerArgs.LiveVariable));
            Assert.That(text, Does.Contain(ProfilerArgs.TraceVariable));
            Assert.That(text, Does.Contain(ProfilerArgs.BindVariable), "the bind variable used to be declared and never mentioned");
            Assert.That(text, Does.Contain("9100").And.Contain("run.typhon-trace").And.Contain("10.0.0.4"), "values carry through, so the lines can be pasted");
            Assert.That(text, Does.Contain("swg-serve.ps1"), "and the one-command equivalent is named");
        });
    }

    /// <summary>Every line of the refusal carries the program's name.</summary>
    /// <remarks>
    /// The message is several lines. When only the first was prefixed, the rest started at column 0 and did not read as this program's output at all.
    /// </remarks>
    [Test]
    public void EveryLineOfTheRefusalIsPrefixed()
    {
        var lines = ProfilerArgs.ExplainRefusal(ProfilerArgs.From(CommandLine.Parse(["--live", "9100"])))
            .Replace("\r\n", "\n")
            .Split('\n');

        Assert.That(lines.Length, Is.GreaterThan(3), "it is a multi-line explanation, not a one-liner");
        foreach (var line in lines)
        {
            Assert.That(line, Does.StartWith("SwgTatooine: "));
        }
    }

    /// <summary>The startup line always says something, and says which layer asked.</summary>
    /// <remarks>
    /// <b>Branching on the live gate rather than asserting one message is deliberate.</b> <c>TelemetryConfig.ProfilerActive</c> is baked once per process, so a
    /// test run under <c>TYPHON__PROFILER__*</c> — which is how this suite runs when someone is profiling the suite — would fail an assertion
    /// hard-coding "off".
    /// What must hold either way is that the line is non-empty and names a source. The branch-independent half is covered below, against
    /// <see cref="ProfilerStatus.DescribeSource"/>.
    /// </remarks>
    [Test]
    public void TheStartupLineNamesTheSource()
    {
        var line = ProfilerStatus.Describe(ProfilerArgs.From(CommandLine.Parse([])));
        Assert.That(line, Is.Not.Empty);

        if (TelemetryConfig.ProfilerActive)
        {
            Assert.Multiple(() =>
            {
                Assert.That(line, Does.StartWith("profiling ON"));
                Assert.That(line, Does.Contain("asked for by"), "an operator who did not ask has to be told who did");
            });
        }
        else
        {
            Assert.Multiple(() =>
            {
                Assert.That(line, Does.StartWith("profiling off"));
                Assert.That(line, Does.Contain(ProfilerArgs.LiveVariable), "and how to turn it on");
            });
        }
    }

    /// <summary>The launcher's marker changes the wording from a warning to an attribution, and a stale variable keeps the warning.</summary>
    /// <remarks>
    /// <para>
    /// The warning is the point of the line — a <c>$env:</c> assignment persists for a shell session, and one left over from an earlier attach silently
    /// profiles
    /// every run after it. But telling an operator who just typed <c>-Live</c> to "clear it for an unprofiled run" reads as a bug in the tool.
    /// </para>
    /// <para>
    /// <b>Asserted through <see cref="ProfilerStatus.DescribeSource"/> rather than <c>Describe</c>, and that is why the method exists.</b> <c>Describe</c>
    /// consults the engine's gate, which bakes once per process, so a test of it can only ever exercise whichever branch its own runner baked — in this
    /// suite the
    /// "off" branch, which would have skipped this claim while reporting green.
    /// </para>
    /// </remarks>
    [Test]
    public void TheLauncherIsCreditedAndAStaleVariableIsNot()
    {
        var fromLauncher = ProfilerStatus.DescribeSource(new ProfilerArgs.Requested { InheritedLive = "9100", Launcher = "scripts/swg-serve.ps1 -Live" });
        var inherited = ProfilerStatus.DescribeSource(new ProfilerArgs.Requested { InheritedLive = "9100" });

        Assert.Multiple(() =>
        {
            Assert.That(fromLauncher, Does.Contain("swg-serve.ps1"), "a deliberate -Live is credited to the launcher");
            Assert.That(fromLauncher, Does.Not.Contain("clear it"), "and is not scolded for it");
            Assert.That(inherited, Does.Contain("NOT by this command line"), "a variable nobody mentioned is the footgun, and is named");
            Assert.That(inherited, Does.Contain(ProfilerArgs.LiveVariable));
            Assert.That(inherited, Does.Not.Contain(ProfilerArgs.TraceVariable), "only the variables actually set are listed");
        });
    }

    /// <summary>Every variable that was set is named, so an operator knows all of what to clear.</summary>
    [Test]
    public void EveryInheritedVariableIsNamed()
    {
        var all = ProfilerStatus.DescribeSource(new ProfilerArgs.Requested
        {
            InheritedLive = "9100",
            InheritedTrace = "run.typhon-trace",
            InheritedEnabled = "true",
        });

        Assert.Multiple(() =>
        {
            Assert.That(all, Does.Contain(ProfilerArgs.LiveVariable));
            Assert.That(all, Does.Contain(ProfilerArgs.TraceVariable));
            Assert.That(all, Does.Contain(ProfilerArgs.EnabledVariable));
        });
    }

    /// <summary>The port reported is the one the engine will actually listen on, not the text of the variable.</summary>
    /// <remarks>
    /// <c>ProfilerLaunchConfig.FromConfiguration</c> falls back to 9100 for a value it cannot parse, so <c>TYPHON__PROFILER__LIVE=yes</c> listens on
    /// 9100 while a
    /// banner echoing the variable printed "live on port yes" — wrong in the one number needed to attach the Workbench.
    /// </remarks>
    [Test]
    public void TheEffectivePortFollowsTheEnginesOwnRule()
    {
        Assert.Multiple(() =>
        {
            Assert.That(new ProfilerArgs.Requested { InheritedLive = "9100" }.EffectiveLivePort, Is.EqualTo(9100));
            Assert.That(new ProfilerArgs.Requested { InheritedLive = "9200" }.EffectiveLivePort, Is.EqualTo(9200));
            Assert.That(new ProfilerArgs.Requested { InheritedLive = "yes" }.EffectiveLivePort, Is.EqualTo(ProfilerArgs.DefaultLivePort),
                "the engine's fallback, not a guess and not the raw text");
            Assert.That(new ProfilerArgs.Requested { InheritedLive = string.Empty }.EffectiveLivePort, Is.EqualTo(ProfilerArgs.DefaultLivePort));
            Assert.That(new ProfilerArgs.Requested().EffectiveLivePort, Is.Zero, "no variable means no port");
        });
    }

    /// <summary>A launcher name from the environment is bounded and stripped of control characters before it reaches a console.</summary>
    /// <remarks>
    /// It is an environment value printed to a terminal, so an escape sequence in it would be interpreted and an unbounded one would scroll the banner
    /// away. The
    /// 60-character cut must not split a UTF-16 surrogate pair, because half a surrogate is not a character.
    /// </remarks>
    [Test]
    public void TheLauncherNameIsSanitisedBeforePrinting()
    {
        var pair = "😀";                              // one astral character, two UTF-16 units
        var splitAt60 = new string('a', 59) + pair + "tail";    // its high half lands on index 59

        Assert.Multiple(() =>
        {
            Assert.That(ProfilerArgs.SanitiseLauncher(null), Is.Null);
            Assert.That(ProfilerArgs.SanitiseLauncher("   "), Is.Null, "whitespace is nothing worth printing");
            Assert.That(ProfilerArgs.SanitiseLauncher("  scripts/swg-serve.ps1  "), Is.EqualTo("scripts/swg-serve.ps1"));
            Assert.That(ProfilerArgs.SanitiseLauncher("a\u001b[31mred"), Is.EqualTo("a launcher"), "an escape sequence never reaches the terminal");
            Assert.That(ProfilerArgs.SanitiseLauncher(new string('x', 200)).Length, Is.LessThanOrEqualTo(60));
            Assert.That(char.IsHighSurrogate(ProfilerArgs.SanitiseLauncher(splitAt60)[^1]), Is.False, "the cut never leaves half a surrogate pair");
        });
    }

    /// <summary>The port clause names the real port, and calls out a variable that is not one.</summary>
    /// <remarks>
    /// Asserted against the pure <see cref="ProfilerStatus.DescribePort"/> rather than <c>Describe</c>, whose branch depends on a gate baked once per process —
    /// in this suite always shut. A version of this check guarded by <c>Assert.Ignore</c> would have asserted nothing while reporting green, which is the dead
    /// branch the review found elsewhere; extracting the clause is the fix rather than the guard.
    /// </remarks>
    [Test]
    public void ThePortClauseNamesTheRealPortAndCallsOutARubbishVariable()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ProfilerStatus.DescribePort(new ProfilerArgs.Requested()), Is.Empty, "no variable, no clause");
            Assert.That(ProfilerStatus.DescribePort(new ProfilerArgs.Requested { InheritedLive = "9200" }), Is.EqualTo(", live on port 9200"));

            var rubbish = ProfilerStatus.DescribePort(new ProfilerArgs.Requested { InheritedLive = "yes" });
            Assert.That(rubbish, Does.Contain($"live on port {ProfilerArgs.DefaultLivePort}"), "the port the engine will really listen on");
            Assert.That(rubbish, Does.Contain("is not a port"), "and the value to go and fix");
            Assert.That(rubbish, Does.Contain("yes"));
        });
    }
}
