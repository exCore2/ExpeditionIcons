namespace ExpeditionIcons.PathPlannerData;

/// <summary>
/// The monsters a runestone spawns when detonated. Scaled by the runes in the recipe the
/// path chose for that runestone, on top of whatever the chain already accumulated.
/// Holds the runestone rather than an index so scoring can reach its candidate list
/// without a lookup table.
/// </summary>
public class RunestoneMonster : IRunicMonster
{
    public RunestoneMonster(RuneEncounter runestone)
    {
        Runestone = runestone;
    }

    public RuneEncounter Runestone { get; }
}
