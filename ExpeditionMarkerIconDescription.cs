using System.Collections.Generic;
using ExileCore2.Shared.Enums;

namespace ExpeditionIcons;

public class ExpeditionMarkerIconDescription
{
    public IconPickerIndex IconPickerIndex { get; init; }
    public MapIconsIndex DefaultIcon { get; init; }
    public List<string> BaseEntityMetadataSubstrings { get; set; } = new List<string>();

    /// <summary>Label for the weight tables. Null falls back to the enum member name.</summary>
    public string DisplayName { get; init; }

    /// <summary>Name shown wherever this description appears in the settings.</summary>
    public string Name => DisplayName ?? IconPickerIndex.ToString();

    /// <summary>
    /// False for descriptions that exist only to earn a weight row - the path-matched box entities,
    /// whose display comes from their own markers, so there is no icon of theirs to pick.
    /// </summary>
    public bool IsIconCustomizable { get; init; } = true;

    /// <summary>
    /// MinimapIcon component names for this marker, matched by EXACT equality.
    /// Substring matching would be wrong here: "RewardChestCurrency" is a prefix of
    /// "RewardChestCurrencyRare", and those two share an identical .ao file, so a prefix
    /// match would silently classify every rare currency chest as a normal one.
    /// Takes priority over <see cref="BaseEntityMetadataSubstrings"/> when populated.
    /// </summary>
    public List<string> MinimapIconNames { get; set; } = new List<string>();

    /// <summary>
    /// Explicit monster-vs-chest scope. Normally inferred from the mod name containing
    /// Monster / Elite / PackSize, which only works while GGG's naming encodes the target -
    /// mods like ExpeditionRelicUpsideSpecialKaruiTotem say nothing, and would silently fall
    /// through to chest. Null means keep inferring from the name.
    /// </summary>
    public bool? AffectsMonsters { get; init; }

    public bool IsWeightCustomizable { get; init; } = true;
}