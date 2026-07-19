using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace Typhon.ARPG.Schema;

/// <summary>
/// AI behavior state machine for monsters and NPCs.
/// Drives aggro, pathing, ability selection, and leashing.
/// </summary>
[Component("ARPG.MonsterAI", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct MonsterAI
{
    [Field] /*[Index(AllowMultiple = true)]*/ public int AIArchetypeId;

    [Field] public int BehaviorState;
    [Field] public long TargetEntityId;

    [Field] public Point3F HomePosition;
    [Field] public float AggroRange;
    [Field] public float LeashRange;

    [Field] public long LastActionTick;
    [Field] public int CurrentAbilityId;

    [Field] public bool IsElite;
    [Field] public bool IsBoss;
}
