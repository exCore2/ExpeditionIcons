using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace ExpeditionIcons.PathPlannerData;

/// <summary>
/// The genome the search operates on: explosion points plus the recipe chosen at each
/// runestone.
/// </summary>
public class PathCandidate
{
    public PathCandidate(List<Vector2> points, int[] choices)
    {
        Points = points;
        Choices = choices;
    }

    public List<Vector2> Points { get; }

    /// <summary>One slot per runestone, indexing into that runestone's candidate list.</summary>
    public int[] Choices { get; }

    /// <summary>
    /// Runestones this path actually detonates, written by GetScore.
    /// Recipe mutation draws from this so it never edits a choice that cannot affect the
    /// score - an uncovered runestone is under no selection pressure, so left free to mutate
    /// it would drift to junk and then be evaluated at junk the moment a path first reaches it.
    /// </summary>
    public ulong CoveredMask { get; set; }

    public PathCandidate Clone()
    {
        return new PathCandidate(Points.ToList(), (int[])Choices.Clone()) { CoveredMask = CoveredMask };
    }
}
