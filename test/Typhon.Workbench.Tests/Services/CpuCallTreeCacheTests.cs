using NUnit.Framework;
using Typhon.Workbench.Dtos.Profiler;
using Typhon.Workbench.Services;

namespace Typhon.Workbench.Tests.Services;

/// <summary>
/// Unit tests for <see cref="CpuCallTreeCache"/> (#351 — H2) — the per-session memo that keeps an identical or repeated
/// call-tree scope request from being re-folded.
/// </summary>
[TestFixture]
public sealed class CpuCallTreeCacheTests
{
    [Test]
    public void TryGet_MissThenHitAfterPut()
    {
        var cache = new CpuCallTreeCache();
        var req = new CallTreeRequestDto(null, null, null, "wall-clock");
        var key = CpuCallTreeCache.KeyFor(req);

        Assert.That(cache.TryGet(key, out _), Is.False, "an unseen scope is a miss");

        var tree = CallTreeResponseDto.Empty;
        cache.Put(key, tree);

        Assert.That(cache.TryGet(key, out var got), Is.True, "the same scope is a hit after Put");
        Assert.That(got, Is.SameAs(tree), "the cached fold instance is returned, not a re-fold");
    }

    [Test]
    public void KeyFor_DistinctScopeAxes_ProduceDistinctKeys()
    {
        var bySystem1 = new CallTreeRequestDto(null, null, null, "wall-clock", SystemIndex: 1);
        var bySystem2 = new CallTreeRequestDto(null, null, null, "wall-clock", SystemIndex: 2);
        var onCpu = new CallTreeRequestDto(null, null, null, "on-cpu", SystemIndex: 1);
        var withRoot = new CallTreeRequestDto(null, null, 7, "wall-clock", SystemIndex: 1);

        Assert.That(CpuCallTreeCache.KeyFor(bySystem1), Is.Not.EqualTo(CpuCallTreeCache.KeyFor(bySystem2)), "system index is part of the key");
        Assert.That(CpuCallTreeCache.KeyFor(bySystem1), Is.Not.EqualTo(CpuCallTreeCache.KeyFor(onCpu)), "view mode is part of the key");
        Assert.That(CpuCallTreeCache.KeyFor(bySystem1), Is.Not.EqualTo(CpuCallTreeCache.KeyFor(withRoot)), "frame root is part of the key");
    }

    [Test]
    public void KeyFor_SameScope_IsStable()
    {
        var a = new CallTreeRequestDto(10, 20, 3, "on-cpu", SystemIndex: 1, Phase: "Render", SpanKind: 5);
        var b = new CallTreeRequestDto(10, 20, 3, "on-cpu", SystemIndex: 1, Phase: "Render", SpanKind: 5);
        Assert.That(CpuCallTreeCache.KeyFor(a), Is.EqualTo(CpuCallTreeCache.KeyFor(b)));
    }
}
