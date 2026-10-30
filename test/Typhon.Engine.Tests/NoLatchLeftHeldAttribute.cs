using NUnit.Framework;
using NUnit.Framework.Interfaces;
using System;

namespace Typhon.Engine.Tests;

/// <summary>
/// Charges a leaked <see cref="OlcLatchHeld.Count"/> to the test that leaked it. The count is thread-static, and a production path restores it on every
/// failure, so a
/// test that leaves it up is the only way it can stay up: the next test on that worker thread then skips a restart it depends on, far from the cause (#1510).
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class NoLatchLeftHeldAttribute : Attribute, ITestAction
{
    [ThreadStatic] private static int s_onEntry;

    /// <inheritdoc/>
    public ActionTargets Targets => ActionTargets.Test;

    /// <inheritdoc/>
    public void BeforeTest(ITest test) => s_onEntry = OlcLatchHeld.Count;

    /// <inheritdoc/>
    public void AfterTest(ITest test)
    {
        var left = OlcLatchHeld.Count - s_onEntry;
        OlcLatchHeld.Count = s_onEntry;
        Assert.That(left, Is.Zero, $"{test.FullName} left {left} latch(es) counted as held on this thread");
    }
}
