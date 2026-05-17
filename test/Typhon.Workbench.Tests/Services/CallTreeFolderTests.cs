using System.Linq;
using NUnit.Framework;
using Typhon.Profiler;
using Typhon.Workbench.Dtos.Profiler;
using Typhon.Workbench.Services;

namespace Typhon.Workbench.Tests.Services;

/// <summary>
/// Unit tests for <see cref="CallTreeFolder"/> (#351 Phase 4 + Phase 5) — the server-side call-tree fold. Drives the fold
/// with synthetic sample / interned-stack sets and an explicit QPC interval set, no on-disk traces, so self/total
/// counting, view-mode filtering, frame-root re-rooting, single- and multi-window scope filtering, the category
/// breakdown and the flat (depth-independent) wire shape are each verified in isolation.
/// </summary>
[TestFixture]
public sealed class CallTreeFolderTests
{
    // Stacks are leaf-first: [leaf, …, root]. Frame 0 = MovementSystem.Execute (a common root),
    // frame 1 / frame 2 = two distinct leaf callees.
    private static readonly ushort[][] Stacks =
    [
        [1, 0], // leaf frame 1 under root frame 0
        [2, 0], // leaf frame 2 under root frame 0
    ];

    // frameId → categoryId: frame 0 → cat 0, frame 1 → cat 1, frame 2 → cat 2.
    private static readonly int[] CategoryByFrameId = [0, 1, 2];

    private static readonly CpuSampleRecord[] Samples =
    [
        new(1000, 0, 0, 0), // Managed, stack 0
        new(2000, 0, 0, 0), // Managed, stack 0
        new(3000, 0, 1, 1), // External, stack 1
    ];

    /// <summary>The "scope to everything" interval set — the Phase-4 default before any scoping.</summary>
    private static (long Start, long End)[] WholeSession => ScopeResolver.WholeSession;

    private static CallTreeRequestDto Request(int? frameRoot = null, string viewMode = "wall-clock")
        => new(null, null, frameRoot, viewMode);

    /// <summary>The synthetic root node — always <c>Nodes[0]</c>.</summary>
    private static CallTreeNodeDto Root(CallTreeResponseDto r) => r.Nodes[0];

    /// <summary>Resolves a node's child-index list to the child nodes themselves.</summary>
    private static CallTreeNodeDto[] ChildrenOf(CallTreeResponseDto r, CallTreeNodeDto n)
        => n.Children.Select(i => r.Nodes[i]).ToArray();

    [Test]
    public void Fold_WallClock_CountsSelfAndTotalPerNode()
    {
        var result = CallTreeFolder.Fold(Samples, Stacks, CategoryByFrameId, WholeSession, Request());

        Assert.That(result.TotalSamples, Is.EqualTo(3));
        Assert.That(result.ManagedSamples, Is.EqualTo(2));
        Assert.That(result.ExternalSamples, Is.EqualTo(1));

        // Synthetic root → one real root frame (0), total 3, no self time.
        Assert.That(Root(result).FrameId, Is.EqualTo(-1));
        Assert.That(Root(result).Children, Has.Length.EqualTo(1));
        var frame0 = ChildrenOf(result, Root(result))[0];
        Assert.That(frame0.FrameId, Is.EqualTo(0));
        Assert.That(frame0.TotalSamples, Is.EqualTo(3));
        Assert.That(frame0.SelfSamples, Is.EqualTo(0));

        // Children sorted hottest-first: frame 1 (2 samples) before frame 2 (1 sample).
        var frame0Kids = ChildrenOf(result, frame0);
        Assert.That(frame0Kids.Select(c => c.FrameId), Is.EqualTo(new[] { 1, 2 }));
        Assert.That(frame0Kids[0].TotalSamples, Is.EqualTo(2));
        Assert.That(frame0Kids[0].SelfSamples, Is.EqualTo(2));
        Assert.That(frame0Kids[1].TotalSamples, Is.EqualTo(1));
        Assert.That(frame0Kids[1].SelfSamples, Is.EqualTo(1));
    }

    [Test]
    public void Fold_CategoryBreakdown_AttributesLeafFrame()
    {
        var result = CallTreeFolder.Fold(Samples, Stacks, CategoryByFrameId, WholeSession, Request());

        // Self-time semantic: leaf frame 1 → cat 1 (×2), leaf frame 2 → cat 2 (×1). No cat 0 (frame 0 never a leaf).
        var byCategory = result.CategoryBreakdown.ToDictionary(s => s.CategoryId, s => s.SelfSamples);
        Assert.That(byCategory, Has.Count.EqualTo(2));
        Assert.That(byCategory[1], Is.EqualTo(2));
        Assert.That(byCategory[2], Is.EqualTo(1));
    }

    [Test]
    public void Fold_OnCpu_DropsExternalSamples()
    {
        var result = CallTreeFolder.Fold(Samples, Stacks, CategoryByFrameId, WholeSession, Request(viewMode: "on-cpu"));

        Assert.That(result.TotalSamples, Is.EqualTo(2));
        Assert.That(result.ExternalSamples, Is.EqualTo(0));
        // Only stack 0 (frame 1 leaf) survives — frame 2 came only from the External sample.
        var frame0 = ChildrenOf(result, Root(result))[0];
        Assert.That(frame0.Children, Has.Length.EqualTo(1));
        Assert.That(result.Nodes[frame0.Children[0]].FrameId, Is.EqualTo(1));
    }

    [Test]
    public void Fold_FrameRoot_ReRootsAtThatFrame()
    {
        var result = CallTreeFolder.Fold(Samples, Stacks, CategoryByFrameId, WholeSession, Request(frameRoot: 1));

        // Only the two samples whose stack contains frame 1 are in scope; the tree is re-rooted at frame 1.
        Assert.That(result.TotalSamples, Is.EqualTo(2));
        Assert.That(Root(result).Children, Has.Length.EqualTo(1));
        var frame1 = ChildrenOf(result, Root(result))[0];
        Assert.That(frame1.FrameId, Is.EqualTo(1));
        Assert.That(frame1.TotalSamples, Is.EqualTo(2));
        Assert.That(frame1.SelfSamples, Is.EqualTo(2));
    }

    [Test]
    public void Fold_SingleWindow_FiltersByQpc()
    {
        // A single window [2000, ∞) drops the qpc-1000 sample.
        var result = CallTreeFolder.Fold(Samples, Stacks, CategoryByFrameId, [(2000, long.MaxValue)], Request());

        Assert.That(result.TotalSamples, Is.EqualTo(2));
        Assert.That(result.ManagedSamples, Is.EqualTo(1));
        Assert.That(result.ExternalSamples, Is.EqualTo(1));
    }

    [Test]
    public void Fold_MultiWindow_FiltersAcrossDisjointWindows()
    {
        // Two disjoint windows with a gap between them: the qpc-1000 and qpc-3000 samples are in scope,
        // the qpc-2000 sample falls in the gap and is excluded.
        var result = CallTreeFolder.Fold(
            Samples, Stacks, CategoryByFrameId, [(500, 1500), (2500, 3500)], Request());

        Assert.That(result.TotalSamples, Is.EqualTo(2));
        Assert.That(result.ManagedSamples, Is.EqualTo(1)); // qpc-1000 (Managed)
        Assert.That(result.ExternalSamples, Is.EqualTo(1)); // qpc-3000 (External)
    }

    [Test]
    public void Fold_NoSamples_ReturnsEmpty()
    {
        var result = CallTreeFolder.Fold([], Stacks, CategoryByFrameId, WholeSession, Request());

        Assert.That(result.TotalSamples, Is.EqualTo(0));
        Assert.That(result.Nodes, Has.Length.EqualTo(1));
        Assert.That(Root(result).FrameId, Is.EqualTo(-1));
        Assert.That(Root(result).Children, Is.Empty);
    }

    [Test]
    public void Fold_EmptyWindowSet_ReturnsEmpty()
    {
        // An empty interval set — a scope that resolved to nothing (e.g. a system that never ran).
        var result = CallTreeFolder.Fold(Samples, Stacks, CategoryByFrameId, [], Request());

        Assert.That(result.TotalSamples, Is.EqualTo(0));
        Assert.That(Root(result).Children, Is.Empty);
    }

    [Test]
    public void Fold_WindowExcludesAll_ReturnsEmpty()
    {
        var result = CallTreeFolder.Fold(Samples, Stacks, CategoryByFrameId, [(99_999, long.MaxValue)], Request());

        Assert.That(result.TotalSamples, Is.EqualTo(0));
        Assert.That(Root(result).Children, Is.Empty);
    }

    [Test]
    public void Fold_DeepStack_ProducesFlatDepthIndependentArray()
    {
        // A 64-frame stack folds to a 64-deep chain. The wire form must stay flat (one array, children by index)
        // so it never trips System.Text.Json's MaxDepth — a nested-object tree this deep is what 500'd the endpoint.
        const int depth = 64;
        var stack = new ushort[depth];
        for (var i = 0; i < depth; i++)
        {
            stack[i] = (ushort)i; // leaf-first: frame 0 is the leaf, frame depth-1 is the stack root
        }
        ushort[][] stacks = [stack];
        var categories = new int[depth];
        CpuSampleRecord[] samples = [new(1000, 0, 0, 0)];

        var result = CallTreeFolder.Fold(samples, stacks, categories, WholeSession, Request());

        // Synthetic root + one node per frame, all in one flat array — no nesting.
        Assert.That(result.Nodes, Has.Length.EqualTo(depth + 1));
        Assert.That(result.TotalSamples, Is.EqualTo(1));

        // Walk root → leaf following the single child link each step; the chain must be `depth` long.
        var nodeIdx = Root(result).Children[0];
        var steps = 1;
        while (result.Nodes[nodeIdx].Children.Length > 0)
        {
            nodeIdx = result.Nodes[nodeIdx].Children[0];
            steps++;
        }
        Assert.That(steps, Is.EqualTo(depth));
        Assert.That(result.Nodes[nodeIdx].FrameId, Is.EqualTo(0), "leaf is frame 0");
        Assert.That(result.Nodes[nodeIdx].SelfSamples, Is.EqualTo(1), "leaf carries the self time");
    }

    [Test]
    public void Fold_MultipleThreadSlots_ScansAcrossRuns()
    {
        // Samples grouped by (threadSlot, qpc): slot 0 = [100, 300], slot 1 = [200, 400]. The index-aware fold (#351 H1)
        // must visit every in-scope sample across all per-thread runs — a window straddling both slots picks one from each.
        var samples = new CpuSampleRecord[]
        {
            new(100, 0, 0, 0),
            new(300, 0, 0, 1),
            new(200, 1, 0, 0),
            new(400, 1, 0, 0),
        };
        var runs = new (int Start, int Count)[] { (0, 2), (2, 2) };

        var all = CallTreeFolder.Fold(samples, Stacks, CategoryByFrameId, WholeSession, Request(), runs);
        Assert.That(all.TotalSamples, Is.EqualTo(4), "every sample across both thread-slot runs is in scope");

        var mid = CallTreeFolder.Fold(samples, Stacks, CategoryByFrameId, [(150, 350)], Request(), runs);
        Assert.That(mid.TotalSamples, Is.EqualTo(2), "the [150,350] window picks qpc-300 (slot 0) and qpc-200 (slot 1)");

        // Derived runs (threadRuns == null) must reach the same result as the explicit table.
        var derived = CallTreeFolder.Fold(samples, Stacks, CategoryByFrameId, [(150, 350)], Request());
        Assert.That(derived.TotalSamples, Is.EqualTo(2), "deriving the per-thread runs inline matches the explicit table");
    }
}
