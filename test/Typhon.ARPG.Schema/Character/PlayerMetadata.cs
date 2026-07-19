using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace Typhon.ARPG.Schema;

/// <summary>
/// Player identity, account linkage, and meta-progression.
/// Only on player character entities (not monsters or NPCs).
/// </summary>
[Component("ARPG.PlayerMetadata", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct PlayerMetadata
{
    [Field] public String64 CharacterName;

    [Field] /*[Index]*/ public long AccountId;
    [Field] /*[Index(AllowMultiple = true)]*/ public int CharacterClass;

    [Field] public long CreationTimestamp;
    [Field] public long LastLoginTimestamp;
    [Field] public int PlayTimeSeconds;

    [Field] public int GoldAmount;
    [Field] public int CraftingLevel;
    [Field] public int DeathCount;

    [Field] public bool IsHardcore;
    [Field] public bool IsOnline;
}
