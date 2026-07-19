using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace Typhon.ARPG.Schema;

/// <summary>
/// Crafting recipe definition: what materials go in, what item comes out.
/// Static data — loaded once, queried by ID or required station type.
/// Up to 4 input material slots (most ARPG recipes use 1-4 ingredients).
/// </summary>
[Component("ARPG.CraftingRecipe", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct CraftingRecipe
{
    [Field] /*[Index]*/ public int RecipeId;
    [Field] public String64 RecipeName;

    [Field] /*[Index(AllowMultiple = true)]*/ public int RequiredStationTypeId;
    [Field] public float CraftingTimeSec;
    [Field] public int SkillLevelReq;

    // Output
    [Field] public int OutputItemTypeId;
    [Field] public int OutputCount;

    // Input slot 1 (0 = unused)
    [Field] public int Input1TypeId;
    [Field] public int Input1Count;

    // Input slot 2
    [Field] public int Input2TypeId;
    [Field] public int Input2Count;

    // Input slot 3
    [Field] public int Input3TypeId;
    [Field] public int Input3Count;

    // Input slot 4
    [Field] public int Input4TypeId;
    [Field] public int Input4Count;

    [Field] /*[Index]*/ public bool IsDiscovered;
}
