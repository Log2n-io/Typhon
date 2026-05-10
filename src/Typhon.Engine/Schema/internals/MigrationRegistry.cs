using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// Stores user-registered migration functions and resolves migration chains via BFS pathfinding.
/// Migrations are keyed by (componentName, fromRevision, toRevision).
/// </summary>
internal sealed class MigrationRegistry
{
    private readonly Dictionary<(string Name, int FromRev, int ToRev), IMigrationEntry> _migrations = new();

    /// <summary>Number of registered migrations.</summary>
    public int Count => _migrations.Count;

    /// <summary>
    /// Registers a strongly-typed migration function. Validates [Component] attributes at registration time.
    /// </summary>
    public void Register<TOld, TNew>(MigrationFunc<TOld, TNew> func) where TOld : unmanaged where TNew : unmanaged
    {
        var entry = new TypedMigrationEntry<TOld, TNew>(func);
        AddEntry(entry);
    }

    /// <summary>
    /// Registers a byte-level migration function for scenarios where the old struct type is no longer in code.
    /// </summary>
    public void RegisterByte(string componentName, int fromRevision, int toRevision, int oldSize, int newSize, ByteMigrationFunc func)
    {
        var entry = new ByteMigrationEntry(componentName, fromRevision, toRevision, oldSize, newSize, func);
        AddEntry(entry);
    }

    /// <summary>
    /// Returns the direct migration entry for a specific (name, fromRev, toRev) tuple, or null if not registered.
    /// </summary>
    public IMigrationEntry GetDirect(string componentName, int fromRevision, int toRevision) => _migrations.GetValueOrDefault((componentName, fromRevision, toRevision));

    /// <summary>
    /// Resolves a migration chain from <paramref name="fromRevision"/> to <paramref name="toRevision"/> using BFS pathfinding.
    /// Returns null if no path exists (caller should throw <see cref="SchemaValidationException"/>).
    /// </summary>
    public MigrationChain? GetChain(string componentName, int fromRevision, int toRevision)
    {
        // Direct lookup first (common case: single-step migration)
        var direct = GetDirect(componentName, fromRevision, toRevision);
        if (direct != null)
        {
            return new MigrationChain
            {
                Steps = [direct],
                MaxIntermediateSize = Math.Max(direct.OldSize, direct.NewSize),
            };
        }

        // Build adjacency list for this component
        var adjacency = BuildAdjacencyList(componentName);
        if (adjacency.Count == 0)
        {
            return null;
        }

        // BFS from fromRevision to toRevision
        var path = BfsPath(adjacency, fromRevision, toRevision);
        if (path == null)
        {
            return null;
        }

        // Convert path to chain of migration entries
        var steps = new IMigrationEntry[path.Count - 1];
        var maxSize = 0;
        for (int i = 0; i < steps.Length; i++)
        {
            var entry = _migrations[(componentName, path[i], path[i + 1])];
            steps[i] = entry;
            maxSize = Math.Max(maxSize, Math.Max(entry.OldSize, entry.NewSize));
        }

        return new MigrationChain
        {
            Steps = steps,
            MaxIntermediateSize = maxSize,
        };
    }

    /// <summary>
    /// Checks whether any migrations are registered for the given component name.
    /// </summary>
    public bool HasMigrationsFor(string componentName)
    {
        foreach (var key in _migrations.Keys)
        {
            if (key.Name == componentName)
            {
                return true;
            }
        }

        return false;
    }

    private void AddEntry(IMigrationEntry entry)
    {
        var key = (entry.ComponentName, entry.FromRevision, entry.ToRevision);
        if (_migrations.ContainsKey(key))
        {
            throw new InvalidOperationException(
                $"Duplicate migration registered for component '{entry.ComponentName}' from revision {entry.FromRevision} to {entry.ToRevision}.");
        }

        // Sanity check: call with zeroed input to verify the function doesn't throw on basic input
        ValidateMigrationFunction(entry);

        _migrations[key] = entry;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ValidateMigrationFunction(IMigrationEntry entry)
    {
        Span<byte> oldBuf = stackalloc byte[entry.OldSize];
        Span<byte> newBuf = stackalloc byte[entry.NewSize];
        oldBuf.Clear();
        newBuf.Clear();

        try
        {
            entry.Execute(oldBuf, newBuf);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Migration function for '{entry.ComponentName}' (rev {entry.FromRevision}→{entry.ToRevision}) " +
                $"threw on zero-initialized input: {ex.GetType().Name}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Builds an adjacency list of (fromRev → [toRev, ...]) for a specific component.
    /// </summary>
    private Dictionary<int, List<int>> BuildAdjacencyList(string componentName)
    {
        var adj = new Dictionary<int, List<int>>();
        foreach (var key in _migrations.Keys)
        {
            if (key.Name != componentName)
            {
                continue;
            }

            if (!adj.TryGetValue(key.FromRev, out var neighbors))
            {
                neighbors = new List<int>(4);
                adj[key.FromRev] = neighbors;
            }

            neighbors.Add(key.ToRev);
        }

        return adj;
    }

    /// <summary>
    /// BFS shortest-path search from <paramref name="start"/> to <paramref name="goal"/>.
    /// Returns the ordered list of revision nodes in the path, or null if unreachable.
    /// </summary>
    private static List<int> BfsPath(Dictionary<int, List<int>> adjacency, int start, int goal)
    {
        if (!adjacency.ContainsKey(start))
        {
            return null;
        }

        var visited = new HashSet<int> { start };
        var parentMap = new Dictionary<int, int>(); // child → parent for path reconstruction
        var queue = new Queue<int>();
        queue.Enqueue(start);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            if (current == goal)
            {
                return ReconstructPath(parentMap, start, goal);
            }

            if (!adjacency.TryGetValue(current, out var neighbors))
            {
                continue;
            }

            foreach (var next in neighbors)
            {
                if (visited.Add(next))
                {
                    parentMap[next] = current;
                    queue.Enqueue(next);
                }
            }
        }

        return null;
    }

    private static List<int> ReconstructPath(Dictionary<int, int> parentMap, int start, int goal)
    {
        var path = new List<int> { goal };
        var current = goal;

        while (current != start)
        {
            current = parentMap[current];
            path.Add(current);
        }

        path.Reverse();
        return path;
    }
}
