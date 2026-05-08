using NUnit.Framework;
using System;
using System.Linq;

namespace Typhon.Engine.Tests.Runtime;

[TestFixture]
public class PhaseTests
{
    private ResourceRegistry _registry;

    [SetUp]
    public void SetUp()
    {
        _registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "PhaseTest" });
    }

    [TearDown]
    public void TearDown()
    {
        _registry?.Dispose();
    }

    // ── Test system fixtures ──────────────────────────────────────────

    private class PhasedSystem : CallbackSystem
    {
        private readonly string _name;
        private readonly Phase? _phase;

        public PhasedSystem(string name, Phase? phase)
        {
            _name = name;
            _phase = phase;
        }

        protected override void Configure(SystemBuilder b)
        {
            b.Name(_name);
            if (_phase.HasValue)
            {
                b.Phase(_phase.Value);
            }
        }

        protected override void Execute(TickContext ctx)
        {
        }
    }

    // ── 1. Phase struct equality ──────────────────────────────────────

    [Test]
    public void Phase_Equality_SameNameEqual()
    {
        var a = new Phase("Simulation");
        var b = new Phase("Simulation");

        Assert.That(a == b, Is.True);
        Assert.That(a.Equals(b), Is.True);
        Assert.That(Phase.Simulation == new Phase("Simulation"), Is.True);
    }

    [Test]
    public void Phase_Equality_DifferentNamesNotEqual()
    {
        Assert.That(Phase.Input == Phase.Simulation, Is.False);
        Assert.That(Phase.Input != Phase.Simulation, Is.True);
        Assert.That(Phase.Input.Equals(Phase.Simulation), Is.False);
    }

    // ── 2. Phase struct hash ──────────────────────────────────────────

    [Test]
    public void Phase_HashCode_EqualPhasesShareHash()
    {
        var a = new Phase("Cleanup");
        var b = Phase.Cleanup;

        Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()));
    }

    // ── 3. Phase.ToString ─────────────────────────────────────────────

    [Test]
    public void Phase_ToString_ReturnsName()
    {
        Assert.That(Phase.Input.ToString(), Is.EqualTo("Input"));
        Assert.That(Phase.Simulation.ToString(), Is.EqualTo("Simulation"));
        Assert.That(new Phase("Custom").ToString(), Is.EqualTo("Custom"));
    }

    // ── 4. RuntimeOptions.DefaultPhases ───────────────────────────────

    [Test]
    public void DefaultPhases_HasFourEntriesInOrder()
    {
        var phases = RuntimeOptions.DefaultPhases;

        Assert.That(phases, Has.Length.EqualTo(4));
        Assert.That(phases[0], Is.EqualTo(Phase.Input));
        Assert.That(phases[1], Is.EqualTo(Phase.Simulation));
        Assert.That(phases[2], Is.EqualTo(Phase.Output));
        Assert.That(phases[3], Is.EqualTo(Phase.Cleanup));
    }

    // ── 5. Custom phase insertion ─────────────────────────────────────

    [Test]
    public void CustomPhase_RegisteredAndResolved()
    {
        var ai = new Phase("AI");
        var physics = new Phase("Physics");

        var options = new RuntimeOptions
        {
            BaseTickRate = 1000,
            WorkerCount = 1,
            Phases = [Phase.Input, ai, physics, Phase.Simulation, Phase.Output, Phase.Cleanup],
            DefaultPhase = Phase.Simulation,
        };

        using var scheduler = RuntimeSchedule.Create(options)
            .Add(new PhasedSystem("AISystem", ai))
            .Add(new PhasedSystem("PhysicsSystem", physics))
            .Build(_registry.Runtime);

        var aiSys = scheduler.Systems.First(s => s.Name == "AISystem");
        var physicsSys = scheduler.Systems.First(s => s.Name == "PhysicsSystem");

        Assert.That(aiSys.PhaseIndex, Is.EqualTo(1));
        Assert.That(aiSys.Phase, Is.EqualTo(ai));
        Assert.That(physicsSys.PhaseIndex, Is.EqualTo(2));
        Assert.That(physicsSys.Phase, Is.EqualTo(physics));
    }

    // ── 6. Duplicate phase rejection ──────────────────────────────────

    [Test]
    public void DuplicatePhase_InOptions_ThrowsAtBuild()
    {
        var options = new RuntimeOptions
        {
            Phases = [Phase.Input, Phase.Simulation, Phase.Input],
        };

        var schedule = RuntimeSchedule.Create(options)
            .Add(new PhasedSystem("Sys", Phase.Simulation));

        var ex = Assert.Throws<InvalidOperationException>(() => schedule.Build(_registry.Runtime));
        Assert.That(ex.Message, Does.Contain("Duplicate phase"));
        Assert.That(ex.Message, Does.Contain("Input"));
    }

    // ── 7. Empty phases rejection ─────────────────────────────────────

    [Test]
    public void EmptyPhases_InOptions_ThrowsAtBuild()
    {
        var options = new RuntimeOptions
        {
            Phases = [],
        };

        var schedule = RuntimeSchedule.Create(options)
            .Add(new PhasedSystem("Sys", null));

        var ex = Assert.Throws<InvalidOperationException>(() => schedule.Build(_registry.Runtime));
        Assert.That(ex.Message, Does.Contain("at least one phase"));
    }

    // ── 8. Unknown phase rejection ────────────────────────────────────

    [Test]
    public void UnknownPhase_DeclaredBySystem_ThrowsAtBuild()
    {
        var options = new RuntimeOptions
        {
            Phases = RuntimeOptions.DefaultPhases,
        };

        var schedule = RuntimeSchedule.Create(options)
            .Add(new PhasedSystem("BogusSystem", new Phase("NotInList")));

        var ex = Assert.Throws<InvalidOperationException>(() => schedule.Build(_registry.Runtime));
        Assert.That(ex.Message, Does.Contain("BogusSystem"));
        Assert.That(ex.Message, Does.Contain("NotInList"));
    }

    // ── 9. Undeclared phase resolves to the default (RFC 07 / Unit 5) ─

    [Test]
    public void NoPhaseDeclared_GetsDefaultPhase()
    {
        // Default RuntimeOptions: DefaultPhase = Phase.Simulation (index 1 in DefaultPhases)
        using var scheduler = RuntimeSchedule.Create(new RuntimeOptions { BaseTickRate = 1000, WorkerCount = 1 })
            .Add(new PhasedSystem("NoPhaseSys", null))
            .Build(_registry.Runtime);

        var sys = scheduler.Systems.First(s => s.Name == "NoPhaseSys");
        Assert.That(sys.PhaseIndex, Is.EqualTo(1));
        Assert.That(sys.Phase, Is.EqualTo(Phase.Simulation));
    }

    // ── 10. Default phases round-trip (Cleanup → index 3) ─────────────

    [Test]
    public void DefaultPhases_DeclaredCleanup_ResolvesToIndex3()
    {
        using var scheduler = RuntimeSchedule.Create(new RuntimeOptions { BaseTickRate = 1000, WorkerCount = 1 })
            .Add(new PhasedSystem("CleanupSys", Phase.Cleanup))
            .Build(_registry.Runtime);

        var sys = scheduler.Systems.First(s => s.Name == "CleanupSys");
        Assert.That(sys.PhaseIndex, Is.EqualTo(3));
        Assert.That(sys.Phase, Is.EqualTo(Phase.Cleanup));
    }

    // ── 11. Custom default phase applies to undeclared systems ────────

    [Test]
    public void CustomDefaultPhase_AppliesToUndeclaredSystems()
    {
        var options = new RuntimeOptions
        {
            BaseTickRate = 1000,
            WorkerCount = 1,
            DefaultPhase = Phase.Output,
        };

        using var scheduler = RuntimeSchedule.Create(options)
            .Add(new PhasedSystem("UndeclaredSys", null))
            .Build(_registry.Runtime);

        var sys = scheduler.Systems.First(s => s.Name == "UndeclaredSys");
        Assert.That(sys.Phase, Is.EqualTo(Phase.Output));
        Assert.That(sys.PhaseIndex, Is.EqualTo(2));
    }

    // ── 12. Default phase missing from Phases throws at Build ─────────

    [Test]
    public void DefaultPhase_NotInPhasesList_ThrowsAtBuild()
    {
        var custom = new Phase("NotShipped");
        var options = new RuntimeOptions
        {
            BaseTickRate = 1000,
            WorkerCount = 1,
            DefaultPhase = custom, // not in DefaultPhases
        };

        var schedule = RuntimeSchedule.Create(options)
            .Add(new PhasedSystem("Sys", null));

        var ex = Assert.Throws<InvalidOperationException>(() => schedule.Build(_registry.Runtime));
        Assert.That(ex.Message, Does.Contain("DefaultPhase"));
        Assert.That(ex.Message, Does.Contain("NotShipped"));
    }

    // ── 13. Mixed declared + undeclared cohabit cleanly ──────────────

    [Test]
    public void MixedDeclared_And_Undeclared_BothLandSomewhere()
    {
        using var scheduler = RuntimeSchedule.Create(new RuntimeOptions { BaseTickRate = 1000, WorkerCount = 1 })
            .Add(new PhasedSystem("Declared", Phase.Cleanup))
            .Add(new PhasedSystem("Undeclared", null))
            .Build(_registry.Runtime);

        var declared = scheduler.Systems.First(s => s.Name == "Declared");
        var undeclared = scheduler.Systems.First(s => s.Name == "Undeclared");

        Assert.That(declared.PhaseIndex, Is.EqualTo(3));   // Cleanup
        Assert.That(undeclared.PhaseIndex, Is.EqualTo(1)); // Default = Simulation
        // Post 2026-05-07 cross-phase amendment: phases are logical ordering contracts, not
        // runtime barriers. Two systems with no declared access conflict get no derived edge
        // even when they sit in different phases — the runtime can dispatch them concurrently.
        // The PhaseIndex assignments above are the correct half of "both land somewhere"; the
        // pre-amendment expectation of an unconditional cross-phase edge was an artefact of the
        // old all-to-all chaining (see runtime-scheduling.md §ED-05).
        Assert.That(undeclared.Successors, Does.Not.Contain(declared.Index));
    }
}
