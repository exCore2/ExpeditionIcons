using System.Collections.Generic;
using System.Drawing;
using ExileCore2.Shared.Attributes;
using ExileCore2.Shared.Nodes;

namespace ExpeditionIcons;

/// <summary>
/// Pricing and display settings for Expedition2 rune encounters.
/// Ported from Expedition2GoodSettings; every default here matches Expedition2Good.
/// </summary>
[Submenu(CollapsedByDefault = true)]
public class RuneDisplaySettings
{
    [Menu("Enable rune values", "Prices Expedition2 rune encounters via the NinjaPrice plugin bridge and draws the results.")]
    public ToggleNode EnableRuneDisplay { get; set; } = new ToggleNode(true);

    [Menu("Show value on map", "Draws 'Rune <value> (<n> sockets)' at the encounter position on the minimap and the large map.")]
    public ToggleNode ShowOnMinimap { get; set; } = new ToggleNode(true);

    public ColorNode TextColor { get; set; } = new ColorNode(Color.LightBlue);
    public ColorNode TopPickColor { get; set; } = new ColorNode(Color.LightGreen);

    [Menu("Valuable color threshold", "Recipes worth at least this much use the valuable text color. Purely cosmetic - the planner uses its own threshold under Planner settings.")]
    public RangeNode<float> ValuableColorThreshold { get; set; } = new RangeNode<float>(25, 0, 10000);

    public ColorNode ValuableTextColor { get; set; } = new ColorNode(Color.Pink);

    //Deliberately one-sided: a correct selection draws nothing at all. Marking both states means
    //hunting down every marker to read its colour, where marking only the bad ones makes anything
    //you can see a thing to go and fix.
    [Menu("Plan not selected color", "Frames the map value of a planned encounter while the selected recipe is missing or is not the one the plan wants. A correct selection is left unmarked.")]
    public ColorNode PlanNotSelectedColor { get; set; } = new ColorNode(Color.Yellow);

    [Menu("Plan not selected frame thickness", "How heavy that frame is. A thin outline is easy to miss against a busy minimap.")]
    public RangeNode<int> PlanNotSelectedFrameThickness { get; set; } = new RangeNode<int>(3, 1, 15);

    //Text marker rather than a colour: the map value already carries threshold colouring and the
    //planner's yellow frame, and a third colour on the same string would be unreadable.
    [Menu("Mark reroll candidates with ***",
        "Wraps the map value in ***asterisks*** when nothing the runestone can produce clears the planner's value threshold AND none of its recipes can pass on a rune ticked under Keep in Planner settings -> Relic weight modifiers AND it has fewer sockets than the keep threshold below. Locked runestones and ones the planned path has already committed to are never marked.")]
    public ToggleNode MarkRerollCandidates { get; set; } = new ToggleNode(true);

    //Socket count on purpose, not the chosen recipe's length: this is the display asking "could this
    //stone ever be worth keeping", and a big stone can still be offered a long recipe on a reroll.
    [Menu("Never reroll at this socket count",
        "Runestones with at least this many sockets are never marked as reroll candidates, however cheap their recipes " +
        "are - a stone that big spawns enough monsters to be worth keeping on its own. Applies on top of the other two " +
        "reasons a runestone escapes the marker, not instead of them. Set to 0 to disable this exemption.")]
    public RangeNode<int> KeepRuneCountThreshold { get; set; } = new RangeNode<int>(5, 0, 12);

    [Menu("Minimum value to show", "When greater than 0, recipes with a value below this are hidden. Set to 0 to list every item and value. Ignored for encounters the planned path visits, which show only the recipe to select.")]
    public RangeNode<float> MinimumValueToShow { get; set; } = new RangeNode<float>(0, 0, 500);

    [Menu("Max items to show", "Maximum number of recipes listed per label (highest value first). Set to 0 to show all. Encounters the planned path visits show only the recipe to select, so this does not apply to them.")]
    public RangeNode<int> MaxItemsToShow { get; set; } = new RangeNode<int>(0, 0, 20);

    [Menu("Hide collected encounters", "Hides encounters that are finished with - collected or destroyed (activated state 7 and 8). Ones still worth acting on, including those mid-fight or waiting to be collected, keep their value on screen.")]
    public ToggleNode HideCollectedEncounters { get; set; } = new ToggleNode(true);

    [Menu("Render offset X", "Horizontal offset (pixels) of the text relative to the on-ground object label. 0 keeps the original position.")]
    public RangeNode<int> RenderOffsetX { get; set; } = new RangeNode<int>(0, -2000, 2000);

    [Menu("Render offset Y", "Vertical offset (pixels) of the text relative to the on-ground object label. 0 keeps the original position.")]
    public RangeNode<int> RenderOffsetY { get; set; } = new RangeNode<int>(0, -2000, 2000);

    public ContentNode<PriceOverride> PriceOverrides { get; set; } = new ContentNode<PriceOverride>
        { Content = [], EnableControls = true, EnableItemCollapsing = true, ItemFactory = () => new PriceOverride(), };
}

/// <summary>
/// How the path planner weighs rune encounters.
/// Lives inside PlannerSettings because that is the only object handed to
/// PathPlanner and its worker threads.
/// </summary>
[Submenu(CollapsedByDefault = true)]
public class RuneScoringSettings
{
    [Menu("Consider rune values when planning", "When off, rune encounters are invisible to the path planner.")]
    public ToggleNode EnableRuneScoring { get; set; } = new ToggleNode(true);

    [Menu("Value threshold", "Encounters worth at least this much are chased, everything else is avoided.")]
    public RangeNode<float> ValueThreshold { get; set; } = new RangeNode<float>(25, 0, 1000);

    [Menu("Weight above threshold",
        "Flat score for clearing the threshold, before the recipe's own price counts. For scale, a runic monster is 3 and a normal monster is effectively 0, so 10 means any qualifying recipe is worth about three runic monsters on its own.")]
    public RangeNode<float> AboveThresholdWeight { get; set; } = new RangeNode<float>(10, 0, 100);

    //Deliberately most of the weight: at a flat 25 with a 0.05 slope, a 181 recipe scored barely six
    //points above a 59.8 one - less than the rune multipliers on the monsters a runestone spawns can
    //swing - so the planner kept choosing cheap recipes with good runes over ones worth three times more.
    [Menu("Extra weight per point of value above threshold",
        "How much the recipe's price matters. Together with the flat weight this decides whether a much more expensive recipe actually wins: at 0.15 a recipe worth 130 more scores about 20 higher.")]
    public RangeNode<float> AboveThresholdScale { get; set; } = new RangeNode<float>(0.15f, 0, 1);

    //Kept small on purpose. This is a nudge, not a veto: covering a cheap runestone still hands
    //its passed-on runes to every runic monster caught later, which is usually worth more than the
    //drop was. A large penalty made the planner refuse those detours outright.
    [Menu("Weight below threshold", "Score for an encounter below the threshold. Negative values make the planner route around it. For scale, a runic monster is 3, so -3 means one cheap runestone costs about as much as missing a single runic monster.")]
    public RangeNode<float> BelowThresholdWeight { get; set; } = new RangeNode<float>(-3, -100, 0);

    [Menu("Sentinel rune charges",
        "How many runic monsters a Sentinel's runes buff before expiring. Each Sentinel keeps its own count, and every runic monster spends one charge from every active Sentinel. The real number is not readable from the game, so this is an estimate. 0 disables Sentinels entirely.")]
    public RangeNode<int> SentinelRuneCharges { get; set; } = new RangeNode<int>(8, 0, 50);

    //The effect a covered rune has on runic monsters caught later along the path is configured
    //as a row in the relic weight modifier table, alongside every other relic modifier.
    //See PlannerSettings.RuneEncounterRelicSettings.
}

[Submenu]
public class PriceOverride
{
    public ListNode Type { get; set; } = new ListNode();
    public RangeNode<float> Value { get; set; } = new RangeNode<float>(0, 0, 10000);

    public override string ToString()
    {
        return string.IsNullOrWhiteSpace(Type.Value) ? base.ToString() : $"{Type.Value} -> {Value}";
    }
}
