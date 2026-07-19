using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace Typhon.ARPG.Schema;

/// <summary>
/// A temporary buff or debuff on an entity. Single instance per entity — component-level multi-instance (AllowMultiple) was removed.
/// </summary>
[Component("ARPG.StatusEffects", 1)]
[StructLayout(LayoutKind.Sequential)]
public partial struct StatusEffects
{
    [Field] /*[Index(AllowMultiple = true)]*/ public int EffectTypeId;

    [Field] public long TargetEntityId;
    [Field] public long SourceEntityId;

    [Field] public int StackCount;
    [Field] public long ExpirationTick;

    // Effect parameters
    [Field] public int DamagePerTick;
    [Field] public int StatModifier;
    [Field] public int TickIntervalMs;
}
