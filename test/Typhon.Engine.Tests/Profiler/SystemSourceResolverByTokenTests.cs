using System;
using System.Reflection;
using NUnit.Framework;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// Unit tests for the <see cref="SystemSourceResolver.ResolveByToken"/> entry point added in Phase 2 (#351) — resolving a method by its
/// <c>(module, metadataToken)</c> pair (a CPU sample frame has no live <see cref="MethodInfo"/>), including the IL-offset → covering sequence point path.
/// Also re-checks the existing delegate path as a regression guard on the shared-core extraction.
/// </summary>
[TestFixture]
public sealed class SystemSourceResolverByTokenTests
{
    // A multi-statement target — its sequence points span several lines, so the IL-offset covering-SP selection is observable.
    private static int SampleTargetMethod(int seed)
    {
        var a = seed + 1;
        var b = a * 3;
        var c = b - 7;
        var d = c ^ a;
        return d + b;
    }

    private static MethodInfo TargetMethod() =>
        typeof(SystemSourceResolverByTokenTests).GetMethod(nameof(SampleTargetMethod), BindingFlags.NonPublic | BindingFlags.Static);

    [Test]
    public void ResolveByToken_KnownMethod_ResolvesToSource()
    {
        var mi = TargetMethod();
        var resolved = SystemSourceResolver.ResolveByToken(mi.Module, mi.MetadataToken);
        Assert.That(resolved.HasValue, Is.True, "A test-assembly method (which has a portable PDB) must resolve by token.");
        Assert.That(resolved.Value.FilePath, Does.EndWith("SystemSourceResolverByTokenTests.cs"));
        Assert.That(resolved.Value.Line, Is.GreaterThan(0));
    }

    [Test]
    public void ResolveByToken_WithIlOffset_PicksCoveringSequencePoint()
    {
        var mi = TargetMethod();
        var entry = SystemSourceResolver.ResolveByToken(mi.Module, mi.MetadataToken, -1);
        var atEntry = SystemSourceResolver.ResolveByToken(mi.Module, mi.MetadataToken, 0);
        var atEnd = SystemSourceResolver.ResolveByToken(mi.Module, mi.MetadataToken, int.MaxValue);
        Assert.That(entry.HasValue && atEntry.HasValue && atEnd.HasValue, Is.True);
        // Offset 0 resolves to the first sequence point — the same line as the no-offset entry resolution.
        Assert.That(atEntry.Value.Line, Is.EqualTo(entry.Value.Line));
        // A huge offset lands on the last sequence point — a strictly later line in this multi-statement method.
        Assert.That(atEnd.Value.Line, Is.GreaterThan(entry.Value.Line));
    }

    [Test]
    public void ResolveByToken_BogusToken_ReturnsNull()
    {
        var mi = TargetMethod();
        // 0x06FFFFFF — a MethodDef token whose row id is far past the assembly's method table.
        Assert.That(SystemSourceResolver.ResolveByToken(mi.Module, 0x06FFFFFF), Is.Null);
    }

    [Test]
    public void ResolveByToken_NullModule_ReturnsNull()
    {
        Assert.That(SystemSourceResolver.ResolveByToken(null, 0x06000001), Is.Null);
    }

    [Test]
    public void Resolve_Delegate_StillResolves_AfterCoreExtraction()
    {
        Action del = () => SampleTargetMethod(0);
        var resolved = SystemSourceResolver.Resolve(del);
        Assert.That(resolved.HasValue, Is.True, "The delegate-based Resolve path must keep working after the ResolveCore extraction.");
        Assert.That(resolved.Value.FilePath, Does.EndWith("SystemSourceResolverByTokenTests.cs"));
    }
}
