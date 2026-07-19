using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace Typhon.ARPG.Schema;

/// <summary>
/// Equipment loadout: 10 gear slots referencing ItemData entities.
/// Changing equipment triggers CombatStats recalculation.
/// Only on player characters and equipped monsters.
/// </summary>
[Component("ARPG.Equipment", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct Equipment
{
    [Field] public long WeaponId;
    [Field] public long OffhandId;
    [Field] public long HelmetId;
    [Field] public long ChestId;
    [Field] public long GlovesId;
    [Field] public long BootsId;
    [Field] public long BeltId;
    [Field] public long AmuletId;
    [Field] public long Ring1Id;
    [Field] public long Ring2Id;
}
