using System;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// A fence phase's chunk refuses to run once the tick fence window has closed — the loud form of a <c>CD-01</c> stale claim.
/// </summary>
/// <remarks>
/// <c>EnterWorker</c> enrols the thread whether or not a fence is running, so <c>EW-01</c>'s detector cannot see a stale fence chunk; the check at the top of
/// <c>FencePhaseExecSystemBase.Execute</c> is what names one. <c>Execute</c> is invoked directly: the only way to reach it with the window closed through the
/// runtime is the scheduler bug CD-01 closed.
/// </remarks>
[TestFixture]
class FenceWindowTripwireTests : TestBase<FenceWindowTripwireTests>
{
    private static readonly MethodInfo Execute =
        typeof(FencePhaseExecSystemBase).GetMethod("Execute", BindingFlags.NonPublic | BindingFlags.Instance, [typeof(TickContext)]);

    [Test]
    public void AFenceChunkRunWithTheWindowClosed_Throws()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var prep = new FencePrepExecSystem(dbe);
        Assert.That(dbe.EpochManager.FenceWindow.IsOpen, Is.False, "precondition: no fence is running");

        var ex = Assert.Throws<TargetInvocationException>(() => Execute.Invoke(prep, [new TickContext { ChunkIndex = 0 }]));
        Assert.That(ex.InnerException, Is.TypeOf<InvalidOperationException>().With.Message.Contains("window closed"));
    }

    /// <summary>The control: the same call inside the window returns normally, so the window is the only thing the throw above depends on.</summary>
    [Test]
    public void AFenceChunkRunInsideTheWindow_DoesNotThrow()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var prep = new FencePrepExecSystem(dbe);
        using var window = dbe.EpochManager.FenceWindow.Open();

        Assert.DoesNotThrow(() => Execute.Invoke(prep, [new TickContext { ChunkIndex = 0 }]));
    }
}
