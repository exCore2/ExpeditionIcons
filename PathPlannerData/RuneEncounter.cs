using System;

namespace ExpeditionIcons.PathPlannerData;

/// <summary>
/// A runestone's static loot drop - the reward of whichever recipe the path chose.
/// The monsters a runestone spawns are a separate loot entry, <see cref="RunestoneMonster"/>.
/// <para>
/// Deliberately neither <see cref="IMonster"/> nor <see cref="IChest"/>, which is what keeps the
/// drop relic-immune: <see cref="ConfigurableRelic.GetScoreMultiplier"/> matches only those two, and
/// a "+40% chest quantity" relic must not inflate a rune price - least of all a negative one.
/// </para>
/// <para>
/// Deliberately NOT an <see cref="IExpeditionRelic"/> either. Propagation is handled by the rune
/// mask in the loot pass; putting runestones in the relic set would add ~11 always-neutral
/// entries to the per-loot-item aggregate, the hottest loop in the planner.
/// </para>
/// </summary>
public class RuneEncounter : IExpeditionLoot
{
    public RuneEncounter(uint entityId, int runestoneIndex, RunestoneCandidate[] candidates)
    {
        EntityId = entityId;
        RunestoneIndex = runestoneIndex;
        Candidates = candidates;
    }

    public uint EntityId { get; }

    public int RunestoneIndex { get; }

    /// <summary>Pruned, seed-first: index 0 is always the highest-priced candidate.</summary>
    public RunestoneCandidate[] Candidates { get; }

    /// <summary>
    /// Clamped because candidate counts differ per runestone after pruning - one may have
    /// four and another one - so an index carried across mutations can be out of range.
    /// </summary>
    public RunestoneCandidate GetCandidate(int index)
    {
        return Candidates[Math.Clamp(index, 0, Candidates.Length - 1)];
    }
}
