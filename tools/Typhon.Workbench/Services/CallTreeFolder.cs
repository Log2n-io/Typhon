using System;
using System.Collections.Generic;
using Typhon.Profiler;
using Typhon.Workbench.Dtos.Profiler;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Services;

/// <summary>
/// Folds a flat CPU-sample set into a dotTrace-style call tree for one scope (#351 Phase 4, §8.3). Runs server-side at
/// request time, off any engine hot path — ordinary allocation-friendly code is fine here. The folded tree is KB-scale;
/// the raw sample firehose never crosses to the browser.
/// </summary>
public static class CallTreeFolder
{
    private sealed class Node
    {
        public int FrameId;
        public long SelfSamples;
        public long TotalSamples;
        public readonly Dictionary<int, Node> Children = [];
    }

    /// <summary>Synthetic <c>FrameId</c> of the §8.7 <c>[GC suspension]</c> involuntary-stall aggregate node.</summary>
    public const int GcSuspensionFrameId = -2;

    /// <summary>Synthetic <c>FrameId</c> of the §8.7 <c>[Preempted]</c> (scheduler-preemption) involuntary-stall aggregate node.</summary>
    public const int PreemptedFrameId = -3;

    /// <summary>Synthetic <c>FrameId</c> of the §8.7 <c>[Paging]</c> involuntary-stall aggregate node.</summary>
    public const int PagingFrameId = -4;

    /// <summary>
    /// Folds the samples that fall in <paramref name="scopeWindows"/> into a call tree. Only the in-scope samples are
    /// visited — a <see cref="ScopedSampleCursor"/> binary-searches the per-thread qpc-sorted runs, so a narrow scope
    /// costs <c>O(runs·windows·log n + k)</c>, not a full scan (#351 — H1). Stacks are stored leaf-first, so each
    /// in-scope sample is walked root→leaf: <c>TotalSamples</c> increments on every node on the path, <c>SelfSamples</c>
    /// only on the leaf. The category breakdown attributes each sample's leaf-frame category (self-time semantic).
    /// Returns <see cref="CallTreeResponseDto.Empty"/> when no sample is in scope.
    /// <para>
    /// §8.7 — each sample is first classified by <paramref name="classifier"/>: <b>on-CPU</b> folds per-method always;
    /// <b>voluntary-wait</b> folds per-method in the wall-clock view but is dropped in the on-CPU view; <b>involuntary-stall</b>
    /// (GC suspension / scheduler preemption / paging) never folds per-method — it collapses into a labelled aggregate node
    /// (<see cref="GcSuspensionFrameId"/> / <see cref="PreemptedFrameId"/> / <see cref="PagingFrameId"/>) hung off the root,
    /// in both views. A <c>null</c> classifier — or one with no context-switch data — degrades to the <c>SampleType</c>
    /// proxy (Managed ⇒ on-CPU, External ⇒ voluntary), with GC stalls still aggregated.
    /// </para>
    /// </summary>
    /// <param name="samples">All CPU samples, qpc-sorted per thread slot.</param>
    /// <param name="stacks">The interned stack table — each entry a leaf-first array of frame ids.</param>
    /// <param name="categoryByFrameId"><c>frameId → categoryId</c> table, for leaf-frame category attribution.</param>
    /// <param name="scopeWindows">
    /// The resolved scope as a sorted, disjoint QPC interval set (see <see cref="ScopeResolver"/>). A sample is in scope iff
    /// its <c>Qpc</c> falls in any window. <see cref="ScopeResolver.WholeSession"/> means no filtering; an empty array means
    /// no sample is in scope.
    /// </param>
    /// <param name="request">Carries the non-time scope axes — view mode and the optional frame-root re-root.</param>
    /// <param name="threadRuns">
    /// The per-thread-slot contiguous runs of <paramref name="samples"/> (see <see cref="CpuSampleScope.BuildThreadRuns"/>).
    /// Pass the session's pre-built table; <c>null</c> derives it inline (one O(n) pass — used by tests only).
    /// </param>
    /// <param name="classifier">
    /// The §8.7 per-sample classifier (see <see cref="SampleClassifier"/>). <c>null</c> ⇒ pure degraded <c>SampleType</c>
    /// proxy mode with no involuntary classification at all.
    /// </param>
    public static CallTreeResponseDto Fold(
        CpuSampleRecord[] samples,
        ushort[][] stacks,
        int[] categoryByFrameId,
        (long Start, long End)[] scopeWindows,
        CallTreeRequestDto request,
        (int Start, int Count)[] threadRuns = null,
        SampleClassifier classifier = null)
    {
        var classificationAvailable = classifier is { ClassificationAvailable: true };
        if (samples.Length == 0)
        {
            return CallTreeResponseDto.Empty;
        }
        threadRuns ??= CpuSampleScope.BuildThreadRuns(samples);

        var onCpuOnly = string.Equals(request.ViewMode, "on-cpu", StringComparison.OrdinalIgnoreCase);
        var frameRoot = request.FrameRoot;

        var root = new Node { FrameId = -1 };
        long total = 0, managed = 0, external = 0;
        long gcStalls = 0, schedulerStalls = 0, pagingStalls = 0;
        var categoryBreakdown = new Dictionary<int, long>();

        foreach (ref readonly var s in new ScopedSampleCursor(samples, threadRuns, scopeWindows))
        {
            // §8.7 classification. An Unknown verdict (no scheduling evidence) degrades to the SampleType proxy.
            var cls = classifier?.Classify(s.Qpc, s.ThreadSlot) ?? SampleClass.Unknown;
            if (cls == SampleClass.Unknown)
            {
                cls = s.SampleType == 0 ? SampleClass.OnCpu : SampleClass.Voluntary;
            }

            // Involuntary stalls never fold per-method — their stack is bad-luck noise. They collapse into a labelled
            // aggregate hung off the root. Suppressed entirely under a frame-root drill: a stall has no stack, so it
            // cannot honestly be claimed to have happened "under" the drilled method.
            if (cls is SampleClass.InvoluntaryGc or SampleClass.InvoluntaryScheduler or SampleClass.InvoluntaryPaging)
            {
                if (frameRoot.HasValue)
                {
                    continue;
                }
                switch (cls)
                {
                    case SampleClass.InvoluntaryGc: gcStalls++; break;
                    case SampleClass.InvoluntaryScheduler: schedulerStalls++; break;
                    default: pagingStalls++; break;
                }
                total++;
                if (s.SampleType == 0) { managed++; } else { external++; }
                continue;
            }

            // Voluntary waits carry a meaningful stack (which lock / which IO) — folded in the wall-clock view, dropped
            // in the on-CPU view (a parked thread is not a CPU cycle).
            if (cls == SampleClass.Voluntary && onCpuOnly)
            {
                continue;
            }

            if (s.StackIndex >= (uint)stacks.Length)
            {
                continue;
            }
            var stack = stacks[s.StackIndex];
            if (stack.Length == 0)
            {
                continue;
            }

            // Root frame is the last element (leaf-first storage). A frameRoot re-roots the walk at the outermost
            // occurrence of that frame; a sample whose stack lacks the frame is out of scope.
            var topIndex = stack.Length - 1;
            if (frameRoot.HasValue)
            {
                var found = -1;
                for (var k = stack.Length - 1; k >= 0; k--)
                {
                    if (stack[k] == frameRoot.Value)
                    {
                        found = k;
                        break;
                    }
                }
                if (found < 0)
                {
                    continue;
                }
                topIndex = found;
            }

            total++;
            if (s.SampleType == 0)
            {
                managed++;
            }
            else
            {
                external++;
            }

            var node = root;
            for (var k = topIndex; k >= 0; k--)
            {
                int frameId = stack[k];
                if (!node.Children.TryGetValue(frameId, out var child))
                {
                    child = new Node { FrameId = frameId };
                    node.Children[frameId] = child;
                }
                child.TotalSamples++;
                node = child;
            }
            node.SelfSamples++;

            int leafFrame = stack[0];
            var categoryId = leafFrame < categoryByFrameId.Length ? categoryByFrameId[leafFrame] : -1;
            if (categoryId >= 0)
            {
                categoryBreakdown[categoryId] = categoryBreakdown.GetValueOrDefault(categoryId) + 1;
            }
        }

        if (total == 0)
        {
            return new CallTreeResponseDto([new CallTreeNodeDto(-1, 0, 0, [])], 0, 0, 0, [], classificationAvailable);
        }

        root.TotalSamples = total;
        AddInvoluntaryAggregate(root, GcSuspensionFrameId, gcStalls);
        AddInvoluntaryAggregate(root, PreemptedFrameId, schedulerStalls);
        AddInvoluntaryAggregate(root, PagingFrameId, pagingStalls);

        var slices = new CategorySliceDto[categoryBreakdown.Count];
        var sliceIdx = 0;
        foreach (var kv in categoryBreakdown)
        {
            slices[sliceIdx++] = new CategorySliceDto(kv.Key, kv.Value);
        }

        return new CallTreeResponseDto(Flatten(root), total, managed, external, slices, classificationAvailable);
    }

    /// <summary>
    /// Hangs a §8.7 involuntary-stall aggregate (<paramref name="count"/> samples, no children) off the tree root under a
    /// synthetic negative <paramref name="frameId"/>. A zero count adds nothing — the node only appears when stalls occurred.
    /// </summary>
    private static void AddInvoluntaryAggregate(Node root, int frameId, long count)
    {
        if (count <= 0)
        {
            return;
        }
        root.Children[frameId] = new Node { FrameId = frameId, SelfSamples = count, TotalSamples = count };
    }

    /// <summary>
    /// Flattens the mutable build tree into the wire array. Breadth-first index assignment puts the synthetic root at
    /// index 0 and gives every child a higher index than its parent; each node's children are ordered hottest-first
    /// (by total samples) so the panel renders the hot path without re-sorting. Depth lives in index links, not nested
    /// objects — so an arbitrarily deep call stack never trips System.Text.Json's MaxDepth.
    /// </summary>
    private static CallTreeNodeDto[] Flatten(Node root)
    {
        var ordered = new List<Node> { root };
        var index = new Dictionary<Node, int> { [root] = 0 };
        var sortedChildren = new List<List<Node>>();

        for (var i = 0; i < ordered.Count; i++)
        {
            var kids = new List<Node>(ordered[i].Children.Values);
            kids.Sort(static (a, b) => b.TotalSamples.CompareTo(a.TotalSamples));
            sortedChildren.Add(kids);
            foreach (var kid in kids)
            {
                index[kid] = ordered.Count;
                ordered.Add(kid);
            }
        }

        var result = new CallTreeNodeDto[ordered.Count];
        for (var i = 0; i < ordered.Count; i++)
        {
            var node = ordered[i];
            var kids = sortedChildren[i];
            var childIndices = new int[kids.Count];
            for (var c = 0; c < kids.Count; c++)
            {
                childIndices[c] = index[kids[c]];
            }
            result[i] = new CallTreeNodeDto(node.FrameId, node.SelfSamples, node.TotalSamples, childIndices);
        }
        return result;
    }
}
