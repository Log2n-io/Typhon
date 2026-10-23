using System;
using System.Collections.Generic;
using System.Text.Json;

namespace SwgTatooine.Server;

/// <summary>
/// An interior's published navigation (<c>ASSET_FORMAT.md</c> § 5.9): a waypoint graph, and a cell grid naming for every floor position the node it can
/// walk straight to. In the world's frame convention, X mirrored, like everything else read from a descriptor.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a program needs it at all.</b> A room is small and full of furniture, and "walk towards the door" from a random point walks into a table. The
/// graph's edges are published clear for a 0.3 m body, and so is the walk from any position in a cell to that cell's node, so a route built from them
/// touches nothing: cell, node, edges, node, cell.
/// </para>
/// <para>
/// <b>The mirror is applied to the nodes and undone at the grid.</b> The grid is published in the glTF frame and its columns run along glTF +X; a world
/// position is looked up by mirroring it back, which keeps the published <c>origin</c> and <c>cols</c> meaningful as read.
/// </para>
/// </remarks>
internal sealed class NavGraph
{
    private readonly float[] _x;
    private readonly float[] _z;
    private readonly int[][] _neighbours;
    private readonly float _originX;
    private readonly float _originZ;
    private readonly float _size;
    private readonly int _cols;
    private readonly int _rows;
    private readonly int[] _cell;

    private NavGraph(float[] x, float[] z, int[][] neighbours, float originX, float originZ, float size, int cols, int rows, int[] cell)
    {
        _x = x;
        _z = z;
        _neighbours = neighbours;
        _originX = originX;
        _originZ = originZ;
        _size = size;
        _cols = cols;
        _rows = rows;
        _cell = cell;
    }

    /// <summary>How many waypoints the graph has.</summary>
    internal int NodeCount => _x.Length;

    /// <summary>A waypoint, in the world's convention.</summary>
    /// <param name="node">Its index.</param>
    /// <returns>Its plan position.</returns>
    internal (float X, float Z) Node(int node) => (_x[node], _z[node]);

    /// <summary>Reads <c>nav</c> from a descriptor.</summary>
    /// <param name="json">The descriptor.</param>
    /// <returns>The graph, or <see langword="null"/> when the asset publishes none or one this reader cannot trust.</returns>
    /// <remarks>
    /// Refused whole rather than read in part: an edge naming a node that does not exist, or a cell table of the wrong length, is a pipeline this build is
    /// older than, and a route built on half of it would walk a body into what the other half described.
    /// </remarks>
    internal static NavGraph Read(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("nav", out var nav) || nav.ValueKind != JsonValueKind.Object
            || !nav.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array
            || !nav.TryGetProperty("edges", out var edges) || edges.ValueKind != JsonValueKind.Array
            || !nav.TryGetProperty("cells", out var cells) || cells.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var n = nodes.GetArrayLength();
        var x = new float[n];
        var z = new float[n];
        for (var i = 0; i < n; i++)
        {
            var p = nodes[i];
            if (p.ValueKind != JsonValueKind.Array || p.GetArrayLength() < 3)
            {
                return null;
            }

            x[i] = -p[0].GetSingle();
            z[i] = p[2].GetSingle();
        }

        var lists = new List<int>[n];
        for (var i = 0; i < n; i++)
        {
            lists[i] = [];
        }

        foreach (var edge in edges.EnumerateArray())
        {
            if (edge.ValueKind != JsonValueKind.Array || edge.GetArrayLength() < 2)
            {
                return null;
            }

            var a = edge[0].GetInt32();
            var b = edge[1].GetInt32();
            if ((uint)a >= (uint)n || (uint)b >= (uint)n)
            {
                return null;
            }

            lists[a].Add(b);
            lists[b].Add(a);
        }

        var neighbours = new int[n][];
        for (var i = 0; i < n; i++)
        {
            neighbours[i] = [.. lists[i]];
        }

        if (!cells.TryGetProperty("origin", out var origin) || origin.ValueKind != JsonValueKind.Array || origin.GetArrayLength() < 2
            || !cells.TryGetProperty("size", out var size) || !cells.TryGetProperty("cols", out var cols) || !cells.TryGetProperty("rows", out var rows)
            || !cells.TryGetProperty("node", out var table) || table.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var c = cols.GetInt32();
        var r = rows.GetInt32();
        var s = size.GetSingle();
        if (c <= 0 || r <= 0 || !(s > 0f) || table.GetArrayLength() != c * r)
        {
            return null;
        }

        var cell = new int[c * r];
        for (var i = 0; i < cell.Length; i++)
        {
            cell[i] = table[i].GetInt32();
            if (cell[i] >= n)
            {
                return null;
            }
        }

        return new NavGraph(x, z, neighbours, origin[0].GetSingle(), origin[1].GetSingle(), s, c, r, cell);
    }

    /// <summary>
    /// The node a body standing here walks straight to, or -1 when no cell near it serves one.
    /// </summary>
    /// <param name="x">The body, east, in the world's convention.</param>
    /// <param name="z">The body, north.</param>
    /// <returns>A node index, or -1.</returns>
    /// <remarks>
    /// A body pressed against a wall or a table can stand in a cell the grid marks -1 (no position in it is clear) or just outside the grid: § 5.9 says to
    /// take the nearest cell that has a node, so the search widens ring by ring, up to two metres.
    /// </remarks>
    internal int NodeAt(float x, float z)
    {
        // Back to the glTF frame the grid is published in.
        var col = (int)MathF.Floor((-x - _originX) / _size);
        var row = (int)MathF.Floor((z - _originZ) / _size);
        var rings = (int)MathF.Ceiling(2f / _size);
        for (var ring = 0; ring <= rings; ring++)
        {
            var best = -1;
            var bestD = float.MaxValue;
            for (var dr = -ring; dr <= ring; dr++)
            {
                for (var dc = -ring; dc <= ring; dc++)
                {
                    if (Math.Max(Math.Abs(dr), Math.Abs(dc)) != ring)
                    {
                        continue;
                    }

                    var cc = col + dc;
                    var rr = row + dr;
                    if ((uint)cc >= (uint)_cols || (uint)rr >= (uint)_rows)
                    {
                        continue;
                    }

                    var node = _cell[(rr * _cols) + cc];
                    var d = (dr * dr) + (dc * dc);
                    if (node >= 0 && d < bestD)
                    {
                        best = node;
                        bestD = d;
                    }
                }
            }

            if (best >= 0)
            {
                return best;
            }
        }

        return -1;
    }

    /// <summary>The node standing at this position, or -1 — how a published path names the node it starts on (§ 5.7, by position).</summary>
    /// <param name="x">East, in the world's convention.</param>
    /// <param name="z">North.</param>
    /// <returns>The index of a node within a centimetre, or -1.</returns>
    internal int NodeExactlyAt(float x, float z)
    {
        for (var i = 0; i < _x.Length; i++)
        {
            if (MathF.Abs(_x[i] - x) < 0.01f && MathF.Abs(_z[i] - z) < 0.01f)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// The waypoints from a position to a node: the position's own node first, then the shortest edge path (Dijkstra, cost = distance).
    /// </summary>
    /// <param name="fromX">Where the body stands, east.</param>
    /// <param name="fromZ">Where it stands, north.</param>
    /// <param name="to">The node to reach.</param>
    /// <param name="into">Receives the waypoints in walking order; the body's own position is not included.</param>
    /// <returns>Whether a route exists.</returns>
    internal bool TryRoute(float fromX, float fromZ, int to, List<(float X, float Z)> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        var from = NodeAt(fromX, fromZ);
        if (from < 0 || (uint)to >= (uint)_x.Length)
        {
            return false;
        }

        var n = _x.Length;
        var dist = new float[n];
        var prev = new int[n];
        var done = new bool[n];
        Array.Fill(dist, float.PositiveInfinity);
        Array.Fill(prev, -1);
        dist[from] = 0f;

        // A dozen nodes: the linear scan for the nearest open node is the whole priority queue.
        for (var step = 0; step < n; step++)
        {
            var u = -1;
            for (var i = 0; i < n; i++)
            {
                if (!done[i] && (u < 0 || dist[i] < dist[u]))
                {
                    u = i;
                }
            }

            if (u < 0 || float.IsPositiveInfinity(dist[u]))
            {
                break;
            }

            done[u] = true;
            foreach (var v in _neighbours[u])
            {
                var dx = _x[v] - _x[u];
                var dz = _z[v] - _z[u];
                var d = dist[u] + MathF.Sqrt((dx * dx) + (dz * dz));
                if (d < dist[v])
                {
                    dist[v] = d;
                    prev[v] = u;
                }
            }
        }

        if (float.IsPositiveInfinity(dist[to]))
        {
            return false;
        }

        var start = into.Count;
        for (var v = to; v >= 0; v = prev[v])
        {
            into.Add((_x[v], _z[v]));
        }

        into.Reverse(start, into.Count - start);
        return true;
    }
}
