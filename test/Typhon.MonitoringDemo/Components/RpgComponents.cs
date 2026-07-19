using System.Runtime.InteropServices;
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace Typhon.MonitoringDemo;

// ============================================================================
// RPG Game Components
// ============================================================================
// These components model a typical RPG/MMORPG game. Each component represents
// an aspect of characters, items, quests, and combat.
// ============================================================================

/// <summary>
/// A playable or NPC character.
/// </summary>
[Component("RPG.Character", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct Character
{
    /// <summary>
    /// Character name
    /// </summary>
    [Field]
    public String64 Name;

    /// <summary>
    /// Character class (0 = Warrior, 1 = Mage, 2 = Rogue, etc.)
    /// </summary>
    [Field]
    public int Class;

    /// <summary>
    /// Character level
    /// </summary>
    [Field]
    public int Level;

    /// <summary>
    /// Experience points
    /// </summary>
    [Field]
    public long Experience;

    /// <summary>
    /// Experience needed for next level
    /// </summary>
    [Field]
    public long ExperienceToNextLevel;

    /// <summary>
    /// Current health
    /// </summary>
    [Field]
    public float Health;

    /// <summary>
    /// Maximum health
    /// </summary>
    [Field]
    public float MaxHealth;

    /// <summary>
    /// Current mana/energy
    /// </summary>
    [Field]
    public float Mana;

    /// <summary>
    /// Maximum mana/energy
    /// </summary>
    [Field]
    public float MaxMana;

    /// <summary>
    /// Is this an NPC?
    /// </summary>
    [Field]
    public bool IsNpc;

    /// <summary>
    /// Guild/faction ID (0 = none)
    /// </summary>
    [Field]
    public int GuildId;

    public static Character Create(Random rand, string name, bool isNpc = false)
    {
        var level = rand.Next(1, 100);
        var baseHealth = 100f + level * 20f;
        var baseMana = 50f + level * 10f;

        return new Character
        {
            Name = (String64)name,
            Class = rand.Next(0, 5),
            Level = level,
            Experience = rand.Next(0, 100000),
            ExperienceToNextLevel = level * 1000,
            Health = baseHealth * (0.5f + (float)rand.NextDouble() * 0.5f),
            MaxHealth = baseHealth,
            Mana = baseMana * (0.3f + (float)rand.NextDouble() * 0.7f),
            MaxMana = baseMana,
            IsNpc = isNpc,
            GuildId = isNpc ? 0 : rand.Next(0, 50)
        };
    }
}

/// <summary>
/// Character inventory (storage slots).
/// </summary>
[Component("RPG.Inventory", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct Inventory
{
    /// <summary>
    /// Owner character entity ID
    /// </summary>
    [Field]
    public long CharacterId;

    /// <summary>
    /// Item name
    /// </summary>
    [Field]
    public String64 ItemName;

    /// <summary>
    /// Item type (0 = weapon, 1 = armor, 2 = consumable, etc.)
    /// </summary>
    [Field]
    public int ItemType;

    /// <summary>
    /// Item rarity (0 = common, 1 = uncommon, 2 = rare, 3 = epic, 4 = legendary)
    /// </summary>
    [Field]
    public int Rarity;

    /// <summary>
    /// Stack count
    /// </summary>
    [Field]
    public int Quantity;

    /// <summary>
    /// Slot index (0-39 = bags, 40-49 = equipped)
    /// </summary>
    [Field]
    public int SlotIndex;

    /// <summary>
    /// Item level requirement
    /// </summary>
    [Field]
    public int LevelRequirement;

    /// <summary>
    /// Gold value
    /// </summary>
    [Field]
    public int GoldValue;

    public static Inventory Create(Random rand, long characterId, int slot)
    {
        var items = new[] { "Sword", "Staff", "Dagger", "Helm", "Chestplate", "Potion", "Scroll", "Ring" };
        var item = items[rand.Next(items.Length)];

        return new Inventory
        {
            CharacterId = characterId,
            ItemName = (String64)item,
            ItemType = rand.Next(0, 5),
            Rarity = rand.Next(0, 5),
            Quantity = rand.Next(1, 20),
            SlotIndex = slot,
            LevelRequirement = rand.Next(1, 60),
            GoldValue = rand.Next(1, 10000)
        };
    }
}

/// <summary>
/// Equipment worn by a character.
/// </summary>
[Component("RPG.Equipment", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct Equipment
{
    /// <summary>
    /// Owner character entity ID
    /// </summary>
    [Field]
    public long CharacterId;

    /// <summary>
    /// Equipment slot (0 = head, 1 = chest, 2 = legs, etc.)
    /// </summary>
    [Field]
    public int Slot;

    /// <summary>
    /// Item entity ID
    /// </summary>
    [Field]
    public long ItemId;

    /// <summary>
    /// Armor value
    /// </summary>
    [Field]
    public int Armor;

    /// <summary>
    /// Damage bonus
    /// </summary>
    [Field]
    public int DamageBonus;

    /// <summary>
    /// Stamina bonus
    /// </summary>
    [Field]
    public int StaminaBonus;

    /// <summary>
    /// Intelligence bonus
    /// </summary>
    [Field]
    public int IntelligenceBonus;

    /// <summary>
    /// Durability remaining
    /// </summary>
    [Field]
    public int Durability;

    /// <summary>
    /// Max durability
    /// </summary>
    [Field]
    public int MaxDurability;

    public static Equipment Create(Random rand, long characterId, int slot)
    {
        return new Equipment
        {
            CharacterId = characterId,
            Slot = slot,
            ItemId = rand.Next(1, 10000),
            Armor = rand.Next(0, 500),
            DamageBonus = rand.Next(0, 200),
            StaminaBonus = rand.Next(0, 50),
            IntelligenceBonus = rand.Next(0, 50),
            Durability = rand.Next(50, 100),
            MaxDurability = 100
        };
    }
}

/// <summary>
/// A learned skill or ability.
/// </summary>
[Component("RPG.Skill", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct Skill
{
    /// <summary>
    /// Owner character entity ID
    /// </summary>
    [Field]
    public long CharacterId;

    /// <summary>
    /// Skill name
    /// </summary>
    [Field]
    public String64 Name;

    /// <summary>
    /// Skill level (1-10)
    /// </summary>
    [Field]
    public int SkillLevel;

    /// <summary>
    /// Skill type (0 = combat, 1 = magic, 2 = crafting, etc.)
    /// </summary>
    [Field]
    public int SkillType;

    /// <summary>
    /// Cooldown in seconds
    /// </summary>
    [Field]
    public float Cooldown;

    /// <summary>
    /// Mana cost
    /// </summary>
    [Field]
    public int ManaCost;

    /// <summary>
    /// Base damage/effect value
    /// </summary>
    [Field]
    public int BaseValue;

    /// <summary>
    /// Is currently on cooldown?
    /// </summary>
    [Field]
    public bool OnCooldown;

    public static Skill Create(Random rand, long characterId, string name)
    {
        return new Skill
        {
            CharacterId = characterId,
            Name = (String64)name,
            SkillLevel = rand.Next(1, 11),
            SkillType = rand.Next(0, 4),
            Cooldown = (float)(rand.NextDouble() * 30),
            ManaCost = rand.Next(0, 100),
            BaseValue = rand.Next(10, 500),
            OnCooldown = rand.Next(0, 2) == 1
        };
    }
}

/// <summary>
/// An active or completed quest.
/// </summary>
[Component("RPG.Quest", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct Quest
{
    /// <summary>
    /// Character who has this quest
    /// </summary>
    [Field]
    public long CharacterId;

    /// <summary>
    /// Quest name
    /// </summary>
    [Field]
    public String64 Name;

    /// <summary>
    /// Quest ID (for linking objectives)
    /// </summary>
    [Field]
    public int QuestId;

    /// <summary>
    /// Quest status (0 = active, 1 = completed, 2 = failed)
    /// </summary>
    [Field]
    public int Status;

    /// <summary>
    /// Current objective progress
    /// </summary>
    [Field]
    public int ObjectiveProgress;

    /// <summary>
    /// Objective target count
    /// </summary>
    [Field]
    public int ObjectiveTarget;

    /// <summary>
    /// Experience reward
    /// </summary>
    [Field]
    public int RewardXp;

    /// <summary>
    /// Gold reward
    /// </summary>
    [Field]
    public int RewardGold;

    public static Quest Create(Random rand, long characterId, int questId)
    {
        var quests = new[] { "Slay the Dragon", "Gather Herbs", "Escort the Merchant", "Find the Artifact", "Clear the Dungeon" };
        return new Quest
        {
            CharacterId = characterId,
            Name = (String64)quests[questId % quests.Length],
            QuestId = questId,
            Status = rand.Next(0, 3),
            ObjectiveProgress = rand.Next(0, 10),
            ObjectiveTarget = 10,
            RewardXp = rand.Next(100, 5000),
            RewardGold = rand.Next(10, 1000)
        };
    }
}

/// <summary>
/// World position for an entity.
/// </summary>
[Component("RPG.WorldPosition", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct WorldPosition
{
    /// <summary>
    /// Entity this position belongs to
    /// </summary>
    [Field]
    public long EntityId;

    /// <summary>
    /// World X coordinate
    /// </summary>
    [Field]
    public float X;

    /// <summary>
    /// World Y coordinate
    /// </summary>
    [Field]
    public float Y;

    /// <summary>
    /// World Z coordinate (height)
    /// </summary>
    [Field]
    public float Z;

    /// <summary>
    /// Rotation in degrees
    /// </summary>
    [Field]
    public float Rotation;

    /// <summary>
    /// Zone/map ID
    /// </summary>
    [Field]
    public int ZoneId;

    /// <summary>
    /// Movement speed
    /// </summary>
    [Field]
    public float Speed;

    /// <summary>
    /// Is currently moving?
    /// </summary>
    [Field]
    public bool IsMoving;

    public static WorldPosition Create(Random rand, long entityId, int zoneId)
    {
        return new WorldPosition
        {
            EntityId = entityId,
            X = (float)(rand.NextDouble() * 10000 - 5000),
            Y = (float)(rand.NextDouble() * 10000 - 5000),
            Z = (float)(rand.NextDouble() * 100),
            Rotation = (float)(rand.NextDouble() * 360),
            ZoneId = zoneId,
            Speed = 5f + (float)(rand.NextDouble() * 5),
            IsMoving = rand.Next(0, 2) == 1
        };
    }
}

/// <summary>
/// Combat statistics for an entity in battle.
/// </summary>
[Component("RPG.CombatStats", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct CombatStats
{
    /// <summary>
    /// Entity this belongs to
    /// </summary>
    [Field]
    public long EntityId;

    /// <summary>
    /// Attack power
    /// </summary>
    [Field]
    public int AttackPower;

    /// <summary>
    /// Defense rating
    /// </summary>
    [Field]
    public int Defense;

    /// <summary>
    /// Critical hit chance (0-100)
    /// </summary>
    [Field]
    public float CritChance;

    /// <summary>
    /// Critical damage multiplier
    /// </summary>
    [Field]
    public float CritMultiplier;

    /// <summary>
    /// Dodge chance (0-100)
    /// </summary>
    [Field]
    public float DodgeChance;

    /// <summary>
    /// Total damage dealt this session
    /// </summary>
    [Field]
    public long DamageDealt;

    /// <summary>
    /// Total damage taken this session
    /// </summary>
    [Field]
    public long DamageTaken;

    /// <summary>
    /// Kills count
    /// </summary>
    [Field]
    public int Kills;

    /// <summary>
    /// Deaths count
    /// </summary>
    [Field]
    public int Deaths;

    public static CombatStats Create(Random rand, long entityId)
    {
        return new CombatStats
        {
            EntityId = entityId,
            AttackPower = rand.Next(10, 500),
            Defense = rand.Next(5, 300),
            CritChance = (float)(rand.NextDouble() * 30),
            CritMultiplier = 1.5f + (float)(rand.NextDouble() * 1.5),
            DodgeChance = (float)(rand.NextDouble() * 20),
            DamageDealt = rand.Next(0, 1000000),
            DamageTaken = rand.Next(0, 500000),
            Kills = rand.Next(0, 1000),
            Deaths = rand.Next(0, 100)
        };
    }
}
