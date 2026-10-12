using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using ExileCore2.Shared.Attributes;
using ExileCore2.Shared.Enums;
using ExileCore2.Shared.Helpers;
using ExileCore2.Shared.Interfaces;
using ExileCore2.Shared.Nodes;
using ImGuiNET;
using Newtonsoft.Json;
using Vector4 = System.Numerics.Vector4;

namespace ExpeditionIcons;

public class IconPickerDrawer
{
    public static readonly IconPickerDrawer Instance = new();

    internal IntPtr _iconsImageId;
    private IconPickerIndex? _shownIconPicker;
    private string _iconFilter = "";
    internal ExpeditionIconsSettings Settings;

    public bool PickIcon(string iconName, ref MapIconsIndex icon, Vector4 tintColor)
    {
        var isOpen = true;
        ImGui.Begin($"Pick icon for {iconName}", ref isOpen, ImGuiWindowFlags.AlwaysAutoResize);
        if (!isOpen)
        {
            return true;
        }

        ImGui.InputTextWithHint("##Filter", "Filter", ref _iconFilter, 100);
        ImGui.SliderInt("Icon size (only in this menu)", ref Settings.IconPickerSize, 15, 60);
        ImGui.SliderInt("Icons per row", ref Settings.IconsPerRow, 5, 60);
        var icons = Enum.GetValues<MapIconsIndex>()
            .Where(x => string.IsNullOrEmpty(_iconFilter) || x.ToString().Contains(_iconFilter, StringComparison.InvariantCultureIgnoreCase))
            .ToArray();
        for (var i = 0; i < icons.Length; i++)
        {
            var testIcon = icons[i];
            var rect = SpriteHelper.GetUV(testIcon);
            if (icon == testIcon)
            {
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(1, 0, 1, 1));
            }
            else
            {
                ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.WindowBg));
            }

            var btnClicked = ImGui.ImageButton($"btn{i}", _iconsImageId, System.Numerics.Vector2.One * Settings.IconPickerSize,
                rect.TopLeft, rect.BottomRight, Vector4.Zero, tintColor);
            ImGui.PopStyleColor();
            if (btnClicked)
            {
                icon = testIcon;
                return true;
            }

            if ((i + 1) % Settings.IconsPerRow != 0)
            {
                ImGui.SameLine();
            }
        }

        ImGui.End();
        return false;
    }

    public void PickIcon(IconPickerIndex iconKey, MapIconsIndex defaultIcon)
    {
        var iconSettings = Settings.IconMapping.GetValueOrDefault(iconKey, new IconDisplaySettings());
        ImGui.Checkbox($"Show {iconKey} on map", ref iconSettings.ShowOnMap);
        ImGui.SameLine();
        ImGui.Text("(");
        ImGui.SameLine(0, 0);
        ImGui.Checkbox("in world", ref iconSettings.ShowInWorld);
        ImGui.SameLine(0, 0);
        ImGui.Text(")");
        ImGui.SameLine();
        var effectiveIcon = iconSettings.Icon ?? defaultIcon;
        var uv = SpriteHelper.GetUV(effectiveIcon);
        var uv0 = uv.TopLeft;
        var uv1 = uv.BottomRight;
        ImGui.PushID(iconKey.ToString());
        var tintVector = (iconSettings.Tint ?? Color.White).ToImguiVec4();
        var buttonClicked = ImGui.ImageButton("iconbtn", _iconsImageId, System.Numerics.Vector2.One * 15, uv0, uv1, Vector4.Zero, tintVector);
        ImGui.SameLine();
        if (ImGui.ColorEdit4("Tint", ref tintVector,
                ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.NoInputs |
                ImGuiColorEditFlags.AlphaPreviewHalf))
        {
            var tint = tintVector.ToColor();
            if (tint != Color.White)
            {
                iconSettings.Tint = tint;
            }
        }

        if (buttonClicked)
        {
            _iconFilter = "";
        }

        if (buttonClicked || iconKey == _shownIconPicker)
        {
            _shownIconPicker = iconKey;
            if (PickIcon(iconKey.ToString(), ref effectiveIcon, tintVector))
            {
                iconSettings.Icon = effectiveIcon != defaultIcon ? effectiveIcon : null;
                _shownIconPicker = null;
            }
        }

        ImGui.PopID();
        Settings.IconMapping[iconKey] = iconSettings;
    }
}

public class ExpeditionIconsSettings : ISettings
{
    public const MapIconsIndex DefaultBadModsIcon = MapIconsIndex.RedFlag;
    public const MapIconsIndex DefaultEliteMonsterIcon = MapIconsIndex.HeistSpottedMiniBoss;
    public const MapIconsIndex DefaultChestIcon = MapIconsIndex.MissionTarget;

    public int IconPickerSize = 20;
    public int IconsPerRow = 15;
    public Dictionary<IconPickerIndex, IconDisplaySettings> IconMapping = new();

    public ExpeditionIconsSettings()
    {
        IconPickerDrawer.Instance.Settings = this;
        GoodModsIconPicker = new CustomNode
        {
            DrawDelegate = () =>
            {
                foreach (var expeditionMarkerIconDescription in Icons.ExpeditionRelicIcons)
                {
                    ImGui.PushID($"IconLine{expeditionMarkerIconDescription.IconPickerIndex}");
                    IconPickerDrawer.Instance.PickIcon(expeditionMarkerIconDescription.IconPickerIndex, expeditionMarkerIconDescription.DefaultIcon);
                    ImGui.PopID();
                }
            }
        };
        ChestSettings = new CustomNode
        {
            DrawDelegate = () =>
            {
                //One list feeds both chest tables; the path-matched box entities are drawn from
                //their own markers, so they get a weight row but no icon of their own to pick.
                foreach (var chest in Icons.LogbookChestIcons.Concat(Icons.PathMatchedChests).Where(x => x.IsIconCustomizable))
                {
                    ImGui.PushID($"IconLine{chest.IconPickerIndex}");
                    IconPickerDrawer.Instance.PickIcon(chest.IconPickerIndex, chest.DefaultIcon);
                    ImGui.PopID();
                }
            }
        };
        DrawEliteMonstersInWorld = new CustomNode
        {
            DrawDelegate = () => { IconPickerDrawer.Instance.PickIcon(IconPickerIndex.EliteMonstersIndicator, DefaultEliteMonsterIcon); }
        };
    }

    public ToggleNode Enable { get; set; } = new ToggleNode(false);

    [JsonIgnore]
    public CustomNode DrawEliteMonstersInWorld { get; set; }

    public RangeNode<int> WorldIconSize { get; set; } = new RangeNode<int>(50, 25, 200);
    public RangeNode<int> MapIconSize { get; set; } = new RangeNode<int>(30, 15, 200);

    [Menu("Ignore secondary explosions for valuable runestones",
        "Chained blasts don't reliably set off runestones - disable this if GGG fixes that.",
        99)]
    public ToggleNode IgnoreSecondaryExplosionsForValuableRunestones { get; set; } = new ToggleNode(true);

    [Menu("Good mods", 100, CollapsedByDefault = true)]
    [JsonIgnore]
    public EmptyNode SettingsEmptyGood { get; set; }

    [Menu(null, parentIndex = 100)]
    public ToggleNode DrawGoodModsOnMap { get; set; } = new ToggleNode(true);

    [Menu(null, parentIndex = 100)]
    public ToggleNode DrawGoodModsInWorld { get; set; } = new ToggleNode(true);

    [JsonIgnore]
    [Menu(null, parentIndex = 100)]
    public CustomNode GoodModsIconPicker { get; }

    public ModWarningSettings ModWarningSettings { get; set; } = new ModWarningSettings();

    [Menu("Chest settings", index = 103, CollapsedByDefault = true)]
    [JsonIgnore]
    public EmptyNode ChestSettingsHeader { get; set; }

    [Menu(null, parentIndex = 103)]
    [JsonIgnore]
    public CustomNode ChestSettings { get; set; }

    public RuneDisplaySettings RuneSettings { get; set; } = new RuneDisplaySettings();

    public ExpeditionExplosiveSettings ExplosivesSettings { get; set; } = new ExpeditionExplosiveSettings();
    public PlannerSettings PlannerSettings { get; set; } = new PlannerSettings();
}

[Submenu]
public class PlannerSettings
{
    public Dictionary<IconPickerIndex, ChestSettings> ChestSettingsMap = new()
    {
        //Anything not listed falls back to ChestSettings' own default of 1.
        [IconPickerIndex.LeagueChest] = new ChestSettings { Weight = 2 },
        [IconPickerIndex.CurrencyChestRare] = new ChestSettings { Weight = 1.5f },
        //Tracks the rare currency chest, as you asked when it was added.
        [IconPickerIndex.KaruiGate] = new ChestSettings { Weight = 1.5f },
        [IconPickerIndex.RunesChest] = new ChestSettings { Weight = 1.2f },
        [IconPickerIndex.ResearchStrongbox] = new ChestSettings { Weight = 1.2f },
        [IconPickerIndex.GoldBoxes] = new ChestSettings { Weight = 1.1f },
    };

    public Dictionary<IconPickerIndex, RelicSettings> RelicSettingsMap = new()
    {
        [IconPickerIndex.Logbooks] = new RelicSettings { Multiplier = 1.5f, },
        [IconPickerIndex.PackSize] = new RelicSettings { Multiplier = 1.25f, },
        [IconPickerIndex.Artifacts] = new RelicSettings { Increase = 0.4f, },
        [IconPickerIndex.ArtifactsExcavatedChest] = new RelicSettings { Increase = 0.4f, },
        [IconPickerIndex.Quantity] = new RelicSettings { Increase = 0.4f, },
        //Rarity itself has no entry, so it falls back to RelicSettings.Default (Increase 0.15).
        //The Karui Totem and Sulphite Pillar are scored on that same Rarity weight rather than
        //on their own, so they no longer appear here.
        //Multiplier rather than Increase, and small - what the beast skin actually grants is
        //unknown, so this is a nudge to prefer covering one, not a real valuation.
        [IconPickerIndex.BeastSkin] = new RelicSettings { Multiplier = 1.05f, },
        [IconPickerIndex.QuantityExcavatedChest] = new RelicSettings { Increase = 0.4f, },
    };

    public RelicSettings DefaultRelicSettings = RelicSettings.Default;

    internal const string OtherRunesRow = "*other*";

    //Hardcoded, matching the tiers you specified. A rune not listed here falls to
    //DefaultRuneMultiplier, so a rune GGG adds later ships at D tier rather than breaking.
    internal static readonly string[] RuneWeightRows =
        ["Opulent", "Bond", "Death", "Time", "Power", "Oath", "Soul", "Life", OtherRunesRow];

    //Which runes are worth keeping a cheap runestone for. If none of its recipes can pass on any
    //of these, the display marks it as a reroll candidate. See ExpeditionIcons.IsRerollCandidate.
    //Ticked by default only for the top two tiers - the ones actually worth passing on.
    public Dictionary<string, bool> KeepRunes = new()
    {
        ["Opulent"] = true,
        ["Bond"] = true,
        ["Death"] = true,
        ["Time"] = true,
        ["Power"] = false,
        ["Oath"] = false,
        ["Soul"] = false,
        ["Life"] = false,
    };

    //The "Other runes" row: anything not named in RuneWeightRows.
    public bool KeepOtherRunes = false;

    public Dictionary<string, float> RuneMultipliers = new()
    {
        ["Opulent"] = 1.6f,
        ["Bond"] = 1.6f,
        ["Death"] = 1.4f,
        ["Time"] = 1.4f,
        ["Power"] = 1.25f,
        ["Oath"] = 1.15f,
        ["Soul"] = 1.15f,
        ["Life"] = 1.15f,
    };

    public float DefaultRuneMultiplier = 1.05f;

    public RuneScoringSettings RuneScoring { get; set; } = new RuneScoringSettings();

    public PlannerSettings()
    {
        ChestWeightSettings = new CustomNode
        {
            DrawDelegate = () =>
            {
                //Marker chests first, then the path-matched box entities, which have no icon picker
                //and so are drawn here but not in the icon table.
                foreach (var chest in Icons.LogbookChestIcons.Concat(Icons.PathMatchedChests))
                {
                    var index = chest.IconPickerIndex;
                    ImGui.PushID($"IconLine{index}");
                    var chestSettings = ChestSettingsMap.GetValueOrDefault(index, new ChestSettings());
                    if (ImGui.SliderFloat($"{chest.Name} weight", ref chestSettings.Weight, 0, 5))
                    {
                        ChestSettingsMap[index] = chestSettings;
                    }

                    ImGui.PopID();
                }
            }
        };
        RelicWeightSettings = new CustomNode
        {
            DrawDelegate = () =>
            {
                //Five columns for four headers: the last one is an unnamed filler that soaks up the
                //slack, which is what the spare fourth column did before the Keep column existed.
                if (ImGui.BeginTable("Relic Weight", 5, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.Borders))
                {
                    ImGui.TableSetupColumn("Name");
                    ImGui.TableSetupColumn("Multiplier", ImGuiTableColumnFlags.WidthFixed, 300);
                    ImGui.TableSetupColumn("Increase", ImGuiTableColumnFlags.WidthFixed, 300);
                    ImGui.TableSetupColumn("Keep");
                    ImGui.TableHeadersRow();
                    foreach (var relic in Icons.ExpeditionRelicIcons)
                    {
                        var index = relic.IconPickerIndex;
                        ImGui.PushID($"Icon{index}");
                        ImGui.TableNextRow(ImGuiTableRowFlags.None);
                        ImGui.TableNextColumn();
                        ImGui.Text(relic.Name);
                        var relicSettings = RelicSettingsMap.GetValueOrDefault(index, RelicSettings.Default);

                        ImGui.TableNextColumn();
                        ImGui.SetNextItemWidth(300);
                        if (ImGui.SliderFloat("##multiplier", ref relicSettings.Multiplier, 0, 5))
                        {
                            RelicSettingsMap[index] = relicSettings;
                        }

                        ImGui.TableNextColumn();
                        ImGui.SetNextItemWidth(300);
                        if (ImGui.SliderFloat("##increase", ref relicSettings.Increase, 0, 5))
                        {
                            RelicSettingsMap[index] = relicSettings;
                        }

                        //Keeping is a rune-only concept.
                        ImGui.TableNextColumn();
                        ImGui.Text("-");

                        ImGui.PopID();
                    }

                    {
                        ImGui.PushID("OtherRelics");
                        ImGui.TableNextRow(ImGuiTableRowFlags.None);
                        ImGui.TableNextColumn();
                        ImGui.Text("Other relics");
                        var relicSettings = DefaultRelicSettings;

                        ImGui.TableNextColumn();
                        ImGui.SetNextItemWidth(300);
                        ImGui.SliderFloat("##multiplier", ref relicSettings.Multiplier, 0, 5);

                        ImGui.TableNextColumn();
                        ImGui.SetNextItemWidth(300);
                        ImGui.SliderFloat("##increase", ref relicSettings.Increase, 0, 5);

                        ImGui.TableNextColumn();
                        ImGui.Text("-");
                        ImGui.PopID();
                    }

                    foreach (var runeId in RuneWeightRows)
                    {
                        ImGui.PushID($"Rune{runeId}");
                        ImGui.TableNextRow(ImGuiTableRowFlags.None);
                        ImGui.TableNextColumn();
                        ImGui.Text(runeId == OtherRunesRow ? "Other runes" : $"Rune: {runeId}");

                        var multiplier = runeId == OtherRunesRow
                            ? DefaultRuneMultiplier
                            : RuneMultipliers.GetValueOrDefault(runeId, DefaultRuneMultiplier);

                        ImGui.TableNextColumn();
                        ImGui.SetNextItemWidth(300);
                        if (ImGui.SliderFloat("##multiplier", ref multiplier, 1, 3))
                        {
                            if (runeId == OtherRunesRow)
                            {
                                DefaultRuneMultiplier = multiplier;
                            }
                            else
                            {
                                RuneMultipliers[runeId] = multiplier;
                            }
                        }

                        //Runes are a pure scalar on runic monsters, so there is no additive term.
                        ImGui.TableNextColumn();
                        ImGui.Text("-");

                        ImGui.TableNextColumn();
                        if (runeId == OtherRunesRow)
                        {
                            ImGui.Checkbox("##keep", ref KeepOtherRunes);
                        }
                        else
                        {
                            var keep = KeepRunes.GetValueOrDefault(runeId, false);
                            if (ImGui.Checkbox("##keep", ref keep))
                            {
                                KeepRunes[runeId] = keep;
                            }
                        }

                        ImGui.PopID();
                    }

                    ImGui.EndTable();
                }
            }
        };
    }

    public HotkeyNode StartSearchHotkey { get; set; } = new HotkeyNode(Keys.F13);
    public HotkeyNode StopSearchHotkey { get; set; } = new HotkeyNode(Keys.F13);
    public HotkeyNode ClearSearchHotkey { get; set; } = new HotkeyNode(Keys.F13);
    public HotkeyNode ConfirmEditorPlacementHotkey { get; set; } = new HotkeyNode(Keys.Enter);

    [Menu("Blacklist the area around the player", "Explosives will not path through the marked circles. Cleared on zone change.")]
    public HotkeyNode BlacklistAreaHotkey { get; set; } = new HotkeyNode(Keys.F10);

    [Menu("Blacklisted area radius (grid units)")]
    public RangeNode<float> BlacklistRadius { get; set; } = new RangeNode<float>(15, 1, 30);

    public ToggleNode ShowBlacklistedAreas { get; set; } = new ToggleNode(true);

    [Menu("Color for blacklisted areas")]
    public ColorNode BlacklistColor { get; set; } = new ColorNode(Color.Red);

    [JsonIgnore]
    [ConditionalDisplay(nameof(IsSearchRunning), false)]
    public ButtonNode StartSearch { get; set; } = new ButtonNode();

    [JsonIgnore]
    [ConditionalDisplay(nameof(IsSearchRunning))]
    public ButtonNode StopSearch { get; set; } = new ButtonNode();

    [JsonIgnore]
    [ConditionalDisplay(nameof(HasSearchResult))]
    public ButtonNode ClearSearch { get; set; } = new ButtonNode();

    public ToggleNode PlaySoundOnFinish { get; set; } = new ToggleNode(false);

    [Menu("Color for suggested explosive radius")]
    public ColorNode ExplosiveColor { get; set; } = new ColorNode(Color.Purple);

    public ColorNode MapLineColor { get; set; } = new ColorNode(Color.Red);
    public ColorNode WorldLineColor { get; set; } = new ColorNode(Color.Orange);

    [Menu("Color for captured entities in world")]
    public ColorNode CapturedEntityWorldFrameColor { get; set; } = new ColorNode(Color.Purple);

    [Menu("Color for captured entities on map")]
    public ColorNode CapturedEntityMapFrameColor { get; set; } = new ColorNode(Color.Purple);

    public RangeNode<float> TextMarkerScale { get; set; } = new RangeNode<float>(1, 0, 5);

    [Menu("Search time, maps", "How long the search runs in a map expedition, in seconds.")]
    public RangeNode<float> MaximumGenerationTimeSeconds { get; set; } = new RangeNode<float>(5, 0, 60);

    [Menu("Search time, logbooks",
        "How long the search runs in a logbook area, in seconds. Separate from the map figure because a logbook is a larger area with more explosives, so its search has a much bigger space to cover and takes longer to settle.")]
    public RangeNode<float> LogbookMaximumGenerationTimeSeconds { get; set; } = new RangeNode<float>(30, 0, 60);

    /// <summary>
    /// The time limit that applies to an area of this kind. One accessor rather than the caller
    /// picking, so a new call site cannot quietly go on using the map figure everywhere.
    /// </summary>
    public float GenerationTimeSeconds(bool isLogbook)
    {
        return isLogbook ? LogbookMaximumGenerationTimeSeconds.Value : MaximumGenerationTimeSeconds.Value;
    }

    public RangeNode<int> SearchThreads { get; set; } = new RangeNode<int>(5, 1, 10);
    public RangeNode<float> NewRandomPathInjectionRate { get; set; } = new RangeNode<float>(1f, 0, 2);
    public RangeNode<float> PathMutateChance { get; set; } = new RangeNode<float>(0.5f, 0, 1);
    public RangeNode<int> PathGenerationSize { get; set; } = new RangeNode<int>(100, 1, 1000);
    [Menu("Validate placements by walkable path",
        "Only accepts placements the explosives can actually reach.")]
    //On by default on the evidence: across two logbook biomes the sampling rule produced a plan
    //with an unmakeable placement in 12 of 15 searches - median buildable score 52.6 against a
    //reported 141.3 in the Wastes - while this rule was buildable in every run and scored about 11%
    //above the straight-line-only variant of itself. It costs roughly a fifth of the generations.
    public ToggleNode UseGeodesicPlacement { get; set; } = new ToggleNode(true);

    [Menu("Placement rule: line sampling", 300, CollapsedByDefault = true)]
    [JsonIgnore]
    public EmptyNode OriginalPlacementHeader { get; set; }

    [Menu("Samples per segment",
        "Points checked between placements; a thinner wall goes unseen.",
        parentIndex = 300)]
    public RangeNode<int> ValidatedIntermediatePoints { get; set; } = new RangeNode<int>(1, 0, 5);

    [Menu("Placement rule: walkable path", 310, CollapsedByDefault = true)]
    [JsonIgnore]
    public EmptyNode GeodesicPlacementHeader { get; set; }

    [Menu("Allow routes around obstacles",
        "Off, only a straight run counts - measured about 11% worse.",
        parentIndex = 310)]
    public ToggleNode GeodesicAllowWrapArounds { get; set; } = new ToggleNode(true);

    [Menu("Coarse block size",
        "Larger is faster per test and refuses more legal placements.",
        parentIndex = 310)]
    public RangeNode<int> GeodesicCoarseBlockSize { get; set; } = new RangeNode<int>(3, 2, 16);

    [Menu("Landmark count",
        "More rule out impossible placements faster, at 2 bytes per cell each.",
        parentIndex = 310)]
    public RangeNode<int> GeodesicLandmarkCount { get; set; } = new RangeNode<int>(16, 0, 32);
    public RangeNode<float> RunicMonsterWeight { get; set; } = new RangeNode<float>(3, 0, 5);
    public RangeNode<float> RunicMonsterLogbookWeight { get; set; } = new RangeNode<float>(3, 0, 5);
    public RangeNode<float> NormalMonsterWeight { get; set; } = new RangeNode<float>(0.001f, 0, 5);

    [Menu("Color for chained blast radius")]
    public ColorNode ChainedBlastColor { get; set; } = new ColorNode(Color.Orange);

    [Menu("Runestone monster weight",
        "What ONE rune of the chosen recipe is worth in spawned monsters, before any rune scaling. The runestone's " +
        "monsters score this times the number of runes that recipe actually consumes - its own length, minus any runes " +
        "passed on, and never the stone's socket count. Defaults to exactly one runic monster. Logbook areas use the " +
        "runic monster logbook weight instead.")]
    public RangeNode<float> RunestoneMonsterWeight { get; set; } = new RangeNode<float>(3, 0, 20);

    [Menu("Recipe mutate chance", "How often a mutation switches a runestone's recipe instead of moving an explosive.")]
    public RangeNode<float> RecipeMutateChance { get; set; } = new RangeNode<float>(0.3f, 0, 1);

    [Menu("Reach mutate chance",
        "How often a mutation rebuilds the end of a path to reach a valuable runestone it is missing, rather than nudging one explosive. The only move that can add a runestone the path does not already reach; raise it when good paths keep settling for fewer stones than the area offers, lower it if the search stops refining what it has.")]
    public RangeNode<float> ReachMutateChance { get; set; } = new RangeNode<float>(0.15f, 0, 1);

    [Menu("Recipe search window",
        "In score weight - the largest score sacrifice worth making for a better rune. Recipes further than this below the best are not searched. At the default value scale, 20 weight is roughly a 400 currency gap.")]
    public RangeNode<float> RecipeSearchWindow { get; set; } = new RangeNode<float>(20, 0, 200);

    [Menu("Max recipe candidates", "Hard ceiling on how many recipes per runestone the search considers. Includes the highest-priced one, which is always kept.")]
    public RangeNode<int> MaxRecipeCandidates { get; set; } = new RangeNode<int>(4, 1, 16);

    [Menu("Pathfinding diagnostics", 320, CollapsedByDefault = true)]
    [JsonIgnore]
    public EmptyNode PathfindingDiagnosticsHeader { get; set; }

    [Menu("Collect measurements",
        "Writes a report on every placement test; slows the search a lot.",
        parentIndex = 320)]
    public ToggleNode CollectPathfindingDiagnostics { get; set; } = new ToggleNode(false);

    [Menu("Ground-truth sample rate",
        "One in N placements gets a full search to check the rest against.",
        parentIndex = 320)]
    public RangeNode<int> DiagnosticsGeodesicSampleRate { get; set; } = new RangeNode<int>(64, 1, 4096);

    [Menu("Comparison runs per rule",
        "Searches per rule; the report gives medians across them.",
        parentIndex = 320)]
    public RangeNode<int> PlacementComparisonRuns { get; set; } = new RangeNode<int>(5, 1, 10);

    [Menu("Compare placement rules",
        "Searches under each rule and reports what each really scores.",
        parentIndex = 320)]
    [JsonIgnore]
    public ButtonNode RunPlacementComparison { get; set; } = new ButtonNode();

    [Menu("Chest weight", 888, CollapsedByDefault = true)]
    [JsonIgnore]
    public EmptyNode ChestWeightStub { get; set; }

    [JsonIgnore]
    [Menu(null, parentIndex = 888)]
    public CustomNode ChestWeightSettings { get; set; }

    [Menu("Relic weight modifiers", 999, CollapsedByDefault = true)]
    [JsonIgnore]
    public EmptyNode RelicWeightStub { get; set; }

    [JsonIgnore]
    [Menu(null, parentIndex = 999)]
    public CustomNode RelicWeightSettings { get; set; }

    public RangeNode<int> LogbookCaveRunicMonsterMultiplier { get; set; } = new RangeNode<int>(2, 0, 10);
    public RangeNode<int> LogbookCaveArtifactChestMultiplier { get; set; } = new RangeNode<int>(0, 0, 10);
    public RangeNode<int> LogbookBossRunicMonsterMultiplier { get; set; } = new RangeNode<int>(1, 0, 20);

    public ToggleNode ShowScoreHistory { get; set; } = new ToggleNode(false);
    public ToggleNode ShowScoreHistoryAfterSearchEnds { get; set; } = new ToggleNode(false);

    internal bool HasSearchResult => SearchState != SearchState.Empty;
    internal bool IsSearchRunning => SearchState == SearchState.Searching;

    internal SearchState SearchState = SearchState.Empty;
}

[Submenu(CollapsedByDefault = true)]
public class ExpeditionExplosiveSettings
{
    [Menu("Show explosive radius")]
    public ToggleNode ShowExplosives { get; set; } = new ToggleNode(true);

    [Menu("Color for explosive radius")]
    public ColorNode ExplosiveColor { get; set; } = new ColorNode(Color.Red);

    [Menu("Explosive radius")]
    public RangeNode<int> ExplosiveRadius { get; set; } = new RangeNode<int>(326, 10, 600);

    [Menu("Automatically calculate Radius from map mods")]
    public ToggleNode CalculateRadiusAutomatically { get; set; } = new ToggleNode(true);

    [Menu("Merge explosive radii")]
    public ToggleNode EnableExplosiveRadiusMerging { get; set; } = new ToggleNode(true);

    [Menu("Mark entities captured by explosives in world")]
    public ToggleNode MarkCapturedEntitiesInWorld { get; set; } = new ToggleNode(true);

    [Menu("Mark entities captured by explosives on map")]
    public ToggleNode MarkCapturedEntitiesOnMap { get; set; } = new ToggleNode(true);

    [Menu("Hide icons of entities captured by explosives in world")]
    public ToggleNode HideCapturedEntitiesInWorld { get; set; } = new ToggleNode(false);

    [Menu("Hide icons of entities captured by explosives on map")]
    public ToggleNode HideCapturedEntitiesOnMap { get; set; } = new ToggleNode(false);

    [Menu("Color for captured entities in world")]
    public ColorNode CapturedEntityWorldFrameColor { get; set; } = new ColorNode(Color.Green);

    [Menu("Color for captured entities on map")]
    public ColorNode CapturedEntityMapFrameColor { get; set; } = new ColorNode(Color.Green);

    [Menu("Rectangle Thickness for captured entities in world")]
    public RangeNode<int> CapturedEntityWorldFrameThickness { get; set; } = new RangeNode<int>(2, 1, 20);

    [Menu("Rectangle Thickness for captured entities on map")]
    public RangeNode<int> CapturedEntityMapFrameThickness { get; set; } = new RangeNode<int>(2, 1, 20);
}

[Submenu(CollapsedByDefault = true)]
public class ModWarningSettings
{
    public ModWarningSettings()
    {
        DrawBadMods = new CustomNode
        {
            DrawDelegate = () => { IconPickerDrawer.Instance.PickIcon(IconPickerIndex.BadModsIndicator, ExpeditionIconsSettings.DefaultBadModsIcon); }
        };
    }

    [JsonIgnore]
    public CustomNode DrawBadMods { get; }

    [Menu("Warn for physical immune")]
    public ToggleNode WarnPhysImmune { get; set; } = new ToggleNode(false);

    [Menu("Warn for fire immune")]
    public ToggleNode WarnFireImmune { get; set; } = new ToggleNode(false);

    [Menu("Warn for ailment immune")]
    public ToggleNode WarnAilmentImmune { get; set; } = new ToggleNode(false);

    [Menu("Warn for cold immune")]
    public ToggleNode WarnColdImmune { get; set; } = new ToggleNode(false);

    [Menu("Warn for lightning immune")]
    public ToggleNode WarnLightningImmune { get; set; } = new ToggleNode(false);

    [Menu("Warn for chaos immune")]
    public ToggleNode WarnChaosImmune { get; set; } = new ToggleNode(false);

    [Menu("Warn for crit immune")]
    public ToggleNode WarnCritImmune { get; set; } = new ToggleNode(false);

    [Menu("Warn for curse immune")]
    public ToggleNode WarnCurseImmune { get; set; } = new ToggleNode(false);

    [Menu("Warn for armor pen (100% overwhelm)")]
    public ToggleNode WarnArmorPen { get; set; } = new ToggleNode(false);

    [Menu("Warn for no flask")]
    public ToggleNode WarnNoFlask { get; set; } = new ToggleNode(false);

    [Menu("Warn for no evade")]
    public ToggleNode WarnNoEvade { get; set; } = new ToggleNode(false);

    [Menu("Warn for no leech")]
    public ToggleNode WarnNoLeech { get; set; } = new ToggleNode(true);

    [Menu("Warn for petrify")]
    public ToggleNode WarnPetrify { get; set; } = new ToggleNode(true);

    [Menu("Warn for 20% cull")]
    public ToggleNode WarnCull { get; set; } = new ToggleNode(true);

    [Menu("Warn for monster regen")]
    public ToggleNode WarnMonsterRegen { get; set; } = new ToggleNode(false);

    [Menu("Warn for monster block")]
    public ToggleNode WarnMonsterBlock { get; set; } = new ToggleNode(false);

    [Menu("Warn for monster resistances")]
    public ToggleNode WarnMonsterResist { get; set; } = new ToggleNode(false);

    [Menu("Warn for corrupted items")]
    public ToggleNode WarnCorrupted { get; set; } = new ToggleNode(false);

    [Menu("Warn for \"always crit\"")]
    public ToggleNode WarnAlwaysCrit { get; set; } = new ToggleNode(false);

    [Menu("Warn for reduced damage taken")]
    public ToggleNode WarnReducedDamageTaken { get; set; } = new ToggleNode(false);

    [Menu("Warn for bleed")]
    public ToggleNode WarnBleed { get; set; } = new ToggleNode(false);

    [Menu("Warn for poison")]
    public ToggleNode WarnPoison { get; set; } = new ToggleNode(false);

    [Menu("Warn for phys as chaos")]
    public ToggleNode WarnPhysicalAsExtraChaos { get; set; } = new ToggleNode(false);

    public ToggleNode WarnAvoidDamage { get; set; } = new(false);
    public ToggleNode WarnHexer { get; set; } = new(false);
    public ToggleNode WarnBreaksArmor { get; set; } = new(false);
    public ToggleNode WarnRegen { get; set; } = new(false);
    public ToggleNode WarnEnrage { get; set; } = new(false);
    public ToggleNode WarnCICrit { get; set; } = new(false);
    public ToggleNode WarnFirePen { get; set; } = new(false);
    public ToggleNode WarnColdPen { get; set; } = new(false);
    public ToggleNode WarnLightningPen { get; set; } = new(false);
    public ToggleNode WarnChaosPen { get; set; } = new(false);
    public ToggleNode WarnChaosExtra { get; set; } = new(false);
    public ToggleNode WarnMoreAilments { get; set; } = new(false);
    public ToggleNode WarnSpeed { get; set; } = new(false);
}

public enum SearchState
{
    Empty,
    Searching,
    Stopped,
}