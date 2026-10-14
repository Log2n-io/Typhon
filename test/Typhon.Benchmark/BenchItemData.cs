using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace Bench;

/// <summary>
/// Item data for <see cref="Typhon.Benchmark.QueryViewBenchmarks"/>. Local copy of the single struct that benchmark
/// needs — carried here so the benchmark owns its schema instead of depending on a whole sample/schema project
/// (a former example schema project was retired in #531). Benchmarks always regenerate their database, so the
/// standalone identity is safe.
/// </summary>
// TYPHON013 suppressed deliberately: 136 bytes is past the 112 a ComponentValue carries inline, so this is spawned in two steps - empty Spawn
// then a write through OpenMut, see QueryViewBenchmarks.Setup. Pack = 4 would bring it to 132, still over, and splitting it would change what the
// benchmark measures: the four [Index] fields, the 64-byte name and the stat block are one row on purpose, because the queries under test filter
// on the keys and read the whole row. A benchmark whose subject changed would have a history nobody can compare against.
#pragma warning disable TYPHON013
[Component("Bench.ItemData", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct ItemData
{
    [Field] [Index(AllowMultiple = true)] public int ItemTypeId;
    [Field] public String64 ItemName;
    [Field] [Index(AllowMultiple = true)] public int Rarity;
    [Field] [Index(AllowMultiple = true)] public int ItemCategory;

    [Field] [Index(AllowMultiple = true)] public long OwnerId;

    [Field] public int ItemLevel;
    [Field] public int RequiredLevel;

    // Stacking (materials/consumables stack, equipment doesn't)
    [Field] public int StackCount;
    [Field] public int MaxStack;

    [Field] public bool IsEquipped;

    // Ground drop location (when OwnerId == 0)
    [Field] public Point3F DropLocation;

    // Base stats (for equipment; 0 for non-equipment)
    [Field] public int BaseMinDamage;
    [Field] public int BaseMaxDamage;
    [Field] public int BaseArmor;
    [Field] public int BaseBlockChance;
}
#pragma warning restore TYPHON013
