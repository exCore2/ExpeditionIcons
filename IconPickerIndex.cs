using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace ExpeditionIcons;

[JsonConverter(typeof(StringEnumConverter))]
public enum IconPickerIndex
{
    Experience,
    Rarity,
    Logbooks,
    PackSize,
    MonsterMods,
    Goblin,
    Artifacts,
    BeastSkin,
    Quantity,
    CorruptedItems,
    RarityExcavatedChest,
    ArtifactsExcavatedChest,
    QuantityExcavatedChest,
    RunicMonsterDuplication,
    EliteMonstersIndicator,
    BadModsIndicator,
    LeagueChest,
    JewelleryChest,
    WeaponChest,
    CurrencyChest,
    CurrencyChestRare,
    RunesChest,
    MapsChest,
    GemsChest,
    EssenceChest,
    ArmourChest,
    DeliriumChest,
    UniquesChest,
    OtherChests,

    //Real Chest/IngameIcon entities inside the expedition rather than ExpeditionMarker doodads,
    //so they are matched on entity path. See Icons.PathMatchedChests.
    ArmourerStrongbox,
    MartialStrongbox,
    JewellerStrongbox,
    OrnateStrongbox,
    ResearchStrongbox,
    LargeStrongbox,
    GoldBoxes,
    KaruiGate,
    Lighthouse,
    Effectiveness,
}