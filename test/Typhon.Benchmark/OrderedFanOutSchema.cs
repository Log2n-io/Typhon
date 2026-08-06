using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace Typhon.Benchmark;

// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// Ordered-query fan-out profile (#678 steps 6-8).
//
// Rebuilds the measurement that produced the 22-33x figure in claude/design/Indexing/index-scope-and-uniqueness.md
// §3.2, whose original probe (scratch/fanout/) was deleted with its worktree. Four independent archetype trees of
// K = 1 / 4 / 16 / 64 archetypes hold the SAME total entity count, so K is the only variable.
//
// Both index shapes are measured, because they take different code paths and only one of them is easy to stream:
//   - UKey  is a UNIQUE index          -> BTree.RangeEnumerator
//   - Score is an AllowMultiple index  -> BTree.RangeMultipleEnumerator (+ VariableSizedBufferAccessor)
//
// Keys are assigned round-robin across the archetypes of a tree, so every archetype's key range spans the whole
// domain. That is deliberate: it is the case where §4.4's disjoint-range collapse and deferred cursor opening can
// never fire, so the numbers isolate the merge itself.
//
// Run: dotnet run -c Release -- --profile-fanout
// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>Component carrying one unique and one AllowMultiple indexed field. SingleVersion keeps it cluster-eligible.</summary>
[Component("Typhon.Benchmark.FoData", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct FoData
{
    /// <summary>Globally unique -> the per-archetype tree is a unique tree, driving <c>BTree.RangeEnumerator</c>.</summary>
    [Field] [Index] public int UKey;

    /// <summary>Duplicated across entities -> drives <c>BTree.RangeMultipleEnumerator</c> and the VSBS value buffers.</summary>
    [Field] [Index(AllowMultiple = true)] public int Score;

    public long Pad;

    public FoData(int uKey, int score)
    {
        UKey = uKey;
        Score = score;
        Pad = 0;
    }
}

// ── K = 1 ────────────────────────────────────────────────────────────

[Archetype]
class FoK1Root : Archetype<FoK1Root>
{
    public static readonly Comp<FoData> Data = Register<FoData>();
}

// ── K = 4 ────────────────────────────────────────────────────────────

[Archetype]
class FoK4Root : Archetype<FoK4Root>
{
    public static readonly Comp<FoData> Data = Register<FoData>();
}
[Archetype] class FoK4A1 : Archetype<FoK4A1, FoK4Root> { }
[Archetype] class FoK4A2 : Archetype<FoK4A2, FoK4Root> { }
[Archetype] class FoK4A3 : Archetype<FoK4A3, FoK4Root> { }

// ── K = 16 ────────────────────────────────────────────────────────────

[Archetype]
class FoK16Root : Archetype<FoK16Root>
{
    public static readonly Comp<FoData> Data = Register<FoData>();
}
[Archetype] class FoK16A1 : Archetype<FoK16A1, FoK16Root> { }
[Archetype] class FoK16A2 : Archetype<FoK16A2, FoK16Root> { }
[Archetype] class FoK16A3 : Archetype<FoK16A3, FoK16Root> { }
[Archetype] class FoK16A4 : Archetype<FoK16A4, FoK16Root> { }
[Archetype] class FoK16A5 : Archetype<FoK16A5, FoK16Root> { }
[Archetype] class FoK16A6 : Archetype<FoK16A6, FoK16Root> { }
[Archetype] class FoK16A7 : Archetype<FoK16A7, FoK16Root> { }
[Archetype] class FoK16A8 : Archetype<FoK16A8, FoK16Root> { }
[Archetype] class FoK16A9 : Archetype<FoK16A9, FoK16Root> { }
[Archetype] class FoK16A10 : Archetype<FoK16A10, FoK16Root> { }
[Archetype] class FoK16A11 : Archetype<FoK16A11, FoK16Root> { }
[Archetype] class FoK16A12 : Archetype<FoK16A12, FoK16Root> { }
[Archetype] class FoK16A13 : Archetype<FoK16A13, FoK16Root> { }
[Archetype] class FoK16A14 : Archetype<FoK16A14, FoK16Root> { }
[Archetype] class FoK16A15 : Archetype<FoK16A15, FoK16Root> { }

// ── K = 64 ────────────────────────────────────────────────────────────

[Archetype]
class FoK64Root : Archetype<FoK64Root>
{
    public static readonly Comp<FoData> Data = Register<FoData>();
}
[Archetype] class FoK64A1 : Archetype<FoK64A1, FoK64Root> { }
[Archetype] class FoK64A2 : Archetype<FoK64A2, FoK64Root> { }
[Archetype] class FoK64A3 : Archetype<FoK64A3, FoK64Root> { }
[Archetype] class FoK64A4 : Archetype<FoK64A4, FoK64Root> { }
[Archetype] class FoK64A5 : Archetype<FoK64A5, FoK64Root> { }
[Archetype] class FoK64A6 : Archetype<FoK64A6, FoK64Root> { }
[Archetype] class FoK64A7 : Archetype<FoK64A7, FoK64Root> { }
[Archetype] class FoK64A8 : Archetype<FoK64A8, FoK64Root> { }
[Archetype] class FoK64A9 : Archetype<FoK64A9, FoK64Root> { }
[Archetype] class FoK64A10 : Archetype<FoK64A10, FoK64Root> { }
[Archetype] class FoK64A11 : Archetype<FoK64A11, FoK64Root> { }
[Archetype] class FoK64A12 : Archetype<FoK64A12, FoK64Root> { }
[Archetype] class FoK64A13 : Archetype<FoK64A13, FoK64Root> { }
[Archetype] class FoK64A14 : Archetype<FoK64A14, FoK64Root> { }
[Archetype] class FoK64A15 : Archetype<FoK64A15, FoK64Root> { }
[Archetype] class FoK64A16 : Archetype<FoK64A16, FoK64Root> { }
[Archetype] class FoK64A17 : Archetype<FoK64A17, FoK64Root> { }
[Archetype] class FoK64A18 : Archetype<FoK64A18, FoK64Root> { }
[Archetype] class FoK64A19 : Archetype<FoK64A19, FoK64Root> { }
[Archetype] class FoK64A20 : Archetype<FoK64A20, FoK64Root> { }
[Archetype] class FoK64A21 : Archetype<FoK64A21, FoK64Root> { }
[Archetype] class FoK64A22 : Archetype<FoK64A22, FoK64Root> { }
[Archetype] class FoK64A23 : Archetype<FoK64A23, FoK64Root> { }
[Archetype] class FoK64A24 : Archetype<FoK64A24, FoK64Root> { }
[Archetype] class FoK64A25 : Archetype<FoK64A25, FoK64Root> { }
[Archetype] class FoK64A26 : Archetype<FoK64A26, FoK64Root> { }
[Archetype] class FoK64A27 : Archetype<FoK64A27, FoK64Root> { }
[Archetype] class FoK64A28 : Archetype<FoK64A28, FoK64Root> { }
[Archetype] class FoK64A29 : Archetype<FoK64A29, FoK64Root> { }
[Archetype] class FoK64A30 : Archetype<FoK64A30, FoK64Root> { }
[Archetype] class FoK64A31 : Archetype<FoK64A31, FoK64Root> { }
[Archetype] class FoK64A32 : Archetype<FoK64A32, FoK64Root> { }
[Archetype] class FoK64A33 : Archetype<FoK64A33, FoK64Root> { }
[Archetype] class FoK64A34 : Archetype<FoK64A34, FoK64Root> { }
[Archetype] class FoK64A35 : Archetype<FoK64A35, FoK64Root> { }
[Archetype] class FoK64A36 : Archetype<FoK64A36, FoK64Root> { }
[Archetype] class FoK64A37 : Archetype<FoK64A37, FoK64Root> { }
[Archetype] class FoK64A38 : Archetype<FoK64A38, FoK64Root> { }
[Archetype] class FoK64A39 : Archetype<FoK64A39, FoK64Root> { }
[Archetype] class FoK64A40 : Archetype<FoK64A40, FoK64Root> { }
[Archetype] class FoK64A41 : Archetype<FoK64A41, FoK64Root> { }
[Archetype] class FoK64A42 : Archetype<FoK64A42, FoK64Root> { }
[Archetype] class FoK64A43 : Archetype<FoK64A43, FoK64Root> { }
[Archetype] class FoK64A44 : Archetype<FoK64A44, FoK64Root> { }
[Archetype] class FoK64A45 : Archetype<FoK64A45, FoK64Root> { }
[Archetype] class FoK64A46 : Archetype<FoK64A46, FoK64Root> { }
[Archetype] class FoK64A47 : Archetype<FoK64A47, FoK64Root> { }
[Archetype] class FoK64A48 : Archetype<FoK64A48, FoK64Root> { }
[Archetype] class FoK64A49 : Archetype<FoK64A49, FoK64Root> { }
[Archetype] class FoK64A50 : Archetype<FoK64A50, FoK64Root> { }
[Archetype] class FoK64A51 : Archetype<FoK64A51, FoK64Root> { }
[Archetype] class FoK64A52 : Archetype<FoK64A52, FoK64Root> { }
[Archetype] class FoK64A53 : Archetype<FoK64A53, FoK64Root> { }
[Archetype] class FoK64A54 : Archetype<FoK64A54, FoK64Root> { }
[Archetype] class FoK64A55 : Archetype<FoK64A55, FoK64Root> { }
[Archetype] class FoK64A56 : Archetype<FoK64A56, FoK64Root> { }
[Archetype] class FoK64A57 : Archetype<FoK64A57, FoK64Root> { }
[Archetype] class FoK64A58 : Archetype<FoK64A58, FoK64Root> { }
[Archetype] class FoK64A59 : Archetype<FoK64A59, FoK64Root> { }
[Archetype] class FoK64A60 : Archetype<FoK64A60, FoK64Root> { }
[Archetype] class FoK64A61 : Archetype<FoK64A61, FoK64Root> { }
[Archetype] class FoK64A62 : Archetype<FoK64A62, FoK64Root> { }
[Archetype] class FoK64A63 : Archetype<FoK64A63, FoK64Root> { }

public static partial class OrderedFanOutSchema
{
    /// <summary>Spawns one entity into the <paramref name="index"/>-th archetype of the K-tree. A switch, not reflection — the Spawn generic is resolved at compile time.</summary>
    public static EntityId SpawnK1(Transaction tx, int index, in FoData d) => index switch
    {
        0 => tx.Spawn<FoK1Root>(FoK1Root.Data.Set(in d)),
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    public static EntityId SpawnK4(Transaction tx, int index, in FoData d) => index switch
    {
        0 => tx.Spawn<FoK4Root>(FoK4Root.Data.Set(in d)),
        1 => tx.Spawn<FoK4A1>(FoK4Root.Data.Set(in d)),
        2 => tx.Spawn<FoK4A2>(FoK4Root.Data.Set(in d)),
        3 => tx.Spawn<FoK4A3>(FoK4Root.Data.Set(in d)),
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    public static EntityId SpawnK16(Transaction tx, int index, in FoData d) => index switch
    {
        0 => tx.Spawn<FoK16Root>(FoK16Root.Data.Set(in d)),
        1 => tx.Spawn<FoK16A1>(FoK16Root.Data.Set(in d)),
        2 => tx.Spawn<FoK16A2>(FoK16Root.Data.Set(in d)),
        3 => tx.Spawn<FoK16A3>(FoK16Root.Data.Set(in d)),
        4 => tx.Spawn<FoK16A4>(FoK16Root.Data.Set(in d)),
        5 => tx.Spawn<FoK16A5>(FoK16Root.Data.Set(in d)),
        6 => tx.Spawn<FoK16A6>(FoK16Root.Data.Set(in d)),
        7 => tx.Spawn<FoK16A7>(FoK16Root.Data.Set(in d)),
        8 => tx.Spawn<FoK16A8>(FoK16Root.Data.Set(in d)),
        9 => tx.Spawn<FoK16A9>(FoK16Root.Data.Set(in d)),
        10 => tx.Spawn<FoK16A10>(FoK16Root.Data.Set(in d)),
        11 => tx.Spawn<FoK16A11>(FoK16Root.Data.Set(in d)),
        12 => tx.Spawn<FoK16A12>(FoK16Root.Data.Set(in d)),
        13 => tx.Spawn<FoK16A13>(FoK16Root.Data.Set(in d)),
        14 => tx.Spawn<FoK16A14>(FoK16Root.Data.Set(in d)),
        15 => tx.Spawn<FoK16A15>(FoK16Root.Data.Set(in d)),
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    public static EntityId SpawnK64(Transaction tx, int index, in FoData d) => index switch
    {
        0 => tx.Spawn<FoK64Root>(FoK64Root.Data.Set(in d)),
        1 => tx.Spawn<FoK64A1>(FoK64Root.Data.Set(in d)),
        2 => tx.Spawn<FoK64A2>(FoK64Root.Data.Set(in d)),
        3 => tx.Spawn<FoK64A3>(FoK64Root.Data.Set(in d)),
        4 => tx.Spawn<FoK64A4>(FoK64Root.Data.Set(in d)),
        5 => tx.Spawn<FoK64A5>(FoK64Root.Data.Set(in d)),
        6 => tx.Spawn<FoK64A6>(FoK64Root.Data.Set(in d)),
        7 => tx.Spawn<FoK64A7>(FoK64Root.Data.Set(in d)),
        8 => tx.Spawn<FoK64A8>(FoK64Root.Data.Set(in d)),
        9 => tx.Spawn<FoK64A9>(FoK64Root.Data.Set(in d)),
        10 => tx.Spawn<FoK64A10>(FoK64Root.Data.Set(in d)),
        11 => tx.Spawn<FoK64A11>(FoK64Root.Data.Set(in d)),
        12 => tx.Spawn<FoK64A12>(FoK64Root.Data.Set(in d)),
        13 => tx.Spawn<FoK64A13>(FoK64Root.Data.Set(in d)),
        14 => tx.Spawn<FoK64A14>(FoK64Root.Data.Set(in d)),
        15 => tx.Spawn<FoK64A15>(FoK64Root.Data.Set(in d)),
        16 => tx.Spawn<FoK64A16>(FoK64Root.Data.Set(in d)),
        17 => tx.Spawn<FoK64A17>(FoK64Root.Data.Set(in d)),
        18 => tx.Spawn<FoK64A18>(FoK64Root.Data.Set(in d)),
        19 => tx.Spawn<FoK64A19>(FoK64Root.Data.Set(in d)),
        20 => tx.Spawn<FoK64A20>(FoK64Root.Data.Set(in d)),
        21 => tx.Spawn<FoK64A21>(FoK64Root.Data.Set(in d)),
        22 => tx.Spawn<FoK64A22>(FoK64Root.Data.Set(in d)),
        23 => tx.Spawn<FoK64A23>(FoK64Root.Data.Set(in d)),
        24 => tx.Spawn<FoK64A24>(FoK64Root.Data.Set(in d)),
        25 => tx.Spawn<FoK64A25>(FoK64Root.Data.Set(in d)),
        26 => tx.Spawn<FoK64A26>(FoK64Root.Data.Set(in d)),
        27 => tx.Spawn<FoK64A27>(FoK64Root.Data.Set(in d)),
        28 => tx.Spawn<FoK64A28>(FoK64Root.Data.Set(in d)),
        29 => tx.Spawn<FoK64A29>(FoK64Root.Data.Set(in d)),
        30 => tx.Spawn<FoK64A30>(FoK64Root.Data.Set(in d)),
        31 => tx.Spawn<FoK64A31>(FoK64Root.Data.Set(in d)),
        32 => tx.Spawn<FoK64A32>(FoK64Root.Data.Set(in d)),
        33 => tx.Spawn<FoK64A33>(FoK64Root.Data.Set(in d)),
        34 => tx.Spawn<FoK64A34>(FoK64Root.Data.Set(in d)),
        35 => tx.Spawn<FoK64A35>(FoK64Root.Data.Set(in d)),
        36 => tx.Spawn<FoK64A36>(FoK64Root.Data.Set(in d)),
        37 => tx.Spawn<FoK64A37>(FoK64Root.Data.Set(in d)),
        38 => tx.Spawn<FoK64A38>(FoK64Root.Data.Set(in d)),
        39 => tx.Spawn<FoK64A39>(FoK64Root.Data.Set(in d)),
        40 => tx.Spawn<FoK64A40>(FoK64Root.Data.Set(in d)),
        41 => tx.Spawn<FoK64A41>(FoK64Root.Data.Set(in d)),
        42 => tx.Spawn<FoK64A42>(FoK64Root.Data.Set(in d)),
        43 => tx.Spawn<FoK64A43>(FoK64Root.Data.Set(in d)),
        44 => tx.Spawn<FoK64A44>(FoK64Root.Data.Set(in d)),
        45 => tx.Spawn<FoK64A45>(FoK64Root.Data.Set(in d)),
        46 => tx.Spawn<FoK64A46>(FoK64Root.Data.Set(in d)),
        47 => tx.Spawn<FoK64A47>(FoK64Root.Data.Set(in d)),
        48 => tx.Spawn<FoK64A48>(FoK64Root.Data.Set(in d)),
        49 => tx.Spawn<FoK64A49>(FoK64Root.Data.Set(in d)),
        50 => tx.Spawn<FoK64A50>(FoK64Root.Data.Set(in d)),
        51 => tx.Spawn<FoK64A51>(FoK64Root.Data.Set(in d)),
        52 => tx.Spawn<FoK64A52>(FoK64Root.Data.Set(in d)),
        53 => tx.Spawn<FoK64A53>(FoK64Root.Data.Set(in d)),
        54 => tx.Spawn<FoK64A54>(FoK64Root.Data.Set(in d)),
        55 => tx.Spawn<FoK64A55>(FoK64Root.Data.Set(in d)),
        56 => tx.Spawn<FoK64A56>(FoK64Root.Data.Set(in d)),
        57 => tx.Spawn<FoK64A57>(FoK64Root.Data.Set(in d)),
        58 => tx.Spawn<FoK64A58>(FoK64Root.Data.Set(in d)),
        59 => tx.Spawn<FoK64A59>(FoK64Root.Data.Set(in d)),
        60 => tx.Spawn<FoK64A60>(FoK64Root.Data.Set(in d)),
        61 => tx.Spawn<FoK64A61>(FoK64Root.Data.Set(in d)),
        62 => tx.Spawn<FoK64A62>(FoK64Root.Data.Set(in d)),
        63 => tx.Spawn<FoK64A63>(FoK64Root.Data.Set(in d)),
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

}
