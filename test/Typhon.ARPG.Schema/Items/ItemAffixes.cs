using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace Typhon.ARPG.Schema;

/// <summary>
/// Randomly-rolled item modifier (prefix or suffix). Single instance per item — component-level multi-instance (AllowMultiple) was removed.
/// </summary>
[Component("ARPG.ItemAffixes", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct ItemAffixes
{
    [Field] /*[Index(AllowMultiple = true)]*/ public int AffixTypeId;

    [Field] public int MinValue;
    [Field] public int MaxValue;
    [Field] public int RolledValue;
}
