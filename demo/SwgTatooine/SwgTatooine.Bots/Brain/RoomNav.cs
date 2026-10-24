using System;
using System.Collections.Generic;
using System.Text.Json;

namespace SwgTatooine.Bots.Brain;

/// <summary>
/// A room's floor graph as its descriptor publishes it (<c>ASSET_FORMAT.md</c> § 5.9), and the walk across it: to the cell's node, along the graph, out
/// to the point.
/// </summary>
/// <remarks>
/// <para>
/// <b>Mirrored like every published point</b>, so its nodes are world-convention positions in the room's frame — and a room stands at its realm's
/// origin, unturned, so that frame is the realm's. The cell grid is read in the descriptor's own frame and queried at the mirrored point, which is the
/// same lookup without rebuilding the grid.
/// </para>
/// <para>
/// <b>The exporter's guarantee is what makes this short</b>: from any position in a cell a body walks straight to that cell's node without touching a
/// collider, and every edge is a straight walk. So the path is the two ends plus a Dijkstra over a dozen nodes, and nothing here tests a collider.
/// </para>
/// </remarks>
public sealed class RoomNav
{
    private readonly (float X, float Z)[] _nodes;
    private readonly List<int>[] _adjacent;
    private readonly float _originX;
    private readonly float _originZ;
    private readonly float _size;
    private readonly int _cols;
    private readonly int _rows;
    private readonly int[] _cellNode;

    /// <summary>A graph from parts: mirrored nodes, edges, and the descriptor-frame cell grid.</summary>
    public RoomNav((float X, float Z)[] nodes, (int A, int B)[] edges, float originX, float originZ, float size, int cols, int rows, int[] cellNode)
    {
        _nodes = nodes;
        _adjacent = new List<int>[nodes.Length];
        for (var i = 0; i < nodes.Length; i++)
        {
            _adjacent[i] = [];
        }

        foreach (var (a, b) in edges)
        {
            if ((uint)a < (uint)nodes.Length && (uint)b < (uint)nodes.Length)
            {
                _adjacent[a].Add(b);
                _adjacent[b].Add(a);
            }
        }

        _originX = originX;
        _originZ = originZ;
        _size = size;
        _cols = cols;
        _rows = rows;
        _cellNode = cellNode;
    }

    /// <summary>The nodes, mirrored.</summary>
    public IReadOnlyList<(float X, float Z)> Nodes => _nodes;

    /// <summary>Reads a descriptor's <c>nav</c>.</summary>
    public static RoomNav Read(JsonElement nav)
    {
        var nodes = new List<(float, float)>();
        foreach (var n in nav.GetProperty("nodes").EnumerateArray())
        {
            nodes.Add(BotAssets.Point(n));
        }

        var edges = new List<(int, int)>();
        foreach (var e in nav.GetProperty("edges").EnumerateArray())
        {
            edges.Add((e[0].GetInt32(), e[1].GetInt32()));
        }

        var cells = nav.GetProperty("cells");
        var origin = cells.GetProperty("origin");
        var node = new List<int>();
        foreach (var n in cells.GetProperty("node").EnumerateArray())
        {
            node.Add(n.GetInt32());
        }

        return new RoomNav(
            [.. nodes],
            [.. edges],
            origin[0].GetSingle(),
            origin[1].GetSingle(),
            cells.GetProperty("size").GetSingle(),
            cells.GetProperty("cols").GetInt32(),
            cells.GetProperty("rows").GetInt32(),
            [.. node]);
    }

    /// <summary>The node that serves a point, or -1 when it is off the floor; a point in a <c>-1</c> cell takes the nearest cell that has a node.</summary>
    /// <param name="x">The point, east (world convention).</param>
    /// <param name="z">The point, north.</param>
    public int NodeAt(float x, float z)
    {
        // The grid is the descriptor's, unmirrored: the mirrored point's cell is the published point's.
        var col = (int)MathF.Floor((-x - _originX) / _size);
        var row = (int)MathF.Floor((z - _originZ) / _size);
        var best = -1;
        var bestD = int.MaxValue;
        for (var ring = 0; ring <= 4 && best < 0; ring++)
        {
            for (var dr = -ring; dr <= ring; dr++)
            {
                for (var dc = -ring; dc <= ring; dc++)
                {
                    if (Math.Max(Math.Abs(dr), Math.Abs(dc)) != ring)
                    {
                        continue;
                    }

                    var r = row + dr;
                    var c = col + dc;
                    if (r < 0 || r >= _rows || c < 0 || c >= _cols)
                    {
                        continue;
                    }

                    var n = _cellNode[(r * _cols) + c];
                    var d = (dr * dr) + (dc * dc);
                    if (n >= 0 && d < bestD)
                    {
                        best = n;
                        bestD = d;
                    }
                }
            }
        }

        return best;
    }

    /// <summary>The walk from one point of the room to another, as waypoints ending on <paramref name="toX"/>, <paramref name="toZ"/>; null off the floor.</summary>
    public List<(float X, float Z)> Route(float fromX, float fromZ, float toX, float toZ)
    {
        var a = NodeAt(fromX, fromZ);
        var b = NodeAt(toX, toZ);
        if (a < 0 || b < 0)
        {
            return null;
        }

        var dist = new float[_nodes.Length];
        var prev = new int[_nodes.Length];
        var done = new bool[_nodes.Length];
        Array.Fill(dist, float.PositiveInfinity);
        Array.Fill(prev, -1);
        dist[a] = 0f;
        for (var k = 0; k < _nodes.Length; k++)
        {
            // A dozen nodes: the linear minimum is the honest priority queue.
            var u = -1;
            for (var i = 0; i < _nodes.Length; i++)
            {
                if (!done[i] && (u < 0 || dist[i] < dist[u]))
                {
                    u = i;
                }
            }

            if (u < 0 || float.IsPositiveInfinity(dist[u]) || u == b)
            {
                break;
            }

            done[u] = true;
            foreach (var v in _adjacent[u])
            {
                var w = dist[u] + MathF.Sqrt(Square(_nodes[u].X - _nodes[v].X) + Square(_nodes[u].Z - _nodes[v].Z));
                if (w < dist[v])
                {
                    dist[v] = w;
                    prev[v] = u;
                }
            }
        }

        if (a != b && prev[b] < 0)
        {
            return null;
        }

        var path = new List<(float X, float Z)>();
        for (var n = b; n >= 0; n = n == a ? -1 : prev[n])
        {
            path.Add(_nodes[n]);
        }

        path.Reverse();
        path.Add((toX, toZ));
        return path;
    }

    private static float Square(float v) => v * v;
}
