namespace ExpeditionIcons.PathPlannerData;

/// <summary>
/// An object that grants runes to later explosions without being a runestone: no recipe choice, no
/// drop of its own, no monsters. Worth nothing directly - its entire value is the multiplier its
/// runes apply to runic monsters caught afterwards.
/// <para>
/// Unlike a runestone's passed-on runes, this buff is NOT permanent: it lasts a fixed number of
/// runic monsters and then expires, so it is tracked per source rather than folded into the path's
/// accumulated rune mask. It also does not dedupe against that mask - a rune held permanently and
/// the same rune from a source will both apply, which the game would not do. Deliberate
/// simplification; it makes a source slightly overvalued when it duplicates a rune already held.
/// </para>
/// <para>Like a runestone, it does not scale the explosion that consumed it.</para>
/// </summary>
public class RuneSource : IExpeditionLoot
{
    public RuneSource(int index, ulong runeMask)
    {
        Index = index;
        RuneMask = runeMask;
    }

    /// <summary>Position in the environment's source list, for the per-path charge counters.</summary>
    public int Index { get; }

    /// <summary>Every rune this source grants. A source can carry several at once.</summary>
    public ulong RuneMask { get; }
}
