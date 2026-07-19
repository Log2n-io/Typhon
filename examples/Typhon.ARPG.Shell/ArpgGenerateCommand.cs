using System;
using System.Diagnostics;
using Typhon.ARPG.Schema;
using Typhon.Schema.Definition;
using Typhon.Shell.Extensibility;

// Disambiguate ARPG types from engine types with the same name
using ResourceNode = Typhon.ARPG.Schema.ResourceNode;

namespace Typhon.ARPG.Shell;

// ── ARPG Archetypes ──────────────────────────────────────────────────
// IDs 400+ to avoid collisions with test archetypes (200-356) and engine archetypes (100-101)

[Archetype(400)]
class CharacterArch : Archetype<CharacterArch>
{
    public static readonly Comp<CharacterStats> Stats = Register<CharacterStats>();
    public static readonly Comp<PlayerMetadata> Meta = Register<PlayerMetadata>();
}

[Archetype(401)]
class EquipmentArch : Archetype<EquipmentArch>
{
    public static readonly Comp<Equipment> Equip = Register<Equipment>();
}

[Archetype(402)]
class ActiveSkillsArch : Archetype<ActiveSkillsArch>
{
    public static readonly Comp<ActiveSkills> Skills = Register<ActiveSkills>();
}

[Archetype(403)]
class PositionArch : Archetype<PositionArch>
{
    public static readonly Comp<Position> Pos = Register<Position>();
}

[Archetype(404)]
class ResourceNodeArch : Archetype<ResourceNodeArch>
{
    public static readonly Comp<ResourceNode> Node = Register<ResourceNode>();
}

[Archetype(405)]
class ItemDataArch : Archetype<ItemDataArch>
{
    public static readonly Comp<ItemData> Item = Register<ItemData>();
}

[Archetype(406)]
class MonsterArch : Archetype<MonsterArch>
{
    public static readonly Comp<CharacterStats> Stats = Register<CharacterStats>();
    public static readonly Comp<CombatStats> Combat = Register<CombatStats>();
}

[Archetype(407)]
class MonsterAIArch : Archetype<MonsterAIArch>
{
    public static readonly Comp<MonsterAI> AI = Register<MonsterAI>();
    public static readonly Comp<Position> Pos = Register<Position>();
}

[Archetype(408)]
class CraftingRecipeArch : Archetype<CraftingRecipeArch>
{
    public static readonly Comp<CraftingRecipe> Recipe = Register<CraftingRecipe>();
}

[Archetype(409)]
class CraftingStationArch : Archetype<CraftingStationArch>
{
    public static readonly Comp<CraftingStation> Station = Register<CraftingStation>();
}

/// <summary>
/// Shell command that generates ARPG game data with configurable scenarios.
/// Discovered automatically when the assembly is loaded via <c>load-schema</c>.
/// </summary>
public sealed class ArpgGenerateCommand : ShellCommand
{
    public override string Name => "arpg-generate";
    public override string Description => "Generate ARPG game data with configurable scenarios";
    public override bool RequiresDatabase => true;

    public override string DetailedHelp =>
        "arpg-generate <scenario> [count] [--seed N]\n" +
        "\n" +
        "  Scenarios:\n" +
        "    characters  Player characters with stats, metadata, equipment, skills\n" +
        "    world       World positions and resource nodes across zones\n" +
        "    items       Items with type, rarity and category\n" +
        "    monsters    Monsters with AI, combat stats, and positions\n" +
        "    crafting    Crafting recipes and stations\n" +
        "    full        All scenarios combined (default counts)\n" +
        "    stress      Large-scale stress test (~100K entities)\n" +
        "\n" +
        "  Options:\n" +
        "    count       Number of primary entities (default varies by scenario)\n" +
        "    --seed N    Random seed for reproducibility (default: 42)\n" +
        "\n" +
        "  Examples:\n" +
        "    arpg-generate characters 50\n" +
        "    arpg-generate stress 200000 --seed 123\n" +
        "    arpg-generate full";

    private const int BatchSize = 200;

    public override ShellCommandResult Execute(IShellCommandContext context, string[] args)
    {
        if (args.Length < 2)
        {
            return ShellCommandResult.Ok(DetailedHelp);
        }

        var scenario = args[1].ToLowerInvariant();
        var count = -1; // -1 means use default
        var seed = 42;

        // Parse optional arguments
        for (var i = 2; i < args.Length; i++)
        {
            if (args[i] == "--seed" && i + 1 < args.Length)
            {
                if (!int.TryParse(args[i + 1], out seed))
                {
                    return ShellCommandResult.Error($"Invalid seed value: '{args[i + 1]}'");
                }

                i++;
            }
            else if (count < 0 && int.TryParse(args[i], out var n))
            {
                count = n;
            }
            else
            {
                return ShellCommandResult.Error($"Unknown argument: '{args[i]}'");
            }
        }

        // Ensure ARPG archetypes are registered and initialized
        context.Engine.InitializeArchetypes();

        var sw = Stopwatch.StartNew();

        try
        {
            var result = scenario switch
            {
                "characters" => GenerateCharacters(context, count < 0 ? 100 : count, seed),
                "world" => GenerateWorld(context, count < 0 ? 200 : count, seed),
                "items" => GenerateItems(context, count < 0 ? 500 : count, seed),
                "monsters" => GenerateMonsters(context, count < 0 ? 300 : count, seed),
                "crafting" => GenerateCrafting(context, count < 0 ? 100 : count, seed),
                "full" => GenerateFull(context, count, seed),
                "stress" => GenerateStress(context, count < 0 ? 100_000 : count, seed),
                _ => (Entities: 0, Error: $"Unknown scenario: '{scenario}'. See 'help arpg-generate'.")
            };

            sw.Stop();

            if (result.Error != null)
            {
                return ShellCommandResult.Error(result.Error);
            }

            return ShellCommandResult.Ok($"  Generated {result.Entities:N0} entities ({scenario}) in {FormatElapsed(sw.Elapsed)}");
        }
        catch (Exception ex)
        {
            return ShellCommandResult.Error($"Generation failed: {ex}");
        }
    }

    // ── Scenarios ──────────────────────────────────────────────────

    private static (int Entities, string Error) GenerateCharacters(IShellCommandContext ctx, int count, int seed)
    {
        var rng = new Random(seed);
        var entities = 0;

        for (var batch = 0; batch < count; batch += BatchSize)
        {
            var batchCount = Math.Min(BatchSize, count - batch);
            using var uow = ctx.Engine.CreateUnitOfWork();

            for (var i = 0; i < batchCount; i++)
            {
                var tx = uow.CreateTransaction();
                var level = rng.Next(1, 100);
                var stats = MakeCharacterStats(rng, level);
                var meta = MakePlayerMetadata(rng, level);
                tx.Spawn<CharacterArch>(CharacterArch.Stats.Set(in stats), CharacterArch.Meta.Set(in meta));

                // Equipment (separate entity — different archetype)
                var equip = MakeEquipment(rng);
                tx.Spawn<EquipmentArch>(EquipmentArch.Equip.Set(in equip));

                // Active skills
                var skills = MakeActiveSkills(rng, level);
                tx.Spawn<ActiveSkillsArch>(ActiveSkillsArch.Skills.Set(in skills));

                var uowCtx = uow.CreateContext();
                tx.Commit(ref uowCtx);
                tx.Dispose();
                entities += 3;
            }
        }

        return (entities, null);
    }

    private static (int Entities, string Error) GenerateWorld(IShellCommandContext ctx, int count, int seed)
    {
        var rng = new Random(seed);
        var entities = 0;
        var zoneCount = Math.Max(1, count / 20); // ~20 entities per zone

        for (var batch = 0; batch < count; batch += BatchSize)
        {
            var batchCount = Math.Min(BatchSize, count - batch);
            using var uow = ctx.Engine.CreateUnitOfWork();

            for (var i = 0; i < batchCount; i++)
            {
                var tx = uow.CreateTransaction();
                var zoneId = rng.Next(1, zoneCount + 1);

                if (rng.Next(100) < 60)
                {
                    var pos = MakePosition(rng, zoneId);
                    tx.Spawn<PositionArch>(PositionArch.Pos.Set(in pos));
                }
                else
                {
                    var node = MakeResourceNode(rng);
                    tx.Spawn<ResourceNodeArch>(ResourceNodeArch.Node.Set(in node));
                }

                var uowCtx = uow.CreateContext();
                tx.Commit(ref uowCtx);
                tx.Dispose();
                entities++;
            }
        }

        return (entities, null);
    }

    private static (int Entities, string Error) GenerateItems(IShellCommandContext ctx, int count, int seed)
    {
        var rng = new Random(seed);
        var entities = 0;

        using var uow = ctx.Engine.CreateUnitOfWork();

        for (var batch = 0; batch < count; batch += BatchSize)
        {
            var batchCount = Math.Min(BatchSize, count - batch);
            var tx = uow.CreateTransaction();

            for (var i = 0; i < batchCount; i++)
            {
                var rarity = rng.Next(0, 5); // 0=Normal, 1=Magic, 2=Rare, 3=Unique, 4=Legendary
                var item = MakeItemData(rng, rarity);

                // AllowMultiple (affixes) retired — create base entity only
                tx.Spawn<ItemDataArch>(ItemDataArch.Item.Set(in item));

                entities++;
            }

            var uowCtx = uow.CreateContext();
            tx.Commit(ref uowCtx);
            tx.Dispose();
        }

        return (entities, null);
    }

    private static (int Entities, string Error) GenerateMonsters(IShellCommandContext ctx, int count, int seed)
    {
        var rng = new Random(seed);
        var entities = 0;

        for (var batch = 0; batch < count; batch += BatchSize)
        {
            var batchCount = Math.Min(BatchSize, count - batch);
            using var uow = ctx.Engine.CreateUnitOfWork();

            for (var i = 0; i < batchCount; i++)
            {
                var tx = uow.CreateTransaction();
                var level = rng.Next(1, 100);

                // Monster: CharacterStats + CombatStats
                var stats = MakeMonsterStats(rng, level);
                var combat = MakeCombatStats(rng, level);
                tx.Spawn<MonsterArch>(MonsterArch.Stats.Set(in stats), MonsterArch.Combat.Set(in combat));

                // MonsterAI + Position (separate entity)
                var ai = MakeMonsterAI(rng, level);
                var pos = MakePosition(rng, rng.Next(1, 20));
                tx.Spawn<MonsterAIArch>(MonsterAIArch.AI.Set(in ai), MonsterAIArch.Pos.Set(in pos));

                var uowCtx = uow.CreateContext();
                tx.Commit(ref uowCtx);
                tx.Dispose();
                entities += 2;
            }
        }

        return (entities, null);
    }

    private static (int Entities, string Error) GenerateCrafting(IShellCommandContext ctx, int count, int seed)
    {
        var rng = new Random(seed);
        var entities = 0;
        var recipeCount = count / 2;
        var stationCount = count - recipeCount;

        // Recipes
        for (var batch = 0; batch < recipeCount; batch += BatchSize)
        {
            var batchCount = Math.Min(BatchSize, recipeCount - batch);
            using var uow = ctx.Engine.CreateUnitOfWork();

            for (var i = 0; i < batchCount; i++)
            {
                var tx = uow.CreateTransaction();
                var recipe = MakeCraftingRecipe(rng, batch + i);
                tx.Spawn<CraftingRecipeArch>(CraftingRecipeArch.Recipe.Set(in recipe));

                var uowCtx = uow.CreateContext();
                tx.Commit(ref uowCtx);
                tx.Dispose();
                entities++;
            }
        }

        // Stations
        for (var batch = 0; batch < stationCount; batch += BatchSize)
        {
            var batchCount = Math.Min(BatchSize, stationCount - batch);
            using var uow = ctx.Engine.CreateUnitOfWork();

            for (var i = 0; i < batchCount; i++)
            {
                var tx = uow.CreateTransaction();
                var station = MakeCraftingStation(rng);
                tx.Spawn<CraftingStationArch>(CraftingStationArch.Station.Set(in station));

                var uowCtx = uow.CreateContext();
                tx.Commit(ref uowCtx);
                tx.Dispose();
                entities++;
            }
        }

        return (entities, null);
    }

    private static (int Entities, string Error) GenerateFull(IShellCommandContext ctx, int count, int seed)
    {
        // count == -1 means use defaults; otherwise scale proportionally
        var scale = count < 0 ? 1.0 : count / 1300.0;
        var totalEntities = 0;

        var (e1, err1) = GenerateCharacters(ctx, (int)(100 * scale), seed);
        if (err1 != null) { return (0, err1); }
        totalEntities += e1;

        var (e2, err2) = GenerateWorld(ctx, (int)(200 * scale), seed + 1);
        if (err2 != null) { return (0, err2); }
        totalEntities += e2;

        var (e3, err3) = GenerateItems(ctx, (int)(500 * scale), seed + 2);
        if (err3 != null) { return (0, err3); }
        totalEntities += e3;

        var (e4, err4) = GenerateMonsters(ctx, (int)(300 * scale), seed + 3);
        if (err4 != null) { return (0, err4); }
        totalEntities += e4;

        var (e5, err5) = GenerateCrafting(ctx, (int)(100 * scale), seed + 4);
        if (err5 != null) { return (0, err5); }
        totalEntities += e5;

        return (totalEntities, null);
    }

    private static (int Entities, string Error) GenerateStress(IShellCommandContext ctx, int totalCount, int seed)
    {
        // Distribute proportionally across all scenarios
        var totalEntities = 0;
        var charCount = totalCount / 2;     // 50% characters (3 entities each → lots of data)
        var worldCount = totalCount / 7;    // ~15%
        var itemCount = totalCount / 5;     // 20%
        var monsterCount = totalCount / 10; // 10%
        var craftCount = totalCount / 20;   // 5%

        var (e1, err1) = GenerateCharacters(ctx, charCount, seed);
        if (err1 != null) { return (0, err1); }
        totalEntities += e1;

        var (e2, err2) = GenerateWorld(ctx, worldCount, seed + 100);
        if (err2 != null) { return (0, err2); }
        totalEntities += e2;

        var (e3, err3) = GenerateItems(ctx, itemCount, seed + 200);
        if (err3 != null) { return (0, err3); }
        totalEntities += e3;

        var (e4, err4) = GenerateMonsters(ctx, monsterCount, seed + 300);
        if (err4 != null) { return (0, err4); }
        totalEntities += e4;

        var (e5, err5) = GenerateCrafting(ctx, craftCount, seed + 400);
        if (err5 != null) { return (0, err5); }
        totalEntities += e5;

        return (totalEntities, null);
    }

    // ── Data Generators ────────────────────────────────────────────

    private static readonly string[] FirstNames =
    [
        "Aric", "Brynn", "Caelum", "Dara", "Elowen", "Fenris", "Greta", "Hakon",
        "Isolde", "Jareth", "Kira", "Lyric", "Magnus", "Nyx", "Orion", "Petra",
        "Quinn", "Ragnar", "Sable", "Thane", "Ursa", "Vex", "Wren", "Xara",
        "Ymir", "Zara", "Aldric", "Bexley", "Corvus", "Dahlia"
    ];

    private static readonly string[] Suffixes =
    [
        "the Bold", "Shadowstep", "Ironheart", "Flamecaller", "Frostborn",
        "Stormbreaker", "Nightwhisper", "Bloodfang", "Starweaver", "Doomhammer"
    ];

    private static readonly string[] MonsterNames =
    [
        "Skeleton Warrior", "Zombie Shambler", "Goblin Scout", "Orc Berserker",
        "Dark Mage", "Spider Queen", "Flame Imp", "Ice Golem", "Shadow Wraith",
        "Bone Dragon", "Bandit Chief", "Forest Troll", "Cave Bat", "Poison Viper",
        "Plague Rat", "Corrupted Treant", "Blood Cultist", "Void Spawn"
    ];

    private static readonly string[] ItemBaseTypes =
    [
        "Sword", "Axe", "Mace", "Dagger", "Staff", "Bow", "Shield",
        "Helmet", "Chest Armor", "Gauntlets", "Boots", "Belt",
        "Amulet", "Ring", "Cloak", "Bracers"
    ];

    private static readonly string[] ItemPrefixes =
    [
        "Rusty", "Iron", "Steel", "Mithril", "Adamantine",          // Normal → Legendary
        "Enchanted", "Blessed", "Cursed", "Ancient", "Ethereal",
        "Glacial", "Molten", "Thundering", "Venomous", "Radiant"
    ];

    private static readonly string[] RecipeNames =
    [
        "Iron Ingot", "Steel Plate", "Mithril Wire", "Enchanted Gem",
        "Healing Potion", "Mana Elixir", "Resistance Scroll", "Sharpening Stone",
        "Frost Rune", "Fire Rune", "Lightning Rune", "Shadow Rune",
        "Leather Strip", "Woven Cloth", "Hardened Scale", "Crystal Lens",
        "Alchemist Flask", "Runic Powder", "Void Essence", "Dragon Scale"
    ];

    private static readonly string[] StationNames =
    [
        "Blacksmith Forge", "Alchemy Lab", "Enchanting Table", "Jeweler Bench",
        "Tanning Rack", "Loom", "Smelter", "Woodworking Bench",
        "Rune Carver", "Crystal Attuner"
    ];

    private static CharacterStats MakeCharacterStats(Random rng, int level)
    {
        var baseHp = 100 + level * 25;
        var baseMp = 50 + level * 12;
        return new CharacterStats
        {
            Strength = 10 + rng.Next(0, level * 2),
            Dexterity = 10 + rng.Next(0, level * 2),
            Intelligence = 10 + rng.Next(0, level * 2),
            Vitality = 10 + rng.Next(0, level * 2),
            CurrentHealth = (int)(baseHp * (0.3f + (float)rng.NextDouble() * 0.7f)),
            MaxHealth = baseHp,
            CurrentMana = (int)(baseMp * (0.2f + (float)rng.NextDouble() * 0.8f)),
            MaxMana = baseMp,
            Armor = rng.Next(0, level * 5),
            EvasionRating = rng.Next(0, level * 3),
            CriticalChance = 5.0f + (float)(rng.NextDouble() * 20.0),
            CriticalMultiplier = 1.5f + (float)(rng.NextDouble() * 1.5),
            Level = level,
            Experience = rng.Next(0, level * 1000),
            ExperienceToNextLevel = level * 1000
        };
    }

    private static PlayerMetadata MakePlayerMetadata(Random rng, int level)
    {
        var name = FirstNames[rng.Next(FirstNames.Length)];
        if (rng.Next(100) < 30)
        {
            name += " " + Suffixes[rng.Next(Suffixes.Length)];
        }

        return new PlayerMetadata
        {
            CharacterName = (String64)name,
            AccountId = rng.Next(1000, 99999),
            CharacterClass = rng.Next(0, 7), // Warrior, Mage, Ranger, Rogue, Cleric, Necromancer, Druid
            CreationTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - rng.Next(0, 365 * 24 * 3600),
            LastLoginTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - rng.Next(0, 7 * 24 * 3600),
            PlayTimeSeconds = rng.Next(3600, 500_000),
            GoldAmount = rng.Next(0, level * 500),
            CraftingLevel = rng.Next(1, Math.Min(level, 50) + 1),
            DeathCount = rng.Next(0, level * 2),
            IsHardcore = rng.Next(100) < 10,
            IsOnline = rng.Next(100) < 30
        };
    }

    private static Equipment MakeEquipment(Random rng)
    {
        // Not all slots filled — simulate incomplete loadout
        return new Equipment
        {
            WeaponId = rng.Next(100) < 80 ? rng.Next(1, 10000) : 0,
            OffhandId = rng.Next(100) < 40 ? rng.Next(1, 10000) : 0,
            HelmetId = rng.Next(100) < 70 ? rng.Next(1, 10000) : 0,
            ChestId = rng.Next(100) < 90 ? rng.Next(1, 10000) : 0,
            GlovesId = rng.Next(100) < 60 ? rng.Next(1, 10000) : 0,
            BootsId = rng.Next(100) < 70 ? rng.Next(1, 10000) : 0,
            BeltId = rng.Next(100) < 50 ? rng.Next(1, 10000) : 0,
            AmuletId = rng.Next(100) < 40 ? rng.Next(1, 10000) : 0,
            Ring1Id = rng.Next(100) < 30 ? rng.Next(1, 10000) : 0,
            Ring2Id = rng.Next(100) < 20 ? rng.Next(1, 10000) : 0
        };
    }

    private static ActiveSkills MakeActiveSkills(Random rng, int level)
    {
        var maxSkillId = level * 2 + 10;
        return new ActiveSkills
        {
            Skill1Id = rng.Next(1, maxSkillId),
            Skill2Id = rng.Next(100) < 90 ? rng.Next(1, maxSkillId) : 0,
            Skill3Id = rng.Next(100) < 80 ? rng.Next(1, maxSkillId) : 0,
            Skill4Id = rng.Next(100) < 60 ? rng.Next(1, maxSkillId) : 0,
            Skill5Id = rng.Next(100) < 40 ? rng.Next(1, maxSkillId) : 0,
            Skill6Id = rng.Next(100) < 20 ? rng.Next(1, maxSkillId) : 0,
            Skill1Cooldown = 0,
            Skill2Cooldown = 0,
            Skill3Cooldown = 0,
            Skill4Cooldown = 0,
            Skill5Cooldown = 0,
            Skill6Cooldown = 0,
            LastCastTick = 0
        };
    }

    private static Position MakePosition(Random rng, int zoneId)
    {
        // Zone-clustered: each zone occupies a 1000x1000 region
        var zoneOffsetX = (zoneId % 10) * 1000.0f;
        var zoneOffsetZ = (zoneId / 10) * 1000.0f;
        return new Position
        {
            Location = new Point3F
            {
                X = zoneOffsetX + (float)(rng.NextDouble() * 1000.0),
                Y = (float)(rng.NextDouble() * 50.0), // terrain height
                Z = zoneOffsetZ + (float)(rng.NextDouble() * 1000.0)
            },
            Rotation = new QuaternionF
            {
                X = 0, Y = (float)(rng.NextDouble() * 2.0 - 1.0), Z = 0,
                W = (float)Math.Sqrt(1.0 - rng.NextDouble() * 0.5) // roughly valid quaternion
            },
            MovementSpeed = 3.0f + (float)(rng.NextDouble() * 4.0),
            Velocity = new Point3F { X = 0, Y = 0, Z = 0 },
            ZoneId = zoneId,
            IsGrounded = rng.Next(100) < 90
        };
    }

    private static ResourceNode MakeResourceNode(Random rng)
    {
        var resourceTypes = 8; // Ore, Herbs, Wood, Gems, Fish, Clay, Crystal, Essence
        var maxAmount = rng.Next(10, 500);
        return new ResourceNode
        {
            ResourceTypeId = rng.Next(1, resourceTypes + 1),
            CurrentAmount = rng.Next(0, maxAmount + 1),
            MaxAmount = maxAmount,
            RespawnTimeSec = 30.0f + (float)(rng.NextDouble() * 570.0), // 30s to 10min
            HarvestSkillReq = rng.Next(0, 50),
            HarvestTimeBase = 1.0f + (float)(rng.NextDouble() * 9.0),
            IsDepleted = rng.Next(100) < 15,
            LastHarvestTick = rng.Next(100) < 50 ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - rng.Next(0, 600_000) : 0
        };
    }

    private static ItemData MakeItemData(Random rng, int rarity)
    {
        var baseType = ItemBaseTypes[rng.Next(ItemBaseTypes.Length)];
        var prefix = rarity > 0 ? ItemPrefixes[rng.Next(rarity * 2, Math.Min((rarity + 1) * 3, ItemPrefixes.Length))] + " " : "";
        var itemLevel = rng.Next(1, 100);
        var isWeapon = baseType is "Sword" or "Axe" or "Mace" or "Dagger" or "Staff" or "Bow";
        var category = isWeapon ? 0 : (baseType is "Shield" ? 1 : (baseType is "Amulet" or "Ring" ? 3 : 2));

        return new ItemData
        {
            ItemTypeId = rng.Next(1, 200),
            ItemName = (String64)(prefix + baseType),
            Rarity = rarity,
            ItemCategory = category,
            OwnerId = rng.Next(100) < 70 ? rng.Next(1, 50000) : 0, // 30% ground drops
            ItemLevel = itemLevel,
            RequiredLevel = Math.Max(1, itemLevel - rng.Next(0, 10)),
            StackCount = category >= 2 ? 1 : rng.Next(1, 20),
            MaxStack = category >= 2 ? 1 : 20,
            IsEquipped = rng.Next(100) < 20,
            DropLocation = new Point3F
            {
                X = (float)(rng.NextDouble() * 10000.0 - 5000.0),
                Y = (float)(rng.NextDouble() * 50.0),
                Z = (float)(rng.NextDouble() * 10000.0 - 5000.0)
            },
            BaseMinDamage = isWeapon ? rng.Next(1, itemLevel * 2 + 5) : 0,
            BaseMaxDamage = isWeapon ? rng.Next(itemLevel * 2 + 5, itemLevel * 4 + 10) : 0,
            BaseArmor = !isWeapon && category != 3 ? rng.Next(1, itemLevel * 3 + 10) : 0,
            BaseBlockChance = baseType == "Shield" ? rng.Next(10, 40) : 0
        };
    }

    private static ItemAffixes MakeItemAffix(Random rng, int rarity)
    {
        var tier = rarity + rng.Next(0, 3); // higher rarity → higher tier affixes
        var minVal = tier * 5 + rng.Next(1, 10);
        var maxVal = minVal + rng.Next(5, 30);
        return new ItemAffixes
        {
            AffixTypeId = rng.Next(1, 50), // 50 affix types: +str, +dex, +fire res, etc.
            MinValue = minVal,
            MaxValue = maxVal,
            RolledValue = rng.Next(minVal, maxVal + 1)
        };
    }

    private static CharacterStats MakeMonsterStats(Random rng, int level)
    {
        // Monsters have more HP but less varied stats
        var baseHp = 200 + level * 40;
        return new CharacterStats
        {
            Strength = 5 + level * 2,
            Dexterity = 5 + level,
            Intelligence = 3 + level / 2,
            Vitality = 10 + level * 3,
            CurrentHealth = baseHp,
            MaxHealth = baseHp,
            CurrentMana = 0,
            MaxMana = level > 20 ? 100 + level * 5 : 0,
            Armor = level * 3,
            EvasionRating = level * 2,
            CriticalChance = 2.0f + level * 0.1f,
            CriticalMultiplier = 1.5f,
            Level = level,
            Experience = level * 100, // XP reward when killed
            ExperienceToNextLevel = 0 // monsters don't level up
        };
    }

    private static CombatStats MakeCombatStats(Random rng, int level)
    {
        var baseDmg = 5 + level * 3;
        return new CombatStats
        {
            MinPhysicalDamage = baseDmg,
            MaxPhysicalDamage = baseDmg + rng.Next(5, 20 + level),
            MinFireDamage = rng.Next(100) < 30 ? rng.Next(1, level * 2 + 5) : 0,
            MaxFireDamage = rng.Next(100) < 30 ? rng.Next(level * 2 + 5, level * 4 + 10) : 0,
            MinColdDamage = rng.Next(100) < 20 ? rng.Next(1, level * 2 + 5) : 0,
            MaxColdDamage = rng.Next(100) < 20 ? rng.Next(level * 2 + 5, level * 4 + 10) : 0,
            MinLightningDamage = rng.Next(100) < 15 ? rng.Next(1, level * 2 + 5) : 0,
            MaxLightningDamage = rng.Next(100) < 15 ? rng.Next(level * 2 + 5, level * 4 + 10) : 0,
            FireResistance = rng.Next(0, 40),
            ColdResistance = rng.Next(0, 40),
            LightningResistance = rng.Next(0, 40),
            ChaosResistance = rng.Next(0, 20),
            AttackRating = 50 + level * 5,
            DefenseRating = 30 + level * 3,
            AttackSpeed = 0.8f + (float)(rng.NextDouble() * 0.8),
            CastSpeed = 0.5f + (float)(rng.NextDouble() * 0.5)
        };
    }

    private static MonsterAI MakeMonsterAI(Random rng, int level)
    {
        return new MonsterAI
        {
            AIArchetypeId = rng.Next(1, 10), // Melee, Ranged, Caster, Support, Boss, etc.
            BehaviorState = rng.Next(0, 5), // Idle, Patrol, Chase, Attack, Flee
            TargetEntityId = 0,
            HomePosition = new Point3F
            {
                X = (float)(rng.NextDouble() * 10000.0 - 5000.0),
                Y = (float)(rng.NextDouble() * 50.0),
                Z = (float)(rng.NextDouble() * 10000.0 - 5000.0)
            },
            AggroRange = 8.0f + (float)(rng.NextDouble() * 12.0),
            LeashRange = 30.0f + (float)(rng.NextDouble() * 20.0),
            LastActionTick = 0,
            CurrentAbilityId = 0,
            IsElite = rng.Next(100) < 10,
            IsBoss = rng.Next(100) < 2
        };
    }

    private static CraftingRecipe MakeCraftingRecipe(Random rng, int index)
    {
        var recipeName = RecipeNames[index % RecipeNames.Length];
        var inputCount = rng.Next(1, 5);
        return new CraftingRecipe
        {
            RecipeId = index + 1,
            RecipeName = (String64)recipeName,
            RequiredStationTypeId = rng.Next(1, 6),
            CraftingTimeSec = 2.0f + (float)(rng.NextDouble() * 28.0),
            SkillLevelReq = rng.Next(0, 50),
            OutputItemTypeId = rng.Next(1, 200),
            OutputCount = rng.Next(1, 5),
            Input1TypeId = rng.Next(1, 100),
            Input1Count = rng.Next(1, 10),
            Input2TypeId = inputCount >= 2 ? rng.Next(1, 100) : 0,
            Input2Count = inputCount >= 2 ? rng.Next(1, 5) : 0,
            Input3TypeId = inputCount >= 3 ? rng.Next(1, 100) : 0,
            Input3Count = inputCount >= 3 ? rng.Next(1, 3) : 0,
            Input4TypeId = inputCount >= 4 ? rng.Next(1, 100) : 0,
            Input4Count = inputCount >= 4 ? rng.Next(1, 3) : 0,
            IsDiscovered = rng.Next(100) < 60
        };
    }

    private static CraftingStation MakeCraftingStation(Random rng)
    {
        var stationName = StationNames[rng.Next(StationNames.Length)];
        var needsFuel = rng.Next(100) < 40;
        return new CraftingStation
        {
            StationTypeId = rng.Next(1, 6),
            StationName = (String64)stationName,
            OwnerEntityId = rng.Next(1, 50000),
            CraftingSpeedMultiplier = 0.8f + (float)(rng.NextDouble() * 0.8),
            CurrentRecipeId = rng.Next(100) < 30 ? rng.Next(1, 100) : 0,
            Progress = rng.Next(100) < 30 ? (float)rng.NextDouble() : 0.0f,
            IsAutomatic = rng.Next(100) < 20,
            FuelTypeId = needsFuel ? rng.Next(1, 5) : 0,
            FuelRemaining = needsFuel ? (float)(rng.NextDouble() * 100.0) : 0.0f,
            FuelBurnRate = needsFuel ? 0.5f + (float)(rng.NextDouble() * 2.0) : 0.0f,
            IsActive = rng.Next(100) < 60
        };
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed.TotalMilliseconds < 1000)
        {
            return $"{elapsed.TotalMilliseconds:F1}ms";
        }

        return $"{elapsed.TotalSeconds:F2}s";
    }
}
