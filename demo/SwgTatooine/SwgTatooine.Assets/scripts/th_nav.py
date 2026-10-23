import math
import numpy as np

# Interior navigation (ASSET_FORMAT.md 5.9): descriptor `nav` = waypoint graph + cell lookup, glTF frame, feet height.
# Plain Python + numpy, no bpy: exec'd by th_export.py, which passes the colliders as collider_obbs() gives them
# ([(name, centre, axes, half)]) and its BODY_* constants.
#
#   nodes  hand-placed (NAV_* empties under the asset root, ordered by name)
#   edges  every node pair the body walks straight between without touching a collider (derived)
#   cells  a grid over the collision footprint: for each cell, the node a body standing ANYWHERE in it (sampled
#          every NAV_SUB_M) walks straight to without touching a collider; -1 where the body fits nowhere (derived)
# So a bot looks its cell up, walks to that node, follows the shortest edge path, and walks straight off the last
# node to its goal's cell (the same guarantee read backwards). Refused: an unreachable cell, a disconnected graph.

NAV_CELL_M = 0.5
NAV_SUB_M = 0.1             # body positions sampled per cell, this far apart (and NAV_SUB_M / 2 off the cell edges)
NAV_SAMPLE_M = 0.05         # along a straight walk


def body_hits(feet, obbs, half, step):
    """feet (n, 3), glTF frame -> bool (n,): the body box (plan half-extent half[0]/half[2], from `step` above the
    feet, vertical half-extent half[1]) overlaps a collider. Separating-axis test, vectorised over positions."""
    ca = feet + [0.0, step + half[1], 0.0]
    ha = np.asarray(half)
    hit = np.zeros(len(feet), bool)
    for _, cb, rb, hb in obbs:
        t = cb - ca                                               # (n, 3)
        ar = np.abs(rb) + 1e-9
        sep = np.zeros(len(feet), bool)
        for i in range(3):
            sep |= np.abs(t[:, i]) > ha[i] + ar[i] @ hb + 1e-6
        for j in range(3):
            sep |= np.abs(t @ rb[:, j]) > ha @ ar[:, j] + hb[j] + 1e-6
        for i in range(3):
            i1, i2 = (i + 1) % 3, (i + 2) % 3
            for j in range(3):
                j1, j2 = (j + 1) % 3, (j + 2) % 3
                sep |= (np.abs(t[:, i2] * rb[i1, j] - t[:, i1] * rb[i2, j])
                        > ha[i1] * ar[i2, j] + ha[i2] * ar[i1, j] + hb[j1] * ar[i, j2] + hb[j2] * ar[i, j1] + 1e-6)
        hit |= ~sep
    return hit


def walk_clear(starts, end, obbs, half, step):
    """True if the body walks straight from every one of starts (n, 3) to end without touching a collider."""
    end = np.asarray(end, float)
    n = max(1, math.ceil(np.max(np.linalg.norm(starts - end, axis=1)) / NAV_SAMPLE_M))
    f = np.linspace(0.0, 1.0, n + 1)[:, None, None]
    pts = (starts[None] + (end - starts)[None] * f).reshape(-1, 3)
    return not body_hits(pts, obbs, half, step).any()


def nav_block(name, nodes, obbs, footprint, half, step):
    """nodes [[x, y, z]], footprint the collision bounds [[x0, y0, z0], [x1, y1, z1]] -> the descriptor's `nav`.
    Raises if a node is blocked, the graph is disconnected or a cell where the body fits reaches no node."""
    P = np.array(nodes, float)
    bad = body_hits(P, obbs, half, step)
    if bad.any():
        raise ValueError(f"{name}: nav nodes {np.flatnonzero(bad).tolist()} are inside a collider")
    edges = [[i, j] for i in range(len(P)) for j in range(i + 1, len(P)) if walk_clear(P[i:i + 1], P[j], obbs, half, step)]
    seen, todo = {0}, [0]
    while todo:
        i = todo.pop()
        for a, b in edges:
            for u, v in ((a, b), (b, a)):
                if u == i and v not in seen:
                    seen.add(v); todo.append(v)
    if len(seen) != len(P):
        raise ValueError(f"{name}: nav nodes {sorted(set(range(len(P))) - seen)} are not connected to node 0")

    # body positions on a NAV_SUB_M grid, kept if they connect to a node through free neighbours: the floor slab
    # reaches under the walls, so the strips outside them are free but not part of the interior
    (x0, _, z0), (x1, _, z1) = footprint
    ox, oz = math.floor(x0 / NAV_CELL_M) * NAV_CELL_M, math.floor(z0 / NAV_CELL_M) * NAV_CELL_M
    cols, rows = math.ceil((x1 - ox) / NAV_CELL_M - 1e-9), math.ceil((z1 - oz) / NAV_CELL_M - 1e-9)
    k = round(NAV_CELL_M / NAV_SUB_M)
    y = float(np.mean(P[:, 1]))                                   # interiors are flat
    gx, gz = ox + (np.arange(cols * k) + 0.5) * NAV_SUB_M, oz + (np.arange(rows * k) + 0.5) * NAV_SUB_M
    mx, mz = np.meshgrid(gx, gz)                                  # [z index, x index]
    G = np.stack([mx, np.full(mx.shape, y), mz], -1)
    free = ~body_hits(G.reshape(-1, 3), obbs, half, step).reshape(mx.shape)
    inside = np.zeros_like(free)
    todo = [(int(np.argmin(np.abs(gz - p[2]))), int(np.argmin(np.abs(gx - p[0])))) for p in P]
    for a, b in todo:
        inside[a, b] = free[a, b]
    todo = [t for t in todo if inside[t]]
    while todo:
        a, b = todo.pop()
        for u, v in ((a + 1, b), (a - 1, b), (a, b + 1), (a, b - 1)):
            if 0 <= u < free.shape[0] and 0 <= v < free.shape[1] and free[u, v] and not inside[u, v]:
                inside[u, v] = True; todo.append((u, v))
    cells, unreached = [], []
    for r in range(rows):
        for c in range(cols):
            m = inside[r * k:(r + 1) * k, c * k:(c + 1) * k]
            S = G[r * k:(r + 1) * k, c * k:(c + 1) * k][m]
            if not len(S):
                cells.append(-1); continue
            centre = np.array([ox + (c + 0.5) * NAV_CELL_M, y, oz + (r + 0.5) * NAV_CELL_M])
            for i in np.argsort(np.linalg.norm(P - centre, axis=1)):
                if walk_clear(S, P[i], obbs, half, step):
                    cells.append(int(i)); break
            else:
                cells.append(-1); unreached.append([round(float(centre[0]), 2), round(float(centre[2]), 2)])
    if unreached:
        raise ValueError(f"{name}: no nav node is reachable from every body position in the cells centred on "
                         f"(x, z) {unreached}: add a node")
    return {"nodes": [[round(v, 5) + 0.0 for v in p] for p in nodes], "edges": edges,
            "cells": {"origin": [round(ox, 5), round(oz, 5)], "size": NAV_CELL_M, "cols": cols, "rows": rows,
                      "node": cells}}
