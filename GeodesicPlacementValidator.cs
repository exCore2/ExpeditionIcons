using System.Numerics;

namespace ExpeditionIcons;

/// <summary>
/// The placement rule the toggle switches to: a segment is valid only when a walkable path from the
/// previous detonator to it exists and is no longer than the explosive's placement range.
/// <para>
/// Layered by cost, because the exact search is roughly a hundred times more expensive than the
/// whole rest of a segment test. Measured on a Prairie logbook, of the segments that reach this
/// class: 31% are settled by the straight line, another 67% of the remainder by the landmark bound
/// at 0.7us, and most of what is left by the coarse blocks. What survives all three is the hard
/// middle - long detours sitting near the budget - and each one costs over half a millisecond, so
/// by default it is refused rather than searched.
/// </para>
/// <para>
/// Every layer is sound in the direction it answers: an accept is a path that provably exists, a
/// reject is a path that provably does not. The only inexactness is the conservative refusal at the
/// end, which loses about a third of the legal wrap-arounds and cannot invent an impossible one.
/// </para>
/// </summary>
public sealed class GeodesicPlacementValidator
{
    private readonly int _inconclusiveNodeCap;
    private readonly bool _allowWrapArounds;

    /// <param name="inconclusiveNodeCap">
    /// Nodes of exact search to spend on a placement none of the cheap tiers settled. Measured at
    /// 400 against 0: no score gain and 8% fewer generations, so nothing sets it above zero today.
    /// It stays a parameter because that measurement was taken in one area, and re-testing it
    /// elsewhere should not need the tier rebuilt.
    /// </param>
    public GeodesicPlacementValidator(PathBoundModel model, int inconclusiveNodeCap = 0, bool allowWrapArounds = true)
    {
        Model = model;
        _inconclusiveNodeCap = inconclusiveNodeCap;
        _allowWrapArounds = allowWrapArounds;
    }

    public PathBoundModel Model { get; }

    /// <summary>Name for reports: the two modes behave differently enough to be worth telling apart.</summary>
    public string RuleName => _allowWrapArounds ? "geodesic" : "exact line";

    public PathBoundModel.Workspace CreateWorkspace() => new(Model, 200_000);

    /// <summary>
    /// The rule the search runs on. Never accepts a placement that cannot be built; may refuse one
    /// that could.
    /// </summary>
    public bool IsSegmentValid(Vector2 from, Vector2 to, float budget, PathBoundModel.Workspace workspace)
    {
        //Outside the component the explosives can reach, so nothing can be placed there at all -
        //something the old rule had no way to notice.
        if (!Model.IsInComponent(from) || !Model.IsInComponent(to))
        {
            return false;
        }

        if (Model.LineIsClear(from, to))
        {
            return true;
        }

        //Exact-line mode stops here: sound, since it only ever accepts a straight walkable run, and
        //four times cheaper than reasoning about detours. What it gives up is the wrap-arounds.
        if (!_allowWrapArounds)
        {
            return false;
        }

        switch (Model.Landmark(from, to, budget))
        {
            case BoundVerdict.Accept:
                return true;
            case BoundVerdict.Reject:
            case BoundVerdict.Outside:
                return false;
        }

        switch (Model.Coarse(from, to, budget, workspace))
        {
            case BoundVerdict.Accept:
                return true;
            case BoundVerdict.Reject:
            case BoundVerdict.Outside:
                return false;
        }

        //The hard middle. Searching it properly costs ~550us a segment, which is more than the rest
        //of the search put together, so it is only attempted when a cap is configured - and a
        //capped search that runs out is a refusal, never an acceptance.
        return _inconclusiveNodeCap > 0 &&
               Model.AStar(from, to, budget, workspace, _inconclusiveNodeCap, out _) == GeodesicResult.Reachable;
    }

    /// <summary>
    /// The uncapped truth, for validating a finished path. Too slow for the search - a handful of
    /// milliseconds for a whole path, which is nothing once per result.
    /// </summary>
    public bool IsExactlyValid(Vector2 from, Vector2 to, float budget, PathBoundModel.Workspace workspace)
    {
        return Model.IsInComponent(from) && Model.IsInComponent(to) &&
               (Model.LineIsClear(from, to) ||
                Model.AStar(from, to, budget, workspace, out _) == GeodesicResult.Reachable);
    }
}
