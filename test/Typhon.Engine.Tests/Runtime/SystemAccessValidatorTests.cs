using NUnit.Framework;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine.Tests.Runtime;

[TestFixture]
public class SystemAccessValidatorTests
{
    private ResourceRegistry _registry;

    [SetUp]
    public void SetUp()
    {
        _registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "AccessValidatorTest" });
    }

    [TearDown]
    public void TearDown()
    {
        _registry?.Dispose();
    }

    private struct CompA { public int V; }
    private struct CompB { public int V; }

    // ─── Direct unit tests on the validator ───────────────────────────

    [Test]
    public void AssertWrite_NoContext_PassesSilently()
    {
        // No EnterSystem call has been made on this thread → _current is null → assertion no-ops.
        Assert.DoesNotThrow(() => SystemAccessValidator.AssertWrite<CompA>());
    }

    [Test]
    public void AssertWrite_ContextWithNoDeclarations_PassesSilently()
    {
        // Descriptor exists but is empty (system hasn't migrated to declared access yet).
        var d = new SystemAccessDescriptor();
        SystemAccessValidator.EnterSystem(d, "MigratingSys");
        try
        {
            Assert.DoesNotThrow(() => SystemAccessValidator.AssertWrite<CompA>());
        }
        finally
        {
            SystemAccessValidator.LeaveSystem();
        }
    }

    [Test]
    public void AssertWrite_DeclaredWrites_Passes()
    {
        var d = new SystemAccessDescriptor();
        d.Writes.Add(typeof(CompA));
        SystemAccessValidator.EnterSystem(d, "WriterSys");
        try
        {
            Assert.DoesNotThrow(() => SystemAccessValidator.AssertWrite<CompA>());
        }
        finally
        {
            SystemAccessValidator.LeaveSystem();
        }
    }

    [Test]
    public void AssertWrite_DeclaredSideWrites_Passes()
    {
        var d = new SystemAccessDescriptor();
        d.SideWrites.Add(typeof(CompA));
        SystemAccessValidator.EnterSystem(d, "SideWriterSys");
        try
        {
            Assert.DoesNotThrow(() => SystemAccessValidator.AssertWrite<CompA>());
        }
        finally
        {
            SystemAccessValidator.LeaveSystem();
        }
    }

#if DEBUG
    [Test]
    public void AssertWrite_Undeclared_ThrowsInDebug()
    {
        var d = new SystemAccessDescriptor();
        d.Writes.Add(typeof(CompA)); // CompA declared, CompB NOT declared
        SystemAccessValidator.EnterSystem(d, "DriftySystem");
        try
        {
            var ex = Assert.Throws<InvalidAccessException>(() => SystemAccessValidator.AssertWrite<CompB>());
            Assert.That(ex.SystemName, Is.EqualTo("DriftySystem"));
            Assert.That(ex.UndeclaredType, Is.EqualTo(typeof(CompB)));
            Assert.That(ex.Message, Does.Contain("DriftySystem"));
            Assert.That(ex.Message, Does.Contain("CompB"));
            Assert.That(ex.Message, Does.Contain("Writes<CompB>"));
            Assert.That(ex.ErrorCode, Is.EqualTo(TyphonErrorCode.InvalidSystemAccess));
        }
        finally
        {
            SystemAccessValidator.LeaveSystem();
        }
    }
#endif

    // ─── Push/pop semantics ───────────────────────────────────────────

    [Test]
    public void EnterLeave_NestedScopes_RestorePreviousDescriptor()
    {
        var outerD = new SystemAccessDescriptor();
        outerD.Writes.Add(typeof(CompA));

        var innerD = new SystemAccessDescriptor();
        innerD.Writes.Add(typeof(CompB));

        SystemAccessValidator.EnterSystem(outerD, "Outer");
        try
        {
            // In outer scope: CompA OK, CompB would throw (in DEBUG)
            Assert.DoesNotThrow(() => SystemAccessValidator.AssertWrite<CompA>());

            SystemAccessValidator.EnterSystem(innerD, "Inner");
            try
            {
                // In inner scope: CompB OK
                Assert.DoesNotThrow(() => SystemAccessValidator.AssertWrite<CompB>());
            }
            finally
            {
                SystemAccessValidator.LeaveSystem();
            }

            // Back in outer scope: CompA OK again
            Assert.DoesNotThrow(() => SystemAccessValidator.AssertWrite<CompA>());
        }
        finally
        {
            SystemAccessValidator.LeaveSystem();
        }

        // Outside any scope: anything passes (no context)
        Assert.DoesNotThrow(() => SystemAccessValidator.AssertWrite<CompB>());
    }

    [Test]
    public async Task EnterLeave_PerThread_IsolatedBetweenThreads()
    {
        // Two threads each set their own descriptor; neither sees the other.
        var dA = new SystemAccessDescriptor();
        dA.Writes.Add(typeof(CompA));

        var dB = new SystemAccessDescriptor();
        dB.Writes.Add(typeof(CompB));

        var threadAReady = new ManualResetEventSlim(false);
        var threadBReady = new ManualResetEventSlim(false);
        var bothChecked = new ManualResetEventSlim(false);
        var aSawCompA = false;
        var bSawCompB = false;

        var taskA = Task.Run(() =>
        {
            SystemAccessValidator.EnterSystem(dA, "TA");
            threadAReady.Set();
            threadBReady.Wait();
            try
            {
                aSawCompA = true;
                SystemAccessValidator.AssertWrite<CompA>();
                bothChecked.Wait();
            }
            finally
            {
                SystemAccessValidator.LeaveSystem();
            }
        });

        var taskB = Task.Run(() =>
        {
            SystemAccessValidator.EnterSystem(dB, "TB");
            threadBReady.Set();
            threadAReady.Wait();
            try
            {
                bSawCompB = true;
                SystemAccessValidator.AssertWrite<CompB>();
                bothChecked.Set();
            }
            finally
            {
                SystemAccessValidator.LeaveSystem();
            }
        });

        await Task.WhenAll(taskA, taskB);
        Assert.That(aSawCompA, Is.True);
        Assert.That(bSawCompB, Is.True);
    }

    // ─── Integration: dispatch sets the context ───────────────────────

    private class CapturingSystem : CallbackSystem
    {
        public Action<SystemBuilder> ConfigureAction;
        public Action OnExecute;

        protected override void Configure(SystemBuilder b) => ConfigureAction?.Invoke(b);

        protected override void Execute(TickContext ctx) => OnExecute?.Invoke();
    }

    [Test]
    public void DispatchedSystem_AssertWriteForDeclaredType_Passes()
    {
        var observed = false;
        var sys = new CapturingSystem
        {
            ConfigureAction = b => b.Name("ASys").Phase(Phase.Simulation).Writes<CompA>(),
            OnExecute = () =>
            {
                SystemAccessValidator.AssertWrite<CompA>();
                observed = true;
            },
        };

        using var scheduler = RuntimeSchedule.Create(new RuntimeOptions { BaseTickRate = 1000, WorkerCount = 1 })
            .Add(sys)
            .Build(_registry.Runtime);

        scheduler.Start();
        SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= 1, TimeSpan.FromSeconds(5));
        scheduler.Shutdown();

        Assert.That(observed, Is.True);
    }

#if DEBUG
    [Test]
    public void DispatchedSystem_AssertWriteForUndeclaredType_ThrowsInDebug()
    {
        Exception captured = null;
        var sys = new CapturingSystem
        {
            ConfigureAction = b => b.Name("BSys").Phase(Phase.Simulation).Writes<CompA>(),
            OnExecute = () =>
            {
                try
                {
                    SystemAccessValidator.AssertWrite<CompB>();
                }
                catch (Exception e)
                {
                    captured = e;
                }
            },
        };

        using var scheduler = RuntimeSchedule.Create(new RuntimeOptions { BaseTickRate = 1000, WorkerCount = 1 })
            .Add(sys)
            .Build(_registry.Runtime);

        scheduler.Start();
        SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= 1, TimeSpan.FromSeconds(5));
        scheduler.Shutdown();

        Assert.That(captured, Is.InstanceOf<InvalidAccessException>());
        var iae = (InvalidAccessException)captured;
        Assert.That(iae.SystemName, Is.EqualTo("BSys"));
        Assert.That(iae.UndeclaredType, Is.EqualTo(typeof(CompB)));
    }
#endif

    [Test]
    public void DispatchedSystem_AfterExecution_ContextIsCleared()
    {
        // Dispatcher must restore the previous (null) descriptor after the system finishes.
        var sys = new CapturingSystem
        {
            ConfigureAction = b => b.Name("CleanupCheck").Phase(Phase.Simulation).Writes<CompA>(),
            OnExecute = () => { /* no-op */ },
        };

        using var scheduler = RuntimeSchedule.Create(new RuntimeOptions { BaseTickRate = 1000, WorkerCount = 1 })
            .Add(sys)
            .Build(_registry.Runtime);

        scheduler.Start();
        SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= 1, TimeSpan.FromSeconds(5));
        scheduler.Shutdown();

        // Back on the test thread, no descriptor should be set — assertion silent for any type.
        Assert.DoesNotThrow(() => SystemAccessValidator.AssertWrite<CompA>());
        Assert.DoesNotThrow(() => SystemAccessValidator.AssertWrite<CompB>());
    }
}
