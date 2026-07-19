using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace Typhon.ARPG.Schema;

/// <summary>
/// Player hotbar: 6 equipped skill slots with cooldown tracking.
/// Only on player character entities.
/// </summary>
[Component("ARPG.ActiveSkills", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct ActiveSkills
{
    // Skill definition IDs (0 = empty slot)
    [Field] public int Skill1Id;
    [Field] public int Skill2Id;
    [Field] public int Skill3Id;
    [Field] public int Skill4Id;
    [Field] public int Skill5Id;
    [Field] public int Skill6Id;

    // Cooldowns remaining (milliseconds, 0 = ready)
    [Field] public int Skill1Cooldown;
    [Field] public int Skill2Cooldown;
    [Field] public int Skill3Cooldown;
    [Field] public int Skill4Cooldown;
    [Field] public int Skill5Cooldown;
    [Field] public int Skill6Cooldown;

    [Field] public long LastCastTick;
}
