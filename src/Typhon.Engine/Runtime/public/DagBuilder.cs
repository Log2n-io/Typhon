using JetBrains.Annotations;
using System;
using System.Collections.Generic;

namespace Typhon.Engine;

/// <summary>
/// Fluent builder for constructing a static system DAG.
/// Validates uniqueness and acyclicity on <see cref="Build"/>.
/// </summary>
/// <remarks>
/// Systems are added with <see cref="AddCallbackSystem"/> or <see cref="AddPipelineSystem"/>, then connected with <see cref="AddEdge"/>.
/// Call <see cref="Build"/> to produce an immutable <see cref="SystemDefinition"/> array with computed predecessors, successors, and topological order.
/// </remarks>
[PublicAPI]
public sealed class DagBuilder
{
    private readonly List<SystemDefinition> _systems = [];
    private readonly Dictionary<string, int> _nameToIndex = new(StringComparer.Ordinal);
    private readonly List<(int from, int to)> _edges = [];

    /// <summary>
    /// Adds a CallbackSystem (single-invocation, inline on dispatching worker).
    /// </summary>
    /// <param name="name">Unique name identifying this system.</param>
    /// <param name="action">Delegate invoked once per tick on a single worker.</param>
    /// <param name="priority">Scheduling priority (enforcement deferred to #201).</param>
    /// <param name="shouldRun">Optional predicate — when it returns <c>false</c>, the system is skipped for that tick.</param>
    public DagBuilder AddCallbackSystem(string name, Action<TickContext> action, SystemPriority priority = SystemPriority.Normal, Func<bool> shouldRun = null)
        => AddCallbackSystemInternal(name, action, priority, shouldRun, null);

    internal DagBuilder AddCallbackSystemInternal(string name, Action<TickContext> action, SystemPriority priority, Func<bool> shouldRun,
        (string FilePath, int Line, string MethodName)? sourceOverride, ISystem instance = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(action);

        if (_nameToIndex.ContainsKey(name))
        {
            throw new InvalidOperationException($"System '{name}' already exists in the DAG.");
        }

        var idx = _systems.Count;
        _nameToIndex[name] = idx;
        var src = sourceOverride ?? SystemSourceResolver.Resolve(action);
        _systems.Add(new SystemDefinition
        {
            Name = name,
            Type = SystemType.CallbackSystem,
            Index = idx,
            Priority = priority,
            CallbackAction = action,
            ShouldRun = shouldRun,
            Instance = instance,
            TotalChunks = 1,
            SourceFilePath = src?.FilePath,
            SourceLine = src?.Line ?? 0,
            SourceMethod = src?.MethodName
        });
        return this;
    }

    /// <summary>
    /// Adds a QuerySystem (single-worker entity iteration).
    /// </summary>
    /// <param name="name">Unique name identifying this system.</param>
    /// <param name="action">Delegate invoked once per tick on a single worker.</param>
    /// <param name="priority">Scheduling priority (enforcement deferred to #201).</param>
    /// <param name="shouldRun">Optional predicate — if false, system is skipped.</param>
    public DagBuilder AddQuerySystem(string name, Action<TickContext> action, SystemPriority priority = SystemPriority.Normal, Func<bool> shouldRun = null)
        => AddQuerySystemInternal(name, action, priority, shouldRun, null);

    internal DagBuilder AddQuerySystemInternal(string name, Action<TickContext> action, SystemPriority priority, Func<bool> shouldRun,
        (string FilePath, int Line, string MethodName)? sourceOverride, ISystem instance = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(action);

        if (_nameToIndex.ContainsKey(name))
        {
            throw new InvalidOperationException($"System '{name}' already exists in the DAG.");
        }

        var idx = _systems.Count;
        _nameToIndex[name] = idx;
        var src = sourceOverride ?? SystemSourceResolver.Resolve(action);
        _systems.Add(new SystemDefinition
        {
            Name = name,
            Type = SystemType.QuerySystem,
            Index = idx,
            Priority = priority,
            CallbackAction = action,
            ShouldRun = shouldRun,
            Instance = instance,
            TotalChunks = 1,
            SourceFilePath = src?.FilePath,
            SourceLine = src?.Line ?? 0,
            SourceMethod = src?.MethodName
        });
        return this;
    }

    /// <summary>
    /// Adds a PipelineSystem (multi-worker chunk-parallel execution).
    /// </summary>
    /// <param name="name">Unique name identifying this system.</param>
    /// <param name="chunkAction">Delegate called per chunk with (chunkIndex, totalChunks). Must be thread-safe.</param>
    /// <param name="totalChunks">Number of chunks to distribute across workers.</param>
    /// <param name="priority">Scheduling priority (enforcement deferred to #201).</param>
    /// <param name="shouldRun">Optional predicate — if false, system is skipped.</param>
    /// <param name="instance">Optional class-based system instance backing this registration; <c>null</c> for a delegate-only pipeline system.</param>
    public DagBuilder AddPipelineSystem(string name, Action<int, int> chunkAction, int totalChunks, SystemPriority priority = SystemPriority.Normal,
        Func<bool> shouldRun = null, ISystem instance = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(chunkAction);

        if (totalChunks < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(totalChunks), totalChunks, "TotalChunks must be >= 1.");
        }

        if (_nameToIndex.ContainsKey(name))
        {
            throw new InvalidOperationException($"System '{name}' already exists in the DAG.");
        }

        var idx = _systems.Count;
        _nameToIndex[name] = idx;
        var src = SystemSourceResolver.Resolve(chunkAction);
        _systems.Add(new SystemDefinition
        {
            Name = name,
            Type = SystemType.PipelineSystem,
            Index = idx,
            Priority = priority,
            PipelineChunkAction = chunkAction,
            ShouldRun = shouldRun,
            Instance = instance,
            TotalChunks = totalChunks,
            SourceFilePath = src?.FilePath,
            SourceLine = src?.Line ?? 0,
            SourceMethod = src?.MethodName
        });
        return this;
    }

    /// <summary>
    /// Adds a dependency edge: <paramref name="from"/> must complete before <paramref name="to"/> can start.
    /// </summary>
    public DagBuilder AddEdge(string from, string to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        if (!_nameToIndex.TryGetValue(from, out var fromIdx))
        {
            throw new InvalidOperationException($"System '{from}' not found. Add systems before edges.");
        }

        if (!_nameToIndex.TryGetValue(to, out var toIdx))
        {
            throw new InvalidOperationException($"System '{to}' not found. Add systems before edges.");
        }

        _edges.Add((fromIdx, toIdx));
        return this;
    }

    /// <summary>
    /// Builds the DAG: computes successors, predecessor counts, topological order, and validates acyclicity.
    /// </summary>
    /// <returns>Immutable array of <see cref="SystemDefinition"/> with all graph metadata computed.</returns>
    /// <exception cref="InvalidOperationException">The graph contains a cycle.</exception>
    public (SystemDefinition[] Systems, int[] TopologicalOrder) Build()
    {
        var graphSpan = TyphonEvent.BeginSchedulerGraphBuild();
        try
        {
            return BuildCore(ref graphSpan);
        }
        finally
        {
            graphSpan.Dispose();
        }
    }

    private (SystemDefinition[] Systems, int[] TopologicalOrder) BuildCore(ref SchedulerGraphBuildEvent graphSpan)
    {
        var count = _systems.Count;
        if (count == 0)
        {
            return ([], []);
        }

        // Compute successors and predecessor counts
        var successorLists = new List<int>[count];
        for (var i = 0; i < count; i++)
        {
            successorLists[i] = [];
        }

        foreach (var (from, to) in _edges)
        {
            successorLists[from].Add(to);
            _systems[to].PredecessorCount++;
        }

        for (var i = 0; i < count; i++)
        {
            _systems[i].Successors = successorLists[i].ToArray();
        }

        // Kahn's algorithm: topological sort + cycle detection
        var inDegree = new int[count];
        for (var i = 0; i < count; i++)
        {
            inDegree[i] = _systems[i].PredecessorCount;
        }

        var queue = new Queue<int>();
        for (var i = 0; i < count; i++)
        {
            if (inDegree[i] == 0)
            {
                queue.Enqueue(i);
            }
        }

        var topologicalOrder = new int[count];
        var visited = 0;
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            topologicalOrder[visited++] = node;

            foreach (var succ in _systems[node].Successors)
            {
                if (--inDegree[succ] == 0)
                {
                    queue.Enqueue(succ);
                }
            }
        }

        if (visited != count)
        {
            throw new InvalidOperationException("DAG contains a cycle.");
        }

        graphSpan.SysCount = (ushort)Math.Min(count, ushort.MaxValue);
        graphSpan.EdgeCount = (ushort)Math.Min(_edges.Count, ushort.MaxValue);
        graphSpan.TopoLen = (ushort)Math.Min(topologicalOrder.Length, ushort.MaxValue);

        return ([.. _systems], topologicalOrder);
    }
}
