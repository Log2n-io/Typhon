using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace Typhon.ARPG.Schema;

/// <summary>
/// Gatherable world resource: ore veins, herb patches, trees, gemstone deposits.
/// Paired with Position. Players harvest these to obtain crafting materials.
/// </summary>
[Component("ARPG.ResourceNode", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct ResourceNode
{
    [Field] [Index(AllowMultiple = true)] public int ResourceTypeId;

    [Field] public int CurrentAmount;
    [Field] public int MaxAmount;
    [Field] public float RespawnTimeSec;

    [Field] public int HarvestSkillReq;
    [Field] public float HarvestTimeBase;

    [Field] public bool IsDepleted;

    [Field] public long LastHarvestTick;
}
