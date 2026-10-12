using System.Collections.Generic;
using System.Linq;
using ExileCore2.Shared.Enums;
using ExpeditionIcons.PathPlannerData;

namespace ExpeditionIcons;

public static class Icons
{
    public static IExpeditionRelic GetRelicType(string relicMod, PlannerSettings plannerSettings)
    {
        var relicDescription = ExpeditionRelicIcons.FirstOrDefault(x => x.BaseEntityMetadataSubstrings.Contains(relicMod));
        return (relicMod, relicDescription) switch
        {
            ("ExpeditionRelicUpsideElitesDuplicated", _) => new DoubledMonstersRelic(),
            (_, { IsWeightCustomizable: true, IconPickerIndex: var index, AffectsMonsters: var affectsMonsters }) when
                (plannerSettings.RelicSettingsMap.GetValueOrDefault(index) ?? RelicSettings.Default) is var setting =>
                new ConfigurableRelic(setting.Multiplier, setting.Increase,
                    affectsMonsters ?? (relicMod.Contains("Monster") || relicMod.Contains("Elite") || relicMod.Contains("PackSize"))),
            _ when relicMod.Contains("Monster") => new ConfigurableRelic(plannerSettings.DefaultRelicSettings.Multiplier, plannerSettings.DefaultRelicSettings.Increase, true),
            _ when relicMod.Contains("Chest") => new ConfigurableRelic(plannerSettings.DefaultRelicSettings.Multiplier, plannerSettings.DefaultRelicSettings.Increase, false),
            _ => null,
        };
    }

    public static readonly List<ExpeditionMarkerIconDescription> ExpeditionRelicIcons = new()
    {
        new()
        {
            IconPickerIndex = IconPickerIndex.CorruptedItems,
            DefaultIcon = MapIconsIndex.QuestObject,
            BaseEntityMetadataSubstrings =
            {

                "ExpeditionRelicUpsideCorruptedDropChanceVaal",
            },
        },
        new()
        {
            IconPickerIndex = IconPickerIndex.Experience,
            DefaultIcon = MapIconsIndex.QuestObject,
            //Experience comes from kills, so this is a monster relic. The mod name says nothing
            //about scope, so without this it fell through the heuristic and multiplied chests.
            AffectsMonsters = true,
            BaseEntityMetadataSubstrings =
            {

                "ExpeditionRelicUpsideExperience",
                "ExpeditionRelicUpsideExperienceKarui"
            },
        },
        new()
        {
            IconPickerIndex = IconPickerIndex.Rarity,
            DefaultIcon = MapIconsIndex.RewardUniques,
            //Every mod here grants monster rarity, but only the first two say so in their name -
            //the two Special* doodad mods would otherwise be inferred as chest relics.
            AffectsMonsters = true,
            BaseEntityMetadataSubstrings =
            {

                "ExpeditionRelicUpsideItemRarityMonster",
                "ExpeditionRelicUpsideItemRarityMonsterEzomyte",
                //Not relic entities but explodable doodads (Karui Totem, Sulphite Pillar) that
                //carry an ExpeditionRelicUpside mod and so ride the same pipeline. They granted
                //+40% and +20% rarity respectively and used to have their own weights; they now
                //share this one, which means the difference between them is no longer expressed.
                "ExpeditionRelicUpsideSpecialKaruiTotem",
                "ExpeditionRelicUpsideSpecialSulphite"
            },
        },
        new()
        {
            IconPickerIndex = IconPickerIndex.Logbooks,
            DefaultIcon = MapIconsIndex.QuestItem,
            BaseEntityMetadataSubstrings =
            {

                "ExpeditionRelicUpsideExpeditionLogbookQuantityMonster",
            },
        },

        new()
        {
            IconPickerIndex = IconPickerIndex.PackSize,
            DefaultIcon = MapIconsIndex.LootFilterLargeGreenTriangle,
            BaseEntityMetadataSubstrings =
            {

                "ExpeditionRelicUpsidePackSize",
            },
        },
        new()
        {
            IconPickerIndex = IconPickerIndex.MonsterMods,
            DefaultIcon = MapIconsIndex.LootFilterLargeGreenTriangle,
            BaseEntityMetadataSubstrings =
            {

                "ExpeditionRelicUpsideRareMonsterChance",
                "ExpeditionRelicUpsideMagicMonsterChance",
            },
        },
        new()
        {
            IconPickerIndex = IconPickerIndex.Goblin,
            DefaultIcon = MapIconsIndex.LootFilterLargeGreenTriangle,
            BaseEntityMetadataSubstrings =
            {

                "ExpeditionRelicUpsideMagicRareMonsterChanceGoblin",
            },
        },
        new()
        {
            IconPickerIndex = IconPickerIndex.RunicMonsterDuplication,
            DefaultIcon = MapIconsIndex.IncursionArchitectReplace,
            BaseEntityMetadataSubstrings =
            {

                "ExpeditionRelicUpsideElitesDuplicated",
            },
            IsWeightCustomizable = false,
        },
        new()
        {
            IconPickerIndex = IconPickerIndex.Quantity,
            DefaultIcon = MapIconsIndex.RewardGenericItems,
            BaseEntityMetadataSubstrings =
            {

                "ExpeditionRelicUpsideItemQuantityMonster",
            },
        },

        new()
        {
            IconPickerIndex = IconPickerIndex.RarityExcavatedChest,
            DefaultIcon = MapIconsIndex.RewardUniques,
            BaseEntityMetadataSubstrings =
            {

                "ExpeditionRelicUpsideItemRarityChest",
            },
        },
        new()
        {
            IconPickerIndex = IconPickerIndex.QuantityExcavatedChest,
            DefaultIcon = MapIconsIndex.RewardGenericItems,
            BaseEntityMetadataSubstrings =
            {

                "ExpeditionRelicUpsideItemQuantityChest",
            },
        },
        new()
        {
            IconPickerIndex = IconPickerIndex.ArtifactsExcavatedChest,
            DefaultIcon = MapIconsIndex.LootFilterLargePurpleSquare,
            BaseEntityMetadataSubstrings =
            {

                "ExpeditionRelicUpsideItemQuantityChest",
            },
        },
        new()
        {
            IconPickerIndex = IconPickerIndex.Effectiveness,
            DefaultIcon = MapIconsIndex.QuestObject,
            //A Gully doodad rather than a relic entity, but it carries an ExpeditionRelicUpside
            //mod and so rides the mod-keyed pipeline like the totems.
            //Monsters only. The mod name carries no scope hint, so without this it would fall
            //through the Monster/Elite/PackSize heuristic and be scored as a chest relic.
            AffectsMonsters = true,
            BaseEntityMetadataSubstrings =
            {

                "ExpeditionRelicUpsideSpecialDevourerTail",
            },
        },
        new()
        {
            IconPickerIndex = IconPickerIndex.BeastSkin,
            DefaultIcon = MapIconsIndex.QuestObject,
            //An explodable doodad like the Karui Totem and Sulphite Pillar folded into Rarity
            //above, but note the mod is ExpeditionRelicModifier*, not ExpeditionRelicUpside* -
            //the only one of that shape seen so far. Without this entry it scores zero.
            //ASSUMED monsters-only, following the totems. The mod name carries no scope hint, so
            //if it turns out to boost chests too this is the line to change.
            AffectsMonsters = true,
            BaseEntityMetadataSubstrings =
            {

                "ExpeditionRelicModifierBeastSkin",
            },
        },
        new()
        {
            IconPickerIndex = IconPickerIndex.Artifacts,
            DefaultIcon = MapIconsIndex.LootFilterLargePurpleSquare,
            BaseEntityMetadataSubstrings =
            {

                "ExpeditionRelicUpsideIncreasedArtifactsMonster",
            },
        },
    };

    /// <summary>
    /// Resolves a chest marker to its icon description.
    /// The MinimapIcon name is matched by EXACT equality and wins when present - it is the only
    /// thing separating RewardChestCurrency from RewardChestCurrencyRare, which share an
    /// identical .ao file. Falls back to animated .ao metadata for markers with no minimap icon.
    /// Monster and elite markers carry no MinimapIcon at all, which is why they stay on .ao
    /// matching in the callers.
    /// </summary>
    public static ExpeditionMarkerIconDescription GetChestIcon(string minimapIconName, string animatedMetadata)
    {
        if (!string.IsNullOrEmpty(minimapIconName) &&
            LogbookChestIcons.FirstOrDefault(icon => icon.MinimapIconNames.Contains(minimapIconName)) is { } byIcon)
        {
            return byIcon;
        }

        return animatedMetadata == null
            ? null
            : LogbookChestIcons.FirstOrDefault(icon => icon.BaseEntityMetadataSubstrings.Any(animatedMetadata.Contains));
    }

    /// <summary>
    /// Chests that are real entities standing in the expedition rather than the ExpeditionMarker
    /// doodads in <see cref="LogbookChestIcons"/>, so their metadata substring is a full entity path.
    /// <para>
    /// These carry matching IngameIcon markers already, but those markers hold nothing that says
    /// which kind of box they belong to - hence matching the box entity itself. Scoring only:
    /// display still comes from the markers, hence IsIconCustomizable = false.
    /// </para>
    /// </summary>
    public static readonly List<ExpeditionMarkerIconDescription> PathMatchedChests =
    [
        PathChest("Metadata/Chests/StrongBoxes/ArmourerStrongboxExpedition", IconPickerIndex.ArmourerStrongbox, "Armourer Strongbox"),
        PathChest("Metadata/Chests/StrongBoxes/MartialStrongboxExpedition", IconPickerIndex.MartialStrongbox, "Martial Strongbox"),
        PathChest("Metadata/Chests/StrongBoxes/JewellerStrongboxExpedition", IconPickerIndex.JewellerStrongbox, "Jeweller Strongbox"),
        PathChest("Metadata/Chests/StrongBoxes/OrnateStrongboxExpedition", IconPickerIndex.OrnateStrongbox, "Ornate Strongbox"),
        PathChest("Metadata/Chests/StrongBoxes/ResearchStrongboxExpedition", IconPickerIndex.ResearchStrongbox, "Research Strongbox"),
        //The only strongbox without an Expedition suffix - the generic large one. It would match
        //outside an expedition too, but the planner only ever runs inside one.
        PathChest("Metadata/Chests/StrongBoxes/LargeStrongboxHigh", IconPickerIndex.LargeStrongbox, "Large Strongbox"),
        PathChest("Metadata/Terrain/Gallows/Leagues/Expedition/Objects/ExplodingFill_BoxxesofGold", IconPickerIndex.GoldBoxes, "Gold Boxes"),
        PathChest(KaruiGatePath, IconPickerIndex.KaruiGate, "Arohongui's Hoard"),
        //Listed here only to earn a row in the chest weight table. It is NOT scored as a chest -
        //see the lighthouse handling in the environment build and PathPlanner.
        PathChest(LighthousePath, IconPickerIndex.Lighthouse, "Lighthouse (logbook)"),
    ];

    private static ExpeditionMarkerIconDescription PathChest(string path, IconPickerIndex index, string name) =>
        new()
        {
            IconPickerIndex = index,
            DefaultIcon = ExpeditionIconsSettings.DefaultChestIcon,
            DisplayName = name,
            IsIconCustomizable = false,
            BaseEntityMetadataSubstrings = { path },
        };

    public static readonly Dictionary<string, IconPickerIndex> StrongboxIndexByPath =
        PathMatchedChests.ToDictionary(x => x.BaseEntityMetadataSubstrings[0], x => x.IconPickerIndex);

    /// <summary>
    /// Two of these gates can block a single hoard, and blowing up either one opens it, so gates
    /// within <c>KaruiGateClusterRadius</c> of an already counted one are dropped rather than
    /// scored twice. Handled in the environment build, not here.
    /// </summary>
    public const string KaruiGatePath = "Metadata/Terrain/Leagues/Expedition/Objects/ExpeditionKaruiGateExplodable";

    /// <summary>
    /// Destroying enough of these across one path yields an Expedition Logbook. Its weight scales
    /// the logbook's own market value rather than being an absolute score, so 1.0 means "worth
    /// exactly what a logbook sells for".
    /// <para>Digsite-specific, so inert in other biomes - same tradeoff as the Basin objects.</para>
    /// </summary>
    public const string LighthousePath = "Metadata/Terrain/Gallows/Leagues/Expedition/Logbook_Digsite/Objects/Lighthouse_Destructable";

    /// <summary>Base item metadata for the logbook the lighthouses produce, for NinjaPricer.</summary>
    public const string LogbookMetadata = "Metadata/Items/Expedition/Expedition2Logbook";

    public static readonly List<ExpeditionMarkerIconDescription> LogbookChestIcons = new()
    {
        new()
        {
            IconPickerIndex = IconPickerIndex.CurrencyChest,
            DefaultIcon = ExpeditionIconsSettings.DefaultChestIcon,
            MinimapIconNames = { "RewardChestCurrency" },
            BaseEntityMetadataSubstrings =
            {
                "Metadata/Terrain/Doodads/Leagues/Expedition/ChestMarkers/ChestCurrency.ao"
            },
        },
        new()
        {
            IconPickerIndex = IconPickerIndex.CurrencyChestRare,
            DefaultIcon = ExpeditionIconsSettings.DefaultChestIcon,
            //Shares a byte-identical .ao with RewardChestCurrency, so the minimap icon name is the
            //ONLY way to tell the two apart. Deliberately has no metadata substrings.
            MinimapIconNames = { "RewardChestCurrencyRare" },
        },
        new()
        {
            IconPickerIndex = IconPickerIndex.RunesChest,
            DefaultIcon = ExpeditionIconsSettings.DefaultChestIcon,
            //A distinct reward chest type. NOT related to the runestones on remnants.
            MinimapIconNames = { "RewardChestRunes" },
            BaseEntityMetadataSubstrings =
            {
                "chestmarker3"
            },
        },
        new()
        {
            IconPickerIndex = IconPickerIndex.MapsChest,
            DefaultIcon = ExpeditionIconsSettings.DefaultChestIcon,
            MinimapIconNames = { "RewardChestMaps" },
            BaseEntityMetadataSubstrings =
            {
                "Metadata/Terrain/Doodads/Leagues/Expedition/ChestMarkers/ChestMaps.ao"
            },
        },
        new()
        {
            IconPickerIndex = IconPickerIndex.JewelleryChest,
            DefaultIcon = ExpeditionIconsSettings.DefaultChestIcon,
            MinimapIconNames = { "RewardChestTrinkets" },
            BaseEntityMetadataSubstrings =
            {
                "Metadata/Terrain/Doodads/Leagues/Expedition/ChestMarkers/ChestTrinkets.ao"
            },
        },
        new()
        {
            IconPickerIndex = IconPickerIndex.UniquesChest,
            DefaultIcon = ExpeditionIconsSettings.DefaultChestIcon,
            MinimapIconNames = { "RewardChestUnique" },
            BaseEntityMetadataSubstrings =
            {
                "Metadata/Terrain/Doodads/Leagues/Expedition/ChestMarkers/ChestUniques.ao"
            },
        },
        new()
        {
            IconPickerIndex = IconPickerIndex.LeagueChest,
            DefaultIcon = ExpeditionIconsSettings.DefaultChestIcon,
            //No MinimapIcon name observed for this type yet - .ao matching only.
            BaseEntityMetadataSubstrings =
            {
                "Metadata/Terrain/Doodads/Leagues/Expedition/ChestMarkers/ChestLeague.ao"
            },
        },
        new()
        {
            IconPickerIndex = IconPickerIndex.WeaponChest,
            DefaultIcon = ExpeditionIconsSettings.DefaultChestIcon,
            //No MinimapIcon name observed for this type yet - .ao matching only.
            BaseEntityMetadataSubstrings =
            {
                "Metadata/Terrain/Doodads/Leagues/Expedition/ChestMarkers/ChestWeapon.ao"
            },
        },
        new()
        {
            IconPickerIndex = IconPickerIndex.GemsChest,
            DefaultIcon = ExpeditionIconsSettings.DefaultChestIcon,
            //No MinimapIcon name observed for this type yet - .ao matching only.
            BaseEntityMetadataSubstrings =
            {
                "Metadata/Terrain/Doodads/Leagues/Expedition/ChestMarkers/ChestGems.ao"
            },
        },
        new()
        {
            IconPickerIndex = IconPickerIndex.EssenceChest,
            DefaultIcon = ExpeditionIconsSettings.DefaultChestIcon,
            //No MinimapIcon name observed for this type yet - .ao matching only.
            BaseEntityMetadataSubstrings =
            {
                "Metadata/Terrain/Doodads/Leagues/Expedition/ChestMarkers/ChestEssence.ao"
            },
        },
        new()
        {
            IconPickerIndex = IconPickerIndex.ArmourChest,
            DefaultIcon = ExpeditionIconsSettings.DefaultChestIcon,
            //No MinimapIcon name observed for this type yet - .ao matching only.
            BaseEntityMetadataSubstrings =
            {
                "Metadata/Terrain/Doodads/Leagues/Expedition/ChestMarkers/ChestArmour.ao"
            },
        },
        new()
        {
            IconPickerIndex = IconPickerIndex.DeliriumChest,
            DefaultIcon = ExpeditionIconsSettings.DefaultChestIcon,
            //No MinimapIcon name observed for this type yet - .ao matching only.
            BaseEntityMetadataSubstrings =
            {
                "Metadata/Terrain/Doodads/Leagues/Expedition/ChestMarkers/ChestDelirium.ao"
            },
        },
        new()
        {
            IconPickerIndex = IconPickerIndex.OtherChests,
            DefaultIcon = MapIconsIndex.MissionAlly,
            //Must stay LAST: the ChestMarkers path substring matches every chest above.
            //"chestmarker3" was removed from this list - it is now RunesChest.
            MinimapIconNames = { "RewardChestGeneric" },
            BaseEntityMetadataSubstrings =
            {
                "chestmarker1",
                "chestmarker2",
                "chestmarker_signpost",
                "Metadata/Terrain/Doodads/Leagues/Expedition/ChestMarkers"
            },
        },
    };
}
