using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace Bench;

/// <summary>
/// Item data for <see cref="Typhon.Benchmark.QueryViewBenchmarks"/>. Local copy of the single struct that benchmark
/// needs — carried here so the benchmark owns its schema instead of depending on a whole sample/schema project
/// (a former example schema project was retired in #531). Benchmarks always regenerate their database, so the
/// standalone identity is safe.
/// </summary>
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
